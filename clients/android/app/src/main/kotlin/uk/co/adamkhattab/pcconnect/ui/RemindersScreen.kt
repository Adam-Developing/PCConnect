package uk.co.adamkhattab.pcconnect.ui

import android.text.format.DateFormat
import androidx.compose.ui.platform.LocalContext
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.IconButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import kotlinx.coroutines.launch
import uk.co.adamkhattab.pcconnect.data.ReminderOccurrenceResponse
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import uk.co.adamkhattab.pcconnect.data.Reminder
import java.time.Instant
import java.time.LocalDate
import java.time.YearMonth
import java.time.ZoneId
import java.time.format.TextStyle
import java.util.Locale

private enum class RemindersViewTab { List, Calendar }

private fun Reminder.hasHitEndDate(): Boolean =
    recurrenceUntil?.let {
        runCatching { Instant.parse(it).isBefore(Instant.now()) }.getOrDefault(false)
    } ?: false

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemindersScreen(
    state: AppState,
    viewModel: AppViewModel? = null,
    onRefresh: () -> Unit,
    onAdd: () -> Unit,
    onEdit: (Reminder) -> Unit,
    onToggle: (Reminder) -> Unit,
    onDelete: (Reminder) -> Unit,
    modifier: Modifier = Modifier,
) {
    var inspecting by remember { mutableStateOf<Reminder?>(null) }
    var viewTab by remember { mutableStateOf(RemindersViewTab.List) }

    PullToRefreshBox(
        isRefreshing = state.isLoading,
        onRefresh = onRefresh,
        modifier = modifier.fillMaxSize(),
    ) {
        if (state.isLoading && state.reminders.isEmpty()) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = PcColors.Primary)
            }
        } else {
            Box(Modifier.fillMaxSize()) {
                Column(Modifier.fillMaxSize()) {
                    // View mode switcher: List vs Calendar
                    Box(Modifier.padding(horizontal = 16.dp, vertical = 6.dp)) {
                        SegmentedPair(
                            options = listOf("List", "Calendar"),
                            selectedIndex = if (viewTab == RemindersViewTab.List) 0 else 1,
                            onSelect = { viewTab = if (it == 0) RemindersViewTab.List else RemindersViewTab.Calendar },
                        )
                    }

                    if (state.reminders.isEmpty()) {
                        NothingScheduled(Modifier.fillMaxSize())
                    } else if (viewTab == RemindersViewTab.List) {
                        RemindersListView(
                            reminders = state.reminders,
                            onToggle = onToggle,
                            onInspect = { inspecting = it },
                            onEdit = onEdit,
                        )
                    } else {
                        RemindersCalendarView(
                            reminders = state.reminders,
                            onToggle = onToggle,
                            onInspect = { inspecting = it },
                            onEdit = onEdit,
                        )
                    }
                }

                // FAB for new reminder
                val fabInteraction = remember { MutableInteractionSource() }
                val fabPressed by fabInteraction.collectIsPressedAsState()
                val fabScale by animateFloatAsState(
                    targetValue = if (fabPressed) 0.94f else 1.0f,
                    animationSpec = spring(dampingRatio = 0.55f, stiffness = 600f),
                    label = "fabScale",
                )

                Box(
                    Modifier
                        .align(Alignment.BottomEnd)
                        .padding(16.dp)
                        .graphicsLayer {
                            scaleX = fabScale
                            scaleY = fabScale
                        }
                        .shadow(10.dp, PcShapes.Tile, ambientColor = PcColors.Primary, spotColor = PcColors.Primary)
                        .size(56.dp)
                        .clip(PcShapes.Tile)
                        .background(PcColors.Primary)
                        .clickable(interactionSource = fabInteraction, indication = null, onClick = onAdd),
                    contentAlignment = Alignment.Center,
                ) {
                    PcIcon(PcIcons.Add, "New reminder", size = 26.dp, tint = Color.White)
                }
            }
        }
    }

    inspecting?.let { reminder ->
        ReminderDetailDialog(
            reminder = reminder,
            viewModel = viewModel,
            onDismiss = { inspecting = null },
            onEdit = {
                inspecting = null
                onEdit(reminder)
            },
            onDelete = {
                inspecting = null
                onDelete(reminder)
            },
        )
    }
}

