using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PCConnect.Client;
using PCConnect.Core.Contracts;

namespace PCConnect.Companion.ViewModels;

/// <summary>
/// Reminders: a month to pick from, the list for what is picked, and the form
/// that writes a new one.
/// </summary>
public partial class RemindersViewModel(
    PcConnectClient api,
    ILogger<RemindersViewModel> logger) : ObservableObject
{
    private static readonly string[] DayInitials = ["M", "T", "W", "T", "F", "S", "S"];

    private DateOnly _viewMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    // ── the form ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    private string _newBody = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    [NotifyPropertyChangedFor(nameof(PickedDateLabel))]
    private DateTime _newDate = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    private string _newTime = DateTime.Now.AddHours(1).ToString("HH:00", CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    [NotifyPropertyChangedFor(nameof(IsCustomRepeat))]
    [NotifyPropertyChangedFor(nameof(DateLabel))]
    private RepeatKind _repeat = RepeatKind.Once;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    private int _intervalWeeks = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    [NotifyPropertyChangedFor(nameof(EndsLabel))]
    private DateTime? _until;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    // ── the list and the calendar ────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DateOnly? _selectedDay;

    [ObservableProperty]
    private string _listTitle = "Upcoming";

    [ObservableProperty]
    private string _listSummary = string.Empty;

    public ObservableCollection<ReminderResponse> Items { get; } = [];

    public ObservableCollection<ReminderRow> Rows { get; } = [];

    public ObservableCollection<DayCell> Days { get; } = [];

    public ObservableCollection<RepeatChip> RepeatChips { get; } = [];

    public ObservableCollection<DayToggle> RepeatDays { get; } = [];

    /// <summary>Additional times, each of which becomes its own series.</summary>
    public ObservableCollection<TimeOnly> ExtraTimes { get; } = [];

    public IReadOnlyList<int> IntervalOptions { get; } = [1, 2, 3, 4];

    public bool IsCustomRepeat => Repeat == RepeatKind.Custom;

    public bool HasSelection => SelectedDay is not null;

    public bool NoRows => Rows.Count == 0;

    public string MonthTitle => _viewMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    public string DateLabel => Repeat == RepeatKind.Once ? "Date" : "Starts";

    public string EndsLabel => Until is { } u ? u.ToString("d MMM", CultureInfo.CurrentCulture) : "Never";

    public string PickedDateLabel => Describe(DateOnly.FromDateTime(NewDate));

    /// <summary>The schedule as a sentence, so a rule can be read before it is saved.</summary>
    public string ScheduleSummary => Recurrence.Summarise(
        Repeat,
        RepeatDays.Where(d => d.IsOn).Select(d => d.Day).ToList(),
        IntervalWeeks,
        DateOnly.FromDateTime(NewDate),
        AllTimes(),
        Until is { } u ? DateOnly.FromDateTime(u) : null,
        DateOnly.FromDateTime(DateTime.Today));

    public RemindersViewModel Self => this;

    public void Initialise()
    {
        if (RepeatChips.Count > 0)
        {
            return;
        }

        foreach (var (kind, label) in new[]
                 {
                     (RepeatKind.Once, "Once"),
                     (RepeatKind.Weekly, "Every week"),
                     (RepeatKind.Monthly, "Every month"),
                     (RepeatKind.Custom, "Custom"),
                 })
        {
            RepeatChips.Add(new RepeatChip { Kind = kind, Label = label, IsSelected = kind == RepeatKind.Once });
        }

        // Monday first, to match the design's M T W T F S S row.
        for (var i = 0; i < 7; i++)
        {
            RepeatDays.Add(new DayToggle
            {
                Day = (DayOfWeek)(((i + 1) % 7)),
                Initial = DayInitials[i],
            });
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            var list = await api.ListRemindersAsync(100);

            Items.Clear();
            foreach (var reminder in list.OrderBy(r => r.DueAt))
            {
                Items.Add(reminder);
            }

            RebuildCalendar();
            RebuildRows();
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            StatusMessage = "Could not load your reminders.";
            logger.LogWarning(ex, "Reminder list failed");
        }
    }

    // ── calendar ─────────────────────────────────────────────────────────────

    public void RebuildCalendar()
    {
        Days.Clear();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var first = _viewMonth;
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var daysInMonth = DateTime.DaysInMonth(first.Year, first.Month);
        var cells = (int)Math.Ceiling((offset + daysInMonth) / 7.0) * 7;
        var gridStart = first.AddDays(-offset);

        for (var i = 0; i < cells; i++)
        {
            var day = gridStart.AddDays(i);

            Days.Add(new DayCell
            {
                Date = day,
                InMonth = day.Month == first.Month && day.Year == first.Year,
                IsToday = day == today,
                HasEvents = Items.Any(r => Recurrence.OccursOn(r.Rrule, DateOnly.FromDateTime(r.DueAt.ToLocalTime().Date), day)),
                IsSelected = SelectedDay == day,
            });
        }

        OnPropertyChanged(nameof(MonthTitle));
    }

    [RelayCommand]
    private void PreviousMonth()
    {
        _viewMonth = _viewMonth.AddMonths(-1);
        RebuildCalendar();
        RebuildRows();
    }

    [RelayCommand]
    private void NextMonth()
    {
        _viewMonth = _viewMonth.AddMonths(1);
        RebuildCalendar();
        RebuildRows();
    }

    /// <summary>Clicking the selected day again clears the filter, as Esc does.</summary>
    [RelayCommand]
    private void SelectDay(DayCell? cell)
    {
        if (cell is null)
        {
            return;
        }

        SelectedDay = SelectedDay == cell.Date ? null : cell.Date;

        foreach (var day in Days)
        {
            day.IsSelected = SelectedDay == day.Date;
        }

        RebuildRows();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        SelectedDay = null;

        foreach (var day in Days)
        {
            day.IsSelected = false;
        }

        RebuildRows();
    }

    private void RebuildRows()
    {
        Rows.Clear();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var days = SelectedDay is { } picked
            ? [picked]
            : DaysOfMonthFrom(today);

        foreach (var day in days)
        {
            foreach (var reminder in Items)
            {
                var seriesStart = DateOnly.FromDateTime(reminder.DueAt.ToLocalTime().Date);
                if (!Recurrence.OccursOn(reminder.Rrule, seriesStart, day))
                {
                    continue;
                }

                var local = reminder.DueAt.ToLocalTime();
                var past = day < today || (day == today && reminder.IsCompleted);

                Rows.Add(new ReminderRow(
                    Id: reminder.Id,
                    Time: local.ToString("HH:mm", CultureInfo.CurrentCulture),
                    DayLabel: Describe(day),
                    Body: reminder.Body,
                    Detail: string.Join(" · ", new[] { Recurrence.Describe(reminder.Rrule) }.Where(s => s.Length > 0)),
                    IsCompleted: reminder.IsCompleted && day == today,
                    IsPast: past));
            }
        }

        var count = Rows.Count == 1 ? "1 reminder" : $"{Rows.Count} reminders";

        if (SelectedDay is { } chosen)
        {
            ListTitle = Describe(chosen);
            ListSummary = count;
        }
        else
        {
            ListTitle = "Upcoming";
            ListSummary = $"{MonthTitle} · {count}";
        }

        OnPropertyChanged(nameof(NoRows));
    }

    private List<DateOnly> DaysOfMonthFrom(DateOnly today)
    {
        var days = new List<DateOnly>();
        var isCurrentMonth = _viewMonth.Year == today.Year && _viewMonth.Month == today.Month;

        for (var day = 1; day <= DateTime.DaysInMonth(_viewMonth.Year, _viewMonth.Month); day++)
        {
            var date = new DateOnly(_viewMonth.Year, _viewMonth.Month, day);
            if (!isCurrentMonth || date >= today)
            {
                days.Add(date);
            }
        }

        return days;
    }

    internal static string Describe(DateOnly date)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        return date == today ? "Today"
            : date == today.AddDays(1) ? "Tomorrow"
            : date == today.AddDays(-1) ? "Yesterday"
            : date.ToString("ddd d MMM", CultureInfo.CurrentCulture);
    }

    // ── the form ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ChooseRepeat(RepeatChip? chip)
    {
        if (chip is null)
        {
            return;
        }

        Repeat = chip.Kind;

        foreach (var option in RepeatChips)
        {
            option.IsSelected = option.Kind == chip.Kind;
        }

        OnPropertyChanged(nameof(ScheduleSummary));
    }

    [RelayCommand]
    private void ToggleRepeatDay(DayToggle? day)
    {
        if (day is null)
        {
            return;
        }

        day.IsOn = !day.IsOn;
        OnPropertyChanged(nameof(ScheduleSummary));
    }

    [RelayCommand]
    private void ClearUntil() => Until = null;

    [RelayCommand]
    private void AddTime()
    {
        // A second time is a second series. The design shows them as chips; each
        // one is saved separately so they can be ticked off separately.
        var next = AllTimes().Max().AddHours(1);
        ExtraTimes.Add(new TimeOnly(next.Hour, next.Minute));
        OnPropertyChanged(nameof(ScheduleSummary));
    }

    [RelayCommand]
    private void RemoveTime(object? time)
    {
        if (time is TimeOnly value)
        {
            ExtraTimes.Remove(value);
            OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    private List<TimeOnly> AllTimes()
    {
        var times = new List<TimeOnly>();

        if (TimeOnly.TryParse(NewTime, CultureInfo.CurrentCulture, out var primary))
        {
            times.Add(primary);
        }

        times.AddRange(ExtraTimes);

        return times.Count == 0 ? [new TimeOnly(9, 0)] : times.Distinct().Order().ToList();
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (string.IsNullOrWhiteSpace(NewBody))
        {
            StatusMessage = "Type the reminder first.";
            return;
        }

        if (!TimeOnly.TryParse(NewTime, CultureInfo.CurrentCulture, out _))
        {
            StatusMessage = "That time is not valid. Use HH:mm.";
            return;
        }

        if (Repeat == RepeatKind.Custom && RepeatDays.All(d => !d.IsOn))
        {
            StatusMessage = "Pick at least one day.";
            return;
        }

        IsBusy = true;

        try
        {
            var date = DateOnly.FromDateTime(NewDate);
            var rrule = Recurrence.ToRrule(
                Repeat,
                RepeatDays.Where(d => d.IsOn).Select(d => d.Day).ToList(),
                IntervalWeeks,
                date);

            var timezone = TimeZoneInfo.Local.HasIanaId
                ? TimeZoneInfo.Local.Id
                : TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "Etc/UTC";

            var until = rrule is not null && Until is { } end
                ? new DateTimeOffset(DateTime.SpecifyKind(end.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Local))
                : (DateTimeOffset?)null;

            var created = 0;

            foreach (var time in AllTimes())
            {
                // The local wall time the user typed is converted to a UTC
                // instant here, with the machine's IANA zone travelling
                // alongside it. v1 stored a naive time and fired it at UK time
                // for everybody (S2-07).
                var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Local);

                var reminder = await api.CreateReminderAsync(new CreateReminderRequest(
                    NewBody.Trim(),
                    new DateTimeOffset(local).ToUniversalTime(),
                    timezone,
                    rrule,
                    until));

                if (reminder is not null)
                {
                    created++;
                }
            }

            if (created > 0)
            {
                NewBody = string.Empty;
                ExtraTimes.Clear();
                await LoadAsync();
                StatusMessage = created == 1 ? "Reminder added." : $"{created} reminders added.";
            }
        }
        catch (PcConnectApiException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "Could not reach the PCConnect server.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CompleteAsync(ReminderRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            var updated = await api.CompleteReminderAsync(row.Id, !row.IsCompleted);
            if (updated is not null)
            {
                await LoadAsync();
            }
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            StatusMessage = "Could not update that reminder.";
            logger.LogWarning(ex, "Reminder completion failed");
        }
    }
}
