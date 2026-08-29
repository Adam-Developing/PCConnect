package com.adamkhattab.pcconnect.v2

import android.app.Activity
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import com.adamkhattab.pcconnect.v2.data.AppContainer
import com.adamkhattab.pcconnect.v2.data.ReminderWrite
import com.adamkhattab.pcconnect.v2.data.ReminderEntity
import java.time.LocalDateTime
import java.time.ZoneId
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import retrofit2.HttpException
import java.io.IOException

enum class SessionState { CHECKING, SIGNED_OUT, SIGNED_IN }

internal enum class SessionRestoreResult { RESTORED, RETRY_IN_BACKGROUND, NO_SESSION }

data class UiMessage(val text: String, val isError: Boolean)

internal data class SensitiveUiState(
    val signInPassword: String = "",
    val registrationPassword: String = "",
    val resetPassword: String = "",
    val resetConfirmation: String = "",
    val dialogPassword: String = "",
)

class MainViewModel(private val container: AppContainer) : ViewModel() {
    private val _session = MutableStateFlow(SessionState.CHECKING)
    val session: StateFlow<SessionState> = _session.asStateFlow()
    private val _message = MutableStateFlow<UiMessage?>(null)
    val message: StateFlow<UiMessage?> = _message.asStateFlow()
    private val _busy = MutableStateFlow(false)
    val busy: StateFlow<Boolean> = _busy.asStateFlow()
    private val _passwordResetToken = MutableStateFlow<String?>(null)
    val passwordResetToken: StateFlow<String?> = _passwordResetToken.asStateFlow()
    private val _sensitiveUi = MutableStateFlow(SensitiveUiState())
    internal val sensitiveUi: StateFlow<SensitiveUiState> = _sensitiveUi.asStateFlow()

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
        clearRegistrationPassword()
        _message.value = UiMessage("Account created. Check your email to verify the address.", false)
    }

    fun requestPasswordReset(email: String) = action {
        container.repository.requestPasswordReset(email)
        _message.value = UiMessage("If the account exists, a reset link has been sent.", false)
    }

    fun beginPasswordReset(token: String) {
        clearResetPasswords()
        if (token.length in 32..512) _passwordResetToken.value = token
        else _message.value = UiMessage("The password-reset link is invalid.", true)
    }

    fun completePasswordReset(newPassword: String) = action {
        val token = checkNotNull(_passwordResetToken.value) { "No password-reset link is active." }
        container.repository.resetPassword(token, newPassword)
        container.realtime.stop()
        _passwordResetToken.value = null
        clearResetPasswords()
        _session.value = SessionState.SIGNED_OUT
        _message.value = UiMessage("Password changed. Sign in again on this and every other device.", false)
    }

    fun cancelPasswordReset() {
        _passwordResetToken.value = null
        clearResetPasswords()
    }

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

    fun refreshReminders() = action { container.repository.recoverReminders() }

    fun approveEnrollment(userCode: String) = action {
        container.repository.approveEnrollment(userCode)
        _message.value = UiMessage("Device enrollment approved.", false)
    }

    fun command(deviceId: String, type: String, password: String?) = action {
        container.repository.createCommand(deviceId, type, password)
        _message.value = UiMessage("Command accepted by the server.", false)
    }

    fun saveReminder(existing: ReminderEntity?, text: String, localStart: String, onSaved: () -> Unit) = action {
        val request = ReminderWrite(
            text = text.trim(),
            targetMode = existing?.targetMode ?: "all_devices",
            timezone = existing?.timezone ?: ZoneId.systemDefault().id,
            localStart = LocalDateTime.parse(localStart.trim()).toString(),
            targetDeviceIds = existing?.targetDeviceIds
                ?.split(',')
                ?.filter(String::isNotBlank)
                ?.takeIf { existing.targetMode == "selected_devices" },
            recurrenceRule = existing?.recurrenceRule,
            expectedVersion = existing?.version,
        )
        if (existing == null) {
            container.repository.createReminder(request)
        } else {
            container.repository.updateReminder(existing.id, request)
        }
        _message.value = UiMessage(if (existing == null) "Reminder saved." else "Reminder updated.", false)
        onSaved()
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
        clearSensitiveInput()
        _session.value = SessionState.SIGNED_OUT
        container.repository.clearCachedData()
    }

    fun dismissMessage() { _message.value = null }

    fun updateSignInPassword(value: String) {
        _sensitiveUi.update { it.copy(signInPassword = value.take(1024)) }
    }

    fun clearSignInPassword() {
        _sensitiveUi.update { it.copy(signInPassword = "") }
    }

    fun updateRegistrationPassword(value: String) {
        _sensitiveUi.update { it.copy(registrationPassword = value.take(1024)) }
    }

    fun clearRegistrationPassword() {
        _sensitiveUi.update { it.copy(registrationPassword = "") }
    }

    fun updateResetPassword(value: String) {
        _sensitiveUi.update { it.copy(resetPassword = value.take(1024)) }
    }

    fun updateResetConfirmation(value: String) {
        _sensitiveUi.update { it.copy(resetConfirmation = value.take(1024)) }
    }

    fun clearResetPasswords() {
        _sensitiveUi.update { it.copy(resetPassword = "", resetConfirmation = "") }
    }

    fun updateDialogPassword(value: String) {
        _sensitiveUi.update { it.copy(dialogPassword = value.take(1024)) }
    }

    fun clearDialogPassword() {
        _sensitiveUi.update { it.copy(dialogPassword = "") }
    }

    private fun signedIn() {
        clearSensitiveInput()
        _session.value = SessionState.SIGNED_IN
        container.realtime.start()
    }

    private fun clearSensitiveInput() {
        _sensitiveUi.value = SensitiveUiState()
    }

    private suspend fun restoreSession() {
        if (!container.repository.hasSession()) {
            container.repository.clearCachedData()
            _session.value = SessionState.SIGNED_OUT
            return
        }

        // A persisted refresh credential is enough to retain the signed-in UI.
        // Network recovery is best-effort: the realtime client keeps retrying,
        // while an explicit refresh rejection clears the credential and signs out.
        _session.value = SessionState.SIGNED_IN
        val restoreResult = restorePersistedSession(
            refreshAndRecover = {
                checkNotNull(container.tokenManager.refresh()) { "Session refresh is temporarily unavailable." }
                container.repository.recoverAll()
            },
            hasSession = container.repository::hasSession,
        )
        when (restoreResult) {
            SessionRestoreResult.RESTORED -> container.realtime.start()
            SessionRestoreResult.RETRY_IN_BACKGROUND -> {
                container.realtime.start()
                _message.value = UiMessage(
                    "Connection unavailable. Your session is retained and will reconnect automatically.",
                    false,
                )
            }
            SessionRestoreResult.NO_SESSION -> {
                container.realtime.stop()
                container.repository.clearCachedData()
                _session.value = SessionState.SIGNED_OUT
            }
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

internal suspend fun restorePersistedSession(
    refreshAndRecover: suspend () -> Unit,
    hasSession: suspend () -> Boolean,
): SessionRestoreResult = if (runCatching { refreshAndRecover() }.isSuccess) {
    SessionRestoreResult.RESTORED
} else if (hasSession()) {
    SessionRestoreResult.RETRY_IN_BACKGROUND
} else {
    SessionRestoreResult.NO_SESSION
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
