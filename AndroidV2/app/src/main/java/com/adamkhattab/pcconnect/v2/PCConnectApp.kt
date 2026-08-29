package com.adamkhattab.pcconnect.v2

import android.app.DatePickerDialog
import android.app.TimePickerDialog
import android.os.Build
import android.text.format.DateFormat
import androidx.activity.compose.LocalActivity
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.adamkhattab.pcconnect.v2.data.PlatformCapabilities
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

@Composable
fun PCConnectApp(viewModel: MainViewModel) {
    val session by viewModel.session.collectAsStateWithLifecycle()
    val resetToken by viewModel.passwordResetToken.collectAsStateWithLifecycle()
    val message by viewModel.message.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    LaunchedEffect(message) {
        message?.let { snackbar.showSnackbar(it.text); viewModel.dismissMessage() }
    }
    MaterialTheme {
        Scaffold(snackbarHost = { SnackbarHost(snackbar) }) { padding ->
            Box(Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.Center) {
                when {
                    session == SessionState.CHECKING && resetToken == null -> CircularProgressIndicator(
                        Modifier.semantics { contentDescription = "Restoring your PCConnect session" },
                    )
                    resetToken != null || session == SessionState.SIGNED_OUT -> AuthNavigation(viewModel)
                    else -> ControllerScreen(viewModel)
                }
            }
        }
    }
}

@Composable
private fun ControllerScreen(viewModel: MainViewModel) {
    val navController = rememberNavController()
    val currentRoute = navController.currentBackStackEntryAsState().value?.destination?.route
    val destinations = listOf(
        ControllerDestination(ControllerRoute.DEVICES, "Devices", R.drawable.ic_devices, "Enrolled devices"),
        ControllerDestination(ControllerRoute.COMMANDS, "Commands", R.drawable.ic_history, "Command history"),
        ControllerDestination(ControllerRoute.REMINDERS, "Reminders", R.drawable.ic_reminders, "Reminders"),
        ControllerDestination(ControllerRoute.SETTINGS, "Settings", R.drawable.ic_settings, "Settings"),
    )
    Scaffold(
        bottomBar = {
            NavigationBar {
                destinations.forEach { destination ->
                    NavigationBarItem(
                        selected = currentRoute == destination.route,
                        onClick = {
                            navController.navigate(destination.route) {
                                popUpTo(ControllerRoute.DEVICES) { saveState = true }
                                launchSingleTop = true
                                restoreState = true
                            }
                        },
                        icon = { Icon(painterResource(destination.iconResource), contentDescription = destination.description) },
                        label = { Text(destination.label) },
                    )
                }
            }
        },
    ) { padding ->
        NavHost(
            navController = navController,
            startDestination = ControllerRoute.DEVICES,
            modifier = Modifier.fillMaxSize().padding(padding),
        ) {
            composable(ControllerRoute.DEVICES) { DevicesScreen(viewModel) }
            composable(ControllerRoute.COMMANDS) { CommandsScreen(viewModel) }
            composable(ControllerRoute.REMINDERS) { RemindersScreen(viewModel) }
            composable(ControllerRoute.SETTINGS) { SettingsScreen(viewModel) }
        }
    }
}

private object ControllerRoute {
    const val DEVICES = "controller/devices"
    const val COMMANDS = "controller/commands"
    const val REMINDERS = "controller/reminders"
    const val SETTINGS = "controller/settings"
}

private data class ControllerDestination(
    val route: String,
    val label: String,
    val iconResource: Int,
    val description: String,
)

