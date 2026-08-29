using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PCConnect.Api;
using PCConnect.Infrastructure.Identity;

namespace PCConnect.Api.Security;

public static class PCConnectClaimTypes
{
    public const string SubjectKind = "pcconnect:subject-kind";
    public const string SessionId = "pcconnect:session-id";
    public const string DeviceId = "pcconnect:device-id";
}

public sealed class OpaqueAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PCConnect.Infrastructure.Identity.IAuthenticationService authenticationService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var value = Request.Headers.Authorization.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var raw = value[7..].Trim();
        var subject = await authenticationService.AuthenticateAccessTokenAsync(raw, Context.RequestAborted);
        if (subject is null) return AuthenticateResult.Fail("Invalid access token.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, (subject.UserId ?? subject.DeviceId)!.Value.ToString("D")),
            new(PCConnectClaimTypes.SubjectKind, subject.SubjectKind)
        };
        if (subject.UserId is { } userId) claims.Add(new("sub", userId.ToString("D")));
        if (subject.SessionId is { } sessionId) claims.Add(new(PCConnectClaimTypes.SessionId, sessionId.ToString("D")));
        if (subject.DeviceId is { } deviceId) claims.Add(new(PCConnectClaimTypes.DeviceId, deviceId.ToString("D")));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ProblemMiddleware.WriteProblemAsync(
            Context,
            StatusCodes.Status401Unauthorized,
            "authentication_required",
            "Authentication required");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ProblemMiddleware.WriteProblemAsync(
            Context,
            StatusCodes.Status403Forbidden,
            "forbidden",
            "Forbidden");
}

public static class ClaimsPrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue("sub")!);
    public static Guid SessionId(this ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(PCConnectClaimTypes.SessionId)!);
    public static Guid DeviceId(this ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(PCConnectClaimTypes.DeviceId)!);
}
