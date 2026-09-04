using Microsoft.AspNetCore.Mvc;
using PCConnect.Api.Auth;
using PCConnect.Core.Contracts;
using PCConnect.Infrastructure.Contexts.Reminders;

namespace PCConnect.Api.Endpoints;

public static class ReminderEndpoints
{
    public static IEndpointRouteBuilder MapReminderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v2/reminders").WithTags("Reminders").RequireAuthorization();

        group.MapGet("", async (
            [FromQuery] DateTimeOffset? from,
            [FromQuery] DateTimeOffset? to,
            [FromQuery] bool? completed,
            [FromQuery] string? cursor,
            [FromQuery] int? limit,
            ReminderService reminders,
            HttpContext http,
            CancellationToken ct) =>
            Results.Ok(await reminders.ListAsync(
                await http.CallerAsync(ct), from, to, completed, cursor, limit ?? 50, ct)))
            .WithName("listReminders")
            .WithSummary("Times are UTC RFC 3339 plus the timezone the reminder was created in.");

        group.MapPost("", async (
            CreateReminderRequest request, ReminderService reminders, HttpContext http, CancellationToken ct) =>
        {
            var created = await reminders.CreateAsync(await http.CallerAsync(ct), request, ct);
            return Results.Created($"/v2/reminders/{created.Id}", created);
        })
            .WithName("createReminder");

        group.MapGet("/{reminderId:guid}", async (
            Guid reminderId, ReminderService reminders, HttpContext http, CancellationToken ct) =>
            Results.Ok(await reminders.GetAsync(await http.CallerAsync(ct), reminderId, ct)))
            .WithName("getReminder");

        group.MapPatch("/{reminderId:guid}", async (
            Guid reminderId, UpdateReminderRequest request, ReminderService reminders, HttpContext http, CancellationToken ct) =>
            Results.Ok(await reminders.UpdateAsync(await http.CallerAsync(ct), reminderId, request, ct)))
            .WithName("updateReminder");

        group.MapDelete("/{reminderId:guid}", async (
            Guid reminderId, ReminderService reminders, HttpContext http, CancellationToken ct) =>
        {
            await reminders.DeleteAsync(await http.CallerAsync(ct), reminderId, ct);
            return Results.NoContent();
        })
            .WithName("deleteReminder")
            .WithSummary("Soft delete; recoverable until the retention job runs.");

        group.MapPost("/{reminderId:guid}/complete", async (
            Guid reminderId, CompleteReminderRequest request, ReminderService reminders, HttpContext http, CancellationToken ct) =>
            Results.Ok(await reminders.CompleteAsync(await http.CallerAsync(ct), reminderId, request, ct)))
            .WithName("completeReminder")
            .WithSummary("`occurrenceAt` completes one occurrence of a series rather than the whole series.");

        return app;
    }
}
