using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using PCConnect.Core.Contracts;

namespace PCConnect.Client;

/// <summary>
/// The realtime half of the client: a SignalR connection that authenticates in
/// the handshake, reconnects with backoff, and falls back to polling while it is
/// unhealthy.
///
/// Push is the mechanism; polling is the safety net, not the other way round
/// (01 §1 G6). A connected client does not poll at all.
/// </summary>
public sealed class PcConnectRealtimeClient(
    PcConnectClient api,
    ILogger<PcConnectRealtimeClient> logger) : IAsyncDisposable
{
    private readonly FallbackPollingPolicy _policy = new();
    private HubConnection? _connection;

    /// <summary>
    /// The offset between the server's clock and this machine's, measured at
    /// connect. Freshness is evaluated against this rather than the local wall
    /// clock, so an agent with a wrong clock neither expires every command nor
    /// executes stale ones (05 §8).
    /// </summary>
    public TimeSpan ServerClockOffset { get; private set; }

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    /// <summary>
    /// Raised whenever the socket's health changes, so a UI can show what is
    /// true now rather than what was true when it last asked.
    ///
    /// The companion read <see cref="IsConnected"/> once, just after connecting,
    /// and displayed that answer for the rest of the session — which meant its
    /// badge said "Reconnecting" over a perfectly healthy connection, and would
    /// equally have said "Live" over a dead one. A status indicator that lies in
    /// both directions is worse than none.
    /// </summary>
    public event Action<bool>? ConnectionStateChanged;

    /// <summary>
    /// What this client re-reads after the socket has been unhealthy.
    ///
    /// The two hosts recover differently and only they know how: an agent claims
    /// the commands it missed, a companion reloads what its window shows.
    /// Hard-coding the agent's answer here meant the companion asked a
    /// device-only endpoint for pending commands and was refused with 403 on
    /// every connect — noise in the log for a client that has no device
    /// credential and never will (03 §2.1).
    /// </summary>
    public Func<CancellationToken, Task>? RecoverState { get; set; }

    public event Func<PendingCommand, Task>? CommandReceived;

    public event Func<CommandStatusEvent, Task>? CommandStatusChanged;

    public event Func<DevicePresenceEvent, Task>? DevicePresenceChanged;

    public event Func<ReminderDueEvent, Task>? ReminderDue;

    public event Func<ReminderChangedEvent, Task>? ReminderChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var discovery = await api.GetDiscoveryAsync(ct);
        var url = discovery?.RealtimeUrl ?? new Uri(new Uri(api.Options.BaseAddress), "/rt").ToString();

        if (discovery is not null)
        {
            ServerClockOffset = discovery.ServerTime - DateTimeOffset.UtcNow;
        }

        _connection = new HubConnectionBuilder()
            .WithUrl(url, transport => transport.AccessTokenProvider = async () => await api.GetAccessTokenAsync(ct))
            // Randomised delays, for the same reason the poll interval has
            // jitter: a fleet reconnecting on the same tick after a deploy is a
            // self-inflicted thundering herd (05 §7).
            .WithAutomaticReconnect(new JitteredRetryPolicy())
            .Build();

        _connection.On<RealtimeEvent<PendingCommand>>("command.issued", async envelope =>
        {
            if (CommandReceived is { } handler)
            {
                await handler(envelope.Data);
            }
        });

        _connection.On<RealtimeEvent<CommandStatusEvent>>("command.status", async envelope =>
        {
            if (CommandStatusChanged is { } handler)
            {
                await handler(envelope.Data);
            }
        });

        _connection.On<RealtimeEvent<DevicePresenceEvent>>("device.presence", async envelope =>
        {
            if (DevicePresenceChanged is { } handler)
            {
                await handler(envelope.Data);
            }
        });

        _connection.On<RealtimeEvent<ReminderDueEvent>>("reminder.due", async envelope =>
        {
            if (ReminderDue is { } handler)
            {
                await handler(envelope.Data);
            }
        });

        _connection.On<RealtimeEvent<ReminderChangedEvent>>("reminder.changed", async envelope =>
        {
            if (ReminderChanged is { } handler)
            {
                await handler(envelope.Data);
            }
        });

        _connection.Reconnecting += _ =>
        {
            ConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        _connection.Reconnected += async _ =>
        {
            logger.LogInformation("Realtime connection re-established");
            _policy.Reset();
            ConnectionStateChanged?.Invoke(true);

            // SignalR does not replay what was sent while the socket was down,
            // so a reconnect is not a recovery on its own: a command issued
            // during the gap would sit unclaimed until its TTL expired. 05 §6
            // claims "nothing is lost" when an API instance restarts; this read
            // is what makes that true for the client as well as the server.
            await CatchUpAsync(CancellationToken.None);
        };

        _connection.Closed += error =>
        {
            logger.LogWarning(error, "Realtime connection closed");
            ConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        await _connection.StartAsync(ct);
        logger.LogInformation("Realtime connection established to {Url}", url);
        ConnectionStateChanged?.Invoke(true);

        // Also on the first connection, not only on reconnects. An agent that
        // was stopped while a command was issued would otherwise never see it:
        // it comes up connected, and a connected agent does not poll, so the
        // command would sit until its TTL expired.
        await CatchUpAsync(ct);
    }

    /// <summary>
    /// One iteration of the fallback loop. Returns how long to wait before the
    /// next one: the base interval while healthy, a backed-off and jittered one
    /// while not.
    /// </summary>
    public async Task<TimeSpan> PollIfUnhealthyAsync(CancellationToken ct = default)
    {
        if (!FallbackPollingPolicy.ShouldPoll(IsConnected))
        {
            return _policy.Reset();
        }

        try
        {
            await RecoverAsync(ct);

            // A poll that worked means the network is back even if the socket is
            // not; try to reconnect promptly rather than continuing to back off.
            if (_connection is { State: HubConnectionState.Disconnected })
            {
                try
                {
                    await _connection.StartAsync(ct);
                    ConnectionStateChanged?.Invoke(true);
                    await CatchUpAsync(ct);
                    return _policy.Reset();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Realtime reconnect attempt failed");
                }
            }
        }
        catch (PcConnectApiException ex)
        {
            logger.LogWarning("Fallback poll failed: {Code} {Message}", ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the recovery path itself, so it is the one loop in the
            // agent that must never throw: a transport fault, a resilience
            // timeout or a malformed response all mean "still unhealthy, wait
            // and try again", never "stop the agent".
            logger.LogDebug(ex, "Fallback poll could not reach the server");
        }

        return _policy.NextInterval();
    }

    private Task RecoverAsync(CancellationToken ct) =>
        RecoverState is { } recover ? recover(ct) : Task.CompletedTask;

    /// <summary>
    /// The agent's recovery: claim whatever was issued while the socket was
    /// down. Exposed so the agent can install it as <see cref="RecoverState"/>
    /// rather than this class assuming every host holds a device credential.
    /// </summary>
    public async Task ClaimAndDispatchAsync(CancellationToken ct = default)
    {
        foreach (var command in await api.ClaimPendingCommandsAsync(ct))
        {
            if (CommandReceived is { } handler)
            {
                await handler(command);
            }
        }
    }

    /// <summary>
    /// Re-reads the work that may have been pushed while the socket was down.
    /// Idempotent by construction: the claim only returns commands still marked
    /// <c>issued</c>, and the agent drops any id it has already executed.
    /// </summary>
    private async Task CatchUpAsync(CancellationToken ct)
    {
        try
        {
            await RecoverAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The fallback loop is still running and will try again; a failed
            // catch-up must not tear down a connection that has just come back.
            logger.LogDebug(ex, "Catch-up read after reconnect failed");
        }
    }

    public async Task AckAsync(string commandId, string outcome, string? resultCode, string? resultMessage, CancellationToken ct = default)
    {
        // Prefer the socket, fall back to REST. Both paths reach the same
        // per-command ack, which is what makes them reconcilable (05 §4.3).
        if (IsConnected && _connection is not null)
        {
            try
            {
                await _connection.InvokeAsync<CommandResponse>("Ack", commandId, outcome, resultCode, resultMessage, ct);
                return;
            }
            catch (Exception ex) when (ex is HubException or InvalidOperationException or TimeoutException)
            {
                logger.LogWarning(ex, "Realtime ack failed for {CommandId}; falling back to HTTP", commandId);
            }
        }

        await api.AckCommandAsync(commandId, outcome, resultCode, resultMessage, ct);
    }

    /// <summary>
    /// Tells the server the command actually arrived. Best-effort by design: a
    /// confirmation that does not land costs a row in the delivery funnel, and
    /// must never stop the command from being executed and acked.
    /// </summary>
    public async Task ConfirmDeliveryAsync(string commandId, CancellationToken ct = default)
    {
        if (!IsConnected || _connection is null)
        {
            return;
        }

        try
        {
            await _connection.InvokeAsync("ConfirmDelivery", commandId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not confirm delivery of {CommandId}", commandId);
        }
    }

    public async Task HeartbeatAsync(string deviceId, string osVersion, string agentVersion, CancellationToken ct = default)
    {
        if (IsConnected && _connection is not null)
        {
            try
            {
                await _connection.InvokeAsync("Heartbeat", agentVersion, osVersion, ct);
                return;
            }
            catch (Exception ex) when (ex is HubException or InvalidOperationException or TimeoutException)
            {
                logger.LogDebug(ex, "Realtime heartbeat failed; falling back to HTTP");
            }
        }

        await api.HeartbeatAsync(deviceId, osVersion, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class JitteredRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context)
        {
            var seconds = Math.Min(30, Math.Pow(2, Math.Min(context.PreviousRetryCount, 5)));
            return FallbackPollingPolicy.Jitter(TimeSpan.FromSeconds(seconds));
        }
    }
}