@Composable
private fun DevicesScreen(viewModel: MainViewModel) {
    val devices by viewModel.devices.collectAsStateWithLifecycle()
    val windowsSids by viewModel.windowsSids.collectAsStateWithLifecycle()
    val sensitive by viewModel.sensitiveUi.collectAsStateWithLifecycle()
    var pendingKind by rememberSaveable { mutableStateOf<String?>(null) }
    var pendingDeviceId by rememberSaveable { mutableStateOf<String?>(null) }
    var pendingSecondaryId by rememberSaveable { mutableStateOf<String?>(null) }
    var pendingLabel by rememberSaveable { mutableStateOf<String?>(null) }
    var pendingCommandType by rememberSaveable { mutableStateOf<String?>(null) }
    var enrollmentCode by rememberSaveable { mutableStateOf("") }
    val focusManager = LocalFocusManager.current
    val enrollmentCodeError = enrollmentCode.isNotEmpty() && enrollmentCode.length != 8
    fun openPending(kind: String, deviceId: String, secondaryId: String? = null, label: String, commandType: String? = null) {
        viewModel.clearDialogPassword()
        pendingKind = kind
        pendingDeviceId = deviceId
        pendingSecondaryId = secondaryId
        pendingLabel = label
        pendingCommandType = commandType
    }
    fun clearPending() {
        viewModel.clearDialogPassword()
        pendingKind = null
        pendingDeviceId = null
        pendingSecondaryId = null
        pendingLabel = null
        pendingCommandType = null
    }
    Column(Modifier.fillMaxSize().padding(16.dp)) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Text("Devices", style = MaterialTheme.typography.headlineMedium)
            TextButton(viewModel::refresh) { Text("Recover") }
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(
                enrollmentCode,
                { value -> enrollmentCode = value.filter(Char::isLetterOrDigit).take(8).uppercase() },
                label = { Text("Device code") },
                supportingText = { Text(if (enrollmentCodeError) "Enter all 8 letters or numbers." else "8 letters or numbers") },
                isError = enrollmentCodeError,
                singleLine = true,
                keyboardOptions = KeyboardOptions(
                    capitalization = KeyboardCapitalization.Characters,
                    keyboardType = KeyboardType.Ascii,
                    imeAction = ImeAction.Done,
                ),
                keyboardActions = KeyboardActions(onDone = { focusManager.clearFocus() }),
                modifier = Modifier.fillMaxWidth(0.62f),
            )
            Button(
                {
                    viewModel.approveEnrollment(enrollmentCode)
                    enrollmentCode = ""
                },
                enabled = enrollmentCode.filter(Char::isLetterOrDigit).length == 8,
            ) { Text("Approve") }
        }
        LazyColumn {
            if (devices.isEmpty()) {
                item {
                    Text(
                        "No devices enrolled yet. Enter the 8-character code shown by PCConnect on your computer.",
                        modifier = Modifier.padding(vertical = 20.dp),
                    )
                }
            }
            items(devices, key = { it.id }) { device ->
                Column(Modifier.fillMaxWidth().padding(vertical = 12.dp)) {
                    Text(device.displayName, style = MaterialTheme.typography.titleMedium)
                    Text("${device.platform} · ${device.status}")
                    LazyRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        listOf("lock", "sleep", "hibernate", "sign_out", "restart", "shutdown")
                            .filter { device.capabilities.split(',').contains(it) }
                            .forEach { type ->
                                item(key = type) {
                                    OutlinedButton({
                                        if (type == "lock") {
                                            viewModel.command(device.id, type, null)
                                        } else {
                                            openPending("command", device.id, label = device.displayName, commandType = type)
                                        }
                                    }) { Text(commandLabel(type)) }
                                }
                            }
                    }
                    windowsSids[device.id].orEmpty().filter { it.status == "pending" }.forEach { sid ->
                        OutlinedButton({
                            openPending(
                                "authorize_sid",
                                device.id,
                                sid.windowsSid,
                                sid.displayLabel ?: "Windows account",
                            )
                        }) {
                            Text("Approve ${sid.displayLabel ?: "Windows account"}")
                        }
                    }
                    windowsSids[device.id].orEmpty().filter { it.status == "authorized" }.forEach { sid ->
                        TextButton({
                            openPending(
                                "revoke_sid",
                                device.id,
                                sid.windowsSid,
                                sid.displayLabel ?: "Windows account",
                            )
                        }) {
                            Text("Revoke ${sid.displayLabel ?: "Windows account"}")
                        }
                    }
                    if (device.status != "revoked") {
                        TextButton({ openPending("revoke_device", device.id, label = device.displayName) }) {
                            Text("Revoke device")
                        }
                    }
                }
                HorizontalDivider()
            }
        }
    }
    when (pendingKind) {
        "command" -> StepUpDialog(
            type = checkNotNull(pendingCommandType),
            device = checkNotNull(pendingLabel),
            password = sensitive.dialogPassword,
            onPasswordChange = viewModel::updateDialogPassword,
            dismiss = ::clearPending,
        ) { password ->
            viewModel.command(checkNotNull(pendingDeviceId), checkNotNull(pendingCommandType), password)
            clearPending()
        }

        "authorize_sid" -> StepUpDialog(
            type = "Windows account",
            device = checkNotNull(pendingLabel),
            password = sensitive.dialogPassword,
            onPasswordChange = viewModel::updateDialogPassword,
            dismiss = ::clearPending,
        ) { password ->
            viewModel.authorizeWindowsSid(checkNotNull(pendingDeviceId), checkNotNull(pendingSecondaryId), password)
            clearPending()
        }

        "revoke_sid" -> SecurityChangeDialog(
            title = "Revoke Windows account",
            explanation = "Re-authenticate to stop ${checkNotNull(pendingLabel)} receiving reminders or interactive commands on this PC.",
            password = sensitive.dialogPassword,
            onPasswordChange = viewModel::updateDialogPassword,
            dismiss = ::clearPending,
        ) { password ->
            viewModel.revokeWindowsSid(checkNotNull(pendingDeviceId), checkNotNull(pendingSecondaryId), password)
            clearPending()
        }

        "revoke_device" -> SecurityChangeDialog(
            title = "Revoke device",
            explanation = "Re-authenticate to revoke ${checkNotNull(pendingLabel)} and all of its device credentials.",
            password = sensitive.dialogPassword,
            onPasswordChange = viewModel::updateDialogPassword,
            dismiss = ::clearPending,
        ) { password ->
            viewModel.revokeDevice(checkNotNull(pendingDeviceId), password)
            clearPending()
        }

        null -> Unit
    }
}

