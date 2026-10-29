using System.Globalization;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using PCConnect.Core.Domain;
using PCConnect.Companion.Services;

namespace PCConnect.Companion.ViewModels;

/// <summary>The four places the sidebar can be.</summary>
public enum CompanionPage
{
    ThisPc,
    OtherPcs,
    Reminders,
    Settings,
}

/// <summary>How an outcome reads: done, failed, or neither.</summary>
public enum Tone
{
    Good,
    Bad,
    Neutral,
}

/// <summary>One line in an activity log.</summary>
public sealed record ActivityRow(string Event, string Time, string Source, string Outcome, Tone Tone);

/// <summary>One day in a month grid.</summary>
public sealed partial class DayCell : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public required DateOnly Date { get; init; }

    public required bool InMonth { get; init; }

    public required bool IsToday { get; init; }

    /// <summary>Whether anything is scheduled on this day, which is what the dot means.</summary>
    public required bool HasEvents { get; init; }

    public string Number => Date.Day.ToString(CultureInfo.CurrentCulture);

    public string Tooltip => HasEvents
        ? $"{Date.ToString("ddd d MMM", CultureInfo.CurrentCulture)} · Has reminders"
        : Date.ToString("ddd d MMM", CultureInfo.CurrentCulture);
}

/// <summary>The navigation zoom level of the reminders calendar.</summary>
public enum CalendarViewMode
{
    Days,
    Months,
    Years,
}

/// <summary>One year in a decade/year picker grid.</summary>
public sealed partial class YearOption : ObservableObject
{
    public required int Year { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>One month in a month/year picker grid.</summary>
public sealed partial class MonthOption : ObservableObject
{
    public required int MonthNumber { get; init; }
    public required string ShortName { get; init; }
    public required string FullName { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isCurrent;
}

public enum ReopenChoice
{
    Cancel,
    RescheduleForToday,
    KeepOverdue
}

/// <summary>One reminder in a list, already rendered for the screen.</summary>
public sealed record ReminderRow(
    string Id,
    string Time,
    string DayLabel,
    string Body,
    string Detail,
    bool IsCompleted,
    bool IsPast,
    bool IsSnoozed = false,
    string? SnoozeLabel = null,
    string? SnoozeToolTip = null,
    bool IsDismissed = false,
    bool IsRecurring = false)
{
    public bool IsDismissedBadgeVisible => IsDismissed && !IsCompleted && !IsSnoozed;
    public string DismissedLabel => "Won't rerun";
    public string DismissedToolTip => "Dismissed — this reminder will not alert again.";

    public bool IsOverdue => IsDismissedBadgeVisible;
    public string OverdueLabel => DismissedLabel;
    public string OverdueToolTip => DismissedToolTip;
}

/// <summary>One command in the Settings table, or one button on Other PCs.</summary>
public sealed partial class CommandRow : ObservableObject
{
    [ObservableProperty]
    private bool _accepted;

    [ObservableProperty]
    private bool _asksForPassword;

    public required string Type { get; init; }

    public required string Name { get; init; }

    /// <summary>The key in `Resources/Icons.xaml`, e.g. `Icon.PowerSettingsNew`.</summary>
    public required string IconKey { get; init; }

    public bool IsDestructive => CommandTypes.Destructive.Contains(Type);
}

/// <summary>One PC in the reminder sheet's "Show on" list.</summary>
public sealed partial class TargetToggle : ObservableObject
{
    [ObservableProperty]
    private bool _isChosen;

    public required string DeviceId { get; init; }

    public required string Name { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tag))]
    private bool _isOnline;

    public bool IsThisPc { get; init; }

    public string Tag => IsThisPc ? "this PC" : (IsOnline ? "online" : "offline");
}

/// <summary>A colour a person can pick for the reminder window.</summary>
public sealed partial class Swatch : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public required string Colour { get; init; }
}

/// <summary>One day in the week strip on "This PC".</summary>
public sealed record DayColumn(string Label, string Number, bool IsToday, IReadOnlyList<WeekItem> Items)
{
    public bool IsEmpty => Items.Count == 0;
}

