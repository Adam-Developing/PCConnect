extern alias migration;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PCConnect.Contracts.V2;
using PCConnect.Infrastructure.Database;
using Testcontainers.PostgreSql;
using Xunit;

namespace PCConnect.IntegrationTests;

public sealed class PostgreSqlApiFixture : IAsyncLifetime
{
    private PostgreSqlContainer? postgres;
    private TestApiFactory? factory;
    public static bool Enabled => Environment.GetEnvironmentVariable("PCCONNECT_TESTCONTAINERS") == "1";
    public HttpClient Client => factory!.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
    public string ConnectionString => postgres!.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        if (!Enabled) return;
        postgres = new PostgreSqlBuilder("postgres:18.3-alpine3.22@sha256:af27ebd3413839ccfdcdf88e91ca76ad82117e52192067b76de9bb5be7ea6e04").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<PCConnectDbContext>().UseNpgsql(ConnectionString).Options;
        await using var database = new PCConnectDbContext(options);
        await database.Database.MigrateAsync();
        factory = new TestApiFactory(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (factory is not null) await factory.DisposeAsync();
        if (postgres is not null) await postgres.DisposeAsync();
    }
}

public sealed class TestApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    private readonly string statePath = Path.Combine(Path.GetTempPath(), "pcconnect-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            ["Security:TokenHashingKey"] = key,
            ["Security:LegacyCredentialHashingKey"] = Convert.ToBase64String(SHA256.HashData("legacy-test-key"u8.ToArray())),
            ["Security:ActiveReminderKeyId"] = "test-v1",
            ["Security:ReminderWrappingKeys:test-v1"] = key,
            ["Security:ActiveEmailKeyId"] = "test-v1",
            ["Security:EmailEncryptionKeys:test-v1"] = key,
            ["Security:DeletionTombstoneKey"] = key,
            ["Security:ExportEncryptionKey"] = key,
            ["Security:WebAuthnRpId"] = "localhost",
            ["Security:WebAuthnOrigins:0"] = "https://localhost",
            ["Http:AllowedOrigins:0"] = "https://localhost",
            ["Http:RateLimits:AuthenticationPermitLimit"] = "1000",
            ["Realtime:ValkeyConnection"] = string.Empty,
            ["Exports:Directory"] = Path.Combine(statePath, "exports"),
            ["DataProtection:KeyRingPath"] = Path.Combine(statePath, "keys")
        };
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.Sources.Clear();
            configuration.AddInMemoryCollection(settings);
        });
        foreach (var setting in settings.Where(setting => setting.Value is not null))
            builder.UseSetting(setting.Key, setting.Value);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(statePath)) Directory.Delete(statePath, recursive: true);
    }
}

public sealed class CanonicalSchemaTests(PostgreSqlApiFixture fixture) : IClassFixture<PostgreSqlApiFixture>
{
    [Fact]
    public async Task EmptyPostgreSql18ReachesCanonicalSchemaWithSecurityConstraints()
    {
        if (!PostgreSqlApiFixture.Enabled) return;
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT to_regclass('public.commands') IS NOT NULL,
              to_regclass('public.authentication_throttles') IS NOT NULL,
              to_regclass('public.device_sid_candidates') IS NOT NULL,
              EXISTS(SELECT 1 FROM pg_extension WHERE extname='pgcrypto'),
              EXISTS(SELECT 1 FROM pg_constraint WHERE conname='commands_exactly_one_actor'),
              EXISTS(SELECT 1 FROM pg_indexes WHERE indexname='commands_actor_legacy_idempotency_unique');
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        for (var index = 0; index < 6; index++) Assert.True(reader.GetBoolean(index));
    }

