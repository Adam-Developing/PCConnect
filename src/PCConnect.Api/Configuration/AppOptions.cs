namespace PCConnect.Api.Configuration;

/// <summary>
/// What <c>GET /v2/meta/discovery</c> returns. `MinimumSupportedClient` is the
/// lever that ends the legacy era: raising it makes older builds show a blocking
/// update prompt, and it is raised only when the shim's counter says legacy
/// traffic is under 1% (04 §2, ADR-0008).
/// </summary>
public sealed class DiscoveryOptions
{
    public string ApiVersion { get; set; } = "2.0.0";

    public string RealtimeUrl { get; set; } = "wss://localhost:5001/rt";

    public Dictionary<string, string> MinimumSupportedClient { get; init; } = new(StringComparer.Ordinal)
    {
        ["desktop"] = "5.0.0",
        ["mobile"] = "8.0.0",
    };

    public Dictionary<string, string> RecommendedClient { get; init; } = new(StringComparer.Ordinal)
    {
        ["desktop"] = "5.0.0",
        ["mobile"] = "8.0.0",
    };

    public DateTimeOffset? LegacySunsetAt { get; set; }

    public List<string> Capabilities { get; init; } =
    [
        "commands.ttl",
        "commands.stepup",
        "reminders.rrule",
        "devices.pairing",
        "auth.passkeys",
        "realtime.signalr",
    ];
}

/// <summary>
/// Browser-facing settings. The allow-list is explicit and never
/// <c>AllowAnyOrigin</c> with credentials, which is S1-11 exactly.
/// </summary>
public sealed class WebOptions
{
    public List<string> CorsAllowedOrigins { get; init; } = [];

    /// <summary>
    /// Turns on HSTS with a two-year max-age and preload. Off by default so a
    /// local HTTP environment is not poisoned for the developer's browser.
    /// </summary>
    public bool EnableHsts { get; set; }

    public bool EnableLegacyShim { get; set; } = true;

    /// <summary>
    /// When set, the shim returns 410 Gone instead of serving. This is P6.2:
    /// switching legacy off is a configuration change, not a deploy of new code.
    /// </summary>
    public bool LegacyShimRetired { get; set; }
}
