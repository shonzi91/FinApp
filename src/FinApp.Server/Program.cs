using System.Text;
using FinApp.Contracts;
using FinApp.Domain.Common;
using FinApp.Persistence;
using FinApp.Server.Accounts;
using FinApp.Server.Auth;
using FinApp.Server.BankSync;
using FinApp.Server.Infrastructure;
using FinApp.Server.Invitations;
using FinApp.Server.Sync;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.Security.Claims;

// Register the SQLite (SQLCipher-capable) native provider once for the process.
SQLitePCL.Batteries_V2.Init();

var builder = WebApplication.CreateBuilder(args);

// Database provider: SQLite by default (local dev, tests, MAUI), Postgres in the cloud.
// To use Postgres set Database__Provider=Postgres and ConnectionStrings__FinApp=<Npgsql conn string>.
var usePostgres = string.Equals(builder.Configuration["Database:Provider"], "Postgres", StringComparison.OrdinalIgnoreCase);
var connectionString = builder.Configuration.GetConnectionString("FinApp")
                       ?? $"Data Source={Path.Combine(AppContext.BaseDirectory, "finapp-server.db")}";
builder.Services.AddDbContext<FinAppDbContext>(o =>
{
    if (usePostgres) o.UseNpgsql(NormalizePostgres(connectionString));
    else o.UseSqlite(connectionString);
});

// Accept either an Npgsql key-value string or a postgres:// URI (what Neon/Heroku/etc. hand out),
// since Npgsql itself only parses the key-value form.
static string NormalizePostgres(string cs)
{
    if (!cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !cs.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        return cs; // already key-value

    var uri = new Uri(cs);
    var userInfo = uri.UserInfo.Split(':', 2);
    var b = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        Database = uri.AbsolutePath.Trim('/'),
        SslMode = SslMode.Require,
    };
    return b.ConnectionString;
}

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();

// Refuse to start in production with the dev placeholder signing key. Set a real one via the
// Jwt__Key environment variable (>= 32 chars). The placeholder is fine for local development.
const string DevJwtKeyPlaceholder = "dev-only-finapp-signing-key-change-me-in-production-please";
if (!builder.Environment.IsDevelopment() &&
    (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key == DevJwtKeyPlaceholder || jwt.Key.Length < 32))
{
    throw new InvalidOperationException(
        "Jwt:Key must be set to a real secret (>= 32 chars) outside Development. " +
        "Provide it via the Jwt__Key environment variable.");
}

builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<AvatarService>();
builder.Services.AddScoped<ExternalIdentityService>();
builder.Services.AddScoped<ExternalAuthService>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<ArchivedAccountsService>();
builder.Services.AddScoped<SnapshotService>();
builder.Services.AddScoped<AccountExportService>();
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<EnableBankingClient>();  // mints its own RS256 JWT per call; no shared state to cache
builder.Services.AddScoped<BankSyncService>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<SyncNotifier>();

// CORS for the Blazor WASM web host (different origin from the API in dev).
// SignalR needs an explicit origin list + AllowCredentials (can't use AllowAnyOrigin with credentials).
const string WasmCorsPolicy = "wasm";
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                  ?? ["http://localhost:5080"];
builder.Services.AddCors(o => o.AddPolicy(WasmCorsPolicy, p =>
    p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep "sub"/"email" claim names as-issued
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        // SignalR (WebSockets/SSE) can't use the Authorization header — read the token off the query string.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Behind Cloud Run's TLS-terminating proxy the request reads as http; honour X-Forwarded-Proto so
// Request.Scheme is https (needed so the OAuth redirect_uri we build matches what providers expect).
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost };
forwarded.KnownNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

// Ensure the server DB schema is current on startup.
// SQLite uses the EF migrations; Postgres uses EnsureCreated (the migrations are SQLite-specific,
// and the cloud DB is provisioned fresh) so we build the schema straight from the model.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FinAppDbContext>();
    if (usePostgres) db.Database.EnsureCreated();
    else db.Database.Migrate();
    // Avatars live in a standalone table created idempotently (no EF migration; works on both providers).
    await scope.ServiceProvider.GetRequiredService<AvatarService>().EnsureSchemaAsync();
    // Bank-sync tables (connections + staged transactions) follow the same idempotent-create pattern.
    await scope.ServiceProvider.GetRequiredService<BankSyncService>().EnsureSchemaAsync();
    // External-identity marker table (which users signed up via Google/Facebook) — same pattern.
    await scope.ServiceProvider.GetRequiredService<ExternalIdentityService>().EnsureSchemaAsync();
    // Archived-accounts table + purge anything past its 30-day grace window on startup.
    var archives = scope.ServiceProvider.GetRequiredService<ArchivedAccountsService>();
    await archives.EnsureSchemaAsync();
    await archives.PurgeExpiredAsync();
}

