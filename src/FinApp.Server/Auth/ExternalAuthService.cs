using System.Net.Http.Headers;
using System.Text.Json;
using FinApp.Server.Infrastructure;

namespace FinApp.Server.Auth;

/// <summary>
/// Manual OAuth 2.0 authorization-code flow for Google and Facebook sign-in. We don't use ASP.NET's
/// cookie-based external auth handlers — this app is JWT-only — so we drive the handshake by hand and,
/// on success, mint our own JWT (see <see cref="AuthService.FindOrCreateExternalUserAsync"/>).
/// A provider is "enabled" only when its client id + secret are configured (Auth:Google / Auth:Facebook),
/// so the feature stays inert until credentials are supplied.
/// </summary>
public sealed class ExternalAuthService(IHttpClientFactory httpFactory, IConfiguration config)
{
    public bool IsEnabled(string provider) =>
        !string.IsNullOrWhiteSpace(ClientId(provider)) && !string.IsNullOrWhiteSpace(ClientSecret(provider));

    private string? ClientId(string p) => p switch
    {
        "google" => config["Auth:Google:ClientId"],
        "facebook" => config["Auth:Facebook:AppId"],
        _ => null,
    };

    private string? ClientSecret(string p) => p switch
    {
        "google" => config["Auth:Google:ClientSecret"],
        "facebook" => config["Auth:Facebook:AppSecret"],
        _ => null,
    };

    /// <summary>The provider's consent page URL to redirect the browser to.</summary>
    public string BuildAuthorizeUrl(string provider, string redirectUri, string state)
    {
        var clientId = ClientId(provider)!;
        return provider switch
        {
            "google" =>
                "https://accounts.google.com/o/oauth2/v2/auth?response_type=code" +
                $"&client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&scope=" + Uri.EscapeDataString("openid email profile") +
                $"&state={Uri.EscapeDataString(state)}",
            "facebook" =>
                "https://www.facebook.com/v19.0/dialog/oauth?response_type=code" +
                $"&client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&scope=email" +
                $"&state={Uri.EscapeDataString(state)}",
            _ => throw new BadRequestException("Unknown sign-in provider."),
        };
    }

    /// <summary>Exchange the auth code for a token and fetch the user's email + name.</summary>
    public async Task<(string Email, string? Name)> CompleteAsync(string provider, string code, string redirectUri, CancellationToken ct)
    {
        var http = httpFactory.CreateClient();
        var accessToken = await ExchangeCodeAsync(http, provider, code, redirectUri, ct);
        return await FetchUserAsync(http, provider, accessToken, ct);
    }

    private async Task<string> ExchangeCodeAsync(HttpClient http, string provider, string code, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = ClientId(provider)!,
            ["client_secret"] = ClientSecret(provider)!,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        };
        var tokenUrl = provider == "google"
            ? "https://oauth2.googleapis.com/token"
            : "https://graph.facebook.com/v19.0/oauth/access_token";

        using var resp = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
        if (!resp.IsSuccessStatusCode)
            throw new BadRequestException("Couldn't complete sign-in with the provider.");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("access_token", out var t)
            ? t.GetString() ?? throw new BadRequestException("No access token returned.")
            : throw new BadRequestException("No access token returned.");
    }

    private static async Task<(string Email, string? Name)> FetchUserAsync(HttpClient http, string provider, string accessToken, CancellationToken ct)
    {
        var userInfoUrl = provider == "google"
            ? "https://openidconnect.googleapis.com/v1/userinfo"
            : "https://graph.facebook.com/me?fields=name,email";

        using var req = new HttpRequestMessage(HttpMethod.Get, userInfoUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new BadRequestException("Couldn't read your profile from the provider.");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(email))
            throw new BadRequestException("The provider didn't share an email address.");
        return (email!, name);
    }
}
