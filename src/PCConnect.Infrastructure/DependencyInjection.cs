using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using PCConnect.Core;
using PCConnect.Core.Domain;
using PCConnect.Infrastructure.Caching;
using PCConnect.Infrastructure.Contexts;
using PCConnect.Infrastructure.Contexts.Commands;
using PCConnect.Infrastructure.Contexts.Devices;
using PCConnect.Infrastructure.Contexts.Identity;
using PCConnect.Infrastructure.Contexts.Reminders;
using PCConnect.Infrastructure.Data;
using PCConnect.Infrastructure.Recurrence;
using PCConnect.Infrastructure.Security;
using PCConnect.Infrastructure.Telemetry;
using StackExchange.Redis;

namespace PCConnect.Infrastructure;

public sealed class DatabaseOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    public int MaxPoolSize { get; set; } = 20;
}

public sealed class CacheOptions
{
    /// <summary>
    /// Valkey connection string. Empty selects the in-process fallback, which is
    /// correct for a single instance and for tests but loses presence and rate
    /// limits on restart — the API logs a warning when it is used.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}

public static class DependencyInjection
{
    public static IServiceCollection AddPcConnectInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        Db.ConfigureDapper();

        services.Configure<DatabaseOptions>(configuration.GetSection("Database"));
        services.Configure<CacheOptions>(configuration.GetSection("Cache"));
        services.Configure<Argon2Options>(configuration.GetSection("Argon2"));
        services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
        services.Configure<EnvelopeOptions>(configuration.GetSection("Kek"));
        services.Configure<IdentityOptions>(configuration.GetSection("Identity"));
        services.Configure<WebAuthnOptions>(configuration.GetSection("WebAuthn"));

        // Configuration is validated at boot, not at first use: a missing key
        // must stop the process rather than surface as a 500 on the request that
        // needs it (08 §3).
        services.AddSingleton<IValidateOptions>(sp => new ValidateOptions(sp));

        // Configuration is read when the service is first resolved, not while it
        // is being registered. Reading it here would bind whatever the
        // configuration happened to hold at registration time, which is before a
        // test host or a late provider has contributed anything.
        services.AddSingleton(sp =>
        {
            var connectionString = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>()
                .Value.ConnectionString;

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Database:ConnectionString is not configured. Set PCCONNECT_DATABASE__CONNECTIONSTRING.");
            }

            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.EnableDynamicJson();
            return builder.Build();
        });

        services.AddSingleton<Db>();

        services.AddSingleton<ICacheStore>(sp =>
        {
            var cacheConnection = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CacheOptions>>()
                .Value.ConnectionString;

            if (string.IsNullOrWhiteSpace(cacheConnection))
            {
                // Correct for one instance and for tests, but it loses presence
                // and rate-limit windows on restart and shares nothing across
                // instances — which is the S2-06 failure. Said out loud at boot.
                sp.GetRequiredService<ILogger<InMemoryCacheStore>>().LogWarning(
                    "No cache configured; using the in-process store. Presence and rate limits will not " +
                    "survive a restart and will not be shared across instances.");

                return new InMemoryCacheStore();
            }

            return new ValkeyCacheStore(
                ConnectionMultiplexer.Connect(cacheConnection),
                sp.GetRequiredService<ILogger<ValkeyCacheStore>>());
        });

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<Core.IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<IEnvelopeEncryptor, EnvelopeEncryptor>();
        services.AddSingleton<TokenService>();
        services.AddSingleton<ITokenIssuer>(sp => sp.GetRequiredService<TokenService>());
        services.AddSingleton<IPresenceTracker, PresenceTracker>();
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<CommandMetrics>();
        services.AddSingleton<RecurrenceExpander>();

        services.AddScoped<Realtime.CallerResolver>();
        services.AddScoped<SecurityEventLog>();
        services.AddScoped<IdentityService>();
        services.AddScoped<WebAuthnService>();
        services.AddScoped<StepUpService>();
        services.AddScoped<DeviceService>();
        services.AddScoped<CommandService>();
        services.AddScoped<ReminderService>();

        services.AddHttpClient<IBreachedPasswordChecker, PwnedPasswordChecker>(client =>
        {
            client.BaseAddress = new Uri("https://api.pwnedpasswords.com/");
            client.Timeout = TimeSpan.FromSeconds(3);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PCConnect/2.0");
        });

        services.AddSingleton<IEmailSender, LoggingEmailSender>();

        return services;
    }
}

public interface IValidateOptions
{
    void Validate();
}

internal sealed class ValidateOptions(IServiceProvider services) : IValidateOptions
{
    public void Validate()
    {
        var argon = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Argon2Options>>().Value;
        argon.Validate();

        // Constructing these throws when the key material or the connection
        // string is missing or malformed, so a misconfigured process fails at
        // boot rather than on the first request that needs them (08 §3).
        _ = services.GetRequiredService<IEnvelopeEncryptor>().CurrentKekId;
        _ = services.GetRequiredService<ITokenIssuer>().AccessTokenLifetime;
        _ = services.GetRequiredService<NpgsqlDataSource>();
    }
}

/// <summary>
/// Pwned Passwords range API, k-anonymity: only the first five hex characters of
/// the SHA-1 leave the process (03 §2.5). Fails open — a third-party outage must
/// not stop people setting a password — but says so in the log when it does.
/// </summary>
public sealed class PwnedPasswordChecker(HttpClient http, ILogger<PwnedPasswordChecker> logger) : IBreachedPasswordChecker
{
    public async Task<bool> IsBreachedAsync(string password, CancellationToken ct = default)
    {
        var (prefix, suffix) = PasswordPolicy.PwnedRange(password);

        try
        {
            using var response = await http.GetAsync($"range/{prefix}", ct);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            foreach (var line in body.Split('\n'))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line.AsSpan(0, colon).Trim().Equals(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Pwned Passwords lookup failed; allowing the password");
            return false;
        }
    }
}

/// <summary>
/// Until SMTP is configured, mail is written to the log at information level.
/// The token itself is included because without it a local or staging
/// environment has no way to complete a reset; production configures a real
/// sender and this implementation is not registered.
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default)
    {
        logger.LogInformation("Email to {Recipient}: {Subject}\n{Body}", toEmail, subject, body);
        return Task.CompletedTask;
    }
}
