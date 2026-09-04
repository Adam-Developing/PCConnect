package uk.co.adamkhattab.pcconnect.data

import android.content.Context
import androidx.credentials.CreatePublicKeyCredentialRequest
import androidx.credentials.CreatePublicKeyCredentialResponse
import androidx.credentials.CredentialManager
import androidx.credentials.GetCredentialRequest
import androidx.credentials.GetPublicKeyCredentialOption
import androidx.credentials.PublicKeyCredential
import androidx.credentials.exceptions.CreateCredentialCancellationException
import androidx.credentials.exceptions.CreateCredentialException
import androidx.credentials.exceptions.GetCredentialCancellationException
import androidx.credentials.exceptions.GetCredentialException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray
import kotlinx.serialization.json.putJsonObject

/**
 * The passkey half of step-up (ADR-0011).
 *
 * The server has always accepted a passkey in place of a password for a
 * destructive command; this is the client side that was missing, and it is why
 * the confirm dialog could previously only ever ask for a password.
 *
 * Credential Manager speaks the W3C JSON dialect, and the server speaks its own
 * typed contract. Both use base64url for every binary field, so the translation
 * here is a reshaping and never a re-encoding.
 *
 * Two things must be true of a deployment for this to work at all, and neither
 * is something the app can arrange:
 *
 *  - The relying party must serve `/.well-known/assetlinks.json` naming this
 *    app's package and signing-certificate fingerprint. Android refuses the
 *    ceremony otherwise, which is exactly the phishing resistance being bought.
 *  - The server's `WebAuthn:AllowedOrigins` must include this app's origin,
 *    `android:apk-key-hash:<base64url sha-256 of the signing certificate>`,
 *    because the origin check is exact and an Android origin is not a URL.
 */
class PasskeyClient(context: Context) {