public sealed record WeekItem(
    string Time,
    string Body,
    string Id = "",
    string DayLabel = "",
    string FullDate = "",
    string? RecurrenceText = null,
    string? TargetText = null,
    bool IsCompleted = false,
    bool IsSnoozed = false,
    string? SnoozeLabel = null,
    bool IsDismissed = false)
{
    public bool IsDismissedBadgeVisible => IsDismissed && !IsCompleted && !IsSnoozed;
    public bool IsWonTRerunBadgeVisible => IsDismissedBadgeVisible;
    public bool IsSnoozedBadgeVisible => IsSnoozed && !IsCompleted;
    public bool IsCompletedBadgeVisible => IsCompleted;
    public bool IsActiveBadgeVisible => !IsCompleted && !IsSnoozed && !IsDismissed;
}

/// <summary>Detailed view of a clicked event for the popup modal.</summary>
public sealed partial class EventDetailModalViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _timeAndDate = string.Empty;

    [ObservableProperty]
    private string _recurrence = string.Empty;

    [ObservableProperty]
    private string _targets = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _statusTone = "Offline";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWonTRerunBadgeVisible))]
    [NotifyPropertyChangedFor(nameof(IsDismissedBadgeVisible))]
    [NotifyPropertyChangedFor(nameof(IsSnoozedBadgeVisible))]
    [NotifyPropertyChangedFor(nameof(IsCompletedBadgeVisible))]
    [NotifyPropertyChangedFor(nameof(IsActiveBadgeVisible))]
    private bool _isCompleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWonTRerunBadgeVisible))]
    [NotifyPropertyChangedFor(nameof(IsDismissedBadgeVisible))]
    [NotifyPropertyChangedFor(nameof(IsSnoozedBadgeVisible))]
    [NotifyPropertyChangedFor(nameof(IsCompletedBadgeVisible))]
    [NotifyPropertyChangedFor(nameof(IsActiveBadgeVisible))]
    private bool _isSnoozed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWonTRerunBadgeVisible))]
    [NotifyPropertyChangedFor(nameof(IsDismissedBadgeVisible))]
    [NotifyPropertyChangedFor(nameof(IsSnoozedBadgeVisible))]
    [NotifyPropertyChangedFor(nameof(IsCompletedBadgeVisible))]
    [NotifyPropertyChangedFor(nameof(IsActiveBadgeVisible))]
    private bool _isDismissed;

    [ObservableProperty]
    private bool _canComplete = true;

    [ObservableProperty]
    private bool _canEdit = true;

    [ObservableProperty]
    private bool _canDelete = true;

    [ObservableProperty]
    private bool _isRecurring;

    [ObservableProperty]
    private bool _isLoadingOccurrences;

    public System.Collections.ObjectModel.ObservableCollection<OccurrenceItemViewModel> Occurrences { get; } = new();

    public bool IsWonTRerunBadgeVisible => IsDismissed && !IsCompleted && !IsSnoozed;
    public bool IsDismissedBadgeVisible => IsWonTRerunBadgeVisible;
    public bool IsSnoozedBadgeVisible => IsSnoozed && !IsCompleted;
    public bool IsCompletedBadgeVisible => IsCompleted;
    public bool IsActiveBadgeVisible => !IsCompleted && !IsSnoozed && !IsDismissed;
}

public sealed partial class OccurrenceItemViewModel : ObservableObject
{
    public string ReminderId { get; init; } = string.Empty;
    public DateTimeOffset OccursAt { get; init; }
    public string SeriesBody { get; init; } = string.Empty;

    [ObservableProperty]
    private string _body = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomText))]
    private string? _customBody;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsSkipped))]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private string _status = "pending";

    [ObservableProperty]
    private string _dateLabel = string.Empty;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = string.Empty;

    public bool IsCompleted => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);
    public bool IsSkipped => string.Equals(Status, "skipped", StringComparison.OrdinalIgnoreCase);
    public bool IsPending => !IsCompleted && !IsSkipped;

    public bool HasCustomText => !string.IsNullOrEmpty(CustomBody);
}

/// <summary>The repeat options the design offers, in the order it shows them.</summary>
public enum RepeatKind
{
    Once,
    Daily,
    Weekly,
    Monthly,
    Yearly,
    Hourly,
}

/// <summary>How a repeating reminder stops.</summary>
public enum RepeatEndMode
{
    Never,
    OnDate,
    AfterOccurrences,
}

public sealed record RepeatOption(RepeatKind Kind, string Label);

public sealed record EndModeOption(RepeatEndMode Mode, string Label);

