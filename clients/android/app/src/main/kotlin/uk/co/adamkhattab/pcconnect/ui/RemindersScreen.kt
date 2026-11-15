package uk.co.adamkhattab.pcconnect.ui

import android.text.format.DateFormat
import androidx.annotation.DrawableRes
import androidx.compose.ui.platform.LocalContext
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
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
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import kotlinx.coroutines.launch
import uk.co.adamkhattab.pcconnect.data.Device
import uk.co.adamkhattab.pcconnect.data.Reminder
import uk.co.adamkhattab.pcconnect.data.ReminderOccurrenceResponse
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.time.Instant
import java.time.LocalDate
import java.time.Month
import java.time.YearMonth
import java.time.ZoneId
import java.time.format.TextStyle
import java.util.Locale

private enum class RemindersViewTab { List, Calendar }

internal fun Reminder.hasHitEndDate(now: Instant = Instant.now()): Boolean =
    recurrenceUntil?.let {
        runCatching { Instant.parse(it).isBefore(now) }.getOrDefault(false)
    } ?: false

internal fun Reminder.isActiveReminder(now: Instant = Instant.now()): Boolean =
    if (rrule.isNullOrBlank()) !isCompleted else !hasHitEndDate(now)

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
    var inspectingId by remember { mutableStateOf<String?>(null) }
    val inspecting = inspectingId?.let { id -> state.reminders.firstOrNull { it.id == id } }
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
                            onInspect = { inspectingId = it.id },
                            onEdit = onEdit,
                        )
                    } else {
                        RemindersCalendarView(
                            reminders = state.reminders,
                            onToggle = onToggle,
                            onInspect = { inspectingId = it.id },
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
            devices = state.devices,
            remindersTargetable = state.remindersTargetable,
            viewModel = viewModel,
            onDismiss = { inspectingId = null },
            onToggleComplete = if (reminder.rrule.isNullOrBlank()) {
                { onToggle(reminder) }
            } else {
                null
            },
            onEdit = {
                inspectingId = null
                onEdit(reminder)
            },
            onDelete = {
                inspectingId = null
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

private enum class CalendarViewMode {
    Days,
    Months,
    Years,
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
    var viewMode by remember { mutableStateOf(CalendarViewMode.Days) }
    var pickerYear by remember(viewMonth) { mutableIntStateOf(viewMonth.year) }
    var decadeStart by remember(pickerYear) { mutableIntStateOf((pickerYear / 12) * 12) }

    val density = LocalDensity.current
    val swipeThresholdPx = with(density) { 40.dp.toPx() }
    var totalDragX by remember { mutableFloatStateOf(0f) }

    val calendarSwipeModifier = Modifier.pointerInput(viewMode, viewMonth, pickerYear, decadeStart) {
        detectHorizontalDragGestures(
            onDragStart = {
                totalDragX = 0f
            },
            onHorizontalDrag = { change, dragAmount ->
                change.consume()
                totalDragX += dragAmount
            },
            onDragEnd = {
                if (totalDragX < -swipeThresholdPx) {
                    when (viewMode) {
                        CalendarViewMode.Days -> {
                            viewMonth = viewMonth.plusMonths(1)
                            if (selectedDate != null && YearMonth.from(selectedDate) != viewMonth) {
                                selectedDate = null
                            }
                        }
                        CalendarViewMode.Months -> pickerYear++
                        CalendarViewMode.Years -> decadeStart += 12
                    }
                } else if (totalDragX > swipeThresholdPx) {
                    when (viewMode) {
                        CalendarViewMode.Days -> {
                            viewMonth = viewMonth.minusMonths(1)
                            if (selectedDate != null && YearMonth.from(selectedDate) != viewMonth) {
                                selectedDate = null
                            }
                        }
                        CalendarViewMode.Months -> pickerYear--
                        CalendarViewMode.Years -> decadeStart -= 12
                    }
                }
                totalDragX = 0f
            },
            onDragCancel = {
                totalDragX = 0f
            },
        )
    }

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
                                    .clickable {
                                        when (viewMode) {
                                            CalendarViewMode.Days -> {
                                                viewMonth = viewMonth.minusMonths(1)
                                                if (selectedDate != null && YearMonth.from(selectedDate) != viewMonth) {
                                                    selectedDate = null
                                                }
                                            }
                                            CalendarViewMode.Months -> pickerYear--
                                            CalendarViewMode.Years -> decadeStart -= 12
                                        }
                                    },
                                contentAlignment = Alignment.Center,
                            ) {
                                PcIcon(
                                    PcIcons.ChevronRight,
                                    contentDescription = when (viewMode) {
                                        CalendarViewMode.Days -> "Previous month"
                                        CalendarViewMode.Months -> "Previous year"
                                        CalendarViewMode.Years -> "Previous decade"
                                    },
                                    size = 20.dp,
                                    modifier = Modifier.graphicsLayer { rotationZ = 180f },
                                    tint = PcColors.Ink,
                                )
                            }

                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier
                                    .clip(RoundedCornerShape(8.dp))
                                    .clickable {
                                        when (viewMode) {
                                            CalendarViewMode.Days -> {
                                                pickerYear = viewMonth.year
                                                decadeStart = (pickerYear / 12) * 12
                                                viewMode = CalendarViewMode.Months
                                            }
                                            CalendarViewMode.Months -> {
                                                decadeStart = (pickerYear / 12) * 12
                                                viewMode = CalendarViewMode.Years
                                            }
                                            CalendarViewMode.Years -> {
                                                viewMode = CalendarViewMode.Months
                                            }
                                        }
                                    }
                                    .padding(horizontal = 6.dp, vertical = 4.dp),
                            ) {
                                Text(
                                    text = when (viewMode) {
                                        CalendarViewMode.Days -> "${viewMonth.month.getDisplayName(TextStyle.FULL, Locale.getDefault())} ${viewMonth.year}"
                                        CalendarViewMode.Months -> pickerYear.toString()
                                        CalendarViewMode.Years -> "$decadeStart - ${decadeStart + 11}"
                                    },
                                    color = PcColors.Ink,
                                    style = PcType.Heading.copy(fontSize = 17.sp),
                                    maxLines = 1,
                                )
                                Spacer(Modifier.width(4.dp))
                                PcIcon(
                                    id = if (viewMode == CalendarViewMode.Years) PcIcons.ExpandLess else PcIcons.ExpandMore,
                                    contentDescription = "Switch calendar view",
                                    size = 18.dp,
                                    tint = PcColors.Primary,
                                )
                            }

                            Box(
                                Modifier
                                    .size(36.dp)
                                    .clip(CircleShape)
                                    .clickable {
                                        when (viewMode) {
                                            CalendarViewMode.Days -> {
                                                viewMonth = viewMonth.plusMonths(1)
                                                if (selectedDate != null && YearMonth.from(selectedDate) != viewMonth) {
                                                    selectedDate = null
                                                }
                                            }
                                            CalendarViewMode.Months -> pickerYear++
                                            CalendarViewMode.Years -> decadeStart += 12
                                        }
                                    },
                                contentAlignment = Alignment.Center,
                            ) {
                                PcIcon(
                                    PcIcons.ChevronRight,
                                    contentDescription = when (viewMode) {
                                        CalendarViewMode.Days -> "Next month"
                                        CalendarViewMode.Months -> "Next year"
                                        CalendarViewMode.Years -> "Next decade"
                                    },
                                    size = 20.dp,
                                    tint = PcColors.Ink,
                                )
                            }
                        }

                        TextButton(
                            onClick = {
                                val now = LocalDate.now()
                                pickerYear = now.year
                                decadeStart = (now.year / 12) * 12
                                viewMonth = YearMonth.now()
                                selectedDate = now
                                viewMode = CalendarViewMode.Days
                            },
                        ) {
                            Text("Today", color = PcColors.Primary, style = PcType.BodyStrong)
                        }
                    }

                    Spacer(Modifier.height(10.dp))

                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clipToBounds()
                            .then(calendarSwipeModifier),
                    ) {
                        AnimatedContent(
                            targetState = viewMode,
                            transitionSpec = {
                                fadeIn(animationSpec = tween(220)) togetherWith fadeOut(animationSpec = tween(180))
                            },
                            label = "CalendarViewModeTransition",
                        ) { mode ->
                            when (mode) {
                                CalendarViewMode.Days -> {
                                    Column {
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

                                        AnimatedContent(
                                            targetState = viewMonth,
                                            transitionSpec = {
                                                if (targetState.isAfter(initialState)) {
                                                    (slideInHorizontally { width -> width } + fadeIn(animationSpec = tween(220))).togetherWith(
                                                        slideOutHorizontally { width -> -width } + fadeOut(animationSpec = tween(180)),
                                                    )
                                                } else {
                                                    (slideInHorizontally { width -> -width } + fadeIn(animationSpec = tween(220))).togetherWith(
                                                        slideOutHorizontally { width -> width } + fadeOut(animationSpec = tween(180)),
                                                    )
                                                }
                                            },
                                            label = "CalendarDaysTransition",
                                        ) { currentMonth ->
                                            val firstDay = currentMonth.atDay(1)
                                            val daysInMonth = currentMonth.lengthOfMonth()
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
                                                                val date = currentMonth.atDay(dayNum)
                                                                val isCurrent = date == today
                                                                val isPicked = date == selectedDate

                                                                // Check if any reminder occurs on this day
                                                                val hasEvents = reminders.any { r ->
                                                                    val seriesStart = runCatching {
                                                                        Instant.parse(r.dueAt).atZone(ZoneId.systemDefault()).toLocalDate()
                                                                    }.getOrNull() ?: return@any false

                                                                    val untilDate = r.recurrenceUntil?.let {
                                                                        runCatching { Instant.parse(it).atZone(ZoneId.systemDefault()).toLocalDate() }.getOrNull()
                                                                    }
                                                                    if (untilDate != null && date > untilDate) return@any false
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

                                CalendarViewMode.Months -> {
                                    AnimatedContent(
                                        targetState = pickerYear,
                                        transitionSpec = {
                                            if (targetState > initialState) {
                                                (slideInHorizontally { width -> width } + fadeIn(animationSpec = tween(220))).togetherWith(
                                                    slideOutHorizontally { width -> -width } + fadeOut(animationSpec = tween(180)),
                                                )
                                            } else {
                                                (slideInHorizontally { width -> -width } + fadeIn(animationSpec = tween(220))).togetherWith(
                                                    slideOutHorizontally { width -> width } + fadeOut(animationSpec = tween(180)),
                                                )
                                            }
                                        },
                                        label = "CalendarMonthsTransition",
                                    ) { yearForMonths ->
                                        CalendarMonthsGrid(
                                            pickerYear = yearForMonths,
                                            selectedMonth = viewMonth,
                                            onMonthSelected = { selectedM ->
                                                viewMonth = YearMonth.of(yearForMonths, selectedM)
                                                if (selectedDate != null && YearMonth.from(selectedDate) != viewMonth) {
                                                    selectedDate = null
                                                }
                                                viewMode = CalendarViewMode.Days
                                            },
                                            onBackToCalendar = {
                                                viewMode = CalendarViewMode.Days
                                            },
                                        )
                                    }
                                }

                                CalendarViewMode.Years -> {
                                    AnimatedContent(
                                        targetState = decadeStart,
                                        transitionSpec = {
                                            if (targetState > initialState) {
                                                (slideInHorizontally { width -> width } + fadeIn(animationSpec = tween(220))).togetherWith(
                                                    slideOutHorizontally { width -> -width } + fadeOut(animationSpec = tween(180)),
                                                )
                                            } else {
                                                (slideInHorizontally { width -> -width } + fadeIn(animationSpec = tween(220))).togetherWith(
                                                    slideOutHorizontally { width -> width } + fadeOut(animationSpec = tween(180)),
                                                )
                                            }
                                        },
                                        label = "CalendarYearsTransition",
                                    ) { currentDecadeStart ->
                                        CalendarYearsGrid(
                                            decadeStart = currentDecadeStart,
                                            selectedYear = pickerYear,
                                            onYearSelected = { chosenYear ->
                                                pickerYear = chosenYear
                                                viewMode = CalendarViewMode.Months
                                            },
                                            onBackToCalendar = {
                                                viewMode = CalendarViewMode.Days
                                            },
                                        )
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

                    val untilDate = r.recurrenceUntil?.let {
                        runCatching { Instant.parse(it).atZone(ZoneId.systemDefault()).toLocalDate() }.getOrNull()
                    }
                    if (untilDate != null && filterDate > untilDate) return@filter false
                    occursOn(r.rrule, seriesStart, filterDate)
                }
            } else {
                reminders.filter { r ->
                    val seriesStart = runCatching {
                        Instant.parse(r.dueAt).atZone(ZoneId.systemDefault()).toLocalDate()
                    }.getOrNull() ?: return@filter false

                    val untilDate = r.recurrenceUntil?.let {
                        runCatching { Instant.parse(it).atZone(ZoneId.systemDefault()).toLocalDate() }.getOrNull()
                    }
                    val daysInMonth = viewMonth.lengthOfMonth()
                    (1..daysInMonth).any { day ->
                        val date = viewMonth.atDay(day)
                        if (untilDate != null && date > untilDate) false
                        else occursOn(r.rrule, seriesStart, date)
                    }
                }
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
private fun CalendarMonthsGrid(
    pickerYear: Int,
    selectedMonth: YearMonth,
    onMonthSelected: (Month) -> Unit,
    onBackToCalendar: () -> Unit,
) {
    val now = LocalDate.now()
    val allMonths = Month.values()

    Column(
        Modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        for (row in 0 until 4) {
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                for (col in 0 until 3) {
                    val month = allMonths[row * 3 + col]
                    val isPicked = pickerYear == selectedMonth.year && month == selectedMonth.month
                    val isCurrent = pickerYear == now.year && month == now.month

                    Box(
                        Modifier
                            .weight(1f)
                            .height(42.dp)
                            .clip(RoundedCornerShape(8.dp))
                            .then(
                                when {
                                    isPicked -> Modifier.background(PcColors.Primary)
                                    isCurrent -> Modifier
                                        .background(PcColors.PrimaryTint)
                                        .border(1.5.dp, PcColors.Primary, RoundedCornerShape(8.dp))
                                    else -> Modifier
                                        .background(PcColors.Surface)
                                        .border(1.dp, PcColors.Border, RoundedCornerShape(8.dp))
                                },
                            )
                            .clickable { onMonthSelected(month) },
                        contentAlignment = Alignment.Center,
                    ) {
                        Text(
                            month.getDisplayName(TextStyle.SHORT, Locale.getDefault()),
                            color = when {
                                isPicked -> Color.White
                                isCurrent -> PcColors.Primary
                                else -> PcColors.Ink
                            },
                            style = PcType.BodySmall.copy(
                                fontWeight = if (isPicked || isCurrent) FontWeight.SemiBold else FontWeight.Normal,
                            ),
                        )
                    }
                }
            }
        }

        Spacer(Modifier.height(4.dp))

        Box(
            Modifier.fillMaxWidth(),
            contentAlignment = Alignment.Center,
        ) {
            TextButton(onClick = onBackToCalendar) {
                Text(
                    "Back to calendar",
                    color = PcColors.Primary,
                    style = PcType.Caption.copy(fontWeight = FontWeight.SemiBold),
                )
            }
        }
    }
}

@Composable
private fun CalendarYearsGrid(
    decadeStart: Int,
    selectedYear: Int,
    onYearSelected: (Int) -> Unit,
    onBackToCalendar: () -> Unit,
) {
    val currentYear = LocalDate.now().year

    Column(
        Modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        for (row in 0 until 4) {
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                for (col in 0 until 3) {
                    val year = decadeStart + row * 3 + col
                    val isPicked = year == selectedYear
                    val isCurrent = year == currentYear

                    Box(
                        Modifier
                            .weight(1f)
                            .height(42.dp)
                            .clip(RoundedCornerShape(8.dp))
                            .then(
                                when {
                                    isPicked -> Modifier.background(PcColors.Primary)
                                    isCurrent -> Modifier
                                        .background(PcColors.PrimaryTint)
                                        .border(1.5.dp, PcColors.Primary, RoundedCornerShape(8.dp))
                                    else -> Modifier
                                        .background(PcColors.Surface)
                                        .border(1.dp, PcColors.Border, RoundedCornerShape(8.dp))
                                },
                            )
                            .clickable { onYearSelected(year) },
                        contentAlignment = Alignment.Center,
                    ) {
                        Text(
                            year.toString(),
                            color = when {
                                isPicked -> Color.White
                                isCurrent -> PcColors.Primary
                                else -> PcColors.Ink
                            },
                            style = PcType.BodySmall.copy(
                                fontWeight = if (isPicked || isCurrent) FontWeight.SemiBold else FontWeight.Normal,
                            ),
                        )
                    }
                }
            }
        }

        Spacer(Modifier.height(4.dp))

        Box(
            Modifier.fillMaxWidth(),
            contentAlignment = Alignment.Center,
        ) {
            TextButton(onClick = onBackToCalendar) {
                Text(
                    "Back to calendar",
                    color = PcColors.Primary,
                    style = PcType.Caption.copy(fontWeight = FontWeight.SemiBold),
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
    val done = if (isRecurring) reminder.hasHitEndDate() else reminder.isCompleted
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
internal fun ReminderDetailDialog(
    reminder: Reminder,
    devices: List<Device> = emptyList(),
    remindersTargetable: Boolean = false,
    viewModel: AppViewModel? = null,
    onDismiss: () -> Unit,
    onToggleComplete: (() -> Unit)? = null,
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

    LaunchedEffect(reminder.id, reminder.rrule, reminder.dueAt, reminder.recurrenceUntil) {
        if (isRecurring && viewModel != null) {
            isLoadingOccurrences = true
            occurrences = AppViewModel.sortOccurrencesOldestToNewest(viewModel.getOccurrences(reminder))
            isLoadingOccurrences = false
        }
    }

    val statusLabel: String
    val statusTone: Tone
    if (isRecurring) {
        if (reminder.hasHitEndDate()) {
            statusLabel = "Ended"
            statusTone = Tone.Neutral
        } else {
            statusLabel = "Active · Recurring"
            statusTone = Tone.Good
        }
    } else {
        when {
            reminder.isCompleted -> {
                statusLabel = "Completed"
                statusTone = Tone.Good
            }
            AppViewModel.isPast(reminder.dueAt) -> {
                statusLabel = "Overdue"
                statusTone = Tone.Bad
            }
            else -> {
                statusLabel = "Scheduled · One-time"
                statusTone = Tone.Neutral
            }
        }
    }

    val targetSummary = remember(reminder.deviceIds, devices, remindersTargetable) {
        val ids = reminder.deviceIds
        if (ids.isNullOrEmpty()) {
            "All PCs"
        } else {
            val names = ids.mapNotNull { id -> devices.firstOrNull { it.id == id }?.displayName }
            if (names.isNotEmpty()) names.joinToString(", ") else "${ids.size} selected PC(s)"
        }
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = PcColors.Surface,
        shape = PcShapes.Dialog,
        title = {
            val statusDot = when (statusTone) {
                Tone.Good -> PcColors.OnlineDot
                Tone.Bad -> PcColors.Danger
                Tone.Neutral -> PcColors.InkDisabled
            }
            val statusInk = when (statusTone) {
                Tone.Good -> PcColors.OnlineInk
                Tone.Bad -> PcColors.DangerInk
                Tone.Neutral -> PcColors.InkSoft
            }
            val statusBg = when (statusTone) {
                Tone.Good -> PcColors.OnlineBg
                Tone.Bad -> PcColors.DangerBg
                Tone.Neutral -> PcColors.Track
            }
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(reminder.body, color = PcColors.Ink, style = PcType.Heading)
                StatusPill(text = statusLabel, ink = statusInk, background = statusBg, dot = statusDot)
            }
        },
        text = {
            val showTargets = remindersTargetable || !reminder.deviceIds.isNullOrEmpty() || devices.isNotEmpty()
            val completedMoment = reminder.completedAt?.let {
                AppViewModel.formatOccurrenceMoment(it, compact = true)
            }
            val scheduleHeadline = if (isRecurring) {
                describeRecurrence(reminder.rrule, reminder.dueAt, is24Hour) ?: "Recurring"
            } else {
                AppViewModel.formatOccurrenceMoment(reminder.dueAt)
            }
            val scheduleSubline = if (isRecurring) {
                "Starts ${AppViewModel.formatOccurrenceMoment(reminder.dueAt, compact = true)}"
            } else {
                "One-time reminder"
            }
            val scheduleEndLine = if (isRecurring && reminder.recurrenceUntil != null) {
                "Ends ${AppViewModel.formatOccurrenceMoment(reminder.recurrenceUntil, compact = true)}"
            } else {
                null
            }

            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                // Schedule hero card
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(PcShapes.Control)
                        .background(PcColors.PrimaryTint)
                        .border(1.dp, PcColors.Primary.copy(alpha = 0.16f), PcShapes.Control)
                        .padding(horizontal = 12.dp, vertical = 10.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    Box(
                        modifier = Modifier
                            .size(36.dp)
                            .clip(PcShapes.SmallControl)
                            .background(PcColors.Surface)
                            .border(1.dp, PcColors.Primary.copy(alpha = 0.14f), PcShapes.SmallControl),
                        contentAlignment = Alignment.Center,
                    ) {
                        PcIcon(
                            if (isRecurring) PcIcons.EventRepeat else PcIcons.Schedule,
                            null,
                            size = 19.dp,
                            tint = PcColors.Primary,
                        )
                    }

                    Column(
                        modifier = Modifier.weight(1f),
                        verticalArrangement = Arrangement.spacedBy(2.dp),
                    ) {
                        Text(
                            scheduleHeadline,
                            color = PcColors.Ink,
                            style = PcType.BodyStrong.copy(fontSize = 14.5.sp),
                        )
                        Text(
                            scheduleSubline,
                            color = PcColors.InkSoft,
                            style = PcType.Caption.copy(fontSize = 12.sp),
                        )
                        if (scheduleEndLine != null) {
                            Text(
                                scheduleEndLine,
                                color = PcColors.InkSoft,
                                style = PcType.Caption.copy(fontSize = 12.sp),
                            )
                        }
                    }
                }

                // Compact visual metadata tiles (Timezone intentionally omitted)
                if (showTargets && completedMoment != null) {
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(IntrinsicSize.Min),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        DetailInfoTile(
                            icon = PcIcons.Devices,
                            iconTint = PcColors.Primary,
                            iconBg = PcColors.PrimaryTint,
                            label = "Shows on",
                            value = targetSummary,
                            modifier = Modifier.weight(1f).fillMaxHeight(),
                        )
                        DetailInfoTile(
                            icon = PcIcons.EventAvailable,
                            iconTint = PcColors.OnlineInk,
                            iconBg = PcColors.OnlineBg,
                            label = if (isRecurring) "Last done" else "Completed",
                            value = completedMoment,
                            modifier = Modifier.weight(1f).fillMaxHeight(),
                        )
                    }
                } else if (showTargets) {
                    DetailInfoBanner(
                        icon = PcIcons.Devices,
                        iconTint = PcColors.Primary,
                        iconBg = PcColors.PrimaryTint,
                        label = "Shows on",
                        value = targetSummary,
                    )
                } else if (completedMoment != null) {
                    DetailInfoBanner(
                        icon = PcIcons.EventAvailable,
                        iconTint = PcColors.OnlineInk,
                        iconBg = PcColors.OnlineBg,
                        label = if (isRecurring) "Last done" else "Completed",
                        value = completedMoment,
                    )
                }

                if (isRecurring) {
                    Spacer(Modifier.height(4.dp))
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text("OCCURRENCES", color = PcColors.InkSoft, style = PcType.Section)
                        Caption("Oldest → newest")
                    }

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

                                val occDateStr = AppViewModel.formatOccurrenceMoment(occ.occursAt, compact = true)

                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clip(PcShapes.Control)
                                        .background(PcColors.Track)
                                        .padding(start = 10.dp, end = 6.dp, top = 7.dp, bottom = 7.dp),
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
                                                    occurrences = AppViewModel.sortOccurrencesOldestToNewest(
                                                        occurrences.map { if (it.occursAt == occ.occursAt) updated else it },
                                                    )
                                                }
                                            }
                                        },
                                    )

                                    Column(
                                        modifier = Modifier.weight(1f),
                                        verticalArrangement = Arrangement.spacedBy(1.dp),
                                    ) {
                                        Row(
                                            modifier = Modifier.fillMaxWidth(),
                                            verticalAlignment = Alignment.CenterVertically,
                                            horizontalArrangement = Arrangement.spacedBy(6.dp),
                                        ) {
                                            Text(
                                                occDateStr,
                                                color = if (isCompleted || isSkipped) PcColors.InkSoft else PcColors.Ink,
                                                style = PcType.Label.copy(fontSize = 12.sp),
                                                maxLines = 1,
                                                overflow = TextOverflow.Ellipsis,
                                                modifier = Modifier.weight(1f, fill = false),
                                            )
                                            if (isSkipped) {
                                                OccurrenceBadge(
                                                    text = "Stopped",
                                                    ink = PcColors.DangerInk,
                                                    bg = PcColors.DangerBg,
                                                )
                                            } else if (isCompleted) {
                                                OccurrenceBadge(
                                                    text = "Done",
                                                    ink = PcColors.OnlineInk,
                                                    bg = PcColors.OnlineBg,
                                                )
                                            }
                                            if (hasCustom) {
                                                Text(
                                                    "(edited)",
                                                    color = PcColors.InkFaint,
                                                    style = PcType.Caption.copy(fontSize = 10.sp),
                                                    maxLines = 1,
                                                    softWrap = false,
                                                )
                                            }
                                        }

                                        Text(
                                            occ.effectiveBody,
                                            color = if (isCompleted || isSkipped) PcColors.InkFaint else PcColors.Ink,
                                            style = PcType.BodySmall.copy(fontSize = 12.sp),
                                            textDecoration = if (isCompleted || isSkipped) TextDecoration.LineThrough else null,
                                            maxLines = if (hasCustom) 2 else 1,
                                            overflow = TextOverflow.Ellipsis,
                                        )
                                    }

                                    Row(
                                        verticalAlignment = Alignment.CenterVertically,
                                        horizontalArrangement = Arrangement.spacedBy(0.dp),
                                    ) {
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
                                                            occurrences = AppViewModel.sortOccurrencesOldestToNewest(
                                                                occurrences.map {
                                                                    if (it.occursAt == occ.occursAt) it.copy(status = "skipped") else it
                                                                },
                                                            )
                                                            occurrenceUndoMessage = "Occurrence stopped."
                                                            occurrenceUndoAction = {
                                                                val restored = viewModel.revertOccurrence(
                                                                    reminderId = reminder.id,
                                                                    occursAt = occ.occursAt,
                                                                    status = if (previousStatus == "skipped") "pending" else previousStatus,
                                                                )
                                                                occurrences = AppViewModel.sortOccurrencesOldestToNewest(
                                                                    occurrences.map {
                                                                        if (it.occursAt == occ.occursAt) restored else it
                                                                    },
                                                                )
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
                                                            occurrences = AppViewModel.sortOccurrencesOldestToNewest(
                                                                occurrences.map { if (it.occursAt == occ.occursAt) restored else it },
                                                            )
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
            }
        },
        confirmButton = {
            Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                if (!isRecurring && onToggleComplete != null) {
                    TextButton(onClick = onToggleComplete) {
                        Text(
                            if (reminder.isCompleted) "Reopen" else "Done",
                            color = PcColors.OnlineInk,
                            style = PcType.BodyStrong,
                        )
                    }
                }
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
                                    occurrences = AppViewModel.sortOccurrencesOldestToNewest(
                                        occurrences.map { if (it.occursAt == occ.occursAt) reset else it },
                                    )
                                    occurrenceUndoMessage = "Occurrence reset."
                                    occurrenceUndoAction = {
                                        val reverted = viewModel.revertOccurrence(
                                            reminderId = reminder.id,
                                            occursAt = occ.occursAt,
                                            customBody = previousCustomBody,
                                        )
                                        occurrences = AppViewModel.sortOccurrencesOldestToNewest(
                                            occurrences.map { if (it.occursAt == occ.occursAt) reverted else it },
                                        )
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
                                    occurrences = AppViewModel.sortOccurrencesOldestToNewest(
                                        occurrences.map { if (it.occursAt == target.occursAt) updated else it },
                                    )
                                    occurrenceUndoMessage = "Occurrence updated."
                                    occurrenceUndoAction = {
                                        val reverted = viewModel.revertOccurrence(
                                            reminderId = reminder.id,
                                            occursAt = target.occursAt,
                                            customBody = previousCustomBody,
                                            clearCustomBody = previousCustomBody == null,
                                        )
                                        occurrences = AppViewModel.sortOccurrencesOldestToNewest(
                                            occurrences.map { if (it.occursAt == target.occursAt) reverted else it },
                                        )
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

@Composable
private fun DetailInfoTile(
    @DrawableRes icon: Int,
    iconTint: Color,
    iconBg: Color,
    label: String,
    value: String,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier
            .clip(PcShapes.Control)
            .background(PcColors.Surface)
            .border(1.dp, PcColors.Border, PcShapes.Control)
            .padding(horizontal = 11.dp, vertical = 9.dp),
        verticalArrangement = Arrangement.spacedBy(5.dp),
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            Box(
                modifier = Modifier
                    .size(22.dp)
                    .clip(CircleShape)
                    .background(iconBg),
                contentAlignment = Alignment.Center,
            ) {
                PcIcon(icon, null, size = 13.dp, tint = iconTint)
            }
            Text(
                label,
                color = PcColors.InkSoft,
                style = PcType.Caption.copy(fontSize = 11.sp, fontWeight = FontWeight.Medium),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Text(
            value,
            color = PcColors.Ink,
            style = PcType.Label.copy(fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold),
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
private fun DetailInfoBanner(
    @DrawableRes icon: Int,
    iconTint: Color,
    iconBg: Color,
    label: String,
    value: String,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(PcShapes.Control)
            .background(PcColors.Surface)
            .border(1.dp, PcColors.Border, PcShapes.Control)
            .padding(horizontal = 12.dp, vertical = 9.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Box(
            modifier = Modifier
                .size(28.dp)
                .clip(CircleShape)
                .background(iconBg),
            contentAlignment = Alignment.Center,
        ) {
            PcIcon(icon, null, size = 15.dp, tint = iconTint)
        }
        Column(modifier = Modifier.weight(1f)) {
            Text(
                label,
                color = PcColors.InkSoft,
                style = PcType.Caption.copy(fontSize = 11.sp, fontWeight = FontWeight.Medium),
            )
            Text(
                value,
                color = PcColors.Ink,
                style = PcType.Label.copy(fontSize = 13.sp, fontWeight = FontWeight.SemiBold),
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

@Composable
private fun OccurrenceBadge(
    text: String,
    ink: Color,
    bg: Color,
) {
    Text(
        text,
        color = ink,
        style = PcType.Caption.copy(fontSize = 10.sp, fontWeight = FontWeight.SemiBold),
        maxLines = 1,
        softWrap = false,
        modifier = Modifier
            .clip(RoundedCornerShape(4.dp))
            .background(bg)
            .padding(horizontal = 5.dp, vertical = 1.dp),
    )
}
