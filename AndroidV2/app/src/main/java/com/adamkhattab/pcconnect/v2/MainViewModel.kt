package com.adamkhattab.pcconnect.v2

import android.app.Activity
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import com.adamkhattab.pcconnect.v2.data.AppContainer
import com.adamkhattab.pcconnect.v2.data.ReminderWrite
import java.time.LocalDateTime
import java.time.ZoneId
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import retrofit2.HttpException
import java.io.IOException

enum class SessionState { CHECKING, SIGNED_OUT, SIGNED_IN }

data class UiMessage(val text: String, val isError: Boolean)

class MainViewModel(private val container: AppContainer) : ViewModel() {
    private val _session = MutableStateFlow(SessionState.CHECKING)
    val session: StateFlow<SessionState> = _session.asStateFlow()
    private val _message = MutableStateFlow<UiMessage?>(null)
    val message: StateFlow<UiMessage?> = _message.asStateFlow()
    private val _busy = MutableStateFlow(false)
    val busy: StateFlow<Boolean> = _busy.asStateFlow()
    private val _passwordResetToken = MutableStateFlow<String?>(null)
    val passwordResetToken: StateFlow<String?> = _passwordResetToken.asStateFlow()

    val devices = container.repository.devices.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())
    val commands = container.repository.commands.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())
    val reminders = container.repository.reminders.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())
    val windowsSids = container.repository.windowsSids
    val passkeys = container.repository.passkeys

    init {
        viewModelScope.launch {
            restoreSession()
        }
        viewModelScope.launch {
            container.tokenManager.sessionAvailable.collect { available ->
                if (!available && _session.value == SessionState.SIGNED_IN) {
                    container.realtime.stop()
                    container.repository.clearCachedData()
                    _session.value = SessionState.SIGNED_OUT
                }
            }
        }
    }

    fun login(login: String, password: String) = action {
        container.repository.login(login.trim(), password)
        signedIn()
    }

    fun register(username: String, email: String, displayName: String, password: String, marketingOptIn: Boolean) = action {
        container.repository.register(username, email, displayName, password, ZoneId.systemDefault().id, marketingOptIn)
        _message.value = UiMessage("Account created. Check your email to verify the address.", false)
    }

    fun requestPasswordReset(email: String) = action {
        container.repository.requestPasswordReset(email)
        _message.value = UiMessage("If the account exists, a reset link has been sent.", false)
    }

    fun beginPasswordReset(token: String) {
        if (token.length in 32..512) _passwordResetToken.value = token
        else _message.value = UiMessage("The password-reset link is invalid.", true)
    }

    fun completePasswordReset(newPassword: String) = action {
        val token = checkNotNull(_passwordResetToken.value) { "No password-reset link is active." }
        container.repository.resetPassword(token, newPassword)
        container.realtime.stop()
        _passwordResetToken.value = null
        _session.value = SessionState.SIGNED_OUT
        _message.value = UiMessage("Password changed. Sign in again on this and every other device.", false)
    }

    fun cancelPasswordReset() { _passwordResetToken.value = null }

    fun verifyEmail(token: String) = action {
        require(token.length in 32..512) { "The email-verification link is invalid." }
        container.repository.verifyEmail(token)
        _message.value = UiMessage("Email address verified.", false)
    }

    fun loginWithPasskey(activity: Activity, loginHint: String) = action {
        container.repository.acceptPasskeySession(container.passkeyClient.authenticate(activity, loginHint))
        signedIn()
    }

    fun refresh() = action { container.repository.recoverAll() }

    fun approveEnrollment(userCode: String) = action {
        container.repository.approveEnrollment(userCode)
        _message.value = UiMessage("Device enrollment approved.", false)
    }

    fun command(deviceId: String, type: String, password: String?) = action {
        container.repository.createCommand(deviceId, type, password)
        _message.value = UiMessage("Command accepted by the server.", false)
    }

    fun reminder(text: String, localStart: String) = action {
        container.repository.createReminder(
            ReminderWrite(
                text = text.trim(),
                targetMode = "all_devices",
                timezone = ZoneId.systemDefault().id,
                localStart = LocalDateTime.parse(localStart.trim()).toString(),
            ),
        )
        _message.value = UiMessage("Reminder saved.", false)
    }

    fun authorizeWindowsSid(deviceId: String, windowsSid: String, password: String) = action {
        container.repository.authorizeWindowsSid(deviceId, windowsSid, password)
        _message.value = UiMessage("Windows account authorized for this device.", false)
    }

    fun revokeWindowsSid(deviceId: String, windowsSid: String, password: String) = action {
        container.repository.revokeWindowsSid(deviceId, windowsSid, password)
        _message.value = UiMessage("Windows account authorization revoked.", false)
    }

    fun addPasskey(activity: Activity, password: String) = action {
        container.passkeyClient.register(activity, password)
        container.repository.recoverPasskeys()
        _message.value = UiMessage("Passkey added.", false)
    }

    fun removePasskey(passkeyId: String, password: String) = action {
        container.repository.removePasskey(passkeyId, password)
        _message.value = UiMessage("Passkey removed.", false)
    }

    fun revokeDevice(deviceId: String, password: String) = action {
        container.repository.revokeDevice(deviceId, password)
        _message.value = UiMessage("Device revoked.", false)
    }

    fun logout() = action {
        container.realtime.stop()
        container.repository.logout()
        _session.value = SessionState.SIGNED_OUT
        container.repository.clearCachedData()
    }

    fun dismissMessage() { _message.value = null }

    private fun signedIn() {
        _session.value = SessionState.SIGNED_IN
        container.realtime.start()
    }

    private suspend fun restoreSession() {
        if (!container.repository.hasSession()) {
            container.repository.clearCachedData()
            _session.value = SessionState.SIGNED_OUT
            return
        }
        var delayMillis = 1_000L
        repeat(3) { attempt ->
            val restored = runCatching {
                checkNotNull(container.tokenManager.refresh()) { "Session refresh is temporarily unavailable." }
                container.repository.recoverAll()
            }.isSuccess
            if (restored) {
                signedIn()
                return
            }
            if (!container.repository.hasSession()) return@repeat
            if (attempt < 2) delay(delayMillis)
            delayMillis = (delayMillis * 2).coerceAtMost(30_000L)
        }
        container.repository.clearCachedData()
        _session.value = SessionState.SIGNED_OUT
        if (container.repository.hasSession()) {
            _message.value = UiMessage("We couldn't restore your session. Check the connection, then sign in again.", true)
        }
    }

    private fun action(block: suspend () -> Unit) {
        if (_busy.value) return
        viewModelScope.launch {
            _busy.value = true
            runCatching { block() }.onFailure {
                _message.value = UiMessage(userMessage(it), true)
            }
            _busy.value = false
        }
    }

    override fun onCleared() {
        container.realtime.stop()
        super.onCleared()
    }

    class Factory(private val container: AppContainer) : ViewModelProvider.Factory {
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>): T = MainViewModel(container) as T
    }
}

internal fun userMessage(failure: Throwable): String = when (failure) {
    is HttpException -> when (failure.code()) {
        400 -> "Check the information you entered and try again."
        401 -> "Your email, username, or password was not accepted."
        403 -> "You don't have permission to perform that action."
        404 -> "The requested item could not be found."
        409 -> "That username or email is already in use."
        429 -> "Too many attempts. Wait a moment and try again."
        in 500..599 -> "PCConnect is temporarily unavailable. Try again shortly."
        else -> "The request could not be completed (HTTP ${failure.code()})."
    }
    is IOException -> "Couldn't reach PCConnect. Check your connection and try again."
    else -> failure.message ?: "The request failed."
}