    [Fact]
    public async Task LegacyShaLoginAtomicallyUpgradesToArgon2id()
    {
        if (!PostgreSqlApiFixture.Enabled) return;
        var userId = Guid.CreateVersion7();
        var suffix = Guid.NewGuid().ToString("N");
        const string password = "correct horse battery staple";
        var legacy = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var seed = new NpgsqlCommand("""
                INSERT INTO users(id,username,email,display_name,timezone,created_at,updated_at) VALUES(@id,@username,@email,'Legacy','Europe/London',now(),now());
                INSERT INTO password_credentials(user_id,legacy_sha256,changed_at) VALUES(@id,@legacy,now());
                """, connection);
            seed.Parameters.AddWithValue("id", userId);
            seed.Parameters.AddWithValue("username", "legacy-" + suffix);
            seed.Parameters.AddWithValue("email", suffix + "@example.test");
            seed.Parameters.AddWithValue("legacy", legacy);
            await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var client = fixture.Client;
        using var response = await client.PostAsJsonAsync("/api/v2/auth/password/login", new PasswordLoginRequest(
            "legacy-" + suffix, password, new(PlatformType.Android, "integration", "1")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verify = new NpgsqlConnection(fixture.ConnectionString);
        await verify.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT password_hash,legacy_sha256 FROM password_credentials WHERE user_id=@id", verify);
        command.Parameters.AddWithValue("id", userId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", reader.GetString(0), StringComparison.Ordinal);
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public async Task LegacyUpgradeRollsBackWhenSessionCreationFails()
    {
        if (!PostgreSqlApiFixture.Enabled) return;
        var userId = Guid.CreateVersion7();
        var suffix = Guid.NewGuid().ToString("N");
        const string password = "rollback legacy password";
        var legacy = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO users(id,username,email,display_name,timezone,created_at,updated_at) VALUES(@id,@username,@email,'Rollback','Europe/London',now(),now());
            INSERT INTO password_credentials(user_id,legacy_sha256,changed_at) VALUES(@id,@legacy,now());
            CREATE TABLE IF NOT EXISTS test_rejected_sessions(user_id uuid PRIMARY KEY);
            INSERT INTO test_rejected_sessions(user_id) VALUES(@id);
            CREATE OR REPLACE FUNCTION reject_test_session() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF EXISTS(SELECT 1 FROM test_rejected_sessions WHERE user_id=NEW.user_id) THEN RAISE EXCEPTION 'synthetic session failure'; END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER reject_test_session BEFORE INSERT ON sessions FOR EACH ROW EXECUTE FUNCTION reject_test_session();
            """, connection))
        {
            seed.Parameters.AddWithValue("id", userId);
            seed.Parameters.AddWithValue("username", "rollback-" + suffix);
            seed.Parameters.AddWithValue("email", suffix + "@rollback.example.test");
            seed.Parameters.AddWithValue("legacy", legacy);
            await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            using var client = fixture.Client;
            using var response = await client.PostAsJsonAsync("/api/v2/auth/password/login", new PasswordLoginRequest(
                "rollback-" + suffix, password, new(PlatformType.Android, "integration", "1")), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var failure = await response.Content.ReadFromJsonAsync<Problem>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
            Assert.Equal("internal_error", failure.Code);
            Assert.Null(failure.Detail);

            await using var verify = new NpgsqlCommand("SELECT password_hash,legacy_sha256 FROM password_credentials WHERE user_id=@id", connection);
            verify.Parameters.AddWithValue("id", userId);
            await using var reader = await verify.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.True(reader.IsDBNull(0));
            Assert.Equal(legacy, reader.GetString(1));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("DROP TRIGGER IF EXISTS reject_test_session ON sessions; DROP FUNCTION IF EXISTS reject_test_session(); DROP TABLE IF EXISTS test_rejected_sessions;", connection);
            await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task WrongDisabledAndResetRequiredCredentialsDoNotMintSessions()
    {
        if (!PostgreSqlApiFixture.Enabled) return;
        using var client = fixture.Client;
        var suffix = Guid.NewGuid().ToString("N");
        var login = "state-" + suffix;
        const string password = "Correct horse battery staple 42!";
        using var register = await client.PostAsJsonAsync("/api/v2/auth/register", new RegistrationRequest(
            login, suffix + "@state.example.test", "State", password, "Europe/London", false,
            new(PlatformType.Android, "integration", "1")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);

        using var wrong = await client.PostAsJsonAsync("/api/v2/auth/password/login", new PasswordLoginRequest(
            login, "Wrong password value 42!", new(PlatformType.Android, "integration", "1")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        await SetAccountStateAsync(fixture.ConnectionString, login, "disabled");
        using var disabled = await client.PostAsJsonAsync("/api/v2/auth/password/login", new PasswordLoginRequest(
            login, password, new(PlatformType.Android, "integration", "1")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);

        await SetAccountStateAsync(fixture.ConnectionString, login, "reset_required");
        using var reset = await client.PostAsJsonAsync("/api/v2/auth/password/login", new PasswordLoginRequest(
            login, password, new(PlatformType.Android, "integration", "1")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, reset.StatusCode);
        var problem = await reset.Content.ReadFromJsonAsync<Problem>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
        Assert.Equal("reset_required", problem.Code);
    }

    [Fact]
    public async Task UnknownRequestMembersAreRejectedAsSanitizedProblemDetails()
    {
        if (!PostgreSqlApiFixture.Enabled) return;
        using var client = fixture.Client;
        using var content = new StringContent("""
            {"login":"nobody","password":"not-a-real-password","client":{"platform":"android","name":"test","version":"1"},"unexpected":"denied"}
            """, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/v2/auth/password/login", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Problem>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
        Assert.Equal("validation_failed", problem.Code);
        Assert.Null(problem.Detail);
        Assert.False(string.IsNullOrWhiteSpace(problem.CorrelationId));
    }

    [Fact]
    public async Task RefreshReuseRevokesTheCredentialFamily()
    {
        if (!PostgreSqlApiFixture.Enabled) return;
        using var client = fixture.Client;
        var pair = await RegisterAndLoginAsync(client, "rotation");
        using var first = await client.PostAsJsonAsync("/api/v2/auth/refresh", new RefreshRequest(pair.RefreshToken), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = await first.Content.ReadFromJsonAsync<TokenPair>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
        using var reuse = await client.PostAsJsonAsync("/api/v2/auth/refresh", new RefreshRequest(pair.RefreshToken), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, reuse.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.AccessToken);
        using var rejected = await client.GetAsync("/api/v2/devices", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        var rejectedProblem = await rejected.Content.ReadFromJsonAsync<Problem>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
        Assert.Equal("authentication_required", rejectedProblem.Code);
    }

    [Fact]
    public async Task ConcurrentRefreshAllowsOneRotationAndRevokesTheFamily()
    {
        if (!PostgreSqlApiFixture.Enabled) return;
        using var client = fixture.Client;
        var pair = await RegisterAndLoginAsync(client, "concurrent");

        var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v2/auth/refresh")
        {
            Content = JsonContent.Create(new RefreshRequest(pair.RefreshToken))
        };
        var secondRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v2/auth/refresh")
        {
            Content = JsonContent.Create(new RefreshRequest(pair.RefreshToken))
        };
        var responses = await Task.WhenAll(
            client.SendAsync(firstRequest, TestContext.Current.CancellationToken),
            client.SendAsync(secondRequest, TestContext.Current.CancellationToken));
        using var first = responses[0];
        using var second = responses[1];
        var successful = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var rotated = await successful.Content.ReadFromJsonAsync<TokenPair>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.AccessToken);
        using var revoked = await client.GetAsync("/api/v2/devices", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task LogoutAndDeviceRevocationInvalidateAccessAndRefreshCredentials()
    {
        if (!PostgreSqlApiFixture.Enabled) return;
        const string password = "Correct horse battery staple 42!";

        using (var sessionClient = fixture.Client)
        {
            var session = await RegisterAndLoginAsync(sessionClient, "logout");
            sessionClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var logout = await sessionClient.PostAsync("/api/v2/auth/logout", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            using var accessRejected = await sessionClient.GetAsync("/api/v2/devices", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, accessRejected.StatusCode);
            sessionClient.DefaultRequestHeaders.Authorization = null;
            using var refreshRejected = await sessionClient.PostAsJsonAsync("/api/v2/auth/refresh", new RefreshRequest(session.RefreshToken), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, refreshRejected.StatusCode);
        }

        using var owner = fixture.Client;
        var ownerPair = await RegisterAndLoginAsync(owner, "device-revoke");
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerPair.AccessToken);
        using var enrollmentResponse = await owner.PostAsJsonAsync("/api/v2/device-enrollments", new DeviceEnrollmentRequest(
            PlatformType.Windows, "Revocation integration PC", "2.0", 2, [DeviceCapability.Lock]), TestContext.Current.CancellationToken);
        var enrollment = await enrollmentResponse.Content.ReadFromJsonAsync<DeviceEnrollment>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
        using var approve = await owner.PostAsync($"/api/v2/device-enrollments/{enrollment.UserCode}/approve", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        using var exchange = await owner.PostAsJsonAsync("/api/v2/device-enrollments/token", new DeviceCodeRequest(enrollment.DeviceCode), TestContext.Current.CancellationToken);
        var device = await exchange.Content.ReadFromJsonAsync<DeviceTokenPair>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();

        var intent = new StepUpIntent(StepUpIntentType.DeviceRevoke, Guid.NewGuid(), device.DeviceId);
        using var optionsResponse = await owner.PostAsJsonAsync("/api/v2/auth/step-up/options", intent, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
        var options = await optionsResponse.Content.ReadFromJsonAsync<StepUpOptions>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
        using var completeResponse = await owner.PostAsJsonAsync("/api/v2/auth/step-up/complete", new StepUpCompletion(
            options.IntentId, "password", JsonSerializer.SerializeToElement(new { password })), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var grant = await completeResponse.Content.ReadFromJsonAsync<StepUpGrant>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
        owner.DefaultRequestHeaders.Add("X-Step-Up-Grant", grant.Grant);
        using var revoke = await owner.DeleteAsync($"/api/v2/devices/{device.DeviceId:D}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var deviceClient = fixture.Client;
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", device.AccessToken);
        using var deviceAccessRejected = await deviceClient.GetAsync("/api/v2/agent/commands", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, deviceAccessRejected.StatusCode);
        deviceClient.DefaultRequestHeaders.Authorization = null;
        using var deviceRefreshRejected = await deviceClient.PostAsJsonAsync("/api/v2/agent/auth/refresh", new RefreshRequest(device.RefreshToken), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, deviceRefreshRejected.StatusCode);
    }

    [Fact]
    public async Task OwnershipIdempotencyAndStepUpAreServerEnforced()
    {
        if (!PostgreSqlApiFixture.Enabled) return;
        using var owner = fixture.Client;
        using var stranger = fixture.Client;
        var ownerPair = await RegisterAndLoginAsync(owner, "owner");
        var strangerPair = await RegisterAndLoginAsync(stranger, "stranger");
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerPair.AccessToken);
        stranger.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", strangerPair.AccessToken);

        using var enrollmentResponse = await owner.PostAsJsonAsync("/api/v2/device-enrollments", new DeviceEnrollmentRequest(
            PlatformType.Windows, "Disposable integration PC", "2.0", 2, [DeviceCapability.Lock, DeviceCapability.Shutdown]), TestContext.Current.CancellationToken);
        var enrollment = await enrollmentResponse.Content.ReadFromJsonAsync<DeviceEnrollment>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
        using var approve = await owner.PostAsync($"/api/v2/device-enrollments/{enrollment.UserCode}/approve", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        using var exchange = await owner.PostAsJsonAsync("/api/v2/device-enrollments/token", new DeviceCodeRequest(enrollment.DeviceCode), TestContext.Current.CancellationToken);
        var device = await exchange.Content.ReadFromJsonAsync<DeviceTokenPair>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
        using var crossUser = await stranger.GetAsync($"/api/v2/devices/{device.DeviceId:D}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, crossUser.StatusCode);

        var idempotency = Guid.NewGuid().ToString("D");
        owner.DefaultRequestHeaders.Add("Idempotency-Key", idempotency);
        using var first = await owner.PostAsJsonAsync($"/api/v2/devices/{device.DeviceId:D}/commands", new CommandCreate(CommandType.Lock, ExplicitlyConfirmed: true), TestContext.Current.CancellationToken);
        using var second = await owner.PostAsJsonAsync($"/api/v2/devices/{device.DeviceId:D}/commands", new CommandCreate(CommandType.Lock, ExplicitlyConfirmed: true), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstCommand = await first.Content.ReadFromJsonAsync<Command>(TestContext.Current.CancellationToken);
        var secondCommand = await second.Content.ReadFromJsonAsync<Command>(TestContext.Current.CancellationToken);
        Assert.Equal(firstCommand!.Id, secondCommand!.Id);

        using var crossUserCommand = await stranger.GetAsync($"/api/v2/commands/{firstCommand.Id:D}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, crossUserCommand.StatusCode);

        using var deviceClient = fixture.Client;
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", device.AccessToken);
        using var deviceOnControllerRoute = await deviceClient.GetAsync("/api/v2/devices", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, deviceOnControllerRoute.StatusCode);
        Assert.Equal("application/problem+json", deviceOnControllerRoute.Content.Headers.ContentType?.MediaType);
        var forbidden = await deviceOnControllerRoute.Content.ReadFromJsonAsync<Problem>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
        Assert.Equal("forbidden", forbidden.Code);
        using var controllerOnDeviceRoute = await owner.GetAsync("/api/v2/agent/commands", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, controllerOnDeviceRoute.StatusCode);

        using var recovery = await deviceClient.GetAsync("/api/v2/agent/commands", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
        var recovered = await recovery.Content.ReadFromJsonAsync<Page<Command>>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
        Assert.Contains(recovered.Items, item => item.Id == firstCommand.Id);

        var firstAgent = Guid.NewGuid();
        using var claim = await deviceClient.PostAsJsonAsync($"/api/v2/agent/commands/{firstCommand.Id:D}/claim", new CommandClaim(firstAgent), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        using var competingClaim = await deviceClient.PostAsJsonAsync($"/api/v2/agent/commands/{firstCommand.Id:D}/claim", new CommandClaim(Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, competingClaim.StatusCode);
        using var replayMismatch = await deviceClient.PostAsJsonAsync($"/api/v2/agent/commands/{firstCommand.Id:D}/acknowledgements",
            new CommandAcknowledgement("accepted", firstAgent, Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, replayMismatch.StatusCode);
        using var accepted = await deviceClient.PostAsJsonAsync($"/api/v2/agent/commands/{firstCommand.Id:D}/acknowledgements",
            new CommandAcknowledgement("accepted", firstAgent, firstCommand.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var succeeded = await deviceClient.PostAsJsonAsync($"/api/v2/agent/commands/{firstCommand.Id:D}/acknowledgements",
            new CommandAcknowledgement("succeeded", firstAgent, firstCommand.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, succeeded.StatusCode);
        using var duplicateTerminal = await deviceClient.PostAsJsonAsync($"/api/v2/agent/commands/{firstCommand.Id:D}/acknowledgements",
            new CommandAcknowledgement("succeeded", firstAgent, firstCommand.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, duplicateTerminal.StatusCode);

        owner.DefaultRequestHeaders.Remove("Idempotency-Key");
        owner.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var unsupported = await owner.PostAsJsonAsync($"/api/v2/devices/{device.DeviceId:D}/commands", new CommandCreate(CommandType.Sleep, ExplicitlyConfirmed: true), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupported.StatusCode);
        owner.DefaultRequestHeaders.Remove("Idempotency-Key");
        owner.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var noStepUp = await owner.PostAsJsonAsync($"/api/v2/devices/{device.DeviceId:D}/commands", new CommandCreate(CommandType.Shutdown, ExplicitlyConfirmed: true), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, noStepUp.StatusCode);
    }

    [Fact]
    public async Task MigrationFullDeltaAndRerunKeepStableMappingsWithoutManifestPii()
    {
        if (!PostgreSqlApiFixture.Enabled) return;
        var directory = Path.Combine(Path.GetTempPath(), "pcconnect-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var snapshot = Path.Combine(directory, "snapshot.json");
        var manifest = Path.Combine(directory, "manifest.json");
        var key = Convert.ToBase64String(SHA256.HashData("migration-integration-key"u8.ToArray()));
        var reminderText = "private integration reminder";
        var apiKey = "legacy-api-key-never-in-manifest";
        try
        {
            Environment.SetEnvironmentVariable("PCCONNECT_MIGRATION_TARGET", fixture.ConnectionString);
            Environment.SetEnvironmentVariable("PCCONNECT_MIGRATION_CHECKSUM_KEY", key);
            Environment.SetEnvironmentVariable("PCCONNECT_LEGACY_CREDENTIAL_HASHING_KEY", key);
            Environment.SetEnvironmentVariable("PCCONNECT_MIGRATION_REMINDER_KEY_ID", "migration-test-v1");
            Environment.SetEnvironmentVariable("PCCONNECT_MIGRATION_REMINDER_KEY", key);
            await WriteMigrationSnapshotAsync(snapshot, "Original PC", reminderText, apiKey);

            var firstExit = await migration::MigrationProgram.RunAsync(MigrationArguments(snapshot, manifest, "full"), TestContext.Current.CancellationToken);
            Assert.Equal(0, firstExit);
            var firstMapping = await ReadMigrationStateAsync(fixture.ConnectionString);

            var rerunExit = await migration::MigrationProgram.RunAsync(MigrationArguments(snapshot, manifest, "full"), TestContext.Current.CancellationToken);
            Assert.Equal(0, rerunExit);
            var rerunMapping = await ReadMigrationStateAsync(fixture.ConnectionString);
            Assert.Equal(firstMapping.MappingId, rerunMapping.MappingId);
            Assert.Equal((1L, 1L, 1L), (rerunMapping.Users, rerunMapping.Devices, rerunMapping.Reminders));

            await WriteMigrationSnapshotAsync(snapshot, "Renamed PC", reminderText, apiKey);
            var deltaExit = await migration::MigrationProgram.RunAsync(MigrationArguments(snapshot, manifest, "delta"), TestContext.Current.CancellationToken);
            Assert.Equal(0, deltaExit);
            var deltaState = await ReadMigrationStateAsync(fixture.ConnectionString);
            Assert.Equal(firstMapping.MappingId, deltaState.MappingId);
            Assert.Equal("Renamed PC", deltaState.DeviceName);
            Assert.Equal((1L, 1L, 1L), (deltaState.Users, deltaState.Devices, deltaState.Reminders));

            var manifestText = await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("migration-user@example.test", manifestText, StringComparison.Ordinal);
            Assert.DoesNotContain(apiKey, manifestText, StringComparison.Ordinal);
            Assert.DoesNotContain(reminderText, manifestText, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PCCONNECT_MIGRATION_TARGET", null);
            Environment.SetEnvironmentVariable("PCCONNECT_MIGRATION_CHECKSUM_KEY", null);
            Environment.SetEnvironmentVariable("PCCONNECT_LEGACY_CREDENTIAL_HASHING_KEY", null);
            Environment.SetEnvironmentVariable("PCCONNECT_MIGRATION_REMINDER_KEY_ID", null);
            Environment.SetEnvironmentVariable("PCCONNECT_MIGRATION_REMINDER_KEY", null);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MigrationQuarantinesMalformedAndOrphanRowsWithoutWritingPii()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pcconnect-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var snapshot = Path.Combine(directory, "malformed.json");
        var manifest = Path.Combine(directory, "manifest.json");
        var key = Convert.ToBase64String(SHA256.HashData("migration-quarantine-key"u8.ToArray()));
        try
        {
            Environment.SetEnvironmentVariable("PCCONNECT_MIGRATION_CHECKSUM_KEY", key);
            Environment.SetEnvironmentVariable("PCCONNECT_LEGACY_CREDENTIAL_HASHING_KEY", key);
            await File.WriteAllTextAsync(snapshot, """
                {
                  "sourceSnapshotId":"malformed-fixture","sourceSchemaChecksum":"synthetic",
                  "users":[{"id":"u-bad","username":"secret-user","email":"secret@example.test","displayName":"Bad","dateOfBirth":"not-a-date","marketingOptIn":false,"marketingConsentAt":null,"passwordSha256":"bad","apiKey":"secret-key","enabled":true,"createdAt":"not-a-time"}],
                  "devices":[{"id":"d-orphan","userId":"missing","displayName":"Orphan","platform":"windows","agentVersion":"legacy","timezone":null,"capabilities":["lock"],"lastSeenAt":null}],
                  "reminders":[{"id":"r-orphan","userId":"missing","text":"private text","localStart":"not-a-time","timezone":null,"recurrenceRule":null,"targetDeviceIds":[]}]
                }
                """, TestContext.Current.CancellationToken);

            var exit = await migration::MigrationProgram.RunAsync(MigrationArguments(snapshot, manifest, "dry_run"), TestContext.Current.CancellationToken);
            Assert.Equal(2, exit);
            var text = await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(text);
            var quarantines = document.RootElement.GetProperty("quarantines");
            Assert.True(quarantines.TryGetProperty("invalid_verifier", out _));
            Assert.True(quarantines.TryGetProperty("orphan_device", out _));
            Assert.True(quarantines.TryGetProperty("orphan_reminder", out _));
            Assert.DoesNotContain("secret-user", text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret@example.test", text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-key", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private text", text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PCCONNECT_MIGRATION_CHECKSUM_KEY", null);
            Environment.SetEnvironmentVariable("PCCONNECT_LEGACY_CREDENTIAL_HASHING_KEY", null);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string[] MigrationArguments(string snapshot, string manifest, string mode) =>
    [
        "--snapshot", snapshot,
        "--manifest", manifest,
        "--mode", mode,
        "--source-system", "integration-fixture",
        "--compat-sunset", DateTimeOffset.UtcNow.AddDays(60).ToString("O")
    ];

    private static async Task WriteMigrationSnapshotAsync(string path, string deviceName, string reminderText, string apiKey)
    {
        var snapshot = new
        {
            sourceSnapshotId = "sanitized-integration-fixture",
            sourceSchemaChecksum = "synthetic-v1",
            users = new[] { new { id = "u-1", username = "migration-user", email = "migration-user@example.test", displayName = "Migration User", dateOfBirth = "1990-01-01", marketingOptIn = false, marketingConsentAt = (string?)null, passwordSha256 = Convert.ToHexString(SHA256.HashData("Migration Password 42!"u8.ToArray())).ToLowerInvariant(), apiKey, enabled = true, createdAt = "2024-01-01T00:00:00.0000000+00:00" } },
            devices = new[] { new { id = "d-1", userId = "u-1", displayName = deviceName, platform = "windows", agentVersion = "1.0", timezone = "Europe/London", capabilities = new[] { "lock", "shutdown", "reminders" }, lastSeenAt = "2024-01-02T00:00:00.0000000+00:00" } },
            reminders = new[] { new { id = "r-1", userId = "u-1", text = reminderText, localStart = "2026-01-01T09:30:00", timezone = "Europe/London", recurrenceRule = (string?)null, targetDeviceIds = new[] { "d-1" } } }
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot), TestContext.Current.CancellationToken);
    }

    private static async Task<(Guid MappingId, long Users, long Devices, long Reminders, string DeviceName)> ReadMigrationStateAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT
              (SELECT v2_id FROM legacy_id_map WHERE source_system='integration-fixture' AND entity_type='user' AND legacy_id='u-1'),
              (SELECT count(*) FROM legacy_id_map WHERE source_system='integration-fixture' AND entity_type='user'),
              (SELECT count(*) FROM legacy_id_map WHERE source_system='integration-fixture' AND entity_type='device'),
              (SELECT count(*) FROM legacy_id_map WHERE source_system='integration-fixture' AND entity_type='reminder'),
              (SELECT display_name FROM devices d JOIN legacy_id_map m ON m.v2_id=d.id WHERE m.source_system='integration-fixture' AND m.entity_type='device' AND m.legacy_id='d-1');
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4));
    }

    private static async Task SetAccountStateAsync(string connectionString, string login, string state)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("UPDATE users SET account_state=@state WHERE username=@login", connection);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("login", login);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<TokenPair> RegisterAndLoginAsync(HttpClient client, string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var login = prefix + "-" + suffix;
        const string password = "Correct horse battery staple 42!";
        using var register = await client.PostAsJsonAsync("/api/v2/auth/register", new RegistrationRequest(
            login, suffix + "@example.test", prefix, password, "Europe/London", false,
            new(PlatformType.Android, "integration", "1")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        using var loginResponse = await client.PostAsJsonAsync("/api/v2/auth/password/login", new PasswordLoginRequest(
            login, password, new(PlatformType.Android, "integration", "1")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        return await loginResponse.Content.ReadFromJsonAsync<TokenPair>(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
    }
}
