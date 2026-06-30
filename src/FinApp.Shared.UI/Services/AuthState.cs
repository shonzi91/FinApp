using FinApp.Contracts;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// Tracks the signed-in user and bearer token for the session. Restores a saved token on startup,
/// hands the token to the <see cref="FinAppApiClient"/>, and raises <see cref="Changed"/> so the UI
/// can switch between the auth screen and the app.
/// </summary>
public sealed class AuthState(FinAppApiClient api, ITokenStore tokens)
{
    public UserDto? CurrentUser { get; private set; }
    public bool IsAuthenticated => CurrentUser is not null;
    public Guid UserId => CurrentUser?.Id ?? Guid.Empty;

    public event Action? Changed;

    /// <summary>Restore a persisted session (validates the token via /me). Safe to call once at startup.</summary>
    public async Task<bool> TryRestoreAsync()
    {
        var token = await tokens.GetAsync();
        if (string.IsNullOrEmpty(token)) return false;

        api.Token = token;
        try
        {
            CurrentUser = await api.MeAsync();
            Changed?.Invoke();
            return true;
        }
        catch
        {
            await SignOutAsync();
            return false;
        }
    }

    public Task RegisterAsync(string username, string email, string password) =>
        ApplyAsync(() => api.RegisterAsync(new RegisterRequest(username, email, password)));

    public Task LogInAsync(string usernameOrEmail, string password) =>
        ApplyAsync(() => api.LoginAsync(new LoginRequest(usernameOrEmail, password)));

    public async Task SignOutAsync()
    {
        CurrentUser = null;
        api.Token = null;
        await tokens.ClearAsync();
        Changed?.Invoke();
    }

    /// <summary>Update the signed-in user's avatar in memory (after uploading it to the server).</summary>
    public void SetAvatar(string? dataUrl)
    {
        if (CurrentUser is null) return;
        CurrentUser = CurrentUser with { Avatar = dataUrl };
        Changed?.Invoke();
    }

    private async Task ApplyAsync(Func<Task<AuthResponse>> call)
    {
        var auth = await call();
        api.Token = auth.Token;
        await tokens.SetAsync(auth.Token);
        CurrentUser = new UserDto(auth.UserId, auth.Username, auth.Email);
        Changed?.Invoke();
        // Pull the full profile (incl. avatar) in the background; ignore failures (we're already signed in).
        try { CurrentUser = await api.MeAsync(); Changed?.Invoke(); } catch { /* best effort */ }
    }
}
