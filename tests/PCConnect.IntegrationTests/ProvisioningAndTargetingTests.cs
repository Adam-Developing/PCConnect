using System.Net;
using System.Net.Http.Json;
using Dapper;
using Npgsql;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using Shouldly;

namespace PCConnect.IntegrationTests;

/// <summary>
/// Adding a PC by signing in on it, rather than by reading a code off its screen
/// (ADR-0013).
/// </summary>
[Collection(ApiCollection.Name)]
public class DeviceProvisioningTests(ApiFixture fixture)
{

    /// <summary>
    /// Reads a successful response, failing with the body when the status is not
    /// what the test expected — a null reference three lines later says nothing.
    /// </summary>
    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {body}");

        return System.Text.Json.JsonSerializer.Deserialize<T>(body,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    [Fact]
    public async Task Provisioning_creates_the_device_and_the_agent_collects_the_secret_once()
    {
        var user = await fixture.RegisterUserAsync();

        var provisioned = await Read<DeviceProvisionResponse>(await user.Client.PostAsJsonAsync("/v2/devices/provision",
                new DeviceProvisionRequest("STUDY-PC", "windows", "2.0.0")));

        provisioned.DisplayName.ShouldBe("STUDY-PC");
        provisioned.ProvisioningTicket.ShouldNotBeNullOrWhiteSpace();

        // The device exists straight away: the user half and the claim are the
        // same request, unlike the two-step code flow.
        var devices = await user.Client.GetFromJsonAsync<Page<DeviceResponse>>("/v2/devices");
        devices!.Items.ShouldContain(d => d.Id == provisioned.DeviceId);

        // The agent redeems the ticket through the ordinary poll, so the secret
        // reaches the agent and never the app that asked for the ticket.
        var anonymous = fixture.CreateClient(ApiFixture.FreshIp());
        var poll = await Read<PairPollResponse>(await anonymous.PostAsJsonAsync("/v2/devices/pair/poll",
                new PairPollRequest(provisioned.ProvisioningTicket)));

        poll.Status.ShouldBe("paired");
        poll.DeviceId.ShouldBe(provisioned.DeviceId);
        poll.DeviceSecret.ShouldNotBeNullOrWhiteSpace();

        // Exactly once. A ticket that could be replayed would be a device secret
        // anyone who saw it could collect.
        var second = await anonymous.PostAsJsonAsync("/v2/devices/pair/poll",
            new PairPollRequest(provisioned.ProvisioningTicket));

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Provisioning_requires_a_signed_in_caller()
    {
        var anonymous = fixture.CreateClient(ApiFixture.FreshIp());

        var response = await anonymous.PostAsJsonAsync("/v2/devices/provision",
            new DeviceProvisionRequest("SOMEONE-ELSES-PC"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_provisioned_device_belongs_to_the_caller_and_nobody_else()
    {
        var owner = await fixture.RegisterUserAsync();
        var stranger = await fixture.RegisterUserAsync();

        var provisioned = await Read<DeviceProvisionResponse>(await owner.Client.PostAsJsonAsync("/v2/devices/provision", new DeviceProvisionRequest("OWNER-PC")));

        var strangersDevices = await stranger.Client.GetFromJsonAsync<Page<DeviceResponse>>("/v2/devices");
        strangersDevices!.Items.ShouldNotContain(d => d.Id == provisioned.DeviceId);

        var reach = await stranger.Client.GetAsync($"/v2/devices/{provisioned.DeviceId}");
        reach.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Two_PCs_provisioned_with_the_same_name_stay_separate_devices()
    {
        var user = await fixture.RegisterUserAsync();

        var first = await Read<DeviceProvisionResponse>(await user.Client.PostAsJsonAsync("/v2/devices/provision", new DeviceProvisionRequest("PC")));

        var second = await Read<DeviceProvisionResponse>(await user.Client.PostAsJsonAsync("/v2/devices/provision", new DeviceProvisionRequest("PC")));

        // The unique constraint on (user_id, display_name) would otherwise make
        // the second sign-in fail rather than add a PC.
        second.DeviceId.ShouldNotBe(first.DeviceId);
        second.DisplayName.ShouldNotBe(first.DisplayName);
    }
}

/// <summary>
/// Which PCs a reminder shows on (ADR-0013). No targets means every PC, which is
/// what every reminder written before targeting existed means.
/// </summary>
[Collection(ApiCollection.Name)]
public class ReminderTargetingTests(ApiFixture fixture)
{

    /// <summary>
    /// Reads a successful response, failing with the body when the status is not
    /// what the test expected — a null reference three lines later says nothing.
    /// </summary>
    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {body}");

        return System.Text.Json.JsonSerializer.Deserialize<T>(body,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    [Fact]
    public async Task A_reminder_with_no_targets_shows_on_every_PC()
    {
        var user = await fixture.RegisterUserAsync();

        var created = await Read<ReminderResponse>(await user.Client.PostAsJsonAsync("/v2/reminders",
                new CreateReminderRequest("Take the bins out", DateTimeOffset.UtcNow.AddHours(2), "Europe/London")));

        // Null, not an empty list: the two would read the same in the UI and mean
        // opposite things.
        created.DeviceIds.ShouldBeNull();
    }

    [Fact]
    public async Task A_reminder_can_name_the_PCs_it_shows_on()
    {
        var user = await fixture.RegisterUserAsync();
        var study = await ProvisionAsync(user, "STUDY-PC");
        _ = await ProvisionAsync(user, "LAPTOP");

        var created = await Read<ReminderResponse>(await user.Client.PostAsJsonAsync("/v2/reminders",
                new CreateReminderRequest(
                    "Stand up and stretch", DateTimeOffset.UtcNow.AddHours(1), "Europe/London",
                    DeviceIds: [study])));

        created.DeviceIds.ShouldBe([study]);

        // And it survives the round trip, which is what the client filters on.
        var readBack = await user.Client.GetFromJsonAsync<ReminderResponse>($"/v2/reminders/{created.Id}");
        readBack!.DeviceIds.ShouldBe([study]);

        var listed = await user.Client.GetFromJsonAsync<Page<ReminderResponse>>("/v2/reminders");
        listed!.Items.Single(r => r.Id == created.Id).DeviceIds.ShouldBe([study]);
    }

    [Fact]
    public async Task A_reminder_cannot_name_someone_elses_PC()
    {
        var owner = await fixture.RegisterUserAsync();
        var stranger = await fixture.RegisterUserAsync();
        var theirs = await ProvisionAsync(stranger, "THEIR-PC");

        var response = await owner.Client.PostAsJsonAsync("/v2/reminders",
            new CreateReminderRequest(
                "Not yours", DateTimeOffset.UtcNow.AddHours(1), "Europe/London", DeviceIds: [theirs]));

        // Otherwise the id would come straight back out on the reminder it was
        // written to — the same shape of defect as trusting a caller-supplied
        // PCName (S1-08).
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Choosing_no_PCs_at_all_is_refused()
    {
        var user = await fixture.RegisterUserAsync();

        var response = await user.Client.PostAsJsonAsync("/v2/reminders",
            new CreateReminderRequest(
                "Nobody would see this", DateTimeOffset.UtcNow.AddHours(1), "Europe/London", DeviceIds: []));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Updating_replaces_the_whole_target_set()
    {
        var user = await fixture.RegisterUserAsync();
        var study = await ProvisionAsync(user, "STUDY-PC");
        var laptop = await ProvisionAsync(user, "LAPTOP");

        var created = await Read<ReminderResponse>(await user.Client.PostAsJsonAsync("/v2/reminders",
                new CreateReminderRequest(
                    "Back up the NAS", DateTimeOffset.UtcNow.AddHours(3), "Europe/London", DeviceIds: [study])));

        var updated = await Read<ReminderResponse>(await user.Client.PatchAsJsonAsync($"/v2/reminders/{created.Id}",
                new UpdateReminderRequest(DeviceIds: [laptop])));

        updated.DeviceIds.ShouldBe([laptop]);
    }

    [Fact]
    public async Task Revoking_a_PC_removes_it_from_the_reminders_that_named_it()
    {
        var user = await fixture.RegisterUserAsync();
        var study = await ProvisionAsync(user, "STUDY-PC");

        var created = await Read<ReminderResponse>(await user.Client.PostAsJsonAsync("/v2/reminders",
                new CreateReminderRequest(
                    "Only on the study PC", DateTimeOffset.UtcNow.AddHours(1), "Europe/London",
                    DeviceIds: [study])));

        (await user.Client.DeleteAsync($"/v2/devices/{study}")).EnsureSuccessStatusCode();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        // ON DELETE CASCADE, so no row is left pointing at a device that is gone.
        var remaining = await connection.ExecuteScalarAsync<long>("""
            SELECT count(*) FROM reminder_devices rd
              JOIN reminders r ON r.id = rd.reminder_id
             WHERE r.public_id = @Id
            """, new { Id = Guid.Parse(created.Id) });

        remaining.ShouldBe(0);
    }

    [Fact]
    public async Task The_server_says_it_can_target_reminders()
    {
        var client = fixture.CreateClient(ApiFixture.FreshIp());
        var discovery = await client.GetFromJsonAsync<DiscoveryResponse>("/v2/meta/discovery");

        // The clients only offer "Choose PCs" when this is present; a picker the
        // server ignored would be worse than none.
        discovery!.Capabilities.ShouldContain("reminders.targets");
        discovery.Capabilities.ShouldContain("devices.provisioning");
    }

    private static async Task<string> ProvisionAsync(TestUser user, string name) =>
        (await Read<DeviceProvisionResponse>(await user.Client.PostAsJsonAsync("/v2/devices/provision", new DeviceProvisionRequest(name)))).DeviceId;
}
