package uk.co.adamkhattab.pcconnect.ui

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
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
import uk.co.adamkhattab.pcconnect.data.StepUpMethods
import uk.co.adamkhattab.pcconnect.data.Reminder
import uk.co.adamkhattab.pcconnect.data.ReminderOccurrenceResponse
import uk.co.adamkhattab.pcconnect.data.TokenStore
import uk.co.adamkhattab.pcconnect.data.UpdateDeviceRequest
import uk.co.adamkhattab.pcconnect.data.UpdateReminderOccurrenceRequest
import uk.co.adamkhattab.pcconnect.data.UpdateReminderRequest
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.time.format.TextStyle
import java.util.Locale

    /** A command whose target PC requires the person to confirm it. */
data class PendingCommand(val deviceId: String, val deviceName: String, val type: String)

data class AppState(
    val isSignedIn: Boolean = false,
    val isLoading: Boolean = false,
    val message: String? = null,
    val messageId: Long = 0L,
    val canUndo: Boolean = false,
    val updateNotice: String? = null,
    val realtimeConnected: Boolean = false,
    val profile: Profile? = null,
    val devices: List<Device> = emptyList(),
    val commands: List<Command> = emptyList(),
    val reminders: List<Reminder> = emptyList(),
    val pendingCommand: PendingCommand? = null,
    /** Set when a step-up was refused, so the dialog can stay open and say why. */
    val stepUpError: String? = null,
    val biometricEnabled: Boolean = false,
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

    private sealed interface PendingReminderUndo {
        data class Deleted(val snapshot: Reminder) : PendingReminderUndo
        data class Edited(val previous: Reminder, val updated: Reminder) : PendingReminderUndo
    }

    private val _state = MutableStateFlow(AppState())
    val state: StateFlow<AppState> = _state.asStateFlow()

    private var pendingUndo: PendingReminderUndo? = null
    private var nextMessageId: Long = 1L

    val realtime = RealtimeClient(api, viewModelScope)

    fun device(deviceId: String?): Device? = _state.value.devices.firstOrNull { it.id == deviceId }

    init {
        _state.update {
            it.copy(
                biometricEnabled = tokens.requireBiometricForDestructive,
            )
        }

        viewModelScope.launch {
            checkVersion()
            if (api.isSignedIn && api.accessToken() != null) onSignedIn()
        }

        viewModelScope.launch {
            realtime.connected.collect { connected -> _state.update { it.copy(realtimeConnected = connected) } }
        }

        viewModelScope.launch {
            realtime.presence.collect { event ->
                if (_state.value.devices.none { it.id == event.deviceId }) {
                    // A PC signed in while this phone was already open. Presence
                    // is the first event the new agent emits, so reload the account
                    // list instead of silently discarding an unknown device id.
                    runCatching { refreshAll() }
                } else {
                    _state.update { state ->
                        state.copy(
                            devices = state.devices.map {
                                if (it.id == event.deviceId) it.copy(isOnline = event.isOnline) else it
                            },
                        )
                    }
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
            realtime.reminderChanged.collect { event ->
                AppLog.i(TAG, "reminder ${event.reminderId.take(8)} ${event.type} elsewhere")
                when {
                    event.type.equals("deleted", ignoreCase = true) -> {
                        _state.update { state ->
                            if (state.reminders.none { it.id == event.reminderId }) state
                            else state.copy(reminders = state.reminders.removeById(event.reminderId))
                        }
                    }
                    event.reminder != null -> {
                        val incoming = event.reminder
                        _state.update { state ->
                            if (state.reminders.firstOrNull { it.id == incoming.id } == incoming) state
                            else state.copy(reminders = state.reminders.upsert(incoming))
                        }
                    }
                    else -> refreshReminders()
                }
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

    // ── commands ─────────────────────────────────────────────────────────────

    /** Commands that the target PC marks as protected wait for confirmation. */
    fun requestCommand(deviceId: String, type: String) {
        val device = device(deviceId) ?: return

        if (type in device.passwordRequiredCommands) {
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
     * Confirms the pending command directly using the biometric step-up token
     * after standard Android fingerprint verification.
     */
    fun confirmPendingCommandWithBiometric() {
        val pending = _state.value.pendingCommand ?: return

        viewModelScope.launch {
            _state.update { it.copy(isLoading = true, stepUpError = null, message = null) }

            try {
                val challenge = api.beginStepUp()
                val token = api.verifyStepUpWithBiometric(challenge.challengeId)

                _state.update { it.copy(pendingCommand = null) }
                sendCommandNow(pending.deviceId, pending.type, token.stepUpToken)
            } catch (failure: ApiException) {
                AppLog.w(TAG, "${failure.code} (${failure.statusCode}): ${failure.message}")
                _state.update { it.copy(stepUpError = failure.message) }
            } finally {
                _state.update { it.copy(isLoading = false) }
            }
        }
    }

    fun setStepUpError(error: String?) {
        _state.update { it.copy(stepUpError = error) }
    }

    /**
     * Confirms a destructive or protected command with the password.
     *
     * The dialog stays open until the password is accepted.
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
        val schedule = buildSchedule(date, repeat, deviceIds)
        val trimmedBody = body.trim()
        val sortedTimes = times.sorted()

        // The local time the person typed becomes a UTC instant, with their
        // IANA zone travelling alongside it. v1 stored a naive wall clock
        // and fired it at UK time for everybody (S2-07).
        fun requestFor(time: LocalTime) = CreateReminderRequest(
            body = trimmedBody,
            dueAt = schedule.dueAtIso(date, time),
            timezone = schedule.zone.id,
            rrule = schedule.rrule,
            recurrenceUntil = schedule.until,
            deviceIds = schedule.targetDeviceIds,
        )

        val created = if (sortedTimes.size <= 1) {
            sortedTimes.map { time -> api.createReminder(requestFor(time)) }
        } else {
            api.accessToken()
            coroutineScope {
                sortedTimes.map { time -> async { api.createReminder(requestFor(time)) } }.awaitAll()
            }
        }

        emitMessage(
            text = if (created.size == 1) "Reminder added." else "${created.size} reminders added.",
        ) { state ->
            state.copy(reminders = state.reminders.upsertAll(created))
        }
    }

    fun updateReminder(
        id: String,
        body: String,
        date: LocalDate,
        time: LocalTime,
        repeat: RepeatSpec,
        deviceIds: List<String>?,
    ) = launchWithMessage {
        val previous = _state.value.reminders.firstOrNull { it.id == id }
        val schedule = buildSchedule(date, repeat, deviceIds)
        val updated = api.updateReminder(
            id,
            UpdateReminderRequest(
                body = body.trim(),
                dueAt = schedule.dueAtIso(date, time),
                timezone = schedule.zone.id,
                rrule = schedule.rrule ?: "",
                recurrenceUntil = schedule.until,
                deviceIds = schedule.targetDeviceIds,
                clearDeviceIds = if (schedule.targetDeviceIds == null && !previous?.deviceIds.isNullOrEmpty()) true else null,
            ),
        )

        emitMessage(
            text = "Reminder updated.",
            undo = previous?.let { PendingReminderUndo.Edited(it, updated) },
        ) { state ->
            state.copy(reminders = state.reminders.upsert(updated))
        }
    }

    fun toggleReminder(reminder: Reminder) = launchWithMessage {
        val updated = api.completeReminder(reminder.id, !reminder.isCompleted)
        _state.update { state -> state.copy(reminders = state.reminders.upsert(updated)) }
    }

    fun deleteReminder(reminder: Reminder) = launchWithMessage {
        api.deleteReminder(reminder.id)
        emitMessage(
            text = "Reminder deleted.",
            undo = PendingReminderUndo.Deleted(reminder),
        ) { state ->
            state.copy(reminders = state.reminders.removeById(reminder.id))
        }
    }

    fun undoReminderChange() {
        val undo = pendingUndo ?: return
        pendingUndo = null
        launchWithMessage {
            when (undo) {
                is PendingReminderUndo.Deleted -> {
                    val restored = try {
                        api.restoreReminder(undo.snapshot.id)
                    } catch (e: ApiException) {
                        if (e.statusCode == 404 || e.statusCode == 405) {
                            val recreated = api.createReminder(
                                CreateReminderRequest(
                                    body = undo.snapshot.body,
                                    dueAt = undo.snapshot.dueAt,
                                    timezone = undo.snapshot.timezone,
                                    rrule = undo.snapshot.rrule,
                                    recurrenceUntil = undo.snapshot.recurrenceUntil,
                                    deviceIds = undo.snapshot.deviceIds,
                                ),
                            )
                            if (undo.snapshot.isCompleted) {
                                api.completeReminder(recreated.id, true)
                            } else {
                                recreated
                            }
                        } else {
                            throw e
                        }
                    }
                    emitMessage("Reminder restored.") { state ->
                        state.copy(reminders = state.reminders.upsert(restored))
                    }
                }

                is PendingReminderUndo.Edited -> {
                    val prev = undo.previous
                    val reverted = api.updateReminder(
                        prev.id,
                        UpdateReminderRequest(
                            body = prev.body,
                            dueAt = prev.dueAt,
                            timezone = prev.timezone,
                            rrule = prev.rrule ?: "",
                            recurrenceUntil = prev.recurrenceUntil,
                            deviceIds = prev.deviceIds,
                            clearDeviceIds = if (prev.deviceIds == null && !undo.updated.deviceIds.isNullOrEmpty()) true else null,
                        ),
                    )
                    emitMessage("Changes undone.") { state ->
                        state.copy(reminders = state.reminders.upsert(reverted))
                    }
                }
            }
        }
    }

    private data class ReminderSchedule(
        val zone: ZoneId,
        val rrule: String?,
        val until: String?,
        val targetDeviceIds: List<String>?,
    ) {
        fun dueAtIso(date: LocalDate, time: LocalTime): String =
            ZonedDateTime.of(date, time, zone).toInstant().toString()
    }

    private fun buildSchedule(date: LocalDate, repeat: RepeatSpec, deviceIds: List<String>?): ReminderSchedule {
        val zone = ZoneId.systemDefault()
        val rrule = rruleFor(repeat, date)
        val until = repeat.until
            ?.let { ZonedDateTime.of(it, LocalTime.MAX, zone).toInstant().toString() }
            ?.takeIf { rrule != null }
        val targets = deviceIds?.takeIf { _state.value.remindersTargetable }
        return ReminderSchedule(zone, rrule, until, targets)
    }

    private fun List<Reminder>.upsert(incoming: Reminder): List<Reminder> =
        (filterNot { it.id == incoming.id } + incoming).sortedBy { it.dueAt }

    private fun List<Reminder>.upsertAll(incoming: Collection<Reminder>): List<Reminder> {
        if (incoming.isEmpty()) return this
        val ids = incoming.mapTo(HashSet(incoming.size)) { it.id }
        return (filterNot { it.id in ids } + incoming).sortedBy { it.dueAt }
    }

    private fun List<Reminder>.removeById(id: String): List<Reminder> =
        filterNot { it.id == id }

    suspend fun getOccurrences(reminder: Reminder): List<ReminderOccurrenceResponse> {
        val serverList = runCatching {
            api.getReminderOccurrences(reminder.id)
        }.getOrNull()

        if (!serverList.isNullOrEmpty()) {
            return serverList
        }

        return generateLocalOccurrences(reminder)
    }

    private fun generateLocalOccurrences(reminder: Reminder): List<ReminderOccurrenceResponse> {
        val seriesStart = runCatching {
            Instant.parse(reminder.dueAt).atZone(java.time.ZoneId.systemDefault()).toLocalDate()
        }.getOrNull() ?: return emptyList()

        val seriesTime = runCatching {
            Instant.parse(reminder.dueAt).atZone(java.time.ZoneId.systemDefault()).toLocalTime()
        }.getOrNull() ?: LocalTime.of(9, 0)

        val zone = java.time.ZoneId.systemDefault()
        val today = LocalDate.now()
        val rrule = reminder.rrule.orEmpty()
        val targetCount = when {
            rrule.contains("FREQ=HOURLY", ignoreCase = true) -> 10
            rrule.contains("FREQ=DAILY", ignoreCase = true) -> 7
            else -> 3
        }

        val maxDays = if (rrule.contains("FREQ=YEARLY", ignoreCase = true)) 365L * 6 else 365L * 2
        val fromDate = if (seriesStart.isAfter(today)) seriesStart else today
        val untilDate = reminder.recurrenceUntil?.let {
            runCatching { Instant.parse(it).atZone(zone).toLocalDate() }.getOrNull()
        }
        val toDate = listOfNotNull(today.plusDays(maxDays), untilDate).minOrNull() ?: today.plusDays(maxDays)

        val list = mutableListOf<ReminderOccurrenceResponse>()

        if (rrule.contains("FREQ=HOURLY", ignoreCase = true)) {
            var curDt = java.time.LocalDateTime.now()
            val startDt = seriesStart.atTime(seriesTime)
            if (curDt.isBefore(startDt)) curDt = startDt
            while (list.size < targetCount && list.size < 50) {
                val zdt = curDt.atZone(zone)
                list.add(
                    ReminderOccurrenceResponse(
                        id = 0,
                        reminderId = reminder.id,
                        occursAt = zdt.toInstant().toString(),
                        status = "pending",
                        completedAt = null,
                        customBody = null,
                        effectiveBody = reminder.body,
                    )
                )
                curDt = curDt.plusHours(1)
            }
        } else {
            var cur = fromDate
            while (!cur.isAfter(toDate) && list.size < targetCount) {
                if (occursOn(reminder.rrule, seriesStart, cur)) {
                    val zdt = cur.atTime(seriesTime).atZone(zone)
                    val instantStr = zdt.toInstant().toString()
                    list.add(
                        ReminderOccurrenceResponse(
                            id = 0,
                            reminderId = reminder.id,
                            occursAt = instantStr,
                            status = "pending",
                            completedAt = null,
                            customBody = null,
                            effectiveBody = reminder.body,
                        )
                    )
                }
                cur = cur.plusDays(1)
            }
        }
        return list
    }

    suspend fun toggleOccurrence(reminderId: String, occursAt: String, currentStatus: String): ReminderOccurrenceResponse {
        val willComplete = currentStatus != "completed"
        return api.updateReminderOccurrence(
            reminderId,
            UpdateReminderOccurrenceRequest(
                occursAt = occursAt,
                status = if (willComplete) "completed" else "pending",
            )
        )
    }

    suspend fun modifyOccurrenceText(reminderId: String, occursAt: String, newText: String): ReminderOccurrenceResponse {
        return api.updateReminderOccurrence(
            reminderId,
            UpdateReminderOccurrenceRequest(
                occursAt = occursAt,
                customBody = newText,
            )
        )
    }

    suspend fun resetOccurrenceText(reminderId: String, occursAt: String): ReminderOccurrenceResponse {
        return api.updateReminderOccurrence(
            reminderId,
            UpdateReminderOccurrenceRequest(
                occursAt = occursAt,
                clearCustomBody = true,
            )
        )
    }

    suspend fun revertOccurrence(
        reminderId: String,
        occursAt: String,
        status: String? = null,
        customBody: String? = null,
        clearCustomBody: Boolean = false,
    ): ReminderOccurrenceResponse {
        return api.updateReminderOccurrence(
            reminderId,
            UpdateReminderOccurrenceRequest(
                occursAt = occursAt,
                status = status,
                customBody = customBody,
                clearCustomBody = clearCustomBody,
            )
        )
    }

    suspend fun stopOccurrence(reminderId: String, occursAt: String) {
        api.deleteReminderOccurrence(reminderId, occursAt)
    }

    suspend fun restoreOccurrence(reminderId: String, occursAt: String): ReminderOccurrenceResponse {
        return api.updateReminderOccurrence(
            reminderId,
            UpdateReminderOccurrenceRequest(
                occursAt = occursAt,
                status = "pending",
            )
        )
    }

    // ── settings ─────────────────────────────────────────────────────────────

    fun toggleBiometric(enable: Boolean) {
        tokens.requireBiometricForDestructive = enable
        _state.update {
            it.copy(
                biometricEnabled = enable,
                message = if (enable) "Fingerprint confirmation enabled." else "Fingerprint confirmation disabled.",
            )
        }
    }

    fun toggleCommandProtection(commandType: String, deviceId: String? = null) = launchWithMessage {
        val devices = _state.value.devices
        if (devices.isEmpty()) return@launchWithMessage

        val targets = if (deviceId != null) devices.filter { it.id == deviceId } else devices
        if (targets.isEmpty()) return@launchWithMessage

        val currentlyProtected = targets.all { commandType in it.passwordRequiredCommands }
        val shouldProtect = !currentlyProtected

        // Instant optimistic update so UI switches respond without lag
        val updatedList = devices.map { dev ->
            if (targets.any { it.id == dev.id }) {
                val current = dev.passwordRequiredCommands.toSet()
                val next = if (shouldProtect) current + commandType else current - commandType
                dev.copy(passwordRequiredCommands = next.toList())
            } else {
                dev
            }
        }
        _state.update { it.copy(devices = updatedList) }

        // Persist to backend for each targeted device
        for (dev in targets) {
            val current = dev.passwordRequiredCommands.toSet()
            val next = if (shouldProtect) current + commandType else current - commandType
            api.updateDevice(dev.id, UpdateDeviceRequest(passwordRequiredCommands = next.toList()))
        }
    }

    fun toggleCommandProtectionForAll(commandType: String) = toggleCommandProtection(commandType, deviceId = null)

    var requireBiometric: Boolean
        get() = tokens.requireBiometricForDestructive
        set(value) {
            tokens.requireBiometricForDestructive = value
            _state.update { it.copy(biometricEnabled = value) }
        }

    var baseUrl: String
        get() = api.baseUrl
        set(value) {
            api.baseUrl = value.trim().trimEnd('/')
        }

    fun dismissMessage() {
        pendingUndo = null
        _state.update { it.copy(message = null, canUndo = false) }
    }

    private fun emitMessage(
        text: String,
        undo: PendingReminderUndo? = null,
        transform: (AppState) -> AppState = { it },
    ) {
        pendingUndo = undo
        val id = nextMessageId++
        _state.update { current ->
            transform(current).copy(
                message = text,
                messageId = id,
                canUndo = undo != null,
            )
        }
    }

    private fun launchWithMessage(block: suspend () -> Unit) {
        viewModelScope.launch {
            pendingUndo = null
            _state.update { it.copy(isLoading = true, message = null, canUndo = false) }

            try {
                block()
            } catch (failure: ApiException) {
                // The message is for a person; the code is what the app reacts
                // to (04 §3.1). Neither is a stack trace, which is what the Java
                // app printed (S2-13). Both go to the in-app log, so a failure
                // that produced only a snackbar can still be read afterwards.
                AppLog.w(TAG, "${failure.code} (${failure.statusCode}): ${failure.message}")

                val id = nextMessageId++
                _state.update {
                    it.copy(
                        message = failure.message,
                        messageId = id,
                        canUndo = false,
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
