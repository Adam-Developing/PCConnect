package com.adamkhattab.pcconnect.v2

import android.app.DatePickerDialog
import android.app.TimePickerDialog
import android.text.format.DateFormat
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
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
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.adamkhattab.pcconnect.v2.data.ReminderEntity
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

@Composable
internal fun ReminderListScreen(viewModel: MainViewModel, onReminderClick: (String) -> Unit) {
    val reminders by viewModel.reminders.collectAsStateWithLifecycle()
    val dateFormatter = remember { DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM) }
    val timeFormatter = remember { DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT) }

    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        item { Text("Reminders", style = MaterialTheme.typography.headlineMedium) }
        if (reminders.isEmpty()) {
            item { Text("No reminders scheduled yet.", modifier = Modifier.padding(vertical = 12.dp)) }
        }
        items(reminders, key = { it.id }) { reminder ->
            Column(
                Modifier
                    .fillMaxWidth()
                    .clickable { onReminderClick(reminder.id) }
                    .padding(vertical = 12.dp),
                verticalArrangement = Arrangement.spacedBy(4.dp),
            ) {
                Text(reminder.text, style = MaterialTheme.typography.titleMedium)
                Text("Scheduled ${formatReminderDateTime(reminder.nextOccurrenceAt, reminder.localStart, dateFormatter, timeFormatter)}")
                reminder.acknowledgementLabel(dateFormatter, timeFormatter)?.let { Text(it) }
            }
            HorizontalDivider()
        }
    }
}

@Composable
internal fun ReminderDetailScreen(
    viewModel: MainViewModel,
    reminderId: String,
    onBack: () -> Unit,
    onEdit: () -> Unit,
) {
    val reminders by viewModel.reminders.collectAsStateWithLifecycle()
    val reminder = reminders.firstOrNull { it.id == reminderId }
    val dateFormatter = remember { DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM) }
    val timeFormatter = remember { DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT) }

    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        ReminderScreenHeader(title = "Reminder details", onBack = onBack, onEdit = onEdit)
        if (reminder == null) {
            Text("This reminder is no longer available.")
            return@Column
        }
        Text(reminder.text, style = MaterialTheme.typography.headlineSmall)
        DetailRow(
            "Date and time",
            formatReminderDateTime(reminder.nextOccurrenceAt, reminder.localStart, dateFormatter, timeFormatter),
        )
        DetailRow("Time zone", reminder.timezone)
        DetailRow(
            "Targets",
            if (reminder.targetMode == "all_devices") {
                "All enrolled desktops"
            } else {
                "${reminder.targetDeviceIds.split(',').count(String::isNotBlank)} selected desktop(s)"
            },
        )
        DetailRow("Repeats", reminder.recurrenceRule ?: "Does not repeat")
        DetailRow("Status", reminder.acknowledgementLabel(dateFormatter, timeFormatter) ?: "Scheduled")
    }
}

