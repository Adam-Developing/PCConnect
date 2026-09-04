using Microsoft.IdentityModel.JsonWebTokens;
using PCConnect.Api.Auth;
using PCConnect.Api.Http;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Core.Domain;
using PCConnect.Infrastructure.Contexts.Commands;
using PCConnect.Infrastructure.Contexts.Devices;
using PCConnect.Infrastructure.Contexts.Identity;
using PCConnect.Infrastructure.Contexts.Reminders;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Api.Endpoints;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v2/account").WithTags("Account").RequireAuthorization();

        group.MapGet("/profile", async (IdentityService identity, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            var profile = await identity.GetProfileAsync(caller.UserId, ct);
            return profile is null
                ? Results.NotFound()
                : Results.Ok(profile);
        })
            .WithName("getProfile");

        group.MapPatch("/profile", async (
            UpdateProfileRequest request, IdentityService identity, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            caller.Require(Scopes.AccountManage);
            return Results.Ok(await identity.UpdateProfileAsync(caller.UserId, request, ct));
        })
            .WithName("updateProfile")
            .WithSummary("Changing the password is not here: it lives at /v2/auth/password/change.");

        group.MapGet("/sessions", async (IdentityService identity, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            caller.Require(Scopes.AccountManage);
            var currentFamily = http.User.FindFirst(PcConnectClaims.FamilyId)?.Value;
            return Results.Ok(new Page<SessionResponse>(
                await identity.ListSessionsAsync(caller.UserId, currentFamily, ct), null));
        })
            .WithName("listSessions")
            .WithSummary("Live sessions, so a user can see and end one they do not recognise.");

        group.MapDelete("/sessions/{familyId:guid}", async (
            Guid familyId, IdentityService identity, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            caller.Require(Scopes.AccountManage);
            await identity.RevokeSessionFamilyAsync(caller.UserId, familyId, http.RequestContext(), ct);
            return Results.NoContent();
        })
            .WithName("revokeSession");

        group.MapGet("/export", async (
            IdentityService identity,
            DeviceService devices,
            ReminderService reminders,
            CommandService commands,
            IClock clock,
            HttpContext http,
            CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            caller.Require(Scopes.AccountManage);

            var profile = await identity.GetProfileAsync(caller.UserId, ct)
                ?? throw AppException.NotFound(ErrorCodes.AccountNotFound, "No such account.");

            var reminderPage = await reminders.ListAsync(caller, null, null, null, null, 200, ct);
            var commandPage = await commands.ListAsync(caller, null, 100, null, ct);

            return Results.Ok(new AccountExport(
                profile,
                await devices.ListAsync(caller, ct),
                reminderPage.Items,
                commandPage.Items,
                await identity.ListSessionsAsync(caller.UserId, null, ct),
                clock.UtcNow));
        })
            .WithName("exportAccount")
            .WithSummary("GDPR data export.");

        group.MapDelete("", async (IdentityService identity, HttpContext http, CancellationToken ct) =>
        {
            var caller = await http.CallerAsync(ct);
            caller.Require(Scopes.AccountManage);
            await identity.DeleteAccountAsync(caller.UserId, http.RequestContext(), ct);
            return Results.NoContent();
        })
            .WithName("deleteAccount")
            .WithSummary("Soft delete now; hard delete after 30 days, cascading to devices, commands and reminders.");

        return app;
    }
}
