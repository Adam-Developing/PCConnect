using System.Globalization;
using System.Text.Json;
using PCConnect.Infrastructure.Compatibility;
using PCConnect.Infrastructure.Identity;

namespace PCConnect.Api.Endpoints;

public static class LegacyCompatibilityEndpoints
{
    public static IEndpointRouteBuilder MapLegacyCompatibilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ping", () => Results.Json("Pong")).AllowAnonymous();
        endpoints.MapGet("/api/v1/system/checkinternet", () => Results.Json("Pong")).AllowAnonymous();
        endpoints.MapGet("/api/pcconnect/checkinternet.php", () => Results.Text("yes", "text/plain")).AllowAnonymous();
        endpoints.MapGet("/api/time.php", () => Results.Json(new { time = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) })).AllowAnonymous();

        endpoints.MapGet("/api/v1/devices", ListDevicesAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapGet("/api/pcconnect/PCNames.php", ListDeviceNamesDirectAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapGet("/api/pcclient/PCNames.php", ListDeviceNamesDirectAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapPost("/api/v1/devices", LegacyEnrollmentDisabled).AllowAnonymous();
        endpoints.MapGet("/api/pcclient/addpc.php", LegacyEnrollmentDisabled).AllowAnonymous();

        endpoints.MapPost("/api/v1/devices/requests/exchange", ExchangeAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapPost("/api/pcconnect/exchange.php", ExchangeDirectAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapGet("/api/v1/devices/requests", PollAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapPost("/api/v1/devices/requests/clear", ClearAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapGet("/api/pcclient/findrequests.php", PollDirectAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapGet("/api/pcclient/updaterequest.php", ClearDirectAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapPost("/api/pcclient/updatepctimedatabase.php", TouchDirectAsync).AllowAnonymous().RequireRateLimiting("compatibility");

        endpoints.MapGet("/api/v1/reminders", ListRemindersAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapGet("/api/pcconnect/listreminders.php", ListRemindersDirectAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapGet("/api/pcclient/listreminders.php", ListRemindersDirectAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapPost("/api/pcconnect/reminder.php", CreateReminderDirectAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapPost("/api/pcclient/reminder.php", CreateReminderDirectAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapGet("/api/pcclient/getreminder.php", GetDueReminderDirectAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapPost("/api/pcclient/completereminder.php", CompleteReminderDirectAsync).AllowAnonymous().RequireRateLimiting("compatibility");
        endpoints.MapPost("/api/login.php", LegacyLoginDisabled).AllowAnonymous();
        return endpoints;
    }

    private static IResult LegacyEnrollmentDisabled() => throw new ResourceGoneException("legacy_enrollment_disabled");
    private static IResult LegacyLoginDisabled() => throw new ResourceGoneException("legacy_login_disabled");

    private static async Task<IResult> ListDevicesAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken) =>
        Results.Json(Success(new { PCNames = await service.ListDevicesAsync(ApiKey(context), cancellationToken) }));

    private static async Task<IResult> ListDeviceNamesDirectAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken) =>
        Results.Json(new { PCNames = await service.ListDevicesAsync(ApiKey(context), cancellationToken) });

    private static async Task<IResult> ExchangeAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken)
    {
        var request = await RequestValueAsync(context, cancellationToken);
        await service.CreateCommandAsync(ApiKey(context), DeviceName(context), request, IdempotencyKey(context), ProblemMiddleware.CorrelationId(context), cancellationToken);
        return Results.Json(Success(new { message = "Success" }));
    }

    private static async Task<IResult> ExchangeDirectAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken)
    {
        var request = await RequestValueAsync(context, cancellationToken);
        await service.CreateCommandAsync(ApiKey(context), DeviceName(context), request, IdempotencyKey(context), ProblemMiddleware.CorrelationId(context), cancellationToken);
        return Results.Json(new { success = true });
    }

    private static async Task<IResult> PollAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken) =>
        Results.Json(Success(new { request = await service.PollCommandAsync(ApiKey(context), DeviceName(context), cancellationToken) }));

    private static async Task<IResult> ClearAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken)
    {
        await service.ClearCommandAsync(ApiKey(context), DeviceName(context), cancellationToken);
        return Results.Json(Success(new { message = "Request cleared" }));
    }

    private static async Task<IResult> PollDirectAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken) =>
        Results.Text(await service.PollCommandAsync(ApiKey(context), DeviceName(context), cancellationToken) ?? string.Empty, "text/plain");

    private static async Task<IResult> ClearDirectAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken)
    {
        await service.ClearCommandAsync(ApiKey(context), DeviceName(context), cancellationToken);
        return Results.Text("Request cleared\n", "text/plain");
    }

    private static async Task<IResult> TouchDirectAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken)
    {
        await service.TouchDeviceAsync(ApiKey(context), DeviceName(context), cancellationToken);
        return Results.Text("Time updated successfully\n", "text/plain");
    }

    private static async Task<IResult> ListRemindersAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken)
    {
        var reminders = await service.ListRemindersAsync(ApiKey(context), cancellationToken);
        return Results.Json(Success(reminders.Select(ToLegacyReminder)));
    }

    private static async Task<IResult> ListRemindersDirectAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken)
    {
        var reminders = await service.ListRemindersAsync(ApiKey(context), cancellationToken);
        return Results.Json(reminders.Select(ToLegacyReminder));
    }

    private static async Task<IResult> CreateReminderDirectAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType) throw new ArgumentException("Legacy reminder writes require form data.");
        var form = await context.Request.ReadFormAsync(cancellationToken);
        await service.CreateReminderAsync(ApiKey(context), form["date"].ToString(), form["time"].ToString(), form["reminder"].ToString(),
            IdempotencyKey(context), ProblemMiddleware.CorrelationId(context), cancellationToken);
        return Results.Text("Reminder inserted successfully!\n", "text/plain");
    }

    private static async Task<IResult> GetDueReminderDirectAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken)
    {
        var reminder = await service.GetDueReminderAsync(ApiKey(context), DeviceName(context), cancellationToken);
        return reminder is null
            ? Results.Json(new { })
            : Results.Json(new { id = reminder.LegacyId, date = reminder.Date, time = reminder.Time, reminder = reminder.Text });
    }

    private static async Task<IResult> CompleteReminderDirectAsync(HttpContext context, ILegacyCompatibilityService service, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType) throw new ArgumentException("Legacy reminder completion requires form data.");
        var form = await context.Request.ReadFormAsync(cancellationToken);
        if (!long.TryParse(form["id"], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException("Legacy reminder id is invalid.");
        await service.CompleteReminderAsync(ApiKey(context), id, ProblemMiddleware.CorrelationId(context), cancellationToken);
        return Results.Text("Reminder completed successfully!\n", "text/plain");
    }

    private static object ToLegacyReminder(PCConnect.Contracts.V2.Reminder reminder) => new
    {
        Date = reminder.LocalStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Time = reminder.LocalStart.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        Reminder = reminder.Text,
        Completed = 0
    };

    private static object Success(object data) => new { success = true, data };

    private static string ApiKey(HttpContext context) => context.Request.Headers["X-API-Key"].ToString();
    private static string DeviceName(HttpContext context) => context.Request.Headers["PCName"].ToString();
    private static Guid? IdempotencyKey(HttpContext context) => Guid.TryParse(context.Request.Headers["Idempotency-Key"], out var value) ? value : null;

    private static async Task<string> RequestValueAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.HasFormContentType)
            return (await context.Request.ReadFormAsync(cancellationToken))["Request"].ToString();
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("Request", out var upper) ? upper.GetString() ?? string.Empty
            : document.RootElement.TryGetProperty("request", out var lower) ? lower.GetString() ?? string.Empty
            : string.Empty;
    }
}
