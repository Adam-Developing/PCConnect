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
    IDeviceCredentialStore credentialStore,
    CompanionPairingSecretStore secrets,
    IConfiguration configuration,
    ILogger<InteractiveSessionBroker> logger) : BackgroundService
{
    private readonly object connectionGate = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly ReplayCache helloReplay = new(TimeSpan.FromMinutes(5));
    private readonly string pipeName = PipeProtocol.ResolvePipeName(configuration["PCConnect:PipeName"]);
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

                var displayLabel = TryAccountLabel(hello.UserSid);
                var isEnrolled = await api.IsEnrolledAsync(stoppingToken);
                Guid? deviceId = null;
                var requiresAuthorization = false;
                if (isEnrolled)
                {
                    deviceId = await api.GetDeviceIdAsync(stoppingToken)
                        ?? throw new InvalidOperationException("The stored device credential has no device ID.");
                    var sidStatus = await api.RegisterWindowsSidAsync(hello.UserSid, displayLabel, stoppingToken);
                    requiresAuthorization = sidStatus.Status != "authorized";
                }
                var status = new AgentStatusMessage(
                    PipeProtocol.Version,
                    "agent_status",
                    hello.RequestId,
                    DateTimeOffset.UtcNow,
                    isEnrolled,
                    api.ApiBaseAddress.AbsoluteUri,
                    deviceId,
                    isEnrolled ? hello.UserSid : null,
                    requiresAuthorization);
                await PipeFrameCodec.WriteAsync(pipe, status, PipeJsonContext.Default.AgentStatusMessage, stoppingToken);
                if (!isEnrolled)
                {
                    if (!await ProvisionDeviceAsync(pipe, hello.UserSid, displayLabel, stoppingToken)) continue;
                }
                else if (requiresAuthorization)
                {
                    CandidatePending(logger, HashSid(hello.UserSid), null);
                    if (!await WaitForSidAuthorizationAsync(hello.UserSid, displayLabel, stoppingToken)) continue;
                }
                var ready = new AgentReadyMessage(PipeProtocol.Version, "agent_ready", Guid.NewGuid(), DateTimeOffset.UtcNow);
                await PipeFrameCodec.WriteAsync(pipe, ready, PipeJsonContext.Default.AgentReadyMessage, stoppingToken);
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
            catch (UnauthorizedAccessException exception) when (pipe is null)
            {
                throw new InvalidOperationException(
                    $"PCConnect could not create the local pipe '{pipeName}'. Another Agent instance is probably already running. Stop that instance or configure a unique PCConnect:PipeName for both the Agent and Companion.",
                    exception);
            }
            catch (Exception exception)
            {
                PipeFailure(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task<bool> ProvisionDeviceAsync(NamedPipeServerStream pipe, string windowsSid, string? displayLabel, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var document = await PipeFrameCodec.ReadAsync(pipe, timeout.Token);
        PipeProtocol.EnsureExactProperties(document.RootElement, "protocolVersion", "messageType", "requestId", "sentAt", "deviceId", "accessToken", "accessTokenExpiresAt", "refreshToken", "refreshTokenExpiresAt");
        var request = document.RootElement.Deserialize(PipeJsonContext.Default.ProvisionDeviceRequestMessage)
            ?? throw new InvalidDataException("The device provisioning request is invalid.");
        if (request.ProtocolVersion != PipeProtocol.Version || request.MessageType != "provision_device" || request.RequestId == Guid.Empty || request.DeviceId == Guid.Empty)
            throw new InvalidDataException("The device provisioning request has an unsupported version or shape.");
        if ((DateTimeOffset.UtcNow - request.SentAt).Duration() > PipeProtocol.MaximumClockSkew ||
            request.AccessTokenExpiresAt <= DateTimeOffset.UtcNow || request.RefreshTokenExpiresAt <= DateTimeOffset.UtcNow ||
            request.AccessToken.Length is < 20 or > 4096 || request.RefreshToken.Length is < 20 or > 4096)
            throw new InvalidDataException("The device provisioning credential is invalid or expired.");

        var result = new ProvisionDeviceResultMessage(PipeProtocol.Version, "provision_device_result", request.RequestId, DateTimeOffset.UtcNow, false, null, false);
        var credentialSaved = false;
        try
        {
            if (await credentialStore.LoadAsync(cancellationToken) is not null)
                result = result with { ErrorCode = "already_enrolled" };
            else
            {
                await credentialStore.SaveAsync(new StoredDeviceCredential(
                    request.DeviceId,
                    request.AccessToken,
                    request.AccessTokenExpiresAt,
                    request.RefreshToken,
                    request.RefreshTokenExpiresAt), cancellationToken);
                credentialSaved = true;
                var sidStatus = await api.RegisterWindowsSidAsync(windowsSid, displayLabel, cancellationToken);
                result = result with
                {
                    Succeeded = true,
                    WindowsSid = windowsSid,
                    RequiresAuthorization = sidStatus.Status != "authorized"
                };
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or HttpRequestException)
        {
            if (credentialSaved)
            {
                try { await credentialStore.DeleteAsync(cancellationToken); }
                catch (Exception deleteException) when (deleteException is IOException or UnauthorizedAccessException) { }
                api.ForgetCredential();
            }
            result = result with { ErrorCode = credentialSaved ? "device_registration_failed" : "credential_store_failed" };
        }
        await PipeFrameCodec.WriteAsync(pipe, result, PipeJsonContext.Default.ProvisionDeviceResultMessage, cancellationToken);
        if (!result.Succeeded || !result.RequiresAuthorization) return result.Succeeded;

        CandidatePending(logger, HashSid(windowsSid), null);
        return await WaitForSidAuthorizationAsync(windowsSid, displayLabel, cancellationToken);
    }

    private async Task<bool> WaitForSidAuthorizationAsync(string windowsSid, string? displayLabel, CancellationToken cancellationToken)
    {
        var authorizationDeadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < authorizationDeadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            var sidStatus = await api.RegisterWindowsSidAsync(windowsSid, displayLabel, cancellationToken);
            if (sidStatus.Status == "authorized") return true;
        }
        return false;
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

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        var serviceSid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The service has no Windows SID.");
        security.AddAccessRule(new PipeAccessRule(serviceSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
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
