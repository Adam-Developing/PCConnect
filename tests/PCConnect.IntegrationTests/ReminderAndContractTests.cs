using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Infrastructure.Contexts.Reminders;
using Shouldly;

namespace PCConnect.IntegrationTests;

[Collection(ApiCollection.Name)]
public class ReminderTests(ApiFixture fixture)
{
    [Fact]
    public async Task Reminder_text_is_encrypted_at_rest_and_readable_only_through_the_api()
    {
        var user = await fixture.RegisterUserAsync();
        const string body = "Cardiology appointment, 3pm";

        var created = await (await user.Client.PostAsJsonAsync("/v2/reminders",
            new CreateReminderRequest(body, DateTimeOffset.UtcNow.AddDays(1), "Europe/London")))
            .Content.ReadFromJsonAsync<ReminderResponse>();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var stored = await connection.QuerySingleAsync<(byte[] Ciphertext, string DekId)>(
            "SELECT body_ciphertext, body_dek_id FROM reminders WHERE public_id = @Id",
            new { Id = Guid.Parse(created!.Id) });

        // A database dump yields no plaintext, because the KEK is not in the
        // database (03 §4.1, mitigates T5).
        System.Text.Encoding.UTF8.GetString(stored.Ciphertext).ShouldNotContain("Cardiology");
        stored.Ciphertext.Length.ShouldBeGreaterThan(28);   // 12B nonce + 16B tag + content
        stored.DekId.ShouldNotBeNullOrEmpty();

        var readBack = await user.Client.GetFromJsonAsync<ReminderResponse>($"/v2/reminders/{created.Id}");
        readBack!.Body.ShouldBe(body);
    }

    [Fact]
    public async Task Each_user_gets_their_own_data_key()
    {
        var first = await fixture.RegisterUserAsync();
        var second = await fixture.RegisterUserAsync();

        await first.Client.PostAsJsonAsync("/v2/reminders",
            new CreateReminderRequest("first", DateTimeOffset.UtcNow.AddDays(1)));
        await second.Client.PostAsJsonAsync("/v2/reminders",
            new CreateReminderRequest("second", DateTimeOffset.UtcNow.AddDays(1)));

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var keys = (await connection.QueryAsync<byte[]>("""
            SELECT dek_wrapped FROM users
             WHERE username_normalised IN (@First, @Second) AND dek_wrapped IS NOT NULL
            """,
            new { First = first.Username.ToLowerInvariant(), Second = second.Username.ToLowerInvariant() })).ToList();

        keys.Count.ShouldBe(2);
        // One compromised DEK exposes one user, not everybody (ADR-0004).
        Convert.ToBase64String(keys[0]).ShouldNotBe(Convert.ToBase64String(keys[1]));
    }

    [Fact]
    public async Task A_reminder_keeps_the_local_time_and_the_zone_it_was_written_in()
    {
        var user = await fixture.RegisterUserAsync();

        // 09:00 in Kolkata is 03:30 UTC. v1 stored a naive wall clock and fired
        // it at UK time for everybody (S2-07).
        var kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var local = new DateTime(2026, 12, 1, 9, 0, 0, DateTimeKind.Unspecified);
        var instant = new DateTimeOffset(local, kolkata.GetUtcOffset(local)).ToUniversalTime();

        var created = await (await user.Client.PostAsJsonAsync("/v2/reminders",
            new CreateReminderRequest("Morning tablets", instant, "Asia/Kolkata")))
            .Content.ReadFromJsonAsync<ReminderResponse>();

        created!.Timezone.ShouldBe("Asia/Kolkata");
        created.DueLocalTime.ShouldBe("09:00");
        created.DueAt.UtcDateTime.Hour.ShouldBe(3);
        created.DueAt.UtcDateTime.Minute.ShouldBe(30);
    }

    [Fact]
    public async Task A_recurring_reminder_materialises_a_horizon_of_occurrences()
    {
        var user = await fixture.RegisterUserAsync();

        var created = await (await user.Client.PostAsJsonAsync("/v2/reminders",
            new CreateReminderRequest("Weekly standup", DateTimeOffset.UtcNow.AddDays(1),
                "Europe/London", "FREQ=WEEKLY;BYDAY=MO")))
            .Content.ReadFromJsonAsync<ReminderResponse>();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var occurrences = await connection.ExecuteScalarAsync<long>("""
            SELECT count(*) FROM reminder_occurrences o
              JOIN reminders r ON r.id = o.reminder_id
             WHERE r.public_id = @Id
            """, new { Id = Guid.Parse(created!.Id) });

        // Expanding an RRULE on every scheduler tick does not scale; a rolling
        // horizon does (02 §3).
        occurrences.ShouldBeGreaterThan(5);
    }