// Translate ApiException into a JSON problem response; everything else bubbles to the default handler.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (ApiException ex)
    {
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

// One-origin hosting: serve the Blazor WASM client (_framework + wwwroot assets) as static files.
// Placed before auth so the app shell loads without a token.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // The scoped-CSS bundle and the app shell have hash-less URLs, so browsers would cache them and
    // keep importing the previous build's (hashed) styles after a deploy. Force revalidation on those
    // entry files; the fingerprinted assets they pull in (_framework, _content/<hash>) stay cacheable.
    OnPrepareResponse = ctx =>
    {
        var name = ctx.File.Name;
        if (name.EndsWith(".styles.css", StringComparison.OrdinalIgnoreCase)
            || name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
        }
    }
});

// CORS is only needed when the web client runs on a separate origin (local two-terminal dev).
// In a one-origin deployment the client and API share an origin, so it's a no-op there.
if (app.Environment.IsDevelopment())
    app.UseCors(WasmCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

// --- Auth ----------------------------------------------------------------
var auth = app.MapGroup("/auth");
auth.MapPost("/register", async (RegisterRequest req, AuthService svc, CancellationToken ct) =>
    Results.Ok(await svc.RegisterAsync(req, ct)));
auth.MapPost("/login", async (LoginRequest req, AuthService svc, CancellationToken ct) =>
    Results.Ok(await svc.LoginAsync(req, ct)));
auth.MapPost("/password", async (ChangePasswordRequest req, ClaimsPrincipal user, AuthService svc, CancellationToken ct) =>
{
    await svc.ChangePasswordAsync(user.UserId(), req, ct);
    return Results.NoContent();
}).RequireAuthorization();

// --- External sign-in (Google / Facebook), manual OAuth code flow ---------
auth.MapGet("/providers", (ExternalAuthService ext) =>
    Results.Ok(new ExternalProvidersDto(ext.IsEnabled("google"), ext.IsEnabled("facebook"))));

auth.MapGet("/external/{provider}", (string provider, HttpContext http, ExternalAuthService ext, IConfiguration cfg) =>
{
    if (!ext.IsEnabled(provider)) return Results.NotFound();
    var redirectUri = ExternalRedirectUri(http, cfg, provider);
    var state = Guid.NewGuid().ToString("N");
    http.Response.Cookies.Append("finapp_oauth_state", state, new CookieOptions
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10), Path = "/",
    });
    return Results.Redirect(ext.BuildAuthorizeUrl(provider, redirectUri, state));
});

auth.MapGet("/external/{provider}/callback", async (string provider, string? code, string? state,
    HttpContext http, ExternalAuthService ext, AuthService authSvc, AvatarService avatars, ExternalIdentityService identities, IConfiguration cfg, CancellationToken ct) =>
{
    if (!ext.IsEnabled(provider) || string.IsNullOrEmpty(code)) return Results.Redirect("/?authError=1");
    var expectedState = http.Request.Cookies["finapp_oauth_state"];
    http.Response.Cookies.Delete("finapp_oauth_state");
    if (string.IsNullOrEmpty(state) || state != expectedState) return Results.Redirect("/?authError=1");
    try
    {
        var redirectUri = ExternalRedirectUri(http, cfg, provider);
        var (email, name, picture) = await ext.CompleteAsync(provider, code, redirectUri, ct);
        var result = await authSvc.FindOrCreateExternalUserAsync(email, name, ct);
        await identities.MarkAsync(result.UserId, provider, ct);   // so the UI can hide "change password" for them
        // Adopt the provider's profile picture only if the user hasn't set one of their own.
        if (!string.IsNullOrWhiteSpace(picture) && await avatars.GetAsync(result.UserId, ct) is null)
            await avatars.SetAsync(result.UserId, picture, ct);
        // Hand the token to the SPA via the URL fragment (never sent to the server again; the client reads + clears it).
        return Results.Redirect($"/#access_token={Uri.EscapeDataString(result.Token)}");
    }
    catch { return Results.Redirect("/?authError=1"); }
});

app.MapGet("/me", async (ClaimsPrincipal user, AvatarService avatars, ExternalIdentityService identities, CancellationToken ct) =>
        Results.Ok(new UserDto(user.UserId(), user.Username(), user.Email(),
            await avatars.GetAsync(user.UserId(), ct), await identities.GetProviderAsync(user.UserId(), ct))))
    .RequireAuthorization();

app.MapPut("/me/avatar", async (SetAvatarRequest req, ClaimsPrincipal user, AvatarService avatars, CancellationToken ct) =>
{
    await avatars.SetAsync(user.UserId(), req.DataUrl, ct);
    return Results.NoContent();
}).RequireAuthorization();

