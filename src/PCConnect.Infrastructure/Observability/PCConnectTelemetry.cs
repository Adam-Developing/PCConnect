using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PCConnect.Infrastructure.Observability;

public static class PCConnectTelemetry
{
    public const string ActivitySourceName = "PCConnect";
    public const string MeterName = "PCConnect";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> AuthenticationOutcomes = Meter.CreateCounter<long>(
        "pcconnect.authentication.outcomes", description: "Authentication attempts partitioned by HTTP outcome.");
    private static readonly Counter<long> TokenReuse = Meter.CreateCounter<long>(
        "pcconnect.token_family.reuse", description: "Detected refresh-token family reuse events.");
    private static readonly Counter<long> CommandsCreated = Meter.CreateCounter<long>(
        "pcconnect.commands.created", description: "Durable commands created by type.");
    private static readonly Counter<long> CompatibilityRequests = Meter.CreateCounter<long>(
        "pcconnect.compatibility.requests", description: "Legacy compatibility traffic by client route generation.");
    private static readonly UpDownCounter<long> RealtimeConnections = Meter.CreateUpDownCounter<long>(
        "pcconnect.realtime.active_connections", description: "Currently connected authenticated SignalR clients.");
    private static int readiness;

    static PCConnectTelemetry() => Meter.CreateObservableGauge(
        "pcconnect.api.readiness", () => Volatile.Read(ref readiness), description: "One when the API readiness dependency check succeeds.");

    public static void RecordAuthentication(int statusCode) => AuthenticationOutcomes.Add(1,
        new KeyValuePair<string, object?>("outcome", statusCode is >= 200 and < 300 ? "success" : "denied"));

    public static void RecordTokenReuse() => TokenReuse.Add(1);

    public static void RecordReadiness(bool ready) => Volatile.Write(ref readiness, ready ? 1 : 0);

    public static void RecordRealtimeConnection(string subjectKind, long delta) => RealtimeConnections.Add(delta,
        new KeyValuePair<string, object?>("subject.kind", subjectKind));

    public static void RecordCommandCreated(string commandType) => CommandsCreated.Add(1,
        new KeyValuePair<string, object?>("command.type", commandType));

    public static void RecordCompatibilityRequest(string path, int statusCode) => CompatibilityRequests.Add(1,
        new KeyValuePair<string, object?>("client.generation", ClassifyLegacyRoute(path)),
        new KeyValuePair<string, object?>("outcome", statusCode is >= 200 and < 400 ? "success" : "rejected"));

    private static string ClassifyLegacyRoute(string path) => path.StartsWith("/api/pcclient/", StringComparison.Ordinal) ? "windows-v1"
        : path.StartsWith("/api/pcconnect/", StringComparison.Ordinal) ? "android-v1"
        : path.StartsWith("/api/v1/", StringComparison.Ordinal) ? "compat-v1"
        : "legacy-other";
}
