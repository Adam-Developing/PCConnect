using System.Net;

namespace PCConnect.Core;

/// <summary>
/// The stable, machine-readable error codes of the v2 API (04-api-contract.md §3.1).
/// Clients switch on these; they never switch on <c>message</c>.
/// </summary>
public static class ErrorCodes
{
    // request shape
    public const string ValidationFailed = "request.validation_failed";
    public const string Malformed = "request.malformed";
    public const string IdempotencyConflict = "request.idempotency_conflict";
    public const string RateLimited = "request.rate_limited";
    public const string PayloadTooLarge = "request.payload_too_large";

    // authentication and authorisation
    public const string AuthInvalidCredentials = "auth.invalid_credentials";
    public const string AuthAccountLocked = "auth.account_locked";
    public const string AuthTokenInvalid = "auth.token_invalid";
    public const string AuthTokenExpired = "auth.token_expired";
    public const string AuthTokenReused = "auth.token_reused";
    public const string AuthScopeInsufficient = "auth.scope_insufficient";
    public const string AuthStepUpRequired = "auth.step_up_required";
    public const string AuthStepUpInvalid = "auth.step_up_invalid";
    public const string AuthPasswordPolicy = "auth.password_policy";
    public const string AuthLegacyHashRejected = "auth.legacy_hash_rejected";
    public const string AuthChallengeInvalid = "auth.challenge_invalid";
    public const string AuthEmailUnverified = "auth.email_unverified";

    // passkeys
    public const string PasskeyChallengeInvalid = "passkey.challenge_invalid";
    public const string PasskeyVerificationFailed = "passkey.verification_failed";
    public const string PasskeyUnknownCredential = "passkey.unknown_credential";
    public const string PasskeyCounterRegressed = "passkey.counter_regressed";

    // accounts
    public const string AccountExists = "account.already_exists";
    public const string AccountNotFound = "account.not_found";

    // devices
    public const string DeviceNotFound = "device.not_found";
    public const string DeviceNotPaired = "device.not_paired";
    public const string DeviceRevoked = "device.revoked";
    public const string DeviceNameConflict = "device.name_conflict";
    public const string PairingCodeInvalid = "device.pairing_code_invalid";
    public const string PairingNotClaimed = "device.pairing_not_claimed";
    public const string PairingAlreadyCollected = "device.pairing_already_collected";

    // commands
    public const string CommandNotFound = "command.not_found";
    public const string CommandTypeNotAllowed = "command.type_not_allowed";
    public const string CommandAlreadyTerminal = "command.already_terminal";
    public const string CommandExpired = "command.expired";
    public const string CommandTtlInvalid = "command.ttl_invalid";

    // reminders
    public const string ReminderNotFound = "reminder.not_found";
    public const string ReminderRruleInvalid = "reminder.rrule_invalid";
    public const string ReminderTimezoneInvalid = "reminder.timezone_invalid";
    public const string ReminderBodyTooLong = "reminder.body_too_long";

    // platform
    public const string Internal = "server.internal_error";
    public const string Unavailable = "server.unavailable";
    public const string Gone = "server.endpoint_removed";
}

/// <summary>One field-level detail attached to an error envelope.</summary>
public sealed record ErrorDetail(string Field, string Issue);

/// <summary>
/// Every failure the API returns deliberately, carrying the status code and the
/// stable error code together so a handler cannot accidentally return a 500 for
/// a validation problem.
/// </summary>
public class AppException : Exception
{
    public AppException(
        string code,
        string message,
        HttpStatusCode status = HttpStatusCode.BadRequest,
        IReadOnlyList<ErrorDetail>? details = null,
        TimeSpan? retryAfter = null)
        : base(message)
    {
        Code = code;
        Status = status;
        Details = details ?? [];
        RetryAfter = retryAfter;
    }

    public string Code { get; }
    public HttpStatusCode Status { get; }
    public IReadOnlyList<ErrorDetail> Details { get; }
    public TimeSpan? RetryAfter { get; }

    public static AppException Validation(string message, params ErrorDetail[] details) =>
        new(ErrorCodes.ValidationFailed, message, HttpStatusCode.BadRequest, details);

    public static AppException Unauthorized(string code, string message) =>
        new(code, message, HttpStatusCode.Unauthorized);

    public static AppException Forbidden(string code, string message) =>
        new(code, message, HttpStatusCode.Forbidden);

    /// <summary>
    /// 404 is used for "not visible to this caller" as well as "does not exist".
    /// The two are deliberately indistinguishable across an ownership boundary so
    /// the API is not an existence oracle (04 §3.1).
    /// </summary>
    public static AppException NotFound(string code, string message) =>
        new(code, message, HttpStatusCode.NotFound);

    public static AppException Conflict(string code, string message) =>
        new(code, message, HttpStatusCode.Conflict);

    public static AppException Unprocessable(string code, string message) =>
        new(code, message, HttpStatusCode.UnprocessableEntity);

    public static AppException TooManyRequests(string message, TimeSpan retryAfter) =>
        new(ErrorCodes.RateLimited, message, HttpStatusCode.TooManyRequests, null, retryAfter);
}
