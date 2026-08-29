using Npgsql;
using PCConnect.Domain;

namespace PCConnect.Worker;

public sealed class CommandExpiryWorker(NpgsqlDataSource dataSource, IClock clock, IConfiguration configuration, ILogger<CommandExpiryWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> SweepFailed = LoggerMessage.Define(LogLevel.Error, new EventId(1001, nameof(SweepFailed)), "Command expiry sweep failed");
    private static readonly Action<ILogger, int, Exception?> CommandsExpired = LoggerMessage.Define<int>(LogLevel.Information, new EventId(1002, nameof(CommandsExpired)), "Expired {CommandCount} commands");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Worker:SweepIntervalSeconds", 5), 1, 60));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ExpireCommandsAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { SweepFailed(logger, exception); }
        }
    }

    internal async Task<int> ExpireCommandsAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH expired AS (
                UPDATE commands SET status='expired', finished_at=@now
                WHERE status IN ('queued','claimed') AND expires_at<=@now
                RETURNING id,user_id,target_device_id,status,row_version
            ), events AS (
                INSERT INTO command_events(id,command_id,sequence,from_status,to_status,actor_kind,occurred_at,metadata)
                SELECT uuidv7(),e.id,COALESCE((SELECT max(sequence)+1 FROM command_events ce WHERE ce.command_id=e.id),1),
                    CASE WHEN EXISTS(SELECT 1 FROM command_events ce WHERE ce.command_id=e.id AND ce.to_status='claimed') THEN 'claimed'::command_status ELSE 'queued'::command_status END,
                    'expired','worker',@now,'{}'::jsonb FROM expired e
                RETURNING command_id
            )
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            SELECT uuidv7(),'CommandStatusChanged','command',e.id,e.row_version,
              jsonb_build_object('userId',e.user_id,'commandId',e.id,'deviceId',e.target_device_id,'status','expired','failureCode',NULL),@now
            FROM expired e;
            """;
        command.Parameters.AddWithValue("now", now);
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (count > 0) CommandsExpired(logger, count, null);
        return count;
    }
}
