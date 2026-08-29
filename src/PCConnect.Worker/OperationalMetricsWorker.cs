using System.Diagnostics.Metrics;
using Npgsql;
using PCConnect.Infrastructure.Observability;

namespace PCConnect.Worker;

public sealed class OperationalMetricsWorker : BackgroundService
{
    private static readonly Action<ILogger, Exception?> RefreshFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(9001, nameof(RefreshFailed)), "Operational metric refresh failed");

    private readonly NpgsqlDataSource dataSource;
    private readonly ILogger<OperationalMetricsWorker> logger;
    private long queuedCommands;
    private double oldestCommandSeconds;
    private long outboxDepth;
    private double oldestOutboxSeconds;
    private double reminderLagSeconds;
    private long activeConnections;
    private long overdueClaims;
    private long recentlyExpiredCommands;

    public OperationalMetricsWorker(NpgsqlDataSource dataSource, ILogger<OperationalMetricsWorker> logger)
    {
        this.dataSource = dataSource;
        this.logger = logger;
        PCConnectTelemetry.Meter.CreateObservableGauge("pcconnect.commands.queued", () => Volatile.Read(ref queuedCommands));
        PCConnectTelemetry.Meter.CreateObservableGauge("pcconnect.commands.oldest_age", () => Volatile.Read(ref oldestCommandSeconds), "s");
        PCConnectTelemetry.Meter.CreateObservableGauge("pcconnect.outbox.depth", () => Volatile.Read(ref outboxDepth));
        PCConnectTelemetry.Meter.CreateObservableGauge("pcconnect.outbox.oldest_age", () => Volatile.Read(ref oldestOutboxSeconds), "s");
        PCConnectTelemetry.Meter.CreateObservableGauge("pcconnect.reminders.delivery_lag", () => Volatile.Read(ref reminderLagSeconds), "s");
        PCConnectTelemetry.Meter.CreateObservableGauge("pcconnect.devices.active_presence", () => Volatile.Read(ref activeConnections));
        PCConnectTelemetry.Meter.CreateObservableGauge("pcconnect.commands.overdue_claims", () => Volatile.Read(ref overdueClaims));
        PCConnectTelemetry.Meter.CreateObservableGauge("pcconnect.commands.expired_last_five_minutes", () => Volatile.Read(ref recentlyExpiredCommands));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { RefreshFailed(logger, exception); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
              (SELECT count(*) FROM commands WHERE status IN ('queued','claimed')),
              COALESCE((SELECT extract(epoch FROM now()-min(issued_at))::double precision FROM commands WHERE status IN ('queued','claimed')),0),
              (SELECT count(*) FROM outbox_messages WHERE published_at IS NULL),
              COALESCE((SELECT extract(epoch FROM now()-min(occurred_at))::double precision FROM outbox_messages WHERE published_at IS NULL),0),
              COALESCE((SELECT extract(epoch FROM now()-min(available_at))::double precision FROM reminder_deliveries WHERE status IN ('pending','available') AND available_at<now()),0),
              (SELECT count(*) FROM devices WHERE status='online' AND last_seen_at>now()-interval '2 minutes'),
              (SELECT count(*) FROM commands WHERE status='claimed' AND claimed_until<now()),
              (SELECT count(*) FROM commands WHERE status='expired' AND finished_at>now()-interval '5 minutes');
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;
        Interlocked.Exchange(ref queuedCommands, reader.GetInt64(0));
        Volatile.Write(ref oldestCommandSeconds, reader.GetDouble(1));
        Interlocked.Exchange(ref outboxDepth, reader.GetInt64(2));
        Volatile.Write(ref oldestOutboxSeconds, reader.GetDouble(3));
        Volatile.Write(ref reminderLagSeconds, reader.GetDouble(4));
        Interlocked.Exchange(ref activeConnections, reader.GetInt64(5));
        Interlocked.Exchange(ref overdueClaims, reader.GetInt64(6));
        Interlocked.Exchange(ref recentlyExpiredCommands, reader.GetInt64(7));
    }
}
