package uk.co.adamkhattab.pcconnect.ui

import java.time.DayOfWeek
import java.time.LocalDate
import java.time.LocalTime
import java.time.format.TextStyle
import java.time.temporal.ChronoUnit
import java.util.Locale

/** Repeat options matching desktop companion. */
enum class RepeatKind(val label: String) {
    Once("Once"),
    Daily("Daily"),
    Weekly("Weekly"),
    Monthly("Monthly"),
    Yearly("Yearly"),
    Hourly("Hourly"),
    Custom("Custom"),
}

/**
 * A repeat specification, as the sheet collects it.
 */
data class RepeatSpec(
    val kind: RepeatKind = RepeatKind.Once,
    /** Monday first, to match the M T W T F S S row. */
    val days: List<Boolean> = List(7) { false },
    val intervalWeeks: Int = 1,
    val until: LocalDate? = null,
) {
    val selectedDays: List<DayOfWeek>
        get() = DayOfWeek.entries.filterIndexed { index, _ -> days.getOrElse(index) { false } }

    fun toggleDay(index: Int): RepeatSpec =
        copy(days = days.mapIndexed { i, on -> if (i == index) !on else on })
}

/** `BYDAY` codes in RFC 5545 format. */
val DayCodes = mapOf(
    DayOfWeek.MONDAY to "MO",
    DayOfWeek.TUESDAY to "TU",
    DayOfWeek.WEDNESDAY to "WE",
    DayOfWeek.THURSDAY to "TH",
    DayOfWeek.FRIDAY to "FR",
    DayOfWeek.SATURDAY to "SA",
    DayOfWeek.SUNDAY to "SU",
)

/**
 * The RFC 5545 rule string for one series, or null when the reminder happens once.
 */
fun rruleFor(spec: RepeatSpec, startDate: LocalDate): String? {
    val intervalStr = if (spec.intervalWeeks > 1) ";INTERVAL=${spec.intervalWeeks}" else ""
    return when (spec.kind) {
        RepeatKind.Once -> null
        RepeatKind.Hourly -> "FREQ=HOURLY$intervalStr"
        RepeatKind.Daily -> "FREQ=DAILY$intervalStr"
        RepeatKind.Monthly -> "FREQ=MONTHLY$intervalStr"
        RepeatKind.Yearly -> "FREQ=YEARLY$intervalStr"
        RepeatKind.Weekly -> if (spec.intervalWeeks > 1) "FREQ=WEEKLY$intervalStr" else "FREQ=WEEKLY"
        RepeatKind.Custom -> buildString {
            append("FREQ=WEEKLY")
            append(intervalStr)
            val days = spec.selectedDays.ifEmpty { listOf(startDate.dayOfWeek) }
            append(";BYDAY=").append(days.joinToString(",") { DayCodes.getValue(it) })
        }
    }
}

/** Converts an RFC 5545 rule back to a RepeatSpec for editing. */
fun fromRrule(rrule: String?, recurrenceUntil: String? = null): RepeatSpec {
    if (rrule.isNullOrBlank()) return RepeatSpec(kind = RepeatKind.Once)

    val parts = rrule.removePrefix("RRULE:")
        .split(';')
        .mapNotNull {
            val p = it.split('=', limit = 2)
            if (p.size == 2) p[0].uppercase() to p[1] else null
        }.toMap()

    val freq = parts["FREQ"]?.uppercase()
    val byDay = parts["BYDAY"]?.split(',')?.filter { it.isNotBlank() } ?: emptyList()
    val interval = parts["INTERVAL"]?.toIntOrNull()?.coerceAtLeast(1) ?: 1
    val untilDate = recurrenceUntil?.let {
        runCatching { java.time.Instant.parse(it).atZone(java.time.ZoneId.systemDefault()).toLocalDate() }.getOrNull()
    }

    val daysList = DayOfWeek.entries.map { day ->
        val code = DayCodes[day]
        byDay.contains(code)
    }

    return when (freq) {
        "HOURLY" -> RepeatSpec(kind = RepeatKind.Hourly, intervalWeeks = interval, until = untilDate)
        "DAILY" -> RepeatSpec(kind = RepeatKind.Daily, intervalWeeks = interval, until = untilDate)
        "MONTHLY" -> RepeatSpec(kind = RepeatKind.Monthly, intervalWeeks = interval, until = untilDate)
        "YEARLY" -> RepeatSpec(kind = RepeatKind.Yearly, intervalWeeks = interval, until = untilDate)
        "WEEKLY" -> {
            RepeatSpec(
                kind = RepeatKind.Weekly,
                days = daysList,
                intervalWeeks = interval,
                until = untilDate,
            )
        }
        else -> RepeatSpec(kind = RepeatKind.Once, until = untilDate)
    }
}

