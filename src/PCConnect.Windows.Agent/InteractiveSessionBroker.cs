using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using PCConnect.Contracts.V2;
using PCConnect.Windows.Protocol;

namespace PCConnect.Windows.Agent;

public sealed partial class InteractiveSessionBroker(
    AgentApiClient api,
    CompanionPairingSecretStore secrets,
    ILogger<InteractiveSessionBroker> logger) : BackgroundService
{
    private readonly object connectionGate = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly ReplayCache helloReplay = new(TimeSpan.FromMinutes(5));
    private NamedPipeServerStream? activePipe;
    private static readonly Action<ILogger, string, Exception?> CandidatePending = LoggerMessage.Define<string>(LogLevel.Information, new EventId(9201, nameof(CandidatePending)), "Windows SID candidate {SidHash} is awaiting account-owner authorization");
    private static readonly Action<ILogger, Exception?> PipeFailure = LoggerMessage.Define(LogLevel.Warning, new EventId(9202, nameof(PipeFailure)), "Companion named-pipe connection rejected or lost");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                var hello = await AuthenticateLocalCallerAsync(pipe, stoppingToken);
                var secret = secrets.GetOrCreate(hello.UserSid);
                try
                {
                    var response = new ChallengeResponseMessage(PipeProtocol.Version, "challenge_response", hello.RequestId, DateTimeOffset.UtcNow, hello.Nonce, PipeProtocol.CreateProof(secret, hello));
                    await PipeFrameCodec.WriteAsync(pipe, response, PipeJsonContext.Default.ChallengeResponseMessage, stoppingToken);
                }
                finally { CryptographicOperations.ZeroMemory(secret); }

                var sidStatus = await api.RegisterWindowsSidAsync(hello.UserSid, TryAccountLabel(hello.UserSid), stoppingToken);
                if (sidStatus.Status != "authorized")
                {
                    CandidatePending(logger, HashSid(hello.UserSid), null);
                    continue;
                }
                lock (connectionGate)
                {
                    activePipe?.Dispose();
                    activePipe = pipe;
                    pipe = null;
                }
                while (!stoppingToken.IsCancellationRequested && CurrentPipeIsConnected())
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { PipeFailure(logger, exception); }
            finally { pipe?.Dispose(); }
        }
    }

    public async Task<LocalExecutionResult> ExecuteAsync(Command command, CancellationToken cancellationToken)
    {
        if (command.Type is not (CommandType.Lock or CommandType.SignOut)) return new(false, CommandFailureCode.Unsupported);
        if (command.ExpiresAt <= DateTimeOffset.UtcNow) return new(false, CommandFailureCode.Expired);
        if (!await operationGate.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)) return new(false, CommandFailureCode.NoInteractiveSession);
        try
        {
            var pipe = CurrentPipe();
            if (pipe is null) return new(false, CommandFailureCode.NoInteractiveSession);
            var requestId = Guid.NewGuid();
            var request = new ExecuteRequestMessage(PipeProtocol.Version, "execute_request", requestId, DateTimeOffset.UtcNow, command.Id, command.Type.WireValue(), command.ExpiresAt, command.Id);
            await PipeFrameCodec.WriteAsync(pipe, request, PipeJsonContext.Default.ExecuteRequestMessage, cancellationToken);
            using var document = await PipeFrameCodec.ReadAsync(pipe, cancellationToken);
            PipeProtocol.EnsureExactProperties(document.RootElement, "protocolVersion", "messageType", "requestId", "sentAt", "commandId", "state", "failureCode");
            var result = document.RootElement.Deserialize(PipeJsonContext.Default.ExecuteResultMessage) ?? throw new InvalidDataException("The companion returned an invalid result.");
            if (result.ProtocolVersion != PipeProtocol.Version || result.MessageType != "execute_result" || result.RequestId != requestId || result.CommandId != command.Id)
                throw new InvalidDataException("The companion result does not match the request.");
            return result.State is "succeeded" or "accepted" ? new(true, null) : new(false, ParseFailure(result.FailureCode));
        }
        catch
        {
            DropCurrentPipe();
            return new(false, CommandFailureCode.NoInteractiveSession);
        }
        finally { operationGate.Release(); }
    }

    public async Task<bool> DeliverReminderAsync(ReminderDelivery delivery, CancellationToken cancellationToken)
    {
        if (!await operationGate.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)) return false;
        try
        {
            var pipe = CurrentPipe();
            if (pipe is null) return false;
            var requestId = Guid.NewGuid();
            var request = new ReminderDeliveryMessage(PipeProtocol.Version, "reminder_delivery", requestId, DateTimeOffset.UtcNow, delivery.Id, delivery.OccurrenceAt, delivery.Text);
            await PipeFrameCodec.WriteAsync(pipe, request, PipeJsonContext.Default.ReminderDeliveryMessage, cancellationToken);
            using var document = await PipeFrameCodec.ReadAsync(pipe, cancellationToken);
            PipeProtocol.EnsureExactProperties(document.RootElement, "protocolVersion", "messageType", "requestId", "sentAt", "deliveryId", "state");
            var result = document.RootElement.Deserialize(PipeJsonContext.Default.ReminderAcknowledgementMessage) ?? throw new InvalidDataException("The companion returned an invalid reminder acknowledgement.");
            return result.ProtocolVersion == PipeProtocol.Version && result.MessageType == "reminder_acknowledgement" && result.RequestId == requestId && result.DeliveryId == delivery.Id && result.State == "displayed";
        }
        catch { DropCurrentPipe(); return false; }
        finally { operationGate.Release(); }
    }

    private async Task<HelloMessage> AuthenticateLocalCallerAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var document = await PipeFrameCodec.ReadAsync(pipe, timeout.Token);
        var hello = PipeProtocol.ReadAndValidateHello(document, DateTimeOffset.UtcNow);
        var nonceDigest = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(hello.Nonce));
        if (!helloReplay.TryAccept(new Guid(nonceDigest.AsSpan(0, 16)), DateTimeOffset.UtcNow)) throw new InvalidDataException("The hello nonce was replayed.");
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != hello.ProcessId)
            throw new UnauthorizedAccessException("The claimed companion PID does not match the pipe client.");
        string? callerSid = null;
        pipe.RunAsClient(() => callerSid = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User?.Value);
        if (!string.Equals(callerSid, hello.UserSid, StringComparison.Ordinal)) throw new UnauthorizedAccessException("The claimed companion SID does not match its process token.");
        using var process = Process.GetProcessById(hello.ProcessId);
        if (process.SessionId != unchecked((int)WTSGetActiveConsoleSessionId())) throw new UnauthorizedAccessException("The companion is not in the active console session.");
        return hello;
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        var serviceSid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The service has no Windows SID.");
        security.AddAccessRule(new PipeAccessRule(serviceSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(PipeProtocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough, 0, 0, security);
    }

    private NamedPipeServerStream? CurrentPipe() { lock (connectionGate) return activePipe is { IsConnected: true } ? activePipe : null; }
    private bool CurrentPipeIsConnected() { lock (connectionGate) return activePipe is { IsConnected: true }; }
    private void DropCurrentPipe() { lock (connectionGate) { activePipe?.Dispose(); activePipe = null; } }

    private static string? TryAccountLabel(string sid)
    {
        try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { return null; }
    }

    private static string HashSid(string sid) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sid))).ToLowerInvariant();
    private static CommandFailureCode ParseFailure(string? value) => value switch
    {
        "permission_denied" => CommandFailureCode.PermissionDenied,
        "expired" => CommandFailureCode.Expired,
        "local_replay" => CommandFailureCode.LocalReplay,
        "unsupported" => CommandFailureCode.Unsupported,
        "execution_failed" => CommandFailureCode.ExecutionFailed,
        _ => CommandFailureCode.NoInteractiveSession
    };

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out int clientProcessId);

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    public override void Dispose() { DropCurrentPipe(); operationGate.Dispose(); base.Dispose(); }
}
