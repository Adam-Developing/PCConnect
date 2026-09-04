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
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.clickable
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import uk.co.adamkhattab.pcconnect.data.Reminder
import java.time.LocalDate
import java.time.format.TextStyle
import java.util.Locale

/**
 * Reminders, grouped the way a person asks about them: what is left today,
 * what is coming, and what has already been dealt with.
 */
@Composable
fun RemindersScreen(
    state: AppState,
    onAdd: () -> Unit,
    onToggle: (Reminder) -> Unit,
    onDelete: (Reminder) -> Unit,
    modifier: Modifier = Modifier,
) {
    var inspecting by remember { mutableStateOf<Reminder?>(null) }

    val open = state.reminders.filterNot { it.isCompleted }.sortedBy { it.dueAt }
    val today = open.filter { AppViewModel.isToday(it.dueAt) || AppViewModel.isPast(it.dueAt) }
    val later = open - today.toSet()
    val done = state.reminders.filter { it.isCompleted }.sortedByDescending { it.completedAt ?: it.dueAt }.take(5)

    Box(modifier.fillMaxSize()) {
        if (state.reminders.isEmpty()) {
            NothingScheduled(Modifier.align(Alignment.Center))
        } else {
            LazyColumn(
                Modifier.fillMaxSize(),
                contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 6.dp, bottom = 96.dp),
                verticalArrangement = Arrangement.spacedBy(18.dp),
            ) {
                if (today.isNotEmpty()) {
                    item {
                        ReminderGroup(
                            title = "Today",
                            trailing = LocalDate.now().let {
                                "${it.dayOfWeek.getDisplayName(TextStyle.SHORT, Locale.getDefault())} " +
                                    "${it.dayOfMonth} ${it.month.getDisplayName(TextStyle.SHORT, Locale.getDefault())}"
                            },
                            reminders = today,
                            showDay = false,
                            onToggle = onToggle,
                            onInspect = { inspecting = it },
                        )
                    }
                }

                if (later.isNotEmpty()) {
                    item {
                        ReminderGroup(
                            title = "Later",
                            reminders = later,
                            showDay = true,
                            onToggle = onToggle,
                            onInspect = { inspecting = it },
                        )
                    }
                }

                if (done.isNotEmpty()) {
                    item {
                        ReminderGroup(
                            title = "Done",
                            reminders = done,
                            showDay = false,
                            onToggle = onToggle,
                            onInspect = { inspecting = it },
                        )
                    }
                }
            }
        }

        // The design's floating action button: a rounded square, not a circle.
        Box(
            Modifier
                .align(Alignment.BottomEnd)
                .padding(16.dp)
                .shadow(10.dp, PcShapes.Tile, ambientColor = PcColors.Primary, spotColor = PcColors.Primary)
                .size(56.dp)
                .clip(PcShapes.Tile)
                .background(PcColors.Primary)
                .clickable(onClick = onAdd),
            contentAlignment = Alignment.Center,
        ) {
            PcIcon(PcIcons.Add, "New reminder", size = 26.dp, tint = Color.White)
        }
    }

    inspecting?.let { reminder ->
        ReminderDetailDialog(
            reminder = reminder,
            onDismiss = { inspecting = null },
            onDelete = {
                inspecting = null
                onDelete(reminder)
            },
        )
    }
}

@Composable
private fun ReminderGroup(
    title: String,
    reminders: List<Reminder>,
    showDay: Boolean,
    onToggle: (Reminder) -> Unit,
    onInspect: (Reminder) -> Unit,
    trailing: String? = null,
) {
    Column {
        Row(
            Modifier.fillMaxWidth().padding(start = 2.dp, end = 2.dp, bottom = 10.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.Bottom,
        ) {
            SectionLabel(title)
            if (trailing != null) Caption(trailing)
        }

        PcCard(Modifier.fillMaxWidth()) {
            reminders.forEachIndexed { index, reminder ->
                if (index > 0) RowDivider()
                ReminderRow(reminder, showDay, onToggle = { onToggle(reminder) }, onClick = { onInspect(reminder) })
            }
        }
    }
}

@Composable
private fun ReminderRow(
    reminder: Reminder,
    showDay: Boolean,
    onToggle: () -> Unit,
    onClick: () -> Unit,
) {
    val done = reminder.isCompleted

    Row(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        PcCheck(checked = done, round = true, onClick = onToggle)

        Column(Modifier.weight(1f)) {
            Text(
                reminder.body,
                color = if (done) PcColors.InkFaint else PcColors.Ink,
                style = PcType.Body,
                textDecoration = if (done) TextDecoration.LineThrough else null,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )

            val detail = listOfNotNull(
                describeRrule(reminder.rrule),
                if (done) reminder.completedAt?.let { "Done ${AppViewModel.formatLogTime(it)}" } else null,
            ).joinToString(" · ")

            if (detail.isNotEmpty()) {
                Spacer(Modifier.height(1.dp))
                Caption(detail)
            }
        }

        Column(horizontalAlignment = Alignment.End) {
            Text(
                AppViewModel.formatTime(reminder.dueAt),
                color = if (done) PcColors.InkFaint else PcColors.Ink,
                style = PcType.MonoTime,
            )
            if (showDay) Caption(AppViewModel.formatDay(reminder.dueAt))
        }
    }
}

@Composable
private fun NothingScheduled(modifier: Modifier = Modifier) {
    Column(
        modifier.padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        PcIcon(PcIcons.EventAvailable, null, size = 32.dp, tint = PcColors.InkDisabled)
        Spacer(Modifier.height(12.dp))
        Text("No reminders yet", color = PcColors.Ink, style = PcType.BodyStrong)
        Spacer(Modifier.height(6.dp))
        Text(
            "A reminder appears full-screen on the PC you are sitting at, at the time you choose.",
            color = PcColors.InkFaint,
            style = PcType.Caption,
            textAlign = TextAlign.Center,
        )
    }
}

@Composable
private fun ReminderDetailDialog(reminder: Reminder, onDismiss: () -> Unit, onDelete: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = PcColors.Surface,
        shape = PcShapes.Dialog,
        title = { Text(reminder.body, color = PcColors.Ink, style = PcType.Heading) },
        text = {
            Column {
                Text(
                    "${AppViewModel.formatDay(reminder.dueAt)} at ${AppViewModel.formatTime(reminder.dueAt)}",
                    color = PcColors.InkSoft,
                    style = PcType.BodySmall,
                )
                describeRrule(reminder.rrule)?.let {
                    Spacer(Modifier.height(4.dp))
                    Caption(it)
                }
                Spacer(Modifier.height(4.dp))
                Caption("Times are shown in ${reminder.timezone}.", color = PcColors.InkFaint)
            }
        },
        confirmButton = {
            TextButton(onClick = onDelete) {
                Text("Delete", color = PcColors.DangerInk, style = PcType.BodyStrong)
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("Close", color = PcColors.InkSoft, style = PcType.BodySmall.copy(fontSize = 14.sp))
            }
        },
    )
}
