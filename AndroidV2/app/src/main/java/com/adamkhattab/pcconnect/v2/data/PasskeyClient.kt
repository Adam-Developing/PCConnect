package com.adamkhattab.pcconnect.v2.data

import android.app.Activity
import android.os.Build
import androidx.credentials.CredentialManager
import androidx.credentials.CreatePublicKeyCredentialRequest
import androidx.credentials.CreatePublicKeyCredentialResponse
import androidx.credentials.GetCredentialRequest
import androidx.credentials.GetPublicKeyCredentialOption
import androidx.credentials.PublicKeyCredential
import androidx.credentials.exceptions.NoCredentialException
import com.adamkhattab.pcconnect.v2.BuildConfig
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.put

class PasskeyClient(
    private val anonymousApi: PCConnectApi,
    private val authenticatedApi: PCConnectApi,
    private val json: Json,
) {
    suspend fun authenticate(activity: Activity, loginHint: String?): TokenPair {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) throw UnsupportedOperationException("Passkeys require Android 9 or newer.")
        val options = anonymousApi.passkeyOptions(
            PasskeyOptionsRequest(ClientDescriptor(version = BuildConfig.VERSION_NAME), loginHint?.ifBlank { null }),
        )
        val request = GetCredentialRequest.Builder()
            .addCredentialOption(GetPublicKeyCredentialOption(options.publicKey.toString()))
            .build()
        val result = try {
            CredentialManager.create(activity).getCredential(activity, request)
        } catch (exception: NoCredentialException) {
            throw IllegalStateException("No passkey is available for this PCConnect account.", exception)
        }
        val credential = result.credential as? PublicKeyCredential
            ?: error("The selected credential was not a passkey.")
        return anonymousApi.completePasskey(
            WebAuthnCredential(options.challengeId, json.parseToJsonElement(credential.authenticationResponseJson)),
        )
    }

    suspend fun register(activity: Activity, password: String): PasskeyDto {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) throw UnsupportedOperationException("Passkeys require Android 9 or newer.")
        require(password.isNotBlank()) { "Password step-up is required to add a passkey." }
        val idempotencyKey = java.util.UUID.randomUUID().toString()
        val stepUp = authenticatedApi.stepUpOptions(StepUpIntent("security_change", idempotencyKey))
        val grant = authenticatedApi.completeStepUp(
            StepUpCompletion(
                stepUp.intentId,
                "password",
                kotlinx.serialization.json.buildJsonObject { put("password", password) },
            ),
        ).grant
        val options = authenticatedApi.passkeyRegistrationOptions()
        val result = CredentialManager.create(activity).createCredential(
            activity,
            CreatePublicKeyCredentialRequest(options.publicKey.toString()),
        ) as? CreatePublicKeyCredentialResponse
            ?: error("The credential provider did not return a passkey registration response.")
        return authenticatedApi.completePasskeyRegistration(
            grant,
            WebAuthnCredential(options.challengeId, json.parseToJsonElement(result.registrationResponseJson)),
        )
    }
}