    private val manager = CredentialManager.create(context)

    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }

    /**
     * Registers a passkey for this account.
     *
     * Returns null when the person backed out of the system sheet, which is a
     * decision rather than a failure and must not surface as an error.
     */
    suspend fun register(
        activityContext: Context,
        options: PasskeyRegistrationOptions,
    ): PasskeyRegistrationRequest? {
        val response = try {
            manager.createCredential(
                context = activityContext,
                request = CreatePublicKeyCredentialRequest(creationJson(options)),
            )
        } catch (cancelled: CreateCredentialCancellationException) {
            AppLog.i(TAG, "Passkey registration was cancelled")
            return null
        } catch (failure: CreateCredentialException) {
            AppLog.w(TAG, "Passkey registration failed: ${failure.type}", failure)
            throw ApiException(0, "passkey.unavailable", describe(failure.type))
        }

        val created = response as? CreatePublicKeyCredentialResponse
            ?: throw ApiException(0, "passkey.unavailable", "That credential is not a passkey.")

        val payload = json.parseToJsonElement(created.registrationResponseJson).jsonObject
        val inner = payload["response"]!!.jsonObject

        return PasskeyRegistrationRequest(
            challengeId = options.challengeId,
            credentialId = payload.string("id"),
            clientDataJson = inner.string("clientDataJSON"),
            attestationObject = inner.string("attestationObject"),
            transports = inner["transports"]?.let { element ->
                runCatching { element.jsonArray().map { it.jsonPrimitive.content } }.getOrNull()
            },
        )
    }

    /**
     * Runs an assertion for a step-up challenge the server has already opened.
     *
     * The fingerprint prompt is the platform's own, raised by Credential
     * Manager: the app never sees the biometric, only whether the authenticator
     * signed.
     */
    suspend fun assert(
        activityContext: Context,
        options: PasskeyAssertionOptions,
    ): PasskeyAssertionRequest? {
        val response = try {
            manager.getCredential(
                context = activityContext,
                request = GetCredentialRequest(
                    listOf(GetPublicKeyCredentialOption(requestJson(options))),
                ),
            )
        } catch (cancelled: GetCredentialCancellationException) {
            AppLog.i(TAG, "Passkey confirmation was cancelled")
            return null
        } catch (failure: GetCredentialException) {
            AppLog.w(TAG, "Passkey confirmation failed: ${failure.type}", failure)
            throw ApiException(0, "passkey.unavailable", describe(failure.type))
        }

        val credential = response.credential as? PublicKeyCredential
            ?: throw ApiException(0, "passkey.unavailable", "That credential is not a passkey.")

        val payload = json.parseToJsonElement(credential.authenticationResponseJson).jsonObject
        val inner = payload["response"]!!.jsonObject

        return PasskeyAssertionRequest(
            challengeId = options.challengeId,
            credentialId = payload.string("id"),
            clientDataJson = inner.string("clientDataJSON"),
            authenticatorData = inner.string("authenticatorData"),
            signature = inner.string("signature"),
            userHandle = inner["userHandle"]?.jsonPrimitive?.contentOrNullSafe(),
        )
    }

    /** `PublicKeyCredentialCreationOptionsJSON`, as Credential Manager expects it. */
    private fun creationJson(options: PasskeyRegistrationOptions): String = json.encodeToString(
        JsonObject.serializer(),
        buildJsonObject {
            put("challenge", options.challenge)
            putJsonObject("rp") {
                put("id", options.rp.id)
                put("name", options.rp.name)
            }
            putJsonObject("user") {
                put("id", options.user.id)
                put("name", options.user.name)
                put("displayName", options.user.displayName)
            }
            putJsonArray("pubKeyCredParams") {
                options.pubKeyCredParams.forEach { parameter ->
                    add(buildJsonObject {
                        put("type", parameter.type)
                        put("alg", parameter.alg)
                    })
                }
            }
            putJsonArray("excludeCredentials") {
                options.excludeCredentials.forEach { descriptor ->
                    add(buildJsonObject {
                        put("type", descriptor.type)
                        put("id", descriptor.id)
                    })
                }
            }
            putJsonObject("authenticatorSelection") {
                options.authenticatorSelection.authenticatorAttachment?.let { put("authenticatorAttachment", it) }
                put("residentKey", options.authenticatorSelection.residentKey)
                put("requireResidentKey", options.authenticatorSelection.requireResidentKey)
                put("userVerification", options.authenticatorSelection.userVerification)
            }
            put("timeout", options.timeoutMilliseconds)
            put("attestation", options.attestation)
        },
    )

    /** `PublicKeyCredentialRequestOptionsJSON`. */
    private fun requestJson(options: PasskeyAssertionOptions): String = json.encodeToString(
        JsonObject.serializer(),
        buildJsonObject {
            put("challenge", options.challenge)
            put("rpId", options.rpId)
            put("timeout", options.timeoutMilliseconds)
            put("userVerification", options.userVerification)
            putJsonArray("allowCredentials") {
                options.allowCredentials.forEach { descriptor ->
                    add(buildJsonObject {
                        put("type", descriptor.type)
                        put("id", descriptor.id)
                    })
                }
            }
        },
    )

    private companion object {
        const val TAG = "PCConnect.Passkey"

        fun JsonObject.string(name: String): String =
            this[name]?.jsonPrimitive?.content
                ?: throw ApiException(0, "passkey.unavailable", "The authenticator left out $name.")

        fun kotlinx.serialization.json.JsonElement.jsonArray() =
            this as kotlinx.serialization.json.JsonArray

        fun kotlinx.serialization.json.JsonPrimitive.contentOrNullSafe(): String? =
            if (this is kotlinx.serialization.json.JsonNull) null else content

        /**
         * A sentence rather than an exception type. "No credential available" is
         * the one people actually hit — it means there is no passkey on this
         * phone yet — and it needs to say what to do about it.
         */
        fun describe(type: String): String = when {
            type.contains("NO_CREDENTIAL", ignoreCase = true) ->
                "There is no passkey on this phone yet. Set one up in Settings."
            type.contains("INTERRUPTED", ignoreCase = true) ->
                "That was interrupted. Try again."
            else -> "This phone could not use a passkey. Use your password instead."
        }
    }
}

private fun kotlinx.serialization.json.JsonArrayBuilder.add(value: JsonObject) {
    add(value as kotlinx.serialization.json.JsonElement)
}
