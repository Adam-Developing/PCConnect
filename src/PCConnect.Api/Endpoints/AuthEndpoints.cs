using Microsoft.AspNetCore.Mvc;
using PCConnect.Api.Auth;
using PCConnect.Api.Http;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Infrastructure.Contexts.Identity;

namespace PCConnect.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v2/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest request, IdentityService identity, HttpContext http, CancellationToken ct) =>
            Results.Created("/v2/account/profile", await identity.RegisterAsync(request, http.RequestContext(), ct)))
            .AllowAnonymous()
            .WithName("register")
            .WithSummary("Create an account and return a token pair.");

        group.MapPost("/login", async (
            LoginRequest request, IdentityService identity, HttpContext http, CancellationToken ct) =>
            Results.Ok(await identity.LoginAsync(request, http.RequestContext(), ct)))
            .AllowAnonymous()
            .WithName("login")
            .WithSummary("Exchange a password for a token pair.")
            .WithDescription(
                "Takes the plaintext password over TLS. `legacyPasswordHash` exists only for the " +
                "installed clients that pre-hash with unsalted SHA-256 and is removed at the sunset date.");

        group.MapPost("/refresh", async (
            RefreshRequest request, IdentityService identity, HttpContext http, CancellationToken ct) =>
            Results.Ok(await identity.RefreshAsync(request.RefreshToken, http.RequestContext(), ct)))
            .AllowAnonymous()
            .WithName("refresh")
            .WithSummary("Rotate a refresh token. Presenting a revoked token revokes its whole family.");

        group.MapPost("/logout", async (
            LogoutRequest request, IdentityService identity, HttpContext http, CancellationToken ct) =>
        {
            await identity.LogoutAsync(request.RefreshToken, http.RequestContext(), ct);
            return Results.NoContent();
        })
            .AllowAnonymous()
            .WithName("logout");

        group.MapPost("/logout-all", async (IdentityService identity, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            caller.Require(Core.Domain.Scopes.AccountManage);
            await identity.LogoutAllAsync(caller.UserId, http.RequestContext(), ct);
            return Results.NoContent();
        })
            .RequireAuthorization()
            .WithName("logoutAll");

        group.MapPost("/password/change", async (
            ChangePasswordRequest request, IdentityService identity, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            caller.Require(Core.Domain.Scopes.AccountManage);
            await identity.ChangePasswordAsync(caller.UserId, request, http.RequestContext(), ct);
            return Results.NoContent();
        })
            .RequireAuthorization()
            .WithName("changePassword")
            .WithSummary("Change the password. Requires the current one and ends every session.");

        group.MapPost("/password/forgot", async (
            ForgotPasswordRequest request, IdentityService identity, HttpContext http, CancellationToken ct) =>
        {
            await identity.ForgotPasswordAsync(request.Email, http.RequestContext(), ct);
            // Always 202: whether the account exists is not something this
            // endpoint is willing to tell an anonymous caller.
            return Results.Accepted();
        })
            .AllowAnonymous()
            .WithName("forgotPassword");

        group.MapPost("/password/reset", async (
            ResetPasswordRequest request, IdentityService identity, HttpContext http, CancellationToken ct) =>
        {
            await identity.ResetPasswordAsync(request.Token, request.NewPassword, http.RequestContext(), ct);
            return Results.NoContent();
        })
            .AllowAnonymous()
            .WithName("resetPassword");

        group.MapPost("/email/verify", async (
            VerifyEmailRequest request, IdentityService identity, HttpContext http, CancellationToken ct) =>
        {
            await identity.VerifyEmailAsync(request.Token, http.RequestContext(), ct);
            return Results.NoContent();
        })
            .AllowAnonymous()
            .WithName("verifyEmail");

        MapPasskeys(group);
        MapStepUp(group);

        return app;
    }

    private static void MapPasskeys(RouteGroupBuilder group)
    {
        var passkeys = group.MapGroup("/passkeys").WithTags("Passkeys");

        passkeys.MapGet("", async (WebAuthnService webAuthn, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            return Results.Ok(await webAuthn.ListAsync(caller.UserId, ct));
        })
            .RequireAuthorization()
            .WithName("listPasskeys");

        passkeys.MapPost("/register/start", async (WebAuthnService webAuthn, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            caller.Require(Core.Domain.Scopes.AccountManage);
            return Results.Ok(await webAuthn.BeginRegistrationAsync(caller.UserId, ct));
        })
            .RequireAuthorization()
            .WithName("beginPasskeyRegistration");

        passkeys.MapPost("/register/finish", async (
            PasskeyRegistrationRequest request, WebAuthnService webAuthn, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            caller.Require(Core.Domain.Scopes.AccountManage);
            return Results.Ok(await webAuthn.CompleteRegistrationAsync(caller.UserId, request, http.RequestContext(), ct));
        })
            .RequireAuthorization()
            .WithName("completePasskeyRegistration");

        passkeys.MapDelete("/{passkeyId:guid}", async (
            Guid passkeyId, WebAuthnService webAuthn, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            caller.Require(Core.Domain.Scopes.AccountManage);
            await webAuthn.RevokeAsync(caller.UserId, passkeyId, http.RequestContext(), ct);
            return Results.NoContent();
        })
            .RequireAuthorization()
            .WithName("revokePasskey");

        // Passwordless sign-in. The account is identified by the credential, so
        // the caller does not have to reveal a username to an anonymous endpoint.
        passkeys.MapPost("/assert/start", async (
            [FromQuery] string? login, WebAuthnService webAuthn, IdentityService identity, CancellationToken ct) =>
        {
            _ = identity;
            _ = login;
            return Results.Ok(await webAuthn.BeginAssertionAsync(null, "authentication", ct));
        })
            .AllowAnonymous()
            .WithName("beginPasskeyAssertion");

        passkeys.MapPost("/assert/finish", async (
            PasskeyAssertionRequest request,
            WebAuthnService webAuthn,
            IdentityService identity,
            HttpContext http,
            CancellationToken ct) =>
        {
            var ctx = http.RequestContext();
            var result = await webAuthn.CompleteAssertionAsync(request, "authentication", null, ctx, ct);
            var tokens = await identity.IssueSessionForVerifiedUserAsync(
                result.UserId, request.ClientKind, request.ClientVersion, ctx, ct);
            return Results.Ok(tokens);
        })
            .AllowAnonymous()
            .WithName("completePasskeyAssertion");
    }

    private static void MapStepUp(RouteGroupBuilder group)
    {
        var stepUp = group.MapGroup("/step-up").WithTags("Step-up");

        stepUp.MapPost("/start", async (StepUpService service, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            return Results.Ok(await service.BeginAsync(caller, ct));
        })
            .RequireAuthorization()
            .WithName("beginStepUp")
            .WithSummary("Start a confirmation challenge for a destructive command.");

        stepUp.MapPost("/verify", async (
            StepUpVerifyRequest request, StepUpService service, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            return Results.Ok(await service.VerifyAsync(caller, request, http.RequestContext(), ct));
        })
            .RequireAuthorization()
            .WithName("verifyStepUp")
            .WithSummary("Exchange a passkey assertion or password for a single-use confirmation token.");
    }
}
