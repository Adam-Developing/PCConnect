package com.adamkhattab.pcconnect.v2.data

import com.adamkhattab.pcconnect.v2.BuildConfig
import java.util.UUID
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.map
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

class ControllerRepository(
    private val api: PCConnectApi,
    private val anonymousApi: PCConnectApi,
    private val dao: ReadModelDao,
    private val tokens: TokenManager,
    private val localPiiCipher: LocalPiiCipher,
) {
    val devices: Flow<List<DeviceEntity>> = dao.observeDevices()
    val commands: Flow<List<CommandEntity>> = dao.observeCommands()
    val reminders: Flow<List<ReminderEntity>> = dao.observeReminders().map { rows ->
        rows.mapNotNull { row ->
            runCatching { row.copy(text = localPiiCipher.decryptReminder(row.id, row.text)) }.getOrNull()
        }
    }
    private val _windowsSids = MutableStateFlow<Map<String, List<WindowsSidStatus>>>(emptyMap())
    val windowsSids = _windowsSids.asStateFlow()
    private val _passkeys = MutableStateFlow<List<PasskeyDto>>(emptyList())
    val passkeys = _passkeys.asStateFlow()

    suspend fun hasSession() = tokens.hasSession()

    suspend fun login(login: String, password: String) {
        tokens.accept(
            anonymousApi.passwordLogin(
                PasswordLoginRequest(login, password, ClientDescriptor(version = BuildConfig.VERSION_NAME)),
            ),
        )
        recoverAll()
    }

    suspend fun register(username: String, email: String, displayName: String, password: String, timezone: String, marketingOptIn: Boolean) {
        anonymousApi.register(
            RegistrationRequest(
                username.trim(), email.trim(), displayName.trim(), password, timezone, marketingOptIn,
                ClientDescriptor(version = BuildConfig.VERSION_NAME),
            ),
        )
    }

    suspend fun requestPasswordReset(email: String) {
        anonymousApi.forgotPassword(EmailRequest(email.trim()))
    }

    suspend fun resetPassword(token: String, newPassword: String) {
        anonymousApi.resetPassword(PasswordResetRequest(token, newPassword))
        tokens.clear()
    }

    suspend fun verifyEmail(token: String) {
        anonymousApi.verifyEmail(TokenRequest(token))
    }

    suspend fun acceptPasskeySession(pair: TokenPair) {
        tokens.accept(pair)
        recoverAll()
    }

    suspend fun logout() {
        runCatching { api.logout() }
        tokens.clear()
        clearCachedData()
    }

    suspend fun recoverAll() {
        try {
            recoverDevices()
            recoverCommands()
            recoverReminders()
            recoverPasskeys()
        } catch (failure: Exception) {
            if (!tokens.hasSession()) clearCachedData()
            throw failure
        }
    }

    suspend fun recoverDevices() {
        val all = collectAll(api::devices)
        dao.replaceDevices(all.map { it.toEntity() }, null)
        _windowsSids.value = all.filter { it.platform == "windows" }.associate { device ->
            device.id to runCatching { api.windowsSids(device.id) }.getOrDefault(emptyList())
        }
    }

    suspend fun approveEnrollment(userCode: String) {
        val normalized = userCode.filter(Char::isLetterOrDigit).uppercase()
        require(normalized.length == 8) { "Enter the eight-character device code." }
        api.approveEnrollment(normalized)
        recoverDevices()
    }

    suspend fun recoverCommands() {
        val all = collectAll(api::commands)
        dao.replaceCommands(all.map { it.toEntity() }, null)
    }

    suspend fun recoverReminders() {
        val all = collectAll(api::reminders)
        dao.replaceReminders(all.map { it.toEntity(localPiiCipher) }, null)
    }

    suspend fun recoverPasskeys() {
        _passkeys.value = api.passkeys()
    }

    /** Commands are deliberately never queued locally: the idempotency key protects an online retry only. */
    suspend fun createCommand(deviceId: String, type: String, passwordForStepUp: String? = null): CommandDto {
        val idempotencyKey = UUID.randomUUID().toString()
        val stepUpGrant = if (!CommandPolicy.requiresStepUp(type)) null else {
            require(!passwordForStepUp.isNullOrBlank()) { "This command requires step-up authentication." }
            val options = api.stepUpOptions(StepUpIntent("command", idempotencyKey, deviceId, type))
            api.completeStepUp(
                StepUpCompletion(options.intentId, "password", buildJsonObject { put("password", passwordForStepUp) }),
            ).grant
        }
        val command = api.createCommand(
            deviceId,
            idempotencyKey,
            stepUpGrant,
            CommandCreate(type = type, explicitlyConfirmed = type == "shutdown" || type == "restart"),
        )
        dao.upsertCommands(listOf(command.toEntity()))
        return command
    }

    suspend fun createReminder(request: ReminderWrite): ReminderDto {
        val reminder = api.createReminder(UUID.randomUUID().toString(), request)
        dao.upsertReminders(listOf(reminder.toEntity(localPiiCipher)))
        return reminder
    }

    suspend fun authorizeWindowsSid(deviceId: String, windowsSid: String, password: String) {
        val idempotencyKey = UUID.randomUUID().toString()
        val options = api.stepUpOptions(StepUpIntent("security_change", idempotencyKey, deviceId))
        val grant = api.completeStepUp(
            StepUpCompletion(options.intentId, "password", buildJsonObject { put("password", password) }),
        ).grant
        api.authorizeWindowsSid(deviceId, windowsSid, grant)
        recoverDevices()
    }

    suspend fun revokeWindowsSid(deviceId: String, windowsSid: String, password: String) {
        val idempotencyKey = UUID.randomUUID().toString()
        val options = api.stepUpOptions(StepUpIntent("security_change", idempotencyKey, deviceId))
        val grant = api.completeStepUp(
            StepUpCompletion(options.intentId, "password", buildJsonObject { put("password", password) }),
        ).grant
        api.revokeWindowsSid(deviceId, windowsSid, grant)
        recoverDevices()
    }

    suspend fun removePasskey(passkeyId: String, password: String) {
        require(password.isNotBlank()) { "Password step-up is required to remove a passkey." }
        val idempotencyKey = UUID.randomUUID().toString()
        val options = api.stepUpOptions(StepUpIntent("security_change", idempotencyKey))
        val grant = api.completeStepUp(
            StepUpCompletion(options.intentId, "password", buildJsonObject { put("password", password) }),
        ).grant
        api.removePasskey(passkeyId, grant)
        recoverPasskeys()
    }

    suspend fun revokeDevice(deviceId: String, password: String) {
        require(password.isNotBlank()) { "Password step-up is required to revoke a device." }
        val idempotencyKey = UUID.randomUUID().toString()
        val options = api.stepUpOptions(StepUpIntent("device_revoke", idempotencyKey, deviceId))
        val grant = api.completeStepUp(
            StepUpCompletion(options.intentId, "password", buildJsonObject { put("password", password) }),
        ).grant
        api.revokeDevice(deviceId, grant)
        recoverDevices()
    }

    suspend fun clearCachedData() {
        dao.clearAll()
        _windowsSids.value = emptyMap()
        _passkeys.value = emptyList()
    }
}

object CommandPolicy {
    fun requiresStepUp(type: String): Boolean = type in setOf("sleep", "hibernate", "sign_out", "restart", "shutdown")
}

suspend fun <T> collectAll(fetch: suspend (String?, Int) -> Page<T>): List<T> {
    val all = mutableListOf<T>()
    var cursor: String? = null
    val seen = mutableSetOf<String>()
    do {
        val page = fetch(cursor, 100)
        all += page.items
        cursor = page.nextCursor
        check(cursor == null || seen.add(cursor)) { "The server returned a repeated recovery cursor." }
    } while (cursor != null)
    return all
}

private fun DeviceDto.toEntity() = DeviceEntity(id, displayName, platform, status, lastSeenAt, capabilities.joinToString(","), version)
private fun CommandDto.toEntity() = CommandEntity(id, deviceId, type, status, issuedAt, failureCode, version)
private fun ReminderDto.toEntity(cipher: LocalPiiCipher) = ReminderEntity(id, cipher.encryptReminder(id, text), targetMode, timezone, localStart, nextOccurrenceAt, version)