    [Fact]
    public async Task Completing_one_occurrence_leaves_the_series_running()
    {
        var user = await fixture.RegisterUserAsync();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        var created = await (await user.Client.PostAsJsonAsync("/v2/reminders",
            new CreateReminderRequest("Daily walk", start, "Europe/London", "FREQ=DAILY")))
            .Content.ReadFromJsonAsync<ReminderResponse>();

        var updated = await (await user.Client.PostAsJsonAsync($"/v2/reminders/{created!.Id}/complete",
            new CompleteReminderRequest(true, start)))
            .Content.ReadFromJsonAsync<ReminderResponse>();

        updated!.IsCompleted.ShouldBeFalse();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var completed = await connection.ExecuteScalarAsync<long>("""
            SELECT count(*) FROM reminder_occurrences o
              JOIN reminders r ON r.id = o.reminder_id
             WHERE r.public_id = @Id AND o.status = 'completed'
            """, new { Id = Guid.Parse(created.Id) });

        completed.ShouldBe(1);
    }

    [Fact]
    public async Task The_scheduler_delivers_a_due_reminder_once()
    {
        var user = await fixture.RegisterUserAsync();

        var created = await (await user.Client.PostAsJsonAsync("/v2/reminders",
            new CreateReminderRequest("Take the bins out", DateTimeOffset.UtcNow.AddSeconds(5))))
            .Content.ReadFromJsonAsync<ReminderResponse>();

        using var scope = fixture.Services.CreateScope();
        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();

        var due = await reminders.ClaimDueAsync(TimeSpan.FromMinutes(1));
        due.ShouldContain(d => d.PublicId == Guid.Parse(created!.Id) && d.Body == "Take the bins out");

        // Claimed and marked, so a worker restart does not notify twice.
        var again = await reminders.ClaimDueAsync(TimeSpan.FromMinutes(1));
        again.ShouldNotContain(d => d.PublicId == Guid.Parse(created!.Id));
    }

    [Fact]
    public async Task A_reminder_body_is_bounded()
    {
        var user = await fixture.RegisterUserAsync();

        var response = await user.Client.PostAsJsonAsync("/v2/reminders",
            new CreateReminderRequest(new string('x', 2001), DateTimeOffset.UtcNow.AddDays(1)));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ApiFixture.ErrorCodeAsync(response)).ShouldBe(ErrorCodes.ReminderBodyTooLong);
    }
}

[Collection(ApiCollection.Name)]
public class AccountLifecycleTests(ApiFixture fixture)
{
    [Fact]
    public async Task An_export_contains_everything_the_account_holds()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user, "Export-PC");

        await user.Client.PostAsJsonAsync("/v2/reminders",
            new CreateReminderRequest("Exported reminder", DateTimeOffset.UtcNow.AddDays(1)));
        await user.Client.PostAsJsonAsync("/v2/commands",
            new IssueCommandRequest(Guid.CreateVersion7().ToString(), device.DeviceId, "lock"));

        var export = await user.Client.GetFromJsonAsync<AccountExport>("/v2/account/export");

        export!.Profile.Username.ShouldBe(user.Username);
        export.Devices.ShouldContain(d => d.DisplayName == "Export-PC");
        export.Reminders.ShouldContain(r => r.Body == "Exported reminder");
        export.Commands.ShouldNotBeEmpty();
        export.Sessions.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Deleting_an_account_ends_every_session_and_revokes_every_device()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user, "Deleted-PC");

        (await user.Client.DeleteAsync("/v2/account")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Soft delete now, hard delete after 30 days — but the account is
        // unusable from this moment (03 §8).
        (await user.Client.GetAsync("/v2/devices")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await device.Client.GetAsync("/v2/commands/pending")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var client = fixture.CreateClient(ApiFixture.FreshIp());
        (await client.PostAsJsonAsync("/v2/auth/login",
            new LoginRequest(user.Username, user.Password, null, "mobile", "test")))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_hard_delete_leaves_nothing_behind()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user, "Purged-PC");

        await user.Client.PostAsJsonAsync("/v2/reminders",
            new CreateReminderRequest("Will be purged", DateTimeOffset.UtcNow.AddDays(1)));
        await user.Client.PostAsJsonAsync("/v2/commands",
            new IssueCommandRequest(Guid.CreateVersion7().ToString(), device.DeviceId, "lock"));

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var userId = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM users WHERE username_normalised = @Username",
            new { Username = user.Username.ToLowerInvariant() });

        await user.Client.DeleteAsync("/v2/account");

        // The retention job's hard delete, run directly rather than waited for.
        await connection.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = userId });

        // GDPR erasure has to be complete, and 03 §8 says it is asserted by a
        // test rather than assumed from the cascade declarations.
        foreach (var table in new[] { "devices", "reminders", "refresh_tokens", "user_credentials", "idempotency_keys" })
        {
            var remaining = await connection.ExecuteScalarAsync<long>(
                $"SELECT count(*) FROM {table} WHERE user_id = @Id", new { Id = userId });
            remaining.ShouldBe(0, $"{table} still holds rows for the deleted account");
        }

        var commands = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM commands WHERE issued_by_user_id = @Id", new { Id = userId });
        commands.ShouldBe(0);

        // security_events keeps its row with a null user id: the audit trail of
        // "an account was deleted" must survive the account.
        var events = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM security_events WHERE user_id = @Id", new { Id = userId });
        events.ShouldBe(0);
    }
}

