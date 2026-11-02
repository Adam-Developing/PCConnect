package uk.co.adamkhattab.pcconnect.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import uk.co.adamkhattab.pcconnect.BuildConfig
import uk.co.adamkhattab.pcconnect.data.AppLog
import uk.co.adamkhattab.pcconnect.data.CommandTypes
import uk.co.adamkhattab.pcconnect.data.LogEntry
import uk.co.adamkhattab.pcconnect.data.LogLevel

@Composable
fun SettingsScreen(
    state: AppState,
    biometricAvailable: Boolean,
    onToggleBiometric: (Boolean) -> Unit,
    onToggleCommandProtection: (String) -> Unit,
    baseUrl: String,
    onBaseUrl: (String) -> Unit,
    onChangePassword: (String, String) -> Unit,
    onSignOut: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var changingPassword by remember { mutableStateOf(false) }

    LazyColumn(
        modifier.fillMaxSize(),
        contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 6.dp, bottom = 32.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        item { ProfileCard(state) }
        item {
            SecurityCard(
                state = state,
                biometricAvailable = biometricAvailable,
                onToggleBiometric = onToggleBiometric,
                onToggleCommandProtection = onToggleCommandProtection,
                onChangePassword = { changingPassword = true },
            )
        }
        item { ActivityCard(state) }
        item { AdvancedCard(baseUrl = baseUrl, onBaseUrl = onBaseUrl) }

        item {
            QuietButton(
                text = "Sign out",
                onClick = onSignOut,
                contentColour = PcColors.DangerInk,
            )
        }

        item {
            Text(
                "PCConnect ${BuildConfig.VERSION_NAME}",
                Modifier.fillMaxWidth(),
                color = PcColors.InkFaint,
                style = PcType.Caption.copy(fontSize = 12.sp),
                textAlign = TextAlign.Center,
            )
        }
    }

    if (changingPassword) {
        ChangePasswordDialog(
            onDismiss = { changingPassword = false },
            onConfirm = { current, replacement ->
                onChangePassword(current, replacement)
                changingPassword = false
            },
        )
    }
}