/** Checks whether a reminder occurs on a given day. */
fun occursOn(rrule: String?, seriesStart: LocalDate, day: LocalDate): Boolean {
    if (day.isBefore(seriesStart)) return false
    if (rrule.isNullOrBlank()) return day == seriesStart

    val parts = rrule.removePrefix("RRULE:")
        .split(';')
        .mapNotNull {
            val p = it.split('=', limit = 2)
            if (p.size == 2) p[0].uppercase() to p[1] else null
        }.toMap()

    val interval = parts["INTERVAL"]?.toIntOrNull()?.coerceAtLeast(1) ?: 1
    val freq = parts["FREQ"]?.uppercase()

    return when (freq) {
        "HOURLY" -> true
        "DAILY" -> {
            val daysBetween = ChronoUnit.DAYS.between(seriesStart, day)
            daysBetween % interval == 0L
        }
        "MONTHLY" -> {
            val monthsBetween = (day.year - seriesStart.year) * 12 + (day.monthValue - seriesStart.monthValue)
            monthsBetween >= 0 && monthsBetween % interval == 0 && day.dayOfMonth == seriesStart.dayOfMonth
        }
        "YEARLY" -> {
            val yearsBetween = day.year - seriesStart.year
            yearsBetween >= 0 && yearsBetween % interval == 0 && day.monthValue == seriesStart.monthValue && day.dayOfMonth == seriesStart.dayOfMonth
        }
        "WEEKLY" -> {
            val startMonday = seriesStart.minusDays(((seriesStart.dayOfWeek.value + 6) % 7).toLong())
            val dayMonday = day.minusDays(((day.dayOfWeek.value + 6) % 7).toLong())
            val weeksBetween = ChronoUnit.WEEKS.between(startMonday, dayMonday)
            if (weeksBetween % interval != 0L) return false

            val byDay = parts["BYDAY"]?.split(',')?.filter { it.isNotBlank() } ?: emptyList()
            if (byDay.isEmpty()) {
                day.dayOfWeek == seriesStart.dayOfWeek
            } else {
                byDay.any { DayCodes[day.dayOfWeek] == it }
            }
        }
        else -> day == seriesStart
    }
}

/** Reads a rule back into natural words. */
fun describeRrule(rrule: String?): String? {
    if (rrule.isNullOrBlank()) return null

    val parts = rrule.removePrefix("RRULE:").split(';').mapNotNull {
        val (key, value) = it.split('=', limit = 2).let { p -> p.first() to p.getOrNull(1).orEmpty() }
        if (value.isBlank()) null else key.uppercase() to value
    }.toMap()

    val byDay = parts["BYDAY"]?.split(',')?.filter { it.isNotBlank() }.orEmpty()
    val interval = parts["INTERVAL"]?.toIntOrNull()?.coerceAtLeast(1) ?: 1

    return when (parts["FREQ"]?.uppercase()) {
        "HOURLY" -> if (interval == 1) "Every hour" else "Every $interval hours"
        "DAILY" -> if (interval == 1) "Every day" else "Every $interval days"
        "MONTHLY" -> if (interval == 1) "Every month" else "Every $interval months"
        "YEARLY" -> if (interval == 1) "Every year" else "Every $interval years"
        "WEEKLY" -> when {
            byDay.toSet() == setOf("MO", "TU", "WE", "TH", "FR") -> "Every weekday"
            byDay.isEmpty() && interval == 1 -> "Every week"
            interval > 1 && byDay.isEmpty() -> "Every $interval weeks"
            else -> {
                val names = byDay.mapNotNull { code -> DayCodes.entries.firstOrNull { it.value == code }?.key }
                    .map { it.getDisplayName(TextStyle.SHORT, Locale.getDefault()) }
                val every = if (interval > 1) "Every $interval weeks on " else "Every "
                every + joinNaturally(names)
            }
        }
        else -> rrule
    }
}

