using Microsoft.AspNetCore.Mvc;
using PCConnect.Api.Security;
using PCConnect.Contracts.V2;
using PCConnect.Infrastructure.Accounts;

namespace PCConnect.Api.Endpoints;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/me", async (HttpContext context, IAccountService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetProfileAsync(context.User.UserId(), cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Account");

        endpoints.MapPatch("/me", async ([FromBody] ProfileUpdate request, [FromHeader(Name = "X-Step-Up-Grant")] string grant,
            HttpContext context, IAccountService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.UpdateProfileAsync(context.User.UserId(), context.User.SessionId(), grant, request, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Account");

        endpoints.MapPost("/auth/password/change", async ([FromBody] PasswordChangeRequest request, [FromHeader(Name = "X-Step-Up-Grant")] string grant,
            HttpContext context, IAccountService service, CancellationToken cancellationToken) =>
        {
            await service.ChangePasswordAsync(context.User.UserId(), context.User.SessionId(), grant, request, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("Controller").WithTags("Identity");

        endpoints.MapPost("/auth/password/forgot", async ([FromBody] EmailRequest request, IAccountService service, CancellationToken cancellationToken) =>
        {
            await service.RequestPasswordResetAsync(request.Email, cancellationToken);
            return Results.Accepted();
        }).AllowAnonymous().RequireRateLimiting("authentication").WithTags("Identity");

        endpoints.MapPost("/auth/password/reset", async ([FromBody] PasswordResetRequest request, IAccountService service, CancellationToken cancellationToken) =>
        {
            await service.ResetPasswordAsync(new TokenRequest(request.Token), request.NewPassword, cancellationToken);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("authentication").WithTags("Identity");

        endpoints.MapPost("/auth/email/verify", async ([FromBody] TokenRequest request, IAccountService service, CancellationToken cancellationToken) =>
        {
            await service.VerifyEmailAsync(request.Token, cancellationToken);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("authentication").WithTags("Identity");

        endpoints.MapGet("/sessions", async (string? cursor, int? limit, HttpContext context, IAccountService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListSessionsAsync(context.User.UserId(), context.User.SessionId(), cursor, limit ?? 50, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Sessions");

        endpoints.MapDelete("/sessions/{sessionId:guid}", async (Guid sessionId, HttpContext context, IAccountService service, CancellationToken cancellationToken) =>
        {
            await service.RevokeSessionAsync(context.User.UserId(), context.User.SessionId(), sessionId, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("Controller").WithTags("Sessions");

        endpoints.MapPost("/me/export", async ([FromHeader(Name = "X-Step-Up-Grant")] string grant, HttpContext context, IAccountService service, CancellationToken cancellationToken) =>
            Results.Accepted(value: await service.RequestExportAsync(context.User.UserId(), context.User.SessionId(), grant, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Account");

        endpoints.MapGet("/me/export/{exportId:guid}", async (Guid exportId, HttpContext context, IAccountService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetExportAsync(context.User.UserId(), exportId, cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Account");

        endpoints.MapGet("/me/export/{exportId:guid}/download", async (Guid exportId, HttpContext context, IAccountService service, CancellationToken cancellationToken) =>
        {
            var content = await service.GetExportContentAsync(context.User.UserId(), exportId, cancellationToken);
            return Results.File(content, "application/json", $"pcconnect-export-{exportId:D}.json");
        }).RequireAuthorization("Controller").WithTags("Account");

        endpoints.MapDelete("/me", async ([FromHeader(Name = "X-Step-Up-Grant")] string grant, HttpContext context, IAccountService service, CancellationToken cancellationToken) =>
        {
            await service.RequestDeletionAsync(context.User.UserId(), context.User.SessionId(), grant, cancellationToken);
            return Results.Accepted();
        }).RequireAuthorization("Controller").WithTags("Account");
        return endpoints;
    }
}
