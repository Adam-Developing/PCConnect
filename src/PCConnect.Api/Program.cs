using System.Threading.RateLimiting;
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PCConnect.Api;
using PCConnect.Api.Endpoints;
using PCConnect.Api.Realtime;
using PCConnect.Api.Security;
using PCConnect.Contracts.V2;
using PCConnect.Infrastructure;
using PCConnect.Infrastructure.Database;
using PCConnect.Infrastructure.Observability;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
if (Directory.Exists("/run/secrets")) builder.Configuration.AddKeyPerFile("/run/secrets", optional: false);
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("PCConnect-v2")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeyRingPath"] ?? "data-protection"));
var dataProtectionCertificatePath = builder.Configuration["DataProtection:CertificatePath"];
if (!string.IsNullOrWhiteSpace(dataProtectionCertificatePath))
{
    var certificate = X509CertificateLoader.LoadPkcs12FromFile(
        dataProtectionCertificatePath,
        builder.Configuration["DataProtection:CertificatePassword"]);
    dataProtection.ProtectKeysWithCertificate(certificate);
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576);
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
    options.SerializerOptions.MaxDepth = 64;
});
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails();
builder.Services.AddPCConnectInfrastructure(builder.Configuration);
var release = builder.Configuration["Release"] ?? "unknown";
builder.Logging.AddJsonConsole();
builder.Logging.AddOpenTelemetry(options =>
{
    options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("pcconnect-api", serviceVersion: release));
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
    options.AddOtlpExporter();
});
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("pcconnect-api", serviceVersion: release))
    .WithTracing(tracing => tracing
        .AddSource(PCConnectTelemetry.ActivitySourceName, "Npgsql")
        .AddAspNetCoreInstrumentation(options => options.RecordException = true)
        .AddHttpClientInstrumentation(options => options.RecordException = true)
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(PCConnectTelemetry.MeterName, "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Net.Http", "Npgsql")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());

builder.Services.AddAuthentication("Opaque")
    .AddScheme<AuthenticationSchemeOptions, OpaqueAuthenticationHandler>("Opaque", null);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Controller", policy => policy.RequireAuthenticatedUser().RequireClaim(PCConnectClaimTypes.SubjectKind, "user"));
    options.AddPolicy("Device", policy => policy.RequireAuthenticatedUser().RequireClaim(PCConnectClaimTypes.SubjectKind, "device"));
});

var signalR = builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 65_536;
    options.EnableDetailedErrors = false;
});
var valkey = builder.Configuration["Realtime:ValkeyConnection"];
if (!string.IsNullOrWhiteSpace(valkey)) signalR.AddStackExchangeRedis(valkey);

var origins = builder.Configuration.GetSection("Http:AllowedOrigins").Get<string[]>() ?? [];
var authenticationPermitLimit = Math.Clamp(builder.Configuration.GetValue<int?>("Http:RateLimits:AuthenticationPermitLimit") ?? 10, 1, 10_000);
builder.Services.AddCors(options => options.AddPolicy("OwnedOrigins", policy => policy.WithOrigins(origins).AllowAnyHeader().WithMethods("GET", "POST", "PATCH", "DELETE").AllowCredentials()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ProblemMiddleware.WriteProblemAsync(
            context.HttpContext,
            StatusCodes.Status429TooManyRequests,
            "rate_limited",
            "Too many requests");
    };
    options.AddPolicy("authentication", context => RateLimitPartition.GetSlidingWindowLimiter(
        $"{context.Connection.RemoteIpAddress}", _ => new SlidingWindowRateLimiterOptions { PermitLimit = authenticationPermitLimit, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 }));
    options.AddPolicy("refresh", context => RateLimitPartition.GetFixedWindowLimiter(
        $"{context.Connection.RemoteIpAddress}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("enrollment", context => RateLimitPartition.GetFixedWindowLimiter(
        $"{context.Connection.RemoteIpAddress}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
    options.AddPolicy("commands", context => RateLimitPartition.GetTokenBucketLimiter(
        context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new TokenBucketRateLimiterOptions { TokenLimit = 10, TokensPerPeriod = 10, ReplenishmentPeriod = TimeSpan.FromSeconds(1), AutoReplenishment = true, QueueLimit = 0 }));
    options.AddPolicy("compatibility", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddHealthChecks();
builder.Services.AddHostedService<RealtimeSubscriber>();

var app = builder.Build();
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1
};
var trustedProxyNetwork = builder.Configuration["Http:TrustedProxyNetwork"];
if (!string.IsNullOrWhiteSpace(trustedProxyNetwork))
    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(trustedProxyNetwork));
app.UseForwardedHeaders(forwarded);
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseMiddleware<ProblemMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
    await next();
});
app.UseHttpsRedirection();
app.UseCors("OwnedOrigins");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api/v2");
api.MapGet("/health/live", () => new Health()).AllowAnonymous();
api.MapGet("/health/ready", async (HttpContext context, NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    try
    {
        await using var command = dataSource.CreateCommand("""
            SELECT to_regclass('public.users') IS NOT NULL
              AND to_regclass('public.commands') IS NOT NULL
              AND EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId"='202608260001_InitialCanonicalV2');
            """);
        var ready = await command.ExecuteScalarAsync(cancellationToken) is true;
        PCConnectTelemetry.RecordReadiness(ready);
        return ready
            ? Results.Ok(new Health())
            : Results.Json(
                new Problem(
                    "https://pcconnect.adamdeveloping.co.uk/problems/not_ready",
                    "Service unavailable",
                    StatusCodes.Status503ServiceUnavailable,
                    "not_ready",
                    ProblemMiddleware.CorrelationId(context),
                    null,
                    context.Request.Path),
                statusCode: StatusCodes.Status503ServiceUnavailable,
                contentType: "application/problem+json");
    }
    catch (NpgsqlException)
    {
        PCConnectTelemetry.RecordReadiness(false);
        return Results.Json(
            new Problem(
                "https://pcconnect.adamdeveloping.co.uk/problems/not_ready",
                "Service unavailable",
                StatusCodes.Status503ServiceUnavailable,
                "not_ready",
                ProblemMiddleware.CorrelationId(context),
                null,
                context.Request.Path),
            statusCode: StatusCodes.Status503ServiceUnavailable,
            contentType: "application/problem+json");
    }
}).AllowAnonymous();
api.MapGet("/version", (IConfiguration configuration) => new VersionInfo(configuration["Release"] ?? "unknown")).AllowAnonymous();
api.MapIdentityEndpoints();
api.MapDeviceEndpoints();
api.MapCommandEndpoints();
api.MapReminderEndpoints();
api.MapAccountEndpoints();
api.MapPasskeyEndpoints();
api.MapHub<ControllerHub>("/hubs/controller");
api.MapHub<DeviceHub>("/hubs/device");
app.MapLegacyCompatibilityEndpoints();

app.Run();

public partial class Program;
