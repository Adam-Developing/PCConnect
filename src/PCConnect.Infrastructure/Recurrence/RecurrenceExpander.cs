using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using Microsoft.Extensions.Logging;

namespace PCConnect.Infrastructure.Recurrence;

/// <summary>
/// Expands an RFC 5545 RRULE into concrete UTC instants.
///
/// The rule is evaluated against a start expressed in the reminder's own IANA
/// timezone and then converted to UTC, which is what keeps a daily 09:00
/// reminder at 09:00 across a DST change instead of drifting by an hour — the
/// class of bug the v1 schema could not even express (S2-07, S2-09).
/// </summary>
public sealed class RecurrenceExpander(ILogger<RecurrenceExpander> logger)
{
    /// <summary>How far ahead occurrences are materialised (02 §3, reminder_occurrences).</summary>
    public const int HorizonDays = 90;

    private const int MaxOccurrencesPerExpansion = 1000;

    public IReadOnlyList<DateTimeOffset> Expand(
        string rrule,
        DateTimeOffset seriesStart,
        string timezone,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset? until)
    {
        var effectiveTo = until is { } u && u < to ? u : to;
        if (effectiveTo <= from)
        {
            return [];
        }

        try
        {
            var calendarEvent = BuildEvent(rrule, seriesStart, timezone);

            return calendarEvent
                .GetOccurrences(ToCalDateTime(from))
                // TakeWhileBefore is exclusive and CalDateTime carries second
                // precision, so the bound is nudged past the last instant that
                // should be included rather than dropping it.
                .TakeWhileBefore(ToCalDateTime(effectiveTo.AddSeconds(1)))
                .Take(MaxOccurrencesPerExpansion)
                .Select(o => new DateTimeOffset(o.Period.StartTime.AsUtc, TimeSpan.Zero))
                .Where(instant => instant >= from && instant <= effectiveTo)
                .ToList();
        }
        catch (Exception ex) when (IsRuleProblem(ex))
        {
            // An unparseable rule must not stop the scheduler for every other
            // user. It is logged, the series produces no occurrences, and the
            // reminder still exists with its original due instant.
            logger.LogWarning(ex, "Could not expand RRULE {Rrule} in {Timezone}", rrule, timezone);
            return [];
        }
    }

    /// <summary>
    /// Validates a rule at write time. A rule that parses but never fires is a
    /// 422 rather than a silently dead reminder (04 §3.1).
    /// </summary>
    public bool TryValidate(string rrule, DateTimeOffset seriesStart, out string error)
    {
        error = string.Empty;

        var trimmed = rrule.Trim();
        if (trimmed.Length > 255)
        {
            error = "rrule must be at most 255 characters.";
            return false;
        }

        try
        {
            var calendarEvent = BuildEvent(trimmed, seriesStart, "Etc/UTC");
            var any = calendarEvent
                .GetOccurrences(ToCalDateTime(seriesStart))
                .Take(1)
                .Any();

            if (!any)
            {
                error = "That recurrence rule never produces an occurrence.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (IsRuleProblem(ex))
        {
            error = "That recurrence rule is not a valid RFC 5545 RRULE.";
            return false;
        }
    }

    private static bool IsRuleProblem(Exception ex) =>
        ex is ArgumentException or FormatException or InvalidOperationException
            or OverflowException or EvaluationException;

    private static CalendarEvent BuildEvent(string rrule, DateTimeOffset seriesStart, string timezone)
    {
        var normalised = rrule.Trim();
        if (normalised.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            normalised = normalised["RRULE:".Length..];
        }

        var tzId = SafeTimezone(timezone);
        var localStart = ToLocalWallTime(seriesStart, tzId);

        return new CalendarEvent
        {
            // DTSTART;TZID=<tz>:<local wall time>. Expressing the start as local
            // wall time in the series' own zone is what makes the recurrence
            // DST-stable; a UTC DTSTART would shift the local time twice a year.
            Start = new CalDateTime(localStart, tzId, hasTime: true),
            Duration = Duration.Zero,
            RecurrenceRule = new RecurrenceRule(normalised),
        };
    }

    private static DateTime ToLocalWallTime(DateTimeOffset instant, string tzId)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            return DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, tz).DateTime, DateTimeKind.Unspecified);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return DateTime.SpecifyKind(instant.UtcDateTime, DateTimeKind.Unspecified);
        }
    }

    private static CalDateTime ToCalDateTime(DateTimeOffset instant) =>
        new(DateTime.SpecifyKind(instant.UtcDateTime, DateTimeKind.Unspecified), "UTC", hasTime: true);

    private static string SafeTimezone(string timezone)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            return timezone;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return "UTC";
        }
    }
}