fun formatNaturalTime(time: LocalTime, is24Hour: Boolean = false): String {
    if (is24Hour) {
        return time.format(HourMinute)
    }
    val hour12 = if (time.hour % 12 == 0) 12 else time.hour % 12
    val amPm = if (time.hour < 12) "am" else "pm"
    return if (time.minute == 0) {
        "$hour12 $amPm"
    } else {
        "$hour12:${time.minute.toString().padStart(2, '0')} $amPm"
    }
}

/**
 * Describes the recurrence pattern in natural English, including the scheduled
 * time and recurrence days/dates (e.g. "Every Thursday at 2 pm", "Every 8 October at 3 pm").
 */
fun describeRecurrence(
    rrule: String?,
    date: LocalDate,
    time: LocalTime,
    is24Hour: Boolean = false,
): String? {
    if (rrule.isNullOrBlank()) return null

    val parts = rrule.removePrefix("RRULE:").split(';').mapNotNull {
        val (key, value) = it.split('=', limit = 2).let { p -> p.first() to p.getOrNull(1).orEmpty() }
        if (value.isBlank()) null else key.uppercase() to value
    }.toMap()

    val byDay = parts["BYDAY"]?.split(',')?.filter { it.isNotBlank() }.orEmpty()
    val interval = parts["INTERVAL"]?.toIntOrNull()?.coerceAtLeast(1) ?: 1
    val timeStr = formatNaturalTime(time, is24Hour)

    return when (parts["FREQ"]?.uppercase()) {
        "HOURLY" -> if (interval == 1) "Every hour" else "Every $interval hours"
        "DAILY" -> if (interval == 1) "Every day at $timeStr" else "Every $interval days at $timeStr"
        "MONTHLY" -> {
            val ord = ordinal(date.dayOfMonth)
            if (interval == 1) "Every month on the $ord at $timeStr" else "Every $interval months on the $ord at $timeStr"
        }
        "YEARLY" -> {
            val monthName = date.month.getDisplayName(TextStyle.FULL, Locale.getDefault())
            if (interval == 1) "Every ${date.dayOfMonth} $monthName at $timeStr" else "Every $interval years on ${date.dayOfMonth} $monthName at $timeStr"
        }
        "WEEKLY" -> {
            val days = if (byDay.isEmpty()) {
                listOf(date.dayOfWeek)
            } else {
                byDay.mapNotNull { code -> DayCodes.entries.firstOrNull { it.value == code }?.key }
            }

            if (days.toSet() == setOf(DayOfWeek.MONDAY, DayOfWeek.TUESDAY, DayOfWeek.WEDNESDAY, DayOfWeek.THURSDAY, DayOfWeek.FRIDAY)) {
                if (interval == 1) "Every weekday at $timeStr" else "Every $interval weeks on weekdays at $timeStr"
            } else {
                val names = days.map { it.getDisplayName(TextStyle.FULL, Locale.getDefault()) }
                val dayDesc = joinNaturally(names)
                if (interval == 1) "Every $dayDesc at $timeStr" else "Every $interval weeks on $dayDesc at $timeStr"
            }
        }
        else -> describeRrule(rrule)
    }
}

