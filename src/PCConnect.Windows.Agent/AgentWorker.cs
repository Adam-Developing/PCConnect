using PCConnect.Contracts.V2;

namespace PCConnect.Windows.Agent;

public sealed class AgentWorker(AgentApiClient api, AgentRealtimeClient realtime, IFixedCommandExecutor executor, InteractiveSessionBroker broker, ILogger<AgentWorker> logger) : BackgroundService
{
    private readonly Guid instanceId = Guid.NewGuid();
    private static readonly Action<ILogger, Exception?> CycleFailed = LoggerMessage.Define(LogLevel.Error, new EventId(9001, nameof(CycleFailed)), "Agent synchronization cycle failed");
    private static readonly Action<ILogger, Exception?> NotEnrolled = LoggerMessage.Define(LogLevel.Warning, new EventId(9002, nameof(NotEnrolled)), "PCConnect agent is not enrolled; open the PCConnect companion to sign in and enroll this device");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enrollmentNoticeWritten = false;
        while (!await api.IsEnrolledAsync(stoppingToken))
        {
            if (!enrollmentNoticeWritten)
            {
                NotEnrolled(logger, null);
                enrollmentNoticeWritten = true;
            }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        var fallbackSeconds = 15;
        var failureDelaySeconds = 1;
        var nextHeartbeat = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await realtime.EnsureConnectedAsync(stoppingToken);
                if (DateTimeOffset.UtcNow >= nextHeartbeat)
                {
                    await api.HeartbeatAsync(instanceId, stoppingToken);
                    nextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(30);
                }
                await DrainCommandsAsync(stoppingToken);
                await DrainRemindersAsync(stoppingToken);
                var wait = realtime.IsConnected
                    ? nextHeartbeat - DateTimeOffset.UtcNow
                    : TimeSpan.FromSeconds(fallbackSeconds);
                await realtime.WaitAsync(wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1), stoppingToken);
                fallbackSeconds = realtime.IsConnected ? 15 : Math.Min(fallbackSeconds * 2, 60);
                failureDelaySeconds = 1;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                CycleFailed(logger, exception);
                var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
                await Task.Delay(TimeSpan.FromSeconds(failureDelaySeconds * jitter), stoppingToken);
                failureDelaySeconds = Math.Min(failureDelaySeconds * 2, 60);
            }
        }
    }

    private async Task DrainCommandsAsync(CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await api.ListCommandsAsync(cursor, cancellationToken);
            foreach (var candidate in page.Items)
            {
                var command = await api.ClaimAsync(candidate.Id, instanceId, cancellationToken);
                if (command is null) continue;
                var interactive = command.Type is CommandType.Lock or CommandType.SignOut;
                if (!interactive && !executor.Supports(command.Type))
                {
                    await api.AcknowledgeAsync(command.Id, instanceId, "failed", CommandFailureCode.Unsupported, cancellationToken);
                    continue;
                }
                await api.AcknowledgeAsync(command.Id, instanceId, "accepted", null, cancellationToken);
                var result = interactive
                    ? await broker.ExecuteAsync(command, cancellationToken)
                    : await executor.ExecuteAsync(command.Type, cancellationToken);
                if (result.Succeeded && command.Type == CommandType.SignOut) continue;
                if (result.Succeeded && command.Type is (CommandType.Restart or CommandType.Shutdown or CommandType.Sleep or CommandType.Hibernate)) continue;
                await api.AcknowledgeAsync(command.Id, instanceId, result.Succeeded ? "succeeded" : "failed", result.FailureCode, cancellationToken);
            }
            cursor = page.NextCursor;
        }
        while (cursor is not null);
    }

    private async Task DrainRemindersAsync(CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await api.ListReminderDeliveriesAsync(cursor, cancellationToken);
            foreach (var delivery in page.Items)
                if (await broker.DeliverReminderAsync(delivery, cancellationToken))
                    await api.AcknowledgeReminderAsync(delivery.Id, "displayed", cancellationToken);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
    }
}
