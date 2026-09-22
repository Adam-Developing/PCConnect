using System.Text.Json.Serialization;

namespace PCConnect.Core.Contracts;

// =============================================================================
// The v2 wire contract.
//
// These records ARE the contract (C-5). The OpenAPI document is generated from
// them at build time by the API, and every first-party .NET client binds to the
// same types, so a field cannot drift between server and client without failing
// to compile. Non-.NET clients (Android) are checked against the generated
// document in CI.
//
// Conventions (04 §3): camelCase JSON, UUIDv7 identifiers, RFC 3339 UTC
// instants, cursor pagination, PATCH with a partial body.
// =============================================================================

/// <summary>The one error envelope (04 §3.1).</summary>
public sealed record ErrorEnvelope(ErrorBody Error);

public sealed record ErrorBody(
    string Code,
    string Message,
    string RequestId,
    IReadOnlyList<ErrorDetailDto>? Details = null);

public sealed record ErrorDetailDto(string Field, string Issue);

/// <summary>Cursor-paginated collection. Empty is <c>items: []</c>, never null and never 204.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

// ── auth ─────────────────────────────────────────────────────────────────────

public sealed record RegisterRequest(
    string Username,
    string Email,
    string Password,
    string? DisplayName = null,
    string? Timezone = null,
    bool MarketingOptIn = false);

/// <summary>
/// <paramref name="Password"/> is the plaintext, over TLS. <paramref name="LegacyPasswordHash"/>
/// exists only for the installed clients that pre-hash with unsalted SHA-256
/// (S1-03); it can never trigger an Argon2id upgrade, because the server never
/// sees the real password on that path (02 §6).
/// </summary>
public sealed record LoginRequest(
    string Login,
    string? Password = null,
    string? LegacyPasswordHash = null,
    string ClientKind = "mobile",
    string ClientVersion = "");

public sealed record TokenPairResponse(
    string AccessToken,
    int ExpiresInSeconds,
    string RefreshToken,
    string TokenType,
    IReadOnlyList<string> Scopes,
    ProfileResponse? User = null);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

public sealed record VerifyEmailRequest(string Token);

// ── passkeys (WebAuthn) ──────────────────────────────────────────────────────

public sealed record PasskeyRegistrationOptions(
    string ChallengeId,
    string Challenge,
    RelyingParty Rp,
    PasskeyUser User,
    IReadOnlyList<PublicKeyCredentialParameter> PubKeyCredParams,
    IReadOnlyList<PublicKeyCredentialDescriptor> ExcludeCredentials,
    AuthenticatorSelection AuthenticatorSelection,
    int TimeoutMilliseconds,
    string Attestation);

public sealed record RelyingParty(string Id, string Name);

public sealed record PasskeyUser(string Id, string Name, string DisplayName);

public sealed record PublicKeyCredentialParameter(string Type, int Alg);

public sealed record PublicKeyCredentialDescriptor(string Type, string Id, IReadOnlyList<string>? Transports = null);

public sealed record AuthenticatorSelection(
    string? AuthenticatorAttachment,
    string ResidentKey,
    bool RequireResidentKey,
    string UserVerification);

public sealed record PasskeyRegistrationRequest(
    string ChallengeId,
    string CredentialId,
    string ClientDataJson,
    string AttestationObject,
    IReadOnlyList<string>? Transports = null,
    string? DisplayName = null);

public sealed record PasskeyAssertionOptions(
    string ChallengeId,
    string Challenge,
    string RpId,
    IReadOnlyList<PublicKeyCredentialDescriptor> AllowCredentials,
    int TimeoutMilliseconds,
    string UserVerification);

public sealed record PasskeyAssertionRequest(
    string ChallengeId,
    string CredentialId,
    string ClientDataJson,
    string AuthenticatorData,
    string Signature,
    string? UserHandle = null,
    string ClientKind = "mobile",
    string ClientVersion = "");

public sealed record PasskeySummary(
    string Id,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    bool IsBackedUp);

