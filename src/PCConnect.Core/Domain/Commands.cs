namespace PCConnect.Core.Domain;

/// <summary>
/// The closed command vocabulary. There is no path by which a string from an
/// HTTP body reaches a shell: a request names one of these six values, and the
/// agent maps that value to a fixed argv of its own (03 §3, 02 §3.3).
/// </summary>
public static class CommandTypes
{
    public const string Shutdown = "shutdown";
    public const string Restart = "restart";
    public const string SignOut = "signout";
    public const string Lock = "lock";
    public const string Sleep = "sleep";
    public const string Hibernate = "hibernate";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Shutdown, Restart, SignOut, Lock, Sleep, Hibernate,
    };

    /// <summary>
    /// Commands that end the user's session or power state without warning. These
    /// carry the destructive risk tier and require step-up authentication
    /// (ADR-0011). Sleep and lock are recoverable in one keypress; a shutdown
    /// during work is not.
    /// </summary>
    public static readonly IReadOnlySet<string> Destructive = new HashSet<string>(StringComparer.Ordinal)
    {
        Shutdown, Restart, SignOut, Hibernate,
    };

    /// <summary>
    /// Normalises a caller-supplied type. Case-insensitive because the legacy
    /// clients send <c>Shut_Down</c>/<c>Shutdown</c>/<c>Signout</c>; strict
    /// membership because anything unrecognised must be rejected, not guessed.
    /// </summary>
    public static bool TryNormalise(string? raw, out string normalised)
    {
        normalised = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var candidate = raw.Trim().Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        candidate = candidate switch
        {
            "logoff" or "logout" or "signoff" => SignOut,
            "shutdown" => Shutdown,
            "reboot" => Restart,
            _ => candidate,
        };

        if (!All.Contains(candidate))
        {
            return false;
        }

        normalised = candidate;
        return true;
    }

    public static string RiskTierFor(string commandType) =>
        Destructive.Contains(commandType) ? RiskTiers.Destructive : RiskTiers.Standard;
}

public static class RiskTiers
{
    public const string Standard = "standard";
    public const string Destructive = "destructive";
}

public static class CommandStatuses
{
    public const string Issued = "issued";
    public const string Delivered = "delivered";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Succeeded, Failed, Expired, Cancelled,
    };

    public static bool IsTerminal(string status) => Terminal.Contains(status);
}

public static class CommandEventNames
{
    public const string Issued = "issued";
    public const string Claimed = "claimed";
    public const string Delivered = "delivered";
    public const string Acked = "acked";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";

    /// <summary>
    /// An agent reported executing a command whose TTL had already passed. This
    /// should be structurally impossible; it is recorded and alerted on anyway,
    /// because "impossible" and "unobserved" are different claims (05 §9).
    /// </summary>
    public const string StaleExecution = "stale_execution";
}

/// <summary>
/// The command lifecycle, as one place rather than as conditions scattered
/// through handlers. Transitions that are not listed here cannot happen.
/// </summary>
public static class CommandStateMachine
{
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        [CommandStatuses.Issued] =
        [
            CommandStatuses.Delivered, CommandStatuses.Expired, CommandStatuses.Cancelled,
            // A device may ack straight from `issued` when the push and its ack
            // race the delivery write; treating that as legal avoids a lost ack.
            CommandStatuses.Succeeded, CommandStatuses.Failed,
        ],
        [CommandStatuses.Delivered] =
        [
            CommandStatuses.Succeeded, CommandStatuses.Failed, CommandStatuses.Expired,
        ],
        [CommandStatuses.Succeeded] = [],
        [CommandStatuses.Failed] = [],
        [CommandStatuses.Expired] = [],
        [CommandStatuses.Cancelled] = [],
    };

    public static bool CanTransition(string from, string to) =>
        Allowed.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0;

    public static IReadOnlyCollection<string> NextStates(string from) =>
        Allowed.TryGetValue(from, out var next) ? next : [];
}

/// <summary>Outcome an agent reports when it acknowledges a command.</summary>
public static class CommandOutcomes
{
    public const string Ok = "ok";
    public const string Error = "error";
    public const string Rejected = "rejected";

    public static bool IsValid(string outcome) =>
        outcome is Ok or Error or Rejected;

    public static string ToStatus(string outcome) => outcome switch
    {
        Ok => CommandStatuses.Succeeded,
        _ => CommandStatuses.Failed,
    };
}

/// <summary>Command time-to-live bounds (ADR-0003).</summary>
public static class CommandTtl
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan Min = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(600);

    public static TimeSpan Resolve(int? requestedSeconds)
    {
        if (requestedSeconds is null)
        {
            return Default;
        }

        var ttl = TimeSpan.FromSeconds(requestedSeconds.Value);
        if (ttl < Min || ttl > Max)
        {
            throw new AppException(
                ErrorCodes.CommandTtlInvalid,
                $"ttlSeconds must be between {Min.TotalSeconds:0} and {Max.TotalSeconds:0}.",
                System.Net.HttpStatusCode.BadRequest,
                [new ErrorDetail("ttlSeconds", "out_of_range")]);
        }

        return ttl;
    }
}
