using System.Text.Json;

namespace PCConnect.Contracts.V2;

public sealed record RealtimeEventEnvelope(
    Guid EventId,
    string EventType,
    Guid EntityId,
    long EntityVersion,
    DateTimeOffset OccurredAt,
    JsonElement Payload);

public sealed record RealtimeDispatchMessage(
    string TargetKind,
    Guid TargetId,
    RealtimeEventEnvelope Envelope);

public static class RealtimeChannels
{
    public const string Dispatch = "pcconnect:v2:realtime";
}
