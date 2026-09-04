using System.Net;
using System.Net.Http.Json;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using Shouldly;

namespace PCConnect.IntegrationTests;

/// <summary>
/// The authorisation matrix (04 §6): for every resource, the owner gets 2xx,
/// another user gets 404, a token without the scope gets 403, and no token gets
/// 401.
///
/// 03 §9 calls this "the highest-value test in the codebase and it does not
/// exist today". It is the suite that would have caught S1-08.
/// </summary>
[Collection(ApiCollection.Name)]
public class AuthorisationMatrixTests(ApiFixture fixture)
{
    // ── devices ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Device_is_visible_to_its_owner()
    {
        var owner = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(owner);

        var response = await owner.Client.GetAsync($"/v2/devices/{device.DeviceId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Device_is_invisible_to_another_user()
    {
        var owner = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(owner);
        var stranger = await fixture.RegisterUserAsync();

        var response = await stranger.Client.GetAsync($"/v2/devices/{device.DeviceId}");

        // 404, not 403: telling a stranger that the id exists is an oracle.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ApiFixture.ErrorCodeAsync(response)).ShouldBe(ErrorCodes.DeviceNotFound);
    }

    [Fact]
    public async Task Device_cannot_be_revoked_by_another_user()
    {
        var owner = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(owner);
        var stranger = await fixture.RegisterUserAsync();

        (await stranger.Client.DeleteAsync($"/v2/devices/{device.DeviceId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // And the owner still has it.
        (await owner.Client.GetAsync($"/v2/devices/{device.DeviceId}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Device_list_requires_a_token()
    {
        var response = await fixture.CreateClient().GetAsync("/v2/devices");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ── commands ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Command_cannot_be_issued_to_another_users_device()
    {
        var owner = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(owner);
        var stranger = await fixture.RegisterUserAsync();

        var response = await stranger.Client.PostAsJsonAsync("/v2/commands",
            new IssueCommandRequest(Guid.CreateVersion7().ToString(), device.DeviceId, "lock"));

        // This is S1-08 in one assertion: ownership is decided by the
        // authenticated user id, never by a name the caller supplies.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Command_history_is_not_visible_to_another_user()
    {
        var owner = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(owner);
        var stranger = await fixture.RegisterUserAsync();

        var issued = await owner.Client.PostAsJsonAsync("/v2/commands",
            new IssueCommandRequest(Guid.CreateVersion7().ToString(), device.DeviceId, "lock"));
        var command = await issued.Content.ReadFromJsonAsync<CommandResponse>();

        (await stranger.Client.GetAsync($"/v2/commands/{command!.Id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var strangerHistory = await stranger.Client.GetFromJsonAsync<Page<CommandResponse>>("/v2/commands");
        strangerHistory!.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_users_command_id_cannot_be_hijacked_by_another_user()
    {
        var owner = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(owner);
        var stranger = await fixture.RegisterUserAsync();
        var strangerDevice = await fixture.PairDeviceAsync(stranger);

        var commandId = Guid.CreateVersion7().ToString();

        (await owner.Client.PostAsJsonAsync("/v2/commands",
            new IssueCommandRequest(commandId, device.DeviceId, "lock")))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        // Replaying someone else's client-generated id must not return their
        // command, and must not overwrite it either.
        var response = await stranger.Client.PostAsJsonAsync("/v2/commands",
            new IssueCommandRequest(commandId, strangerDevice.DeviceId, "lock"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── reminders ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reminder_is_invisible_to_another_user()
    {
        var owner = await fixture.RegisterUserAsync();
        var stranger = await fixture.RegisterUserAsync();

        var created = await (await owner.Client.PostAsJsonAsync("/v2/reminders",
            new CreateReminderRequest("Private note", DateTimeOffset.UtcNow.AddDays(1))))
            .Content.ReadFromJsonAsync<ReminderResponse>();

        (await stranger.Client.GetAsync($"/v2/reminders/{created!.Id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await stranger.Client.DeleteAsync($"/v2/reminders/{created.Id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var strangerList = await stranger.Client.GetFromJsonAsync<Page<ReminderResponse>>("/v2/reminders");
        strangerList!.Items.ShouldBeEmpty();
    }

    // ── scope separation ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_device_token_cannot_read_reminders()
    {
        var owner = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(owner);

        var response = await device.Client.GetAsync("/v2/reminders");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ApiFixture.ErrorCodeAsync(response)).ShouldBe(ErrorCodes.AuthScopeInsufficient);
    }

    [Fact]
    public async Task A_device_token_cannot_issue_a_command_even_to_itself()
    {
        var owner = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(owner);

        var response = await device.Client.PostAsJsonAsync("/v2/commands",
            new IssueCommandRequest(Guid.CreateVersion7().ToString(), device.DeviceId, "lock"));

        // command:issue and command:receive are disjoint (03 §2.3). A stolen
        // device secret can execute on one PC and cannot ask for anything.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_device_token_cannot_manage_the_account()
    {
        var owner = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(owner);

        (await device.Client.GetAsync("/v2/account/sessions")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await device.Client.DeleteAsync("/v2/account")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_user_token_cannot_receive_or_acknowledge_commands()
    {
        var owner = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(owner);

        (await owner.Client.GetAsync("/v2/commands/pending")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var command = await (await owner.Client.PostAsJsonAsync("/v2/commands",
            new IssueCommandRequest(Guid.CreateVersion7().ToString(), device.DeviceId, "lock")))
            .Content.ReadFromJsonAsync<CommandResponse>();

        (await owner.Client.PostAsJsonAsync($"/v2/commands/{command!.Id}/ack", new AckCommandRequest("ok")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_device_cannot_acknowledge_another_devices_command()
    {
        var owner = await fixture.RegisterUserAsync();
        var first = await fixture.PairDeviceAsync(owner, "First-PC");
        var second = await fixture.PairDeviceAsync(owner, "Second-PC");

        var command = await (await owner.Client.PostAsJsonAsync("/v2/commands",
            new IssueCommandRequest(Guid.CreateVersion7().ToString(), first.DeviceId, "lock")))
            .Content.ReadFromJsonAsync<CommandResponse>();

        // Even within one account: a device may only speak for itself.
        (await second.Client.PostAsJsonAsync($"/v2/commands/{command!.Id}/ack", new AckCommandRequest("ok")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_device_cannot_heartbeat_for_another_device()
    {
        var owner = await fixture.RegisterUserAsync();
        var first = await fixture.PairDeviceAsync(owner, "Heartbeat-A");
        var second = await fixture.PairDeviceAsync(owner, "Heartbeat-B");

        (await second.Client.PostAsJsonAsync($"/v2/devices/{first.DeviceId}/heartbeat", new HeartbeatRequest()))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── unauthenticated ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/v2/devices")]
    [InlineData("GET", "/v2/reminders")]
    [InlineData("GET", "/v2/commands")]
    [InlineData("GET", "/v2/commands/pending")]
    [InlineData("GET", "/v2/account/profile")]
    [InlineData("GET", "/v2/account/sessions")]
    [InlineData("GET", "/v2/account/export")]
    [InlineData("POST", "/v2/auth/step-up/start")]
    [InlineData("POST", "/v2/auth/passkeys/register/start")]
    public async Task Protected_endpoints_reject_an_anonymous_caller(string method, string path)
    {
        var response = await fixture.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ApiFixture.ErrorCodeAsync(response)).ShouldBe(ErrorCodes.AuthTokenInvalid);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("eyJhbGciOiJub25lIn0.eyJzdWIiOiIxIn0.")]
    public async Task A_forged_or_unsigned_token_is_rejected(string token)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // `alg: none` is refused before any signature check, because the
        // verifier pins the algorithm (03 §2.2).
        (await client.GetAsync("/v2/devices")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_revoked_device_token_stops_working_immediately()
    {
        var owner = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(owner, "Revoked-PC");

        (await device.Client.GetAsync("/v2/commands/pending")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await owner.Client.DeleteAsync($"/v2/devices/{device.DeviceId}");

        // Not "when its 15 minutes are up": the device is checked on every
        // request, so unpairing takes effect at once.
        var response = await device.Client.GetAsync("/v2/commands/pending");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ApiFixture.ErrorCodeAsync(response)).ShouldBe(ErrorCodes.DeviceRevoked);
    }
}
