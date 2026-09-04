using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PCConnect.Agent.Execution;

/// <summary>
/// The channel that lets signing in on a PC be what adds it to the account
/// (ADR-0013).
///
/// It runs the opposite way to <see cref="NamedPipeSessionBridge"/>: there the
/// service asks the companion to act on its session, here the companion hands
/// the service a ticket. The service is the only process that may hold a device
/// credential — it runs as LocalSystem and writes to the machine's credential
/// store, which a process in the user's session cannot do — so the ticket, not
/// the secret, is what crosses.
///
/// What crosses is one opaque token that the server minted for the signed-in
/// user, plus a request for the device id already in use. Nothing derived from a
/// command, a path or a shell ever appears here.
/// </summary>
public static class ProvisioningPipe
{
    public const string PipeName = "PCConnect.Provisioning";

    /// <summary>Asks which device this agent is, or <c>UNPAIRED</c>.</summary>
    public const string WhoAmIVerb = "WHOAMI";

    /// <summary>Followed by a space and the provisioning ticket.</summary>
    public const string ProvisionVerb = "PROVISION";

    public const string Unpaired = "UNPAIRED";

    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Long enough for the round trip to the server the agent makes.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The agent's side: a pipe server that answers the two verbs above.
/// </summary>
public sealed class ProvisioningPipeServer(
    ILogger<ProvisioningPipeServer> logger,
    Func<string?> currentDeviceId,
    Func<string, CancellationToken, Task<string?>> provision)
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = Create();
                await pipe.WaitForConnectionAsync(ct);

                // A ticket is a base64url token; 512 bytes is generous for the
                // verb and one of them, and is a hard ceiling on what a caller
                // can push into this process.
                var buffer = new byte[512];
                var read = await pipe.ReadAsync(buffer, ct);
                var line = Encoding.UTF8.GetString(buffer, 0, read).Trim();

                var response = await HandleAsync(line, ct);

                await pipe.WriteAsync(Encoding.UTF8.GetBytes(response), ct);
                await pipe.FlushAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Provisioning pipe client disconnected");
            }
        }
    }

    private async Task<string> HandleAsync(string line, CancellationToken ct)
    {
        if (line == ProvisioningPipe.WhoAmIVerb)
        {
            return currentDeviceId() ?? ProvisioningPipe.Unpaired;
        }

        if (!line.StartsWith(ProvisioningPipe.ProvisionVerb + " ", StringComparison.Ordinal))
        {
            return "FAILED unknown_verb";
        }

        // Already paired means already claimed. Refusing here is what stops a
        // second user on a shared PC from quietly moving it to their own
        // account: the first person to sign in owns it, which is the same
        // property the pairing code had, and un-pairing is a deliberate act by
        // the owner.
        if (currentDeviceId() is not null)
        {
            logger.LogWarning("Refused a provisioning ticket: this PC is already on an account");
            return "FAILED already_paired";
        }

        var ticket = line[(ProvisioningPipe.ProvisionVerb.Length + 1)..].Trim();
        if (ticket.Length is 0 or > 400)
        {
            return "FAILED bad_ticket";
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProvisioningPipe.CallTimeout);

            var deviceId = await provision(ticket, timeout.Token);

            return deviceId is null ? "FAILED not_accepted" : "OK " + deviceId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reason is logged here and not returned: the companion showed a
            // ticket it obtained legitimately, and the detail of why the server
            // refused it belongs in this machine's log, not on a pipe.
            logger.LogWarning(ex, "Could not redeem a provisioning ticket");
            return "FAILED error";
        }
    }

    /// <summary>
    /// SYSTEM and interactively logged-on users, and nobody else.
    ///
    /// Interactive rather than Users: a service or a remote session on this
    /// machine has no business claiming the PC for an account, and the flow only
    /// makes sense for someone sitting at it.
    /// </summary>
    private static NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            ProvisioningPipe.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }
}

/// <summary>The agent's answer to "which device are you".</summary>
/// <param name="IsRunning">False when the service could not be reached at all.</param>
/// <param name="DeviceId">Null when the agent is running but not yet on an account.</param>
public sealed record AgentIdentity(bool IsRunning, string? DeviceId)
{
    public static readonly AgentIdentity NotRunning = new(false, null);

    public static readonly AgentIdentity Unpaired = new(true, null);

    /// <summary>Only then is provisioning both possible and needed.</summary>
    public bool NeedsProvisioning => IsRunning && DeviceId is null;
}

/// <summary>
/// The companion's side: asks the agent what it is, and hands it a ticket.
/// </summary>
public sealed class ProvisioningPipeClient(ILogger<ProvisioningPipeClient> logger)
{
    /// <summary>
    /// What the agent says it is.
    ///
    /// "Not running" and "running but unpaired" have to be different answers.
    /// Collapsing them into null once meant a PC with no agent installed would
    /// have a device provisioned for it that nothing could ever hold the
    /// credential for — a row on the account that is permanently offline.
    /// </summary>
    public async Task<AgentIdentity> WhoAmIAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(ProvisioningPipe.WhoAmIVerb, ct);

        if (response is null)
        {
            return AgentIdentity.NotRunning;
        }

        return response == ProvisioningPipe.Unpaired
            ? AgentIdentity.Unpaired
            : new AgentIdentity(true, response);
    }

    /// <summary>Hands the agent a ticket. Returns the device id it registered as.</summary>
    public async Task<string?> ProvisionAsync(string ticket, CancellationToken ct = default)
    {
        var response = await SendAsync($"{ProvisioningPipe.ProvisionVerb} {ticket}", ct);

        if (response is null || !response.StartsWith("OK ", StringComparison.Ordinal))
        {
            logger.LogWarning("The agent did not accept the provisioning ticket: {Response}", response ?? "no reply");
            return null;
        }

        return response[3..].Trim();
    }

    private async Task<string?> SendAsync(string line, CancellationToken ct)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", ProvisioningPipe.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            await pipe.ConnectAsync((int)ProvisioningPipe.ConnectTimeout.TotalMilliseconds, ct);

            await pipe.WriteAsync(Encoding.UTF8.GetBytes(line), ct);
            await pipe.FlushAsync(ct);

            var buffer = new byte[256];
            var read = await pipe.ReadAsync(buffer, ct);

            return Encoding.UTF8.GetString(buffer, 0, read).Trim();
        }
        catch (TimeoutException)
        {
            // The service is not installed or not running. The companion falls
            // back to the pairing code, which needs no service to be reachable.
            logger.LogInformation("The PCConnect agent is not running on this PC");
            return null;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not reach the PCConnect agent");
            return null;
        }
    }
}
