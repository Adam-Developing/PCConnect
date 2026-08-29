using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Fido2NetLib;
using PCConnect.Domain;
using PCConnect.Infrastructure.Database;
using PCConnect.Infrastructure.Identity;
using PCConnect.Infrastructure.Devices;
using PCConnect.Infrastructure.Commands;
using PCConnect.Infrastructure.Reminders;
using PCConnect.Infrastructure.Accounts;
using PCConnect.Infrastructure.Security;
using PCConnect.Infrastructure.Compatibility;

namespace PCConnect.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPCConnectInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

        var securitySection = configuration.GetSection(SecurityOptions.SectionName);
        var security = new SecurityOptions
        {
            TokenHashingKey = securitySection[nameof(SecurityOptions.TokenHashingKey)] ?? string.Empty,
            LegacyCredentialHashingKey = securitySection[nameof(SecurityOptions.LegacyCredentialHashingKey)] ?? string.Empty,
            ActiveReminderKeyId = securitySection[nameof(SecurityOptions.ActiveReminderKeyId)] ?? string.Empty,
            ReminderWrappingKeys = securitySection.GetSection(nameof(SecurityOptions.ReminderWrappingKeys)).GetChildren()
                .ToDictionary(x => x.Key, x => x.Value ?? string.Empty, StringComparer.Ordinal),
            ActiveEmailKeyId = securitySection[nameof(SecurityOptions.ActiveEmailKeyId)] ?? string.Empty,
            EmailEncryptionKeys = securitySection.GetSection(nameof(SecurityOptions.EmailEncryptionKeys)).GetChildren()
                .ToDictionary(x => x.Key, x => x.Value ?? string.Empty, StringComparer.Ordinal),
            DeletionTombstoneKey = securitySection[nameof(SecurityOptions.DeletionTombstoneKey)] ?? string.Empty,
            ExportEncryptionKey = securitySection[nameof(SecurityOptions.ExportEncryptionKey)] ?? string.Empty,
            WebAuthnRpId = securitySection[nameof(SecurityOptions.WebAuthnRpId)] ?? "pcconnect.adamdeveloping.co.uk",
            WebAuthnRpName = securitySection[nameof(SecurityOptions.WebAuthnRpName)] ?? "PCConnect",
            WebAuthnOrigins = securitySection.GetSection(nameof(SecurityOptions.WebAuthnOrigins)).GetChildren().Select(x => x.Value).OfType<string>().ToHashSet(StringComparer.Ordinal)
        };
        _ = security.DecodeTokenKey();
        _ = security.DecodeLegacyKey();
        _ = security.DecodeReminderKeys();
        _ = security.DecodeEmailKeys();
        _ = security.DecodeDeletionKey();
        _ = security.DecodeExportKey();

        services.AddSingleton(security);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, Argon2IdPasswordHasher>();
        services.AddSingleton<IOpaqueTokenService, OpaqueTokenService>();
        services.AddSingleton<IReminderCipher, ReminderCipher>();
        services.AddSingleton<IEmailCipher, EmailCipher>();
        services.AddFido2(options =>
        {
            options.ServerDomain = security.WebAuthnRpId;
            options.ServerName = security.WebAuthnRpName;
            options.Origins = security.WebAuthnOrigins;
            options.ChallengeSize = 32;
            options.Timeout = 300_000;
        });
        services.AddScoped<AuthenticationService>();
        services.AddScoped<IAuthenticationService>(provider => provider.GetRequiredService<AuthenticationService>());
        services.AddScoped<LoginAttemptGuard>();
        services.AddScoped<IEmailOutbox, EmailOutbox>();
        services.AddScoped<IPasskeyService, PasskeyService>();
        services.AddScoped<IDeviceService, DeviceService>();
        services.AddScoped<IStepUpService, StepUpService>();
        services.AddScoped<StepUpGrantConsumer>();
        services.AddScoped<ICommandService, CommandService>();
        services.AddScoped<IReminderService, ReminderService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<ILegacyCompatibilityService, LegacyCompatibilityService>();
        services.AddSingleton<IExportArtifactStore, ExportArtifactStore>();

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);
        services.AddDbContext<PCConnectDbContext>(options => options.UseNpgsql(dataSource));
        return services;
    }
}
