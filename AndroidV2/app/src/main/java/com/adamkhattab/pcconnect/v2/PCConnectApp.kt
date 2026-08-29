package com.adamkhattab.pcconnect.v2

import android.app.DatePickerDialog
import android.app.TimePickerDialog
import android.os.Build
import android.text.format.DateFormat
import android.util.Patterns
import androidx.activity.compose.LocalActivity
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
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
import androidx.compose.material3.Checkbox
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
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
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
import com.adamkhattab.pcconnect.v2.data.DeviceEntity
import com.adamkhattab.pcconnect.v2.data.PlatformCapabilities
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

@Composable
fun PCConnectApp(viewModel: MainViewModel) {
    val session by viewModel.session.collectAsStateWithLifecycle()
    val message by viewModel.message.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    LaunchedEffect(message) {
        message?.let { snackbar.showSnackbar(it.text); viewModel.dismissMessage() }
    }
    MaterialTheme {
        Scaffold(snackbarHost = { SnackbarHost(snackbar) }) { padding ->
            Box(Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.Center) {
                when (session) {
                    SessionState.CHECKING -> CircularProgressIndicator(
                        Modifier.semantics { contentDescription = "Restoring your PCConnect session" },
                    )
                    SessionState.SIGNED_OUT -> LoginScreen(viewModel)
                    SessionState.SIGNED_IN -> ControllerScreen(viewModel)
                }
            }
        }
    }
}

