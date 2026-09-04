package uk.co.adamkhattab.pcconnect.data

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * The v2 wire contract, mirroring `PCConnect.Core.Contracts`.
 *
 * These are generated from `docs/architecture/openapi/pcconnect-v2.yaml` in CI
 * and checked in, so a field that changes shape on the server fails the build
 * here rather than at runtime on someone's phone (C-5, 04 §1). Everything is
 * camelCase, every instant is RFC 3339 UTC, and every identifier is a UUIDv7
 * string — never an auto-increment id.
 */

@Serializable
data class ErrorEnvelope(val error: ErrorBody)

@Serializable
data class ErrorBody(
    val code: String,
    val message: String,
    val requestId: String,
    val details: List<ErrorDetail>? = null,
)

@Serializable
data class ErrorDetail(val field: String, val issue: String)

@Serializable
data class Page<T>(val items: List<T>, val nextCursor: String? = null)

// ── auth ─────────────────────────────────────────────────────────────────────

@Serializable
data class LoginRequest(
    val login: String,
    // The plaintext, over TLS. This app does not hash: while a client hashes,
    // the hash *is* the password (S1-03). The Java app it replaces computed a
    // SHA-256 in `LoginActivity.sha256Hash` and sent that.
    val password: String? = null,
    val legacyPasswordHash: String? = null,
    val clientKind: String = "mobile",
    val clientVersion: String = "",
)

@Serializable
data class RegisterRequest(
    val username: String,
    val email: String,
    val password: String,
    val displayName: String? = null,
    val timezone: String? = null,
    val marketingOptIn: Boolean = false,
)

@Serializable
data class TokenPair(
    val accessToken: String,
    val expiresInSeconds: Int,
    val refreshToken: String,
    val tokenType: String,
    val scopes: List<String>,
    val user: Profile? = null,
)

@Serializable
data class RefreshRequest(val refreshToken: String)

@Serializable
data class LogoutRequest(val refreshToken: String)

@Serializable
data class ChangePasswordRequest(val currentPassword: String, val newPassword: String)

@Serializable
data class ForgotPasswordRequest(val email: String)

@Serializable
data class StepUpChallenge(
    val challengeId: String,
    val methods: List<String>,
    val expiresInSeconds: Int,
)

@Serializable
data class StepUpVerifyRequest(
    val challengeId: String,
    val method: String,
    val password: String? = null,
)

@Serializable
data class StepUpToken(val stepUpToken: String, val expiresInSeconds: Int, val method: String)

// ── devices ──────────────────────────────────────────────────────────────────

@Serializable
data class Device(
    val id: String,
    val displayName: String,
    val platform: String,
    val osVersion: String,
    val agentVersion: String,
    val status: String,
    val isOnline: Boolean,
    val lastSeenAt: String? = null,
    val pairedAt: String,
    val allowedCommands: List<String>,
)

@Serializable
data class PairClaimRequest(val pairingCode: String, val displayName: String? = null)

@Serializable
data class PairClaimResponse(val deviceId: String, val displayName: String)

@Serializable
data class UpdateDeviceRequest(val displayName: String? = null, val allowedCommands: List<String>? = null)

// ── commands ─────────────────────────────────────────────────────────────────

@Serializable
data class IssueCommandRequest(
    // Generated on the phone before sending, which is what makes a retry — or
    // the offline queue replaying on reconnect — safe (02 §3.2).
    val id: String,
    val deviceId: String,
    val type: String,
    val ttlSeconds: Int? = null,
    val stepUpToken: String? = null,
)

@Serializable
data class Command(
    val id: String,
    val deviceId: String,
    val type: String,
    val status: String,
    val riskTier: String,
    val issuedAt: String,
    val expiresAt: String,
    val deliveredAt: String? = null,
    val ackedAt: String? = null,
    val resultCode: String? = null,
    val resultMessage: String? = null,
)

// ── reminders ────────────────────────────────────────────────────────────────

@Serializable
data class Reminder(
    val id: String,
    val body: String,
    val dueAt: String,
    val dueLocalTime: String,
    val timezone: String,
    val rrule: String? = null,
    val recurrenceUntil: String? = null,
    val isCompleted: Boolean,
    val completedAt: String? = null,
    val createdAt: String,
    val updatedAt: String,
    /**
     * The PCs this reminder shows on, or null for all of them. A server that
     * does not target reminders never sends it, and null is exactly what that
     * server does: show every reminder on every screen.
     */
    val deviceIds: List<String>? = null,
) {
    fun showsOn(deviceId: String): Boolean = deviceIds?.contains(deviceId) ?: true
}

