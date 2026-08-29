using Npgsql;
using PCConnect.Domain;
using PCConnect.Domain.Reminders;

namespace PCConnect.Worker;

public sealed class ReminderSchedulerWorker(NpgsqlDataSource dataSource, IClock clock, ILogger<ReminderSchedulerWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> SweepFailed = LoggerMessage.Define(LogLevel.Error, new EventId(2001, nameof(SweepFailed)), "Reminder scheduling sweep failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await GenerateOccurrencesAsync(stoppingToken);
                await SynchronizeAllDeviceDeliveriesAsync(stoppingToken);
                await MakeDueDeliveriesAvailableAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { SweepFailed(logger, exception); }
        }
    }

    internal async Task GenerateOccurrencesAsync(CancellationToken cancellationToken)
    {
        var windowEnd = clock.UtcNow.AddDays(30);
        var definitions = new List<Definition>();
        await using (var command = dataSource.CreateCommand("""
            SELECT id,user_id,target_mode::text,timezone,local_start,recurrence_rule
            FROM reminders WHERE deleted_at IS NULL ORDER BY updated_at LIMIT 1000
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                definitions.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetString(5)));

        foreach (var definition in definitions)
        {
            var occurrences = RecurrenceScheduler.Generate(definition.LocalStart, definition.Timezone, definition.RecurrenceRule, windowEnd);
            foreach (var occurrence in occurrences)
                await InsertOccurrenceAsync(definition, occurrence, cancellationToken);
        }
    }

    internal async Task MakeDueDeliveriesAvailableAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            WITH due AS (
              UPDATE reminder_deliveries rd SET status='available',available_at=@now,row_version=row_version+1
              FROM reminder_occurrences ro
              WHERE rd.occurrence_id=ro.id AND rd.status='pending' AND ro.cancelled_at IS NULL AND ro.occurrence_at<=@now
              RETURNING rd.id,rd.device_id,rd.row_version,ro.reminder_id
            )
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            SELECT uuidv7(),'ReminderChanged','reminder_delivery',id,row_version,
              jsonb_build_object('deviceId',device_id,'reminderId',reminder_id,'deliveryId',id,'change','delivery_available'),@now FROM due;
            """);
        command.Parameters.AddWithValue("now", clock.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Occurrences are generated ahead of time. An all-device reminder must also
    // reach a capable device enrolled after that generation pass.
    internal async Task SynchronizeAllDeviceDeliveriesAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO reminder_deliveries(id,occurrence_id,device_id,status,created_at)
            SELECT uuidv7(),ro.id,d.id,'pending',@now
            FROM reminders r
            JOIN reminder_occurrences ro ON ro.reminder_id=r.id AND ro.cancelled_at IS NULL
            JOIN devices d ON d.user_id=r.user_id AND d.status<>'revoked' AND 'reminders'=ANY(d.capabilities)
            WHERE r.deleted_at IS NULL AND r.target_mode='all_devices' AND ro.occurrence_at<=@windowEnd
            ON CONFLICT(occurrence_id,device_id) DO NOTHING;
            """);
        command.Parameters.AddWithValue("now", clock.UtcNow);
        command.Parameters.AddWithValue("windowEnd", clock.UtcNow.AddDays(30));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertOccurrenceAsync(Definition definition, ScheduledOccurrence occurrence, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid? occurrenceId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reminder_occurrences(id,reminder_id,occurrence_at,local_occurrence,timezone_offset_seconds,generated_at)
                VALUES(@id,@reminderId,@instant,@local,@offset,@now)
                ON CONFLICT(reminder_id,occurrence_at) DO NOTHING RETURNING id;
                """;
            var id = Guid.CreateVersion7(clock.UtcNow);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("reminderId", definition.Id);
            command.Parameters.AddWithValue("instant", occurrence.Instant);
            command.Parameters.AddWithValue("local", occurrence.LocalOccurrence);
            command.Parameters.AddWithValue("offset", occurrence.TimezoneOffsetSeconds);
            command.Parameters.AddWithValue("now", clock.UtcNow);
            occurrenceId = await command.ExecuteScalarAsync(cancellationToken) as Guid?;
        }
        if (occurrenceId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        await using var deliveries = connection.CreateCommand();
        deliveries.Transaction = transaction;
        deliveries.CommandText = definition.TargetMode == "all_devices"
            ? """
              INSERT INTO reminder_deliveries(id,occurrence_id,device_id,status,created_at)
              SELECT uuidv7(),@occurrenceId,d.id,'pending',@now FROM devices d
              WHERE d.user_id=@userId AND d.status<>'revoked' AND 'reminders'=ANY(d.capabilities)
              ON CONFLICT(occurrence_id,device_id) DO NOTHING;
              """
            : """
              INSERT INTO reminder_deliveries(id,occurrence_id,device_id,status,created_at)
              SELECT uuidv7(),@occurrenceId,d.id,'pending',@now FROM reminder_targets rt
              JOIN devices d ON d.id=rt.device_id
              WHERE rt.reminder_id=@reminderId AND d.user_id=@userId AND d.status<>'revoked' AND 'reminders'=ANY(d.capabilities)
              ON CONFLICT(occurrence_id,device_id) DO NOTHING;
              """;
        deliveries.Parameters.AddWithValue("occurrenceId", occurrenceId.Value);
        deliveries.Parameters.AddWithValue("userId", definition.UserId);
        deliveries.Parameters.AddWithValue("reminderId", definition.Id);
        deliveries.Parameters.AddWithValue("now", clock.UtcNow);
        await deliveries.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private sealed record Definition(Guid Id, Guid UserId, string TargetMode, string Timezone, DateTime LocalStart, string? RecurrenceRule);
}