@Composable
private fun StepUpDialog(
    type: String,
    device: String,
    password: String,
    onPasswordChange: (String) -> Unit,
    dismiss: () -> Unit,
    confirm: (String) -> Unit,
) {
    val focusManager = LocalFocusManager.current
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text("Confirm ${commandLabel(type)}") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("Re-authenticate before sending this command to $device.")
                OutlinedTextField(
                    password,
                    { onPasswordChange(it.take(1024)) },
                    label = { Text("Password") },
                    visualTransformation = PasswordVisualTransformation(),
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Done),
                    keyboardActions = KeyboardActions(onDone = { focusManager.clearFocus() }),
                )
            }
        },
        confirmButton = { Button({ confirm(password) }, enabled = password.isNotBlank()) { Text("Authenticate and send") } },
        dismissButton = { TextButton(dismiss) { Text("Cancel") } },
    )
}

@Composable
private fun CommandsScreen(viewModel: MainViewModel) {
    val commands by viewModel.commands.collectAsStateWithLifecycle()
    LazyColumn(Modifier.fillMaxSize().padding(16.dp)) {
        item { Text("Command history", style = MaterialTheme.typography.headlineMedium); Spacer(Modifier.height(8.dp)) }
        if (commands.isEmpty()) {
            item { Text("No commands have been sent yet.", modifier = Modifier.padding(vertical = 12.dp)) }
        }
        items(commands, key = { it.id }) { command ->
            Column(Modifier.fillMaxWidth().padding(vertical = 10.dp)) {
                Text(commandLabel(command.type), style = MaterialTheme.typography.titleMedium)
                Text("${command.status} · ${command.issuedAt}")
                command.failureCode?.let { Text("Failure: $it", color = MaterialTheme.colorScheme.error) }
            }
            HorizontalDivider()
        }
    }
}

