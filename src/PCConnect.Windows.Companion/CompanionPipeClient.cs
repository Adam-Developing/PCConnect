using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using PCConnect.Contracts.V2;
using PCConnect.Windows.Protocol;

namespace PCConnect.Windows.Companion;

public sealed class CompanionPipeClient(MainWindow window, bool startInBackground = false) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ReplayCache replay = new(TimeSpan.FromDays(1));
    private readonly string pipeName = PipeProtocol.ResolvePipeName(
        Environment.GetEnvironmentVariable("PCConnect__PipeName"));
    private readonly Channel<PendingProvision> provisioning = Channel.CreateBounded<PendingProvision>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false
    });
    private volatile bool acceptingProvision;
    private Task? runTask;

    public void Start() => runTask = RunAsync(lifetime.Token);

    public async Task<ProvisioningOutcome> ProvisionDeviceAsync(DeviceTokenPair credential, CancellationToken cancellationToken)
    {
        if (!acceptingProvision) throw new InvalidOperationException("The Windows service is not ready for enrollment.");
        var completion = new TaskCompletionSource<ProvisioningOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingProvision(credential, completion);
        if (!provisioning.Writer.TryWrite(pending)) throw new InvalidOperationException("A device enrollment is already in progress.");
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            PendingProvision? pending = null;
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough, TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(5_000, cancellationToken);
                var hello = await AuthenticateServiceAsync(pipe, cancellationToken);
                var status = await ReadAgentStatusAsync(pipe, hello.RequestId, cancellationToken);
                var enrollmentWasShown = false;

                if (!status.IsEnrolled)
                {
                    enrollmentWasShown = true;
                    acceptingProvision = true;
                    await window.Dispatcher.InvokeAsync(() => window.ShowEnrollment(status.ApiBaseUrl));
                    pending = await provisioning.Reader.ReadAsync(cancellationToken);
                    acceptingProvision = false;
                    var outcome = await SendProvisioningCredentialAsync(pipe, pending, cancellationToken);
                    pending.Completion.TrySetResult(outcome);
                    pending = null;
                }
                else if (status.RequiresAuthorization)
                {
                    enrollmentWasShown = true;
                    if (status.DeviceId is null || string.IsNullOrWhiteSpace(status.WindowsSid))
                        throw new InvalidDataException("The Agent did not provide the pending Windows authorization details.");
                    await window.Dispatcher.InvokeAsync(() => window.ShowAuthorization(
                        status.ApiBaseUrl,
                        status.DeviceId.Value,
                        status.WindowsSid));
                }

                await ReadAgentReadyAsync(pipe, cancellationToken);
                await window.Dispatcher.InvokeAsync(() =>
                    window.ShowConnected(showWindow: enrollmentWasShown || !startInBackground));
                delay = TimeSpan.FromSeconds(2);
                await ReceiveAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                pending?.Completion.TrySetException(exception);
                acceptingProvision = false;
                await window.Dispatcher.InvokeAsync(() => window.ShowWaiting("Waiting for the PCConnect Windows service…"));
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    private static async Task<HelloMessage> AuthenticateServiceAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The companion has no Windows SID.");
        var hello = new HelloMessage(PipeProtocol.Version, "hello", Guid.NewGuid(), DateTimeOffset.UtcNow, Environment.ProcessId, sid, PipeProtocol.CreateNonce());
        await PipeFrameCodec.WriteAsync(pipe, hello, PipeJsonContext.Default.HelloMessage, cancellationToken);
        using var challengeDocument = await PipeFrameCodec.ReadAsync(pipe, cancellationToken);
        PipeProtocol.EnsureExactProperties(challengeDocument.RootElement, "protocolVersion", "messageType", "requestId", "sentAt", "nonce", "proof");
        var challenge = challengeDocument.RootElement.Deserialize(PipeJsonContext.Default.ChallengeResponseMessage)
            ?? throw new InvalidDataException("The service challenge response is invalid.");
        if (challenge.ProtocolVersion != PipeProtocol.Version || challenge.MessageType != "challenge_response" || challenge.RequestId != hello.RequestId || challenge.Nonce != hello.Nonce)
            throw new InvalidDataException("The service challenge response does not match the hello.");
        var secret = CompanionPairingSecret.ReadForCurrentUser();
        try
        {
            if (!PipeProtocol.VerifyProof(secret, hello, challenge.Proof))
                throw new UnauthorizedAccessException("The named-pipe server could not prove the service pairing secret.");
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
        return hello;
    }

    private static async Task<AgentStatusMessage> ReadAgentStatusAsync(NamedPipeClientStream pipe, Guid helloRequestId, CancellationToken cancellationToken)
    {
        using var document = await PipeFrameCodec.ReadAsync(pipe, cancellationToken);
        PipeProtocol.EnsureExactProperties(document.RootElement, "protocolVersion", "messageType", "requestId", "sentAt", "isEnrolled", "apiBaseUrl", "deviceId", "windowsSid", "requiresAuthorization");
        var status = document.RootElement.Deserialize(PipeJsonContext.Default.AgentStatusMessage)
            ?? throw new InvalidDataException("The agent status response is invalid.");
        if (status.ProtocolVersion != PipeProtocol.Version || status.MessageType != "agent_status" || status.RequestId != helloRequestId)
            throw new InvalidDataException("The agent status response does not match the connection.");
        if (status.RequiresAuthorization && (!status.IsEnrolled || status.DeviceId is null || string.IsNullOrWhiteSpace(status.WindowsSid)))
            throw new InvalidDataException("The agent status response contains invalid authorization recovery state.");
        _ = new Uri(status.ApiBaseUrl, UriKind.Absolute);
        return status;
    }

    private static async Task<ProvisioningOutcome> SendProvisioningCredentialAsync(NamedPipeClientStream pipe, PendingProvision pending, CancellationToken cancellationToken)
    {
        var credential = pending.Credential;
        var requestId = Guid.NewGuid();
        var request = new ProvisionDeviceRequestMessage(
            PipeProtocol.Version,
            "provision_device",
            requestId,
            DateTimeOffset.UtcNow,
            credential.DeviceId,
            credential.AccessToken,
            credential.AccessTokenExpiresAt,
            credential.RefreshToken,
            credential.RefreshTokenExpiresAt);
        await PipeFrameCodec.WriteAsync(pipe, request, PipeJsonContext.Default.ProvisionDeviceRequestMessage, cancellationToken);
        using var document = await PipeFrameCodec.ReadAsync(pipe, cancellationToken);
        PipeProtocol.EnsureExactProperties(document.RootElement, "protocolVersion", "messageType", "requestId", "sentAt", "succeeded", "windowsSid", "requiresAuthorization", "errorCode");
        var result = document.RootElement.Deserialize(PipeJsonContext.Default.ProvisionDeviceResultMessage)
            ?? throw new InvalidDataException("The device provisioning result is invalid.");
        if (result.ProtocolVersion != PipeProtocol.Version || result.MessageType != "provision_device_result" || result.RequestId != requestId)
            throw new InvalidDataException("The device provisioning result does not match the request.");
        if (!result.Succeeded)
            throw new InvalidOperationException(result.ErrorCode == "already_enrolled"
                ? "This Windows service is already enrolled."
                : result.ErrorCode == "device_registration_failed"
                    ? "The server could not register this Windows account. No device credential was retained; please try again."
                    : "The Windows service could not store the device credential.");
        return new ProvisioningOutcome(result.WindowsSid, result.RequiresAuthorization);
    }

    private static async Task ReadAgentReadyAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        using var document = await PipeFrameCodec.ReadAsync(pipe, cancellationToken);
        PipeProtocol.EnsureExactProperties(document.RootElement, "protocolVersion", "messageType", "requestId", "sentAt");
        var ready = document.RootElement.Deserialize(PipeJsonContext.Default.AgentReadyMessage)
            ?? throw new InvalidDataException("The agent-ready response is invalid.");
        if (ready.ProtocolVersion != PipeProtocol.Version || ready.MessageType != "agent_ready" || ready.RequestId == Guid.Empty)
            throw new InvalidDataException("The agent-ready response has an unsupported version or shape.");
    }

    private async Task ReceiveAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            using var document = await PipeFrameCodec.ReadAsync(pipe, cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != PipeProtocol.Version ||
                !root.TryGetProperty("messageType", out var type)) throw new InvalidDataException("The service sent an unknown protocol frame.");
            switch (type.GetString())
            {
                case "execute_request": await ExecuteAsync(pipe, root, cancellationToken); break;
                case "reminder_delivery": await DisplayReminderAsync(pipe, root, cancellationToken); break;
                default: throw new InvalidDataException("The service sent an unsupported message type.");
            }
        }
    }

    private async Task ExecuteAsync(NamedPipeClientStream pipe, JsonElement root, CancellationToken cancellationToken)
    {
        PipeProtocol.EnsureExactProperties(root, "protocolVersion", "messageType", "requestId", "sentAt", "commandId", "commandType", "expiresAt", "localReplayKey");
        var request = root.Deserialize(PipeJsonContext.Default.ExecuteRequestMessage) ?? throw new InvalidDataException("The execute request is invalid.");
        string state;
        string? failure = null;
        if (request.SentAt < DateTimeOffset.UtcNow.Subtract(PipeProtocol.MaximumClockSkew) || request.ExpiresAt <= DateTimeOffset.UtcNow) { state = "failed"; failure = "expired"; }
        else if (!replay.TryAccept(request.LocalReplayKey, DateTimeOffset.UtcNow)) { state = "failed"; failure = "local_replay"; }
        else if (request.CommandType == "sign_out")
        {
            state = InteractiveCommandExecutor.SignOut() ? "accepted" : "failed";
            failure = state == "failed" ? "execution_failed" : null;
        }
        else
        {
            var result = InteractiveCommandExecutor.Execute(request.CommandType);
            state = result == "succeeded" ? "succeeded" : "failed";
            failure = result switch { "unsupported" => "unsupported", "failed" => "execution_failed", _ => null };
        }
        var response = new ExecuteResultMessage(PipeProtocol.Version, "execute_result", request.RequestId, DateTimeOffset.UtcNow, request.CommandId, state, failure);
        await PipeFrameCodec.WriteAsync(pipe, response, PipeJsonContext.Default.ExecuteResultMessage, cancellationToken);
    }

    private async Task DisplayReminderAsync(NamedPipeClientStream pipe, JsonElement root, CancellationToken cancellationToken)
    {
        PipeProtocol.EnsureExactProperties(root, "protocolVersion", "messageType", "requestId", "sentAt", "deliveryId", "occurrenceAt", "text");
        var request = root.Deserialize(PipeJsonContext.Default.ReminderDeliveryMessage) ?? throw new InvalidDataException("The reminder delivery is invalid.");
        await window.Dispatcher.InvokeAsync(() => window.ShowReminder(request.Text));
        var response = new ReminderAcknowledgementMessage(PipeProtocol.Version, "reminder_acknowledgement", request.RequestId, DateTimeOffset.UtcNow, request.DeliveryId, "displayed");
        await PipeFrameCodec.WriteAsync(pipe, response, PipeJsonContext.Default.ReminderAcknowledgementMessage, cancellationToken);
    }

    public void Dispose()
    {
        lifetime.Cancel();
        provisioning.Writer.TryComplete();
        try { runTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        lifetime.Dispose();
    }

    private sealed record PendingProvision(DeviceTokenPair Credential, TaskCompletionSource<ProvisioningOutcome> Completion);
}

public sealed record ProvisioningOutcome(string? WindowsSid, bool RequiresAuthorization);
