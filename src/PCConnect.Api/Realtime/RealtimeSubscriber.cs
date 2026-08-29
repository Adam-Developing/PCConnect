using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using PCConnect.Contracts.V2;
using StackExchange.Redis;

namespace PCConnect.Api.Realtime;

public sealed class RealtimeSubscriber(
    IConfiguration configuration,
    IHubContext<ControllerHub> controllers,
    IHubContext<DeviceHub> devices,
    ILogger<RealtimeSubscriber> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> SubscriptionFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(5001, nameof(SubscriptionFailed)), "Realtime subscription failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration["Realtime:ValkeyConnection"];
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        try
        {
            await using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
            var subscriber = redis.GetSubscriber();
            await subscriber.SubscribeAsync(RedisChannel.Literal(RealtimeChannels.Dispatch), async (_, value) =>
            {
                try
                {
                    var dispatch = JsonSerializer.Deserialize<RealtimeDispatchMessage>(value.ToString(), JsonOptions)
                        ?? throw new JsonException("Realtime dispatch message was empty.");
                    if (dispatch.TargetKind == "user")
                        await controllers.Clients.Group($"user:{dispatch.TargetId:D}").SendAsync(dispatch.Envelope.EventType, dispatch.Envelope, stoppingToken);
                    else if (dispatch.TargetKind == "device")
                        await devices.Clients.Group($"device:{dispatch.TargetId:D}").SendAsync(dispatch.Envelope.EventType, dispatch.Envelope, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    SubscriptionFailed(logger, exception);
                }
            });
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { SubscriptionFailed(logger, exception); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
