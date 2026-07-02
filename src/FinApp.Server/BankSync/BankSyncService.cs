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
        // Learned merchant rules: a normalized description maps to a category (debits) or fund/contributor (credits).
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"BankMappings\" (" +
            "\"AccountId\" text NOT NULL, \"MatchKey\" text NOT NULL, \"Kind\" text NOT NULL, \"TargetId\" text NOT NULL, " +
            "PRIMARY KEY (\"AccountId\", \"MatchKey\"))", ct);
        // Columns added idempotently so existing tables gain them without a migration. SQLite lacks
        // ADD COLUMN IF NOT EXISTS, so each is wrapped to ignore the "duplicate column" error.
        foreach (var col in new[]
                 {
                     "\"FundId\" text NULL",          // the synced fund this connection is bound to
                     "\"AccountRefs\" text NULL",      // all authorized bank-account uids (comma-separated)
                     "\"Balance\" text NULL",          // last-fetched balance of the selected account
                     "\"BalanceCurrency\" text NULL",
                     "\"BalanceAt\" text NULL",
                     "\"InstitutionLogo\" text NULL",  // ASPSP logo URL for nicer UX
                 })
        {
            try { await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"BankConnections\" ADD COLUMN {col}", ct); }
            catch { /* column already exists */ }
        }
    }

    /// <summary>Normalized merchant key used to match a transaction description to a saved rule.</summary>
    public static string MatchKeyOf(string description) =>
        string.Join(' ', (description ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
            .Select(i => new BankInstitutionDto(i.Name, i.Country, i.Logo)).ToList();
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
            LastSyncedAt: row?.LastSyncedAt,
            FundId: row?.FundId,
            Balance: row?.Balance,
            BalanceCurrency: row?.BalanceCurrency,
            AccountRef: row?.AccountRef,
            InstitutionLogo: row?.InstitutionLogo);
    }

    public async Task<StartBankLinkResponse> StartLinkAsync(Guid userId, Guid accountId, StartBankLinkRequest req, string callbackUrl, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var state = accountId.ToString("N");   // echoed back to the callback so we can find this account again
        var link = await eb.StartAuthAsync(req.InstitutionName, req.Country, callbackUrl, state, ct);
        await UpsertConnectionAsync(accountId, providerRef: "", institution: req.InstitutionName, institutionName: req.InstitutionName,
            accountRef: null, status: "Pending", consentExpiresAt: null, lastSyncedAt: null, ct);
        await WriteConnectionColumnsAsync(accountId, ("InstitutionLogo", req.Logo));   // preserved through link/sync
        return new StartBankLinkResponse(link);
    }

    /// <summary>Called when the bank redirects back with an authorization code. Exchanges it for a session with
    /// Enable Banking before marking the connection Linked — the state alone is not trusted to imply consent.</summary>
    public async Task<bool> CompleteLinkAsync(Guid accountId, string code, CancellationToken ct = default)
    {
        var row = await ReadConnectionAsync(accountId, ct);
        if (row is null) return false;
        if (await eb.CreateSessionAsync(code, ct) is not { } session) return false;

        var selected = session.AccountIds[0];   // default to the first; the user can switch in the UI
        await UpsertConnectionAsync(accountId, providerRef: session.SessionId, institution: row.Institution,
            institutionName: row.InstitutionName, accountRef: selected, status: "Linked",
            consentExpiresAt: DateTimeOffset.UtcNow.AddDays(90), lastSyncedAt: null, ct);
        await WriteConnectionColumnsAsync(accountId, ("AccountRefs", string.Join(",", session.AccountIds)));
        await RefreshBalanceAsync(accountId, selected, ct);
        return true;
    }

    /// <summary>Fetch the account's live balance and store it on the connection (best-effort).</summary>
    private async Task RefreshBalanceAsync(Guid accountId, string accountRef, CancellationToken ct)
    {
        try
        {
            if (await eb.GetBalanceAsync(accountRef, ct) is { } bal)
                await WriteConnectionColumnsAsync(accountId,
                    ("Balance", bal.Amount.ToString(CultureInfo.InvariantCulture)),
                    ("BalanceCurrency", bal.Currency),
                    ("BalanceAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        }
        catch { /* balance is best-effort — don't fail the link/sync */ }
    }

    /// <summary>The authorized bank accounts on this connection, each with a label + live balance, for picking one.</summary>
    public async Task<List<BankAccountDto>> ListAccountsAsync(Guid userId, Guid accountId, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var row = await ReadConnectionAsync(accountId, ct);
        if (row is not { Status: "Linked" }) return [];
        var refs = (row.AccountRefs ?? row.AccountRef ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<BankAccountDto>();
        foreach (var r in refs)
        {
            var bal = await eb.GetBalanceAsync(r, ct);
            var label = await eb.GetAccountLabelAsync(r, ct);
            result.Add(new BankAccountDto(r, label, bal?.Amount, bal?.Currency, r == row.AccountRef));
        }
        return result;
    }

    /// <summary>Choose which authorized account this connection syncs from.</summary>
    public async Task SelectAccountAsync(Guid userId, Guid accountId, string accountRef, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var row = await ReadConnectionAsync(accountId, ct);
        var refs = (row?.AccountRefs ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (!refs.Contains(accountRef)) throw new BadRequestException("That account isn't part of this connection.");
        await WriteConnectionColumnsAsync(accountId, ("AccountRef", accountRef));
        await RefreshBalanceAsync(accountId, accountRef, ct);
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
        await RefreshBalanceAsync(accountId, accountRef, ct);   // keep the stored balance current

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

    /// <summary>Drop the bank connection so the account can be linked afresh. We keep already-handled
    /// (Confirmed/Dismissed) transactions so that re-linking the same bank doesn't resurface entries the user has
    /// already reviewed — only the un-reviewed Pending stage is cleared.</summary>
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
            c2.CommandText = "DELETE FROM \"PendingBankTransactions\" WHERE \"AccountId\" = @acc AND \"Status\" = 'Pending'";
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

    /// <summary>Re-open (set back to Pending) any handled transactions dated in a range — used when a period is
    /// deleted so its imported rows resurface for re-handling when the period is entered again (feature 3).</summary>
    public async Task ResetRangeAsync(Guid userId, Guid accountId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE \"PendingBankTransactions\" SET \"Status\" = 'Pending' " +
                              "WHERE \"AccountId\" = @acc AND \"Date\" >= @from AND \"Date\" <= @to AND \"Status\" <> 'Pending'";
            AddParam(cmd, "@acc", accountId.ToString());
            AddParam(cmd, "@from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            AddParam(cmd, "@to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    // --- Merchant mapping rules (feature 2.3) -----------------------------

    public async Task<List<BankMappingDto>> GetMappingsAsync(Guid userId, Guid accountId, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var result = new List<BankMappingDto>();
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"MatchKey\", \"Kind\", \"TargetId\" FROM \"BankMappings\" WHERE \"AccountId\" = @acc";
            AddParam(cmd, "@acc", accountId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (Guid.TryParse(reader.GetString(2), out var target))
                    result.Add(new BankMappingDto(reader.GetString(0), reader.GetString(1), target));
        }
        finally { if (opened) await conn.CloseAsync(); }
        return result;
    }

    /// <summary>Save (or replace) the rule for a merchant: kind is "category", "fund" or "contributor".</summary>
    public async Task SetMappingAsync(Guid userId, Guid accountId, string description, string kind, Guid targetId, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var key = MatchKeyOf(description);
        if (key.Length == 0) throw new BadRequestException("Can't map an empty merchant.");
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"BankMappings\" (\"AccountId\", \"MatchKey\", \"Kind\", \"TargetId\") VALUES (@acc, @key, @kind, @target) " +
                "ON CONFLICT (\"AccountId\", \"MatchKey\") DO UPDATE SET \"Kind\" = @kind, \"TargetId\" = @target";
            AddParam(cmd, "@acc", accountId.ToString());
            AddParam(cmd, "@key", key);
            AddParam(cmd, "@kind", kind);
            AddParam(cmd, "@target", targetId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Remove a merchant rule. Does not touch any expenses/contributions already created from it.</summary>
    public async Task RemoveMappingAsync(Guid userId, Guid accountId, string description, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM \"BankMappings\" WHERE \"AccountId\" = @acc AND \"MatchKey\" = @key";
            AddParam(cmd, "@acc", accountId.ToString());
            AddParam(cmd, "@key", MatchKeyOf(description));
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
        string? AccountRef, string Status, DateTimeOffset? ConsentExpiresAt, DateTimeOffset? LastSyncedAt, Guid? FundId,
        string? AccountRefs, decimal? Balance, string? BalanceCurrency, string? InstitutionLogo);

    private async Task<ConnectionRow?> ReadConnectionAsync(Guid accountId, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"ProviderRef\", \"Institution\", \"InstitutionName\", \"AccountRef\", \"Status\", \"ConsentExpiresAt\", \"LastSyncedAt\", \"FundId\", \"AccountRefs\", \"Balance\", \"BalanceCurrency\", \"InstitutionLogo\" " +
                              "FROM \"BankConnections\" WHERE \"AccountId\" = @acc";
            AddParam(cmd, "@acc", accountId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new ConnectionRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                reader.IsDBNull(7) || !Guid.TryParse(reader.GetString(7), out var fid) ? null : fid,
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) || !decimal.TryParse(reader.GetString(9), System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out var bal) ? null : bal,
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11));
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Bind (or unbind, with null) the fund this connection mirrors. Kept separate from link/sync so
    /// those never overwrite the binding.</summary>
    public async Task SetConnectionFundAsync(Guid userId, Guid accountId, Guid? fundId, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        await WriteConnectionColumnsAsync(accountId, ("FundId", fundId?.ToString()));
    }

    /// <summary>Targeted UPDATE of one or more connection columns (null value → SQL NULL).</summary>
    private async Task WriteConnectionColumnsAsync(Guid accountId, params (string Column, string? Value)[] columns)
    {
        if (columns.Length == 0) return;
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, CancellationToken.None);
        try
        {
            await using var cmd = conn.CreateCommand();
            var sets = string.Join(", ", columns.Select((c, i) => $"\"{c.Column}\" = @v{i}"));
            cmd.CommandText = $"UPDATE \"BankConnections\" SET {sets} WHERE \"AccountId\" = @acc";
            for (var i = 0; i < columns.Length; i++) AddParam(cmd, $"@v{i}", (object?)columns[i].Value ?? DBNull.Value);
            AddParam(cmd, "@acc", accountId.ToString());
            await cmd.ExecuteNonQueryAsync();
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