private fun commandLabel(type: String): String = type
    .split('_')
    .joinToString(" ") { word -> word.replaceFirstChar(Char::uppercase) }

@Composable
private fun RemindersScreen(viewModel: MainViewModel) {
    val reminders by viewModel.reminders.collectAsStateWithLifecycle()
    var text by rememberSaveable { mutableStateOf("") }
    var selectedDateValue by rememberSaveable { mutableStateOf<String?>(null) }
    var selectedTimeValue by rememberSaveable { mutableStateOf<String?>(null) }
    val context = LocalContext.current
    val focusManager = LocalFocusManager.current
    val suggestedStartValue = rememberSaveable {
        LocalDateTime.now().plusHours(1).withSecond(0).withNano(0).toString()
    }
    val suggestedStart = LocalDateTime.parse(suggestedStartValue)
    val selectedDate = selectedDateValue?.let(LocalDate::parse)
    val selectedTime = selectedTimeValue?.let(LocalTime::parse)
    val selectedStart = if (selectedDate != null && selectedTime != null) {
        LocalDateTime.of(selectedDate, selectedTime)
    } else null
    val startValid = selectedStart?.isAfter(LocalDateTime.now()) == true
    val startError = selectedStart != null && !startValid
    val dateFormatter = remember { DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM) }
    val timeFormatter = remember { DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT) }
    LazyColumn(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        item {
            Text("Reminders", style = MaterialTheme.typography.headlineMedium)
            OutlinedTextField(
                text,
                { text = it.take(500) },
                label = { Text("Reminder") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(
                    capitalization = KeyboardCapitalization.Sentences,
                    keyboardType = KeyboardType.Text,
                    imeAction = ImeAction.Done,
                ),
                keyboardActions = KeyboardActions(onDone = { focusManager.clearFocus() }),
                modifier = Modifier.fillMaxWidth(),
            )
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(
                    onClick = {
                        val initial = selectedDate ?: suggestedStart.toLocalDate()
                        DatePickerDialog(
                            context,
                            { _, year, month, day -> selectedDateValue = LocalDate.of(year, month + 1, day).toString() },
                            initial.year,
                            initial.monthValue - 1,
                            initial.dayOfMonth,
                        ).show()
                    },
                    modifier = Modifier.weight(1f),
                ) { Text(selectedDate?.format(dateFormatter) ?: "Choose date") }
                OutlinedButton(
                    onClick = {
                        val initial = selectedTime ?: suggestedStart.toLocalTime()
                        TimePickerDialog(
                            context,
                            { _, hour, minute -> selectedTimeValue = LocalTime.of(hour, minute).toString() },
                            initial.hour,
                            initial.minute,
                            DateFormat.is24HourFormat(context),
                        ).show()
                    },
                    modifier = Modifier.weight(1f),
                ) { Text(selectedTime?.format(timeFormatter) ?: "Choose time") }
            }
            if (startError) {
                Text("Choose a future date and time.", color = MaterialTheme.colorScheme.error)
            }
            Button(
                {
                    viewModel.reminder(text, checkNotNull(selectedStart).toString())
                    text = ""
                    selectedDateValue = null
                    selectedTimeValue = null
                },
                enabled = text.isNotBlank() && startValid,
            ) { Text("Save reminder") }
            if (reminders.isEmpty()) {
                Text("No reminders scheduled yet.", modifier = Modifier.padding(vertical = 12.dp))
            }
        }
        items(reminders, key = { it.id }) { reminder ->
            Column(Modifier.fillMaxWidth().padding(vertical = 8.dp)) {
                Text(reminder.text, style = MaterialTheme.typography.titleMedium)
                Text(reminder.nextOccurrenceAt ?: "Next occurrence pending")
            }
            HorizontalDivider()
        }
    }
}

