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

    /// <summary>
    /// When true the agent prints a pairing code and waits for the user to
    /// confirm it in the app. When false it stays idle until it is paired, which
    /// is what a freshly-installed service does before anyone has set it up.
    /// </summary>
    public bool AutoStartPairing { get; set; } = true;
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
    ILogger<AgentWorker> logger) : BackgroundService
{
    private readonly AgentOptions _options = options.Value;
    private string? _deviceId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PCConnect agent {Version} starting for {Machine}", _options.Version, _options.DisplayName);

        await WaitForPairingAsync(stoppingToken);

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
    /// Pairing: the agent asks for a code, shows it, and waits for the account
    /// owner to confirm it in the app. Nothing about this machine's name grants
    /// it anything — that is the whole of C-2.
    /// </summary>
    private async Task WaitForPairingAsync(CancellationToken ct)
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

            // The account owner revoked this device, and EnsureSessionAsync has
            // just cleared the credential. Returning here left the agent running
            // with nothing: no credential, no pairing, polling forever against a
            // token the server will always reject, and never showing a code. The
            // only way back was to delete the entry from Credential Manager by
            // hand. Falling through offers a new pairing code instead, which is
            // what someone who has just un-revoked their own PC expects.
            logger.LogInformation("This PC is no longer paired. Starting again so it can be linked.");
        }

        if (!_options.AutoStartPairing)
        {
            logger.LogInformation("This agent is not paired. Start pairing from the PCConnect companion.");
            return;
        }

        var backoff = InitialBackoff;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var start = await api.StartPairingAsync(_options.DisplayName, ct);
                if (start is null)
                {
                    backoff = NextBackoff(backoff);
                    await Task.Delay(backoff, ct);
                    continue;
                }

                backoff = InitialBackoff;

                logger.LogInformation(
                    "Pairing code {Code} — enter it in the PCConnect app within {Minutes} minutes to link this PC",
                    start.PairingCode, start.ExpiresInSeconds / 60);

                var deadline = DateTimeOffset.UtcNow.AddSeconds(start.ExpiresInSeconds);

                while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);

                    var poll = await api.PollPairingAsync(start.PollToken, ct);

                    if (poll?.Status == "paired" && poll.DeviceId is not null && poll.DeviceSecret is not null)
                    {
                        logger.LogInformation("Paired as {DisplayName}", poll.DisplayName);
                        _deviceId = poll.DeviceId;

                        // The secret is written to Credential Manager and then
                        // exchanged for a short-lived device token. It crosses
                        // the wire exactly once, here.
                        await tokens.WriteAsync(new StoredTokens(null, poll.DeviceId, poll.DeviceSecret), ct);
                        await api.ExchangeDeviceSecretAsync(poll.DeviceId, poll.DeviceSecret,
                            Environment.OSVersion.VersionString, ct);
                        return;
                    }

                    if (poll?.Status == "expired")
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Everything reachable from here is transient by definition: the
                // agent holds no credential yet, so there is no failure it could
                // answer by behaving differently. A 429 from the pairing budget,
                // a Polly timeout, a DNS failure and a 503 are all "come back
                // later". Letting any of them escape faults the BackgroundService
                // and stops the host, which turns a ten-minute rate limit into an
                // agent that never returns until someone restarts the service.
                backoff = NextBackoff(backoff);
                logger.LogWarning(ex, "Pairing attempt failed; retrying in {Delay}", backoff);

                try
                {
                    await Task.Delay(backoff, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Exponential to a five-minute ceiling with the same +/-20% jitter the
    /// realtime fallback uses (05 section 5), so a fleet that all lost the
    /// server does not come back in lockstep and knock it over again.
    /// </summary>
    private static TimeSpan NextBackoff(TimeSpan current)
    {
        var seconds = Math.Min(current.TotalSeconds * 2, 300);
        var jitter = 1 + (0.2 * ((Random.Shared.NextDouble() * 2) - 1));
        return TimeSpan.FromSeconds(Math.Max(1, seconds * jitter));
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
            logger.LogWarning("This device has been unpaired from the account; clearing the stored credential");
            await tokens.ClearAsync(ct);
            _deviceId = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same reasoning as the pairing loop: an unreachable or overloaded
            // server must not stop a paired agent, because the main loop below
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
