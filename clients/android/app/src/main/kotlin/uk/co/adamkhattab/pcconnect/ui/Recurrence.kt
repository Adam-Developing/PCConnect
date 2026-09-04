package uk.co.adamkhattab.pcconnect.ui

import java.time.DayOfWeek
import java.time.LocalDate
import java.time.LocalTime
import java.time.format.TextStyle
import java.util.Locale

/** The four repeat options the design offers, in the order it shows them. */
enum class RepeatKind(val label: String) {
    Once("Once"),
    Weekly("Every week"),
    Monthly("Every month"),
    Custom("Custom"),
}

/**
 * A repeat, as the sheet collects it.
 *
 * Plain words on the screen, RFC 5545 on the wire. A recurrence editor that
 * asks a person to write `FREQ=WEEKLY;BYDAY=MO` is a recurrence editor nobody
 * uses.
 */
data class RepeatSpec(
    val kind: RepeatKind = RepeatKind.Once,
    /** Monday first, to match the design's M T W T F S S row. */
    val days: List<Boolean> = List(7) { false },
    val intervalWeeks: Int = 1,
    val until: LocalDate? = null,
) {
    val selectedDays: List<DayOfWeek>
        get() = DayOfWeek.entries.filterIndexed { index, _ -> days.getOrElse(index) { false } }

    fun toggleDay(index: Int): RepeatSpec =
        copy(days = days.mapIndexed { i, on -> if (i == index) !on else on })
}

/** `BYDAY` codes, in the order RFC 5545 lists them. */
private val DayCodes = mapOf(
    DayOfWeek.MONDAY to "MO",
    DayOfWeek.TUESDAY to "TU",
    DayOfWeek.WEDNESDAY to "WE",
    DayOfWeek.THURSDAY to "TH",
    DayOfWeek.FRIDAY to "FR",
    DayOfWeek.SATURDAY to "SA",
    DayOfWeek.SUNDAY to "SU",
)

/**
 * The rule for one series, or null when the reminder happens once.
 *
 * A custom repeat carries only the days and the interval. The *times* are not
 * in the rule: `BYHOUR` and `BYMINUTE` multiply out, so 10:30 and 15:45 would
 * expand to four occurrences a day rather than two. Each time is its own
 * series instead, which is exactly what it means and what the server can
 * already expand.
 */
fun rruleFor(spec: RepeatSpec, startDate: LocalDate): String? {
    val rule = when (spec.kind) {
        RepeatKind.Once -> return null
        RepeatKind.Weekly -> "FREQ=WEEKLY"
        RepeatKind.Monthly -> "FREQ=MONTHLY"
        RepeatKind.Custom -> buildString {
            append("FREQ=WEEKLY")
            if (spec.intervalWeeks > 1) append(";INTERVAL=${spec.intervalWeeks}")

            // No day ticked means "the day it starts on", which is what a weekly
            // rule does anyway — but saying it explicitly keeps the rule stable
            // if the start date is later moved.
            val days = spec.selectedDays.ifEmpty { listOf(startDate.dayOfWeek) }
            append(";BYDAY=").append(days.joinToString(",") { DayCodes.getValue(it) })
        }
    }

    return rule
}

/** Reads a rule back into the words the app showed when it was written. */
fun describeRrule(rrule: String?): String? {
    if (rrule.isNullOrBlank()) return null

    val parts = rrule.removePrefix("RRULE:").split(';').mapNotNull {
        val (key, value) = it.split('=', limit = 2).let { p -> p.first() to p.getOrNull(1).orEmpty() }
        if (value.isBlank()) null else key.uppercase() to value
    }.toMap()

    val byDay = parts["BYDAY"]?.split(',').orEmpty()
    val interval = parts["INTERVAL"]?.toIntOrNull() ?: 1

    return when (parts["FREQ"]?.uppercase()) {
        "DAILY" -> "Every day"
        "MONTHLY" -> "Every month"
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

/**
 * The sentence under the repeat editor.
 *
 * It is there so nobody has to work out what a set of chips adds up to: the
 * design shows the schedule back in words before the reminder is saved.
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

    return when (spec.kind) {
        RepeatKind.Once ->
            "Once — ${if (start == "on $longDate") longDate else "$start, $longDate"}, at $timeList."

        RepeatKind.Weekly ->
            "Every $weekday at $timeList, starting $start.$ends"

        RepeatKind.Monthly ->
            "On the ${ordinal(date.dayOfMonth)} of every month at $timeList, starting $start.$ends"

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

/** "Monday, Tuesday and Thursday" — an Oxford-comma-free list, as the design writes it. */
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
