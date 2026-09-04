using PCConnect.Core;
using PCConnect.Infrastructure.Realtime;

namespace PCConnect.Api.Auth;

/// <summary>
/// Resolves the caller once per request and caches it on the context, so a
/// handler taking a <see cref="CallerIdentity"/> does not cause a second lookup.
/// </summary>
public static class CallerExtensions
{
    private const string ItemKey = "pcconnect.caller";

    public static async Task<CallerIdentity> CallerAsync(this HttpContext context, CancellationToken ct = default)
    {
        if (context.Items.TryGetValue(ItemKey, out var cached) && cached is CallerIdentity caller)
        {
            return caller;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            throw AppException.Unauthorized(ErrorCodes.AuthTokenInvalid, "Sign in to continue.");
        }

        var resolver = context.RequestServices.GetRequiredService<CallerResolver>();
        var resolved = await resolver.ResolveAsync(context.User, ct);
        context.Items[ItemKey] = resolved;
        return resolved;
    }
}
