using Microsoft.AspNetCore.Mvc;
using PCConnect.Api.Security;
using PCConnect.Contracts.V2;
using PCConnect.Infrastructure.Devices;

namespace PCConnect.Api.Endpoints;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/device-enrollments", async ([FromBody] DeviceEnrollmentRequest request, IDeviceService service, CancellationToken cancellationToken) =>
            Results.Created("/device-enrollments/token", await service.CreateEnrollmentAsync(request, cancellationToken)))
            .AllowAnonymous().RequireRateLimiting("enrollment").WithTags("Devices");

        endpoints.MapPost("/device-enrollments/{userCode}/approve", async (string userCode, HttpContext context, IDeviceService service, CancellationToken cancellationToken) =>
        {
            await service.ApproveEnrollmentAsync(context.User.UserId(), userCode, ProblemMiddleware.CorrelationId(context), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("Controller").WithTags("Devices");

        endpoints.MapPost("/device-enrollments/token", async ([FromBody] DeviceCodeRequest request, HttpContext context, IDeviceService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ExchangeDeviceCodeAsync(request.DeviceCode, ProblemMiddleware.CorrelationId(context), cancellationToken)))
            .AllowAnonymous().RequireRateLimiting("enrollment").WithTags("Devices");

        endpoints.MapGet("/devices", async (string? cursor, int? limit, HttpContext context, IDeviceService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(context.User.UserId(), cursor, limit ?? 50, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Devices");

        endpoints.MapGet("/devices/{deviceId:guid}", async (Guid deviceId, HttpContext context, IDeviceService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(context.User.UserId(), deviceId, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Devices");

        endpoints.MapPatch("/devices/{deviceId:guid}", async (Guid deviceId, [FromBody] DeviceUpdate request, HttpContext context, IDeviceService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.UpdateAsync(context.User.UserId(), deviceId, request, ProblemMiddleware.CorrelationId(context), cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Devices");

        endpoints.MapDelete("/devices/{deviceId:guid}", async (Guid deviceId, [FromHeader(Name = "X-Step-Up-Grant")] string stepUpGrant, HttpContext context, IDeviceService service, CancellationToken cancellationToken) =>
        {
            await service.RevokeAsync(context.User.UserId(), context.User.SessionId(), deviceId, stepUpGrant, ProblemMiddleware.CorrelationId(context), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("Controller").WithTags("Devices");

        endpoints.MapPost("/agent/heartbeat", async ([FromBody] Heartbeat request, HttpContext context, IDeviceService service, CancellationToken cancellationToken) =>
        {
            await service.HeartbeatAsync(context.User.DeviceId(), request, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("Device").WithTags("Agent");

        endpoints.MapPost("/agent/windows-sid-candidates", async ([FromBody] WindowsSidCandidateRequest request, HttpContext context, IDeviceService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.RegisterSidCandidateAsync(context.User.DeviceId(), request, cancellationToken)))
            .RequireAuthorization("Device").WithTags("Agent");

        endpoints.MapGet("/devices/{deviceId:guid}/windows-sids", async (Guid deviceId, HttpContext context, IDeviceService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListWindowsSidsAsync(context.User.UserId(), deviceId, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Devices");

        endpoints.MapPost("/devices/{deviceId:guid}/windows-sids/{windowsSid}/authorize", async (
            Guid deviceId, string windowsSid, [FromHeader(Name = "X-Step-Up-Grant")] string grant,
            HttpContext context, IDeviceService service, CancellationToken cancellationToken) =>
        {
            await service.AuthorizeWindowsSidAsync(context.User.UserId(), context.User.SessionId(), deviceId, windowsSid, grant, ProblemMiddleware.CorrelationId(context), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("Controller").WithTags("Devices");

        endpoints.MapDelete("/devices/{deviceId:guid}/windows-sids/{windowsSid}", async (
            Guid deviceId, string windowsSid, [FromHeader(Name = "X-Step-Up-Grant")] string grant,
            HttpContext context, IDeviceService service, CancellationToken cancellationToken) =>
        {
            await service.RevokeWindowsSidAsync(context.User.UserId(), context.User.SessionId(), deviceId, windowsSid, grant, ProblemMiddleware.CorrelationId(context), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("Controller").WithTags("Devices");
        return endpoints;
    }
}