@Composable
private fun LoginScreen(viewModel: MainViewModel) {
    val resetToken by viewModel.passwordResetToken.collectAsStateWithLifecycle()
    if (resetToken != null) {
        PasswordResetScreen(viewModel)
        return
    }
    var mode by remember { mutableStateOf(LoginMode.SignIn) }
    var login by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var email by remember { mutableStateOf("") }
    var username by remember { mutableStateOf("") }
    var displayName by remember { mutableStateOf("") }
    var marketingOptIn by remember { mutableStateOf(false) }
    val busy by viewModel.busy.collectAsStateWithLifecycle()
    val activity = LocalActivity.current
    val focusManager = LocalFocusManager.current
    val usernameLength = username.trim().length
    val usernameError = username.isNotEmpty() && usernameLength !in 3..50
    val emailValid = email.trim().let { it.length in 3..254 && Patterns.EMAIL_ADDRESS.matcher(it).matches() }
    val emailError = email.isNotEmpty() && !emailValid
    val displayNameLength = displayName.trim().length
    val displayNameError = displayName.isNotEmpty() && displayNameLength !in 1..100
    val passwordValid = password.length in 12..1024 && password.none(Char::isISOControl)
    val passwordError = password.isNotEmpty() && !passwordValid
    fun switchMode(next: LoginMode) {
        login = ""
        password = ""
        email = ""
        username = ""
        displayName = ""
        marketingOptIn = false
        mode = next
    }
    Column(Modifier.padding(28.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("PCConnect", style = MaterialTheme.typography.headlineLarge)
        when (mode) {
            LoginMode.SignIn -> {
                Text("Securely control your enrolled computers.")
                OutlinedTextField(
                    login,
                    { login = it.take(254) },
                    label = { Text("Email or username") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email, imeAction = ImeAction.Next),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    password,
                    { password = it.take(1024) },
                    label = { Text("Password") },
                    visualTransformation = PasswordVisualTransformation(),
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Done),
                    keyboardActions = KeyboardActions(onDone = {
                        focusManager.clearFocus()
                        if (!busy && login.isNotBlank() && password.isNotBlank()) {
                            viewModel.login(login, password)
                        }
                    }),
                    modifier = Modifier.fillMaxWidth(),
                )
                Button({ viewModel.login(login, password) }, enabled = !busy && login.isNotBlank() && password.isNotBlank(), modifier = Modifier.fillMaxWidth()) {
                    Text("Sign in")
                }
                if (PlatformCapabilities.supportsPasskeys(Build.VERSION.SDK_INT)) {
                    OutlinedButton(
                        { viewModel.loginWithPasskey(checkNotNull(activity), login) },
                        enabled = !busy && activity != null,
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text("Sign in with a passkey") }
                }
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    TextButton({ switchMode(LoginMode.Register) }) { Text("Create account") }
                    TextButton({ switchMode(LoginMode.Forgot) }) { Text("Forgot password?") }
                }
            }
            LoginMode.Register -> {
                Text("Create an account", style = MaterialTheme.typography.titleLarge)
                OutlinedTextField(
                    username,
                    { username = it.take(50) },
                    label = { Text("Username") },
                    supportingText = if (usernameError) { { Text("Use 3–50 characters.") } } else null,
                    isError = usernameError,
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(
                        capitalization = KeyboardCapitalization.None,
                        keyboardType = KeyboardType.Text,
                        imeAction = ImeAction.Next,
                    ),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    email,
                    { email = it.take(254) },
                    label = { Text("Email") },
                    supportingText = if (emailError) { { Text("Enter a valid email address.") } } else null,
                    isError = emailError,
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email, imeAction = ImeAction.Next),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    displayName,
                    { displayName = it.take(100) },
                    label = { Text("Display name") },
                    supportingText = if (displayNameError) { { Text("Use 1–100 characters.") } } else null,
                    isError = displayNameError,
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(
                        capitalization = KeyboardCapitalization.Words,
                        imeAction = ImeAction.Next,
                    ),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    password,
                    { password = it.take(1024) },
                    label = { Text("Password") },
                    supportingText = if (passwordError) { { Text("Use at least 12 characters.") } } else null,
                    isError = passwordError,
                    visualTransformation = PasswordVisualTransformation(),
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Done),
                    keyboardActions = KeyboardActions(onDone = { focusManager.clearFocus() }),
                    modifier = Modifier.fillMaxWidth(),
                )
                Row(
                    Modifier.fillMaxWidth().clickable { marketingOptIn = !marketingOptIn },
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Checkbox(marketingOptIn, { marketingOptIn = it })
                    Text("Send me optional PCConnect product updates")
                }
                Button(
                    { viewModel.register(username, email, displayName, password, marketingOptIn) },
                    enabled = !busy && usernameLength in 3..50 && emailValid && displayNameLength in 1..100 && passwordValid,
                    modifier = Modifier.fillMaxWidth(),
                ) { Text("Create account") }
                TextButton({ switchMode(LoginMode.SignIn) }) { Text("Back to sign in") }
            }
            LoginMode.Forgot -> {
                Text("Reset password", style = MaterialTheme.typography.titleLarge)
                Text("Enter your email address. The response is the same whether or not an account exists.")
                OutlinedTextField(
                    email,
                    { email = it.take(254) },
                    label = { Text("Email") },
                    supportingText = if (emailError) { { Text("Enter a valid email address.") } } else null,
                    isError = emailError,
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email, imeAction = ImeAction.Done),
                    keyboardActions = KeyboardActions(onDone = { focusManager.clearFocus() }),
                    modifier = Modifier.fillMaxWidth(),
                )
                Button(
                    { viewModel.requestPasswordReset(email) },
                    enabled = !busy && emailValid,
                    modifier = Modifier.fillMaxWidth(),
                ) { Text("Send reset link") }
                TextButton({ switchMode(LoginMode.SignIn) }) { Text("Back to sign in") }
            }
        }
    }
}

private enum class LoginMode { SignIn, Register, Forgot }

