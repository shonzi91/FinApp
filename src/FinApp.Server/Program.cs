using System.Text;
using FinApp.Contracts;
using FinApp.Domain.Common;
using FinApp.Persistence;
using FinApp.Server.Accounts;
using FinApp.Server.Auth;
using FinApp.Server.Infrastructure;
using FinApp.Server.Invitations;
using FinApp.Server.Sync;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<SnapshotService>();
builder.Services.AddScoped<InvitationService>();
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

// Ensure the server DB schema is current on startup.
// SQLite uses the EF migrations; Postgres uses EnsureCreated (the migrations are SQLite-specific,
// and the cloud DB is provisioned fresh) so we build the schema straight from the model.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FinAppDbContext>();
    if (usePostgres) db.Database.EnsureCreated();
    else db.Database.Migrate();
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

app.MapGet("/me", (ClaimsPrincipal user) =>
        Results.Ok(new UserDto(user.UserId(), user.Username(), user.Email())))
    .RequireAuthorization();

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

// --- Account snapshot (full aggregate, opaque blob) ----------------------
accounts.MapGet("/{id:guid}/snapshot", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetAsync(user.UserId(), id, ct)));

accounts.MapPut("/{id:guid}/snapshot", async (Guid id, SaveAccountRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var version = await svc.SaveAsync(user.UserId(), id, req, ct);
    await notifier.AccountChangedAsync(id, user.UserId(), version);
    return Results.Ok(new AccountSnapshot(id, version, req.Payload));
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

/// <summary>Exposed so integration tests can host the app via WebApplicationFactory.</summary>
public partial class Program;
