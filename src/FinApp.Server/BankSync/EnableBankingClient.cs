using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FinApp.Server.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace FinApp.Server.BankSync;

/// <summary>
/// Thin wrapper over the Enable Banking API (api.enablebanking.com), a European Open Banking aggregator that
/// offers self-serve signup and free access on your own accounts — used here to link a FinApp account to
/// Revolut and pull transactions. Enable Banking is the regulated party, so this app never needs its own
/// Open Banking authorization.
///
/// <para>Auth is different from a typical client-secret provider: we register an application (uploading a
/// self-signed cert) to get an <b>application id</b>, then authenticate every call with a short-lived JWT we
/// sign ourselves with the matching RSA private key (RS256, <c>kid</c> = application id, <c>iss</c> =
/// enablebanking.com, <c>aud</c> = api.enablebanking.com). No token endpoint round-trip — we mint the JWT
/// locally per request. Inert until <c>BankSync:EnableBanking:ApplicationId</c> + <c>PrivateKey</c> (PEM) are
/// configured (same "inert until credentialed" stance as external sign-in), so it's safe to ship unconfigured.</para>
/// </summary>
public sealed class EnableBankingClient(IHttpClientFactory httpFactory, IConfiguration config)
{
    private const string BaseUrl = "https://api.enablebanking.com";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(config["BankSync:EnableBanking:ApplicationId"]) &&
        !string.IsNullOrWhiteSpace(config["BankSync:EnableBanking:PrivateKey"]);