@Composable
internal fun ReminderEditorScreen(
    viewModel: MainViewModel,
    reminderId: String?,
    onBack: () -> Unit,
    onSaved: () -> Unit,
) {
    val reminders by viewModel.reminders.collectAsStateWithLifecycle()
    val busy by viewModel.busy.collectAsStateWithLifecycle()
    val existing = reminders.firstOrNull { it.id == reminderId }
    var text by rememberSaveable(reminderId) { mutableStateOf("") }
    var selectedDateValue by rememberSaveable(reminderId) { mutableStateOf<String?>(null) }
    var selectedTimeValue by rememberSaveable(reminderId) { mutableStateOf<String?>(null) }
    var initializedReminderId by rememberSaveable(reminderId) { mutableStateOf<String?>(null) }
    val context = LocalContext.current
    val focusManager = LocalFocusManager.current
    val suggestedStartValue = rememberSaveable(reminderId) {
        LocalDateTime.now().plusHours(1).withSecond(0).withNano(0).toString()
    }
    val suggestedStart = LocalDateTime.parse(suggestedStartValue)

    LaunchedEffect(existing?.id) {
        if (existing != null && initializedReminderId == null) {
            val start = LocalDateTime.parse(existing.localStart)
            text = existing.text
            selectedDateValue = start.toLocalDate().toString()
            selectedTimeValue = start.toLocalTime().toString()
            initializedReminderId = existing.id
        }
    }

    val selectedDate = selectedDateValue?.let(LocalDate::parse)
    val selectedTime = selectedTimeValue?.let(LocalTime::parse)
    val selectedStart = if (selectedDate != null && selectedTime != null) LocalDateTime.of(selectedDate, selectedTime) else null
    val unchangedExistingStart = existing?.localStart?.let(LocalDateTime::parse) == selectedStart
    val startValid = selectedStart?.isAfter(LocalDateTime.now()) == true || unchangedExistingStart
    val startError = selectedStart != null && !startValid
    val ready = reminderId == null || existing != null
    val dateFormatter = remember { DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM) }
    val timeFormatter = remember { DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT) }

    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .imePadding()
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        ReminderScreenHeader(title = if (reminderId == null) "Add reminder" else "Edit reminder", onBack = onBack)
        if (!ready) {
            CircularProgressIndicator(Modifier.align(Alignment.CenterHorizontally))
            return@Column
        }
        OutlinedTextField(
            value = text,
            onValueChange = { text = it.take(500) },
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
        if (startError) Text("Choose a future date and time.", color = MaterialTheme.colorScheme.error)
        Button(
            onClick = {
                viewModel.saveReminder(existing, text, checkNotNull(selectedStart).toString(), onSaved)
            },
            enabled = text.isNotBlank() && startValid && !busy,
            modifier = Modifier.align(Alignment.CenterHorizontally),
        ) { Text(if (existing == null) "Save reminder" else "Save changes") }
    }
}

@Composable
private fun ReminderScreenHeader(title: String, onBack: () -> Unit, onEdit: (() -> Unit)? = null) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = onBack) {
            Icon(painterResource(R.drawable.ic_back), contentDescription = "Back")
        }
        Text(title, style = MaterialTheme.typography.headlineMedium, modifier = Modifier.weight(1f))
        onEdit?.let {
            IconButton(onClick = it) {
                Icon(painterResource(R.drawable.ic_edit), contentDescription = "Edit reminder")
            }
        }
    }
}

@Composable
private fun DetailRow(label: String, value: String) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(label, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
        Text(value, style = MaterialTheme.typography.bodyLarge)
    }
}

private fun ReminderEntity.acknowledgementLabel(
    dateFormatter: DateTimeFormatter,
    timeFormatter: DateTimeFormatter,
): String? = lastAcknowledgementStatus?.let { status ->
    buildString {
        append(status.replaceFirstChar(Char::uppercase))
        append(" by ")
        append(lastAcknowledgedBy ?: "desktop")
        lastAcknowledgedAt?.let { value ->
            formatInstantDateTime(value, dateFormatter, timeFormatter)?.let { formatted ->
                append(" · ")
                append(formatted)
            }
        }
    }
}

private fun formatReminderDateTime(
    nextOccurrenceAt: String?,
    localStart: String,
    dateFormatter: DateTimeFormatter,
    timeFormatter: DateTimeFormatter,
): String {
    val dateTime = nextOccurrenceAt?.let {
        runCatching { Instant.parse(it).atZone(ZoneId.systemDefault()).toLocalDateTime() }.getOrNull()
    } ?: LocalDateTime.parse(localStart)
    return "${dateTime.toLocalDate().format(dateFormatter)} at ${dateTime.toLocalTime().format(timeFormatter)}"
}

private fun formatInstantDateTime(
    value: String,
    dateFormatter: DateTimeFormatter,
    timeFormatter: DateTimeFormatter,
): String? = runCatching {
    val dateTime = Instant.parse(value).atZone(ZoneId.systemDefault()).toLocalDateTime()
    "${dateTime.toLocalDate().format(dateFormatter)} at ${dateTime.toLocalTime().format(timeFormatter)}"
}.getOrNull()
