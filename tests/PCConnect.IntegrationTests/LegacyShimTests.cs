using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using PCConnect.Core.Domain;
using Shouldly;

namespace PCConnect.IntegrationTests;

/// <summary>
/// Golden-file fidelity for the installed VB.NET and Java clients (04 §5).
///
/// The expectations here were read out of the clients themselves, not out of
/// `api/api_spec.md` — that document describes a gateway that was never
/// deployed. Where the two disagree, the installed client wins, because the
/// installed client is what is running on someone's PC tonight.
/// </summary>
[Collection(ApiCollection.Name)]
public class LegacyShimTests(ApiFixture fixture)
{
    /// <summary>
    /// Puts an account back into the state the import leaves it in and mints the
    /// compatibility key the shim hands back as an "API key".
    /// </summary>
    private async Task<(TestUser User, string ApiKey, HttpClient Client)> LegacyUserAsync()
    {
        var user = await fixture.RegisterUserAsync();
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            UPDATE user_credentials
               SET algo = 'legacy_sha256_unsalted', password_hash = @Hash, must_rehash = true
             WHERE user_id = (SELECT id FROM users WHERE username_normalised = @Username)
            """,
            new { Hash = Normalise.Sha256Hex(user.Password), Username = user.Username.ToLowerInvariant() });

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["loginUsername"] = user.Username,
            ["loginPassword"] = Normalise.Sha256Hex(user.Password),
        });

        var response = await client.PostAsync("/api/login.php", form);
        var apiKey = (await response.Content.ReadAsStringAsync()).Trim();

        client.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", apiKey);
        return (user, apiKey, client);
    }

    [Fact]
    public async Task Login_returns_a_bare_api_key_as_text()
    {
        var (_, apiKey, _) = await LegacyUserAsync();

        // `Login.vb` compares the whole body against a string and then uses it
        // as the key: no JSON, no quotes, no envelope.
        apiKey.ShouldNotBeNullOrWhiteSpace();
        apiKey.ShouldNotStartWith("{");
        apiKey.ShouldNotContain("\"");
    }

    [Fact]
    public async Task Login_failure_returns_the_exact_string_the_vb_client_compares_against()
    {
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var response = await client.PostAsync("/api/login.php", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["loginUsername"] = "nobody",
            ["loginPassword"] = Normalise.Sha256Hex("nothing"),
        }));

        // `Login.vb:57` — `If LoginResponseContent <> "Invalid username or password."`
        (await response.Content.ReadAsStringAsync()).ShouldBe("Invalid username or password.");
    }

    [Fact]
    public async Task Checkinternet_says_yes_not_pong()
    {
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var body = await (await client.GetAsync("/api/pcconnect/checkinternet.php")).Content.ReadAsStringAsync();

        // `PCClient.vb:380` treats anything that is not "yes" or "no" as offline
        // and greys out its indicator. `api_spec.md` documents "Pong" for the
        // never-deployed Node gateway; the installed client wins.
        body.ShouldBe("yes");
    }

    [Fact]
    public async Task Time_returns_a_flat_dictionary_of_strings()
    {
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var body = await (await client.GetAsync("/api/time.php")).Content.ReadAsStringAsync();

        // `PCClient.vb:130` deserialises into Dictionary(Of String, String) and
        // posts TimeJSON("time") straight back.
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("time").GetString().ShouldNotBeNullOrWhiteSpace();
        TimeSpan.TryParse(document.RootElement.GetProperty("time").GetString(), out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Pcnames_returns_the_capitalised_array_the_clients_index_by_name()
    {
        var (_, _, client) = await LegacyUserAsync();

        client.DefaultRequestHeaders.TryAddWithoutValidation("PCName", "Legacy-PC");
        await client.GetAsync("/api/pcclient/addpc.php");

        var body = await (await client.GetAsync("/api/pcconnect/PCNames.php")).Content.ReadAsStringAsync();

        // `MainActivity.java:238` — json.getJSONArray("PCNames"). camelCase
        // would break it, so the shim keeps v1's spelling.
        body.ShouldContain("\"PCNames\"");
        body.ShouldContain("Legacy-PC");

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("PCNames").EnumerateArray()
            .Select(e => e.GetString()).ShouldContain("Legacy-PC");
    }

    [Fact]
    public async Task An_old_client_can_add_a_pc_issue_a_command_and_collect_it()
    {
        var (_, _, client) = await LegacyUserAsync();
        client.DefaultRequestHeaders.TryAddWithoutValidation("PCName", "Study-PC");

        await client.GetAsync("/api/pcclient/addpc.php");

        var exchange = await client.PostAsync("/api/pcconnect/exchange.php",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Request"] = "Shutdown" }));

        (await exchange.Content.ReadAsStringAsync()).ShouldContain("\"message\"");

        var found = await (await client.GetAsync("/api/pcclient/findrequests.php")).Content.ReadAsStringAsync();

        // `PCClient.vb:302` looks the response up in a dictionary keyed by
        // "Shutdown", "Restart", "Signout", "Lock", "Sleep", "Hibernate".
        found.ShouldBe("Shutdown");

        var cleared = await client.GetAsync("/api/pcclient/updaterequest.php");
        cleared.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Once acknowledged there is nothing left to find, so the v1 client does
        // not execute the same command twice.
        (await (await client.GetAsync("/api/pcclient/findrequests.php")).Content.ReadAsStringAsync())
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task A_legacy_command_still_gets_a_ttl_and_an_audit_trail()
    {
        var (user, _, client) = await LegacyUserAsync();
        client.DefaultRequestHeaders.TryAddWithoutValidation("PCName", "Audited-PC");

        await client.GetAsync("/api/pcclient/addpc.php");
        await client.PostAsync("/api/pcconnect/exchange.php",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Request"] = "Sleep" }));

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var command = await connection.QuerySingleAsync<(string Type, string Client, DateTimeOffset ExpiresAt, DateTimeOffset IssuedAt)>("""
            SELECT c.command_type, c.issued_by_client, c.expires_at, c.issued_at
              FROM commands c
              JOIN users u ON u.id = c.issued_by_user_id
             WHERE u.username_normalised = @Username
             ORDER BY c.id DESC LIMIT 1
            """, new { Username = user.Username.ToLowerInvariant() });

        // The shim does not get a weaker command model — a v1 client's shutdown
        // expires like any other, and is attributable.
        command.Type.ShouldBe("sleep");
        command.Client.ShouldBe("legacy_shim");
        (command.ExpiresAt - command.IssuedAt).ShouldBe(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Auto_pairing_a_legacy_device_is_recorded_as_a_security_event()
    {
        var (user, _, client) = await LegacyUserAsync();
        client.DefaultRequestHeaders.TryAddWithoutValidation("PCName", "Auto-Paired-PC");

        await client.GetAsync("/api/pcclient/addpc.php");

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        // This is the one place the old weak model survives (04 §5). It is
        // logged every time so the cost of keeping it is visible.
        var events = await connection.ExecuteScalarAsync<long>("""
            SELECT count(*) FROM security_events e
              JOIN users u ON u.id = e.user_id
             WHERE u.username_normalised = @Username AND e.event = 'legacy.auto_pair'
            """, new { Username = user.Username.ToLowerInvariant() });

        events.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Reminders_come_back_in_the_v1_array_shape()
    {
        var (_, _, client) = await LegacyUserAsync();

        await client.PostAsync("/api/pcconnect/reminder.php", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["date"] = "2026-12-01",
            ["time"] = "09:30",
            ["reminder"] = "Collect the parcel",
        }));

        var body = await (await client.GetAsync("/api/pcclient/listreminders.php")).Content.ReadAsStringAsync();

        // `ReminderData.vb` and `ListRemindersActivity.java:68` both index by
        // "Date", "Time", "Reminder" and "Completed".
        using var document = JsonDocument.Parse(body);
        var first = document.RootElement.EnumerateArray().First();

        first.GetProperty("ID").GetInt64().ShouldBeGreaterThan(0);
        first.GetProperty("Date").GetString().ShouldBe("2026-12-01");
        first.GetProperty("Time").GetString().ShouldBe("09:30:00");
        first.GetProperty("Reminder").GetString().ShouldBe("Collect the parcel");
        first.GetProperty("Completed").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task The_next_due_reminder_has_the_shape_the_reminder_window_parses()
    {
        var (_, _, client) = await LegacyUserAsync();

        await client.PostAsync("/api/pcconnect/reminder.php", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["date"] = "2026-12-02",
            ["time"] = "08:15",
            ["reminder"] = "Ring the dentist",
        }));

        var body = await (await client.GetAsync("/api/pcclient/getreminder.php")).Content.ReadAsStringAsync();

        // `PCClient.vb:343-347` parses id, date, time and reminder out of a flat
        // dictionary and then raises its reminder window. The endpoint is absent
        // from 04 §5's table; it is implemented and the table is corrected.
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("id").GetString().ShouldNotBeNullOrEmpty();
        document.RootElement.GetProperty("date").GetString().ShouldBe("2026-12-02");
        document.RootElement.GetProperty("time").GetString().ShouldBe("08:15:00");
        document.RootElement.GetProperty("reminder").GetString().ShouldBe("Ring the dentist");
    }

    [Fact]
    public async Task Completing_a_reminder_by_its_integer_id_works()
    {
        var (_, _, client) = await LegacyUserAsync();

        await client.PostAsync("/api/pcconnect/reminder.php", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["date"] = "2026-12-03",
            ["time"] = "10:00",
            ["reminder"] = "Water the plants",
        }));

        var listed = await (await client.GetAsync("/api/pcclient/listreminders.php")).Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(listed);
        var id = document.RootElement.EnumerateArray().First().GetProperty("ID").GetInt64();

        var response = await client.PostAsync("/api/pcclient/completereminder.php",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = id.ToString() }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await (await client.GetAsync("/api/pcclient/listreminders.php")).Content.ReadAsStringAsync();
        using var afterDocument = JsonDocument.Parse(after);
        afterDocument.RootElement.EnumerateArray()
            .First(e => e.GetProperty("ID").GetInt64() == id)
            .GetProperty("Completed").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Every_shim_response_announces_its_own_deprecation()
    {
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var response = await client.GetAsync("/api/pcconnect/checkinternet.php");

        response.Headers.TryGetValues("Deprecation", out var deprecation).ShouldBeTrue();
        deprecation!.First().ShouldBe("true");
        response.Headers.TryGetValues("Link", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task An_invalid_api_key_is_refused()
    {
        var client = fixture.CreateClient(ApiFixture.FreshIp());
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", "not-a-real-key");
        client.DefaultRequestHeaders.TryAddWithoutValidation("PCName", "Nope");

        (await client.GetAsync("/api/pcconnect/PCNames.php")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_legacy_key_cannot_reach_the_v2_surface()
    {
        var (_, apiKey, _) = await LegacyUserAsync();

        var client = fixture.CreateClient(ApiFixture.FreshIp());
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        // The compatibility credential is an opaque token in refresh_tokens, not
        // a signed access token: it opens the shim and nothing else.
        (await client.GetAsync("/v2/devices")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_legacy_key_is_visible_and_revocable_as_a_session()
    {
        // An account that has already been upgraded: the shim still mints a
        // compatibility key for it, because an installed client that has not
        // been updated keeps calling these endpoints.
        var user = await fixture.RegisterUserAsync();
        var legacyClient = fixture.CreateClient(ApiFixture.FreshIp());

        var apiKey = (await (await legacyClient.PostAsync("/api/login.php",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["loginUsername"] = user.Username,
                ["loginPassword"] = user.Password,
            }))).Content.ReadAsStringAsync()).Trim();

        legacyClient.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", apiKey);
        (await legacyClient.GetAsync("/api/pcconnect/PCNames.php")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var sessions = await user.Client.GetFromJsonAsync<PCConnect.Core.Contracts.Page<PCConnect.Core.Contracts.SessionResponse>>(
            "/v2/account/sessions");

        // v1's API key was permanent, unscoped and unrevocable (S1-05). The
        // compatibility token is none of those things: it is a session the user
        // can see and end.
        sessions!.Items.ShouldContain(s => s.ClientKind == "legacy");

        var legacySession = sessions.Items.First(s => s.ClientKind == "legacy");
        (await user.Client.DeleteAsync($"/v2/account/sessions/{legacySession.FamilyId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await legacyClient.GetAsync("/api/pcconnect/PCNames.php")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Upgrading_a_legacy_password_revokes_the_compatibility_key_it_minted()
    {
        var (user, _, legacyClient) = await LegacyUserAsync();
        (await legacyClient.GetAsync("/api/pcconnect/PCNames.php")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The user updates their client and signs in with the real password,
        // which upgrades the stored hash to Argon2id.
        var modern = fixture.CreateClient(ApiFixture.FreshIp());
        (await modern.PostAsJsonAsync("/v2/auth/login",
            new PCConnect.Core.Contracts.LoginRequest(user.Username, user.Password, null, "mobile", "8.0.0")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Every credential minted while the account was on the weaker hash dies
        // with it — including the long-lived compatibility key. The old client
        // signs in again and gets a fresh one.
        (await legacyClient.GetAsync("/api/pcconnect/PCNames.php")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
