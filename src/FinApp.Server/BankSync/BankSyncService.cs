using System.Data;
using System.Globalization;
using FinApp.Contracts;
using FinApp.Persistence;
using FinApp.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.BankSync;

/// <summary>
/// Links a FinApp account to a bank (currently Revolut, but any Enable Banking-supported ASPSP works) via the
/// Enable Banking Open Banking aggregator, and stages fetched transactions for the user to turn into real
/// expenses client-side. Kept in two standalone tables created idempotently with <c>CREATE TABLE IF NOT
/// EXISTS</c> — same reasoning as <see cref="FinApp.Server.Auth.AvatarService"/>: prod Postgres builds its
/// schema via <c>EnsureCreated</c>, which never alters an existing table, so a new EF-migrated column/table
/// would be invisible there. Raw ADO keeps the same SQL working on both SQLite (dev/tests) and Postgres.
///
/// <para><b>Consent flow (Enable Banking):</b> <see cref="StartLinkAsync"/> asks the provider for an
/// authorization URL and stashes a Pending row keyed by the FinApp account id (passed as the OAuth-style
/// <c>state</c>). The user consents at their bank and is redirected back to <c>/bank/callback</c> with a
/// <c>code</c>; <see cref="CompleteLinkAsync"/> exchanges that code for a session + account id and marks the
/// row Linked. Later <see cref="SyncAsync"/> calls pull booked transactions and stage new ones.</para>
///
/// <para>Deliberately server-side only: turning a pending transaction into a real
/// <see cref="FinApp.Domain.Budgeting.Expense"/> happens on the client (<c>BudgetingState.AddExpense</c>),
/// because the account's actual content lives in the client-owned, opaque <see cref="AccountSnapshotRow"/> blob
/// — the server never deserializes or mutates it (see <see cref="FinApp.Server.Accounts.SnapshotService"/>).
/// This service only stages/dedupes the raw bank data and remembers which rows the user already acted on
/// (Confirmed/Dismissed), so a later sync doesn't resurrect them — the provider returns the whole
/// transaction-history window on every fetch.</para>
/// </summary>
public sealed class BankSyncService(FinAppDbContext db, EnableBankingClient eb, IConfiguration config)
{
    public bool IsEnabled => eb.IsEnabled;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"BankConnections\" (" +
            "\"AccountId\" text PRIMARY KEY, \"ProviderRef\" text NOT NULL, \"Institution\" text NOT NULL, " +
            "\"InstitutionName\" text NOT NULL, \"AccountRef\" text NULL, \"Status\" text NOT NULL, " +
            "\"ConsentExpiresAt\" text NULL, \"LastSyncedAt\" text NULL)", ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"PendingBankTransactions\" (" +
            "\"AccountId\" text NOT NULL, \"ExternalId\" text NOT NULL, \"Date\" text NOT NULL, " +
            "\"Amount\" text NOT NULL, \"Description\" text NOT NULL, \"Status\" text NOT NULL, " +
            "PRIMARY KEY (\"AccountId\", \"ExternalId\"))", ct);
    }

    public async Task<List<BankInstitutionDto>> SearchInstitutionsAsync(Guid userId, Guid accountId, string countryCode, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        // Dev/testing escape hatch: point BankSync:EnableBanking:SandboxAspsp at Enable Banking's mock bank
        // (e.g. "Mock ASPSP") to exercise the whole consent→transactions flow with fake data. When set it
        // replaces the Revolut-name filter (and optionally the country) so the UI's auto-pick lands on it.
        var sandbox = config["BankSync:EnableBanking:SandboxAspsp"];
        var usingSandbox = !string.IsNullOrWhiteSpace(sandbox);
        // In production, BankSync:EnableBanking:Country selects which country's bank list to search (Revolut is
        // listed per-country, e.g. GB for the UK, LT/DE/… for the EEA). Falls back to the caller's country.
        var country = usingSandbox
            ? (config["BankSync:EnableBanking:SandboxCountry"] ?? countryCode)
            : (config["BankSync:EnableBanking:Country"] ?? countryCode);
        var filter = usingSandbox ? sandbox! : "revolut";

        var all = await eb.GetAspspsAsync(country, ct);
        return all.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(i => new BankInstitutionDto(i.Name, i.Country)).ToList();
    }

    public async Task<BankSyncStatusDto> GetStatusAsync(Guid userId, Guid accountId, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var row = await ReadConnectionAsync(accountId, ct);
        return new BankSyncStatusDto(
            eb.IsEnabled,
            Connected: row is { Status: "Linked" },
            InstitutionName: row?.InstitutionName,
            ConsentExpiresAt: row?.ConsentExpiresAt,
            LastSyncedAt: row?.LastSyncedAt);
    }

    public async Task<StartBankLinkResponse> StartLinkAsync(Guid userId, Guid accountId, StartBankLinkRequest req, string callbackUrl, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var state = accountId.ToString("N");   // echoed back to the callback so we can find this account again
        var link = await eb.StartAuthAsync(req.InstitutionName, req.Country, callbackUrl, state, ct);
        await UpsertConnectionAsync(accountId, providerRef: "", institution: req.InstitutionName, institutionName: req.InstitutionName,
            accountRef: null, status: "Pending", consentExpiresAt: null, lastSyncedAt: null, ct);
        return new StartBankLinkResponse(link);
    }

    /// <summary>Called when the bank redirects back with an authorization code. Exchanges it for a session with
    /// Enable Banking before marking the connection Linked — the state alone is not trusted to imply consent.</summary>
    public async Task<bool> CompleteLinkAsync(Guid accountId, string code, CancellationToken ct = default)
    {
        var row = await ReadConnectionAsync(accountId, ct);
        if (row is null) return false;
        if (await eb.CreateSessionAsync(code, ct) is not { } session) return false;

        await UpsertConnectionAsync(accountId, providerRef: session.SessionId, institution: row.Institution,
            institutionName: row.InstitutionName, accountRef: session.AccountId, status: "Linked",
            consentExpiresAt: DateTimeOffset.UtcNow.AddDays(90), lastSyncedAt: null, ct);
        return true;
    }

    public async Task SyncAsync(Guid userId, Guid accountId, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var row = await ReadConnectionAsync(accountId, ct);
        if (row is not { Status: "Linked", AccountRef: { } accountRef })
            throw new BadRequestException("This account isn't linked to a bank yet.");

        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-89);   // ~90-day window most banks allow
        var transactions = await eb.GetTransactionsAsync(accountRef, since, ct);
        foreach (var t in transactions)
            await InsertPendingIfNewAsync(accountId, t, ct);

        await UpsertConnectionAsync(accountId, row.ProviderRef, row.Institution, row.InstitutionName,
            row.AccountRef, row.Status, row.ConsentExpiresAt, DateTimeOffset.UtcNow, ct);
    }

    public async Task<List<PendingBankTransactionDto>> GetPendingAsync(Guid userId, Guid accountId, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var result = new List<PendingBankTransactionDto>();
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"ExternalId\", \"Date\", \"Amount\", \"Description\" FROM \"PendingBankTransactions\" " +
                              "WHERE \"AccountId\" = @acc AND \"Status\" = 'Pending' ORDER BY \"Date\" DESC";
            AddParam(cmd, "@acc", accountId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new PendingBankTransactionDto(
                    reader.GetString(0),
                    decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    reader.GetString(3)));
            }
        }
        finally { if (opened) await conn.CloseAsync(); }
        return result;
    }

    /// <summary>Drop the bank connection (and any staged transactions) so the account can be linked afresh —
    /// e.g. after switching environments or when a consent went stale.</summary>
    public async Task DisconnectAsync(Guid userId, Guid accountId, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using (var c1 = conn.CreateCommand())
            {
                c1.CommandText = "DELETE FROM \"BankConnections\" WHERE \"AccountId\" = @acc";
                AddParam(c1, "@acc", accountId.ToString());
                await c1.ExecuteNonQueryAsync(ct);
            }
            await using var c2 = conn.CreateCommand();
            c2.CommandText = "DELETE FROM \"PendingBankTransactions\" WHERE \"AccountId\" = @acc";
            AddParam(c2, "@acc", accountId.ToString());
            await c2.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    public async Task AckAsync(Guid userId, Guid accountId, string externalId, bool confirmed, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE \"PendingBankTransactions\" SET \"Status\" = @status WHERE \"AccountId\" = @acc AND \"ExternalId\" = @ext";
            AddParam(cmd, "@status", confirmed ? "Confirmed" : "Dismissed");
            AddParam(cmd, "@acc", accountId.ToString());
            AddParam(cmd, "@ext", externalId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private async Task InsertPendingIfNewAsync(Guid accountId, BankTransaction t, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"PendingBankTransactions\" (\"AccountId\", \"ExternalId\", \"Date\", \"Amount\", \"Description\", \"Status\") " +
                "VALUES (@acc, @ext, @date, @amount, @desc, 'Pending') " +
                "ON CONFLICT (\"AccountId\", \"ExternalId\") DO NOTHING";
            AddParam(cmd, "@acc", accountId.ToString());
            AddParam(cmd, "@ext", t.ExternalId);
            AddParam(cmd, "@date", t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            AddParam(cmd, "@amount", t.Amount.ToString(CultureInfo.InvariantCulture));
            AddParam(cmd, "@desc", t.Description);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private sealed record ConnectionRow(string ProviderRef, string Institution, string InstitutionName,
        string? AccountRef, string Status, DateTimeOffset? ConsentExpiresAt, DateTimeOffset? LastSyncedAt);

    private async Task<ConnectionRow?> ReadConnectionAsync(Guid accountId, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"ProviderRef\", \"Institution\", \"InstitutionName\", \"AccountRef\", \"Status\", \"ConsentExpiresAt\", \"LastSyncedAt\" " +
                              "FROM \"BankConnections\" WHERE \"AccountId\" = @acc";
            AddParam(cmd, "@acc", accountId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new ConnectionRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture));
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private async Task UpsertConnectionAsync(Guid accountId, string providerRef, string institution, string institutionName,
        string? accountRef, string status, DateTimeOffset? consentExpiresAt, DateTimeOffset? lastSyncedAt, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"BankConnections\" (\"AccountId\", \"ProviderRef\", \"Institution\", \"InstitutionName\", \"AccountRef\", \"Status\", \"ConsentExpiresAt\", \"LastSyncedAt\") " +
                "VALUES (@acc, @ref, @inst, @instName, @accRef, @status, @expires, @synced) " +
                "ON CONFLICT (\"AccountId\") DO UPDATE SET \"ProviderRef\" = @ref, \"Institution\" = @inst, \"InstitutionName\" = @instName, " +
                "\"AccountRef\" = @accRef, \"Status\" = @status, \"ConsentExpiresAt\" = @expires, \"LastSyncedAt\" = @synced";
            AddParam(cmd, "@acc", accountId.ToString());
            AddParam(cmd, "@ref", providerRef);
            AddParam(cmd, "@inst", institution);
            AddParam(cmd, "@instName", institutionName);
            AddParam(cmd, "@accRef", (object?)accountRef ?? DBNull.Value);
            AddParam(cmd, "@status", status);
            AddParam(cmd, "@expires", (object?)consentExpiresAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
            AddParam(cmd, "@synced", (object?)lastSyncedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private async Task EnsureContributorAsync(Guid userId, Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts.Include(a => a.Members).FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null || !account.IsContributor(userId))
            throw new NotFoundException("Account not found.");
    }

    private static async Task<bool> OpenAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        if (conn.State == ConnectionState.Open) return false;
        await conn.OpenAsync(ct);
        return true;
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
