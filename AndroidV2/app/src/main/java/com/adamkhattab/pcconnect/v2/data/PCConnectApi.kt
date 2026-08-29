package com.adamkhattab.pcconnect.v2.data

import kotlinx.serialization.json.JsonElement
import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.Header
import retrofit2.http.POST
import retrofit2.http.DELETE
import retrofit2.http.Path
import retrofit2.http.Query

interface PCConnectApi {
    @POST("auth/password/login")
    suspend fun passwordLogin(@Body request: PasswordLoginRequest): TokenPair

    @POST("auth/register")
    suspend fun register(@Body request: RegistrationRequest)

    @POST("auth/password/forgot")
    suspend fun forgotPassword(@Body request: EmailRequest)

    @POST("auth/password/reset")
    suspend fun resetPassword(@Body request: PasswordResetRequest)

    @POST("auth/email/verify")
    suspend fun verifyEmail(@Body request: TokenRequest)

    @POST("auth/refresh")
    suspend fun refresh(@Body request: RefreshRequest): TokenPair

    @POST("auth/logout")
    suspend fun logout()

    @GET("devices")
    suspend fun devices(@Query("cursor") cursor: String? = null, @Query("limit") limit: Int = 100): Page<DeviceDto>

    @POST("device-enrollments/{userCode}/approve")
    suspend fun approveEnrollment(@Path("userCode") userCode: String)

    @GET("devices/{deviceId}/windows-sids")
    suspend fun windowsSids(@Path("deviceId") deviceId: String): List<WindowsSidStatus>

    @DELETE("devices/{deviceId}")
    suspend fun revokeDevice(
        @Path("deviceId") deviceId: String,
        @Header("X-Step-Up-Grant") stepUpGrant: String,
    )

    @POST("devices/{deviceId}/windows-sids/{windowsSid}/authorize")
    suspend fun authorizeWindowsSid(
        @Path("deviceId") deviceId: String,
        @Path("windowsSid") windowsSid: String,
        @Header("X-Step-Up-Grant") stepUpGrant: String,
    )

    @DELETE("devices/{deviceId}/windows-sids/{windowsSid}")
    suspend fun revokeWindowsSid(
        @Path("deviceId") deviceId: String,
        @Path("windowsSid") windowsSid: String,
        @Header("X-Step-Up-Grant") stepUpGrant: String,
    )

    @GET("commands")
    suspend fun commands(@Query("cursor") cursor: String? = null, @Query("limit") limit: Int = 100): Page<CommandDto>

    @POST("devices/{deviceId}/commands")
    suspend fun createCommand(
        @Path("deviceId") deviceId: String,
        @Header("Idempotency-Key") idempotencyKey: String,
        @Header("X-Step-Up-Grant") stepUpGrant: String?,
        @Body request: CommandCreate,
    ): CommandDto

    @GET("reminders")
    suspend fun reminders(@Query("cursor") cursor: String? = null, @Query("limit") limit: Int = 100): Page<ReminderDto>

    @POST("reminders")
    suspend fun createReminder(
        @Header("Idempotency-Key") idempotencyKey: String,
        @Body request: ReminderWrite,
    ): ReminderDto

    @POST("auth/step-up/options")
    suspend fun stepUpOptions(@Body request: StepUpIntent): StepUpOptions

    @POST("auth/step-up/complete")
    suspend fun completeStepUp(@Body request: StepUpCompletion): StepUpGrant

    @POST("auth/passkeys/authentication/options")
    suspend fun passkeyOptions(@Body request: PasskeyOptionsRequest): WebAuthnOptions

    @POST("auth/passkeys/authentication/complete")
    suspend fun completePasskey(@Body request: WebAuthnCredential): TokenPair

    @POST("auth/passkeys/registration/options")
    suspend fun passkeyRegistrationOptions(): WebAuthnOptions

    @POST("auth/passkeys/registration/complete")
    suspend fun completePasskeyRegistration(
        @Header("X-Step-Up-Grant") stepUpGrant: String,
        @Body request: WebAuthnCredential,
    ): PasskeyDto

    @GET("passkeys")
    suspend fun passkeys(): List<PasskeyDto>

    @DELETE("passkeys/{passkeyId}")
    suspend fun removePasskey(
        @Path("passkeyId") passkeyId: String,
        @Header("X-Step-Up-Grant") stepUpGrant: String,
    )
}
