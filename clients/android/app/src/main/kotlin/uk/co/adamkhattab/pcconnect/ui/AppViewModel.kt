package uk.co.adamkhattab.pcconnect.ui

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import uk.co.adamkhattab.pcconnect.data.ApiException
import uk.co.adamkhattab.pcconnect.data.AppLog
import uk.co.adamkhattab.pcconnect.data.Capabilities
import uk.co.adamkhattab.pcconnect.data.Command
import uk.co.adamkhattab.pcconnect.data.CommandTypes
import uk.co.adamkhattab.pcconnect.data.CreateReminderRequest
import uk.co.adamkhattab.pcconnect.data.Device
import uk.co.adamkhattab.pcconnect.data.ErrorCodes
import uk.co.adamkhattab.pcconnect.data.PcConnectApi
import uk.co.adamkhattab.pcconnect.data.Profile
import uk.co.adamkhattab.pcconnect.data.RealtimeClient
import uk.co.adamkhattab.pcconnect.data.RegisterRequest
import uk.co.adamkhattab.pcconnect.data.Reminder
import uk.co.adamkhattab.pcconnect.data.TokenStore
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.time.format.TextStyle
import java.util.Locale

/** A destructive command waiting for the person to confirm it (ADR-0011). */
data class PendingCommand(val deviceId: String, val deviceName: String, val type: String)

data class AppState(
    val isSignedIn: Boolean = false,
    val isLoading: Boolean = false,
    val message: String? = null,
    val updateNotice: String? = null,
    val realtimeConnected: Boolean = false,
    val profile: Profile? = null,
    val devices: List<Device> = emptyList(),
    val commands: List<Command> = emptyList(),
    val reminders: List<Reminder> = emptyList(),
    val pendingCommand: PendingCommand? = null,
    /** Set when a step-up was refused, so the dialog can stay open and say why. */
    val stepUpError: String? = null,
    /**
     * Whether the server understands a reminder that names its PCs. The picker
     * only appears when it does; otherwise every reminder shows everywhere,
     * which is what the server actually does.
     */
    val remindersTargetable: Boolean = false,
)

