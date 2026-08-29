using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCConnect.Contracts.V2;

public sealed record ClientDescriptor(PlatformType Platform, string Name, string Version);
public sealed record PasswordLoginRequest(string Login, string Password, ClientDescriptor Client);
public sealed record RegistrationRequest(string Username, string Email, string DisplayName, string Password, string Timezone, bool MarketingOptIn, ClientDescriptor Client, DateOnly? DateOfBirth = null);
public sealed record RefreshRequest(string RefreshToken);
public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);
public sealed record PasswordResetRequest(string Token, string NewPassword);
public sealed record EmailRequest(string Email);
public sealed record TokenRequest(string Token);

public sealed record TokenPair(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt, Guid SessionId);
public sealed record DeviceTokenPair(Guid DeviceId, string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

public sealed record Profile(Guid Id, string Username, string Email, bool EmailVerified, string DisplayName, DateOnly? DateOfBirth, bool MarketingOptIn, string Timezone, bool TimezoneAssumed, DateTimeOffset CreatedAt, long Version);
public sealed record ProfileUpdate(
    string? DisplayName = null,
    string? Email = null,
    PatchValue<DateOnly?> DateOfBirth = default,
    bool? MarketingOptIn = null,
    string? Timezone = null,
    long? ExpectedVersion = null);
public sealed record Session(Guid Id, PlatformType Platform, string ClientName, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt, DateTimeOffset ExpiresAt, bool Current);
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record DeviceEnrollmentRequest(PlatformType Platform, string DisplayName, string AgentVersion, int ProtocolVersion, IReadOnlyList<DeviceCapability> Capabilities, string? Timezone = null);
public sealed record DeviceEnrollment(string DeviceCode, string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt, int PollIntervalSeconds);
public sealed record DeviceCodeRequest(string DeviceCode);
public sealed record Device(Guid Id, PlatformType Platform, string DisplayName, string AgentVersion, int ProtocolVersion, string? Timezone, IReadOnlyList<DeviceCapability> Capabilities, string Status, DateTimeOffset? LastSeenAt, DateTimeOffset CreatedAt, long Version);
public sealed record DeviceUpdate(string? DisplayName = null, long? ExpectedVersion = null);
public sealed record Heartbeat(Guid AgentInstanceId, string AgentVersion, int ProtocolVersion, IReadOnlyList<DeviceCapability> Capabilities, DateTimeOffset SentAt);
public sealed record WindowsSidCandidateRequest(string WindowsSid, string? DisplayLabel = null);
public sealed record WindowsSidStatus(string WindowsSid, string? DisplayLabel, string Status, DateTimeOffset? ObservedAt, DateTimeOffset? AuthorizedAt);

public sealed record StepUpIntent(StepUpIntentType Intent, Guid IdempotencyKey, Guid? DeviceId = null, CommandType? CommandType = null);
public sealed record StepUpOptions(Guid IntentId, DateTimeOffset ExpiresAt, IReadOnlyList<string> Methods, JsonElement? PasskeyOptions = null);
public sealed record StepUpCompletion(Guid IntentId, string Method, JsonElement Proof);
public sealed record StepUpGrant(string Grant, DateTimeOffset ExpiresAt);

public sealed record CommandCreate(CommandType Type, int? ExpiresInSeconds = null, bool? ExplicitlyConfirmed = null);
public sealed record Command(Guid Id, Guid DeviceId, CommandType Type, CommandStatus Status, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, DateTimeOffset? ClaimedUntil, DateTimeOffset? AcceptedAt, DateTimeOffset? FinishedAt, CommandFailureCode? FailureCode, long Version);
public sealed record CommandClaim(Guid AgentInstanceId);
public sealed record CommandAcknowledgement(string State, Guid AgentInstanceId, Guid LocalReplayKey, CommandFailureCode? FailureCode = null);

public sealed record ReminderWrite(string Text, ReminderTargetMode TargetMode, string Timezone, DateTime LocalStart, IReadOnlyList<Guid>? TargetDeviceIds = null, string? RecurrenceRule = null, long? ExpectedVersion = null);
public sealed record Reminder(
    Guid Id,
    string Text,
    ReminderTargetMode TargetMode,
    IReadOnlyList<Guid> TargetDeviceIds,
    string Timezone,
    bool TimezoneAssumed,
    DateTime LocalStart,
    string? RecurrenceRule,
    DateTimeOffset? NextOccurrenceAt,
    DateTimeOffset CreatedAt,
    long Version,
    string? LastAcknowledgementStatus = null,
    DateTimeOffset? LastAcknowledgedAt = null,
    string? LastAcknowledgedBy = null);
public sealed record ReminderDelivery(Guid Id, Guid ReminderId, DateTimeOffset OccurrenceAt, string Text, string Status, long Version);
public sealed record ReminderAcknowledgement(string State, DateTimeOffset AcknowledgedAt);

public sealed record Passkey(Guid Id, string DisplayName, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);
public sealed record WebAuthnOptions(Guid ChallengeId, JsonElement PublicKey);
public sealed record WebAuthnCredential(Guid ChallengeId, JsonElement Credential);
public sealed record PasskeyAuthenticationOptionsRequest(ClientDescriptor Client, string? LoginHint = null);

public sealed record Health(string Status = "ok");
public sealed record VersionInfo(string Release, string ApiContract = "2.0.0", string RealtimeContract = "2.0.0");
public sealed record ExportJob(Guid Id, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, Uri? DownloadUrl = null);

public sealed record RealtimeEnvelope<T>(Guid EventId, Guid EntityId, long EntityVersion, DateTimeOffset OccurredAt, T Payload);
public sealed record CommandAvailable(Guid DeviceId);
public sealed record CommandStatusChanged(Guid UserId, CommandStatus Status);
public sealed record DevicePresenceChanged(Guid UserId, string Status, DateTimeOffset? LastSeenAt);
public sealed record ReminderAvailable(Guid DeviceId, Guid DeliveryId);
public sealed record SessionRevoked(Guid UserId, Guid? SessionId, Guid? DeviceId, string Reason);

public sealed record Problem(
    string Type,
    string Title,
    int Status,
    string Code,
    string CorrelationId,
    string? Detail = null,
    string? Instance = null,
    IReadOnlyList<object>? Errors = null);
