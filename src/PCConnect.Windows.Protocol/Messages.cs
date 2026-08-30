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

public sealed record AgentStatusMessage(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    DateTimeOffset SentAt,
    bool IsEnrolled,
    string ApiBaseUrl,
    Guid? DeviceId,
    string? WindowsSid,
    bool RequiresAuthorization) : PipeMessage(ProtocolVersion, MessageType, RequestId, SentAt);

public sealed record ProvisionDeviceRequestMessage(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    DateTimeOffset SentAt,
    Guid DeviceId,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt) : PipeMessage(ProtocolVersion, MessageType, RequestId, SentAt);

public sealed record ProvisionDeviceResultMessage(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    DateTimeOffset SentAt,
    bool Succeeded,
    string? WindowsSid,
    bool RequiresAuthorization,
    string? ErrorCode = null) : PipeMessage(ProtocolVersion, MessageType, RequestId, SentAt);

public sealed record AgentReadyMessage(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    DateTimeOffset SentAt) : PipeMessage(ProtocolVersion, MessageType, RequestId, SentAt);

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
[JsonSerializable(typeof(AgentStatusMessage))]
[JsonSerializable(typeof(ProvisionDeviceRequestMessage))]
[JsonSerializable(typeof(ProvisionDeviceResultMessage))]
[JsonSerializable(typeof(AgentReadyMessage))]
[JsonSerializable(typeof(ExecuteRequestMessage))]
[JsonSerializable(typeof(ExecuteResultMessage))]
[JsonSerializable(typeof(ReminderDeliveryMessage))]
[JsonSerializable(typeof(ReminderAcknowledgementMessage))]
public partial class PipeJsonContext : JsonSerializerContext;
