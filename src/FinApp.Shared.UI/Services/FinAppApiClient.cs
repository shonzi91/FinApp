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
    public Task<ExternalProvidersDto> GetProvidersAsync(CancellationToken ct = default) =>
        SendAsync<ExternalProvidersDto>(HttpMethod.Get, "/auth/providers", null, ct);

    // --- Consent (audit-logged) -------------------------------------------
    public Task<ConsentStatusDto> GetConsentAsync(string scope, Guid? accountId = null, CancellationToken ct = default) =>
        SendAsync<ConsentStatusDto>(HttpMethod.Get, $"/consent?scope={Uri.EscapeDataString(scope)}{(accountId is { } a ? $"&accountId={a}" : "")}", null, ct);
    public Task RecordConsentAsync(string scope, Guid? accountId, bool granted, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/consent", new RecordConsentRequest(scope, accountId, granted), ct);
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

    // --- Membership / archiving -------------------------------------------
    public async Task<LeaveAccountResult> LeaveAccountAsync(Guid id, Guid? newOwnerUserId, CancellationToken ct = default)
    {
        var res = await SendAsync<LeaveResultDto>(HttpMethod.Post, $"/accounts/{id}/leave", new LeaveAccountRequest(newOwnerUserId), ct);
        return Enum.TryParse<LeaveAccountResult>(res.Result, out var r) ? r : LeaveAccountResult.Left;
    }
    public Task RemoveMemberAsync(Guid id, Guid memberUserId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/accounts/{id}/members/{memberUserId}", null, ct);
    public Task TransferOwnershipAsync(Guid id, Guid newOwnerUserId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/accounts/{id}/transfer-ownership", new TransferOwnershipRequest(newOwnerUserId), ct);
    public Task<List<ArchivedAccountDto>> GetArchivedAccountsAsync(CancellationToken ct = default) =>
        SendAsync<List<ArchivedAccountDto>>(HttpMethod.Get, "/accounts/archived", null, ct);
    public Task ReactivateAccountAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/accounts/{id}/reactivate", null, ct);

    private record LeaveResultDto(string Result);

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

    // --- Bank sync (Open Banking) -----------------------------------------
    public Task<BankSyncStatusDto> GetBankStatusAsync(Guid accountId, CancellationToken ct = default) =>
        SendAsync<BankSyncStatusDto>(HttpMethod.Get, $"/accounts/{accountId}/bank/status", null, ct);
    public Task<List<BankInstitutionDto>> GetBankInstitutionsAsync(Guid accountId, string country, CancellationToken ct = default) =>
        SendAsync<List<BankInstitutionDto>>(HttpMethod.Get, $"/accounts/{accountId}/bank/institutions?country={Uri.EscapeDataString(country)}", null, ct);
    public Task<StartBankLinkResponse> StartBankLinkAsync(Guid accountId, StartBankLinkRequest req, CancellationToken ct = default) =>
        SendAsync<StartBankLinkResponse>(HttpMethod.Post, $"/accounts/{accountId}/bank/link", req, ct);
    public Task SyncBankAsync(Guid accountId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/accounts/{accountId}/bank/sync", null, ct);
    public Task<List<PendingBankTransactionDto>> GetPendingBankTransactionsAsync(Guid accountId, CancellationToken ct = default) =>
        SendAsync<List<PendingBankTransactionDto>>(HttpMethod.Get, $"/accounts/{accountId}/bank/pending", null, ct);
    public Task AckBankTransactionAsync(Guid accountId, string externalId, bool confirmed, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/accounts/{accountId}/bank/ack", new BankTransactionAck(externalId, confirmed), ct);
    public Task DisconnectBankAsync(Guid accountId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/accounts/{accountId}/bank/connection", null, ct);
    public Task ResetBankRangeAsync(Guid accountId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/accounts/{accountId}/bank/reset?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", null, ct);
    public Task SetBankFundAsync(Guid accountId, Guid? fundId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/accounts/{accountId}/bank/fund", new SetBankFundRequest(fundId), ct);
    public Task<List<BankMappingDto>> GetBankMappingsAsync(Guid accountId, CancellationToken ct = default) =>
        SendAsync<List<BankMappingDto>>(HttpMethod.Get, $"/accounts/{accountId}/bank/mappings", null, ct);
    public Task SetBankMappingAsync(Guid accountId, string description, string kind, Guid targetId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/accounts/{accountId}/bank/mappings", new SetBankMappingRequest(description, kind, targetId), ct);
    public Task RemoveBankMappingAsync(Guid accountId, string description, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/accounts/{accountId}/bank/mappings?description={Uri.EscapeDataString(description)}", null, ct);

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