public sealed partial class IntervalOption : ObservableObject
{
    public required int Value { get; init; }

    [ObservableProperty]
    private string _label = string.Empty;

    public void RefreshLabel(RepeatKind kind) => Label = kind switch
    {
        RepeatKind.Hourly => Value == 1 ? "Every hour" : $"Every {Value} hours",
        RepeatKind.Daily => Value == 1 ? "Every day" : $"Every {Value} days",
        RepeatKind.Monthly => Value == 1 ? "Every month" : $"Every {Value} months",
        RepeatKind.Yearly => Value == 1 ? "Every year" : $"Every {Value} years",
        _ => Value == 1 ? "Every week" : $"Every {Value} weeks",
    };
}

public sealed partial class ExtraTimeItem : ObservableObject
{
    [ObservableProperty]
    private string _timeText = "12:00";
}

/// <summary>One of the seven day circles in the custom repeat editor.</summary>
public sealed partial class DayToggle : ObservableObject
{
    [ObservableProperty]
    private bool _isOn;

    public required DayOfWeek Day { get; init; }

    public required string Initial { get; init; }
}

/// <summary>
/// Plain words on the screen, RFC 5545 on the wire.
///
/// The times are deliberately not in the rule: BYHOUR and BYMINUTE multiply
/// out, so "10:30 and 15:45" in one rule would fire four times a day rather
/// than twice. Each time becomes its own series, which is what it means and
/// what the server already expands.
/// </summary>
public static class Recurrence
{
    private static readonly Dictionary<DayOfWeek, string> Codes = new()
    {
        [DayOfWeek.Monday] = "MO",
        [DayOfWeek.Tuesday] = "TU",
        [DayOfWeek.Wednesday] = "WE",
        [DayOfWeek.Thursday] = "TH",
        [DayOfWeek.Friday] = "FR",
        [DayOfWeek.Saturday] = "SA",
        [DayOfWeek.Sunday] = "SU",
    };

