using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PCConnect.Api;
using PCConnect.Core.Contracts;
using PCConnect.DbMigrator;
using Testcontainers.PostgreSql;

namespace PCConnect.IntegrationTests;

/// <summary>
/// A real PostgreSQL 18, migrated by the real migration runner, shared by every
/// test in the collection.
///
/// Not an in-memory substitute: the schema carries CHECK constraints, partial
/// indexes and `FOR UPDATE SKIP LOCKED` claims that only a real engine
/// exercises, and half of what these tests assert is enforced by the database.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<ApiEntryPoint>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("pcconnect_test")
        .WithUsername("pcconnect")
        .WithPassword("test-only-not-a-secret")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var applied = await new Migrator(ConnectionString, _ => { }).UpAsync(allowDestructive: false);
        if (applied == 0)
        {
            throw new InvalidOperationException("No migrations were applied to the test database.");
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = ConnectionString,
                ["Database:MigrateOnStartup"] = "false",
                // No Valkey: the in-process store is correct for a single
                // instance, and the tests assert behaviour rather than topology.
                ["Cache:ConnectionString"] = string.Empty,
                ["Jwt:PrivateKeyPem"] = signingKey.ExportECPrivateKeyPem(),
                ["Jwt:Issuer"] = "https://api.pcconnect.test",
                ["Kek:CurrentKekId"] = "test",
                ["Kek:Keys:test"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                // The real cost would make the suite take minutes; the parameter
                // handling itself is covered by Argon2PasswordHasherTests.
                ["Argon2:MemoryKib"] = "19456",
                ["Argon2:TimeCost"] = "2",
                ["Identity:AcceptLegacyPasswordHash"] = "true",
                ["Web:EnableLegacyShim"] = "true",
                ["Discovery:RealtimeUrl"] = "ws://localhost/rt",
            });
        });

        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
    }

    /// <summary>Registers a fresh account and returns a client already carrying its token.</summary>
    public async Task<TestUser> RegisterUserAsync(string? password = null)
    {
        // Random, not UUIDv7: v7's leading hex digits are a millisecond
        // timestamp, so a prefix of one collides for every account created in
        // the same millisecond.
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var credentials = new RegisterRequest(
            $"user{suffix}",
            $"user{suffix}@example.test",
            password ?? "correct-horse-battery-staple",
            "Test User",
            "Europe/London");

        var client = CreateClient(FreshIp());
        var tokens = await ReadAsync<TokenPairResponse>(
            await client.PostAsJsonAsync("/v2/auth/register", credentials), "auth/register");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        return new TestUser(credentials.Username, credentials.Email, credentials.Password, tokens, client);
    }

    /// <summary>Pairs a device end to end and returns a client carrying its device token.</summary>
    public async Task<TestDevice> PairDeviceAsync(TestUser user, string name = "Test-PC")
    {
        var anonymous = CreateClient(FreshIp());

        var start = await ReadAsync<PairStartResponse>(
            await anonymous.PostAsJsonAsync("/v2/devices/pair/start", new PairStartRequest(name, "windows", "5.0.0")),
            "pair/start");

        _ = await ReadAsync<PairClaimResponse>(
            await user.Client.PostAsJsonAsync("/v2/devices/pair/claim", new PairClaimRequest(start.PairingCode)),
            "pair/claim");

        var poll = await ReadAsync<PairPollResponse>(
            await anonymous.PostAsJsonAsync("/v2/devices/pair/poll", new PairPollRequest(start.PollToken)),
            "pair/poll");

        if (poll.Status != "paired" || poll.DeviceId is null || poll.DeviceSecret is null)
        {
            throw new InvalidOperationException($"Pairing did not complete: status was '{poll.Status}'.");
        }

        var tokens = await ReadAsync<TokenPairResponse>(
            await anonymous.PostAsJsonAsync("/v2/devices/token",
                new DeviceTokenRequest(poll.DeviceId, poll.DeviceSecret, "5.0.0", "Windows 11")),
            "devices/token");

        var deviceClient = CreateClient(FreshIp());
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        return new TestDevice(poll.DeviceId, poll.DeviceSecret, tokens, deviceClient);
    }

    /// <summary>
    /// A client that presents a distinct caller IP.
    ///
    /// Every test would otherwise share one address and exhaust the per-IP
    /// budgets that 03 §6 puts on pairing and login — the rate limiter doing its
    /// job would look like an authorisation failure. Tests that mean to exercise
    /// a limit ask for one address deliberately.
    /// </summary>
    public HttpClient CreateClient(string clientIp)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", clientIp);
        return client;
    }

    private static int _ipCounter;

    public static string FreshIp()
    {
        var next = Interlocked.Increment(ref _ipCounter);
        return $"198.51.100.{next % 250 + 1}:{40000 + (next % 20000)}";
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{what} failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new InvalidOperationException($"{what} returned an empty body.");
    }

    /// <summary>A single-use confirmation token for a destructive command.</summary>
    public static async Task<string> StepUpAsync(TestUser user)
    {
        var challenge = await (await user.Client.PostAsync("/v2/auth/step-up/start", null))
            .Content.ReadFromJsonAsync<StepUpChallengeResponse>()
            ?? throw new InvalidOperationException("No step-up challenge was issued.");

        var verified = await (await user.Client.PostAsJsonAsync("/v2/auth/step-up/verify",
            new StepUpVerifyRequest(challenge.ChallengeId, "password", user.Password)))
            .Content.ReadFromJsonAsync<StepUpTokenResponse>()
            ?? throw new InvalidOperationException("Step-up was not verified.");

        return verified.StepUpToken;
    }

    public static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        return envelope?.Error.Code ?? $"http.{(int)response.StatusCode}";
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed record TestUser(string Username, string Email, string Password, TokenPairResponse Tokens, HttpClient Client);

public sealed record TestDevice(string DeviceId, string DeviceSecret, TokenPairResponse Tokens, HttpClient Client);

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
