package uk.co.adamkhattab.pcconnect.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import uk.co.adamkhattab.pcconnect.data.CommandTypes
import uk.co.adamkhattab.pcconnect.data.Device

/**
 * "My PCs".
 *
 * The safe commands are one tap from the list. Anything that ends a session or
 * a power state lives one level down, on the PC's own screen, behind a
 * confirmation — so "shut down" is never adjacent to "lock".
 */
@Composable
fun DevicesScreen(
    state: AppState,
    onOpenDevice: (String) -> Unit,
    onCommand: (String, String) -> Unit,
    onNewReminder: (String?) -> Unit,
    onPair: (String) -> Unit,
    onShareDownloadLink: () -> Unit,
    modifier: Modifier = Modifier,
) {
    if (state.devices.isEmpty()) {
        NoDevicesYet(
            username = state.profile?.username ?: state.profile?.displayName ?: "your account",
            onPair = onPair,
            onShareDownloadLink = onShareDownloadLink,
            modifier = modifier,
        )
        return
    }

    LazyColumn(
        modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(
            start = 16.dp,
            end = 16.dp,
            top = 6.dp,
            bottom = 24.dp,
        ),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        item {
            QuietButton(
                text = if (state.remindersTargetable) "Reminder for all PCs" else "New reminder",
                onClick = { onNewReminder(null) },
                icon = PcIcons.AddAlert,
                iconTint = PcColors.Primary,
            )
        }

        items(state.devices, key = { it.id }) { device ->
            DeviceCard(
                device = device,
                showReminderCount = state.remindersTargetable,
                onOpen = { onOpenDevice(device.id) },
                onCommand = { type -> onCommand(device.id, type) },
                onNewReminder = { onNewReminder(device.id) },
            )
        }

        item {
            Spacer(Modifier.height(2.dp))
            AddAPcCard(onPair = onPair)
        }
    }
}

@Composable
private fun DeviceCard(
    device: Device,
    showReminderCount: Boolean,
    onOpen: () -> Unit,
    onCommand: (String) -> Unit,
    onNewReminder: () -> Unit,
) {
    PcCard(Modifier.fillMaxWidth(), onClick = onOpen) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(device.displayName, color = PcColors.Ink, style = PcType.CardTitle)

                    Spacer(Modifier.height(3.dp))

                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(6.dp),
                    ) {
                        Dot(if (device.isOnline) PcColors.OnlineDot else PcColors.OfflineDot, 8.dp)
                        Text(
                            describeDevice(device, showReminderCount),
                            color = PcColors.InkSoft,
                            style = PcType.Caption.copy(fontSize = 13.sp),
                        )
                    }
                }

                PcIcon(PcIcons.ChevronRight, null, size = 22.dp, tint = PcColors.InkFaint)
            }

            Spacer(Modifier.height(14.dp))

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                // Only the two recoverable commands. An offline PC shows them
                // greyed rather than hiding them, so the row does not reflow
                // every time a machine goes to sleep.
                listOf(CommandTypes.LOCK, CommandTypes.SLEEP).forEach { type ->
                    val allowed = device.allowedCommands.isEmpty() || type in device.allowedCommands
                    val enabled = device.isOnline && allowed

                    PcChip(
                        text = CommandTypes.label(type),
                        icon = PcIcons.forCommand(type),
                        style = if (enabled) ChipStyle.Outline else ChipStyle.Disabled,
                        onClick = { onCommand(type) },
                    )
                }

                PcChip(
                    text = "Reminder",
                    icon = PcIcons.Add,
                    style = ChipStyle.Tinted,
                    onClick = onNewReminder,
                )
            }
        }
    }
}

private fun describeDevice(device: Device, showReminderCount: Boolean): String = buildList {
    add(
        if (device.isOnline) {
            "Online"
        } else {
            device.lastSeenAt?.let { "Seen ${AppViewModel.formatLogTime(it)}" } ?: "Never seen"
        },
    )
    if (device.osVersion.isNotBlank()) add(device.osVersion)
}.joinToString(" · ")

