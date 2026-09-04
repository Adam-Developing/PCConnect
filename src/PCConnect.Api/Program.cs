using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using PCConnect.Api.Auth;
using PCConnect.Api.Configuration;
using PCConnect.Api.Endpoints;
using PCConnect.Api.Http;
using PCConnect.Api.Legacy;
using PCConnect.Infrastructure.Realtime;
using PCConnect.Core;
using PCConnect.DbMigrator;
using PCConnect.Infrastructure;
using PCConnect.Infrastructure.Security;
using PCConnect.Infrastructure.Telemetry;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// Configuration comes from appsettings plus PCCONNECT_-prefixed environment
// variables. Nothing reads a file of secrets from the repository (03 §7).
builder.Configuration.AddEnvironmentVariables("PCCONNECT_");
DevelopmentKeys.FillMissing(builder);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

builder.Services.Configure<DiscoveryOptions>(builder.Configuration.GetSection("Discovery"));
builder.Services.Configure<WebOptions>(builder.Configuration.GetSection("Web"));

builder.Services.AddPcConnectInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IRealtimeNotifier, SignalRNotifier>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    // Clients must ignore unknown fields (04 §2); the server refuses them, so a
    // typo in a request body is a 400 rather than a silently discarded value.
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});

// ── authentication ───────────────────────────────────────────────────────────
// Bearer only. There is no cookie authentication on /v2/*, which makes CSRF
// structurally impossible on the API surface (03 §5, closes S1-12).

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // A browser WebSocket cannot set an Authorization header, so
                // SignalR passes the token in the query string. It is accepted
                // only for the hub path.
                var accessToken = context.Request.Query["access_token"].FirstOrDefault();
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/rt", StringComparison.Ordinal))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },

            OnChallenge = async context =>
            {
                // The default challenge writes an empty body; the contract says
                // every failure carries the envelope.
                context.HandleResponse();

                if (context.Response.HasStarted)
                {
                    return;
                }

                context.HttpContext.RequestServices.GetRequiredService<CommandMetrics>().AuthFailure("http");

                var expired = context.AuthenticateFailure is
                    Microsoft.IdentityModel.Tokens.SecurityTokenExpiredException;

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(new PCConnect.Core.Contracts.ErrorEnvelope(
                    new PCConnect.Core.Contracts.ErrorBody(
                        expired ? ErrorCodes.AuthTokenExpired : ErrorCodes.AuthTokenInvalid,
                        expired ? "That access token has expired. Refresh and try again." : "Sign in to continue.",
                        context.HttpContext.TraceIdentifier)));
            },
        };
    });

builder.Services.AddAuthorization();

// TokenValidationParameters are built from the same TokenService that mints
// tokens, so the signing algorithm is pinned in one place for both directions.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<TokenService>((options, tokens) => options.TokenValidationParameters = tokens.CreateValidationParameters());

// ── realtime ─────────────────────────────────────────────────────────────────

var signalR = builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaximumReceiveMessageSize = 16 * 1024;   // 05 §7: anything larger closes the socket
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

var backplane = builder.Configuration["Cache:ConnectionString"];
if (!string.IsNullOrWhiteSpace(backplane))
{
    // The backplane is what makes a restart non-destructive and `--scale api=N`
    // possible: group membership lives in Valkey, not in one process's heap.
    signalR.AddStackExchangeRedis(backplane, options => options.Configuration.ChannelPrefix =
        StackExchange.Redis.RedisChannel.Literal("pcconnect"));
}

// ── platform ─────────────────────────────────────────────────────────────────

builder.Services.AddExceptionHandler<ErrorEnvelopeHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddPcConnectOpenApi();

builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddMeter(CommandMetrics.MeterName)
    .AddPrometheusExporter());

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    var origins = builder.Configuration.GetSection("Web:CorsAllowedOrigins").Get<string[]>() ?? [];

    if (origins.Length == 0)
    {
        // No origins configured means no browser origin is allowed, which is the
        // safe default. `AllowAnyOrigin` with credentials is S1-11.
        policy.WithOrigins("https://localhost");
    }
    else
    {
        policy.WithOrigins(origins);
    }

    policy.AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders(RequestContextMiddleware.HeaderName, "Deprecation", "Sunset", "Retry-After")
        .AllowCredentials();
}));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Behind Caddy: without this the caller IP in every rate limit and security
    // event is the proxy's.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Configuration is validated before the first request, so a missing KEK or an
// under-strength Argon2 setting stops the process at boot (08 §3).
app.Services.GetRequiredService<IValidateOptions>().Validate();

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    var connectionString = app.Configuration["Database:ConnectionString"]!;
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migrations");
    var applied = await new Migrator(connectionString, message => logger.LogInformation("{Migration}", message))
        .UpAsync(allowDestructive: false);
    logger.LogInformation("Applied {Count} migration(s) at startup", applied);
}

app.UseForwardedHeaders();
app.UseMiddleware<RequestContextMiddleware>();
app.UseExceptionHandler();

if (app.Services.GetRequiredService<IOptions<WebOptions>>().Value.EnableHsts)
{
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), microphone=(), payment=()";
    headers["Cross-Origin-Resource-Policy"] = "same-site";

    // An API that never renders HTML should never be allowed to load anything.
    if (!context.Request.Path.StartsWithSegments("/openapi", StringComparison.Ordinal))
    {
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    }

    await next();
});

app.UseSerilogRequestLogging(options => options.EnrichDiagnosticContext = (diagnostic, context) =>
{
    diagnostic.Set("RequestId", context.TraceIdentifier);
    diagnostic.Set("ClientIp", context.Connection.RemoteIpAddress?.ToString());
});

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapDeviceEndpoints();
app.MapCommandEndpoints();
app.MapReminderEndpoints();
app.MapAccountEndpoints();
app.MapMetaEndpoints();

var web = app.Services.GetRequiredService<IOptions<WebOptions>>().Value;
if (web.LegacyShimRetired)
{
    // P6.2: legacy endpoints answer 410 with a link to the current client,
    // rather than 404, so an old client can tell "gone" from "broken".
    app.Map("/api/{**rest}", () => Results.Json(
        new PCConnect.Core.Contracts.ErrorEnvelope(new PCConnect.Core.Contracts.ErrorBody(
            ErrorCodes.Gone,
            "This version of PCConnect is no longer supported. Install the current client.",
            "legacy")),
        statusCode: StatusCodes.Status410Gone));
}
else if (web.EnableLegacyShim)
{
    app.MapLegacyShim();
}

app.MapHub<PcConnectHub>("/rt");
app.MapPrometheusScrapingEndpoint("/metrics");
app.MapOpenApi();

await app.RunAsync();

/// <summary>
/// A public marker in this assembly so the integration tests can host the real
/// application. The implicit top-level `Program` class is not used for this: it
/// collides with the worker's, which also has one.
/// </summary>
namespace PCConnect.Api
{
    public sealed class ApiEntryPoint;
}
