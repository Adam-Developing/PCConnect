using System.Data;
using System.Text.Json;
using Npgsql;
using PCConnect.Contracts.V2;
using PCConnect.Domain;
using StackExchange.Redis;

namespace PCConnect.Worker;

public sealed class OutboxPublisherWorker(
    NpgsqlDataSource dataSource,
    IClock clock,
    IConfiguration configuration,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    private readonly Guid instanceId = Guid.CreateVersion7();
    private static readonly Action<ILogger, Exception?> DispatchFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(4001, nameof(DispatchFailed)), "Outbox dispatch cycle failed");
    private static readonly Action<ILogger, Exception?> ValkeyMissing = LoggerMessage.Define(
        LogLevel.Warning, new EventId(4002, nameof(ValkeyMissing)), "Realtime:ValkeyConnection is absent; durable outbox messages remain pending");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration["Realtime:ValkeyConnection"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            ValkeyMissing(logger, null);
            return;
        }

        await using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var subscriber = redis.GetSubscriber();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var messages = await ClaimAsync(stoppingToken);
                foreach (var message in messages)
                {
                    try
                    {
                        var dispatch = Convert(message);
                        await subscriber.PublishAsync(
                            RedisChannel.Literal(RealtimeChannels.Dispatch),
                            JsonSerializer.Serialize(dispatch, JsonOptions));
                        await MarkPublishedAsync(message.Id, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        await ReleaseAsync(message.Id, exception.GetType().Name, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { DispatchFailed(logger, exception); }
        }
    }

    internal async Task<IReadOnlyList<OutboxRow>> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH candidates AS (
              SELECT id FROM outbox_messages
              WHERE published_at IS NULL AND available_at<=@now AND (claimed_until IS NULL OR claimed_until<=@now)
              ORDER BY occurred_at,id LIMIT 100 FOR UPDATE SKIP LOCKED
            )
            UPDATE outbox_messages o SET claimed_by=@worker,claimed_until=@lease,attempt_count=attempt_count+1
            FROM candidates c WHERE o.id=c.id
            RETURNING o.id,o.event_type,o.aggregate_id,o.aggregate_version,o.payload::text,o.occurred_at;
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("worker", instanceId);
        command.Parameters.AddWithValue("lease", now.AddSeconds(30));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<OutboxRow>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetInt64(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
        await reader.CloseAsync();
        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    private async Task MarkPublishedAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE outbox_messages SET published_at=@now,claimed_by=NULL,claimed_until=NULL,last_error_code=NULL
            WHERE id=@id AND claimed_by=@worker AND published_at IS NULL;
            """);
        command.Parameters.AddWithValue("now", clock.UtcNow);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("worker", instanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ReleaseAsync(Guid id, string errorCode, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE outbox_messages SET claimed_by=NULL,claimed_until=NULL,available_at=@retry,last_error_code=@error
            WHERE id=@id AND claimed_by=@worker AND published_at IS NULL;
            """);
        command.Parameters.AddWithValue("retry", clock.UtcNow.AddSeconds(5));
        command.Parameters.AddWithValue("error", errorCode[..Math.Min(errorCode.Length, 100)]);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("worker", instanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static RealtimeDispatchMessage Convert(OutboxRow row)
    {
        using var document = JsonDocument.Parse(row.Payload);
        var source = document.RootElement;
        var (targetKind, targetId, payload) = row.EventType switch
        {
            "CommandAvailable" => ("device", RequiredGuid(source, "deviceId"), Select(source, "commandId", "expiresAt")),
            "CommandStatusChanged" => ("user", RequiredGuid(source, "userId"), Select(source, "commandId", "deviceId", "status", "failureCode")),
            "DevicePresenceChanged" => ("user", RequiredGuid(source, "userId"), Select(source, "deviceId", "status", "lastSeenAt")),
            "ReminderChanged" when source.TryGetProperty("deviceId", out var device) => ("device", device.GetGuid(), Select(source, "reminderId", "deliveryId", "change")),
            "ReminderChanged" => ("user", RequiredGuid(source, "userId"), Select(source, "reminderId", "deliveryId", "change")),
            "SessionRevoked" when source.TryGetProperty("deviceId", out var device) => ("device", device.GetGuid(), Select(source, "sessionId", "reason")),
            "SessionRevoked" => ("user", RequiredGuid(source, "userId"), Select(source, "sessionId", "reason")),
            _ => throw new InvalidOperationException($"Unsupported realtime outbox event type '{row.EventType}'.")
        };
        return new(targetKind, targetId, new(row.Id, row.EventType, row.AggregateId, row.AggregateVersion, row.OccurredAt, payload));
    }

    private static Guid RequiredGuid(JsonElement source, string property) => source.GetProperty(property).GetGuid();

    private static JsonElement Select(JsonElement source, params string[] names)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in names)
            result[name] = source.TryGetProperty(name, out var value) ? value.Clone() : JsonSerializer.SerializeToElement<object?>(null);
        return JsonSerializer.SerializeToElement(result, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal sealed record OutboxRow(Guid Id, string EventType, Guid AggregateId, long AggregateVersion, string Payload, DateTimeOffset OccurredAt);
}
