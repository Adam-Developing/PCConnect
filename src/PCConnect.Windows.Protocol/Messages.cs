using System.Text.Json.Serialization;

namespace PCConnect.Windows.Protocol;

public abstract record PipeMessage(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    DateTimeOffset SentAt);

public sealed record HelloMessage(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    DateTimeOffset SentAt,
    int ProcessId,
    string UserSid,
    string Nonce) : PipeMessage(ProtocolVersion, MessageType, RequestId, SentAt);

public sealed record ChallengeResponseMessage(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    DateTimeOffset SentAt,
    string Nonce,
    string Proof) : PipeMessage(ProtocolVersion, MessageType, RequestId, SentAt);

public sealed record ExecuteRequestMessage(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    DateTimeOffset SentAt,
    Guid CommandId,
    string CommandType,
    DateTimeOffset ExpiresAt,
    Guid LocalReplayKey) : PipeMessage(ProtocolVersion, MessageType, RequestId, SentAt);

public sealed record ExecuteResultMessage(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    DateTimeOffset SentAt,
    Guid CommandId,
    string State,
    string? FailureCode = null) : PipeMessage(ProtocolVersion, MessageType, RequestId, SentAt);

public sealed record ReminderDeliveryMessage(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    DateTimeOffset SentAt,
    Guid DeliveryId,
    DateTimeOffset OccurrenceAt,
    string Text) : PipeMessage(ProtocolVersion, MessageType, RequestId, SentAt);

public sealed record ReminderAcknowledgementMessage(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    DateTimeOffset SentAt,
    Guid DeliveryId,
    string State) : PipeMessage(ProtocolVersion, MessageType, RequestId, SentAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(ChallengeResponseMessage))]
[JsonSerializable(typeof(ExecuteRequestMessage))]
[JsonSerializable(typeof(ExecuteResultMessage))]
[JsonSerializable(typeof(ReminderDeliveryMessage))]
[JsonSerializable(typeof(ReminderAcknowledgementMessage))]
public partial class PipeJsonContext : JsonSerializerContext;
