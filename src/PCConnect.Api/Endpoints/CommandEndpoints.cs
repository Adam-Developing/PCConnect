using Microsoft.AspNetCore.Mvc;
using PCConnect.Api.Security;
using PCConnect.Contracts.V2;
using PCConnect.Infrastructure.Commands;
using PCConnect.Infrastructure.Identity;

namespace PCConnect.Api.Endpoints;

public static class CommandEndpoints
{
    public static IEndpointRouteBuilder MapCommandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/step-up/options", async ([FromBody] StepUpIntent request, HttpContext context, IStepUpService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.CreateOptionsAsync(context.User.UserId(), context.User.SessionId(), request, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Identity");

        endpoints.MapPost("/auth/step-up/complete", async ([FromBody] StepUpCompletion request, HttpContext context, IStepUpService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.CompleteAsync(context.User.UserId(), context.User.SessionId(), request, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Identity");

        endpoints.MapPost("/devices/{deviceId:guid}/commands", async (
            Guid deviceId, [FromBody] CommandCreate request, [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey,
            [FromHeader(Name = "X-Step-Up-Grant")] string? stepUpGrant, HttpContext context, ICommandService service, CancellationToken cancellationToken) =>
        {
            var command = await service.CreateAsync(context.User.UserId(), context.User.SessionId(), deviceId, idempotencyKey, stepUpGrant, request, ProblemMiddleware.CorrelationId(context), cancellationToken);
            return Results.Accepted($"/api/v2/commands/{command.Id:D}", command);
        })
            .RequireAuthorization("Controller").RequireRateLimiting("commands").WithTags("Commands");

        endpoints.MapGet("/commands/{commandId:guid}", async (Guid commandId, HttpContext context, ICommandService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(context.User.UserId(), commandId, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Commands");

        endpoints.MapGet("/commands", async (string? cursor, int? limit, HttpContext context, ICommandService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListForUserAsync(context.User.UserId(), cursor, limit ?? 50, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Commands");

        endpoints.MapPost("/commands/{commandId:guid}/cancel", async (Guid commandId, HttpContext context, ICommandService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.CancelAsync(context.User.UserId(), context.User.SessionId(), commandId, ProblemMiddleware.CorrelationId(context), cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Commands");

        endpoints.MapGet("/agent/commands", async (string? cursor, int? limit, HttpContext context, ICommandService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListPendingAsync(context.User.DeviceId(), cursor, limit ?? 50, cancellationToken)))
            .RequireAuthorization("Device").WithTags("Agent");

        endpoints.MapPost("/agent/commands/{commandId:guid}/claim", async (Guid commandId, [FromBody] CommandClaim request, HttpContext context, ICommandService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ClaimAsync(context.User.DeviceId(), commandId, request, cancellationToken)))
            .RequireAuthorization("Device").WithTags("Agent");

        endpoints.MapPost("/agent/commands/{commandId:guid}/acknowledgements", async (Guid commandId, [FromBody] CommandAcknowledgement request, HttpContext context, ICommandService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.AcknowledgeAsync(context.User.DeviceId(), commandId, request, cancellationToken)))
            .RequireAuthorization("Device").WithTags("Agent");
        return endpoints;
    }
}
