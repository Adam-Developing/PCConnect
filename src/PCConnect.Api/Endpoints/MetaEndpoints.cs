using Dapper;
using Microsoft.Extensions.Options;
using PCConnect.Api.Configuration;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Infrastructure.Data;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Api.Endpoints;

public static class MetaEndpoints
{
    public static IEndpointRouteBuilder MapMetaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v2/meta").WithTags("Meta");

        // Unauthenticated on purpose: this is what lets an installed client
        // discover that it is below the minimum supported build and show an
        // update prompt, which is the mechanism that eventually ends the legacy
        // era (04 §2).
        group.MapGet("/discovery", (IOptions<DiscoveryOptions> options, IClock clock) =>
        {
            var value = options.Value;
            return Results.Ok(new DiscoveryResponse(
                value.ApiVersion,
                value.RealtimeUrl,
                value.MinimumSupportedClient,
                value.RecommendedClient,
                new Dictionary<string, DateTimeOffset?> { ["v1"] = value.LegacySunsetAt },
                value.Capabilities,
                clock.UtcNow));
        })
            .AllowAnonymous()
            .WithName("discovery");

        group.MapGet("/time", (IClock clock) =>
            Results.Ok(new ServerTimeResponse(clock.UtcNow, clock.UtcNow.ToUnixTimeMilliseconds())))
            .AllowAnonymous()
            .WithName("serverTime")
            .WithSummary(
                "Clock-skew diagnostics only. Scheduling is UTC end to end, so no client needs " +
                "this to decide when a reminder fires.");

        // /healthz says the process is alive. /readyz says it can actually serve
        // a request, and is what gates a deploy and drives the proxy.
        app.MapGet("/healthz", () => Results.Ok(new HealthResponse("ok", new Dictionary<string, string>())))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapGet("/readyz", async (Db db, ICacheStore cache, CancellationToken ct) =>
        {
            var checks = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                await using var connection = await db.OpenAsync(ct);
                _ = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
                checks["database"] = "ok";
            }
            catch (Npgsql.NpgsqlException ex)
            {
                checks["database"] = $"failed: {ex.GetType().Name}";
            }

            checks["cache"] = await cache.PingAsync(ct) ? "ok" : "failed";

            var healthy = checks.Values.All(v => v == "ok");
            var response = new HealthResponse(healthy ? "ok" : "degraded", checks);

            return healthy ? Results.Ok(response) : Results.Json(response, statusCode: 503);
        })
            .AllowAnonymous()
            .ExcludeFromDescription();

        // The public half of the signing key, for any verifier that is not this
        // process — a future service, or an operator checking a token by hand.
        app.MapGet("/.well-known/jwks.json", (TokenService tokens) => Results.Ok(tokens.BuildJwks()))
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }
}
