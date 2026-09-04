using PCConnect.Api.Auth;
using PCConnect.Api.Http;
using PCConnect.Core.Contracts;
using PCConnect.Infrastructure.Contexts.Devices;

namespace PCConnect.Api.Endpoints;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v2/devices").WithTags("Devices");

        group.MapGet("", async (DeviceService devices, HttpContext http, CancellationToken ct) =>
            Results.Ok(new Page<DeviceResponse>(await devices.ListAsync(await http.CallerAsync(ct), ct), null)))
            .RequireAuthorization()
            .WithName("listDevices");

        group.MapGet("/{deviceId:guid}", async (
            Guid deviceId, DeviceService devices, HttpContext http, CancellationToken ct) =>
            Results.Ok(await devices.GetAsync(await http.CallerAsync(ct), deviceId, ct)))
            .RequireAuthorization()
            .WithName("getDevice");

        group.MapPatch("/{deviceId:guid}", async (
            Guid deviceId, UpdateDeviceRequest request, DeviceService devices, HttpContext http, CancellationToken ct) =>
            Results.Ok(await devices.UpdateAsync(await http.CallerAsync(ct), deviceId, request, ct)))
            .RequireAuthorization()
            .WithName("updateDevice")
            .WithSummary("Rename a device or change which commands it accepts.");

        group.MapDelete("/{deviceId:guid}", async (
            Guid deviceId, DeviceService devices, HttpContext http, CancellationToken ct) =>
        {
            await devices.RevokeAsync(await http.CallerAsync(ct), deviceId, http.RequestContext(), ct);
            return Results.NoContent();
        })
            .RequireAuthorization()
            .WithName("revokeDevice")
            .WithSummary("Unpair a device: its credential, sessions and pending commands all end.");

        // ── pairing ──────────────────────────────────────────────────────────
        // There is no POST /v2/devices. A device comes into being only through a
        // code the account owner confirms (C-2, closes S1-08).

        group.MapPost("/pair/start", async (
            PairStartRequest request, DeviceService devices, HttpContext http, CancellationToken ct) =>
            Results.Ok(await devices.StartPairingAsync(request, http.RequestContext(), ct)))
            .AllowAnonymous()
            .WithName("startPairing")
            .WithSummary("Agent-initiated. Returns a code for the user to confirm in the app.");

        group.MapPost("/pair/claim", async (
            PairClaimRequest request, DeviceService devices, HttpContext http, CancellationToken ct) =>
            Results.Ok(await devices.ClaimPairingAsync(await http.CallerAsync(ct), request, http.RequestContext(), ct)))
            .RequireAuthorization()
            .WithName("claimPairing")
            .WithSummary("User-initiated. Confirms the code and creates the device.");

        group.MapPost("/pair/poll", async (
            PairPollRequest request, DeviceService devices, HttpContext http, CancellationToken ct) =>
            Results.Ok(await devices.PollPairingAsync(request, http.RequestContext(), ct)))
            .AllowAnonymous()
            .WithName("pollPairing")
            .WithSummary("Agent collects its device id and secret. The secret is returned exactly once.");

        group.MapPost("/token", async (
            DeviceTokenRequest request, DeviceService devices, HttpContext http, CancellationToken ct) =>
            Results.Ok(await devices.IssueDeviceTokenAsync(request, http.RequestContext(), ct)))
            .AllowAnonymous()
            .WithName("issueDeviceToken")
            .WithSummary("Agent exchanges its device secret for a token scoped to command:receive and command:ack.");

        group.MapPost("/{deviceId:guid}/heartbeat", async (
            Guid deviceId, HeartbeatRequest request, DeviceService devices, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            if (caller.DevicePublicId != deviceId)
            {
                // A device may only report its own liveness.
                throw PCConnect.Core.AppException.NotFound(
                    PCConnect.Core.ErrorCodes.DeviceNotFound, "No such device.");
            }

            await devices.HeartbeatAsync(caller, request, ct);
            return Results.NoContent();
        })
            .RequireAuthorization()
            .WithName("deviceHeartbeat")
            .WithSummary("Durable liveness, coalesced to at most one write per minute.");

        return app;
    }
}