// --- Accounts ------------------------------------------------------------
var accounts = app.MapGroup("/accounts").RequireAuthorization();

accounts.MapGet("", async (ClaimsPrincipal user, AccountService svc, CancellationToken ct) =>
    Results.Ok(await svc.ListForUserAsync(user.UserId(), ct)));

accounts.MapPost("", async (CreateAccountRequest req, ClaimsPrincipal user, AccountService svc, CancellationToken ct) =>
    Results.Ok(await svc.CreateAsync(user.UserId(), user.Username(), req, ct)));

accounts.MapPut("/{id:guid}/name", async (Guid id, RenameAccountRequest req, ClaimsPrincipal user, AccountService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    await svc.RenameAsync(user.UserId(), id, req.Name, ct);
    await notifier.AccountChangedAsync(id, user.UserId());
    return Results.NoContent();
});

accounts.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, AccountService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    await svc.DeleteAsync(user.UserId(), id, ct);
    await notifier.AccountChangedAsync(id, user.UserId());
    return Results.NoContent();
});

// --- Membership: leave / remove / transfer / archive ---------------------
accounts.MapGet("/archived", async (ClaimsPrincipal user, AccountService svc, CancellationToken ct) =>
    Results.Ok(await svc.ListArchivedForUserAsync(user.UserId(), ct)));

accounts.MapPost("/{id:guid}/leave", async (Guid id, LeaveAccountRequest req, ClaimsPrincipal user, AccountService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var result = await svc.LeaveAsync(user.UserId(), id, req.NewOwnerUserId, ct);
    await notifier.AccountChangedAsync(id, user.UserId());   // remaining members re-pull the new membership/owner
    return Results.Ok(new { result = result.ToString() });
});

accounts.MapDelete("/{id:guid}/members/{memberUserId:guid}", async (Guid id, Guid memberUserId, ClaimsPrincipal user, AccountService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    await svc.RemoveMemberAsync(user.UserId(), id, memberUserId, ct);
    await notifier.AccountChangedAsync(id, user.UserId());
    return Results.NoContent();
});

accounts.MapPost("/{id:guid}/transfer-ownership", async (Guid id, TransferOwnershipRequest req, ClaimsPrincipal user, AccountService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    await svc.TransferOwnershipAsync(user.UserId(), id, req.NewOwnerUserId, ct);
    await notifier.AccountChangedAsync(id, user.UserId());
    return Results.NoContent();
});

accounts.MapPost("/{id:guid}/reactivate", async (Guid id, ClaimsPrincipal user, AccountService svc, CancellationToken ct) =>
{
    await svc.ReactivateAsync(user.UserId(), id, ct);
    return Results.NoContent();
});

// --- Account snapshot (full aggregate, opaque blob) ----------------------
accounts.MapGet("/{id:guid}/snapshot", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetAsync(user.UserId(), id, ct)));

accounts.MapPut("/{id:guid}/snapshot", async (Guid id, SaveAccountRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var version = await svc.SaveAsync(user.UserId(), id, req, ct);
    await notifier.AccountChangedAsync(id, user.UserId(), version);
    return Results.Ok(new AccountSnapshot(id, version, req.Payload));
});

// --- Member avatars (for showing profile pictures in member lists) -------
accounts.MapGet("/{id:guid}/avatars", async (Guid id, ClaimsPrincipal user, AvatarService avatars, CancellationToken ct) =>
    Results.Ok(await avatars.GetForAccountAsync(user.UserId(), id, ct)));

// --- Bank sync (Open Banking via GoCardless) -----------------------------
accounts.MapGet("/{id:guid}/bank/status", async (Guid id, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetStatusAsync(user.UserId(), id, ct)));

