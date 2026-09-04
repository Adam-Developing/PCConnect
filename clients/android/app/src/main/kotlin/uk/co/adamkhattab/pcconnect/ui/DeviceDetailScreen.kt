package uk.co.adamkhattab.pcconnect.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import uk.co.adamkhattab.pcconnect.data.Command
import uk.co.adamkhattab.pcconnect.data.CommandTypes
import uk.co.adamkhattab.pcconnect.data.Device
import uk.co.adamkhattab.pcconnect.data.Reminder

/**
 * One PC: what can be done to it, what it will show, and what it has done.
 *
 * The destructive four live here rather than in the list, and each one carries
 * the key that says it will ask for a password before it is sent.
 */
@Composable
fun DeviceDetailScreen(
    device: Device,
    state: AppState,
    onBack: () -> Unit,
    onCommand: (String) -> Unit,
    onNewReminder: () -> Unit,
    onRename: (String) -> Unit,
    onRemove: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var editing by remember { mutableStateOf(false) }

    val reminders = remember(state.reminders, device.id) {
        state.reminders
            .filter { it.showsOn(device.id) && !it.isCompleted }
            .sortedBy { it.dueAt }
            .take(6)
    }
    val recent = remember(state.commands, device.id) {
        state.commands.filter { it.deviceId == device.id }.take(6)
    }

    Column(modifier.fillMaxSize()) {
        DetailTopBar(device, onBack = onBack, onEdit = { editing = true })

        LazyColumn(
            Modifier.fillMaxSize(),
            contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(18.dp),
        ) {
            item { ControlsSection(device, onCommand) }

            item {
                RemindersSection(
                    reminders = reminders,
                    targetable = state.remindersTargetable,
                    onAdd = onNewReminder,
                )
            }

            if (recent.isNotEmpty()) {
                item { RecentSection(recent) }
            }
        }
    }

    if (editing) {
        DeviceOptionsDialog(
            device = device,
            onDismiss = { editing = false },
            onRename = {
                onRename(it)
                editing = false
            },
            onRemove = {
                editing = false
                onRemove()
            },
        )
    }
}

@Composable
private fun DetailTopBar(device: Device, onBack: () -> Unit, onEdit: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .height(56.dp)
            .padding(start = 8.dp, end = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        IconAction(PcIcons.ArrowBack, "Back", onBack, tint = PcColors.Ink)

        Column(Modifier.weight(1f).padding(horizontal = 4.dp)) {
            Text(
                device.displayName,
                color = PcColors.Ink,
                style = PcType.Heading,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )

            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                Dot(if (device.isOnline) PcColors.OnlineDot else PcColors.OfflineDot)
                Text(
                    buildString {
                        append(if (device.isOnline) "Online" else "Offline")
                        if (device.osVersion.isNotBlank()) append(" · ").append(device.osVersion)
                    },
                    color = if (device.isOnline) PcColors.OnlineInk else PcColors.InkSoft,
                    style = PcType.Caption,
                    maxLines = 1,
                )
            }
        }

        IconAction(PcIcons.Edit, "Rename or remove this PC", onEdit)
    }
}

@Composable
private fun ControlsSection(device: Device, onCommand: (String) -> Unit) {
    Column {
        SectionLabel("Controls", Modifier.padding(start = 2.dp, bottom = 10.dp))

        // A two-column grid built from rows: LazyVerticalGrid inside a
        // LazyColumn needs a fixed height, and six tiles do not need lazy
        // layout at all.
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            CommandTypes.ALL.chunked(2).forEach { pair ->
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    pair.forEach { type ->
                        CommandTile(
                            type = type,
                            enabled = device.isOnline &&
                                (device.allowedCommands.isEmpty() || type in device.allowedCommands),
                            onClick = { onCommand(type) },
                            modifier = Modifier.weight(1f),
                        )
                    }
                    if (pair.size == 1) Spacer(Modifier.weight(1f))
                }
            }
        }

        Spacer(Modifier.height(10.dp))

        Row(
            Modifier.padding(horizontal = 2.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            PcIcon(PcIcons.Key, null, size = 15.dp, tint = PcColors.InkFaint)
            Caption("Asks for your password before it's sent")
        }
    }
}

@Composable
private fun CommandTile(
    type: String,
    enabled: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val destructive = type in CommandTypes.DESTRUCTIVE

    PcCard(modifier.alpha(if (enabled) 1f else 0.45f), onClick = if (enabled) onClick else null) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                PcIcon(
                    PcIcons.forCommand(type),
                    null,
                    size = 24.dp,
                    tint = if (destructive) PcColors.Danger else PcColors.Ink,
                )
                // The key says the command will stop and ask, before it is
                // pressed rather than after.
                if (destructive) PcIcon(PcIcons.Key, null, size = 16.dp, tint = PcColors.InkFaint)
            }

            Text(
                CommandTypes.label(type),
                color = if (destructive) PcColors.DangerInk else PcColors.Ink,
                style = PcType.BodyStrong,
            )
        }
    }
}

