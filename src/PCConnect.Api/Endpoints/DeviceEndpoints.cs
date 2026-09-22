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
            .WithSummary("Change a device's name, accepted commands, or password-confirmation rules.");

        group.MapDelete("/{deviceId:guid}", async (
            Guid deviceId, DeviceService devices, HttpContext http, CancellationToken ct) =>
        {
            await devices.RevokeAsync(await http.CallerAsync(ct), deviceId, http.RequestContext(), ct);
            return Results.NoContent();
        })
            .RequireAuthorization()
            .WithName("revokeDevice")
            .WithSummary("Unpair a device: its credential, sessions and pending commands all end.");

        group.MapPost("/provision", async (
            DeviceProvisionRequest request, DeviceService devices, HttpContext http, CancellationToken ct) =>
            Results.Ok(await devices.ProvisionAsync(await http.CallerAsync(ct), request, http.RequestContext(), ct)))
            .RequireAuthorization()
            .WithName("provisionDevice")
            .WithSummary("Adds the PC the caller is signed in on and returns a one-time ticket for its local agent.");

        group.MapPost("/provision/complete", async (
            DeviceProvisionCompleteRequest request, DeviceService devices, HttpContext http, CancellationToken ct) =>
            Results.Ok(await devices.CompleteProvisioningAsync(request, http.RequestContext(), ct)))
            .AllowAnonymous()
            .WithName("completeDeviceProvisioning")
            .WithSummary("The local agent redeems its one-time ticket and collects its credential.");

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
