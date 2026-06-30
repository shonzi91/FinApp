using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;

namespace FinApp.Server.Tests;

public class AvatarApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public AvatarApiTests(FinAppServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Avatar_round_trips_through_me_and_can_be_cleared()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("avatar_user");

        // No picture initially.
        var me0 = (await (await client.GetAsync("/me")).Content.ReadFromJsonAsync<UserDto>())!;
        Assert.Null(me0.Avatar);

        // Upload one (upsert via raw SQL) and read it back through /me.
        const string pic = "data:image/png;base64,AAAA";
        var put = await client.PutAsJsonAsync("/me/avatar", new SetAvatarRequest(pic));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var me1 = (await (await client.GetAsync("/me")).Content.ReadFromJsonAsync<UserDto>())!;
        Assert.Equal(pic, me1.Avatar);

        // Updating again overwrites (ON CONFLICT path).
        await client.PutAsJsonAsync("/me/avatar", new SetAvatarRequest("data:image/png;base64,BBBB"));
        var me2 = (await (await client.GetAsync("/me")).Content.ReadFromJsonAsync<UserDto>())!;
        Assert.Equal("data:image/png;base64,BBBB", me2.Avatar);

        // Clearing it removes the row.
        await client.PutAsJsonAsync("/me/avatar", new SetAvatarRequest(null));
        var me3 = (await (await client.GetAsync("/me")).Content.ReadFromJsonAsync<UserDto>())!;
        Assert.Null(me3.Avatar);
    }

    [Fact]
    public async Task Account_avatars_returns_member_pictures()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("avatar_owner");
        await client.PutAsJsonAsync("/me/avatar", new SetAvatarRequest("data:image/png;base64,CCCC"));

        var account = (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest("Shared", "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

        var avatars = (await (await client.GetAsync($"/accounts/{account.Id}/avatars"))
            .Content.ReadFromJsonAsync<Dictionary<Guid, string>>())!;

        Assert.Equal("data:image/png;base64,CCCC", Assert.Contains(account.OwnerUserId, avatars));
    }
}