@Composable
private fun RemindersSection(reminders: List<Reminder>, targetable: Boolean, onAdd: () -> Unit) {
    Column {
        Row(
            Modifier.fillMaxWidth().padding(start = 2.dp, end = 2.dp, bottom = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            SectionLabel(if (targetable) "Reminders on this PC" else "Reminders")

            Row(
                Modifier.clip(PcShapes.Pill),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(4.dp),
            ) {
                PcIcon(PcIcons.Add, null, size = 18.dp, tint = PcColors.Primary)
                TextLink("Add", onAdd, style = PcType.Chip)
            }
        }

        PcCard(Modifier.fillMaxWidth()) {
            if (reminders.isEmpty()) {
                Column(Modifier.padding(16.dp)) {
                    Caption(
                        if (targetable) {
                            "Nothing set for this PC."
                        } else {
                            "Nothing coming up. Reminders show on every PC signed in to this account."
                        },
                    )
                }
            } else {
                reminders.forEachIndexed { index, reminder ->
                    if (index > 0) RowDivider(startIndent = 68.dp)
                    ReminderLine(reminder)
                }
            }
        }

        if (reminders.isNotEmpty() && !targetable) {
            Spacer(Modifier.height(8.dp))
            Caption("Reminders show on every PC signed in to this account.", Modifier.padding(horizontal = 2.dp))
        }
    }
}

@Composable
private fun ReminderLine(reminder: Reminder) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(
            AppViewModel.formatTime(reminder.dueAt),
            Modifier.width(42.dp),
            color = PcColors.InkSoft,
            style = PcType.MonoTime.copy(fontSize = 13.sp),
        )

        Column(Modifier.weight(1f)) {
            Text(reminder.body, color = PcColors.Ink, style = PcType.Body, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Caption(
                listOfNotNull(
                    AppViewModel.formatDay(reminder.dueAt),
                    describeRrule(reminder.rrule),
                ).joinToString(" · "),
            )
        }
    }
}

@Composable
private fun RecentSection(commands: List<Command>) {
    Column {
        SectionLabel("Recent", Modifier.padding(start = 2.dp, bottom = 10.dp))

        PcCard(Modifier.fillMaxWidth()) {
            commands.forEachIndexed { index, command ->
                if (index > 0) RowDivider(startIndent = 68.dp)

                Row(
                    Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 11.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    Text(
                        AppViewModel.formatTime(command.issuedAt),
                        Modifier.width(42.dp),
                        color = PcColors.InkSoft,
                        style = PcType.MonoTime.copy(fontSize = 13.sp),
                    )
                    Text(
                        CommandTypes.label(command.type),
                        Modifier.weight(1f),
                        color = PcColors.Ink,
                        style = PcType.BodySmall.copy(fontSize = 14.5.sp),
                    )
                    OutcomeBadge(outcomeLabel(command), outcomeTone(command))
                }
            }
        }
    }
}

/** What actually happened, not that a row was written (05 §6). */
internal fun outcomeLabel(command: Command): String = when (command.status) {
    "succeeded" -> "Done"
    "expired" -> "Expired · offline"
    "failed" -> command.resultMessage?.takeIf { it.isNotBlank() } ?: "Failed"
    "pending" -> "Sending"
    "delivered" -> "Delivered"
    else -> command.status.replaceFirstChar { it.uppercase() }
}

internal fun outcomeTone(command: Command): Tone = when (command.status) {
    "succeeded" -> Tone.Good
    "expired", "failed", "rejected" -> Tone.Bad
    else -> Tone.Neutral
}

@Composable
private fun DeviceOptionsDialog(
    device: Device,
    onDismiss: () -> Unit,
    onRename: (String) -> Unit,
    onRemove: () -> Unit,
) {
    var name by remember { mutableStateOf(device.displayName) }
    var confirmingRemoval by remember { mutableStateOf(false) }

    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = PcColors.Surface,
        shape = PcShapes.Dialog,
        title = {
            Text(
                if (confirmingRemoval) "Remove ${device.displayName}?" else "This PC",
                color = PcColors.Ink,
                style = PcType.Heading,
            )
        },
        text = {
            if (confirmingRemoval) {
                Text(
                    "It stops accepting commands from this account. To use it again, " +
                        "sign in on that PC and add it back.",
                    color = PcColors.InkSoft,
                    style = PcType.BodySmall,
                )
            } else {
                Column {
                    PcTextField(
                        value = name,
                        onValueChange = { name = it },
                        label = "Name",
                        height = 48.dp,
                    )
                    Spacer(Modifier.height(14.dp))
                    Caption("How this PC appears here and on your other PCs.")
                    Spacer(Modifier.height(16.dp))
                    Box(
                        Modifier
                            .fillMaxWidth()
                            .height(1.dp)
                            .background(PcColors.Divider),
                    )
                    Spacer(Modifier.height(8.dp))
                    TextLink(
                        "Remove this PC",
                        onClick = { confirmingRemoval = true },
                        colour = PcColors.DangerInk,
                        style = PcType.BodySmall,
                    )
                }
            }
        },
        confirmButton = {
            if (confirmingRemoval) {
                TextButton(onClick = onRemove) {
                    Text("Remove", color = PcColors.DangerInk, style = PcType.BodyStrong)
                }
            } else {
                TextButton(
                    onClick = { onRename(name) },
                    enabled = name.isNotBlank() && name != device.displayName,
                ) {
                    Text(
                        "Save",
                        color = if (name.isNotBlank() && name != device.displayName) {
                            PcColors.Primary
                        } else {
                            PcColors.InkDisabled
                        },
                        style = PcType.BodyStrong,
                    )
                }
            }
        },
        dismissButton = {
            TextButton(onClick = if (confirmingRemoval) { { confirmingRemoval = false } } else onDismiss) {
                Text(
                    if (confirmingRemoval) "Keep it" else "Cancel",
                    color = PcColors.InkSoft,
                    style = PcType.BodySmall,
                )
            }
        },
    )
}
