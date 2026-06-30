using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinApp.Contracts;

namespace FinApp.Shared.UI.Services;

/// <summary>A friendly error from the API carrying the server's message and HTTP status.</summary>
public sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Typed client over the FinApp sync API. Attaches the bearer <see cref="Token"/> to every call and
/// turns non-success responses into <see cref="ApiException"/> with the server's error message.
/// </summary>
public sealed class FinAppApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Set after login; cleared on logout. Null = anonymous calls (register/login).</summary>
    public string? Token { get; set; }

    // --- Auth -------------------------------------------------------------
    public Task<AuthResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "/auth/register", req, ct);
    public Task<AuthResponse> LoginAsync(LoginRequest req, CancellationToken ct = default) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "/auth/login", req, ct);
    public Task<UserDto> MeAsync(CancellationToken ct = default) =>
        SendAsync<UserDto>(HttpMethod.Get, "/me", null, ct);
    public Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/auth/password", new ChangePasswordRequest(currentPassword, newPassword), ct);
    public Task UpdateAvatarAsync(string? dataUrl, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, "/me/avatar", new SetAvatarRequest(dataUrl), ct);
    public Task<Dictionary<Guid, string>> GetAccountAvatarsAsync(Guid accountId, CancellationToken ct = default) =>
        SendAsync<Dictionary<Guid, string>>(HttpMethod.Get, $"/accounts/{accountId}/avatars", null, ct);

    // --- Accounts ---------------------------------------------------------
    public Task<List<AccountSummaryDto>> GetAccountsAsync(CancellationToken ct = default) =>
        SendAsync<List<AccountSummaryDto>>(HttpMethod.Get, "/accounts", null, ct);
    public Task<AccountSummaryDto> CreateAccountAsync(CreateAccountRequest req, CancellationToken ct = default) =>
        SendAsync<AccountSummaryDto>(HttpMethod.Post, "/accounts", req, ct);
    public Task RenameAccountAsync(Guid id, string name, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/accounts/{id}/name", new RenameAccountRequest(name), ct);
    public Task DeleteAccountAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/accounts/{id}", null, ct);

    // --- Snapshot ---------------------------------------------------------
    public Task<AccountSnapshot> GetSnapshotAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<AccountSnapshot>(HttpMethod.Get, $"/accounts/{id}/snapshot", null, ct);
    public Task<AccountSnapshot> SaveSnapshotAsync(Guid id, SaveAccountRequest req, CancellationToken ct = default) =>
        SendAsync<AccountSnapshot>(HttpMethod.Put, $"/accounts/{id}/snapshot", req, ct);

    /// <summary>Download the account as an .xlsx (one sheet per period). Returns the bytes + suggested file name.</summary>
    public async Task<(byte[] Bytes, string FileName)> ExportAccountAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Get, $"/accounts/{id}/export", null, ct);
        await EnsureSuccessAsync(response, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? "account.xlsx";
        return (bytes, fileName);
    }

    // --- Invitations ------------------------------------------------------
    public Task<List<InvitationDto>> GetPendingInvitationsAsync(CancellationToken ct = default) =>
        SendAsync<List<InvitationDto>>(HttpMethod.Get, "/invitations/pending", null, ct);
    public Task InviteAsync(Guid accountId, string username, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/accounts/{accountId}/invitations", new CreateInvitationRequest(username), ct);
    public async Task<Guid> AcceptInvitationAsync(Guid invitationId, CancellationToken ct = default) =>
        (await SendAsync<AcceptResult>(HttpMethod.Post, $"/invitations/{invitationId}/accept", null, ct)).AccountId;
    public Task DeclineInvitationAsync(Guid invitationId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/invitations/{invitationId}/decline", null, ct);

    private record AcceptResult(Guid AccountId);

    // --- Plumbing ---------------------------------------------------------
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, body, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, body, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        if (!string.IsNullOrEmpty(Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return http.SendAsync(request, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var message = response.ReasonPhrase ?? "Request failed.";
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct);
            if (!string.IsNullOrWhiteSpace(error?.Error)) message = error!.Error;
        }
        catch { /* non-JSON error body — keep the reason phrase */ }

        throw new ApiException(response.StatusCode, message);
    }

    private record ErrorBody(string Error);
}