/// <summary>
/// The contract is generated from the same records the handlers return, and the
/// document is what every non-.NET client is generated from (C-5, 04 §1).
/// </summary>
[Collection(ApiCollection.Name)]
public class ContractTests(ApiFixture fixture)
{
    [Fact]
    public async Task The_openapi_document_is_served_and_describes_the_v2_surface()
    {
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        foreach (var path in new[]
        {
            "/v2/auth/login", "/v2/auth/refresh", "/v2/auth/step-up/verify",
            "/v2/devices", "/v2/devices/pair/start", "/v2/devices/pair/claim", "/v2/devices/token",
            "/v2/commands", "/v2/commands/pending", "/v2/reminders", "/v2/account/profile",
            "/v2/meta/discovery",
        })
        {
            paths.TryGetProperty(path, out _).ShouldBeTrue($"{path} is missing from the OpenAPI document");
        }
    }

    [Fact]
    public async Task The_legacy_shim_is_not_part_of_the_documented_contract()
    {
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        using var document = JsonDocument.Parse(
            await (await client.GetAsync("/openapi/v1.json")).Content.ReadAsStringAsync());

        // The shim is a compatibility surface with an expiry date, not an API
        // anyone should generate a new client from (04 §5).
        foreach (var property in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            property.Name.ShouldNotStartWith("/api/");
            property.Name.ShouldNotStartWith("/legacy/");
        }
    }

    [Fact]
    public async Task Every_error_carries_the_one_envelope()
    {
        var user = await fixture.RegisterUserAsync();
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var responses = new[]
        {
            await client.GetAsync("/v2/devices"),                                   // 401
            await user.Client.GetAsync($"/v2/devices/{Guid.CreateVersion7()}"),      // 404
            await user.Client.PostAsJsonAsync("/v2/commands",
                new IssueCommandRequest("not-a-uuid", Guid.CreateVersion7().ToString(), "lock")), // 400
        };

        foreach (var response in responses)
        {
            var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();

            envelope.ShouldNotBeNull();
            envelope.Error.Code.ShouldNotBeNullOrWhiteSpace();
            envelope.Error.Message.ShouldNotBeNullOrWhiteSpace();
            envelope.Error.RequestId.ShouldNotBeNullOrWhiteSpace();

            // The id in the body is the id in the header, so a user quoting one
            // is enough to find the request (01 §6.5).
            response.Headers.GetValues("X-Request-Id").First().ShouldBe(envelope.Error.RequestId);
        }
    }

    [Fact]
    public async Task Responses_carry_the_security_headers()
    {
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var response = await client.GetAsync("/v2/meta/discovery");

        response.Headers.GetValues("X-Content-Type-Options").First().ShouldBe("nosniff");
        response.Headers.GetValues("Referrer-Policy").First().ShouldBe("no-referrer");
        response.Headers.GetValues("Content-Security-Policy").First().ShouldContain("default-src 'none'");
    }

    [Fact]
    public async Task An_unknown_field_in_a_request_body_is_refused_rather_than_ignored()
    {
        var user = await fixture.RegisterUserAsync();

        var response = await user.Client.PostAsync("/v2/reminders", new StringContent(
            """{"body":"test","dueAt":"2026-12-01T09:00:00Z","tyop":"typo"}""",
            System.Text.Encoding.UTF8, "application/json"));

        // A typo in a field name is a 400, not a silently discarded value.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Discovery_tells_a_client_whether_it_is_still_supported()
    {
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var discovery = await client.GetFromJsonAsync<DiscoveryResponse>("/v2/meta/discovery");

        discovery!.MinimumSupportedClient.ShouldContainKey("desktop");
        discovery.MinimumSupportedClient.ShouldContainKey("mobile");
        discovery.Capabilities.ShouldContain("commands.ttl");
        discovery.Capabilities.ShouldContain("commands.stepup");
        discovery.RealtimeUrl.ShouldNotBeNullOrWhiteSpace();

        // This is the lever that ends the legacy era (04 §2).
        PCConnect.Client.PcConnectClient.IsBelowMinimum(discovery, "desktop", "4.5.0").ShouldBeTrue();
        PCConnect.Client.PcConnectClient.IsBelowMinimum(discovery, "desktop", "5.0.0").ShouldBeFalse();
    }

    [Fact]
    public async Task Readyz_reports_what_it_actually_checked()
    {
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var health = await client.GetFromJsonAsync<HealthResponse>("/readyz");

        health!.Status.ShouldBe("ok");
        health.Checks["database"].ShouldBe("ok");
        health.Checks["cache"].ShouldBe("ok");
    }

    [Fact]
    public async Task Metrics_are_exposed_for_prometheus()
    {
        var user = await fixture.RegisterUserAsync();
        var device = await fixture.PairDeviceAsync(user, "Metrics-PC");
        await user.Client.PostAsJsonAsync("/v2/commands",
            new IssueCommandRequest(Guid.CreateVersion7().ToString(), device.DeviceId, "lock"));

        var client = fixture.CreateClient(ApiFixture.FreshIp());
        var body = await (await client.GetAsync("/metrics")).Content.ReadAsStringAsync();

        // The counters the alerts in 08 are written against.
        body.ShouldContain("pcconnect_commands_issued_total");
    }
}
