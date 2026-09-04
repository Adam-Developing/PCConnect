using System.Diagnostics.Metrics;

namespace PCConnect.Infrastructure.Telemetry;

/// <summary>
/// The application metrics of 05 §9, in one place so the names stay stable and
/// the alerting rules in deploy/ keep matching them.
/// </summary>
public sealed class CommandMetrics : IDisposable
{
    public const string MeterName = "PCConnect";

    private readonly Meter _meter;
    private readonly Counter<long> _issued;
    private readonly Counter<long> _expired;
    private readonly Counter<long> _acked;
    private readonly Counter<long> _staleExecutions;
    private readonly Histogram<double> _deliverySeconds;
    private readonly Counter<long> _legacyRequests;
    private readonly Counter<long> _authFailures;
    private readonly Counter<long> _presenceFlaps;
    private int _realtimeConnections;

    public CommandMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);

        _issued = _meter.CreateCounter<long>("pcconnect_commands_issued_total",
            description: "Commands accepted for delivery.");
        _expired = _meter.CreateCounter<long>("pcconnect_commands_expired_total",
            description: "Commands that passed their TTL without being acknowledged.");
        _acked = _meter.CreateCounter<long>("pcconnect_commands_acked_total",
            description: "Commands acknowledged by an agent, by terminal status.");
        _staleExecutions = _meter.CreateCounter<long>("pcconnect_command_stale_executions_total",
            description: "Commands an agent reported executing after their TTL. Any non-zero value is an incident.");
        _deliverySeconds = _meter.CreateHistogram<double>("pcconnect_command_delivery_seconds",
            unit: "s", description: "Time from issue to delivery.");
        _legacyRequests = _meter.CreateCounter<long>("pcconnect_legacy_requests_total",
            description: "Requests served by the legacy compatibility shim. This counter decides when the shim can be deleted.");
        _authFailures = _meter.CreateCounter<long>("pcconnect_auth_failures_total",
            description: "Rejected authentication attempts, by surface.");
        _presenceFlaps = _meter.CreateCounter<long>("pcconnect_presence_flaps_total",
            description: "Device presence transitions.");

        _meter.CreateObservableGauge("pcconnect_realtime_connections",
            () => Volatile.Read(ref _realtimeConnections),
            description: "Live realtime connections.");
    }

    public void CommandIssued(string type, string riskTier) =>
        _issued.Add(1, new KeyValuePair<string, object?>("type", type), new KeyValuePair<string, object?>("risk", riskTier));

    public void CommandDelivered(string type, TimeSpan sinceIssue) =>
        _deliverySeconds.Record(sinceIssue.TotalSeconds, new KeyValuePair<string, object?>("type", type));

    public void CommandAcked(string type, string status) =>
        _acked.Add(1, new KeyValuePair<string, object?>("type", type), new KeyValuePair<string, object?>("status", status));

    public void CommandExpired(string type) =>
        _expired.Add(1, new KeyValuePair<string, object?>("type", type));

    public void StaleExecution(string type) =>
        _staleExecutions.Add(1, new KeyValuePair<string, object?>("type", type));

    public void LegacyRequest(string endpoint) =>
        _legacyRequests.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint));

    public void AuthFailure(string surface) =>
        _authFailures.Add(1, new KeyValuePair<string, object?>("surface", surface));

    public void RealtimeConnected(string clientKind)
    {
        Interlocked.Increment(ref _realtimeConnections);
        _presenceFlaps.Add(1, new KeyValuePair<string, object?>("transition", "connect"),
            new KeyValuePair<string, object?>("client", clientKind));
    }

    public void RealtimeDisconnected(string clientKind)
    {
        Interlocked.Decrement(ref _realtimeConnections);
        _presenceFlaps.Add(1, new KeyValuePair<string, object?>("transition", "disconnect"),
            new KeyValuePair<string, object?>("client", clientKind));
    }

    public void Dispose() => _meter.Dispose();
}
