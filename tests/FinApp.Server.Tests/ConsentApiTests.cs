using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;

namespace FinApp.Server.Tests;

/// <summary>
/// Consent is an append-only audit log; the latest event for a scope is the current flag. Linking a bank is
/// blocked until bank-link consent is active.
/// </summary>
public class ConsentApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public ConsentApiTests(FinAppServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Recording_consent_flips_the_active_flag()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("consent1");

        Assert.False((await client.GetFromJsonAsync<ConsentStatusDto>("/consent?scope=login"))!.Active);

        await client.PostAsJsonAsync("/consent", new RecordConsentRequest("login", null, true));
        var granted = await client.GetFromJsonAsync<ConsentStatusDto>("/consent?scope=login");
        Assert.True(granted!.Active);
        Assert.NotNull(granted.At);

        // A later withdrawal wins (latest event is the current state).
        await client.PostAsJsonAsync("/consent", new RecordConsentRequest("login", null, false));
        Assert.False((await client.GetFromJsonAsync<ConsentStatusDto>("/consent?scope=login"))!.Active);
    }

    [Fact]
    public async Task Linking_a_bank_is_blocked_without_consent()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("consent2");
        var created = await (await client.PostAsJsonAsync("/accounts",
            new CreateAccountRequest("Main", "GBP"))).Content.ReadFromJsonAsync<AccountSummaryDto>();

        var resp = await client.PostAsJsonAsync($"/accounts/{created!.Id}/bank/link",
            new StartBankLinkRequest("Revolut", "GB"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
