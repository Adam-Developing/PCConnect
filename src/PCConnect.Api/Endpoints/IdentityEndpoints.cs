using Microsoft.AspNetCore.Mvc;
using PCConnect.Api.Security;
using PCConnect.Contracts.V2;
using PCConnect.Infrastructure.Identity;

namespace PCConnect.Api.Endpoints;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/auth").WithTags("Identity");
        auth.MapPost("/register", async ([FromBody] RegistrationRequest request, HttpContext context, IAuthenticationService service, CancellationToken cancellationToken) =>
        {
            await service.RegisterAsync(request, ProblemMiddleware.CorrelationId(context), cancellationToken);
            return Results.Accepted();
        }).AllowAnonymous().RequireRateLimiting("authentication");

        auth.MapPost("/password/login", async ([FromBody] PasswordLoginRequest request, HttpContext context, IAuthenticationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.PasswordLoginAsync(request, context.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0", ProblemMiddleware.CorrelationId(context), cancellationToken)))
            .AllowAnonymous().RequireRateLimiting("authentication");

        auth.MapPost("/refresh", async ([FromBody] RefreshRequest request, HttpContext context, IAuthenticationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.RefreshUserSessionAsync(request.RefreshToken, ProblemMiddleware.CorrelationId(context), cancellationToken)))
            .AllowAnonymous().RequireRateLimiting("refresh");

        auth.MapPost("/logout", async (HttpContext context, IAuthenticationService service, CancellationToken cancellationToken) =>
        {
            await service.LogoutAsync(context.User.SessionId(), ProblemMiddleware.CorrelationId(context), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("Controller");

        endpoints.MapPost("/agent/auth/refresh", async ([FromBody] RefreshRequest request, HttpContext context, IAuthenticationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.RefreshDeviceAsync(request.RefreshToken, ProblemMiddleware.CorrelationId(context), cancellationToken)))
            .AllowAnonymous().RequireRateLimiting("refresh").WithTags("Agent");
        return endpoints;
    }
}