// ── step-up ──────────────────────────────────────────────────────────────────

/// <summary>
/// Step-up is required before a destructive command (ADR-0011). The response
/// says which methods this account can satisfy it with, so the client does not
/// have to guess whether a passkey prompt or a password prompt is appropriate.
/// </summary>
public sealed record StepUpChallengeResponse(
    string ChallengeId,
    IReadOnlyList<string> Methods,
    PasskeyAssertionOptions? Passkey,
    int ExpiresInSeconds);

public sealed record StepUpVerifyRequest(
    string ChallengeId,
    string Method,
    string? Password = null,
    PasskeyAssertionRequest? Passkey = null);

public sealed record StepUpTokenResponse(string StepUpToken, int ExpiresInSeconds, string Method);

// ── devices ──────────────────────────────────────────────────────────────────

public sealed record DeviceResponse(
    string Id,
    string DisplayName,
    string Platform,
    string OsVersion,
    string AgentVersion,
    string Status,
    bool IsOnline,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset PairedAt,
    IReadOnlyList<string> AllowedCommands,
    IReadOnlyList<string> PasswordRequiredCommands);

public sealed record UpdateDeviceRequest(
    string? DisplayName = null,
    IReadOnlyList<string>? AllowedCommands = null,
    IReadOnlyList<string>? PasswordRequiredCommands = null);

/// <summary>
/// Adds the PC the caller is signed in on, with no code to read off a screen.
/// The proof of ownership is the sign-in itself (ADR-0013).
/// </summary>
public sealed record DeviceProvisionRequest(
    string RequestedName,
    string Platform = "windows",
    string AgentVersion = "");

/// <summary>
/// The ticket is an opaque, single-use capability. The local agent redeems it, so
/// the device secret goes to the agent and never through whatever asked for it.
/// </summary>
public sealed record DeviceProvisionResponse(
    string DeviceId,
    string DisplayName,
    string ProvisioningTicket,
    int ExpiresInSeconds);

public sealed record DeviceProvisionCompleteRequest(string ProvisioningTicket);

/// <summary>The device secret crosses the wire exactly once, to the local agent.</summary>
public sealed record DeviceProvisionCompleteResponse(string DeviceId, string DeviceSecret, string DisplayName);

public sealed record DeviceTokenRequest(string DeviceId, string DeviceSecret, string AgentVersion = "", string OsVersion = "");

public sealed record HeartbeatRequest(string AgentVersion = "", string OsVersion = "");

// ── commands ─────────────────────────────────────────────────────────────────

/// <summary>
/// <paramref name="Id"/> is generated by the client before sending. That is the
/// load-bearing idempotency property: a retry, or an offline queue replaying on
/// reconnect, returns the existing command instead of issuing a second shutdown
/// (02 §3.2).
/// </summary>
public sealed record IssueCommandRequest(
    string Id,
    string DeviceId,
    string Type,
    IReadOnlyDictionary<string, object?>? Params = null,
    int? TtlSeconds = null,
    string? StepUpToken = null);

public sealed record CommandResponse(
    string Id,
    string DeviceId,
    string Type,
    string Status,
    string RiskTier,
    IReadOnlyDictionary<string, object?>? Params,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? AckedAt,
    string? ResultCode,
    string? ResultMessage);

public sealed record AckCommandRequest(string Outcome, string? ResultCode = null, string? ResultMessage = null);

/// <summary>What an agent receives, over SignalR or by polling. Identical either way.</summary>
public sealed record PendingCommand(
    string Id,
    string Type,
    IReadOnlyDictionary<string, object?>? Params,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ServerTime);

// ── reminders ────────────────────────────────────────────────────────────────

public sealed record ReminderResponse(
    string Id,
    string Body,
    DateTimeOffset DueAt,
    string DueLocalTime,
    string Timezone,
    string? Rrule,
    DateTimeOffset? RecurrenceUntil,
    bool IsCompleted,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    /// <summary>
    /// The devices this reminder shows on, or null for every device on the
    /// account. Null and "all of them" are the same thing, and are what every
    /// reminder written before targeting existed means.
    /// </summary>
    IReadOnlyList<string>? DeviceIds = null);

