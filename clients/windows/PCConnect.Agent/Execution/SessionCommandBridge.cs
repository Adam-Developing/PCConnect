using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PCConnect.Agent.Execution;

/// <summary>
/// Locking the workstation and signing a user out act on an interactive session.
/// A Windows service runs in session 0 and has none, so those two commands are
/// handed to the companion running in the user's own session (ADR-0012).
/// </summary>
public interface ISessionCommandBridge
{
    Task<ExecutionResult> LockAsync(CancellationToken ct = default);

    Task<ExecutionResult> SignOutAsync(CancellationToken ct = default);
}

/// <summary>
/// The service side of the bridge: a named-pipe client that asks the companion
/// to act.
///
/// The pipe carries only the two verbs below — never a command line, never a
/// path, never anything derived from an HTTP body. If the companion is not
/// running, the command fails honestly rather than being reported as done.
/// </summary>
public sealed class NamedPipeSessionBridge(ILogger<NamedPipeSessionBridge> logger) : ISessionCommandBridge
{
    public const string PipeName = "PCConnect.Session";
    public const string LockVerb = "LOCK";
    public const string SignOutVerb = "SIGNOUT";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public Task<ExecutionResult> LockAsync(CancellationToken ct = default) => SendAsync(LockVerb, ct);

    public Task<ExecutionResult> SignOutAsync(CancellationToken ct = default) => SendAsync(SignOutVerb, ct);

    private async Task<ExecutionResult> SendAsync(string verb, CancellationToken ct)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct);

            var payload = Encoding.UTF8.GetBytes(verb);
            await pipe.WriteAsync(payload, ct);
            await pipe.FlushAsync(ct);

            var buffer = new byte[64];
            var read = await pipe.ReadAsync(buffer, ct);
            var response = Encoding.UTF8.GetString(buffer, 0, read).Trim();

            return response == "OK"
                ? ExecutionResult.Ok(verb.ToLowerInvariant())
                : ExecutionResult.Fail("session_bridge", $"The companion reported: {response}");
        }
        catch (TimeoutException)
        {
            logger.LogWarning("The PCConnect companion is not running, so {Verb} could not be performed", verb);
            return ExecutionResult.Fail("companion_offline",
                "Nobody is signed in on this PC, so it cannot be locked or signed out.");
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Session bridge failed for {Verb}", verb);
            return ExecutionResult.Fail("session_bridge", "Could not reach the PCConnect companion.");
        }
    }
}

/// <summary>
/// The companion side of the bridge. Runs in the user's session and performs the
/// two session actions.
///
/// The pipe is restricted to the interactive user and to SYSTEM, so another
/// standard user on the same machine cannot lock this one's session.
/// </summary>
public sealed class SessionCommandServer(ILogger<SessionCommandServer> logger, Func<string, bool> handler)
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(
                    WindowsIdentity.GetCurrent().User!,
                    PipeAccessRights.FullControl, AccessControlType.Allow));

                await using var pipe = NamedPipeServerStreamAcl.Create(
                    NamedPipeSessionBridge.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

                await pipe.WaitForConnectionAsync(ct);

                var buffer = new byte[64];
                var read = await pipe.ReadAsync(buffer, ct);
                var verb = Encoding.UTF8.GetString(buffer, 0, read).Trim();

                var ok = handler(verb);
                var response = Encoding.UTF8.GetBytes(ok ? "OK" : "FAILED");
                await pipe.WriteAsync(response, ct);
                await pipe.FlushAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Session pipe client disconnected");
            }
        }
    }
}