class AppViewModel(
    private val api: PcConnectApi,
    private val tokens: TokenStore,
) : ViewModel() {

    private val _state = MutableStateFlow(AppState())
    val state: StateFlow<AppState> = _state.asStateFlow()

    val realtime = RealtimeClient(api, viewModelScope)

    fun device(deviceId: String?): Device? = _state.value.devices.firstOrNull { it.id == deviceId }

    init {
        viewModelScope.launch {
            checkVersion()
            if (api.isSignedIn && api.accessToken() != null) onSignedIn()
        }

        viewModelScope.launch {
            realtime.connected.collect { connected -> _state.update { it.copy(realtimeConnected = connected) } }
        }

        viewModelScope.launch {
            realtime.presence.collect { event ->
                _state.update { state ->
                    state.copy(
                        devices = state.devices.map {
                            if (it.id == event.deviceId) it.copy(isOnline = event.isOnline) else it
                        },
                    )
                }
            }
        }

        viewModelScope.launch {
            realtime.commandStatus.collect { event ->
                AppLog.i(
                    TAG,
                    "command ${event.id.take(8)} -> ${event.status}" + (event.resultCode?.let { " ($it)" } ?: ""),
                )

                _state.update { state ->
                    state.copy(
                        commands = state.commands.map {
                            if (it.id == event.id) {
                                it.copy(status = event.status, resultMessage = event.resultMessage)
                            } else {
                                it
                            }
                        },
                        // The phone reports the real outcome. v1 said
                        // {"message":"Success"} for "a row was written" (05 §6).
                        message = when (event.status) {
                            "succeeded" -> "Done."
                            "expired" -> "Not delivered — that PC was offline."
                            "failed" -> event.resultMessage ?: "That PC could not do it."
                            else -> state.message
                        },
                    )
                }
            }
        }

        viewModelScope.launch {
            // Another client changed a reminder. Re-read rather than patching
            // from the event: the list is small and a re-read cannot drift.
            realtime.reminderChanged.collect { event ->
                AppLog.i(TAG, "reminder ${event.reminderId.take(8)} ${event.type} elsewhere")
                refreshReminders()
            }
        }

        viewModelScope.launch {
            realtime.reminderDue.collect { event ->
                _state.update { it.copy(message = "Reminder: ${event.body}") }
                refreshReminders()
            }
        }
    }

    private suspend fun checkVersion() {
        val discovery = runCatching { api.discovery() }.getOrNull() ?: return

        _state.update {
            it.copy(
                remindersTargetable = Capabilities.REMINDER_TARGETS in discovery.capabilities,
                updateNotice = if (PcConnectApi.isBelowMinimum(discovery, uk.co.adamkhattab.pcconnect.BuildConfig.VERSION_NAME)) {
                    "This version of PCConnect is no longer supported. " +
                        "Update to ${discovery.recommendedClient["mobile"] ?: "the latest version"} to keep using it."
                } else {
                    it.updateNotice
                },
            )
        }
    }

    // ── session ──────────────────────────────────────────────────────────────

    fun signIn(login: String, password: String) = launchWithMessage {
        AppLog.i(TAG, "Signing in")
        api.login(login, password)
        onSignedIn()
    }

    fun register(username: String, email: String, password: String) = launchWithMessage {
        api.register(
            RegisterRequest(
                username = username,
                email = email,
                password = password,
                timezone = ZoneId.systemDefault().id,
            ),
        )
        onSignedIn()
    }

    fun signOut() = launchWithMessage {
        AppLog.i(TAG, "Signing out")

        // The log names devices and reminders' timings. It belongs to the
        // session that produced it, not to whoever signs in next on a shared
        // phone.
        AppLog.clear()
        realtime.stop()
        api.signOut()
        _state.value = AppState(message = "Signed out.", remindersTargetable = _state.value.remindersTargetable)
    }

    fun changePassword(current: String, replacement: String) = launchWithMessage {
        api.changePassword(current, replacement)
        _state.update { it.copy(message = "Password changed.") }
    }

    /** Always reports the same thing, because the endpoint always answers the same thing. */
    fun forgotPassword(email: String) = launchWithMessage {
        api.forgotPassword(email)
        _state.update { it.copy(message = "If that address has an account, a reset link is on its way.") }
    }

    private suspend fun onSignedIn() {
        _state.update { it.copy(isSignedIn = true) }

        refreshAll()
        realtime.start()

        // Push is the mechanism; this is the safety net for a phone that has
        // just come off a captive portal or out of a tunnel (05 §5).
        realtime.startFallbackLoop { refreshAll() }
    }

    // ── data ─────────────────────────────────────────────────────────────────

    fun refresh() = launchWithMessage { refreshAll() }

    private suspend fun refreshAll() {
        val devices = api.devices()

        _state.update {
            it.copy(
                profile = runCatching { api.profile() }.getOrNull() ?: it.profile,
                devices = devices,
                commands = runCatching { api.commands() }.getOrDefault(it.commands),
                reminders = runCatching { api.reminders() }.getOrDefault(it.reminders),
            )
        }
    }

    private suspend fun refreshReminders() {
        runCatching { api.reminders() }.onSuccess { reminders ->
            _state.update { it.copy(reminders = reminders.sortedBy { r -> r.dueAt }) }
        }
    }

    fun renameDevice(deviceId: String, displayName: String) = launchWithMessage {
        val trimmed = displayName.trim()
        if (trimmed.isEmpty()) return@launchWithMessage

        api.renameDevice(deviceId, trimmed)
        refreshAll()
        _state.update { it.copy(message = "Renamed to $trimmed.") }
    }

    fun revokeDevice(deviceId: String) = launchWithMessage {
        api.revokeDevice(deviceId)
        refreshAll()
        AppLog.i(TAG, "Removed device $deviceId")
        _state.update { it.copy(message = "That PC has been removed.") }
    }

    fun claimPairing(code: String) = launchWithMessage {
        val claimed = api.claimPairing(code.trim().uppercase())
        refreshAll()
        AppLog.i(TAG, "Added ${claimed.displayName}")
        _state.update { it.copy(message = "Added ${claimed.displayName}.") }
    }

    // ── commands ─────────────────────────────────────────────────────────────

    /**
     * Standard commands go straight through. Destructive ones are held until
     * the person confirms them, because a valid session is not enough to power
     * a machine off (ADR-0011).
     */
    fun requestCommand(deviceId: String, type: String) {
        val device = device(deviceId) ?: return

        if (type in CommandTypes.DESTRUCTIVE) {
            _state.update {
                it.copy(
                    pendingCommand = PendingCommand(deviceId, device.displayName, type),
                    stepUpError = null,
                )
            }
        } else {
            launchWithMessage { sendCommandNow(deviceId, type, stepUpToken = null) }
        }
    }

    fun cancelPendingCommand() = _state.update { it.copy(pendingCommand = null, stepUpError = null) }

    /**
     * Confirms a destructive command.
     *
     * The dialog stays open until the password is accepted. Closing it on a
     * refusal and showing a snackbar meant a mistyped password lost the dialog,
     * the typed password and the pending command all at once, and the only
     * explanation vanished after a few seconds — for the one flow in the app
     * where the user most needs to know what happened.
     */
    fun confirmPendingCommand(password: String) {
        val pending = _state.value.pendingCommand ?: return

        viewModelScope.launch {
            _state.update { it.copy(isLoading = true, stepUpError = null, message = null) }

            try {
                val challenge = api.beginStepUp()
                val token = api.verifyStepUp(challenge.challengeId, password)

                _state.update { it.copy(pendingCommand = null) }
                sendCommandNow(pending.deviceId, pending.type, token.stepUpToken)
            } catch (failure: ApiException) {
                AppLog.w(TAG, "${failure.code} (${failure.statusCode}): ${failure.message}")

                if (failure.code == ErrorCodes.STEP_UP_INVALID) {
                    // Still pending: the person can correct the typo in place.
                    _state.update { it.copy(stepUpError = failure.message) }
                } else {
                    _state.update { it.copy(pendingCommand = null, message = failure.message) }
                }
            } finally {
                _state.update { it.copy(isLoading = false) }
            }
        }
    }

    private suspend fun sendCommandNow(deviceId: String, type: String, stepUpToken: String?) {
        val command = api.issueCommand(deviceId, type, stepUpToken)

        _state.update {
            it.copy(
                commands = listOf(command) + it.commands,
                message = "${CommandTypes.label(type)} sent to ${device(deviceId)?.displayName ?: "that PC"}.",
            )
        }
    }

    // ── reminders ────────────────────────────────────────────────────────────

    /**
     * Saves a reminder.
     *
     * One series per time: `BYHOUR`/`BYMINUTE` multiply out, so "10:30 and
     * 15:45" in a single rule would fire four times a day rather than twice.
     * See [rruleFor].
     */
    fun addReminder(
        body: String,
        date: LocalDate,
        times: List<LocalTime>,
        repeat: RepeatSpec,
        deviceIds: List<String>?,
    ) = launchWithMessage {
        val zone = ZoneId.systemDefault()
        val rrule = rruleFor(repeat, date)
        val until = repeat.until
            ?.let { ZonedDateTime.of(it, LocalTime.MAX, zone).toInstant().toString() }
            ?.takeIf { rrule != null }

        val created = times.sorted().map { time ->
            // The local time the person typed becomes a UTC instant, with their
            // IANA zone travelling alongside it. v1 stored a naive wall clock
            // and fired it at UK time for everybody (S2-07).
            api.createReminder(
                CreateReminderRequest(
                    body = body.trim(),
                    dueAt = ZonedDateTime.of(date, time, zone).toInstant().toString(),
                    timezone = zone.id,
                    rrule = rrule,
                    recurrenceUntil = until,
                    deviceIds = deviceIds?.takeIf { _state.value.remindersTargetable },
                ),
            )
        }

        _state.update { state ->
            state.copy(
                reminders = (state.reminders + created).sortedBy { it.dueAt },
                message = if (created.size == 1) "Reminder added." else "${created.size} reminders added.",
            )
        }
    }

    fun toggleReminder(reminder: Reminder) = launchWithMessage {
        val updated = api.completeReminder(reminder.id, !reminder.isCompleted)
        _state.update { state ->
            state.copy(reminders = state.reminders.map { if (it.id == updated.id) updated else it })
        }
    }

    fun deleteReminder(reminder: Reminder) = launchWithMessage {
        api.deleteReminder(reminder.id)
        _state.update { state -> state.copy(reminders = state.reminders.filterNot { it.id == reminder.id }) }
    }

    // ── settings ─────────────────────────────────────────────────────────────

    var requireBiometric: Boolean
        get() = tokens.requireBiometricForDestructive
        set(value) {
            tokens.requireBiometricForDestructive = value
        }

    var baseUrl: String
        get() = api.baseUrl
        set(value) {
            api.baseUrl = value.trim().trimEnd('/')
        }

    fun dismissMessage() = _state.update { it.copy(message = null) }

    private fun launchWithMessage(block: suspend () -> Unit) {
        viewModelScope.launch {
            _state.update { it.copy(isLoading = true, message = null) }

            try {
                block()
            } catch (failure: ApiException) {
                // The message is for a person; the code is what the app reacts
                // to (04 §3.1). Neither is a stack trace, which is what the Java
                // app printed (S2-13). Both go to the in-app log, so a failure
                // that produced only a snackbar can still be read afterwards.
                AppLog.w(TAG, "${failure.code} (${failure.statusCode}): ${failure.message}")

                _state.update {
                    it.copy(
                        message = failure.message,
                        isSignedIn = if (failure.isUnauthorised && failure.code == ErrorCodes.TOKEN_INVALID) {
                            false
                        } else {
                            it.isSignedIn
                        },
                    )
                }
            } finally {
                _state.update { it.copy(isLoading = false) }
            }
        }
    }

    companion object {
        private const val TAG = "PCConnect.App"

        private val DayAndTime = DateTimeFormatter.ofPattern("EEE d MMM HH:mm")
        private val TimeOnly = DateTimeFormatter.ofPattern("HH:mm")

        fun zoned(value: String, zone: ZoneId = ZoneId.systemDefault()): ZonedDateTime? =
            runCatching { ZonedDateTime.ofInstant(Instant.parse(value), zone) }.getOrNull()

        fun formatInstant(value: String, zone: ZoneId = ZoneId.systemDefault()): String =
            zoned(value, zone)?.format(DayAndTime) ?: value

        /** "15:30" — the column the design lines reminders up in. */
        fun formatTime(value: String, zone: ZoneId = ZoneId.systemDefault()): String =
            zoned(value, zone)?.format(TimeOnly) ?: value

        /** "Today", "Tomorrow", "Fri 5 Sep" — how the design labels a day. */
        fun formatDay(value: String, zone: ZoneId = ZoneId.systemDefault()): String {
            val moment = zoned(value, zone) ?: return value
            return describeDay(moment.toLocalDate())
        }

        fun describeDay(date: LocalDate, today: LocalDate = LocalDate.now()): String = when (date) {
            today -> "Today"
            today.plusDays(1) -> "Tomorrow"
            today.minusDays(1) -> "Yesterday"
            else -> "${date.dayOfWeek.getDisplayName(TextStyle.SHORT, Locale.getDefault())} " +
                "${date.dayOfMonth} ${date.month.getDisplayName(TextStyle.SHORT, Locale.getDefault())}"
        }

        /** "Yesterday 23:10" for a log line, "09:14" when it happened today. */
        fun formatLogTime(value: String, zone: ZoneId = ZoneId.systemDefault()): String {
            val moment = zoned(value, zone) ?: return value
            val day = describeDay(moment.toLocalDate())
            val time = moment.format(TimeOnly)
            return if (day == "Today") time else "$day $time"
        }

        fun isToday(value: String, zone: ZoneId = ZoneId.systemDefault()): Boolean =
            zoned(value, zone)?.toLocalDate() == LocalDate.now(zone)

        fun isPast(value: String): Boolean =
            runCatching { Instant.parse(value).isBefore(Instant.now()) }.getOrDefault(false)

        fun defaultReminderTime(now: LocalDateTime = LocalDateTime.now()): LocalTime =
            now.plusHours(1).toLocalTime().withSecond(0).withNano(0).withMinute(0)
    }
}
