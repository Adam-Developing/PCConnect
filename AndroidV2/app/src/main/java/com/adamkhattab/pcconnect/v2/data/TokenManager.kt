package com.adamkhattab.pcconnect.v2.data

import java.time.Instant
import java.time.OffsetDateTime
import java.time.format.DateTimeFormatter
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import retrofit2.HttpException

class TokenManager(private val store: SessionStore) {
    private val rotation = Mutex()
    @Volatile private var accessToken: String? = null
    @Volatile private var accessTokenExpiresAt: Instant = Instant.EPOCH
    private val _sessionAvailable = MutableStateFlow(false)
    val sessionAvailable = _sessionAvailable.asStateFlow()
    lateinit var refreshCall: suspend (RefreshRequest) -> TokenPair

    fun currentAccessToken(): String? = accessToken?.takeIf { accessTokenExpiresAt.isAfter(Instant.now().plusSeconds(30)) }

    suspend fun accept(pair: TokenPair) {
        store.writeRefreshToken(pair.refreshToken)
        accessToken = pair.accessToken
        accessTokenExpiresAt = parseApiInstant(pair.accessTokenExpiresAt)
        _sessionAvailable.value = true
    }

    suspend fun refresh(force: Boolean = false): String? = rotation.withLock {
        if (!force) currentAccessToken()?.let { return it }
        val refreshToken = store.readRefreshToken() ?: return null
        try {
            refreshCall(RefreshRequest(refreshToken)).also { accept(it) }.accessToken
        } catch (failure: HttpException) {
            if (failure.code() in setOf(400, 401, 409)) clear()
            null
        } catch (_: Exception) {
            // Network and server failures are retryable. Deleting the only
            // rotating credential here would turn an outage into forced logout.
            null
        }
    }

    suspend fun clear() {
        accessToken = null
        accessTokenExpiresAt = Instant.EPOCH
        store.clear()
        _sessionAvailable.value = false
    }

    suspend fun hasSession(): Boolean = (currentAccessToken() != null || store.readRefreshToken() != null).also {
        _sessionAvailable.value = it
    }
}

internal fun parseApiInstant(value: String): Instant =
    OffsetDateTime.parse(value, DateTimeFormatter.ISO_OFFSET_DATE_TIME).toInstant()
