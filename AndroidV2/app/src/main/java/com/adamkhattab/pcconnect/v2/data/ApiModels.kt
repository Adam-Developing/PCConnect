package com.adamkhattab.pcconnect.v2.data

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement

@Serializable
data class ClientDescriptor(val platform: String = "android", val name: String = "PCConnect Android", val version: String)

@Serializable
data class PasswordLoginRequest(val login: String, val password: String, val client: ClientDescriptor)

@Serializable
data class RegistrationRequest(
    val username: String,
    val email: String,
    val displayName: String,
    val password: String,
    val timezone: String,
    val marketingOptIn: Boolean,
    val client: ClientDescriptor,
)

@Serializable
data class EmailRequest(val email: String)

@Serializable
data class TokenRequest(val token: String)

@Serializable
data class PasswordResetRequest(val token: String, val newPassword: String)

@Serializable
data class RefreshRequest(val refreshToken: String)

@Serializable
data class TokenPair(
    val accessToken: String,
    val accessTokenExpiresAt: String,
    val refreshToken: String,
    val refreshTokenExpiresAt: String,
    val sessionId: String,
)

@Serializable
data class Page<T>(val items: List<T>, val nextCursor: String? = null)

@Serializable
data class DeviceDto(
    val id: String,
    val platform: String,
    val displayName: String,
    val agentVersion: String,
    val protocolVersion: Int,
    val timezone: String? = null,
    val capabilities: List<String>,
    val status: String,
    val lastSeenAt: String? = null,
    val createdAt: String,
    val version: Long,
)

@Serializable
data class WindowsSidStatus(
    val windowsSid: String,
    val displayLabel: String? = null,
    val status: String,
    val observedAt: String? = null,
    val authorizedAt: String? = null,
)

@Serializable
data class CommandCreate(
    val type: String,
    val expiresInSeconds: Int? = null,
    val explicitlyConfirmed: Boolean? = null,
)

@Serializable
data class CommandDto(
    val id: String,
    val deviceId: String,
    val type: String,
    val status: String,
    val issuedAt: String,
    val expiresAt: String,
    val claimedUntil: String? = null,
    val acceptedAt: String? = null,
    val finishedAt: String? = null,
    val failureCode: String? = null,
    val version: Long,
)

@Serializable
data class ReminderDto(
    val id: String,
    val text: String,
    val targetMode: String,
    val targetDeviceIds: List<String>,
    val timezone: String,
    val timezoneAssumed: Boolean,
    val localStart: String,
    val recurrenceRule: String? = null,
    val nextOccurrenceAt: String? = null,
    val createdAt: String,
    val version: Long,
)

@Serializable
data class ReminderWrite(
    val text: String,
    val targetMode: String,
    val timezone: String,
    val localStart: String,
    val targetDeviceIds: List<String>? = null,
    val recurrenceRule: String? = null,
    val expectedVersion: Long? = null,
)

@Serializable
data class StepUpIntent(
    val intent: String,
    val idempotencyKey: String,
    val deviceId: String? = null,
    val commandType: String? = null,
)

@Serializable
data class StepUpOptions(
    val intentId: String,
    val expiresAt: String,
    val methods: List<String>,
    val passkeyOptions: JsonElement? = null,
)

@Serializable
data class StepUpCompletion(val intentId: String, val method: String, val proof: JsonElement)

@Serializable
data class StepUpGrant(val grant: String, val expiresAt: String)

@Serializable
data class PasskeyOptionsRequest(val client: ClientDescriptor, val loginHint: String? = null)

@Serializable
data class WebAuthnOptions(val challengeId: String, val publicKey: JsonElement)

@Serializable
data class WebAuthnCredential(val challengeId: String, val credential: JsonElement)

@Serializable
data class PasskeyDto(
    val id: String,
    val displayName: String,
    val createdAt: String,
    val lastUsedAt: String? = null,
)

@Serializable
data class ProblemDetails(
    val type: String? = null,
    val title: String? = null,
    val status: Int? = null,
    val code: String? = null,
    val correlationId: String? = null,
    val detail: String? = null,
)