@Composable
private fun RemindersListView(
    reminders: List<Reminder>,
    onToggle: (Reminder) -> Unit,
    onInspect: (Reminder) -> Unit,
    onEdit: (Reminder) -> Unit,
) {
    // 1. One-time reminders due today
    val today = reminders.filter { it.rrule.isNullOrBlank() && !it.isCompleted && AppViewModel.isToday(it.dueAt) }

    // 2. Overdue one-time reminders (from past days, not today)
    val overdue = reminders.filter { it.rrule.isNullOrBlank() && !it.isCompleted && AppViewModel.isPast(it.dueAt) && !AppViewModel.isToday(it.dueAt) }

    // 3. Upcoming one-time reminders
    val upcoming = reminders.filter { it.rrule.isNullOrBlank() && !it.isCompleted && !AppViewModel.isPast(it.dueAt) && !AppViewModel.isToday(it.dueAt) }

    // 4. Recurring reminders (active, have not hit end date)
    val recurring = reminders.filter { !it.rrule.isNullOrBlank() && !it.hasHitEndDate() }

    // 5. Done: completed one-time reminders PLUS recurring that hit end date
    val done = reminders.filter {
        (it.rrule.isNullOrBlank() && it.isCompleted) || (!it.rrule.isNullOrBlank() && it.hasHitEndDate())
    }.sortedByDescending { it.completedAt ?: it.dueAt }

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
                    isRecurringGroup = false,
                    onToggle = onToggle,
                    onInspect = onInspect,
                    onEdit = onEdit,
                )
            }
        }

        if (overdue.isNotEmpty()) {
            item {
                ReminderGroup(
                    title = "Overdue",
                    reminders = overdue,
                    showDay = true,
                    isRecurringGroup = false,
                    onToggle = onToggle,
                    onInspect = onInspect,
                    onEdit = onEdit,
                )
            }
        }

        if (upcoming.isNotEmpty()) {
            item {
                ReminderGroup(
                    title = "Upcoming",
                    reminders = upcoming,
                    showDay = true,
                    isRecurringGroup = false,
                    onToggle = onToggle,
                    onInspect = onInspect,
                    onEdit = onEdit,
                )
            }
        }

        if (recurring.isNotEmpty()) {
            item {
                ReminderGroup(
                    title = "Recurring",
                    trailing = "${recurring.size} active",
                    reminders = recurring,
                    showDay = false,
                    isRecurringGroup = true,
                    onToggle = onToggle,
                    onInspect = onInspect,
                    onEdit = onEdit,
                )
            }
        }

        if (done.isNotEmpty()) {
            item {
                ReminderGroup(
                    title = "Done",
                    reminders = done,
                    showDay = true,
                    isRecurringGroup = false,
                    onToggle = onToggle,
                    onInspect = onInspect,
                    onEdit = onEdit,
                )
            }
        }
    }
}