fun describeRecurrence(
    rrule: String?,
    dueAt: String,
    is24Hour: Boolean = false,
    zone: java.time.ZoneId = java.time.ZoneId.systemDefault(),
): String? {
    if (rrule.isNullOrBlank()) return null
    val moment = AppViewModel.zoned(dueAt, zone) ?: return describeRrule(rrule)
    return describeRecurrence(rrule, moment.toLocalDate(), moment.toLocalTime(), is24Hour)
}

/**
 * The schedule sentence under the repeat editor.
 */
fun scheduleSummary(
    spec: RepeatSpec,
    date: LocalDate,
    times: List<LocalTime>,
    today: LocalDate = LocalDate.now(),
): String {
    val timeList = joinNaturally(times.map { it.format(HourMinute) })
    val longDate = "${date.dayOfWeek.getDisplayName(TextStyle.FULL, Locale.getDefault())} " +
        "${date.dayOfMonth} ${date.month.getDisplayName(TextStyle.FULL, Locale.getDefault())}"

    val start = when (date) {
        today -> "today"
        today.plusDays(1) -> "tomorrow"
        else -> "on $longDate"
    }

    val ends = spec.until?.let {
        " Ends on ${it.dayOfMonth} ${it.month.getDisplayName(TextStyle.FULL, Locale.getDefault())}."
    } ?: if (spec.kind == RepeatKind.Once) "" else " Doesn't end."

    val weekday = date.dayOfWeek.getDisplayName(TextStyle.FULL, Locale.getDefault())
    val n = spec.intervalWeeks

    return when (spec.kind) {
        RepeatKind.Once ->
            "Once — ${if (start == "on $longDate") longDate else "$start, $longDate"}, at $timeList."

        RepeatKind.Hourly ->
            (if (n == 1) "Every hour" else "Every $n hours") + " at $timeList, starting $start.$ends"

        RepeatKind.Daily ->
            (if (n == 1) "Every day" else "Every $n days") + " at $timeList, starting $start.$ends"

        RepeatKind.Weekly -> {
            val picked = spec.selectedDays.map { it.getDisplayName(TextStyle.FULL, Locale.getDefault()) }
            val dayDesc = if (picked.isEmpty()) weekday else joinNaturally(picked)
            val every = if (n > 1) "Every $n weeks on $dayDesc" else "Every $dayDesc"
            "$every at $timeList, starting $start.$ends"
        }

        RepeatKind.Monthly -> {
            val every = if (n > 1) "Every $n months on the ${ordinal(date.dayOfMonth)}" else "On the ${ordinal(date.dayOfMonth)} of every month"
            "$every at $timeList, starting $start.$ends"
        }

        RepeatKind.Yearly -> {
            val every = if (n > 1) "Every $n years" else "Every year"
            "$every on ${date.dayOfMonth} ${date.month.getDisplayName(TextStyle.FULL, Locale.getDefault())} at $timeList, starting $start.$ends"
        }

        RepeatKind.Custom -> {
            val picked = spec.selectedDays.map { it.getDisplayName(TextStyle.FULL, Locale.getDefault()) }
            if (picked.isEmpty()) {
                "Pick at least one day."
            } else {
                val every = if (spec.intervalWeeks > 1) {
                    "Every ${spec.intervalWeeks} weeks on ${joinNaturally(picked)}"
                } else {
                    "Every ${joinNaturally(picked)}"
                }
                "$every at $timeList, starting $start.$ends"
            }
        }
    }
}

/** "Monday, Tuesday and Thursday" — list without Oxford comma. */
internal fun joinNaturally(values: List<String>): String = when (values.size) {
    0 -> ""
    1 -> values.first()
    else -> values.dropLast(1).joinToString(", ") + " and " + values.last()
}

internal fun ordinal(day: Int): String {
    val suffix = when {
        day % 100 in 11..13 -> "th"
        day % 10 == 1 -> "st"
        day % 10 == 2 -> "nd"
        day % 10 == 3 -> "rd"
        else -> "th"
    }
    return "$day$suffix"
}

internal val HourMinute: java.time.format.DateTimeFormatter =
    java.time.format.DateTimeFormatter.ofPattern("HH:mm")
