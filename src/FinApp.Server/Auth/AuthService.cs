using FinApp.Contracts;
using FinApp.Domain.Common;
using FinApp.Domain.Users;
using FinApp.Persistence;
using FinApp.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>Registers and authenticates users, returning a bearer token on success.</summary>
public sealed class AuthService(FinAppDbContext db, IPasswordHasher hasher, JwtTokenService tokens)
{
    private const int MinPasswordLength = 8;

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var username = (request.Username ?? "").Trim();
        var email = (request.Email ?? "").Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(username))
            throw new BadRequestException("Username is required.");
        if ((request.Password ?? "").Length < MinPasswordLength)
            throw new BadRequestException($"Password must be at least {MinPasswordLength} characters.");

        if (await db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower(), ct))
            throw new ConflictException("That username is already taken.");
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ConflictException("That email is already registered.");

        User user;
        try
        {
            user = new User(username, email, hasher.Hash(request.Password!));
        }
        catch (ArgumentException ex)
        {
            throw new BadRequestException(ex.Message);
        }

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return tokens.Issue(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var identifier = (request.UsernameOrEmail ?? "").Trim();
        var identifierLower = identifier.ToLowerInvariant();

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Username.ToLower() == identifierLower || u.Email == identifierLower, ct);

        if (user is null || !hasher.Verify(request.Password ?? "", user.PasswordHash))
            throw new UnauthorizedException("Invalid username or password.");

        return tokens.Issue(user);
    }

    /// <summary>Find an existing user by email or create one for an external (Google/Facebook) sign-in, then issue a token.
    /// External users get a random password hash (they sign in via the provider, not a password).</summary>
    public async Task<AuthResponse> FindOrCreateExternalUserAsync(string email, string? displayName, CancellationToken ct = default)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            throw new BadRequestException("The sign-in provider didn't return an email address.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            var baseName = MakeUsername(displayName, email);
            var username = baseName;
            var n = 1;
            while (await db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower(), ct))
                username = $"{baseName}{++n}";

            var randomSecret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            user = new User(username, email, hasher.Hash(randomSecret));
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }
        return tokens.Issue(user);
    }

    private static string MakeUsername(string? displayName, string email)
    {
        var src = !string.IsNullOrWhiteSpace(displayName) ? displayName! : email.Split('@')[0];
        var clean = new string(src.Trim().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.').ToArray()).Trim();
        if (clean.Length == 0) clean = "user";
        return clean.Length > 30 ? clean[..30] : clean;
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedException("Not signed in.");
        if (!hasher.Verify(request.CurrentPassword ?? "", user.PasswordHash))
            throw new BadRequestException("Current password is incorrect.");
        if ((request.NewPassword ?? "").Length < MinPasswordLength)
            throw new BadRequestException($"Password must be at least {MinPasswordLength} characters.");

        user.SetPasswordHash(hasher.Hash(request.NewPassword!));
        await db.SaveChangesAsync(ct);
    }
}
