using System.Data;
using System.Globalization;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>
/// Records user consent for sensitive actions (accepting the terms at login, linking a bank, syncing a fund)
/// as an <b>append-only audit log</b> — every grant and withdrawal is a row with a timestamp and the policy
/// version in force. The current "flag" for a scope is simply the latest event: consent is active when the most
/// recent event for that (user, account, scope) is a grant under the <see cref="PolicyVersion"/> currently in
/// force. This keeps a defensible history if the app scales, while still answering "is consent active now?".
/// Backed by a standalone table created idempotently (same migration-free pattern as <see cref="AvatarService"/>).
/// </summary>
public sealed class ConsentService(FinAppDbContext db)
{
    /// <summary>Bump when the terms/privacy or the link/sync disclosures change materially — forces re-consent.</summary>
    public const string PolicyVersion = "2026-07-01";

    public static class Scope
    {
        public const string Login = "login";       // accepted the Terms + Privacy (user-level, no account)
        public const string BankLink = "bank_link"; // authorized linking a bank to an account
        public const string BankSync = "bank_sync"; // authorized syncing a fund to the linked account
    }

    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"Consents\" (" +
            "\"Id\" text PRIMARY KEY, \"UserId\" text NOT NULL, \"AccountId\" text NULL, \"Scope\" text NOT NULL, " +
            "\"Granted\" text NOT NULL, \"PolicyVersion\" text NOT NULL, \"At\" text NOT NULL)", ct);

    /// <summary>Append a grant/withdrawal event.</summary>
    public async Task RecordAsync(Guid userId, Guid? accountId, string scope, bool granted, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"Consents\" (\"Id\", \"UserId\", \"AccountId\", \"Scope\", \"Granted\", \"PolicyVersion\", \"At\") " +
                "VALUES (@id, @uid, @acc, @scope, @granted, @ver, @at)";
            AddParam(cmd, "@id", Guid.NewGuid().ToString());
            AddParam(cmd, "@uid", userId.ToString());
            AddParam(cmd, "@acc", (object?)accountId?.ToString() ?? DBNull.Value);
            AddParam(cmd, "@scope", scope);
            AddParam(cmd, "@granted", granted ? "1" : "0");
            AddParam(cmd, "@ver", PolicyVersion);
            AddParam(cmd, "@at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>The latest event for a scope, or null if none was ever recorded.</summary>
    public async Task<(bool Granted, DateTimeOffset At, string PolicyVersion)?> LatestAsync(
        Guid userId, Guid? accountId, string scope, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT \"Granted\", \"At\", \"PolicyVersion\" FROM \"Consents\" " +
                "WHERE \"UserId\" = @uid AND \"Scope\" = @scope AND " +
                (accountId is null ? "\"AccountId\" IS NULL " : "\"AccountId\" = @acc ") +
                "ORDER BY \"At\" DESC LIMIT 1";
            AddParam(cmd, "@uid", userId.ToString());
            AddParam(cmd, "@scope", scope);
            if (accountId is not null) AddParam(cmd, "@acc", accountId.Value.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return (reader.GetString(0) == "1",
                    DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    reader.GetString(2));
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Whether consent is currently active: latest event is a grant under the current policy version.</summary>
    public async Task<bool> IsActiveAsync(Guid userId, Guid? accountId, string scope, CancellationToken ct = default) =>
        await LatestAsync(userId, accountId, scope, ct) is { Granted: true, PolicyVersion: PolicyVersion };

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
