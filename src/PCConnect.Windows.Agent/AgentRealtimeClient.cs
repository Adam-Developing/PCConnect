using Microsoft.AspNetCore.SignalR.Client;

namespace PCConnect.Windows.Agent;

public sealed class AgentRealtimeClient : IAsyncDisposable
{
    private readonly HubConnection connection;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly ILogger<AgentRealtimeClient> logger;
    private static readonly Action<ILogger, Exception?> ConnectionFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(9101, nameof(ConnectionFailed)), "SignalR connection attempt failed; REST recovery polling remains active");

    public AgentRealtimeClient(AgentApiClient api, ILogger<AgentRealtimeClient> logger)
    {
        this.logger = logger;
        var origin = new Uri(api.ApiBaseAddress, "../..");
        connection = new HubConnectionBuilder()
            .WithUrl(new Uri(origin, "api/v2/hubs/device"), options =>
                options.AccessTokenProvider = async () => (string?)await api.GetAccessTokenAsync(CancellationToken.None))
            .WithAutomaticReconnect(new JitterRetryPolicy())
            .Build();
        connection.On<object>("CommandAvailable", _ => Signal());
        connection.On<object>("ReminderChanged", _ => Signal());
        connection.Reconnected += _ => { Signal(); return Task.CompletedTask; };
        connection.Closed += _ => { Signal(); return Task.CompletedTask; };
    }

    public bool IsConnected => connection.State == HubConnectionState.Connected;

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (connection.State != HubConnectionState.Disconnected) return;
        try { await connection.StartAsync(cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ConnectionFailed(logger, exception);
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        _ = await wake.WaitAsync(timeout, cancellationToken);
    }

    private void Signal()
    {
        if (wake.CurrentCount == 0) wake.Release();
    }

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        wake.Dispose();
    }

    private sealed class JitterRetryPolicy : IRetryPolicy
    {
        private static readonly double[] Delays = [1, 2, 5, 10, 30];
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var index = (int)Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1);
            var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
            return TimeSpan.FromSeconds(Delays[index] * jitter);
        }
    }
}