@Composable
private fun ProfileCard(state: AppState) {
    val profile = state.profile

    PcCard(Modifier.fillMaxWidth()) {
        Row(
            Modifier.padding(horizontal = 16.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Box(
                Modifier.size(44.dp).clip(CircleShape).background(PcColors.PrimaryTint),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    initialsOf(profile?.displayName ?: profile?.username ?: "?"),
                    color = PcColors.Primary,
                    style = PcType.BodyStrong.copy(fontSize = 16.sp),
                )
            }

            Column(Modifier.weight(1f)) {
                Text(
                    profile?.displayName ?: profile?.username ?: "Signed in",
                    color = PcColors.Ink,
                    style = PcType.BodyStrong.copy(fontSize = 16.sp),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    profile?.email ?: "",
                    color = PcColors.InkSoft,
                    style = PcType.Caption.copy(fontSize = 13.sp),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

internal fun initialsOf(name: String): String =
    name.split(' ', '.', '_', '-')
        .filter { it.isNotBlank() }
        .take(2)
        .map { it.first().uppercaseChar() }
        .joinToString("")
        .ifEmpty { "?" }

@Composable
private fun SecurityCard(
    state: AppState,
    biometricAvailable: Boolean,
    onToggleBiometric: (Boolean) -> Unit,
    onToggleCommandProtection: (String) -> Unit,
    onChangePassword: () -> Unit,
) {
    PcCard(Modifier.fillMaxWidth()) {
        // Single option for fingerprint confirmation
        Row(
            Modifier
                .fillMaxWidth()
                .clickable(enabled = biometricAvailable) { onToggleBiometric(!state.biometricEnabled) }
                .padding(horizontal = 16.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Box(
                Modifier
                    .size(40.dp)
                    .clip(CircleShape)
                    .background(if (state.biometricEnabled) PcColors.PrimaryTint else PcColors.Track),
                contentAlignment = Alignment.Center,
            ) {
                PcIcon(
                    PcIcons.Fingerprint,
                    null,
                    size = 24.dp,
                    tint = if (state.biometricEnabled) PcColors.Primary else PcColors.InkFaint,
                )
            }

            Column(Modifier.weight(1f)) {
                Text("Fingerprint confirmation", color = PcColors.Ink, style = PcType.BodyStrong)
                Caption(
                    if (!biometricAvailable) {
                        "Fingerprint sensor unavailable on this device"
                    } else if (state.biometricEnabled) {
                        "Confirm protected commands using your fingerprint"
                    } else {
                        "Disabled (password required for protected commands)"
                    },
                )
            }

            PcSwitch(
                checked = state.biometricEnabled,
                onCheckedChange = onToggleBiometric,
                enabled = biometricAvailable,
            )
        }

        RowDivider(startIndent = 16.dp)

        // Mobile-owned command protection preferences (freely editable across multiple PCs)
        Column(Modifier.padding(horizontal = 16.dp, vertical = 14.dp)) {
            Text("Protected commands", color = PcColors.Ink, style = PcType.BodyStrong)
            Spacer(Modifier.height(2.dp))
            Caption("Choose your mobile preferences for commands sent from this phone:")

            Spacer(Modifier.height(10.dp))

            InfoNote(
                text = "You can edit your mobile preferences freely for all your PCs. When controlling a specific PC, that desktop's own preferences take priority.",
                icon = PcIcons.Info,
                background = PcColors.PrimaryTint,
                iconTint = PcColors.Primary,
                modifier = Modifier.fillMaxWidth(),
            )

            Spacer(Modifier.height(12.dp))

            val allCommands = listOf(
                CommandTypes.SHUTDOWN,
                CommandTypes.RESTART,
                CommandTypes.SIGN_OUT,
                CommandTypes.HIBERNATE,
                CommandTypes.SLEEP,
                CommandTypes.LOCK,
            )

            val confirmLabel = if (state.biometricEnabled) "fingerprint confirmation" else "password confirmation"

            Column(
                Modifier
                    .fillMaxWidth()
                    .clip(PcShapes.Control)
                    .border(1.dp, PcColors.Border, PcShapes.Control),
            ) {
                allCommands.forEachIndexed { index, cmdType ->
                    if (index > 0) RowDivider()

                    val isProtected = cmdType in state.mobileProtectedCommands

                    val iconTint = when (cmdType) {
                        CommandTypes.SHUTDOWN, CommandTypes.RESTART -> PcColors.Danger
                        CommandTypes.HIBERNATE, CommandTypes.SLEEP, CommandTypes.SIGN_OUT -> PcColors.WarnInk
                        else -> PcColors.Primary
                    }

                    Row(
                        Modifier
                            .fillMaxWidth()
                            .clickable { onToggleCommandProtection(cmdType) }
                            .padding(horizontal = 14.dp, vertical = 12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                    ) {
                        Box(
                            Modifier
                                .size(36.dp)
                                .clip(PcShapes.SmallControl)
                                .background(if (isProtected) iconTint.copy(alpha = 0.12f) else PcColors.Track),
                            contentAlignment = Alignment.Center,
                        ) {
                            PcIcon(
                                PcIcons.forCommand(cmdType),
                                null,
                                size = 20.dp,
                                tint = if (isProtected) iconTint else PcColors.InkSoft,
                            )
                        }

                        Column(Modifier.weight(1f)) {
                            Text(
                                CommandTypes.label(cmdType),
                                color = PcColors.Ink,
                                style = PcType.BodyStrong,
                            )
                            Caption(
                                if (isProtected) {
                                    "Requires $confirmLabel"
                                } else {
                                    "No mobile confirmation"
                                },
                            )
                        }

                        PcSwitch(
                            checked = isProtected,
                            onCheckedChange = { onToggleCommandProtection(cmdType) },
                        )
                    }
                }
            }
        }

        RowDivider(startIndent = 16.dp)

        SettingRow(
            icon = PcIcons.Key,
            title = "Change password",
            onClick = onChangePassword,
        )
    }
}

@Composable
private fun SettingRow(
    icon: Int,
    title: String,
    onClick: () -> Unit,
    subtitle: String? = null,
) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        PcIcon(icon, null, size = 22.dp)

        Column(Modifier.weight(1f)) {
            Text(title, color = PcColors.Ink, style = PcType.Body)
            if (subtitle != null) Caption(subtitle)
        }

        PcIcon(PcIcons.ChevronRight, null, size = 22.dp, tint = PcColors.InkFaint)
    }
}

/**
 * What has been sent from this phone and what came back.
 *
 * The app this replaces failed silently: a command that did not work left no
 * trace at all. This is the first thing to ask someone for when they say it
 * isn't working.
 */
@Composable
private fun ActivityCard(state: AppState) {
    var open by rememberSaveable { mutableStateOf(false) }

    PcCard(Modifier.fillMaxWidth()) {
        Row(
            Modifier
                .fillMaxWidth()
                .clickable { open = !open }
                .padding(horizontal = 16.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            PcIcon(PcIcons.History, null, size = 22.dp)

            Column(Modifier.weight(1f)) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text("Activity", color = PcColors.Ink, style = PcType.Body)
                    Text(
                        "${state.commands.size}",
                        Modifier
                            .clip(PcShapes.Pill)
                            .background(PcColors.Track)
                            .padding(horizontal = 7.dp, vertical = 2.dp),
                        color = PcColors.InkFaint,
                        style = PcType.Label.copy(fontSize = 12.sp),
                    )
                }
                Caption("Commands sent from this phone and what happened.")
            }

            PcIcon(
                if (open) PcIcons.ExpandLess else PcIcons.ExpandMore,
                null,
                size = 22.dp,
                tint = PcColors.InkFaint,
            )
        }

        if (open) {
            if (state.commands.isEmpty()) {
                Column(Modifier.padding(horizontal = 16.dp, vertical = 14.dp)) {
                    Caption("Nothing sent from this phone yet.")
                }
            } else {
                state.commands.take(20).forEach { command ->
                    RowDivider()

                    Row(
                        Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 11.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                    ) {
                        Text(
                            AppViewModel.formatLogTime(command.issuedAt),
                            Modifier.width(52.dp),
                            color = PcColors.InkSoft,
                            style = PcType.MonoSmall,
                        )

                        Column(Modifier.weight(1f)) {
                            Text(
                                CommandTypes.label(command.type),
                                color = PcColors.Ink,
                                style = PcType.BodySmall.copy(fontSize = 14.5.sp),
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                            val devName = state.devices.firstOrNull { it.id == command.deviceId }?.displayName
                                ?: "That PC"
                            val failureMessage = command.resultMessage?.takeIf { it.isNotBlank() && command.status == "failed" }

                            if (failureMessage != null) {
                                Text(
                                    "$devName · $failureMessage",
                                    color = PcColors.DangerInk,
                                    style = PcType.Caption.copy(fontSize = 12.sp),
                                    maxLines = 2,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            } else {
                                Caption(devName)
                            }
                        }

                        OutcomeBadge(
                            text = outcomeLabel(command),
                            tone = outcomeTone(command),
                        )
                    }
                }
            }
        }
    }
}

/**
 * The backend address and the request log.
 *
 * The address is here because a build-time default must stay overridable at
 * runtime (06 §1) — the app this replaces compiled a developer's LAN address
 * into a release build.
 */
@Composable
private fun AdvancedCard(baseUrl: String, onBaseUrl: (String) -> Unit) {
    var draft by rememberSaveable(baseUrl) { mutableStateOf(baseUrl) }
    var logsOpen by rememberSaveable { mutableStateOf(false) }

    val entries by AppLog.entries.collectAsState()
    val clipboard = LocalClipboardManager.current

    PcCard(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp)) {
            Text("PCConnect server", color = PcColors.Ink, style = PcType.Body)
            Spacer(Modifier.height(8.dp))

            PcTextField(
                value = draft,
                onValueChange = { draft = it },
                height = 46.dp,
                textStyle = PcType.MonoTime.copy(fontSize = 13.sp),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
            )

            if (draft.trim().trimEnd('/') != baseUrl) {
                Spacer(Modifier.height(8.dp))
                QuietButton("Save server address", onClick = { onBaseUrl(draft) }, height = 42.dp)
            }
        }

        RowDivider()

        Row(
            Modifier
                .fillMaxWidth()
                .clickable { logsOpen = !logsOpen }
                .padding(horizontal = 16.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            PcIcon(PcIcons.Info, null, size = 22.dp)

            Column(Modifier.weight(1f)) {
                Text("Diagnostics", color = PcColors.Ink, style = PcType.Body)
                Caption(
                    if (entries.isEmpty()) {
                        "Nothing yet."
                    } else {
                        "${entries.size} entries. No passwords or tokens are recorded."
                    },
                )
            }

            PcIcon(
                if (logsOpen) PcIcons.ExpandLess else PcIcons.ExpandMore,
                null,
                size = 22.dp,
                tint = PcColors.InkFaint,
            )
        }

        if (logsOpen) {
            RowDivider()

            Row(
                Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp),
                horizontalArrangement = Arrangement.End,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Row(
                    Modifier
                        .clip(PcShapes.SmallControl)
                        .clickable(enabled = entries.isNotEmpty()) {
                            clipboard.setText(AnnotatedString(AppLog.dump()))
                        }
                        .padding(horizontal = 10.dp, vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    PcIcon(PcIcons.ContentCopy, null, size = 18.dp, tint = PcColors.Primary)
                    Text("Copy", color = PcColors.Primary, style = PcType.Label.copy(fontSize = 13.sp))
                }

                Text(
                    "Clear",
                    Modifier
                        .clip(PcShapes.SmallControl)
                        .clickable(enabled = entries.isNotEmpty()) { AppLog.clear() }
                        .padding(horizontal = 10.dp, vertical = 8.dp),
                    color = PcColors.Primary,
                    style = PcType.Label.copy(fontSize = 13.sp),
                )
            }

            Column(
                Modifier
                    .heightIn(max = 280.dp)
                    .verticalScroll(rememberScrollState()),
            ) {
                entries.asReversed().forEach { entry ->
                    RowDivider()
                    LogLine(entry)
                }
            }
        }
    }
}

@Composable
private fun LogLine(entry: LogEntry) {
    val colour = when (entry.level) {
        LogLevel.Error -> PcColors.DangerInk
        LogLevel.Warn -> PcColors.WarnInk
        LogLevel.Info -> PcColors.Ink
        LogLevel.Debug -> PcColors.InkSoft
    }

    Column(Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 7.dp)) {
        Text(
            "${entry.timestamp()}  ${entry.tag}",
            color = PcColors.InkFaint,
            style = PcType.MonoSmall.copy(fontSize = 11.sp),
        )
        Text(
            entry.message,
            // A long line scrolls sideways rather than wrapping into a wall:
            // these are mostly "METHOD /path -> status in Nms" and line up.
            Modifier.horizontalScroll(rememberScrollState()),
            color = colour,
            style = PcType.MonoSmall,
            maxLines = 1,
        )
    }
}

@Composable
private fun ChangePasswordDialog(onDismiss: () -> Unit, onConfirm: (String, String) -> Unit) {
    var current by remember { mutableStateOf("") }
    var replacement by remember { mutableStateOf("") }

    val ready = current.isNotEmpty() && replacement.length >= 12

    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = PcColors.Surface,
        shape = PcShapes.Dialog,
        title = { Text("Change password", color = PcColors.Ink, style = PcType.Heading) },
        text = {
            Column {
                PcTextField(
                    value = current,
                    onValueChange = { current = it },
                    label = "Current password",
                    height = 48.dp,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    visualTransformation = PasswordVisualTransformation(),
                )
                Spacer(Modifier.height(12.dp))
                PcTextField(
                    value = replacement,
                    onValueChange = { replacement = it },
                    label = "New password",
                    height = 48.dp,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    visualTransformation = PasswordVisualTransformation(),
                )
                Spacer(Modifier.height(10.dp))
                Caption("At least 12 characters. Every other signed-in session stays signed in.")
            }
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(current, replacement) }, enabled = ready) {
                Text(
                    "Change it",
                    color = if (ready) PcColors.Primary else PcColors.InkDisabled,
                    style = PcType.BodyStrong,
                )
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("Cancel", color = PcColors.InkSoft, style = PcType.BodySmall)
            }
        },
    )
}
