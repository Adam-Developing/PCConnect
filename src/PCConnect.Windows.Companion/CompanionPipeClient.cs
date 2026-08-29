using System.Diagnostics;
using System.IO.Pipes;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using PCConnect.Windows.Protocol;

namespace PCConnect.Windows.Companion;

public sealed class CompanionPipeClient(MainWindow window) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ReplayCache replay = new(TimeSpan.FromDays(1));
    private Task? runTask;

    public void Start() => runTask = RunAsync(lifetime.Token);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough, TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(5_000, cancellationToken);
                var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The companion has no Windows SID.");
                var hello = new HelloMessage(PipeProtocol.Version, "hello", Guid.NewGuid(), DateTimeOffset.UtcNow, Environment.ProcessId, sid, PipeProtocol.CreateNonce());
                await PipeFrameCodec.WriteAsync(pipe, hello, PipeJsonContext.Default.HelloMessage, cancellationToken);
                using (var challengeDocument = await PipeFrameCodec.ReadAsync(pipe, cancellationToken))
                {
                    PipeProtocol.EnsureExactProperties(challengeDocument.RootElement, "protocolVersion", "messageType", "requestId", "sentAt", "nonce", "proof");
                    var challenge = challengeDocument.RootElement.Deserialize(PipeJsonContext.Default.ChallengeResponseMessage)
                        ?? throw new InvalidDataException("The service challenge response is invalid.");
                    if (challenge.ProtocolVersion != PipeProtocol.Version || challenge.MessageType != "challenge_response" || challenge.RequestId != hello.RequestId || challenge.Nonce != hello.Nonce)
                        throw new InvalidDataException("The service challenge response does not match the hello.");
                    var secret = CompanionPairingSecret.ReadForCurrentUser();
                    try
                    {
                        if (!PipeProtocol.VerifyProof(secret, hello, challenge.Proof)) throw new UnauthorizedAccessException("The named-pipe server could not prove the service pairing secret.");
                    }
                    finally { CryptographicOperations.ZeroMemory(secret); }
                }
                await window.Dispatcher.InvokeAsync(() => window.ShowStatus("Connected securely. If this Windows account is new, approve the pending identity from an authenticated PCConnect controller."));
                delay = TimeSpan.FromSeconds(2);
                await ReceiveAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception)
            {
                await window.Dispatcher.InvokeAsync(() => window.ShowStatus("Waiting for the PCConnect service or account-owner approval."));
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
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
        try { runTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        lifetime.Dispose();
    }
}
