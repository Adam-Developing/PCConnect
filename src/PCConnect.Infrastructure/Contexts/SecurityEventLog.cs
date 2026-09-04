using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using PCConnect.Infrastructure.Data;

namespace PCConnect.Infrastructure.Contexts;

/// <summary>Names of the security events written to <c>security_events</c> (03 §8).</summary>
public static class SecurityEventNames
{
    public const string LoginSucceeded = "login.succeeded";
    public const string LoginFailed = "login.failed";
    public const string LoginLocked = "login.locked";
    public const string LoginLegacyHash = "login.legacy_hash";
    public const string PasswordUpgraded = "password.upgraded";
    public const string PasswordChanged = "password.changed";
    public const string PasswordResetRequested = "password.reset_requested";
    public const string PasswordResetCompleted = "password.reset_completed";
    public const string TokenRefreshed = "token.refreshed";
    public const string TokenReuseDetected = "token.reuse_detected";
    public const string SessionRevoked = "session.revoked";
    public const string AllSessionsRevoked = "session.revoked_all";
    public const string DevicePairingStarted = "device.pairing_started";
    public const string DevicePairingClaimed = "device.pairing_claimed";
    public const string DevicePairingCollected = "device.pairing_collected";
    public const string DeviceRevoked = "device.revoked";
    public const string DeviceTokenIssued = "device.token_issued";
    public const string LegacyAutoPair = "legacy.auto_pair";
    public const string LegacyLogin = "legacy.login";
    public const string StepUpSucceeded = "stepup.succeeded";
    public const string StepUpFailed = "stepup.failed";
    public const string PasskeyRegistered = "passkey.registered";
    public const string PasskeyUsed = "passkey.used";
    public const string PasskeyRevoked = "passkey.revoked";
    public const string PasskeyCounterRegressed = "passkey.counter_regressed";
    public const string AccountRegistered = "account.registered";
    public const string AccountDeleted = "account.deleted";
    public const string AccountExported = "account.exported";
}

/// <summary>
/// Append-only record of every authentication decision. Deliberately never
/// carries a password, hash, token, code or decrypted reminder — the detail
/// column takes outcomes and identifiers only (03 §8).
/// </summary>
public sealed class SecurityEventLog(Db db, ILogger<SecurityEventLog> logger)
{
    private const string InsertSql = """
        INSERT INTO security_events (user_id, event, outcome, source_ip, user_agent, detail)
        VALUES (@UserId, @Event, @Outcome, @SourceIp::inet, @UserAgent, @Detail::jsonb)
        """;

    public async Task WriteAsync(
        long? userId,
        string @event,
        bool success,
        RequestContext context,
        object? detail = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(InsertSql, new
            {
                UserId = userId,
                Event = @event,
                Outcome = success ? "success" : "failure",
                SourceIp = context.IpAddress,
                UserAgent = Truncate(context.UserAgent, 255),
                Detail = detail is null ? null : DbJson.Serialise(detail),
            }, cancellationToken: ct));
        }
        catch (NpgsqlException ex)
        {
            // A security event that cannot be written must not fail the request it
            // describes: losing the audit line is bad, refusing a legitimate login
            // because the audit table is unavailable is worse.
            logger.LogError(ex, "Failed to write security event {Event} for user {UserId}", @event, userId);
        }
    }

    public Task WriteInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long? userId,
        string @event,
        bool success,
        RequestContext context,
        object? detail = null,
        CancellationToken ct = default) =>
        connection.ExecuteAsync(new CommandDefinition(InsertSql, new
        {
            UserId = userId,
            Event = @event,
            Outcome = success ? "success" : "failure",
            SourceIp = context.IpAddress,
            UserAgent = Truncate(context.UserAgent, 255),
            Detail = detail is null ? null : DbJson.Serialise(detail),
        }, transaction, cancellationToken: ct));

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

/// <summary>
/// The ambient facts about a request that the contexts need but must not reach
/// into HTTP for: caller IP, user agent, correlation id.
/// </summary>
public sealed record RequestContext(string? IpAddress, string UserAgent, string RequestId)
{
    public static readonly RequestContext System = new(null, "system", "system");
}
