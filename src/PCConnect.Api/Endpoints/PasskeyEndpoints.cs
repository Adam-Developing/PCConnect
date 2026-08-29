using Microsoft.AspNetCore.Mvc;
using PCConnect.Api.Security;
using PCConnect.Contracts.V2;
using PCConnect.Infrastructure.Identity;

namespace PCConnect.Api.Endpoints;

public static class PasskeyEndpoints
{
    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/passkeys/registration/options", async (HttpContext context, IPasskeyService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.CreateRegistrationOptionsAsync(context.User.UserId(), context.User.SessionId(), cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Identity");

        endpoints.MapPost("/auth/passkeys/registration/complete", async ([FromBody] WebAuthnCredential request,
            [FromHeader(Name = "X-Step-Up-Grant")] string grant, HttpContext context, IPasskeyService service, CancellationToken cancellationToken) =>
        {
            var passkey = await service.CompleteRegistrationAsync(context.User.UserId(), context.User.SessionId(), grant, request, cancellationToken);
            return Results.Created($"/api/v2/passkeys/{passkey.Id:D}", passkey);
        }).RequireAuthorization("Controller").WithTags("Identity");

        endpoints.MapPost("/auth/passkeys/authentication/options", async ([FromBody] PasskeyAuthenticationOptionsRequest request,
            IPasskeyService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.CreateAuthenticationOptionsAsync(request, cancellationToken)))
            .AllowAnonymous().RequireRateLimiting("authentication").WithTags("Identity");

        endpoints.MapPost("/auth/passkeys/authentication/complete", async ([FromBody] WebAuthnCredential request,
            HttpContext context, IPasskeyService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.CompleteAuthenticationAsync(request, ProblemMiddleware.CorrelationId(context), cancellationToken)))
            .AllowAnonymous().RequireRateLimiting("authentication").WithTags("Identity");

        endpoints.MapGet("/passkeys", async (HttpContext context, IPasskeyService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(context.User.UserId(), cancellationToken)))
            .RequireAuthorization("Controller").WithTags("Identity");

        endpoints.MapDelete("/passkeys/{passkeyId:guid}", async (Guid passkeyId, [FromHeader(Name = "X-Step-Up-Grant")] string grant,
            HttpContext context, IPasskeyService service, CancellationToken cancellationToken) =>
        {
            await service.RemoveAsync(context.User.UserId(), context.User.SessionId(), passkeyId, grant, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("Controller").WithTags("Identity");
        return endpoints;
    }
}