/** Where a PC gets added, in the design's numbered-steps card. */
@Composable
private fun NoDevicesYet(
    username: String,
    onPair: (String) -> Unit,
    onShareDownloadLink: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier
            .fillMaxSize()
            .padding(16.dp),
        verticalArrangement = Arrangement.Center,
    ) {
        PcCard(Modifier.fillMaxWidth(), shape = PcShapes.Tile) {
            Column(
                Modifier.padding(horizontal = 24.dp, vertical = 28.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Box(
                    Modifier
                        .size(64.dp)
                        .clip(androidx.compose.foundation.shape.RoundedCornerShape(20.dp))
                        .background(PcColors.PrimaryTint),
                    contentAlignment = Alignment.Center,
                ) {
                    PcIcon(PcIcons.Computer, null, size = 32.dp, tint = PcColors.Primary)
                }

                Spacer(Modifier.height(18.dp))
                Text("No PCs yet", color = PcColors.Ink, style = PcType.Heading.copy(fontSize = 19.sp))
                Spacer(Modifier.height(8.dp))
                Text(
                    "Install PCConnect on a Windows PC and sign in there as $username. " +
                        "It shows a code; type it below and the PC is yours to control.",
                    color = PcColors.InkSoft,
                    style = PcType.BodySmall,
                    textAlign = TextAlign.Center,
                )

                Spacer(Modifier.height(22.dp))

                Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    listOf(
                        "Download PCConnect for Windows",
                        "Sign in with this account",
                        "Type the code it shows",
                    ).forEachIndexed { index, step ->
                        Row(
                            horizontalArrangement = Arrangement.spacedBy(12.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Box(
                                Modifier.size(24.dp).clip(CircleShape).background(PcColors.Ink),
                                contentAlignment = Alignment.Center,
                            ) {
                                Text(
                                    "${index + 1}",
                                    color = androidx.compose.ui.graphics.Color.White,
                                    style = PcType.MonoSmall.copy(fontWeight = FontWeight.SemiBold),
                                )
                            }
                            Text(step, color = PcColors.Ink, style = PcType.BodySmall)
                        }
                    }
                }

                Spacer(Modifier.height(20.dp))
                PairingEntry(onPair = onPair)
                Spacer(Modifier.height(12.dp))

                QuietButton(
                    text = "Send myself the download link",
                    onClick = onShareDownloadLink,
                    icon = PcIcons.IosShare,
                )
            }
        }
    }
}

/** The same pairing entry, offered again once at least one PC exists. */
@Composable
private fun AddAPcCard(onPair: (String) -> Unit) {
    PcCard(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp)) {
            Text("Add another PC", color = PcColors.Ink, style = PcType.BodyStrong)
            Spacer(Modifier.height(4.dp))
            Caption("Sign in to PCConnect on that PC and type the code it shows.")
            Spacer(Modifier.height(12.dp))
            PairingEntry(onPair = onPair)
        }
    }
}

@Composable
private fun PairingEntry(onPair: (String) -> Unit) {
    var code by rememberSaveable { mutableStateOf("") }

    Column(Modifier.fillMaxWidth()) {
        PcTextField(
            value = code,
            // Upper case as it is typed: the codes are shown in capitals and a
            // lower-case one that silently fails to match reads as a broken code.
            onValueChange = { code = it.uppercase() },
            label = "Code from that PC",
            placeholder = "7KQ4-M2XA",
            height = 48.dp,
            textStyle = PcType.MonoTime.copy(fontSize = 17.sp, letterSpacing = 1.sp),
            keyboardOptions = KeyboardOptions(
                keyboardType = androidx.compose.ui.text.input.KeyboardType.Ascii,
                autoCorrectEnabled = false,
            ),
        )

        Spacer(Modifier.height(10.dp))

        PrimaryButton(
            text = "Add this PC",
            onClick = {
                onPair(code)
                code = ""
            },
            enabled = code.isNotBlank(),
            height = 46.dp,
        )
    }
}
