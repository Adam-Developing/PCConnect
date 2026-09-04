using System.Net.Http.Json;
using PCConnect.Core.Contracts;
using Shouldly;

namespace PCConnect.IntegrationTests;

/// <summary>
/// Guards the test harness itself: if two "different" users turn out to share a
/// token, every authorisation assertion in the suite becomes meaningless while
/// still passing.
/// </summary>
[Collection(ApiCollection.Name)]
public class HarnessTests(ApiFixture fixture)
{
    [Fact]
    public async Task Two_registered_users_are_actually_different_principals()
    {
        var first = await fixture.RegisterUserAsync();
        var second = await fixture.RegisterUserAsync();

        first.Username.ShouldNotBe(second.Username);
        first.Tokens.AccessToken.ShouldNotBe(second.Tokens.AccessToken);

        var firstProfile = await first.Client.GetFromJsonAsync<ProfileResponse>("/v2/account/profile");
        var secondProfile = await second.Client.GetFromJsonAsync<ProfileResponse>("/v2/account/profile");

        firstProfile!.Username.ShouldBe(first.Username);
        secondProfile!.Username.ShouldBe(second.Username);
        firstProfile.Id.ShouldNotBe(secondProfile.Id);
    }

    [Fact]
    public async Task A_device_token_is_not_a_user_token()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user, "Harness-PC");

        user.Tokens.AccessToken.ShouldNotBe(device.Tokens.AccessToken);
        device.Tokens.Scopes.ShouldBe(["command:receive", "command:ack"], ignoreOrder: true);
        device.Tokens.User.ShouldBeNull();
    }
}