@Composable
private fun PasswordResetScreen(viewModel: MainViewModel) {
    var password by remember { mutableStateOf("") }
    var confirmation by remember { mutableStateOf("") }
    val busy by viewModel.busy.collectAsStateWithLifecycle()
    Column(Modifier.padding(28.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Choose a new password", style = MaterialTheme.typography.headlineMedium)
        Text("This will revoke every existing PCConnect session.")
        OutlinedTextField(
            password,
            { password = it.take(1024) },
            label = { Text("New password") },
            supportingText = if (password.isNotEmpty() && password.length < 12) { { Text("Use at least 12 characters.") } } else null,
            isError = password.isNotEmpty() && password.length < 12,
            visualTransformation = PasswordVisualTransformation(),
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Next),
            modifier = Modifier.fillMaxWidth(),
        )
        OutlinedTextField(
            confirmation,
            { confirmation = it.take(1024) },
            label = { Text("Confirm password") },
            supportingText = if (confirmation.isNotEmpty() && confirmation != password) { { Text("Passwords do not match.") } } else null,
            isError = confirmation.isNotEmpty() && confirmation != password,
            visualTransformation = PasswordVisualTransformation(),
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Done),
            modifier = Modifier.fillMaxWidth(),
        )
        Button(
            { viewModel.completePasswordReset(password) },
            enabled = !busy && password.length >= 12 && password == confirmation,
            modifier = Modifier.fillMaxWidth(),
        ) { Text("Change password") }
        TextButton(viewModel::cancelPasswordReset) { Text("Cancel") }
    }
}

@Composable
private fun ControllerScreen(viewModel: MainViewModel) {
    var selected by remember { mutableIntStateOf(0) }
    val destinations = listOf(
        Triple("Devices", R.drawable.ic_devices, "Enrolled devices"),
        Triple("Commands", R.drawable.ic_history, "Command history"),
        Triple("Reminders", R.drawable.ic_reminders, "Reminders"),
        Triple("Settings", R.drawable.ic_settings, "Settings"),
    )
    Scaffold(
        bottomBar = {
            NavigationBar {
                destinations.forEachIndexed { index, (label, iconResource, description) ->
                    NavigationBarItem(
                        selected = selected == index,
                        onClick = { selected = index },
                        icon = { Icon(painterResource(iconResource), contentDescription = description) },
                        label = { Text(label) },
                    )
                }
            }
        },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            when (selected) {
                0 -> DevicesScreen(viewModel)
                1 -> CommandsScreen(viewModel)
                2 -> RemindersScreen(viewModel)
                else -> SettingsScreen(viewModel)
            }
        }
    }
}

@Composable
private fun DevicesScreen(viewModel: MainViewModel) {
    val devices by viewModel.devices.collectAsStateWithLifecycle()
    val windowsSids by viewModel.windowsSids.collectAsStateWithLifecycle()
    var pending by remember { mutableStateOf<Pair<DeviceEntity, String>?>(null) }
    var pendingSid by remember { mutableStateOf<Triple<String, String, String>?>(null) }
    var pendingSidRevoke by remember { mutableStateOf<Triple<String, String, String>?>(null) }
    var pendingDeviceRevoke by remember { mutableStateOf<Pair<String, String>?>(null) }
    var enrollmentCode by remember { mutableStateOf("") }
    val focusManager = LocalFocusManager.current
    val enrollmentCodeError = enrollmentCode.isNotEmpty() && enrollmentCode.length != 8
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
                                        if (type == "lock") viewModel.command(device.id, type, null) else pending = device to type
                                    }) { Text(commandLabel(type)) }
                                }
                            }
                    }
                    windowsSids[device.id].orEmpty().filter { it.status == "pending" }.forEach { sid ->
                        OutlinedButton({ pendingSid = Triple(device.id, sid.windowsSid, sid.displayLabel ?: "Windows account") }) {
                            Text("Approve ${sid.displayLabel ?: "Windows account"}")
                        }
                    }
                    windowsSids[device.id].orEmpty().filter { it.status == "authorized" }.forEach { sid ->
                        TextButton({ pendingSidRevoke = Triple(device.id, sid.windowsSid, sid.displayLabel ?: "Windows account") }) {
                            Text("Revoke ${sid.displayLabel ?: "Windows account"}")
                        }
                    }
                    if (device.status != "revoked") {
                        TextButton({ pendingDeviceRevoke = device.id to device.displayName }) { Text("Revoke device") }
                    }
                }
                HorizontalDivider()
            }
        }
    }
    pending?.let { (device, type) -> StepUpDialog(type, device.displayName, { pending = null }) { password ->
        viewModel.command(device.id, type, password)
        pending = null
    } }
    pendingSid?.let { (deviceId, sid, label) -> StepUpDialog("Windows account", label, { pendingSid = null }) { password ->
        viewModel.authorizeWindowsSid(deviceId, sid, password)
        pendingSid = null
    } }
    pendingSidRevoke?.let { (deviceId, sid, label) ->
        SecurityChangeDialog(
            title = "Revoke Windows account",
            explanation = "Re-authenticate to stop $label receiving reminders or interactive commands on this PC.",
            dismiss = { pendingSidRevoke = null },
        ) { password ->
            viewModel.revokeWindowsSid(deviceId, sid, password)
            pendingSidRevoke = null
        }
    }
    pendingDeviceRevoke?.let { (deviceId, name) ->
        SecurityChangeDialog(
            title = "Revoke device",
            explanation = "Re-authenticate to revoke $name and all of its device credentials.",
            dismiss = { pendingDeviceRevoke = null },
        ) { password ->
            viewModel.revokeDevice(deviceId, password)
            pendingDeviceRevoke = null
        }
    }
}

