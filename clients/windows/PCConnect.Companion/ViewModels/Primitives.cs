using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PCConnect.Core.Domain;

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
}

/// <summary>One reminder in a list, already rendered for the screen.</summary>
public sealed record ReminderRow(
    string Id,
    string Time,
    string DayLabel,
    string Body,
    string Detail,
    bool IsCompleted,
    bool IsPast);

/// <summary>One command in the Settings table, or one button on Other PCs.</summary>
public sealed partial class CommandRow : ObservableObject
{
    [ObservableProperty]
    private bool _accepted;

    public required string Type { get; init; }

    public required string Name { get; init; }

    /// <summary>The key in `Resources/Icons.xaml`, e.g. `Icon.PowerSettingsNew`.</summary>
    public required string IconKey { get; init; }

    /// <summary>
    /// Whether this command stops and asks for a password.
    ///
    /// It is the server's policy, not a switch on this PC: the four destructive
    /// commands always ask (ADR-0011). Shown, never toggled — a switch that
    /// cannot turn the requirement off would be a lie about what it does.
    /// </summary>
    public bool AsksForPassword => CommandTypes.Destructive.Contains(Type);

    public bool IsDestructive => AsksForPassword;
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

public sealed record WeekItem(string Time, string Body);

/// <summary>The repeat options the design offers, in the order it shows them.</summary>
public enum RepeatKind
{
    Once,
    Weekly,
    Monthly,
    Custom,
}

public sealed partial class RepeatChip : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public required RepeatKind Kind { get; init; }

    public required string Label { get; init; }
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

    public static string? ToRrule(RepeatKind kind, IReadOnlyList<DayOfWeek> days, int intervalWeeks, DateOnly start)
    {
        switch (kind)
        {
            case RepeatKind.Once:
                return null;

            case RepeatKind.Weekly:
                return "FREQ=WEEKLY";

            case RepeatKind.Monthly:
                return "FREQ=MONTHLY";

            default:
                // No day ticked means "the day it starts on", which is what a
                // weekly rule does anyway — saying it keeps the rule stable if
                // the start date is later moved.
                var chosen = days.Count > 0 ? days : [start.DayOfWeek];
                var interval = intervalWeeks > 1 ? $";INTERVAL={intervalWeeks}" : string.Empty;
                return $"FREQ=WEEKLY{interval};BYDAY={string.Join(",", chosen.Select(d => Codes[d]))}";
        }
    }

    /// <summary>Reads a rule back into the words the app showed when it was written.</summary>
    public static string Describe(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return string.Empty;
        }

        var parts = rrule.Replace("RRULE:", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.Ordinal);

        var byDay = parts.GetValueOrDefault("BYDAY")?.Split(',') ?? [];
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var n) ? n : 1;

        return parts.GetValueOrDefault("FREQ")?.ToUpperInvariant() switch
        {
            "DAILY" => "Every day",
            "MONTHLY" => "Every month",
            "WEEKLY" when byDay.ToHashSet().SetEquals(["MO", "TU", "WE", "TH", "FR"]) => "Every weekday",
            "WEEKLY" when byDay.Length == 0 && interval == 1 => "Every week",
            "WEEKLY" when byDay.Length == 0 => $"Every {interval} weeks",
            "WEEKLY" => (interval > 1 ? $"Every {interval} weeks on " : "Every ") +
                JoinNaturally(byDay.Select(NameFor).Where(s => s.Length > 0).ToList()),
            _ => rrule!,
        };
    }

    /// <summary>The sentence under the repeat editor, so a rule can be read back before it is saved.</summary>
    public static string Summarise(
        RepeatKind kind,
        IReadOnlyList<DayOfWeek> days,
        int intervalWeeks,
        DateOnly date,
        IReadOnlyList<TimeOnly> times,
        DateOnly? until,
        DateOnly today)
    {
        var timeList = JoinNaturally(times.Order().Select(t => t.ToString("HH\\:mm", CultureInfo.InvariantCulture)).ToList());
        var longDate = date.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
        var start = date == today ? "today" : date == today.AddDays(1) ? "tomorrow" : $"on {longDate}";
        var ends = until is { } u
            ? $" Ends on {u.ToString("d MMMM", CultureInfo.CurrentCulture)}."
            : kind == RepeatKind.Once ? string.Empty : " Doesn't end.";

        return kind switch
        {
            RepeatKind.Once =>
                $"Once — {(date == today || date == today.AddDays(1) ? $"{start}, {longDate}" : longDate)}, at {timeList}.",

            RepeatKind.Weekly =>
                $"Every {date.ToString("dddd", CultureInfo.CurrentCulture)} at {timeList}, starting {start}.{ends}",

            RepeatKind.Monthly =>
                $"On the {Ordinal(date.Day)} of every month at {timeList}, starting {start}.{ends}",

            _ when days.Count == 0 => "Pick at least one day.",

            _ => (intervalWeeks > 1
                    ? $"Every {intervalWeeks} weeks on {JoinNaturally(days.Select(FullName).ToList())}"
                    : $"Every {JoinNaturally(days.Select(FullName).ToList())}") +
                $" at {timeList}, starting {start}.{ends}",
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

        var parts = rrule.Replace("RRULE:", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.Ordinal);

        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var n) && n > 0 ? n : 1;

        switch (parts.GetValueOrDefault("FREQ")?.ToUpperInvariant())
        {
            case "DAILY":
                return (day.DayNumber - seriesStart.DayNumber) % interval == 0;

            case "MONTHLY":
                return day.Day == seriesStart.Day;

            case "WEEKLY":
                var weeks = (day.DayNumber - StartOfWeek(seriesStart).DayNumber) / 7;
                if (weeks % interval != 0)
                {
                    return false;
                }

                var byDay = parts.GetValueOrDefault("BYDAY");
                if (string.IsNullOrEmpty(byDay))
                {
                    return day.DayOfWeek == seriesStart.DayOfWeek;
                }

                return byDay.Split(',').Any(code => Codes.GetValueOrDefault(day.DayOfWeek) == code);

            default:
                return day == seriesStart;
        }
    }

    private static DateOnly StartOfWeek(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

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
