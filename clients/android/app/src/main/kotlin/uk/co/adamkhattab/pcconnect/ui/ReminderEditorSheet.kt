package uk.co.adamkhattab.pcconnect.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDefaults
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TimePicker
import androidx.compose.material3.TimePickerDefaults
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.material3.rememberTimePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import uk.co.adamkhattab.pcconnect.data.Device
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZoneOffset

private val WeekdayInitials = listOf("M", "T", "W", "T", "F", "S", "S")

/**
 * The new-reminder sheet.
 *
 * The repeat editor speaks in days and times and shows the schedule back as a
 * sentence, because a rule you cannot read back is a rule you cannot check.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ReminderEditorSheet(
    devices: List<Device>,
    targetable: Boolean,
    initialDeviceId: String?,
    onDismiss: () -> Unit,
    onSave: (String, LocalDate, List<LocalTime>, RepeatSpec, List<String>?) -> Unit,
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)

    var body by remember { mutableStateOf("") }
    var date by remember { mutableStateOf(LocalDate.now()) }
    var times by remember { mutableStateOf(listOf(AppViewModel.defaultReminderTime())) }
    var repeat by remember { mutableStateOf(RepeatSpec()) }
    var allPcs by remember { mutableStateOf(initialDeviceId == null) }
    var chosen by remember { mutableStateOf(setOfNotNull(initialDeviceId)) }

    var picking by remember { mutableStateOf<Picker?>(null) }

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
        containerColor = PcColors.Surface,
        shape = PcShapes.Sheet,
        dragHandle = null,
    ) {
        Column(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(start = 20.dp, end = 20.dp, top = 10.dp)) {
                Box(
                    Modifier
                        .align(Alignment.CenterHorizontally)
                        .size(width = 36.dp, height = 4.dp)
                        .clip(PcShapes.Pill)
                        .background(PcColors.Border),
                )

                Spacer(Modifier.height(14.dp))

                Row(
                    Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text("New reminder", color = PcColors.Ink, style = PcType.Heading)
                    IconAction(PcIcons.Close, "Close", onDismiss)
                }
            }

            Column(
                Modifier
                    .weight(1f, fill = false)
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = 20.dp, vertical = 16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                PcTextField(
                    value = body,
                    onValueChange = { body = it },
                    placeholder = "What should it say?",
                    singleLine = false,
                    height = 72.dp,
                    textStyle = PcType.Body.copy(fontSize = 16.sp),
                )

                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    PickerField(
                        value = AppViewModel.describeDay(date),
                        icon = PcIcons.CalendarToday,
                        onClick = { picking = Picker.Date },
                        label = if (repeat.kind == RepeatKind.Once) "Date" else "Starts",
                        modifier = Modifier.weight(1f),
                    )
                    PickerField(
                        value = times.min().format(HourMinute),
                        icon = PcIcons.Schedule,
                        onClick = { picking = Picker.PrimaryTime },
                        label = "Time",
                        mono = true,
                        modifier = Modifier.weight(1f),
                    )
                }

                RepeatEditor(
                    repeat = repeat,
                    times = times,
                    onRepeatChange = { repeat = it },
                    onAddTime = { picking = Picker.ExtraTime },
                    onRemoveTime = { removed -> if (times.size > 1) times = times - removed },
                    onPickUntil = { picking = Picker.Until },
                )

                InfoNote(
                    text = scheduleSummary(repeat, date, times.sorted()),
                    icon = PcIcons.EventRepeat,
                    background = PcColors.Track,
                    iconTint = PcColors.InkFaint,
                )

                if (targetable && devices.isNotEmpty()) {
                    ScopePicker(
                        devices = devices,
                        allPcs = allPcs,
                        chosen = chosen,
                        onAllPcs = { allPcs = it },
                        onToggle = { id ->
                            chosen = if (id in chosen) chosen - id else chosen + id
                        },
                    )
                }
            }

            Box(
                Modifier
                    .fillMaxWidth()
                    .background(PcColors.Divider)
                    .height(1.dp),
            )

            Column(Modifier.padding(start = 20.dp, end = 20.dp, top = 12.dp, bottom = 20.dp)) {
                PrimaryButton(
                    text = "Save reminder",
                    onClick = {
                        onSave(
                            body,
                            date,
                            times.sorted(),
                            repeat,
                            if (targetable && !allPcs) chosen.toList() else null,
                        )
                    },
                    enabled = body.isNotBlank() &&
                        (repeat.kind != RepeatKind.Custom || repeat.selectedDays.isNotEmpty()) &&
                        (!targetable || allPcs || chosen.isNotEmpty()),
                )
            }
        }
    }

    when (picking) {
        Picker.Date, Picker.Until -> {
            val forUntil = picking == Picker.Until
            DatePickerSheet(
                initial = if (forUntil) repeat.until ?: date else date,
                onDismiss = { picking = null },
                onPick = {
                    if (forUntil) repeat = repeat.copy(until = it) else date = it
                    picking = null
                },
            )
        }

        Picker.PrimaryTime, Picker.ExtraTime -> {
            val extra = picking == Picker.ExtraTime
            TimePickerSheet(
                initial = if (extra) LocalTime.of(9, 0) else times.min(),
                onDismiss = { picking = null },
                onPick = { picked ->
                    times = if (extra) {
                        (times + picked).distinct().sorted()
                    } else {
                        // The field edits the earliest time; the rest of the set
                        // is left alone.
                        (times - times.min() + picked).distinct().sorted()
                    }
                    picking = null
                },
            )
        }

        null -> Unit
    }
}

private enum class Picker { Date, PrimaryTime, ExtraTime, Until }

@Composable
private fun RepeatEditor(
    repeat: RepeatSpec,
    times: List<LocalTime>,
    onRepeatChange: (RepeatSpec) -> Unit,
    onAddTime: () -> Unit,
    onRemoveTime: (LocalTime) -> Unit,
    onPickUntil: () -> Unit,
) {
    Column {
        FieldLabel("Repeat")
        Spacer(Modifier.height(8.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            RepeatKind.entries.forEach { kind ->
                PcChip(
                    text = kind.label,
                    style = if (repeat.kind == kind) ChipStyle.Selected else ChipStyle.Outline,
                    onClick = { onRepeatChange(repeat.copy(kind = kind)) },
                )
            }
        }

        if (repeat.kind != RepeatKind.Custom) return@Column

        Spacer(Modifier.height(12.dp))

        Column(
            Modifier
                .fillMaxWidth()
                .clip(PcShapes.Control)
                .border(1.dp, PcColors.Border, PcShapes.Control)
                .padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Column {
                FieldLabel("On these days")
                Spacer(Modifier.height(8.dp))

                Row(
                    Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                ) {
                    WeekdayInitials.forEachIndexed { index, initial ->
                        val on = repeat.days.getOrElse(index) { false }

                        Box(
                            Modifier
                                .size(40.dp)
                                .clip(CircleShape)
                                .then(
                                    if (on) {
                                        Modifier.background(PcColors.Primary)
                                    } else {
                                        Modifier.border(1.dp, PcColors.Border, CircleShape)
                                    },
                                )
                                .clickable { onRepeatChange(repeat.toggleDay(index)) },
                            contentAlignment = Alignment.Center,
                        ) {
                            Text(
                                initial,
                                color = if (on) Color.White else PcColors.InkSoft,
                                style = PcType.Chip.copy(
                                    fontWeight = if (on) FontWeight.SemiBold else FontWeight.Medium,
                                ),
                            )
                        }
                    }
                }
            }

            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                IntervalField(
                    interval = repeat.intervalWeeks,
                    onChange = { onRepeatChange(repeat.copy(intervalWeeks = it)) },
                    modifier = Modifier.weight(1f),
                )

                Column(Modifier.weight(1f)) {
                    FieldLabel("Ends")
                    Spacer(Modifier.height(6.dp))

                    Row(
                        Modifier
                            .fillMaxWidth()
                            .height(44.dp)
                            .clip(PcShapes.SmallControl)
                            .border(1.dp, PcColors.Border, PcShapes.SmallControl)
                            .clickable {
                                if (repeat.until == null) onPickUntil() else onRepeatChange(repeat.copy(until = null))
                            }
                            .padding(horizontal = 12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.SpaceBetween,
                    ) {
                        Text(
                            repeat.until?.let { AppViewModel.describeDay(it) } ?: "Never",
                            color = PcColors.Ink,
                            style = PcType.BodySmall.copy(fontSize = 14.5.sp),
                        )
                        PcIcon(
                            if (repeat.until == null) PcIcons.ExpandMore else PcIcons.Close,
                            null,
                            size = 20.dp,
                            tint = PcColors.InkFaint,
                        )
                    }
                }
            }

            Column {
                FieldLabel("At these times")
                Spacer(Modifier.height(8.dp))

                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    times.sorted().forEach { time ->
                        Row(
                            Modifier
                                .height(34.dp)
                                .clip(PcShapes.Pill)
                                .background(PcColors.Track)
                                .padding(start = 12.dp, end = 8.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(6.dp),
                        ) {
                            Text(time.format(HourMinute), color = PcColors.Ink, style = PcType.MonoTime)

                            if (times.size > 1) {
                                Box(Modifier.clip(CircleShape).clickable { onRemoveTime(time) }) {
                                    PcIcon(PcIcons.Close, "Remove ${time.format(HourMinute)}", size = 16.dp, tint = PcColors.InkFaint)
                                }
                            }
                        }
                    }

                    Row(
                        Modifier
                            .height(34.dp)
                            .clip(PcShapes.Pill)
                            .border(1.dp, PcColors.InkDisabled, PcShapes.Pill)
                            .clickable(onClick = onAddTime)
                            .padding(start = 8.dp, end = 12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(4.dp),
                    ) {
                        PcIcon(PcIcons.Add, null, size = 18.dp, tint = PcColors.Primary)
                        Text("Add time", color = PcColors.Primary, style = PcType.Chip)
                    }
                }

                if (times.size > 1) {
                    Spacer(Modifier.height(8.dp))
                    Caption("Each time is saved as its own reminder, so they can be ticked off separately.")
                }
            }
        }
    }
}

@Composable
private fun IntervalField(interval: Int, onChange: (Int) -> Unit, modifier: Modifier = Modifier) {
    var open by remember { mutableStateOf(false) }

    Column(modifier) {
        FieldLabel("Repeat every")
        Spacer(Modifier.height(6.dp))

        Box {
            Row(
                Modifier
                    .fillMaxWidth()
                    .height(44.dp)
                    .clip(PcShapes.SmallControl)
                    .border(1.dp, PcColors.Border, PcShapes.SmallControl)
                    .clickable { open = true }
                    .padding(horizontal = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Text(
                    if (interval == 1) "1 week" else "$interval weeks",
                    color = PcColors.Ink,
                    style = PcType.BodySmall.copy(fontSize = 14.5.sp),
                )
                PcIcon(PcIcons.ExpandMore, null, size = 20.dp, tint = PcColors.InkFaint)
            }

            DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                (1..4).forEach { weeks ->
                    DropdownMenuItem(
                        text = {
                            Text(
                                if (weeks == 1) "1 week" else "$weeks weeks",
                                color = PcColors.Ink,
                                style = PcType.BodySmall,
                            )
                        },
                        onClick = {
                            onChange(weeks)
                            open = false
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun ScopePicker(
    devices: List<Device>,
    allPcs: Boolean,
    chosen: Set<String>,
    onAllPcs: (Boolean) -> Unit,
    onToggle: (String) -> Unit,
) {
    Column {
        FieldLabel("Show on")
        Spacer(Modifier.height(8.dp))

        SegmentedPair(
            options = listOf("All PCs", "Choose PCs"),
            selectedIndex = if (allPcs) 0 else 1,
            onSelect = { onAllPcs(it == 0) },
        )

        Spacer(Modifier.height(10.dp))

        if (allPcs) {
            Caption(
                "Appears on every PC signed in to this account — including ones you add later.",
                Modifier.padding(horizontal = 2.dp),
            )
        } else {
            Column(
                Modifier
                    .fillMaxWidth()
                    .clip(PcShapes.Control)
                    .border(1.dp, PcColors.Border, PcShapes.Control),
            ) {
                devices.forEachIndexed { index, device ->
                    if (index > 0) RowDivider()

                    Row(
                        Modifier
                            .fillMaxWidth()
                            .clickable { onToggle(device.id) }
                            .padding(horizontal = 14.dp, vertical = 11.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                    ) {
                        PcCheck(checked = device.id in chosen)
                        Text(device.displayName, Modifier.weight(1f), color = PcColors.Ink, style = PcType.Body)
                        Caption(if (device.isOnline) "online" else "offline")
                    }
                }
            }
        }
    }
}

// ── the platform pickers ─────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun DatePickerSheet(initial: LocalDate, onDismiss: () -> Unit, onPick: (LocalDate) -> Unit) {
    val state = rememberDatePickerState(
        initialSelectedDateMillis = initial.atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli(),
    )

    DatePickerDialog(
        onDismissRequest = onDismiss,
        colors = DatePickerDefaults.colors(containerColor = PcColors.Surface),
        confirmButton = {
            TextButton(
                onClick = {
                    // The picker works in UTC midnights, so the date is read
                    // back the same way rather than through the local zone,
                    // which would shift it a day west of Greenwich.
                    state.selectedDateMillis?.let {
                        onPick(Instant.ofEpochMilli(it).atZone(ZoneId.of("UTC")).toLocalDate())
                    }
                },
            ) {
                Text("Done", color = PcColors.Primary, style = PcType.BodyStrong)
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("Cancel", color = PcColors.InkSoft, style = PcType.BodySmall)
            }
        },
    ) {
        DatePicker(state = state, colors = DatePickerDefaults.colors(containerColor = PcColors.Surface))
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TimePickerSheet(initial: LocalTime, onDismiss: () -> Unit, onPick: (LocalTime) -> Unit) {
    val state = rememberTimePickerState(initial.hour, initial.minute, is24Hour = true)

    DatePickerDialog(
        onDismissRequest = onDismiss,
        colors = DatePickerDefaults.colors(containerColor = PcColors.Surface),
        confirmButton = {
            TextButton(onClick = { onPick(LocalTime.of(state.hour, state.minute)) }) {
                Text("Done", color = PcColors.Primary, style = PcType.BodyStrong)
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("Cancel", color = PcColors.InkSoft, style = PcType.BodySmall)
            }
        },
    ) {
        Box(
            Modifier
                .fillMaxWidth()
                .heightIn(min = 240.dp)
                .padding(horizontal = 24.dp, vertical = 12.dp),
            contentAlignment = Alignment.Center,
        ) {
            TimePicker(
                state = state,
                colors = TimePickerDefaults.colors(
                    selectorColor = PcColors.Primary,
                    containerColor = PcColors.Track,
                    periodSelectorSelectedContainerColor = PcColors.PrimaryTint,
                    timeSelectorSelectedContainerColor = PcColors.PrimaryTint,
                    timeSelectorSelectedContentColor = PcColors.Primary,
                ),
            )
        }
    }
}
