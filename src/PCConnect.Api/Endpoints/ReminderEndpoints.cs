using Microsoft.AspNetCore.Mvc;
using PCConnect.Api.Security;
using PCConnect.Contracts.V2;
using PCConnect.Infrastructure.Reminders;

namespace PCConnect.Api.Endpoints;

public static class ReminderEndpoints
{
    public static IEndpointRouteBuilder MapReminderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/reminders", async (string? cursor, int? limit, HttpContext context, IReminderService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(context.User.UserId(), cursor, limit ?? 50, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Reminders");
        endpoints.MapPost("/reminders", async ([FromBody] ReminderWrite request, [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey, HttpContext context, IReminderService service, CancellationToken cancellationToken) =>
        {
            var reminder = await service.CreateAsync(context.User.UserId(), context.User.SessionId(), idempotencyKey, request, cancellationToken);
            return Results.Created($"/api/v2/reminders/{reminder.Id:D}", reminder);
        }).RequireAuthorization("Controller").WithTags("Reminders");
        endpoints.MapGet("/reminders/{reminderId:guid}", async (Guid reminderId, HttpContext context, IReminderService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(context.User.UserId(), reminderId, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Reminders");
        endpoints.MapPatch("/reminders/{reminderId:guid}", async (Guid reminderId, [FromBody] ReminderWrite request, HttpContext context, IReminderService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.UpdateAsync(context.User.UserId(), reminderId, request, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Reminders");
        endpoints.MapDelete("/reminders/{reminderId:guid}", async (Guid reminderId, HttpContext context, IReminderService service, CancellationToken cancellationToken) =>
        {
            await service.DeleteAsync(context.User.UserId(), reminderId, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("Controller").WithTags("Reminders");
        endpoints.MapGet("/agent/reminder-deliveries", async (string? cursor, int? limit, HttpContext context, IReminderService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAvailableDeliveriesAsync(context.User.DeviceId(), cursor, limit ?? 50, cancellationToken)))
            .RequireAuthorization("Device").WithTags("Agent");
        endpoints.MapPost("/reminder-deliveries/{deliveryId:guid}/acknowledgements", async (Guid deliveryId, [FromBody] ReminderAcknowledgement request, HttpContext context, IReminderService service, CancellationToken cancellationToken) =>
        {
            await service.AcknowledgeDeliveryAsync(context.User.DeviceId(), deliveryId, request, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("Device").WithTags("Agent");
        return endpoints;
    }
}
