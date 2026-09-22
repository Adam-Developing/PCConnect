using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PCConnect.Agent.Execution;
using PCConnect.Client;
using PCConnect.Core.Contracts;

namespace PCConnect.Agent;

public sealed class AgentOptions
{
    /// <summary>
    /// Build-time default, overridable at runtime — never a hardcoded absolute
    /// constant compiled into the binary (S3-08, 06 §1).
    /// </summary>
    public string BaseAddress { get; set; } = "http://localhost:5080";

    public string DisplayName { get; set; } = Environment.MachineName;

    public string Version { get; set; } = "5.0.0";

    public int HeartbeatSeconds { get; set; } = 45;

}

/// <summary>
/// The agent's main loop.
///
/// It holds a device credential and nothing else: it can receive and
/// acknowledge commands for one PC, and it cannot read a reminder, rename a
/// device, or issue a command — including to itself (03 §2.1).
/// </summary>
public sealed class AgentWorker(
    PcConnectClient api,
    PcConnectRealtimeClient realtime,
    CommandExecutor executor,
    ITokenStore tokens,
    IOptions<AgentOptions> options,
    ILogger<ProvisioningPipeServer> provisioningLogger,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private readonly AgentOptions _options = options.Value;

    /// <summary>Completed when a signed-in companion provisions this PC.</summary>
    private readonly TaskCompletionSource _provisionedNow =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string? _deviceId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PCConnect agent {Version} starting for {Machine}", _options.Version, _options.DisplayName);

        // The service starts before anyone signs in. Its local pipe lets the
        // companion hand it the one-time ticket created by that sign-in.
        var provisioning = new ProvisioningPipeServer(
            provisioningLogger,
            () => _deviceId,
            RedeemProvisioningTicketAsync,
            UnregisterAsync);

        _ = Task.Run(() => provisioning.RunAsync(stoppingToken), stoppingToken);

        await WaitForProvisioningAsync(stoppingToken);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        realtime.CommandReceived += OnCommandAsync;

        // How this host recovers after the socket has been unhealthy: claim the
        // commands that were pushed while it was not listening.
        realtime.RecoverState = realtime.ClaimAndDispatchAsync;

        // A failure to connect is not fatal: the fallback poll below is the
        // recovery path, and it is what makes a flaky network survivable.
        try
        {
            await realtime.StartAsync(stoppingToken);
            executor.ServerClockOffset = realtime.ServerClockOffset;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex, "Could not establish the realtime connection at startup; falling back to polling");
        }

        var lastHeartbeat = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = await realtime.PollIfUnhealthyAsync(stoppingToken);

            if (DateTimeOffset.UtcNow - lastHeartbeat > TimeSpan.FromSeconds(_options.HeartbeatSeconds))
            {
                try
                {
                    await realtime.HeartbeatAsync(_deviceId!, Environment.OSVersion.VersionString, _options.Version, stoppingToken);
                    lastHeartbeat = DateTimeOffset.UtcNow;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Heartbeat failed");
                }
            }

            try
            {
                await Task.Delay(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("PCConnect agent stopping");
    }

    /// <summary>
    /// Redeems a ticket the companion obtained for the signed-in user.
    ///
    /// The device secret is written to Credential Manager by this process and
    /// crosses the wire exactly once, to it (ADR-0013).
    /// </summary>
    private async Task<string?> RedeemProvisioningTicketAsync(string ticket, CancellationToken ct)
    {
        var completed = await api.CompleteDeviceProvisioningAsync(ticket, ct);

        if (completed is null)
        {
            logger.LogWarning("A provisioning ticket was not accepted");
            return null;
        }

        await tokens.WriteAsync(new StoredTokens(null, completed.DeviceId, completed.DeviceSecret), ct);
        await api.ExchangeDeviceSecretAsync(
            completed.DeviceId, completed.DeviceSecret, Environment.OSVersion.VersionString, ct);

        _deviceId = completed.DeviceId;
        logger.LogInformation("This PC was added to the account as {DisplayName}", completed.DisplayName);

        // The realtime connection was never started, because the agent was
        // not provisioned when it booted. Waking the loop makes the PC usable
        // straight away rather than after the next restart.
        _provisionedNow.TrySetResult();

        return completed.DeviceId;
    }

    private async Task<bool> UnregisterAsync(CancellationToken ct)
    {
        logger.LogInformation("Unregistering this PC: clearing stored credentials");
        await tokens.ClearAsync(ct);
        _deviceId = null;
        return true;
    }

    /// <summary>
    /// Uses an existing device credential or waits until someone signs in through
    /// the companion on this PC. There is no remote or code-entry registration path.
    /// </summary>
    private async Task WaitForProvisioningAsync(CancellationToken ct)
    {
        var stored = await tokens.ReadAsync(ct);

        if (stored is { DeviceId: not null, DeviceSecret: not null })
        {
            _deviceId = stored.DeviceId;
            await EnsureSessionAsync(stored, ct);

            if (_deviceId is not null)
            {
                return;
            }

            logger.LogInformation("This PC is no longer on an account. Waiting for a sign-in on this PC.");
        }

        logger.LogInformation("This PC is waiting for a user to sign in through the PCConnect companion.");
        await _provisionedNow.Task.WaitAsync(ct);
    }

    private async Task EnsureSessionAsync(StoredTokens stored, CancellationToken ct)
    {
        try
        {
            await api.ExchangeDeviceSecretAsync(stored.DeviceId!, stored.DeviceSecret!,
                Environment.OSVersion.VersionString, ct);
        }
        catch (PcConnectApiException ex) when (ex.Code is "device.revoked" or "auth.invalid_credentials")
        {
            // The account owner removed this device. Forget the credential
            // rather than retrying with one that will never work again.
            logger.LogWarning("This device was removed from the account; clearing the stored credential");
            await tokens.ClearAsync(ct);
            _deviceId = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreachable or overloaded
            // server must not stop a registered agent, because the main loop below
            // is already the retry path.
            logger.LogWarning(ex, "Could not reach the server to start a device session; will retry");
        }
    }

    private async Task OnCommandAsync(PendingCommand command)
    {
        logger.LogInformation("Received {Type} ({CommandId}), expires {ExpiresAt:O}",
            command.Type, command.Id, command.ExpiresAt);

        // The server's own clock, as of the moment it sent this, re-anchors the
        // offset on every command rather than only at connect.
        executor.ServerClockOffset = command.ServerTime - DateTimeOffset.UtcNow;

        // Confirm receipt before executing, so a command that takes the whole of
        // its TTL to run still shows as delivered while it is running rather
        // than appearing lost until the ack arrives.
        await realtime.ConfirmDeliveryAsync(command.Id, CancellationToken.None);

        var result = await executor.ExecuteAsync(command.Id, command.Type, command.ExpiresAt);

        var outcome = result.Outcome switch
        {
            ExecutionOutcome.Succeeded => "ok",
            ExecutionOutcome.Rejected => "rejected",
            _ => "error",
        };

        try
        {
            await realtime.AckAsync(command.Id, outcome, result.ResultCode, result.ResultMessage);
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            // The command may already have taken effect — a shutdown acks into a
            // machine that is powering off. The server's TTL covers this: an
            // unacknowledged command becomes `expired`, and the UI reports that
            // rather than claiming an unconfirmed success (05 §8).
            logger.LogWarning(ex, "Could not acknowledge {CommandId}", command.Id);
        }
    }
}
