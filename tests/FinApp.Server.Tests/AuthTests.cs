using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;

namespace FinApp.Server.Tests;

public class AuthTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public AuthTests(FinAppServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_returns_token_and_identity()
    {
        var (_, auth) = await _factory.RegisterAndAuthAsync("alice");
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        Assert.Equal("alice", auth.Username);
        Assert.NotEqual(Guid.Empty, auth.UserId);
    }

    [Fact]
    public async Task External_providers_are_off_when_unconfigured()
    {
        var client = _factory.CreateClient();
        var providers = (await (await client.GetAsync("/auth/providers"))
            .Content.ReadFromJsonAsync<ExternalProvidersDto>())!;
        Assert.False(providers.Google);
        Assert.False(providers.Facebook);
    }

    [Fact]
    public async Task External_start_is_not_found_when_provider_is_unconfigured()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/auth/external/google");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Duplicate_username_is_rejected()
    {
        await _factory.RegisterAndAuthAsync("bob");

        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/register",
            new RegisterRequest("BOB", "other@example.com", "password123"));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Short_password_is_rejected()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/register",
            new RegisterRequest("shorty", "shorty@example.com", "abc"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Login_succeeds_with_correct_password_and_fails_otherwise()
    {
        await _factory.RegisterAndAuthAsync("carol", "carol@example.com", "supersecret");
        var client = _factory.CreateClient();

        var ok = await client.PostAsJsonAsync("/auth/login", new LoginRequest("carol", "supersecret"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var byEmail = await client.PostAsJsonAsync("/auth/login", new LoginRequest("carol@example.com", "supersecret"));
        Assert.Equal(HttpStatusCode.OK, byEmail.StatusCode);

        var bad = await client.PostAsJsonAsync("/auth/login", new LoginRequest("carol", "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    [Fact]
    public async Task Me_requires_authentication()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync("/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        var (client, auth) = await _factory.RegisterAndAuthAsync("dave");
        var me = await client.GetFromJsonAsync<UserDto>("/me");
        Assert.Equal(auth.UserId, me!.Id);
        Assert.Equal("dave", me.Username);
    }
}
