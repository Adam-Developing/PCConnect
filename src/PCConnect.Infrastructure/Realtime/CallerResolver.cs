using System.Security.Claims;
using Dapper;
using Microsoft.IdentityModel.JsonWebTokens;
using PCConnect.Core;
using PCConnect.Core.Domain;
using PCConnect.Infrastructure.Caching;
using PCConnect.Infrastructure.Data;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Realtime;

/// <summary>
/// Turns a validated token into the <see cref="CallerIdentity"/> the contexts
/// work with. Every fact comes from a signed claim or from the database; nothing
/// is taken from a header the caller chose, which is the whole of S1-08.
/// </summary>
public sealed class CallerResolver(Db db, ICacheStore cache)
{
    public async Task<CallerIdentity> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti)
            ?? throw AppException.Unauthorized(ErrorCodes.AuthTokenInvalid, "That token is not valid.");

        // 15-minute tokens mean expiry handles most revocation; the deny list
        // covers the cases where that is too slow — device revoked, reuse
        // detected, password changed (03 §2.2).
        if (await cache.ExistsAsync(CacheKeys.DenyListedJti(jti), ct))
        {
            throw AppException.Unauthorized(ErrorCodes.AuthTokenInvalid, "That session has been ended.");
        }

        if (principal.FindFirstValue(PcConnectClaims.Purpose) == PcConnectClaims.StepUpPurpose)
        {
            // A step-up token confirms one action; it is not a session.
            throw AppException.Unauthorized(ErrorCodes.AuthTokenInvalid,
                "A confirmation token cannot be used as an access token.");
        }

        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(subject, out var userPublicId))
        {
            throw AppException.Unauthorized(ErrorCodes.AuthTokenInvalid, "That token is not valid.");
        }

        var clientKind = principal.FindFirstValue(PcConnectClaims.ClientKind) ?? ClientKinds.Mobile;
        var scopes = principal.FindAll(PcConnectClaims.Scopes).Select(c => c.Value).ToList();
        var deviceClaim = principal.FindFirstValue(PcConnectClaims.DeviceId);

        await using var connection = await db.OpenAsync(ct);

        var user = await connection.QuerySingleOrDefaultAsync<UserLookup>(new CommandDefinition(
            "SELECT id, status, deleted_at AS DeletedAt FROM users WHERE public_id = @PublicId",
            new { PublicId = userPublicId }, cancellationToken: ct));

        if (user is null || user.DeletedAt is not null || user.Status == UserStatuses.Suspended)
        {
            throw AppException.Unauthorized(ErrorCodes.AuthTokenInvalid, "That session is no longer valid.");
        }

        long? deviceId = null;
        Guid? devicePublicId = null;

        if (!string.IsNullOrEmpty(deviceClaim))
        {
            if (!Guid.TryParse(deviceClaim, out var parsedDeviceId))
            {
                throw AppException.Unauthorized(ErrorCodes.AuthTokenInvalid, "That token is not valid.");
            }

            // The device must still exist, still be active, and still belong to
            // the subject. A revoked device presenting an unexpired token is
            // rejected here rather than at its next command.
            var device = await connection.QuerySingleOrDefaultAsync<DeviceLookup>(new CommandDefinition("""
                SELECT id, status FROM devices WHERE public_id = @PublicId AND user_id = @UserId
                """, new { PublicId = parsedDeviceId, UserId = user.Id }, cancellationToken: ct));

            if (device is null || device.Status != "active")
            {
                throw AppException.Unauthorized(ErrorCodes.DeviceRevoked,
                    "This device is no longer paired with the account.");
            }

            deviceId = device.Id;
            devicePublicId = parsedDeviceId;
        }

        return new CallerIdentity
        {
            UserId = user.Id,
            UserPublicId = userPublicId,
            DeviceId = deviceId,
            DevicePublicId = devicePublicId,
            ClientKind = clientKind,
            Scopes = scopes,
            TokenId = jti,
        };
    }

    /// <summary>Immediate revocation for the cases 15-minute expiry cannot cover.</summary>
    public Task DenyAsync(string jti, TimeSpan ttl, CancellationToken ct = default) =>
        cache.SetAsync(CacheKeys.DenyListedJti(jti), "1", ttl, ct);

    private sealed record UserLookup
    {
        public long Id { get; init; }
        public string Status { get; init; } = UserStatuses.Active;
        public DateTimeOffset? DeletedAt { get; init; }
    }

    private sealed record DeviceLookup
    {
        public long Id { get; init; }
        public string Status { get; init; } = "active";
    }
}

