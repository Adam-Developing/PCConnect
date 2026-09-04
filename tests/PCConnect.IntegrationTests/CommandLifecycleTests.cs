using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Core.Domain;
using PCConnect.Infrastructure.Contexts;
using PCConnect.Infrastructure.Contexts.Commands;
using Shouldly;

namespace PCConnect.IntegrationTests;

/// <summary>
/// The command lifecycle end to end: issue, deliver, acknowledge, expire — and
/// the properties that make each of those safe (05 §4, ADR-0003, ADR-0011).
/// </summary>
[Collection(ApiCollection.Name)]
public class CommandLifecycleTests(ApiFixture fixture)
{
    /// <summary>
    /// Ages a command past its TTL. Time is moved in the row rather than by
    /// sleeping: the assertions are about the sweep and the pending query, not
    /// about how long a test takes.
    /// </summary>
    private const string AgeCommandSql = """
        UPDATE commands
           SET issued_at = now() - interval '10 minutes',
               expires_at = now() - interval '1 minute'
         WHERE public_id = @Id
        """;

    private static IssueCommandRequest Issue(string deviceId, string type, string? stepUp = null, int? ttl = null) =>
        new(Guid.CreateVersion7().ToString(), deviceId, type, null, ttl, stepUp);

    [Fact]
    public async Task A_standard_command_needs_no_confirmation()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        var response = await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "lock"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var command = await response.Content.ReadFromJsonAsync<CommandResponse>();
        command!.Status.ShouldBe(CommandStatuses.Issued);
        command.RiskTier.ShouldBe(RiskTiers.Standard);
        command.ExpiresAt.ShouldBeGreaterThan(command.IssuedAt);
    }

    [Fact]
    public async Task Replaying_the_client_generated_id_returns_the_same_command()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);
        var request = Issue(device.DeviceId, "lock");

        var first = await user.Client.PostAsJsonAsync("/v2/commands", request);
        var second = await user.Client.PostAsJsonAsync("/v2/commands", request);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        // 200, not 201: the retry created nothing. Without this, "retry" and
        // "shut my PC down twice" are the same operation (02 §3.2).
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var a = await first.Content.ReadFromJsonAsync<CommandResponse>();
        var b = await second.Content.ReadFromJsonAsync<CommandResponse>();
        b!.Id.ShouldBe(a!.Id);

        var history = await user.Client.GetFromJsonAsync<Page<CommandResponse>>("/v2/commands");
        history!.Items.Count(c => c.Id == a.Id).ShouldBe(1);
    }

    [Fact]
    public async Task A_second_command_does_not_overwrite_an_undelivered_first()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "lock"));
        await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "sleep"));

        // v1 had one mutable mailbox row per device, so the second command
        // silently replaced the first (S2-04).
        var pending = await device.Client.GetFromJsonAsync<Page<PendingCommand>>("/v2/commands/pending");

        pending!.Items.Count.ShouldBe(2);
        pending.Items.Select(c => c.Type).ShouldBe(["lock", "sleep"], ignoreOrder: true);
    }

    [Fact]
    public async Task Claiming_pending_commands_marks_them_delivered_so_a_second_poll_serves_nothing()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "lock"));

        var first = await device.Client.GetFromJsonAsync<Page<PendingCommand>>("/v2/commands/pending");
        var second = await device.Client.GetFromJsonAsync<Page<PendingCommand>>("/v2/commands/pending");

        first!.Items.Count.ShouldBe(1);
        second!.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_agent_is_told_the_server_time_so_it_can_judge_freshness()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "lock"));
        var pending = await device.Client.GetFromJsonAsync<Page<PendingCommand>>("/v2/commands/pending");

        // An agent with a wrong clock must not expire everything or execute
        // stale commands; it anchors on this (05 §8).
        pending!.Items[0].ServerTime.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Acknowledging_carries_the_real_outcome_back_to_the_issuer()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        var issued = await (await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "lock")))
            .Content.ReadFromJsonAsync<CommandResponse>();

        await device.Client.GetAsync("/v2/commands/pending");

        var acked = await (await device.Client.PostAsJsonAsync($"/v2/commands/{issued!.Id}/ack",
            new AckCommandRequest("error", "win32_5", "Access is denied")))
            .Content.ReadFromJsonAsync<CommandResponse>();

        // v1 returned {"message":"Success"} for "a database row was written".
        acked!.Status.ShouldBe(CommandStatuses.Failed);
        acked.ResultCode.ShouldBe("win32_5");
        acked.ResultMessage.ShouldBe("Access is denied");

        var seenByUser = await user.Client.GetFromJsonAsync<CommandResponse>($"/v2/commands/{issued.Id}");
        seenByUser!.Status.ShouldBe(CommandStatuses.Failed);
    }

    [Fact]
    public async Task A_duplicate_acknowledgement_changes_nothing()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        var issued = await (await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "lock")))
            .Content.ReadFromJsonAsync<CommandResponse>();

        await device.Client.GetAsync("/v2/commands/pending");
        await device.Client.PostAsJsonAsync($"/v2/commands/{issued!.Id}/ack", new AckCommandRequest("ok"));

        var duplicate = await device.Client.PostAsJsonAsync($"/v2/commands/{issued.Id}/ack",
            new AckCommandRequest("error", "should-not-apply"));

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var final = await user.Client.GetFromJsonAsync<CommandResponse>($"/v2/commands/{issued.Id}");
        final!.Status.ShouldBe(CommandStatuses.Succeeded);
        final.ResultCode.ShouldNotBe("should-not-apply");
    }

    [Fact]
    public async Task A_command_can_be_cancelled_before_it_is_delivered_and_not_after()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        var first = await (await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "lock")))
            .Content.ReadFromJsonAsync<CommandResponse>();

        var cancelled = await (await user.Client.PostAsync($"/v2/commands/{first!.Id}/cancel", null))
            .Content.ReadFromJsonAsync<CommandResponse>();
        cancelled!.Status.ShouldBe(CommandStatuses.Cancelled);

        // A cancelled command is never served to the agent.
        var pending = await device.Client.GetFromJsonAsync<Page<PendingCommand>>("/v2/commands/pending");
        pending!.Items.ShouldBeEmpty();

        var second = await (await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "sleep")))
            .Content.ReadFromJsonAsync<CommandResponse>();
        await device.Client.GetAsync("/v2/commands/pending");

        (await user.Client.PostAsync($"/v2/commands/{second!.Id}/cancel", null))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ── TTL: the fix for S2-03 ───────────────────────────────────────────────

    [Fact]
    public async Task A_command_past_its_ttl_is_expired_and_never_delivered()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        var issued = await (await user.Client.PostAsJsonAsync("/v2/commands",
            Issue(device.DeviceId, "lock", ttl: 5))).Content.ReadFromJsonAsync<CommandResponse>();

        // Rather than sleeping, the row is aged: the assertion is about the
        // sweep and the pending query, not about wall-clock time.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        // Both instants move: ck_commands_ttl requires expires_at > issued_at,
        // and that constraint is part of what makes a TTL meaningful.
        await connection.ExecuteAsync(AgeCommandSql, new { Id = Guid.Parse(issued!.Id) });

        // The agent asking for work must not be handed a stale command even
        // before the sweep runs.
        var pending = await device.Client.GetFromJsonAsync<Page<PendingCommand>>("/v2/commands/pending");
        pending!.Items.ShouldBeEmpty();

        using var scope = fixture.Services.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<CommandService>();
        var expired = await commands.ExpireDueAsync();

        expired.ShouldContain(e => e.PublicId == Guid.Parse(issued.Id));

        var final = await user.Client.GetFromJsonAsync<CommandResponse>($"/v2/commands/{issued.Id}");
        final!.Status.ShouldBe(CommandStatuses.Expired);
    }

    [Fact]
    public async Task An_expired_command_cannot_be_acknowledged_as_succeeded_without_being_recorded()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        var issued = await (await user.Client.PostAsJsonAsync("/v2/commands",
            Issue(device.DeviceId, "lock", ttl: 600))).Content.ReadFromJsonAsync<CommandResponse>();

        await device.Client.GetAsync("/v2/commands/pending");

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(AgeCommandSql, new { Id = Guid.Parse(issued!.Id) });

        // An agent reporting success after the TTL should be structurally
        // impossible. It is instrumented anyway, because "impossible" and
        // "unobserved" are different claims (05 §9).
        await device.Client.PostAsJsonAsync($"/v2/commands/{issued.Id}/ack", new AckCommandRequest("ok"));

        var staleEvents = await connection.ExecuteScalarAsync<long>("""
            SELECT count(*) FROM command_events e
              JOIN commands c ON c.id = e.command_id
             WHERE c.public_id = @Id AND e.event = 'stale_execution'
            """, new { Id = Guid.Parse(issued.Id) });

        staleEvents.ShouldBe(1);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(601)]
    public async Task A_ttl_outside_the_documented_range_is_refused(int ttlSeconds)
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        var response = await user.Client.PostAsJsonAsync("/v2/commands",
            Issue(device.DeviceId, "lock", ttl: ttlSeconds));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ApiFixture.ErrorCodeAsync(response)).ShouldBe(ErrorCodes.CommandTtlInvalid);
    }

    // ── step-up on destructive commands (ADR-0011) ───────────────────────────

    [Fact]
    public async Task A_destructive_command_is_refused_without_a_confirmation()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        var response = await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "shutdown"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ApiFixture.ErrorCodeAsync(response)).ShouldBe(ErrorCodes.AuthStepUpRequired);
    }

    [Fact]
    public async Task A_confirmation_lets_exactly_one_destructive_command_through()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        var stepUp = await ApiFixture.StepUpAsync(user);

        var accepted = await user.Client.PostAsJsonAsync("/v2/commands",
            Issue(device.DeviceId, "shutdown", stepUp));
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created);

        var command = await accepted.Content.ReadFromJsonAsync<CommandResponse>();
        command!.RiskTier.ShouldBe(RiskTiers.Destructive);

        // The same confirmation cannot authorise a second one.
        var reused = await user.Client.PostAsJsonAsync("/v2/commands",
            Issue(device.DeviceId, "signout", stepUp));

        reused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ApiFixture.ErrorCodeAsync(reused)).ShouldBe(ErrorCodes.AuthStepUpInvalid);
    }

    [Fact]
    public async Task An_access_token_is_not_a_confirmation_token()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        // Without this check, simply holding a session would satisfy step-up and
        // the control would be decorative.
        var response = await user.Client.PostAsJsonAsync("/v2/commands",
            Issue(device.DeviceId, "restart", user.Tokens.AccessToken));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ApiFixture.ErrorCodeAsync(response)).ShouldBe(ErrorCodes.AuthStepUpInvalid);
    }

    [Fact]
    public async Task Another_users_confirmation_does_not_authorise_my_command()
    {
        var owner = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(owner);
        var stranger = await fixture.RegisterUserAsync();

        var strangerStepUp = await ApiFixture.StepUpAsync(stranger);

        var response = await owner.Client.PostAsJsonAsync("/v2/commands",
            Issue(device.DeviceId, "shutdown", strangerStepUp));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_confirmation_needs_the_right_password()
    {
        var user = await fixture.RegisterUserAsync();

        var challenge = await (await user.Client.PostAsync("/v2/auth/step-up/start", null))
            .Content.ReadFromJsonAsync<StepUpChallengeResponse>();

        var response = await user.Client.PostAsJsonAsync("/v2/auth/step-up/verify",
            new StepUpVerifyRequest(challenge!.ChallengeId, "password", "not-my-password"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ApiFixture.ErrorCodeAsync(response)).ShouldBe(ErrorCodes.AuthStepUpInvalid);
    }

    [Fact]
    public async Task Every_destructive_command_carries_a_recorded_step_up()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        await user.Client.PostAsJsonAsync("/v2/commands",
            Issue(device.DeviceId, "shutdown", await ApiFixture.StepUpAsync(user)));

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        // The database refuses a destructive command with no step-up as well as
        // the service, so this invariant survives a bug in the service.
        var unconfirmed = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM commands WHERE risk_tier = 'destructive' AND step_up_verified_at IS NULL");

        unconfirmed.ShouldBe(0);
    }

    // ── policy and audit ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_command_type_disabled_for_a_device_is_refused()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        await user.Client.PatchAsJsonAsync($"/v2/devices/{device.DeviceId}",
            new UpdateDeviceRequest(null, ["lock", "sleep"]));

        var response = await user.Client.PostAsJsonAsync("/v2/commands",
            Issue(device.DeviceId, "shutdown", await ApiFixture.StepUpAsync(user)));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ApiFixture.ErrorCodeAsync(response)).ShouldBe(ErrorCodes.CommandTypeNotAllowed);

        (await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "lock")))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task An_unknown_command_type_is_refused()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        foreach (var type in new[] { "format", "shutdown; rm -rf /", "exec", string.Empty })
        {
            var response = await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, type));
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task Every_transition_is_audited()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user);

        var issued = await (await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "lock")))
            .Content.ReadFromJsonAsync<CommandResponse>();

        await device.Client.GetAsync("/v2/commands/pending");
        await device.Client.PostAsJsonAsync($"/v2/commands/{issued!.Id}/ack", new AckCommandRequest("ok"));

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var events = (await connection.QueryAsync<string>("""
            SELECT e.event FROM command_events e
              JOIN commands c ON c.id = e.command_id
             WHERE c.public_id = @Id
             ORDER BY e.occurred_at, e.id
            """, new { Id = Guid.Parse(issued.Id) })).ToList();

        // "Who asked for this, when, and did it happen" has to be answerable for
        // a destructive action on someone's computer (ADR-0003).
        events.ShouldBe(["issued", "delivered", "acked"]);
    }

    /// <summary>
    /// A command pushed over the socket is delivered just as much as one that
    /// was polled for, and has to be recorded that way: 01 section 8 measures
    /// the delivery SLO as delivered/issued, and under push-first delivery that
    /// ratio was measuring the fallback path only. See 09 section 9.
    /// </summary>
    [Fact]
    public async Task A_pushed_command_is_recorded_as_delivered_when_the_agent_confirms_it()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user, "Pushed-PC");

        var issued = await (await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "lock")))
            .Content.ReadFromJsonAsync<CommandResponse>();

        // Deliberately no call to /v2/commands/pending: this is the path a
        // healthy agent takes, where the command arrives over the hub.
        issued!.Status.ShouldBe(CommandStatuses.Issued);

        using var scope = fixture.Services.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<CommandService>();

        var caller = await DeviceCallerAsync(device);
        await commands.ConfirmDeliveryAsync(caller, Guid.Parse(issued.Id), RequestContext.System);

        var delivered = await user.Client.GetFromJsonAsync<CommandResponse>($"/v2/commands/{issued.Id}");
        delivered!.Status.ShouldBe(CommandStatuses.Delivered);
        delivered.DeliveredAt.ShouldNotBeNull();

        // Confirming twice is what a reconnecting agent does. It must not write
        // a second audit row or move an already-terminal command.
        await commands.ConfirmDeliveryAsync(caller, Guid.Parse(issued.Id), RequestContext.System);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var events = (await connection.QueryAsync<string>("""
            SELECT e.event
              FROM command_events e
              JOIN commands c ON c.id = e.command_id
             WHERE c.public_id = @Id
             ORDER BY e.occurred_at, e.id
            """, new { Id = Guid.Parse(issued.Id) })).ToList();

        events.ShouldBe(["issued", "delivered"]);
    }

    [Fact]
    public async Task Only_the_device_itself_can_confirm_delivery()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user, "Confirming-PC");
        var other = await fixture.PairDeviceAsync(user, "Bystander-PC");

        var issued = await (await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "lock")))
            .Content.ReadFromJsonAsync<CommandResponse>();

        using var scope = fixture.Services.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<CommandService>();

        // Another of the same owner's PCs holds a valid device token. It still
        // has no business confirming, or learning about, a command addressed
        // elsewhere (03 section 2.1). Silence, not an error that would tell it
        // the command exists.
        await commands.ConfirmDeliveryAsync(await DeviceCallerAsync(other), Guid.Parse(issued!.Id), RequestContext.System);

        var untouched = await user.Client.GetFromJsonAsync<CommandResponse>($"/v2/commands/{issued.Id}");
        untouched!.Status.ShouldBe(CommandStatuses.Issued);

        // And a user token, which carries command:issue but never command:ack.
        var owner = await UserIdsAsync(user);

        var userCaller = new CallerIdentity
        {
            UserId = owner.UserId,
            UserPublicId = owner.UserPublicId,
            ClientKind = "mobile",
            Scopes = [Scopes.CommandIssue],
            TokenId = Guid.NewGuid().ToString(),
        };

        await Should.ThrowAsync<AppException>(() =>
            commands.ConfirmDeliveryAsync(userCaller, Guid.Parse(issued.Id), RequestContext.System));
    }

    /// <summary>
    /// The hub hands <see cref="CommandService"/> a caller built from token
    /// claims. A test that calls the service directly has to build the same
    /// thing, and the internal ids only exist in the database.
    /// </summary>
    private async Task<CallerIdentity> DeviceCallerAsync(TestDevice device)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var ids = await connection.QuerySingleAsync<(long DeviceId, long UserId, Guid UserPublicId)>("""
            SELECT d.id AS "DeviceId", u.id AS "UserId", u.public_id AS "UserPublicId"
              FROM devices d
              JOIN users u ON u.id = d.user_id
             WHERE d.public_id = @Id
            """, new { Id = Guid.Parse(device.DeviceId) });

        return new CallerIdentity
        {
            UserId = ids.UserId,
            UserPublicId = ids.UserPublicId,
            DeviceId = ids.DeviceId,
            DevicePublicId = Guid.Parse(device.DeviceId),
            ClientKind = "desktop",
            Scopes = [Scopes.CommandReceive, Scopes.CommandAck],
            TokenId = Guid.NewGuid().ToString(),
        };
    }

    private async Task<(long UserId, Guid UserPublicId)> UserIdsAsync(TestUser user)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        return await connection.QuerySingleAsync<(long UserId, Guid UserPublicId)>("""
            SELECT id AS "UserId", public_id AS "UserPublicId"
              FROM users
             WHERE username = @Username
            """, new { user.Username });
    }

    /// <summary>
    /// The sweep records the expiry; it does not create it. A command past its
    /// TTL must never be reported as still on its way, however long the worker
    /// has been down — otherwise the phone shows "issued" indefinitely for a
    /// command that will never run, which is v1's "Success" for a row that was
    /// merely written.
    /// </summary>
    [Fact]
    public async Task A_command_past_its_ttl_reads_as_expired_before_the_sweep_runs()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user, "Unswept-PC");

        var issued = await (await user.Client.PostAsJsonAsync("/v2/commands",
            Issue(device.DeviceId, "lock", ttl: 600))).Content.ReadFromJsonAsync<CommandResponse>();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(AgeCommandSql, new { Id = Guid.Parse(issued!.Id) });

        // Deliberately no ExpireDueAsync: this is the state between the TTL
        // passing and the sweep noticing, which is where the lie used to live.
        var stored = await connection.ExecuteScalarAsync<string>(
            "SELECT status FROM commands WHERE public_id = @Id", new { Id = Guid.Parse(issued.Id) });
        stored.ShouldBe(CommandStatuses.Issued);

        var single = await user.Client.GetFromJsonAsync<CommandResponse>($"/v2/commands/{issued.Id}");
        single!.Status.ShouldBe(CommandStatuses.Expired);

        var listed = await user.Client.GetFromJsonAsync<Page<CommandResponse>>("/v2/commands");
        listed!.Items.Single(c => c.Id == issued.Id).Status.ShouldBe(CommandStatuses.Expired);
    }

    [Fact]
    public async Task A_revoked_device_abandons_its_in_flight_commands()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user, "Doomed-PC");

        var issued = await (await user.Client.PostAsJsonAsync("/v2/commands", Issue(device.DeviceId, "lock")))
            .Content.ReadFromJsonAsync<CommandResponse>();

        await user.Client.DeleteAsync($"/v2/devices/{device.DeviceId}");

        var final = await user.Client.GetFromJsonAsync<CommandResponse>($"/v2/commands/{issued!.Id}");
        final!.Status.ShouldBe(CommandStatuses.Cancelled);
    }
}