    public static int TargetOccurrenceCount(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule)) return 1;
        if (rrule.Contains("FREQ=HOURLY", StringComparison.OrdinalIgnoreCase)) return 10;
        if (rrule.Contains("FREQ=DAILY", StringComparison.OrdinalIgnoreCase)) return 7;
        return 3;
    }

    public static string? ToRrule(
        RepeatKind kind,
        IReadOnlyList<DayOfWeek> days,
        int intervalWeeks,
        DateOnly start,
        int? count = null)
    {
        var n = intervalWeeks > 0 ? intervalWeeks : 1;
        var intervalStr = n > 1 ? $";INTERVAL={n}" : string.Empty;
        var countStr = count is > 0 ? $";COUNT={count.Value}" : string.Empty;

        switch (kind)
        {
            case RepeatKind.Once:
                return null;

            case RepeatKind.Hourly:
                return $"FREQ=HOURLY{intervalStr}{countStr}";

            case RepeatKind.Daily:
                return $"FREQ=DAILY{intervalStr}{countStr}";

            case RepeatKind.Weekly:
                if (days.Count == 0 && n == 1)
                {
                    return $"FREQ=WEEKLY{countStr}";
                }
                var chosenWeekly = days.Count > 0 ? days : [start.DayOfWeek];
                return $"FREQ=WEEKLY{intervalStr};BYDAY={string.Join(",", chosenWeekly.Select(d => Codes[d]))}{countStr}";

            case RepeatKind.Monthly:
                return $"FREQ=MONTHLY{intervalStr}{countStr}";

            case RepeatKind.Yearly:
                return $"FREQ=YEARLY{intervalStr}{countStr}";

            default:
                var chosen = days.Count > 0 ? days : [start.DayOfWeek];
                return $"FREQ=WEEKLY{intervalStr};BYDAY={string.Join(",", chosen.Select(d => Codes[d]))}{countStr}";
        }
    }

    /// <summary>Extracts COUNT from an RFC 5545 RRULE when present.</summary>
    public static int? ParseCount(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return null;
        }

        var span = StripPrefix(rrule);
        foreach (var range in span.Split(';'))
        {
            var part = span[range];
            var eq = part.IndexOf('=');
            if (eq > 0 &&
                part[..eq].Equals("COUNT", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(part[(eq + 1)..], out var c) && c > 0)
            {
                return c;
            }
        }

        return null;
    }

    private static ReadOnlySpan<char> StripPrefix(string rrule)
    {
        var span = rrule.AsSpan().Trim();
        return span.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase) ? span[6..] : span;
    }

    private static Dictionary<string, string> ParseParts(string rrule) =>
        rrule.Replace("RRULE:", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.Ordinal);

    /// <summary>Reads a rule back into the words the app showed when it was written.</summary>
    public static string Describe(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return string.Empty;
        }

        var parts = ParseParts(rrule);
        var byDay = parts.GetValueOrDefault("BYDAY")?.Split(',') ?? [];
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var n) && n > 0 ? n : 1;
        var countSuffix = int.TryParse(parts.GetValueOrDefault("COUNT"), out var c) && c > 0
            ? (c == 1 ? " (1 time)" : $" ({c} times)")
            : string.Empty;

        var baseText = parts.GetValueOrDefault("FREQ")?.ToUpperInvariant() switch
        {
            "HOURLY" when interval == 1 => "Every hour",
            "HOURLY" => $"Every {interval} hours",
            "DAILY" when interval == 1 => "Every day",
            "DAILY" => $"Every {interval} days",
            "MONTHLY" when interval == 1 => "Every month",
            "MONTHLY" => $"Every {interval} months",
            "YEARLY" when interval == 1 => "Every year",
            "YEARLY" => $"Every {interval} years",
            "WEEKLY" when byDay.ToHashSet().SetEquals(["MO", "TU", "WE", "TH", "FR"]) => "Every weekday",
            "WEEKLY" when byDay.Length == 0 && interval == 1 => "Every week",
            "WEEKLY" when byDay.Length == 0 => $"Every {interval} weeks",
            "WEEKLY" => (interval > 1 ? $"Every {interval} weeks on " : "Every ") +
                JoinNaturally(byDay.Select(NameFor).Where(s => s.Length > 0).ToList()),
            _ => rrule!,
        };

        return baseText == rrule ? baseText : baseText + countSuffix;
    }

    private static string FormatNaturalTime(TimeOnly time, bool use24Hour)
    {
        if (use24Hour)
        {
            return time.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        var hour12 = time.Hour % 12 == 0 ? 12 : time.Hour % 12;
        var amPm = time.Hour < 12 ? "am" : "pm";
        return time.Minute == 0 ? $"{hour12} {amPm}" : $"{hour12}:{time.Minute:D2} {amPm}";
    }

    /// <summary>
    /// Describes the recurrence pattern in natural English, including the scheduled
    /// time and recurrence days/dates (e.g. "Every Thursday at 2 pm", "Every 8 October at 3 pm").
    /// </summary>
    public static string Describe(string? rrule, DateTimeOffset dueAt, bool use24Hour = true)
    {
        var local = dueAt.ToLocalTime();
        return Describe(rrule, DateOnly.FromDateTime(local.Date), TimeOnly.FromDateTime(local.DateTime), use24Hour);
    }

    public static string Describe(string? rrule, DateOnly date, TimeOnly time, bool use24Hour = true)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return string.Empty;
        }

        var parts = ParseParts(rrule);
        var byDay = parts.GetValueOrDefault("BYDAY")?.Split(',') ?? [];
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var n) && n > 0 ? n : 1;
        var countSuffix = int.TryParse(parts.GetValueOrDefault("COUNT"), out var c) && c > 0
            ? (c == 1 ? " · 1 time" : $" · {c} times")
            : string.Empty;
        var timeStr = FormatNaturalTime(time, use24Hour);

        var freq = parts.GetValueOrDefault("FREQ")?.ToUpperInvariant();
        if (freq is not ("HOURLY" or "DAILY" or "MONTHLY" or "YEARLY" or "WEEKLY"))
        {
            return Describe(rrule);
        }

        var baseText = freq switch
        {
            "HOURLY" when interval == 1 => "Every hour",
            "HOURLY" => $"Every {interval} hours",
            "DAILY" when interval == 1 => $"Every day at {timeStr}",
            "DAILY" => $"Every {interval} days at {timeStr}",
            "MONTHLY" when interval == 1 => $"Every month on the {Ordinal(date.Day)} at {timeStr}",
            "MONTHLY" => $"Every {interval} months on the {Ordinal(date.Day)} at {timeStr}",
            "YEARLY" when interval == 1 => $"Every {date.Day} {date.ToString("MMMM", CultureInfo.InvariantCulture)} at {timeStr}",
            "YEARLY" => $"Every {interval} years on {date.Day} {date.ToString("MMMM", CultureInfo.InvariantCulture)} at {timeStr}",
            "WEEKLY" when byDay.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["MO", "TU", "WE", "TH", "FR"]) =>
                interval == 1 ? $"Every weekday at {timeStr}" : $"Every {interval} weeks on weekdays at {timeStr}",
            _ => DescribeWeekly(byDay, interval, date.DayOfWeek, timeStr),
        };

        return baseText + countSuffix;
    }

    private static string DescribeWeekly(string[] byDay, int interval, DayOfWeek defaultDay, string timeStr)
    {
        var days = byDay.Length == 0
            ? [defaultDay]
            : byDay.Select(code => Codes.FirstOrDefault(p => p.Value.Equals(code, StringComparison.OrdinalIgnoreCase)).Key)
                   .Where(d => Codes.ContainsKey(d))
                   .Distinct()
                   .ToList();

        if (days.Count == 0)
        {
            days = [defaultDay];
        }

        var names = days.Select(FullName).ToList();
        var dayDesc = JoinNaturally(names);

        return interval > 1
            ? $"Every {interval} weeks on {dayDesc} at {timeStr}"
            : $"Every {dayDesc} at {timeStr}";
    }

    public static (RepeatKind Kind, List<DayOfWeek> Days, int IntervalWeeks) FromRrule(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return (RepeatKind.Once, [], 1);
        }

        var parts = ParseParts(rrule);
        var freq = parts.GetValueOrDefault("FREQ")?.ToUpperInvariant();
        var byDay = parts.GetValueOrDefault("BYDAY")?.Split(',') ?? [];
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var n) && n > 0 ? n : 1;

        if (freq == "HOURLY")
        {
            return (RepeatKind.Hourly, [], interval);
        }

        if (freq == "DAILY")
        {
            return (RepeatKind.Daily, [], interval);
        }

        if (freq == "YEARLY")
        {
            return (RepeatKind.Yearly, [], interval);
        }

        if (freq == "MONTHLY")
        {
            return (RepeatKind.Monthly, [], interval);
        }

        if (freq == "WEEKLY")
        {
            var codeToDay = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
            {
                ["MO"] = DayOfWeek.Monday,
                ["TU"] = DayOfWeek.Tuesday,
                ["WE"] = DayOfWeek.Wednesday,
                ["TH"] = DayOfWeek.Thursday,
                ["FR"] = DayOfWeek.Friday,
                ["SA"] = DayOfWeek.Saturday,
                ["SU"] = DayOfWeek.Sunday,
            };

            var days = byDay.Where(codeToDay.ContainsKey).Select(c => codeToDay[c]).ToList();
            return (RepeatKind.Weekly, days, interval);
        }

        return (RepeatKind.Once, [], 1);
    }

    /// <summary>The sentence under the repeat editor, so a rule can be read back before it is saved.</summary>
    public static string Summarise(
        RepeatKind kind,
        IReadOnlyList<DayOfWeek> days,
        int intervalWeeks,
        DateOnly date,
        IReadOnlyList<TimeOnly> times,
        DateOnly? until,
        DateOnly today,
        bool use24Hour = true,
        int? count = null)
    {
        var timeList = times.Count == 0 ? "at scheduled time" : JoinNaturally(times.Order().Select(t => TimeFormatting.FormatTime(t, use24Hour)).ToList());
        var longDate = date.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
        var start = date == today ? "today" : date == today.AddDays(1) ? "tomorrow" : $"on {longDate}";
        var ends = kind == RepeatKind.Once
            ? string.Empty
            : until is { } u
                ? $" Ends on {u.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}."
                : count is > 0
                    ? (count.Value == 1 ? " Ends after 1 occurrence." : $" Ends after {count.Value} occurrences.")
                    : " Doesn't end.";

        var n = intervalWeeks > 0 ? intervalWeeks : 1;

        return kind switch
        {
            RepeatKind.Once =>
                $"Once — {(date == today || date == today.AddDays(1) ? $"{start}, {longDate}" : longDate)}, at {timeList}.",

            RepeatKind.Hourly =>
                (n > 1 ? $"Every {n} hours" : "Every hour") +
                $" at {timeList}, starting {start}.{ends}",

            RepeatKind.Daily =>
                (n > 1 ? $"Every {n} days" : "Every day") +
                $" at {timeList}, starting {start}.{ends}",

            RepeatKind.Weekly when days.Count == 0 && n == 1 =>
                $"Every {date.ToString("dddd", CultureInfo.CurrentCulture)} at {timeList}, starting {start}.{ends}",

            RepeatKind.Weekly when days.Count == 0 =>
                $"Every {n} weeks on {date.ToString("dddd", CultureInfo.CurrentCulture)} at {timeList}, starting {start}.{ends}",

            RepeatKind.Weekly =>
                (n > 1
                    ? $"Every {n} weeks on {JoinNaturally(days.Select(FullName).ToList())}"
                    : $"Every {JoinNaturally(days.Select(FullName).ToList())}") +
                $" at {timeList}, starting {start}.{ends}",

            RepeatKind.Monthly =>
                (n > 1
                    ? $"On the {Ordinal(date.Day)} of every {n} months"
                    : $"On the {Ordinal(date.Day)} of every month") +
                $" at {timeList}, starting {start}.{ends}",

            RepeatKind.Yearly =>
                (n > 1
                    ? $"Every {n} years on {date.ToString("d MMMM", CultureInfo.CurrentCulture)}"
                    : $"Every year on {date.ToString("d MMMM", CultureInfo.CurrentCulture)}") +
                $" at {timeList}, starting {start}.{ends}",

            _ => "Pick recurrence options.",
        };
    }

    /// <summary>
    /// Whether a series falls on a given day.
    ///
    /// It understands the rules this app writes — weekly with days and an
    /// interval, monthly, daily — and nothing else. The server is the authority
    /// on when a reminder actually fires; this only decides what to draw in a
    /// calendar, so an unrecognised rule shows on its start day and no more,
    /// which is the truthful answer rather than a guessed one.
    /// </summary>
    public static bool OccursOn(string? rrule, DateOnly seriesStart, DateOnly day)
    {
        if (day < seriesStart)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(rrule))
        {
            return day == seriesStart;
        }

        ReadOnlySpan<char> freq = default;
        ReadOnlySpan<char> byDay = default;
        var interval = 1;
        var count = 0;

        var span = StripPrefix(rrule);
        foreach (var range in span.Split(';'))
        {
            var part = span[range];
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq];
            var val = part[(eq + 1)..];
            if (key.Equals("FREQ", StringComparison.OrdinalIgnoreCase))
            {
                freq = val;
            }
            else if (key.Equals("INTERVAL", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var n) && n > 0)
            {
                interval = n;
            }
            else if (key.Equals("COUNT", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var c) && c > 0)
            {
                count = c;
            }
            else if (key.Equals("BYDAY", StringComparison.OrdinalIgnoreCase))
            {
                byDay = val;
            }
        }

        if (freq.Equals("HOURLY", StringComparison.OrdinalIgnoreCase))
        {
            if (count > 0)
            {
                var minHoursElapsed = Math.Max(0, (day.DayNumber - seriesStart.DayNumber) * 24 - 23);
                if (minHoursElapsed / interval >= count)
                {
                    return false;
                }
            }
            return true;
        }

        if (freq.Equals("DAILY", StringComparison.OrdinalIgnoreCase))
        {
            var days = day.DayNumber - seriesStart.DayNumber;
            return days % interval == 0 && (count == 0 || days / interval < count);
        }

        if (freq.Equals("MONTHLY", StringComparison.OrdinalIgnoreCase))
        {
            var months = (day.Year - seriesStart.Year) * 12 + (day.Month - seriesStart.Month);
            if (months < 0 || months % interval != 0 || day.Day != seriesStart.Day)
            {
                return false;
            }
            if (count == 0)
            {
                return true;
            }
            if (seriesStart.Day <= 28)
            {
                return months / interval < count;
            }

            var validMonths = 0;
            for (var step = 0; step <= months; step += interval)
            {
                var totalMonth = seriesStart.Year * 12 + (seriesStart.Month - 1) + step;
                if (seriesStart.Day <= DateTime.DaysInMonth(totalMonth / 12, (totalMonth % 12) + 1) && ++validMonths > count)
                {
                    return false;
                }
            }
            return true;
        }

        if (freq.Equals("YEARLY", StringComparison.OrdinalIgnoreCase))
        {
            var years = day.Year - seriesStart.Year;
            if (years < 0 || years % interval != 0 || day.Month != seriesStart.Month || day.Day != seriesStart.Day)
            {
                return false;
            }
            if (count == 0)
            {
                return true;
            }
            if (seriesStart is not { Month: 2, Day: 29 })
            {
                return years / interval < count;
            }

            var validYears = 0;
            for (var y = seriesStart.Year; y <= day.Year; y += interval)
            {
                if (DateTime.IsLeapYear(y) && ++validYears > count)
                {
                    return false;
                }
            }
            return true;
        }

        if (freq.Equals("WEEKLY", StringComparison.OrdinalIgnoreCase))
        {
            var weekDiff = (day.DayNumber - StartOfWeek(seriesStart).DayNumber) / 7;
            if (weekDiff < 0 || weekDiff % interval != 0)
            {
                return false;
            }

            var mask = 0;
            if (byDay.IsEmpty)
            {
                mask = 1 << MondayIndex(seriesStart.DayOfWeek);
            }
            else
            {
                foreach (var r in byDay.Split(','))
                {
                    mask |= DayBit(byDay[r].Trim());
                }
            }

            var dayIdx = MondayIndex(day.DayOfWeek);
            if ((mask & (1 << dayIdx)) == 0)
            {
                return false;
            }
            if (count == 0)
            {
                return true;
            }

            var startIdx = MondayIndex(seriesStart.DayOfWeek);
            var cycleIndex = weekDiff / interval;
            if (cycleIndex == 0)
            {
                var sameWeekMask = mask & (((1 << (dayIdx + 1)) - 1) & ~((1 << startIdx) - 1));
                return BitOperations.PopCount((uint)sameWeekMask) <= count;
            }

            var firstWeek = BitOperations.PopCount((uint)(mask & ~((1 << startIdx) - 1)));
            var perWeek = BitOperations.PopCount((uint)mask);
            var lastWeek = BitOperations.PopCount((uint)(mask & ((1 << (dayIdx + 1)) - 1)));
            var seen = firstWeek + (long)(cycleIndex - 1) * perWeek + lastWeek;
            return seen <= count;
        }

        return day == seriesStart;
    }

    private static int DayBit(ReadOnlySpan<char> code)
    {
        if (code.Equals("MO", StringComparison.OrdinalIgnoreCase)) return 1 << 0;
        if (code.Equals("TU", StringComparison.OrdinalIgnoreCase)) return 1 << 1;
        if (code.Equals("WE", StringComparison.OrdinalIgnoreCase)) return 1 << 2;
        if (code.Equals("TH", StringComparison.OrdinalIgnoreCase)) return 1 << 3;
        if (code.Equals("FR", StringComparison.OrdinalIgnoreCase)) return 1 << 4;
        if (code.Equals("SA", StringComparison.OrdinalIgnoreCase)) return 1 << 5;
        if (code.Equals("SU", StringComparison.OrdinalIgnoreCase)) return 1 << 6;
        return 0;
    }

    private static int MondayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    private static DateOnly StartOfWeek(DateOnly date) =>
        date.AddDays(-MondayIndex(date.DayOfWeek));

    private static string FullName(DayOfWeek day) =>
        CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day);

    private static string NameFor(string code) =>
        Codes.FirstOrDefault(p => p.Value == code) is { Key: var day, Value: not null }
            ? CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day)
            : string.Empty;

    /// <summary>"Monday, Tuesday and Thursday" — the list the design writes.</summary>
    internal static string JoinNaturally(IReadOnlyList<string> values) => values.Count switch
    {
        0 => string.Empty,
        1 => values[0],
        _ => string.Join(", ", values.Take(values.Count - 1)) + " and " + values[^1],
    };

    internal static string Ordinal(int day)
    {
        var suffix = (day % 100) is >= 11 and <= 13
            ? "th"
            : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

        return $"{day}{suffix}";
    }
}

internal static class UiDispatcher
{
    public static void Run(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.InvokeAsync(action);
        }
    }

    public static Task RunAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    public static Task RunAsync(Func<Task> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess()
            ? action()
            : dispatcher.InvokeAsync(action).Task.Unwrap();
    }
}