public sealed record CreateReminderRequest(
    string Body,
    DateTimeOffset DueAt,
    string? Timezone = null,
    string? Rrule = null,
    DateTimeOffset? RecurrenceUntil = null,
    /// <summary>Omit, or send null, for every device on the account.</summary>
    IReadOnlyList<string>? DeviceIds = null);

public sealed record UpdateReminderRequest(
    string? Body = null,
    DateTimeOffset? DueAt = null,
    string? Timezone = null,
    string? Rrule = null,
    DateTimeOffset? RecurrenceUntil = null,
    /// <summary>
    /// Replaces the whole target set. Null leaves it alone; an empty list is
    /// rejected, because "no PCs at all" is a reminder nobody would ever see.
    /// </summary>
    IReadOnlyList<string>? DeviceIds = null);

public sealed record CompleteReminderRequest(bool Completed = true, DateTimeOffset? OccurrenceAt = null);

// ── account ──────────────────────────────────────────────────────────────────

public sealed record ProfileResponse(
    string Id,
    string Username,
    string Email,
    bool IsEmailVerified,
    string DisplayName,
    string Timezone,
    string Locale,
    string Status,
    bool MarketingOptIn,
    DateTimeOffset CreatedAt);

public sealed record UpdateProfileRequest(
    string? DisplayName = null,
    string? Timezone = null,
    string? Locale = null,
    bool? MarketingOptIn = null);

public sealed record SessionResponse(
    string FamilyId,
    string ClientKind,
    string ClientVersion,
    string? DeviceId,
    string? IpFirstSeen,
    DateTimeOffset IssuedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset ExpiresAt,
    bool IsCurrent);

public sealed record AccountExport(
    ProfileResponse Profile,
    IReadOnlyList<DeviceResponse> Devices,
    IReadOnlyList<ReminderResponse> Reminders,
    IReadOnlyList<CommandResponse> Commands,
    IReadOnlyList<SessionResponse> Sessions,
    DateTimeOffset GeneratedAt);

// ── meta ─────────────────────────────────────────────────────────────────────

public sealed record DiscoveryResponse(
    string ApiVersion,
    string RealtimeUrl,
    IReadOnlyDictionary<string, string> MinimumSupportedClient,
    IReadOnlyDictionary<string, string> RecommendedClient,
    IReadOnlyDictionary<string, DateTimeOffset?> LegacySunset,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset ServerTime);

public sealed record HealthResponse(string Status, IReadOnlyDictionary<string, string> Checks);

public sealed record ServerTimeResponse(DateTimeOffset UtcNow, long UnixMilliseconds);

// ── realtime envelope (05 §3) ────────────────────────────────────────────────

/// <summary>
/// Every realtime event carries the same envelope. <c>v</c> lets the event schema
/// evolve without breaking an agent that has not been updated.
/// </summary>
public sealed record RealtimeEvent<T>(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("data")] T Data);

public sealed record CommandStatusEvent(
    string Id,
    string DeviceId,
    string Status,
    string? ResultCode = null,
    string? ResultMessage = null);

public sealed record DevicePresenceEvent(string DeviceId, bool IsOnline);

public sealed record ReminderChangedEvent(string Type, ReminderResponse? Reminder, string ReminderId);

/// <summary>
/// A reminder that has come due, pushed to every client on the account.
///
/// The fan-out is per user because a companion holds a user credential, not a
/// device one, so <see cref="DeviceIds"/> is what lets each PC decide whether
/// the reminder is for the screen it is sitting on. Null means every PC.
/// </summary>
public sealed record ReminderDueEvent(
    string ReminderId,
    string Body,
    DateTimeOffset DueAt,
    IReadOnlyList<string>? DeviceIds = null);