@Composable
private fun StepUpDialog(type: String, device: String, dismiss: () -> Unit, confirm: (String) -> Unit) {
    var password by remember { mutableStateOf("") }
    val focusManager = LocalFocusManager.current
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text("Confirm ${commandLabel(type)}") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("Re-authenticate before sending this command to $device.")
                OutlinedTextField(
                    password,
                    { password = it.take(1024) },
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
    var text by remember { mutableStateOf("") }
    var selectedDate by remember { mutableStateOf<LocalDate?>(null) }
    var selectedTime by remember { mutableStateOf<LocalTime?>(null) }
    val context = LocalContext.current
    val focusManager = LocalFocusManager.current
    val suggestedStart = remember { LocalDateTime.now().plusHours(1).withSecond(0).withNano(0) }
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
                            { _, year, month, day -> selectedDate = LocalDate.of(year, month + 1, day) },
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
                            { _, hour, minute -> selectedTime = LocalTime.of(hour, minute) },
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
                    selectedDate = null
                    selectedTime = null
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
    val activity = LocalActivity.current
    var addingPasskey by remember { mutableStateOf(false) }
    var removingPasskey by remember { mutableStateOf<Pair<String, String>?>(null) }
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
                    TextButton({ removingPasskey = passkey.id to passkey.displayName }) { Text("Remove") }
                }
            }
            OutlinedButton({ addingPasskey = true }, enabled = activity != null) { Text("Add passkey") }
        }
        OutlinedButton(viewModel::logout) { Text("Sign out") }
    }
    if (addingPasskey) {
        SecurityChangeDialog(
            title = "Add passkey",
            explanation = "Re-authenticate, then Android will ask where to save the new passkey.",
            dismiss = { addingPasskey = false },
        ) { password ->
            viewModel.addPasskey(checkNotNull(activity), password)
            addingPasskey = false
        }
    }
    removingPasskey?.let { (id, name) ->
        SecurityChangeDialog(
            title = "Remove passkey",
            explanation = "Re-authenticate to remove $name from your PCConnect account.",
            dismiss = { removingPasskey = null },
        ) { password ->
            viewModel.removePasskey(id, password)
            removingPasskey = null
        }
    }
}

@Composable
private fun SecurityChangeDialog(title: String, explanation: String, dismiss: () -> Unit, confirm: (String) -> Unit) {
    var password by remember { mutableStateOf("") }
    val focusManager = LocalFocusManager.current
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(explanation)
                OutlinedTextField(
                    password,
                    { password = it.take(1024) },
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