@Composable
private fun SettingsScreen(viewModel: MainViewModel) {
    val passkeys by viewModel.passkeys.collectAsStateWithLifecycle()
    val sensitive by viewModel.sensitiveUi.collectAsStateWithLifecycle()
    val activity = LocalActivity.current
    var addingPasskey by rememberSaveable { mutableStateOf(false) }
    var removingPasskeyId by rememberSaveable { mutableStateOf<String?>(null) }
    var removingPasskeyName by rememberSaveable { mutableStateOf<String?>(null) }
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Settings", style = MaterialTheme.typography.headlineMedium)
        Text("Access tokens remain in memory. The rotating refresh token is encrypted by Android Keystore and excluded from backup.")
        if (PlatformCapabilities.supportsPasskeys(Build.VERSION.SDK_INT)) {
            Text("Passkeys", style = MaterialTheme.typography.titleLarge)
            if (passkeys.isEmpty()) {
                Text("No passkeys added yet.")
            }
            passkeys.forEach { passkey ->
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.fillMaxWidth(0.75f)) {
                        Text(passkey.displayName, style = MaterialTheme.typography.titleMedium)
                        Text("Added ${passkey.createdAt}")
                    }
                    TextButton({
                        viewModel.clearDialogPassword()
                        removingPasskeyId = passkey.id
                        removingPasskeyName = passkey.displayName
                    }) { Text("Remove") }
                }
            }
            OutlinedButton({
                viewModel.clearDialogPassword()
                addingPasskey = true
            }, enabled = activity != null) { Text("Add passkey") }
        }
        OutlinedButton(viewModel::logout) { Text("Sign out") }
    }
    if (addingPasskey) {
        SecurityChangeDialog(
            title = "Add passkey",
            explanation = "Re-authenticate, then Android will ask where to save the new passkey.",
            password = sensitive.dialogPassword,
            onPasswordChange = viewModel::updateDialogPassword,
            dismiss = {
                viewModel.clearDialogPassword()
                addingPasskey = false
            },
        ) { password ->
            viewModel.addPasskey(checkNotNull(activity), password)
            viewModel.clearDialogPassword()
            addingPasskey = false
        }
    }
    removingPasskeyId?.let { id ->
        SecurityChangeDialog(
            title = "Remove passkey",
            explanation = "Re-authenticate to remove ${checkNotNull(removingPasskeyName)} from your PCConnect account.",
            password = sensitive.dialogPassword,
            onPasswordChange = viewModel::updateDialogPassword,
            dismiss = {
                viewModel.clearDialogPassword()
                removingPasskeyId = null
                removingPasskeyName = null
            },
        ) { password ->
            viewModel.removePasskey(id, password)
            viewModel.clearDialogPassword()
            removingPasskeyId = null
            removingPasskeyName = null
        }
    }
}

@Composable
private fun SecurityChangeDialog(
    title: String,
    explanation: String,
    password: String,
    onPasswordChange: (String) -> Unit,
    dismiss: () -> Unit,
    confirm: (String) -> Unit,
) {
    val focusManager = LocalFocusManager.current
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(explanation)
                OutlinedTextField(
                    password,
                    { onPasswordChange(it.take(1024)) },
                    label = { Text("Password") },
                    visualTransformation = PasswordVisualTransformation(),
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Done),
                    keyboardActions = KeyboardActions(onDone = { focusManager.clearFocus() }),
                )
            }
        },
        confirmButton = { Button({ confirm(password) }, enabled = password.isNotBlank()) { Text("Continue") } },
        dismissButton = { TextButton(dismiss) { Text("Cancel") } },
    )
}