    public async Task<List<BankInstitution>> GetAspspsAsync(string countryCode, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"/aspsps?country={Uri.EscapeDataString(countryCode)}", null, ct);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var result = new List<BankInstitution>();
        if (doc.TryGetProperty("aspsps", out var aspsps))
            foreach (var a in aspsps.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var country = a.TryGetProperty("country", out var c) ? c.GetString() : countryCode;
                if (!string.IsNullOrEmpty(name))
                    result.Add(new BankInstitution(name!, country ?? countryCode));
            }
        return result;
    }

    /// <summary>Start a consent: returns the Enable Banking authorization URL to redirect the user to.
    /// <paramref name="state"/> is echoed back to the callback so we can correlate it to the FinApp account.</summary>
    public async Task<string> StartAuthAsync(string aspspName, string aspspCountry, string redirectUrl, string state, CancellationToken ct)
    {
        var body = new
        {
            access = new { valid_until = DateTimeOffset.UtcNow.AddDays(90).ToString("O") },
            aspsp = new { name = aspspName, country = aspspCountry },
            state,
            redirect_url = redirectUrl,
            psu_type = "personal",
        };
        using var resp = await SendAsync(HttpMethod.Post, "/auth", body, ct);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return doc.GetProperty("url").GetString()!;
    }

    /// <summary>Exchange the callback's authorization code for a session, returning the first authorized account id.</summary>
    public async Task<(string SessionId, string AccountId)?> CreateSessionAsync(string code, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Post, "/sessions", new { code }, ct);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var sessionId = doc.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
        if (sessionId is null || !doc.TryGetProperty("accounts", out var accounts) || accounts.GetArrayLength() == 0)
            return null;
        // "accounts" is a list of account uids (strings); tolerate objects carrying a "uid" too.
        var first = accounts[0];
        var accountId = first.ValueKind == JsonValueKind.String
            ? first.GetString()
            : first.TryGetProperty("uid", out var uid) ? uid.GetString() : null;
        return accountId is null ? null : (sessionId, accountId);
    }

    /// <summary>Booked transactions for one authorized account since <paramref name="dateFrom"/>. The parser
    /// tolerates both the Berlin Group / NextGenPSD2 camelCase shape (signed <c>transactionAmount.amount</c>,
    /// <c>bookingDate</c>, <c>transactionId</c>, transactions nested under <c>booked</c>) and Enable Banking's
    /// snake_case native shape (unsigned <c>transaction_amount</c> + <c>credit_debit_indicator</c>).</summary>
    public async Task<List<BankTransaction>> GetTransactionsAsync(string accountId, DateOnly dateFrom, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get,
            $"/accounts/{Uri.EscapeDataString(accountId)}/transactions?date_from={dateFrom:yyyy-MM-dd}", null, ct);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return ParseTransactions(doc);
    }

    /// <summary>Pure transaction parser (exposed for testing). Handles both provider JSON shapes described on
    /// <see cref="GetTransactionsAsync"/>.</summary>
    public static List<BankTransaction> ParseTransactions(JsonElement doc)
    {
        var result = new List<BankTransaction>();
        if (!doc.TryGetProperty("transactions", out var txns)) return result;
        // The array is either directly under "transactions" (Enable Banking) or nested under "booked"
        // (Berlin Group / GoCardless: { transactions: { booked: [...], pending: [...] } }).
        var booked = txns.ValueKind == JsonValueKind.Object && txns.TryGetProperty("booked", out var b) ? b : txns;
        if (booked.ValueKind != JsonValueKind.Array) return result;

        foreach (var t in booked.EnumerateArray())
        {
            var amountEl = Prop(t, "transactionAmount", "transaction_amount");
            if (amountEl is null || Prop(amountEl.Value, "amount")?.GetString() is not { } amountStr) continue;
            var raw = decimal.Parse(amountStr, System.Globalization.CultureInfo.InvariantCulture);
            // Amount handling covers both conventions: Berlin Group amounts are already signed with no
            // indicator; Enable Banking's native shape is unsigned + a creditDebitIndicator. Apply the
            // indicator when present, otherwise trust the sign already on the amount.
            var indicator = Str(t, "creditDebitIndicator", "credit_debit_indicator");
            var amount = indicator switch
            {
                "DBIT" => -Math.Abs(raw),
                "CRDT" => Math.Abs(raw),
                _ => raw,
            };
            var date = Str(t, "bookingDate", "booking_date", "valueDate", "value_date");
            if (date is null) continue;
            var description = Describe(t);
            var id = Str(t, "transactionId", "entry_reference", "internalTransactionId")
                     ?? $"{date}:{raw}:{description}".GetHashCode().ToString("x");   // stable synthetic id for dedupe
            result.Add(new BankTransaction(id, DateOnly.Parse(date), amount, description));
        }
        return result;
    }

    private static string Describe(JsonElement t)
    {
        // Berlin Group unstructured remittance is a plain string; Enable Banking uses an array.
        if (Str(t, "remittanceInformationUnstructured") is { Length: > 0 } s) return s;
        if (t.TryGetProperty("remittance_information", out var ri) && ri.ValueKind == JsonValueKind.Array && ri.GetArrayLength() > 0)
            return string.Join(" ", ri.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
        if (Str(t, "creditorName", "debtorName") is { Length: > 0 } party) return party;
        if (t.TryGetProperty("creditor", out var cr) && cr.TryGetProperty("name", out var cn) && cn.GetString() is { } cName) return cName;
        if (t.TryGetProperty("debtor", out var db) && db.TryGetProperty("name", out var dn) && dn.GetString() is { } dName) return dName;
        return "Bank transaction";
    }

    /// <summary>First present property among <paramref name="names"/> (tolerates camelCase vs snake_case shapes).</summary>
    private static JsonElement? Prop(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v)) return v;
        return null;
    }

    private static string? Str(JsonElement e, params string[] names) =>
        Prop(e, names) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (!IsEnabled) throw new ApiException(StatusCodes.Status503ServiceUnavailable, "Bank sync isn't configured.");
        var http = httpFactory.CreateClient();
        var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BuildJwt());
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // A 404 on an account/session means the stored connection is no longer valid (e.g. consent expired
            // or it was created under a different app/environment) — steer the user to reconnect.
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new ApiException(StatusCodes.Status400BadRequest, "The bank connection is no longer valid. Please disconnect and link your bank again.");
            throw new ApiException(StatusCodes.Status502BadGateway, $"Bank sync provider returned {(int)resp.StatusCode}.");
        }
        return resp;
    }

    private string BuildJwt() =>
        BuildJwt(config["BankSync:EnableBanking:ApplicationId"]!, config["BankSync:EnableBanking:PrivateKey"]!);

    /// <summary>Mint a short-lived RS256 JWT signed with the given private key (Enable Banking's app auth).
    /// Exposed for testing. A fresh RSA is created and disposed per call, so signature-provider caching is
    /// disabled — a cached provider would outlive its RSA and throw ObjectDisposedException on the next call.</summary>
    public static string BuildJwt(string applicationId, string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var now = DateTimeOffset.UtcNow;
        var key = new RsaSecurityKey(rsa)
        {
            KeyId = applicationId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "enablebanking.com",
            Audience = "api.enablebanking.com",
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddHours(1).UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        };
        var handler = new JwtSecurityTokenHandler();
        var token = (JwtSecurityToken)handler.CreateToken(descriptor);
        token.Header["kid"] = applicationId;   // Enable Banking keys the cert by application id
        return handler.WriteToken(token);
    }
}

public record BankInstitution(string Name, string Country);
public record BankTransaction(string ExternalId, DateOnly Date, decimal Amount, string Description);
