package uk.co.adamkhattab.pcconnect.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.util.UUID
import java.util.concurrent.TimeUnit

/** A failed call, carrying the stable error code the UI switches on (04 §3.1). */
class ApiException(
    val statusCode: Int,
    val code: String,
    override val message: String,
    val retryAfterSeconds: Int? = null,
) : Exception(message) {
    val isStepUpRequired: Boolean get() = code == ErrorCodes.STEP_UP_REQUIRED
    val isUnauthorised: Boolean get() = statusCode == 401
}

/**
 * The v2 client.
 *
 * One place that talks to the network, so the token lifecycle, the error
 * envelope and the request id exist once rather than in six activities — which
 * is how the Java app ended up with `HttpURLConnection` and OkHttp side by side
 * and `printStackTrace` as its error strategy (S2-13).
 */
class PcConnectApi(
    private val tokens: TokenStore,
    defaultBaseUrl: String,
    private val clientVersion: String,
) {
    @PublishedApi
    internal val jsonFormat: Json = Json {
        ignoreUnknownKeys = true      // additive server changes are not breaking (04 §2)
        encodeDefaults = false
        explicitNulls = false
    }

    private val http = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()

    private val refreshLock = Mutex()

    @Volatile
    private var accessToken: String? = null

    @Volatile
    private var accessTokenExpiresAt: Long = 0

    var baseUrl: String = tokens.baseUrl ?: defaultBaseUrl
        set(value) {
            field = value.trimEnd('/')
            tokens.baseUrl = field
        }

    val isSignedIn: Boolean get() = tokens.readRefreshToken() != null

    // ── auth ─────────────────────────────────────────────────────────────────

    suspend fun discovery(): Discovery = get("/v2/meta/discovery", authenticated = false)

    suspend fun register(request: RegisterRequest): TokenPair =
        post<RegisterRequest, TokenPair>("/v2/auth/register", request, authenticated = false).also(::adopt)

    suspend fun login(login: String, password: String): TokenPair =
        post<LoginRequest, TokenPair>(
            "/v2/auth/login",
            LoginRequest(login = login, password = password, clientKind = "mobile", clientVersion = clientVersion),
            authenticated = false,
        ).also(::adopt)

    suspend fun signOut() {
        tokens.readRefreshToken()?.let { refresh ->
            runCatching { post<LogoutRequest, Unit>("/v2/auth/logout", LogoutRequest(refresh), authenticated = false) }
        }

        accessToken = null
        accessTokenExpiresAt = 0
        tokens.clear()
    }

    private fun adopt(pair: TokenPair) {
        accessToken = pair.accessToken
        accessTokenExpiresAt = System.currentTimeMillis() + (pair.expiresInSeconds * 1000L)
        tokens.writeRefreshToken(pair.refreshToken)
    }

    /**
     * Returns a usable access token, refreshing when it is missing or close to
     * expiry. Serialised, so a screen that fires three requests at once causes
     * one refresh rather than three — and three rotations of the same token
     * would look like reuse to the server.
     */
    suspend fun accessToken(): String? {
        accessToken?.let { if (accessTokenExpiresAt > System.currentTimeMillis() + 30_000) return it }

        return refreshLock.withLock {
            accessToken?.let { if (accessTokenExpiresAt > System.currentTimeMillis() + 30_000) return it }

            val refresh = tokens.readRefreshToken() ?: return null

            try {
                post<RefreshRequest, TokenPair>("/v2/auth/refresh", RefreshRequest(refresh), authenticated = false)
                    .also(::adopt)
                    .accessToken
            } catch (expired: ApiException) {
                if (expired.isUnauthorised) {
                    // The chain is dead — expired, or reuse was detected and the
                    // family was revoked. Either way this session is over.
                    tokens.clear()
                    accessToken = null
                }
                null
            }
        }
    }

    // ── step-up (ADR-0011) ───────────────────────────────────────────────────

    suspend fun beginStepUp(): StepUpChallenge = post("/v2/auth/step-up/start", Unit)

    suspend fun verifyStepUp(challengeId: String, password: String): StepUpToken =
        post("/v2/auth/step-up/verify", StepUpVerifyRequest(challengeId, "password", password))

    // ── devices, commands, reminders ─────────────────────────────────────────

    suspend fun devices(): List<Device> = get<Page<Device>>("/v2/devices").items

    suspend fun renameDevice(deviceId: String, displayName: String): Device =
        patch("/v2/devices/$deviceId", UpdateDeviceRequest(displayName = displayName))

    suspend fun revokeDevice(deviceId: String) = delete("/v2/devices/$deviceId")

    suspend fun claimPairing(code: String): PairClaimResponse =
        post("/v2/devices/pair/claim", PairClaimRequest(code))

    suspend fun issueCommand(deviceId: String, type: String, stepUpToken: String? = null): Command =
        post(
            "/v2/commands",
            IssueCommandRequest(
                // Client-generated, so a retry after a timeout returns the same
                // command instead of shutting the machine down twice.
                id = UUID.randomUUID().toString(),
                deviceId = deviceId,
                type = type,
                stepUpToken = stepUpToken,
            ),
        )

    suspend fun commands(limit: Int = 25): List<Command> =
        get<Page<Command>>("/v2/commands?limit=$limit").items

    suspend fun command(commandId: String): Command = get("/v2/commands/$commandId")

    suspend fun reminders(limit: Int = 100): List<Reminder> =
        get<Page<Reminder>>("/v2/reminders?limit=$limit").items

    suspend fun createReminder(request: CreateReminderRequest): Reminder = post("/v2/reminders", request)

    suspend fun completeReminder(reminderId: String, completed: Boolean = true): Reminder =
        post("/v2/reminders/$reminderId/complete", CompleteReminderRequest(completed))

    suspend fun deleteReminder(reminderId: String) = delete("/v2/reminders/$reminderId")

    suspend fun profile(): Profile = get("/v2/account/profile")

    suspend fun changePassword(currentPassword: String, newPassword: String) {
        post<ChangePasswordRequest, Unit>(
            "/v2/auth/password/change",
            ChangePasswordRequest(currentPassword, newPassword),
        )
    }

    /**
     * Always answers the same way whether or not the address is on the account,
     * so the endpoint cannot be used to find out who has one.
     */
    suspend fun forgotPassword(email: String) {
        post<ForgotPasswordRequest, Unit>(
            "/v2/auth/password/forgot",
            ForgotPasswordRequest(email),
            authenticated = false,
        )
    }

    // ── transport ────────────────────────────────────────────────────────────

    private suspend inline fun <reified T> get(path: String, authenticated: Boolean = true): T =
        send(path, "GET", null, authenticated)

    private suspend inline fun <reified TRequest, reified TResponse> post(
        path: String,
        body: TRequest,
        authenticated: Boolean = true,
    ): TResponse = send(path, "POST", encode(body), authenticated)

    private suspend inline fun <reified TRequest, reified TResponse> patch(
        path: String,
        body: TRequest,
    ): TResponse = send(path, "PATCH", encode(body), true)

    private suspend fun delete(path: String) {
        send<Unit>(path, "DELETE", null, true)
    }

    @PublishedApi
    internal inline fun <reified T> encode(value: T): String =
        if (value is Unit) "{}" else jsonFormat.encodeToString(value)

    @PublishedApi
    internal suspend inline fun <reified T> send(
        path: String,
        method: String,
        body: String?,
        authenticated: Boolean,
    ): T = withContext(Dispatchers.IO) {
        var response = execute(path, method, body, authenticated)

        // Refresh once on a 401, then give up: retrying a refresh that already
        // failed just hammers an endpoint that will never succeed.
        //
        // Only for a 401 that is *about the token*. Not every 401 is: a wrong
        // step-up password is one too, and retrying that meant every mistyped
        // password was checked twice — two of the ten attempts the account gets
        // in fifteen minutes, and a refresh-token rotation, spent on a typo. A
        // credential check is the last thing that should be retried
        // automatically.
        if (response.first == 401 && authenticated && isTokenProblem(response.second)) {
            invalidateAccessToken()
            if (accessToken() != null) {
                response = execute(path, method, body, authenticated)
            }
        }

        val (status, payload) = response

        if (status !in 200..299) {
            val envelope = runCatching { jsonFormat.decodeFromString<ErrorEnvelope>(payload) }.getOrNull()
            throw ApiException(
                status,
                envelope?.error?.code ?: "http.$status",
                envelope?.error?.message ?: "The request failed.",
            )
        }

        if (T::class == Unit::class) {
            @Suppress("UNCHECKED_CAST")
            Unit as T
        } else {
            jsonFormat.decodeFromString(payload)
        }
    }

    /**
     * Whether a 401 means "this token is no longer good" rather than "what you
     * sent me was wrong". A body that cannot be parsed is treated as a token
     * problem, which is the pre-existing behaviour and the safe default: the
     * worst case is one extra attempt, not a silently unrefreshed session.
     */
    @PublishedApi
    internal fun isTokenProblem(payload: String): Boolean {
        val code = runCatching { jsonFormat.decodeFromString<ErrorEnvelope>(payload).error.code }.getOrNull()
            ?: return true

        return code == ErrorCodes.TOKEN_INVALID || code == ErrorCodes.TOKEN_EXPIRED
    }

    /** Forces the next call to refresh. Exposed for the inline transport below. */
    @PublishedApi
    internal fun invalidateAccessToken() {
        accessTokenExpiresAt = 0
    }

    @PublishedApi
    internal suspend fun execute(
        path: String,
        method: String,
        body: String?,
        authenticated: Boolean,
    ): Pair<Int, String> {
        val builder = Request.Builder()
            .url(baseUrl + path)
            .header("X-Request-Id", UUID.randomUUID().toString().replace("-", ""))

        when (method) {
            "GET" -> builder.get()
            "DELETE" -> builder.delete()
            else -> builder.method(method, (body ?: "{}").toRequestBody(JSON_MEDIA_TYPE))
        }

        if (authenticated) {
            accessToken()?.let { builder.header("Authorization", "Bearer $it") }
        }

        return withContext(Dispatchers.IO) {
            val startedAt = System.currentTimeMillis()

            try {
                http.newCall(builder.build()).execute().use { response ->
                    val elapsed = System.currentTimeMillis() - startedAt

                    // Method, path, status, duration — and nothing else. Headers
                    // carry the bearer token and bodies carry reminder text, so
                    // neither is ever written to a log the user can read.
                    val line = "$method $path -> ${response.code} in ${elapsed}ms"
                    if (response.isSuccessful) AppLog.d(TAG, line) else AppLog.w(TAG, line)

                    response.code to (response.body?.string().orEmpty())
                }
            } catch (offline: IOException) {
                AppLog.e(TAG, "$method $path failed after ${System.currentTimeMillis() - startedAt}ms", offline)
                throw ApiException(0, "network.unavailable", "Could not reach PCConnect: ${offline.message}")
            }
        }
    }

    companion object {
        @PublishedApi
        internal const val TAG = "PCConnect.Api"

        @PublishedApi
        internal val JSON_MEDIA_TYPE = "application/json; charset=utf-8".toMediaType()

        /**
         * True when this build is below the server's minimum. That check is what
         * eventually lets the legacy surface be switched off (04 §2).
         */
        fun isBelowMinimum(discovery: Discovery, current: String): Boolean {
            val minimum = discovery.minimumSupportedClient["mobile"] ?: return false
            return compareVersions(current, minimum) < 0
        }

        fun compareVersions(left: String, right: String): Int {
            val a = left.split('.').map { it.toIntOrNull() ?: 0 }
            val b = right.split('.').map { it.toIntOrNull() ?: 0 }

            for (index in 0 until maxOf(a.size, b.size)) {
                val comparison = (a.getOrElse(index) { 0 }).compareTo(b.getOrElse(index) { 0 })
                if (comparison != 0) return comparison
            }

            return 0
        }
    }
}
