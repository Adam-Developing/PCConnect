using Npgsql;
using PCConnect.Domain;

namespace PCConnect.Worker;

public sealed class PresenceWorker(NpgsqlDataSource dataSource, IClock clock, ILogger<PresenceWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> SweepFailed = LoggerMessage.Define(LogLevel.Error, new EventId(3001, nameof(SweepFailed)), "Presence sweep failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await MarkOfflineAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { SweepFailed(logger, exception); }
        }
    }

    internal async Task MarkOfflineAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            WITH offline AS (
              UPDATE devices SET status='offline',row_version=row_version+1
              WHERE status='online' AND (last_seen_at IS NULL OR last_seen_at<@threshold)
              RETURNING id,user_id,row_version,last_seen_at
            )
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            SELECT uuidv7(),'DevicePresenceChanged','device',id,row_version,
              jsonb_build_object('userId',user_id,'deviceId',id,'status','offline','lastSeenAt',last_seen_at),@now FROM offline;
            """);
        command.Parameters.AddWithValue("threshold", clock.UtcNow.AddSeconds(-75));
        command.Parameters.AddWithValue("now", clock.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
