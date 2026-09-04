using System.Net;
using System.Net.Http.Json;
using Dapper;
using Npgsql;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Core.Domain;
using Shouldly;

namespace PCConnect.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthenticationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Registration_returns_a_token_pair_and_a_profile()
    {
        var user = await fixture.RegisterUserAsync();

        user.Tokens.TokenType.ShouldBe("Bearer");
        user.Tokens.ExpiresInSeconds.ShouldBe(900);
        user.Tokens.User!.Username.ShouldBe(user.Username);
        user.Tokens.Scopes.ShouldContain(Scopes.CommandIssue);
        user.Tokens.Scopes.ShouldNotContain(Scopes.CommandReceive);
    }

    [Fact]
    public async Task Registration_refuses_a_password_that_fails_the_policy()
    {
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var response = await client.PostAsJsonAsync("/v2/auth/register",
            new RegisterRequest("shortpw", "shortpw@example.test", "short"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ApiFixture.ErrorCodeAsync(response)).ShouldBe(ErrorCodes.AuthPasswordPolicy);
    }

    [Fact]
    public async Task Registration_does_not_reveal_which_identifier_already_exists()
    {
        var existing = await fixture.RegisterUserAsync();
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var sameUsername = await client.PostAsJsonAsync("/v2/auth/register",
            new RegisterRequest(existing.Username, "different@example.test", "correct-horse-battery-staple"));

        var sameEmail = await client.PostAsJsonAsync("/v2/auth/register",
            new RegisterRequest($"different{Guid.NewGuid():N}"[..20], existing.Email, "correct-horse-battery-staple"));

        // Identical status and code either way: otherwise registration becomes
        // an account enumeration endpoint.
        sameUsername.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        sameEmail.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ApiFixture.ErrorCodeAsync(sameUsername)).ShouldBe(await ApiFixture.ErrorCodeAsync(sameEmail));
    }

    [Fact]
    public async Task Login_works_by_username_or_email_and_is_case_insensitive()
    {
        var user = await fixture.RegisterUserAsync();
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        foreach (var login in new[] { user.Username, user.Username.ToUpperInvariant(), user.Email, user.Email.ToUpperInvariant() })
        {
            var response = await client.PostAsJsonAsync("/v2/auth/login",
                new LoginRequest(login, user.Password, null, "mobile", "test"));

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_account_are_indistinguishable()
    {
        var user = await fixture.RegisterUserAsync();
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var wrongPassword = await client.PostAsJsonAsync("/v2/auth/login",
            new LoginRequest(user.Username, "definitely-not-the-password", null, "mobile", "test"));

        var unknownAccount = await client.PostAsJsonAsync("/v2/auth/login",
            new LoginRequest($"nobody{Guid.NewGuid():N}", "definitely-not-the-password", null, "mobile", "test"));

        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknownAccount.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ApiFixture.ErrorCodeAsync(wrongPassword)).ShouldBe(await ApiFixture.ErrorCodeAsync(unknownAccount));
    }

    [Fact]
    public async Task The_server_stores_argon2id_and_never_the_password()
    {
        var user = await fixture.RegisterUserAsync();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var credential = await connection.QuerySingleAsync<(string Algo, string PasswordHash)>("""
            SELECT c.algo, c.password_hash
              FROM user_credentials c
              JOIN users u ON u.id = c.user_id
             WHERE u.username_normalised = @Username
            """, new { Username = user.Username.ToLowerInvariant() });

        credential.Algo.ShouldBe(PasswordAlgorithms.Argon2id);
        credential.PasswordHash.ShouldStartWith("$argon2id$v=19$m=19456,t=2,p=1$");
        credential.PasswordHash.ShouldNotContain(user.Password);

        // And emphatically not the unsalted SHA-256 the v1 clients computed,
        // which was itself a working credential (S1-03).
        credential.PasswordHash.ShouldNotContain(Normalise.Sha256Hex(user.Password));
    }

    // ── refresh rotation and reuse detection ─────────────────────────────────

    [Fact]
    public async Task Refresh_rotates_the_token()
    {
        var user = await fixture.RegisterUserAsync();
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var rotated = await (await client.PostAsJsonAsync("/v2/auth/refresh",
            new RefreshRequest(user.Tokens.RefreshToken))).Content.ReadFromJsonAsync<TokenPairResponse>();

        rotated!.RefreshToken.ShouldNotBe(user.Tokens.RefreshToken);
        rotated.AccessToken.ShouldNotBe(user.Tokens.AccessToken);
    }

    [Fact]
    public async Task Presenting_a_rotated_token_revokes_the_whole_family()
    {
        var user = await fixture.RegisterUserAsync();
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var rotated = await (await client.PostAsJsonAsync("/v2/auth/refresh",
            new RefreshRequest(user.Tokens.RefreshToken))).Content.ReadFromJsonAsync<TokenPairResponse>();

        // The old link is presented again: a copy leaked and both parties are
        // now walking the chain (03 §2.4).
        var reuse = await client.PostAsJsonAsync("/v2/auth/refresh", new RefreshRequest(user.Tokens.RefreshToken));
        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ApiFixture.ErrorCodeAsync(reuse)).ShouldBe(ErrorCodes.AuthTokenReused);

        // The whole family dies, including the token the legitimate holder has.
        // This is the property that makes silent token theft self-limiting — and
        // it has to survive the failure it reports, so the revocation commits
        // even though the request 401s.
        var afterReuse = await client.PostAsJsonAsync("/v2/auth/refresh", new RefreshRequest(rotated!.RefreshToken));
        afterReuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var live = await connection.ExecuteScalarAsync<long>("""
            SELECT count(*) FROM refresh_tokens r
              JOIN users u ON u.id = r.user_id
             WHERE u.username_normalised = @Username AND r.revoked_at IS NULL
            """, new { Username = user.Username.ToLowerInvariant() });

        live.ShouldBe(0);

        var reuseRecorded = await connection.ExecuteScalarAsync<long>("""
            SELECT count(*) FROM security_events e
              JOIN users u ON u.id = e.user_id
             WHERE u.username_normalised = @Username AND e.event = 'token.reuse_detected'
            """, new { Username = user.Username.ToLowerInvariant() });

        // At least one: every subsequent presentation of any link in a revoked
        // family is itself a reuse, and each one is recorded.
        reuseRecorded.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Logout_revokes_only_the_presented_session()
    {
        var user = await fixture.RegisterUserAsync();
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var second = await (await client.PostAsJsonAsync("/v2/auth/login",
            new LoginRequest(user.Username, user.Password, null, "web", "test")))
            .Content.ReadFromJsonAsync<TokenPairResponse>();

        await client.PostAsJsonAsync("/v2/auth/logout", new LogoutRequest(user.Tokens.RefreshToken));

        (await client.PostAsJsonAsync("/v2/auth/refresh", new RefreshRequest(user.Tokens.RefreshToken)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await client.PostAsJsonAsync("/v2/auth/refresh", new RefreshRequest(second!.RefreshToken)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Changing_the_password_ends_every_session()
    {
        var user = await fixture.RegisterUserAsync();
        const string newPassword = "an-entirely-different-passphrase";

        var change = await user.Client.PostAsJsonAsync("/v2/auth/password/change",
            new ChangePasswordRequest(user.Password, newPassword));

        change.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var client = fixture.CreateClient(ApiFixture.FreshIp());

        (await client.PostAsJsonAsync("/v2/auth/refresh", new RefreshRequest(user.Tokens.RefreshToken)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await client.PostAsJsonAsync("/v2/auth/login",
            new LoginRequest(user.Username, newPassword, null, "mobile", "test")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.PostAsJsonAsync("/v2/auth/login",
            new LoginRequest(user.Username, user.Password, null, "mobile", "test")))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Changing_the_password_applies_the_same_policy_as_registration()
    {
        var user = await fixture.RegisterUserAsync();

        // S1-10 was exactly this: the policy existed on signup and nowhere else.
        var response = await user.Client.PostAsJsonAsync("/v2/auth/password/change",
            new ChangePasswordRequest(user.Password, "short"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ApiFixture.ErrorCodeAsync(response)).ShouldBe(ErrorCodes.AuthPasswordPolicy);
    }

    [Fact]
    public async Task Password_reset_is_the_same_answer_whether_or_not_the_account_exists()
    {
        var user = await fixture.RegisterUserAsync();
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        var known = await client.PostAsJsonAsync("/v2/auth/password/forgot", new ForgotPasswordRequest(user.Email));
        var unknown = await client.PostAsJsonAsync("/v2/auth/password/forgot",
            new ForgotPasswordRequest($"nobody{Guid.NewGuid():N}@example.test"));

        known.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    // ── the legacy password path (02 §6) ─────────────────────────────────────

    [Fact]
    public async Task A_legacy_account_authenticates_with_its_pre_hash_and_is_upgraded_by_a_real_password()
    {
        var user = await fixture.RegisterUserAsync();
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        // Put the account back the way the import leaves it: an unsalted
        // client-side SHA-256 that cannot be reversed.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            UPDATE user_credentials
               SET algo = 'legacy_sha256_unsalted', password_hash = @Hash, must_rehash = true
             WHERE user_id = (SELECT id FROM users WHERE username_normalised = @Username)
            """,
            new { Hash = Normalise.Sha256Hex(user.Password), Username = user.Username.ToLowerInvariant() });

        // An installed client sends the pre-hash. It authenticates...
        var legacyLogin = await client.PostAsJsonAsync("/v2/auth/login",
            new LoginRequest(user.Username, null, Normalise.Sha256Hex(user.Password), "legacy", "v1"));

        legacyLogin.StatusCode.ShouldBe(HttpStatusCode.OK);

        // ...but cannot upgrade the stored hash, because on that path the server
        // never sees the real password.
        var algoAfterLegacy = await connection.ExecuteScalarAsync<string>("""
            SELECT algo FROM user_credentials
             WHERE user_id = (SELECT id FROM users WHERE username_normalised = @Username)
            """, new { Username = user.Username.ToLowerInvariant() });

        algoAfterLegacy.ShouldBe(PasswordAlgorithms.LegacySha256Unsalted);

        // An updated client sends the plaintext, and the account is upgraded.
        var modernLogin = await client.PostAsJsonAsync("/v2/auth/login",
            new LoginRequest(user.Username, user.Password, null, "mobile", "8.0.0"));

        modernLogin.StatusCode.ShouldBe(HttpStatusCode.OK);

        var algoAfterUpgrade = await connection.ExecuteScalarAsync<string>("""
            SELECT algo FROM user_credentials
             WHERE user_id = (SELECT id FROM users WHERE username_normalised = @Username)
            """, new { Username = user.Username.ToLowerInvariant() });

        algoAfterUpgrade.ShouldBe(PasswordAlgorithms.Argon2id);

        var upgradeRecorded = await connection.ExecuteScalarAsync<long>("""
            SELECT count(*) FROM security_events e
              JOIN users u ON u.id = e.user_id
             WHERE u.username_normalised = @Username AND e.event = 'password.upgraded'
            """, new { Username = user.Username.ToLowerInvariant() });

        upgradeRecorded.ShouldBe(1);
    }

    [Fact]
    public async Task An_upgraded_account_no_longer_accepts_the_pre_hash()
    {
        var user = await fixture.RegisterUserAsync();
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        // The account is Argon2id from registration. The stored hash is no
        // longer a working credential, which is the whole point of the change.
        var response = await client.PostAsJsonAsync("/v2/auth/login",
            new LoginRequest(user.Username, null, Normalise.Sha256Hex(user.Password), "legacy", "v1"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ── lockout and rate limiting (03 §6) ────────────────────────────────────

    [Fact]
    public async Task Repeated_failures_from_one_address_are_rate_limited()
    {
        var user = await fixture.RegisterUserAsync();
        var ip = ApiFixture.FreshIp();
        var client = fixture.CreateClient(ip);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
        {
            var response = await client.PostAsJsonAsync("/v2/auth/login",
                new LoginRequest(user.Username, $"wrong-{i}", null, "mobile", "test"));
            statuses.Add(response.StatusCode);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter.ShouldNotBeNull();
                break;
            }
        }

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Sessions_are_listed_so_a_user_can_end_one_they_do_not_recognise()
    {
        var user = await fixture.RegisterUserAsync();
        var client = fixture.CreateClient(ApiFixture.FreshIp());

        await client.PostAsJsonAsync("/v2/auth/login", new LoginRequest(user.Username, user.Password, null, "web", "1.0"));

        var sessions = await user.Client.GetFromJsonAsync<Page<SessionResponse>>("/v2/account/sessions");

        sessions!.Items.Count.ShouldBeGreaterThanOrEqualTo(2);
        sessions.Items.ShouldContain(s => s.ClientKind == "web");

        var other = sessions.Items.First(s => s.ClientKind == "web");
        (await user.Client.DeleteAsync($"/v2/account/sessions/{other.FamilyId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
