using System.Security.Cryptography;

namespace PCConnect.Client;

/// <summary>
/// When to poll, and how long to wait between polls, while the realtime
/// connection is unhealthy.
///
/// This is a direct port of the policy in the Go agent's
/// <c>internal/realtime/policy.go</c>, which the assessment identified as small,
/// tested and correct (05 §5). It carries forward unchanged except for the one
/// extension that document asks for: jitter, so that a server restart does not
/// make every agent in the fleet reconnect on the same tick.
/// </summary>
public sealed class FallbackPollingPolicy(TimeSpan? baseInterval = null, TimeSpan? maxInterval = null)
{
    public static readonly TimeSpan DefaultBaseInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultMaxInterval = TimeSpan.FromSeconds(30);

    /// <summary>±20% randomisation, per 05 §5.</summary>
    private const double JitterFraction = 0.2;

    private readonly TimeSpan _base = baseInterval ?? DefaultBaseInterval;
    private readonly TimeSpan _max = maxInterval ?? DefaultMaxInterval;

    public TimeSpan Current { get; private set; } = baseInterval ?? DefaultBaseInterval;

    /// <summary>A connected agent does not poll at all.</summary>
    public static bool ShouldPoll(bool socketHealthy) => !socketHealthy;

    /// <summary>Doubles the interval, capped, and applies jitter to the result.</summary>
    public TimeSpan NextInterval()
    {
        var next = TimeSpan.FromTicks(Math.Min(Current.Ticks * 2, _max.Ticks));
        Current = next;
        return Jitter(next);
    }

    public TimeSpan Reset()
    {
        Current = _base;
        return Jitter(_base);
    }

    /// <summary>
    /// Deterministic when <paramref name="fraction"/> is supplied, which is what
    /// makes the jitter testable rather than merely present.
    /// </summary>
    public static TimeSpan Jitter(TimeSpan interval, double? fraction = null)
    {
        var offset = fraction ?? ((RandomNumberGenerator.GetInt32(0, 2001) / 1000.0) - 1.0);
        var scaled = interval.TotalMilliseconds * (1 + (JitterFraction * Math.Clamp(offset, -1, 1)));
        return TimeSpan.FromMilliseconds(Math.Max(scaled, 1));
    }
}