@Serializable
data class CreateReminderRequest(
    val body: String,
    val dueAt: String,
    val timezone: String? = null,
    val rrule: String? = null,
    val recurrenceUntil: String? = null,
    /**
     * Which PCs the reminder shows on, or null for every PC on the account.
     *
     * Additive, and omitted entirely when null (`explicitNulls = false`), so a
     * server that does not know the field is unaffected. The app only offers
     * the choice when discovery advertises [Capabilities.REMINDER_TARGETS]:
     * a picker whose choice the server ignores is worse than no picker.
     */
    val deviceIds: List<String>? = null,
)

@Serializable
data class CompleteReminderRequest(val completed: Boolean = true, val occurrenceAt: String? = null)

// ── account and meta ─────────────────────────────────────────────────────────

@Serializable
data class Profile(
    val id: String,
    val username: String,
    val email: String,
    val isEmailVerified: Boolean,
    val displayName: String,
    val timezone: String,
    val locale: String,
    val status: String,
    val marketingOptIn: Boolean,
    val createdAt: String,
)

@Serializable
data class Discovery(
    val apiVersion: String,
    val realtimeUrl: String,
    val minimumSupportedClient: Map<String, String>,
    val recommendedClient: Map<String, String>,
    val legacySunset: Map<String, String?>,
    val capabilities: List<String>,
    val serverTime: String,
)

// ── realtime (05 §3) ─────────────────────────────────────────────────────────

@Serializable
data class RealtimeEnvelope<T>(
    @SerialName("v") val version: Int,
    val id: String,
    val at: String,
    val data: T,
)

@Serializable
data class CommandStatusEvent(
    val id: String,
    val deviceId: String,
    val status: String,
    val resultCode: String? = null,
    val resultMessage: String? = null,
)

@Serializable
data class DevicePresenceEvent(val deviceId: String, val isOnline: Boolean)

@Serializable
data class ReminderDueEvent(val reminderId: String, val body: String, val dueAt: String)

/**
 * A reminder created, changed or deleted somewhere else on this account —
 * the companion, another phone. The id is always present; the reminder is
 * absent for a deletion.
 */
@Serializable
data class ReminderChangedEvent(
    val type: String,
    val reminder: Reminder? = null,
    val reminderId: String,
)

/** The closed command vocabulary, mirrored so the UI can label and group it. */
object CommandTypes {
    const val SHUTDOWN = "shutdown"
    const val RESTART = "restart"
    const val SIGN_OUT = "signout"
    const val LOCK = "lock"
    const val SLEEP = "sleep"
    const val HIBERNATE = "hibernate"

    val ALL = listOf(LOCK, SLEEP, SIGN_OUT, HIBERNATE, RESTART, SHUTDOWN)

    /**
     * Commands that end the session or the power state without warning. These
     * need a confirmation the server will check (ADR-0011), and the UI marks
     * them apart so "shut down" is never one careless tap from "lock".
     */
    val DESTRUCTIVE = setOf(SHUTDOWN, RESTART, SIGN_OUT, HIBERNATE)

    fun label(type: String): String = when (type) {
        SHUTDOWN -> "Shut down"
        RESTART -> "Restart"
        SIGN_OUT -> "Sign out"
        LOCK -> "Lock"
        SLEEP -> "Sleep"
        HIBERNATE -> "Hibernate"
        else -> type
    }
}

/** Capabilities the server advertises in its discovery document (04 §2). */
object Capabilities {
    /** Reminders can name the devices they show on. */
    const val REMINDER_TARGETS = "reminders.targets"
}

/** Stable error codes the UI switches on. It never switches on `message`. */
object ErrorCodes {
    const val STEP_UP_REQUIRED = "auth.step_up_required"
    const val STEP_UP_INVALID = "auth.step_up_invalid"
    const val TOKEN_INVALID = "auth.token_invalid"
    const val TOKEN_EXPIRED = "auth.token_expired"
    const val TOKEN_REUSED = "auth.token_reused"
    const val INVALID_CREDENTIALS = "auth.invalid_credentials"
    const val RATE_LIMITED = "request.rate_limited"
    const val DEVICE_REVOKED = "device.revoked"
    const val PAIRING_CODE_INVALID = "device.pairing_code_invalid"
    const val COMMAND_TYPE_NOT_ALLOWED = "command.type_not_allowed"
}