accounts.MapGet("/{id:guid}/bank/institutions", async (Guid id, string? country, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
    Results.Ok(await svc.SearchInstitutionsAsync(user.UserId(), id, country ?? "GB", ct)));

accounts.MapPost("/{id:guid}/bank/link", async (Guid id, StartBankLinkRequest req, HttpContext http, ClaimsPrincipal user, BankSyncService svc, IConfiguration cfg, CancellationToken ct) =>
    Results.Ok(await svc.StartLinkAsync(user.UserId(), id, req, BankCallbackUrl(http, cfg), ct)));

accounts.MapPost("/{id:guid}/bank/sync", async (Guid id, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
{
    await svc.SyncAsync(user.UserId(), id, ct);
    return Results.NoContent();
});

accounts.MapGet("/{id:guid}/bank/pending", async (Guid id, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetPendingAsync(user.UserId(), id, ct)));

accounts.MapPost("/{id:guid}/bank/ack", async (Guid id, BankTransactionAck ack, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
{
    await svc.AckAsync(user.UserId(), id, ack.ExternalId, ack.Confirmed, ct);
    return Results.NoContent();
});

accounts.MapDelete("/{id:guid}/bank/connection", async (Guid id, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
{
    await svc.DisconnectAsync(user.UserId(), id, ct);
    return Results.NoContent();
});

accounts.MapPost("/{id:guid}/bank/reset", async (Guid id, DateOnly from, DateOnly to, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
{
    await svc.ResetRangeAsync(user.UserId(), id, from, to, ct);
    return Results.NoContent();
});

accounts.MapGet("/{id:guid}/bank/mappings", async (Guid id, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetMappingsAsync(user.UserId(), id, ct)));

accounts.MapPut("/{id:guid}/bank/mappings", async (Guid id, SetBankMappingRequest req, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
{
    await svc.SetMappingAsync(user.UserId(), id, req.Description, req.Kind, req.TargetId, ct);
    return Results.NoContent();
});

accounts.MapDelete("/{id:guid}/bank/mappings", async (Guid id, string description, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
{
    await svc.RemoveMappingAsync(user.UserId(), id, description, ct);
    return Results.NoContent();
});

// Public: the bank redirects here (with ?code=<auth code>&state=<accountId>) after the user consents. No auth —
// the code is exchanged with Enable Banking server-side to prove real consent — then we bounce to the SPA.
app.MapGet("/bank/callback", async (string? code, string? state, BankSyncService svc, CancellationToken ct) =>
{
    if (!string.IsNullOrEmpty(code) && Guid.TryParseExact(state, "N", out var accountId)
        && await svc.CompleteLinkAsync(accountId, code, ct))
        return Results.Redirect("/?bank=linked");
    return Results.Redirect("/?bank=error");
});

// --- Excel export (one sheet per period) ---------------------------------
accounts.MapGet("/{id:guid}/export", async (Guid id, ClaimsPrincipal user, AccountExportService svc, CancellationToken ct) =>
{
    var (bytes, fileName) = await svc.ExportAsync(user.UserId(), id, ct);
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
});

// --- Invitations ---------------------------------------------------------
accounts.MapPost("/{id:guid}/invitations", async (Guid id, CreateInvitationRequest req, ClaimsPrincipal user, InvitationService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var created = await svc.CreateAsync(user.UserId(), id, req.Username, ct);
    await notifier.InvitationReceivedAsync(created.InviteeUserId, created.InvitationId, created.AccountId, created.AccountName, created.InviterUsername);
    return Results.Ok();
});

var invitations = app.MapGroup("/invitations").RequireAuthorization();

invitations.MapGet("/pending", async (ClaimsPrincipal user, InvitationService svc, CancellationToken ct) =>
    Results.Ok(await svc.PendingForUserAsync(user.UserId(), ct)));

invitations.MapPost("/{id:guid}/accept", async (Guid id, ClaimsPrincipal user, InvitationService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var accountId = await svc.AcceptAsync(user.UserId(), id, ct);
    await notifier.AccountChangedAsync(accountId, user.UserId());
    return Results.Ok(new { accountId });
});

invitations.MapPost("/{id:guid}/decline", async (Guid id, ClaimsPrincipal user, InvitationService svc, CancellationToken ct) =>
{
    await svc.DeclineAsync(user.UserId(), id, ct);
    return Results.NoContent();
});

app.MapHub<SyncHub>("/hubs/sync").RequireAuthorization();

// SPA fallback: any non-API route serves the WASM client's index.html (client-side routing).
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate"
});

app.Run();

// The provider redirect URI must exactly match what's registered in the Google/Facebook console.
// Behind a proxy (Cloud Run) the request scheme can read as http, so prefer an explicit Auth:PublicBaseUrl.
static string ExternalRedirectUri(HttpContext http, IConfiguration cfg, string provider)
{
    var baseUrl = cfg["Auth:PublicBaseUrl"]?.TrimEnd('/')
                  ?? $"{http.Request.Scheme}://{http.Request.Host}";
    return $"{baseUrl}/auth/external/{provider}/callback";
}

// Where the bank sends the user back after consent. Shares Auth:PublicBaseUrl so it's correct behind the
// Cloud Run proxy; this exact URL must be whitelisted for the app in the GoCardless dashboard.
static string BankCallbackUrl(HttpContext http, IConfiguration cfg)
{
    var baseUrl = cfg["Auth:PublicBaseUrl"]?.TrimEnd('/')
                  ?? $"{http.Request.Scheme}://{http.Request.Host}";
    return $"{baseUrl}/bank/callback";
}

/// <summary>Exposed so integration tests can host the app via WebApplicationFactory.</summary>
public partial class Program;