@Composable
private fun RemindersCalendarView(
    reminders: List<Reminder>,
    onToggle: (Reminder) -> Unit,
    onInspect: (Reminder) -> Unit,
    onEdit: (Reminder) -> Unit,
) {
    var viewMonth by remember { mutableStateOf(YearMonth.now()) }
    var selectedDate by remember { mutableStateOf<LocalDate?>(LocalDate.now()) }

    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 6.dp, bottom = 96.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        item {
            PcCard(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(14.dp)) {
                    // Month navigation header
                    Row(
                        Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Box(
                                Modifier
                                    .size(36.dp)
                                    .clip(CircleShape)
                                    .clickable { viewMonth = viewMonth.minusMonths(1) },
                                contentAlignment = Alignment.Center,
                            ) {
                                PcIcon(
                                    PcIcons.ChevronRight,
                                    "Previous month",
                                    size = 20.dp,
                                    modifier = Modifier.graphicsLayer { rotationZ = 180f },
                                    tint = PcColors.Ink,
                                )
                            }

                            Text(
                                "${viewMonth.month.getDisplayName(TextStyle.FULL, Locale.getDefault())} ${viewMonth.year}",
                                color = PcColors.Ink,
                                style = PcType.Heading.copy(fontSize = 17.sp),
                                modifier = Modifier.padding(horizontal = 8.dp),
                            )

                            Box(
                                Modifier
                                    .size(36.dp)
                                    .clip(CircleShape)
                                    .clickable { viewMonth = viewMonth.plusMonths(1) },
                                contentAlignment = Alignment.Center,
                            ) {
                                PcIcon(
                                    PcIcons.ChevronRight,
                                    "Next month",
                                    size = 20.dp,
                                    tint = PcColors.Ink,
                                )
                            }
                        }

                        TextButton(
                            onClick = {
                                viewMonth = YearMonth.now()
                                selectedDate = LocalDate.now()
                            },
                        ) {
                            Text("Today", color = PcColors.Primary, style = PcType.BodyStrong)
                        }
                    }

                    Spacer(Modifier.height(10.dp))

                    // Weekday headers
                    Row(
                        Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                    ) {
                        listOf("M", "T", "W", "T", "F", "S", "S").forEach { initial ->
                            Box(
                                Modifier.weight(1f),
                                contentAlignment = Alignment.Center,
                            ) {
                                Text(
                                    initial,
                                    color = PcColors.InkFaint,
                                    style = PcType.Caption.copy(fontWeight = FontWeight.SemiBold),
                                )
                            }
                        }
                    }

                    Spacer(Modifier.height(8.dp))

                    // Month days grid
                    val firstDay = viewMonth.atDay(1)
                    val daysInMonth = viewMonth.lengthOfMonth()
                    val startOffset = (firstDay.dayOfWeek.value + 6) % 7
                    val totalSlots = ((startOffset + daysInMonth + 6) / 7) * 7

                    val today = LocalDate.now()

                    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        for (week in 0 until totalSlots / 7) {
                            Row(
                                Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                            ) {
                                for (dayCol in 0..6) {
                                    val slot = week * 7 + dayCol
                                    val dayNum = slot - startOffset + 1

                                    if (dayNum in 1..daysInMonth) {
                                        val date = viewMonth.atDay(dayNum)
                                        val isCurrent = date == today
                                        val isPicked = date == selectedDate

                                        // Check if any reminder occurs on this day
                                        val hasEvents = reminders.any { r ->
                                            val seriesStart = runCatching {
                                                Instant.parse(r.dueAt).atZone(ZoneId.systemDefault()).toLocalDate()
                                            }.getOrNull() ?: return@any false

                                            if (r.hasHitEndDate()) return@any false
                                            occursOn(r.rrule, seriesStart, date)
                                        }

                                        Box(
                                            Modifier
                                                .weight(1f)
                                                .aspectRatio(1f)
                                                .clip(CircleShape)
                                                .then(
                                                    when {
                                                        isPicked -> Modifier.background(PcColors.Primary)
                                                        isCurrent -> Modifier.border(1.5.dp, PcColors.Primary, CircleShape)
                                                        else -> Modifier
                                                    },
                                                )
                                                .clickable {
                                                    selectedDate = if (selectedDate == date) null else date
                                                },
                                            contentAlignment = Alignment.Center,
                                        ) {
                                            Column(
                                                horizontalAlignment = Alignment.CenterHorizontally,
                                                verticalArrangement = Arrangement.Center,
                                            ) {
                                                Text(
                                                    dayNum.toString(),
                                                    color = when {
                                                        isPicked -> Color.White
                                                        isCurrent -> PcColors.Primary
                                                        else -> PcColors.Ink
                                                    },
                                                    style = PcType.BodySmall.copy(
                                                        fontWeight = if (isPicked || isCurrent) FontWeight.SemiBold else FontWeight.Normal,
                                                    ),
                                                )

                                                if (hasEvents) {
                                                    Box(
                                                        Modifier
                                                            .padding(top = 2.dp)
                                                            .size(4.dp)
                                                            .clip(CircleShape)
                                                            .background(if (isPicked) Color.White else PcColors.Primary),
                                                    )
                                                }
                                            }
                                        }
                                    } else {
                                        Box(Modifier.weight(1f).aspectRatio(1f))
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Reminders for selected day (or whole month)
        item {
            val filterDate = selectedDate
            val filteredReminders = if (filterDate != null) {
                reminders.filter { r ->
                    val seriesStart = runCatching {
                        Instant.parse(r.dueAt).atZone(ZoneId.systemDefault()).toLocalDate()
                    }.getOrNull() ?: return@filter false

                    if (r.hasHitEndDate()) return@filter false
                    occursOn(r.rrule, seriesStart, filterDate)
                }
            } else {
                reminders
            }

            val title = if (filterDate != null) {
                "Reminders on ${AppViewModel.describeDay(filterDate)}"
            } else {
                "All reminders in ${viewMonth.month.getDisplayName(TextStyle.FULL, Locale.getDefault())}"
            }

            if (filteredReminders.isEmpty()) {
                PcCard(Modifier.fillMaxWidth()) {
                    Column(
                        Modifier.padding(24.dp).fillMaxWidth(),
                        horizontalAlignment = Alignment.CenterHorizontally,
                    ) {
                        Text("No reminders scheduled", color = PcColors.InkSoft, style = PcType.BodySmall)
                    }
                }
            } else {
                ReminderGroup(
                    title = title,
                    reminders = filteredReminders,
                    showDay = filterDate == null,
                    isRecurringGroup = false,
                    onToggle = onToggle,
                    onInspect = onInspect,
                    onEdit = onEdit,
                )
            }
        }
    }
}

@Composable
private fun ReminderGroup(
    title: String,
    reminders: List<Reminder>,
    showDay: Boolean,
    isRecurringGroup: Boolean = false,
    onToggle: (Reminder) -> Unit,
    onInspect: (Reminder) -> Unit,
    onEdit: (Reminder) -> Unit,
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

        PcCard(Modifier.fillMaxWidth().animateContentSize(tween(200))) {
            reminders.forEachIndexed { index, reminder ->
                if (index > 0) RowDivider()
                ReminderRow(
                    reminder = reminder,
                    showDay = showDay,
                    isRecurring = isRecurringGroup || !reminder.rrule.isNullOrBlank(),
                    onToggle = { onToggle(reminder) },
                    onClick = { onInspect(reminder) },
                    onEdit = { onEdit(reminder) },
                )
            }
        }
    }
}

@Composable
private fun ReminderRow(
    reminder: Reminder,
    showDay: Boolean,
    isRecurring: Boolean = false,
    onToggle: () -> Unit,
    onClick: () -> Unit,
    onEdit: () -> Unit,
) {
    val done = reminder.isCompleted
    val is24Hour = DateFormat.is24HourFormat(LocalContext.current)

    Row(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        if (isRecurring && !reminder.hasHitEndDate()) {
            Box(
                Modifier
                    .size(24.dp)
                    .clip(CircleShape)
                    .background(PcColors.Track)
                    .clickable(onClick = onClick),
                contentAlignment = Alignment.Center,
            ) {
                PcIcon(
                    PcIcons.EventRepeat,
                    "Recurring reminder",
                    size = 14.dp,
                    tint = PcColors.Primary,
                )
            }
        } else {
            PcCheck(checked = done, round = true, onClick = onToggle)
        }

        Column(Modifier.weight(1f)) {
            Text(
                reminder.body,
                color = if (done) PcColors.InkFaint else PcColors.Ink,
                style = PcType.Body,
                textDecoration = if (done) TextDecoration.LineThrough else null,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )

            val details = mutableListOf<String>()

            if (isRecurring) {
                // Recurrence info (e.g. "Every Thursday at 2 pm", "Every 8 October at 3 pm")
                describeRecurrence(reminder.rrule, reminder.dueAt, is24Hour)?.let { details.add(it) }

                // Last ran info for recurring reminders (never show start date)
                if (reminder.completedAt != null) {
                    details.add("Last ran: ${AppViewModel.formatInstant(reminder.completedAt)}")
                }
            } else if (done && reminder.completedAt != null) {
                details.add("Done ${AppViewModel.formatLogTime(reminder.completedAt)}")
            }

            if (details.isNotEmpty()) {
                Spacer(Modifier.height(2.dp))
                Caption(details.joinToString(" · "))
            }
        }

        Column(horizontalAlignment = Alignment.End) {
            Text(
                AppViewModel.formatTime(reminder.dueAt),
                color = if (done) PcColors.InkFaint else PcColors.Ink,
                style = PcType.MonoTime,
            )
            if (showDay && !isRecurring) Caption(AppViewModel.formatDay(reminder.dueAt))
        }

        Box(
            Modifier
                .size(32.dp)
                .clip(CircleShape)
                .clickable(onClick = onEdit),
            contentAlignment = Alignment.Center,
        ) {
            PcIcon(PcIcons.Edit, "Edit reminder", size = 18.dp, tint = PcColors.InkFaint)
        }
    }
}

@Composable
private fun NothingScheduled(modifier: Modifier = Modifier) {
    Column(
        modifier.padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        PcIcon(PcIcons.EventAvailable, null, size = 36.dp, tint = PcColors.InkDisabled)
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
private fun ReminderDetailDialog(
    reminder: Reminder,
    viewModel: AppViewModel? = null,
    onDismiss: () -> Unit,
    onEdit: () -> Unit,
    onDelete: () -> Unit,
) {
    val is24Hour = DateFormat.is24HourFormat(LocalContext.current)
    val isRecurring = !reminder.rrule.isNullOrBlank()
    val scope = rememberCoroutineScope()

    var occurrences by remember { mutableStateOf<List<ReminderOccurrenceResponse>>(emptyList()) }
    var isLoadingOccurrences by remember { mutableStateOf(isRecurring) }
    var editingOccurrence by remember { mutableStateOf<ReminderOccurrenceResponse?>(null) }
    var editText by remember { mutableStateOf("") }
    var occurrenceUndoMessage by remember { mutableStateOf<String?>(null) }
    var occurrenceUndoAction by remember { mutableStateOf<(suspend () -> Unit)?>(null) }

    LaunchedEffect(reminder.id) {
        if (isRecurring && viewModel != null) {
            isLoadingOccurrences = true
            occurrences = viewModel.getOccurrences(reminder)
            isLoadingOccurrences = false
        }
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = PcColors.Surface,
        shape = PcShapes.Dialog,
        title = { Text(reminder.body, color = PcColors.Ink, style = PcType.Heading) },
        text = {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                if (isRecurring) {
                    describeRecurrence(reminder.rrule, reminder.dueAt, is24Hour)?.let {
                        Text(it, color = PcColors.InkSoft, style = PcType.BodySmall)
                    }
                } else {
                    Text(
                        "${AppViewModel.formatDay(reminder.dueAt)} at ${AppViewModel.formatTime(reminder.dueAt)}",
                        color = PcColors.InkSoft,
                        style = PcType.BodySmall,
                    )
                }
                if (reminder.completedAt != null) {
                    Caption("Last completed: ${AppViewModel.formatInstant(reminder.completedAt)}")
                }
                if (reminder.recurrenceUntil != null) {
                    Caption("Ends: ${AppViewModel.formatInstant(reminder.recurrenceUntil)}")
                }
                Caption("Times are shown in ${reminder.timezone}.", color = PcColors.InkFaint)

                if (isRecurring) {
                    Spacer(Modifier.height(6.dp))
                    Text("OCCURRENCES", color = PcColors.InkSoft, style = PcType.Section)

                    val undoMsg = occurrenceUndoMessage
                    val undoAct = occurrenceUndoAction
                    if (undoMsg != null && undoAct != null) {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(PcShapes.Control)
                                .background(PcColors.OnlineBg)
                                .padding(horizontal = 10.dp, vertical = 4.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.SpaceBetween,
                        ) {
                            Text(
                                undoMsg,
                                color = PcColors.OnlineInk,
                                style = PcType.Label,
                                modifier = Modifier.weight(1f),
                            )
                            TextButton(
                                onClick = {
                                    occurrenceUndoMessage = null
                                    occurrenceUndoAction = null
                                    scope.launch { undoAct() }
                                },
                            ) {
                                Text("Undo", color = PcColors.OnlineInk, style = PcType.BodyStrong.copy(fontSize = 13.sp))
                            }
                        }
                    }

                    if (isLoadingOccurrences) {
                        Box(
                            Modifier.fillMaxWidth().padding(vertical = 12.dp),
                            contentAlignment = Alignment.Center,
                        ) {
                            CircularProgressIndicator(modifier = Modifier.size(24.dp), color = PcColors.Primary)
                        }
                    } else if (occurrences.isEmpty()) {
                        Text("No occurrences scheduled", color = PcColors.InkFaint, style = PcType.Caption)
                    } else {
                        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                            occurrences.forEach { occ ->
                                val isCompleted = occ.status == "completed"
                                val isSkipped = occ.status == "skipped"
                                val hasCustom = !occ.customBody.isNullOrBlank()

                                val occDateStr = runCatching {
                                    val instant = Instant.parse(occ.occursAt)
                                    val zdt = instant.atZone(ZoneId.systemDefault())
                                    val dayDesc = AppViewModel.describeDay(zdt.toLocalDate())
                                    val timeDesc = AppViewModel.formatTime(occ.occursAt)
                                    "$dayDesc at $timeDesc"
                                }.getOrDefault(occ.occursAt)

                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clip(PcShapes.Control)
                                        .background(PcColors.Track)
                                        .padding(horizontal = 10.dp, vertical = 8.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                                ) {
                                    // Complete toggle
                                    PcCheck(
                                        checked = isCompleted,
                                        round = true,
                                        onClick = {
                                            if (viewModel != null) {
                                                scope.launch {
                                                    val updated = viewModel.toggleOccurrence(reminder.id, occ.occursAt, occ.status)
                                                    occurrences = occurrences.map { if (it.occursAt == occ.occursAt) updated else it }
                                                }
                                            }
                                        },
                                    )

                                    Column(Modifier.weight(1f)) {
                                        Row(
                                            verticalAlignment = Alignment.CenterVertically,
                                            horizontalArrangement = Arrangement.spacedBy(4.dp),
                                        ) {
                                            Text(
                                                occDateStr,
                                                color = PcColors.Ink,
                                                style = PcType.Label.copy(fontSize = 12.sp),
                                            )
                                            if (isSkipped) {
                                                Text(
                                                    "Stopped",
                                                    color = PcColors.DangerInk,
                                                    style = PcType.Caption.copy(fontSize = 10.sp, fontWeight = FontWeight.Bold),
                                                    modifier = Modifier
                                                        .clip(RoundedCornerShape(3.dp))
                                                        .background(PcColors.DangerBg)
                                                        .padding(horizontal = 4.dp, vertical = 1.dp),
                                                )
                                            }
                                            if (isCompleted) {
                                                Text(
                                                    "Completed",
                                                    color = PcColors.OnlineInk,
                                                    style = PcType.Caption.copy(fontSize = 10.sp, fontWeight = FontWeight.Bold),
                                                    modifier = Modifier
                                                        .clip(RoundedCornerShape(3.dp))
                                                        .background(PcColors.OnlineBg)
                                                        .padding(horizontal = 4.dp, vertical = 1.dp),
                                                )
                                            }
                                            if (hasCustom) {
                                                Text(
                                                    "(edited)",
                                                    color = PcColors.InkFaint,
                                                    style = PcType.Caption.copy(fontSize = 10.sp),
                                                )
                                            }
                                        }

                                        Text(
                                            occ.effectiveBody,
                                            color = if (isCompleted) PcColors.InkFaint else PcColors.Ink,
                                            style = PcType.BodySmall.copy(fontSize = 12.sp),
                                            textDecoration = if (isCompleted || isSkipped) TextDecoration.LineThrough else null,
                                            maxLines = 2,
                                            overflow = TextOverflow.Ellipsis,
                                        )
                                    }

                                    // Edit text button
                                    IconButton(
                                        onClick = {
                                            editingOccurrence = occ
                                            editText = occ.effectiveBody
                                        },
                                        modifier = Modifier.size(28.dp),
                                    ) {
                                        PcIcon(PcIcons.Edit, "Edit text", size = 15.dp, tint = PcColors.InkSoft)
                                    }

                                    // Stop / Skip or Restore button
                                    if (!isSkipped) {
                                        IconButton(
                                            onClick = {
                                                if (viewModel != null) {
                                                    val previousStatus = occ.status
                                                    scope.launch {
                                                        viewModel.stopOccurrence(reminder.id, occ.occursAt)
                                                        occurrences = occurrences.map {
                                                            if (it.occursAt == occ.occursAt) it.copy(status = "skipped") else it
                                                        }
                                                        occurrenceUndoMessage = "Occurrence stopped."
                                                        occurrenceUndoAction = {
                                                            val restored = viewModel.revertOccurrence(
                                                                reminderId = reminder.id,
                                                                occursAt = occ.occursAt,
                                                                status = if (previousStatus == "skipped") "pending" else previousStatus,
                                                            )
                                                            occurrences = occurrences.map {
                                                                if (it.occursAt == occ.occursAt) restored else it
                                                            }
                                                        }
                                                    }
                                                }
                                            },
                                            modifier = Modifier.size(28.dp),
                                        ) {
                                            PcIcon(PcIcons.Close, "Stop occurrence", size = 15.dp, tint = PcColors.DangerInk)
                                        }
                                    } else {
                                        IconButton(
                                            onClick = {
                                                if (viewModel != null) {
                                                    scope.launch {
                                                        val restored = viewModel.restoreOccurrence(reminder.id, occ.occursAt)
                                                        occurrences = occurrences.map { if (it.occursAt == occ.occursAt) restored else it }
                                                        occurrenceUndoMessage = null
                                                        occurrenceUndoAction = null
                                                    }
                                                }
                                            },
                                            modifier = Modifier.size(28.dp),
                                        ) {
                                            PcIcon(PcIcons.RestartAlt, "Restore occurrence", size = 15.dp, tint = PcColors.OnlineInk)
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                TextButton(onClick = onEdit) {
                    Text("Edit", color = PcColors.Primary, style = PcType.BodyStrong)
                }
                TextButton(onClick = onDelete) {
                    Text("Delete", color = PcColors.DangerInk, style = PcType.BodyStrong)
                }
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("Close", color = PcColors.InkSoft, style = PcType.BodySmall.copy(fontSize = 14.sp))
            }
        },
    )

    editingOccurrence?.let { occ ->
        AlertDialog(
            onDismissRequest = { editingOccurrence = null },
            containerColor = PcColors.Surface,
            shape = PcShapes.Dialog,
            title = { Text("Edit Occurrence Text", color = PcColors.Ink, style = PcType.Heading) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("Change reminder text for this single occurrence:", color = PcColors.InkSoft, style = PcType.BodySmall)
                    OutlinedTextField(
                        value = editText,
                        onValueChange = { editText = it },
                        modifier = Modifier.fillMaxWidth(),
                        shape = PcShapes.Control,
                        singleLine = false,
                        maxLines = 3,
                        colors = OutlinedTextFieldDefaults.colors(
                            focusedBorderColor = PcColors.Primary,
                            unfocusedBorderColor = PcColors.Border,
                        ),
                    )
                }
            },
            confirmButton = {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    if (!occ.customBody.isNullOrBlank() && viewModel != null) {
                        TextButton(
                            onClick = {
                                val previousCustomBody = occ.customBody
                                scope.launch {
                                    val reset = viewModel.resetOccurrenceText(reminder.id, occ.occursAt)
                                    occurrences = occurrences.map { if (it.occursAt == occ.occursAt) reset else it }
                                    occurrenceUndoMessage = "Occurrence reset."
                                    occurrenceUndoAction = {
                                        val reverted = viewModel.revertOccurrence(
                                            reminderId = reminder.id,
                                            occursAt = occ.occursAt,
                                            customBody = previousCustomBody,
                                        )
                                        occurrences = occurrences.map { if (it.occursAt == occ.occursAt) reverted else it }
                                    }
                                }
                                editingOccurrence = null
                            },
                        ) {
                            Text("Reset", color = PcColors.InkSoft, style = PcType.BodySmall)
                        }
                    }
                    TextButton(
                        onClick = {
                            val target = editingOccurrence ?: return@TextButton
                            val trimmed = editText.trim()
                            if (trimmed.isNotBlank() && trimmed != target.effectiveBody && viewModel != null) {
                                val previousCustomBody = target.customBody
                                scope.launch {
                                    val updated = viewModel.modifyOccurrenceText(reminder.id, target.occursAt, trimmed)
                                    occurrences = occurrences.map { if (it.occursAt == target.occursAt) updated else it }
                                    occurrenceUndoMessage = "Occurrence updated."
                                    occurrenceUndoAction = {
                                        val reverted = viewModel.revertOccurrence(
                                            reminderId = reminder.id,
                                            occursAt = target.occursAt,
                                            customBody = previousCustomBody,
                                            clearCustomBody = previousCustomBody == null,
                                        )
                                        occurrences = occurrences.map { if (it.occursAt == target.occursAt) reverted else it }
                                    }
                                }
                            }
                            editingOccurrence = null
                        },
                    ) {
                        Text("Save", color = PcColors.Primary, style = PcType.BodyStrong)
                    }
                }
            },
            dismissButton = {
                TextButton(onClick = { editingOccurrence = null }) {
                    Text("Cancel", color = PcColors.InkSoft, style = PcType.BodySmall)
                }
            },
        )
    }
}
