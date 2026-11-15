using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PCConnect.Client;
using PCConnect.Companion.Services;
using PCConnect.Core.Contracts;

namespace PCConnect.Companion.ViewModels;

/// <summary>
/// Reminders: a month to pick from, the list for what is picked, and the form
/// that writes a new one.
/// </summary>
public partial class RemindersViewModel(
    PcConnectClient api,
    DevicesViewModel devices,
    ILogger<RemindersViewModel> logger,
    ReminderSnoozeService? snoozeService = null,
    CompanionSettings? settings = null) : ObservableObject
{
    public bool Use24HourClock
    {
        get => settings?.Use24HourClock ?? true;
        set
        {
            if (settings is not null)
            {
                settings.Use24HourClock = value;
                OnPropertyChanged(nameof(Use24HourClock));
                OnPropertyChanged(nameof(ScheduleSummary));
            }
        }
    }
    private static readonly string[] DayInitials = ["M", "T", "W", "T", "F", "S", "S"];
    private bool _isSnoozeHooked;
    private bool _isSettingsHooked;
    private Task? _loadTask;
    private readonly Dictionary<(string ReminderId, long UtcMinute), ReminderOccurrenceResponse> _occurrences = new();

    public event Action? ItemsChanged;
    public event Action? OccurrencesChanged;

    private DateOnly _viewMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    private DispatcherTimer? _statusTimer;
    private double _remainingSeconds;
    private double _activeStatusDuration = TotalStatusDuration;
    private const double TotalStatusDuration = 5.0;
    private const double UndoStatusDuration = 7.0;
    private const double TimerIntervalMs = 50.0;
    private bool _isStatusTimerPaused;
    private Func<Task>? _undoAction;

    // ── the form ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    private string _newBody = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    [NotifyPropertyChangedFor(nameof(PickedDateLabel))]
    private DateTime _newDate = DateTime.Now.AddHours(1).Date;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    private string _newTime = DateTime.Now.AddHours(1).ToString("HH:00", CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    [NotifyPropertyChangedFor(nameof(IsRepeating))]
    [NotifyPropertyChangedFor(nameof(ShowDayBubbles))]
    [NotifyPropertyChangedFor(nameof(DateLabel))]
    private RepeatKind _repeat = RepeatKind.Once;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    private int _intervalWeeks = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    private DateTime? _until;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    [NotifyPropertyChangedFor(nameof(IsEndOnDate))]
    [NotifyPropertyChangedFor(nameof(IsEndAfterOccurrences))]
    private RepeatEndMode _endMode = RepeatEndMode.Never;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleSummary))]
    [NotifyPropertyChangedFor(nameof(OccurrenceUnitLabel))]
    private int _occurrenceCount = 5;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private double _statusProgress = 1.0;

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    private bool _isEditing;

    [ObservableProperty]
    private string? _editingReminderId;

    public string FormTitle => IsEditing ? "Edit reminder" : "New reminder";
    public string SaveButtonText => IsEditing ? "Save changes" : "Save reminder";

    /// <summary>
    /// Whether the server understands a reminder that names its PCs. The picker
    /// only appears when it does: one that the server ignored would be worse
    /// than none at all.
    /// </summary>
    [ObservableProperty]
    private bool _isTargetable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnChosenPcs))]
    private bool _showOnAllPcs = true;

    // ── the list and the calendar ────────────────────────────────────────────

    public const int PageSize = 20;

    private readonly HashSet<DateOnly> _selectedDays = [];
    private readonly List<ReminderRow> _allRows = [];
    private readonly List<ReminderRow> _allNonRecurringRows = [];
    private readonly List<ReminderRow> _allRecurringRows = [];
    private DateOnly? _selectionAnchor;
    private DateOnly? _dragAnchor;
    private DateOnly? _dragEnd;
    private HashSet<DateOnly> _rangeBase = [];

    public event Action? RowsReset;

    [ObservableProperty]
    private string _listTitle = "All events";

    [ObservableProperty]
    private string _listSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompletedRemindersButtonText))]
    [NotifyPropertyChangedFor(nameof(PastRemindersButtonText))]
    private bool _showCompletedReminders;

    [ObservableProperty]
    private bool _hasCompletedReminders;

    public string CompletedRemindersButtonText => ShowCompletedReminders ? "Hide completed" : "Show completed";

    public bool ShowPastReminders
    {
        get => ShowCompletedReminders;
        set => ShowCompletedReminders = value;
    }

    public bool HasPastReminders => HasCompletedReminders;

    public string PastRemindersButtonText => CompletedRemindersButtonText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDaysView))]
    [NotifyPropertyChangedFor(nameof(IsMonthsView))]
    [NotifyPropertyChangedFor(nameof(IsYearsView))]
    [NotifyPropertyChangedFor(nameof(IsMonthYearPickerOpen))]
    [NotifyPropertyChangedFor(nameof(CalendarHeaderTitle))]
    [NotifyPropertyChangedFor(nameof(CalendarHeaderHasChevron))]
    private CalendarViewMode _viewMode = CalendarViewMode.Days;

    public bool IsDaysView => ViewMode == CalendarViewMode.Days;
    public bool IsMonthsView => ViewMode == CalendarViewMode.Months;
    public bool IsYearsView => ViewMode == CalendarViewMode.Years;

    public bool IsMonthYearPickerOpen
    {
        get => ViewMode != CalendarViewMode.Days;
        set => ViewMode = value ? CalendarViewMode.Months : CalendarViewMode.Days;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalendarHeaderTitle))]
    private int _pickerYear = DateTime.Today.Year;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalendarHeaderTitle))]
    private int _decadeStart = (DateTime.Today.Year / 12) * 12;

    public string CalendarHeaderTitle => ViewMode switch
    {
        CalendarViewMode.Days => MonthTitle,
        CalendarViewMode.Months => PickerYear.ToString(CultureInfo.CurrentCulture),
        CalendarViewMode.Years => $"{DecadeStart} – {DecadeStart + 11}",
        _ => MonthTitle
    };

    public bool CalendarHeaderHasChevron => ViewMode != CalendarViewMode.Years;

    public ObservableCollection<YearOption> YearOptions { get; } = [];

    public ObservableCollection<MonthOption> MonthOptions { get; } = new(
        Enumerable.Range(1, 12).Select(m => new MonthOption
        {
            MonthNumber = m,
            ShortName = new DateTime(2026, m, 1).ToString("MMM", CultureInfo.CurrentCulture),
            FullName = new DateTime(2026, m, 1).ToString("MMMM", CultureInfo.CurrentCulture),
        }));

    public ObservableCollection<ReminderResponse> Items { get; } = [];

    public ObservableCollection<ReminderRow> Rows { get; } = [];

    public ObservableCollection<ReminderRow> NonRecurringRows { get; } = [];

    public ObservableCollection<ReminderRow> RecurringRows { get; } = [];

    public ObservableCollection<DayCell> Days { get; } = [];

    public IReadOnlyList<RepeatOption> RepeatOptions { get; } =
    [
        new(RepeatKind.Once, "Does not repeat"),
        new(RepeatKind.Hourly, "Hourly"),
        new(RepeatKind.Daily, "Daily"),
        new(RepeatKind.Weekly, "Weekly"),
        new(RepeatKind.Monthly, "Monthly"),
        new(RepeatKind.Yearly, "Yearly"),
    ];

    public ObservableCollection<DayToggle> RepeatDays { get; } = [];

    /// <summary>Additional times, each of which becomes its own series.</summary>
    public ObservableCollection<ExtraTimeItem> ExtraTimes { get; } = [];

    public bool HasExtraTimes => ExtraTimes.Count > 0;

    /// <summary>The PCs on the account, each with whether this reminder names it.</summary>
    public ObservableCollection<TargetToggle> Targets { get; } = [];

    public ObservableCollection<IntervalOption> IntervalChoiceItems { get; } = new(
        new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 21, 30 }.Select(v =>
        {
            var opt = new IntervalOption { Value = v };
            opt.RefreshLabel(RepeatKind.Once);
            return opt;
        }));

    public IReadOnlyList<EndModeOption> EndModeOptions { get; } =
    [
        new(RepeatEndMode.Never, "Never"),
        new(RepeatEndMode.OnDate, "On date"),
        new(RepeatEndMode.AfterOccurrences, "After"),
    ];

    public ObservableCollection<int> OccurrenceCountOptions { get; } =
        [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 15, 20, 25, 30, 50];

    public bool IsEndOnDate => EndMode == RepeatEndMode.OnDate;
    public bool IsEndAfterOccurrences => EndMode == RepeatEndMode.AfterOccurrences;
    public string OccurrenceUnitLabel => OccurrenceCount == 1 ? "time" : "times";

    public bool IsRepeating => Repeat != RepeatKind.Once;
    public bool ShowDayBubbles => Repeat == RepeatKind.Weekly;

    public bool HasSelection => _selectedDays.Count > 0;

    public bool HasMoreRows => NonRecurringRows.Count < _allNonRecurringRows.Count;

    public bool IsCurrentMonth => _viewMonth.Year == DateTime.Today.Year && _viewMonth.Month == DateTime.Today.Month;

    public bool NoRows => Rows.Count == 0;

    [ObservableProperty]
    private bool _hasRecurringReminders;

    [ObservableProperty]
    private bool _hasNonRecurringReminders;

    public bool ShowRecurringSection => !HideRecurring && RecurringRows.Count > 0;

    public bool ShowNonRecurringHeader => RecurringRows.Count > 0 && NonRecurringRows.Count > 0 && !HideRecurring;

    public string NonRecurringSectionTitle => HasSelection ? "One-time" : "One-time reminders";

    public string RecurringSectionTitle => "Recurring";

    public string NonRecurringCountText => NonRecurringRows.Count == 1 ? "1 reminder" : $"{NonRecurringRows.Count} reminders";

    public string RecurringCountText => RecurringRows.Count == 1 ? "1 active" : $"{RecurringRows.Count} active";

    public bool HideRecurring
    {
        get => settings?.HideRecurring ?? _hideRecurringField;
        set
        {
            if (HideRecurring == value)
            {
                return;
            }

            if (settings is not null)
            {
                settings.HideRecurring = value;
                if (_isSettingsHooked)
                {
                    return;
                }
            }
            else
            {
                _hideRecurringField = value;
            }

            NotifyHideRecurringChanged();
        }
    }
    private bool _hideRecurringField;

    private void NotifyHideRecurringChanged()
    {
        OnPropertyChanged(nameof(HideRecurring));
        OnPropertyChanged(nameof(HideRecurringButtonText));
        OnPropertyChanged(nameof(ShowRecurringSection));
        OnPropertyChanged(nameof(ShowNonRecurringHeader));
        RebuildCalendar();
        RebuildRows();
    }

    public string HideRecurringButtonText => HideRecurring ? "Show recurring" : "Hide recurring";

    [RelayCommand]
    public void ToggleHideRecurring()
    {
        HideRecurring = !HideRecurring;
    }

    public bool ShowOnChosenPcs => !ShowOnAllPcs;

    public string MonthTitle => _viewMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
    public DateOnly ViewMonth => _viewMonth;

    public string DateLabel => Repeat == RepeatKind.Once ? "Date" : "Starts";

    public string PickedDateLabel => Describe(DateOnly.FromDateTime(NewDate));

    /// <summary>The schedule as a sentence, so a rule can be read before it is saved.</summary>
    public string ScheduleSummary => Recurrence.Summarise(
        Repeat,
        RepeatDays.Where(d => d.IsOn).Select(d => d.Day).ToList(),
        IntervalWeeks,
        DateOnly.FromDateTime(NewDate),
        AllTimes(),
        EndMode == RepeatEndMode.OnDate && Until is { } u ? DateOnly.FromDateTime(u) : null,
        DateOnly.FromDateTime(DateTime.Today),
        Use24HourClock,
        EndMode == RepeatEndMode.AfterOccurrences ? OccurrenceCount : null);

    public RemindersViewModel Self => this;

    public void Initialise()
    {
        if (snoozeService is not null && !_isSnoozeHooked)
        {
            _isSnoozeHooked = true;
            snoozeService.ReminderSnoozed += info => UiDispatcher.Run(() =>
            {
                ShowStatus($"Reminder snoozed for {info.FormattedDuration}.", false);
                RebuildRows();
            });
            snoozeService.ReminderUnsnoozed += _ => UiDispatcher.Run(RebuildRows);
            snoozeService.ReminderDismissed += _ => UiDispatcher.Run(RebuildRows);
        }

        if (settings is not null && !_isSettingsHooked)
        {
            _isSettingsHooked = true;
            settings.TimeFormatChanged += () => UiDispatcher.Run(() =>
            {
                OnPropertyChanged(nameof(Use24HourClock));
                OnPropertyChanged(nameof(ScheduleSummary));
                if (TimeFormatting.TryParse(NewTime, out var t))
                {
                    NewTime = TimeFormatting.FormatTime(t, Use24HourClock);
                }
                foreach (var extra in ExtraTimes)
                {
                    if (TimeFormatting.TryParse(extra.TimeText, out var et))
                    {
                        extra.TimeText = TimeFormatting.FormatTime(et, Use24HourClock);
                    }
                }
                RebuildRows();
            });

            settings.RecurringFilterChanged += () => UiDispatcher.Run(NotifyHideRecurringChanged);
        }

        if (RepeatDays.Count > 0)
        {
            return;
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

    /// <summary>Rebuilds the "Show on" list, keeping whatever was already ticked.</summary>
    public void RefreshTargets()
    {
        var chosen = Targets.Where(t => t.IsChosen).Select(t => t.DeviceId).ToHashSet(StringComparer.Ordinal);

        Targets.Clear();

        var thisPcId = devices.ThisPcId;

        foreach (var device in devices.Items)
        {
            var isThisPc = !string.IsNullOrEmpty(thisPcId) && string.Equals(device.Id, thisPcId, StringComparison.OrdinalIgnoreCase);

            Targets.Add(new TargetToggle
            {
                DeviceId = device.Id,
                Name = device.DisplayName,
                IsOnline = device.IsOnline || isThisPc,
                IsThisPc = isThisPc,
                IsChosen = chosen.Contains(device.Id),
            });
        }
    }

    public void ApplyPresence(DevicePresenceEvent presence)
    {
        var target = Targets.FirstOrDefault(t => t.DeviceId == presence.DeviceId);
        if (target is not null && !target.IsThisPc)
        {
            target.IsOnline = presence.IsOnline;
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy || IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            var reloadTask = Task.WhenAll(devices.LoadAsync(), LoadAsync());
            var minDelayTask = Task.Delay(650);
            await Task.WhenAll(reloadTask, minDelayTask);
            RefreshTargets();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void ShowStatus(string message, bool isError, Func<Task>? undoAction = null)
    {
        _undoAction = isError ? null : undoAction;
        CanUndo = _undoAction is not null;
        StatusMessage = message;
        IsStatusError = isError;
        _activeStatusDuration = CanUndo ? UndoStatusDuration : TotalStatusDuration;
        _remainingSeconds = _activeStatusDuration;
        StatusProgress = 1.0;
        _isStatusTimerPaused = false;

        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (_statusTimer is null)
        {
            _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(TimerIntervalMs), DispatcherPriority.Normal, OnStatusTimerTick, dispatcher);
        }
        else
        {
            _statusTimer.Stop();
            _statusTimer.Start();
        }
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (_isStatusTimerPaused)
        {
            return;
        }

        _remainingSeconds -= TimerIntervalMs / 1000.0;
        if (_remainingSeconds <= 0)
        {
            _statusTimer?.Stop();
            _undoAction = null;
            CanUndo = false;
            StatusMessage = string.Empty;
            StatusProgress = 1.0;
        }
        else
        {
            StatusProgress = Math.Clamp(_remainingSeconds / _activeStatusDuration, 0.0, 1.0);
        }
    }

    [RelayCommand]
    public void PauseStatusTimer()
    {
        _isStatusTimerPaused = true;
    }

    [RelayCommand]
    public void ResumeStatusTimer()
    {
        _isStatusTimerPaused = false;
    }

    [RelayCommand]
    public void DismissStatus()
    {
        _statusTimer?.Stop();
        _undoAction = null;
        CanUndo = false;
        StatusMessage = string.Empty;
        StatusProgress = 1.0;
        _isStatusTimerPaused = false;
    }

    [RelayCommand]
    public async Task UndoAsync()
    {
        if (_undoAction is null || IsBusy)
        {
            return;
        }

        var action = _undoAction;
        _undoAction = null;
        CanUndo = false;
        _statusTimer?.Stop();

        IsBusy = true;
        try
        {
            await action();
        }
        catch (PcConnectApiException ex)
        {
            ShowStatus(ex.Message, true);
        }
        catch (HttpRequestException)
        {
            ShowStatus("Could not reach the PCConnect server.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleTarget(TargetToggle? target)
    {
        if (target is not null)
        {
            target.IsChosen = !target.IsChosen;
        }
    }

    [RelayCommand]
    private void ShowOnAll() => ShowOnAllPcs = true;

    [RelayCommand]
    private void ShowOnChosen() => ShowOnAllPcs = false;

    public Task LoadAsync()
    {
        if (_loadTask is { IsCompleted: false } inflight)
        {
            return inflight;
        }

        _loadTask = LoadCoreAsync();
        return _loadTask;
    }

    private async Task LoadCoreAsync()
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
            ItemsChanged?.Invoke();
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            ShowStatus("Could not load your reminders.", true);
            logger.LogWarning(ex, "Reminder list failed");
        }
    }

    /// <summary>
    /// Applies a realtime reminder change in-place without re-fetching the entire
    /// reminder list, skipping duplicate rebuilds when our own HTTP response has
    /// already updated <see cref="Items"/>.
    /// </summary>
    public async Task ApplyReminderChangedAsync(ReminderChangedEvent change)
    {
        if (string.Equals(change.Type, "deleted", StringComparison.OrdinalIgnoreCase))
        {
            if (EditingReminderId == change.ReminderId)
            {
                CancelEdit();
            }

            RemoveItem(change.ReminderId);
            return;
        }

        if (change.Reminder is { } incoming)
        {
            UpsertItem(incoming);
            return;
        }

        await LoadAsync();
    }

    public void UpsertItem(ReminderResponse reminder) => UpsertItems([reminder]);

    public void UpsertItems(IReadOnlyList<ReminderResponse> reminders)
    {
        var changed = false;

        foreach (var incoming in reminders)
        {
            var existingIndex = FindItemIndex(incoming.Id);
            if (existingIndex >= 0)
            {
                if (ReminderEquals(Items[existingIndex], incoming))
                {
                    continue;
                }

                Items.RemoveAt(existingIndex);
            }

            InsertSorted(incoming);
            changed = true;
        }

        if (changed)
        {
            RebuildCalendar();
            RebuildRows();
            ItemsChanged?.Invoke();
        }
    }

    public bool RemoveItem(string reminderId)
    {
        var index = FindItemIndex(reminderId);
        if (index < 0)
        {
            return false;
        }

        RemoveOccurrencesForReminder(reminderId);
        Items.RemoveAt(index);
        RebuildCalendar();
        RebuildRows();
        ItemsChanged?.Invoke();
        return true;
    }

    private static long UtcMinuteKey(DateTimeOffset occursAt) =>
        occursAt.ToUniversalTime().ToUnixTimeSeconds() / 60;

    public bool TryGetOccurrence(string reminderId, DateTimeOffset occursAt, out ReminderOccurrenceResponse? occurrence) =>
        _occurrences.TryGetValue((reminderId, UtcMinuteKey(occursAt)), out occurrence);

    public void SetOccurrences(string reminderId, IEnumerable<ReminderOccurrenceResponse> items)
    {
        var changed = false;
        foreach (var item in items)
        {
            var key = (reminderId, UtcMinuteKey(item.OccursAt));
            if (!_occurrences.TryGetValue(key, out var existing) || !OccurrenceEquals(existing, item))
            {
                _occurrences[key] = item;
                changed = true;
            }
        }

        if (changed)
        {
            RebuildCalendar();
            RebuildRows();
            OccurrencesChanged?.Invoke();
        }
    }

    public void UpsertOccurrence(ReminderOccurrenceResponse item) =>
        SetOccurrences(item.ReminderId, [item]);

    private void RemoveOccurrencesForReminder(string reminderId)
    {
        var keys = _occurrences.Keys.Where(k => string.Equals(k.ReminderId, reminderId, StringComparison.Ordinal)).ToList();
        foreach (var key in keys)
        {
            _occurrences.Remove(key);
        }
    }

    private static bool OccurrenceEquals(ReminderOccurrenceResponse a, ReminderOccurrenceResponse b) =>
        string.Equals(a.Status, b.Status, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.CustomBody, b.CustomBody, StringComparison.Ordinal) &&
        string.Equals(a.EffectiveBody, b.EffectiveBody, StringComparison.Ordinal) &&
        a.CompletedAt == b.CompletedAt;

    private int FindItemIndex(string id)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void InsertSorted(ReminderResponse reminder)
    {
        var index = 0;
        while (index < Items.Count && Items[index].DueAt <= reminder.DueAt)
        {
            index++;
        }

        Items.Insert(index, reminder);
    }

    private static bool ReminderEquals(ReminderResponse a, ReminderResponse b) =>
        a.Id == b.Id &&
        a.Body == b.Body &&
        a.DueAt == b.DueAt &&
        a.DueLocalTime == b.DueLocalTime &&
        a.Timezone == b.Timezone &&
        a.Rrule == b.Rrule &&
        a.RecurrenceUntil == b.RecurrenceUntil &&
        a.IsCompleted == b.IsCompleted &&
        a.CompletedAt == b.CompletedAt &&
        a.UpdatedAt == b.UpdatedAt &&
        ((a.DeviceIds is null && b.DeviceIds is null) ||
         (a.DeviceIds is not null && b.DeviceIds is not null && a.DeviceIds.SequenceEqual(b.DeviceIds)));

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

        var hideRecurring = HideRecurring;
        var oneOffDays = new HashSet<DateOnly>();
        var recurringSeries = new List<ReminderResponse>();

        foreach (var r in Items)
        {
            if (string.IsNullOrEmpty(r.Rrule))
            {
                oneOffDays.Add(DateOnly.FromDateTime(r.DueAt.ToLocalTime().Date));
            }
            else if (!hideRecurring)
            {
                recurringSeries.Add(r);
            }
        }

        for (var i = 0; i < cells; i++)
        {
            var day = gridStart.AddDays(i);
            var hasEvents = oneOffDays.Contains(day);
            if (!hasEvents)
            {
                foreach (var series in recurringSeries)
                {
                    var instants = Recurrence.OccurrencesOn(series.Rrule, series.DueAt, day, series.RecurrenceUntil, series.Timezone);
                    if (instants.Any(occ => !TryGetOccurrence(series.Id, occ, out var cached) ||
                                            !string.Equals(cached?.Status, "skipped", StringComparison.OrdinalIgnoreCase)))
                    {
                        hasEvents = true;
                        break;
                    }
                }
            }

            Days.Add(new DayCell
            {
                Date = day,
                InMonth = day.Month == first.Month && day.Year == first.Year,
                IsToday = day == today,
                HasEvents = hasEvents,
                IsSelected = _selectedDays.Contains(day),
            });
        }

        OnPropertyChanged(nameof(MonthTitle));
        OnPropertyChanged(nameof(ViewMonth));
        OnPropertyChanged(nameof(IsCurrentMonth));
        OnPropertyChanged(nameof(CalendarHeaderTitle));
        OnPropertyChanged(nameof(CalendarHeaderHasChevron));
        UpdateMonthOptions();
    }

    [RelayCommand]
    private void HeaderPrevious()
    {
        switch (ViewMode)
        {
            case CalendarViewMode.Days:
                PreviousMonth();
                break;
            case CalendarViewMode.Months:
                PreviousYear();
                break;
            case CalendarViewMode.Years:
                PreviousDecade();
                break;
        }
    }

    [RelayCommand]
    private void HeaderNext()
    {
        switch (ViewMode)
        {
            case CalendarViewMode.Days:
                NextMonth();
                break;
            case CalendarViewMode.Months:
                NextYear();
                break;
            case CalendarViewMode.Years:
                NextDecade();
                break;
        }
    }

    [RelayCommand]
    private void HeaderTitleClick()
    {
        switch (ViewMode)
        {
            case CalendarViewMode.Days:
                ViewMode = CalendarViewMode.Months;
                PickerYear = _viewMonth.Year;
                UpdateMonthOptions();
                break;
            case CalendarViewMode.Months:
                ViewMode = CalendarViewMode.Years;
                DecadeStart = (PickerYear / 12) * 12;
                UpdateYearOptions();
                break;
            case CalendarViewMode.Years:
                ViewMode = CalendarViewMode.Months;
                UpdateMonthOptions();
                break;
        }
        OnPropertyChanged(nameof(CalendarHeaderTitle));
        OnPropertyChanged(nameof(CalendarHeaderHasChevron));
    }

    [RelayCommand]
    private void PreviousMonth()
    {
        _viewMonth = _viewMonth.AddMonths(-1);
        RebuildCalendar();
        if (HasSelection)
        {
            RebuildRows();
        }
    }

    [RelayCommand]
    private void NextMonth()
    {
        _viewMonth = _viewMonth.AddMonths(1);
        RebuildCalendar();
        if (HasSelection)
        {
            RebuildRows();
        }
    }

    [RelayCommand]
    private void GoToToday()
    {
        _viewMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        PickerYear = DateTime.Today.Year;
        ViewMode = CalendarViewMode.Days;
        RebuildCalendar();
        ClearSelection();
        OnPropertyChanged(nameof(CalendarHeaderTitle));
        OnPropertyChanged(nameof(CalendarHeaderHasChevron));
    }

    [RelayCommand]
    private void ToggleShowCompletedReminders()
    {
        ShowCompletedReminders = !ShowCompletedReminders;
        RebuildRows();
    }

    [RelayCommand]
    private void ToggleShowPastReminders() => ToggleShowCompletedReminders();

    [RelayCommand]
    private void ToggleMonthYearPicker()
    {
        if (ViewMode == CalendarViewMode.Days)
        {
            ViewMode = CalendarViewMode.Months;
            PickerYear = _viewMonth.Year;
            UpdateMonthOptions();
        }
        else
        {
            ViewMode = CalendarViewMode.Days;
        }
        OnPropertyChanged(nameof(CalendarHeaderTitle));
        OnPropertyChanged(nameof(CalendarHeaderHasChevron));
    }

    [RelayCommand]
    private void PreviousYear()
    {
        PickerYear--;
        UpdateMonthOptions();
        OnPropertyChanged(nameof(CalendarHeaderTitle));
    }

    [RelayCommand]
    private void NextYear()
    {
        PickerYear++;
        UpdateMonthOptions();
        OnPropertyChanged(nameof(CalendarHeaderTitle));
    }

    [RelayCommand]
    private void PreviousDecade()
    {
        DecadeStart -= 12;
        UpdateYearOptions();
        OnPropertyChanged(nameof(CalendarHeaderTitle));
    }

    [RelayCommand]
    private void NextDecade()
    {
        DecadeStart += 12;
        UpdateYearOptions();
        OnPropertyChanged(nameof(CalendarHeaderTitle));
    }

    [RelayCommand]
    private void SelectYear(object? param)
    {
        var year = param switch
        {
            int y => y,
            YearOption opt => opt.Year,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => PickerYear
        };

        PickerYear = year;
        ViewMode = CalendarViewMode.Months;
        UpdateMonthOptions();
        OnPropertyChanged(nameof(CalendarHeaderTitle));
        OnPropertyChanged(nameof(CalendarHeaderHasChevron));
    }

    [RelayCommand]
    private void SelectMonth(object? param)
    {
        var monthNumber = param switch
        {
            int n => n,
            MonthOption opt => opt.MonthNumber,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => _viewMonth.Month
        };

        _viewMonth = new DateOnly(PickerYear, monthNumber, 1);
        ViewMode = CalendarViewMode.Days;
        RebuildCalendar();
        if (HasSelection)
        {
            RebuildRows();
        }
        OnPropertyChanged(nameof(CalendarHeaderTitle));
        OnPropertyChanged(nameof(CalendarHeaderHasChevron));
    }

    [RelayCommand]
    private void BackToCalendar()
    {
        ViewMode = CalendarViewMode.Days;
        OnPropertyChanged(nameof(CalendarHeaderTitle));
        OnPropertyChanged(nameof(CalendarHeaderHasChevron));
    }

    private void UpdateYearOptions()
    {
        YearOptions.Clear();
        var todayYear = DateTime.Today.Year;
        for (var y = DecadeStart; y < DecadeStart + 12; y++)
        {
            YearOptions.Add(new YearOption
            {
                Year = y,
                IsSelected = y == PickerYear,
                IsCurrent = y == todayYear,
            });
        }
    }

    private void UpdateMonthOptions()
    {
        var today = DateTime.Today;
        foreach (var opt in MonthOptions)
        {
            opt.IsSelected = opt.MonthNumber == _viewMonth.Month && PickerYear == _viewMonth.Year;
            opt.IsCurrent = opt.MonthNumber == today.Month && PickerYear == today.Year;
        }
    }

    [RelayCommand]
    public void LoadMoreRows()
    {
        if (!HasMoreRows)
        {
            return;
        }

        var nextBatch = _allNonRecurringRows.Skip(NonRecurringRows.Count).Take(PageSize).ToList();
        foreach (var row in nextBatch)
        {
            NonRecurringRows.Add(row);
            Rows.Add(row);
        }

        UpdateListSummary();
        OnPropertyChanged(nameof(HasMoreRows));
    }

    /// <summary>Starts a click or drag; Shift extends a range and Ctrl toggles a day.</summary>
    public void BeginDaySelection(DateOnly date, bool extend, bool toggle)
    {
        if (extend)
        {
            var anchor = _selectionAnchor ?? date;
            _dragAnchor = anchor;
            _dragEnd = date;
            SetRange(anchor, date);
        }
        else if (toggle)
        {
            if (!_selectedDays.Remove(date))
            {
                _selectedDays.Add(date);
            }
            _rangeBase = new HashSet<DateOnly>(_selectedDays);
            _selectionAnchor = date;
            _dragAnchor = date;
            _dragEnd = date;
        }
        else
        {
            _selectedDays.Clear();
            _selectedDays.Add(date);
            _rangeBase.Clear();
            _selectionAnchor = date;
            _dragAnchor = date;
            _dragEnd = date;
        }

        RefreshSelection();
    }

    public void ExtendDaySelection(DateOnly date)
    {
        if (_dragAnchor is not { } anchor || _dragEnd == date)
        {
            return;
        }

        _dragEnd = date;
        SetRange(anchor, date);
        RefreshSelection();
    }

    public void EndDaySelection()
    {
        _dragAnchor = null;
        _dragEnd = null;
    }

    private void SetRange(DateOnly anchor, DateOnly date)
    {
        _selectedDays.Clear();
        _selectedDays.UnionWith(_rangeBase);
        var start = Math.Min(anchor.DayNumber, date.DayNumber);
        var end = Math.Max(anchor.DayNumber, date.DayNumber);
        for (var number = start; number <= end; number++)
        {
            _selectedDays.Add(DateOnly.FromDayNumber(number));
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        EndDaySelection();
        _selectionAnchor = null;
        _rangeBase.Clear();
        _selectedDays.Clear();
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var day in Days)
        {
            day.IsSelected = _selectedDays.Contains(day.Date);
        }

        OnPropertyChanged(nameof(HasSelection));
        RebuildRows();
    }

    public void RebuildRows()
    {
        Rows.Clear();
        NonRecurringRows.Clear();
        RecurringRows.Clear();
        _allRows.Clear();
        _allNonRecurringRows.Clear();
        _allRecurringRows.Clear();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var nowTime = DateTime.Now.TimeOfDay;
        var deviceLookup = BuildDeviceLookup();

        if (HasSelection)
        {
            var days = _selectedDays.Order().ToList();
            var multiDaySelection = days.Count > 1;
            var projected = Items
                .Select(r =>
                {
                    var local = r.DueAt.ToLocalTime();
                    return (
                        Reminder: r,
                        Local: local,
                        StartDate: DateOnly.FromDateTime(local.Date),
                        UntilDate: r.RecurrenceUntil is { } u ? DateOnly.FromDateTime(u.ToLocalTime().Date) : (DateOnly?)null,
                        IsRecurring: !string.IsNullOrEmpty(r.Rrule));
                })
                .OrderBy(x => x.Local.TimeOfDay)
                .ToList();

            foreach (var day in days)
            {
                var dayNonRecurring = new List<(TimeSpan SortTime, ReminderRow Row)>();
                var dayRecurring = new List<(TimeSpan SortTime, ReminderRow Row)>();

                foreach (var entry in projected)
                {
                    if (entry.IsRecurring)
                    {
                        var instants = Recurrence.OccurrencesOn(
                            entry.Reminder.Rrule,
                            entry.Reminder.DueAt,
                            day,
                            entry.Reminder.RecurrenceUntil,
                            entry.Reminder.Timezone);

                        foreach (var occLocal in instants)
                        {
                            var bodyOverride = entry.Reminder.Body;
                            var isCompleted = false;

                            if (TryGetOccurrence(entry.Reminder.Id, occLocal, out var cachedOcc) && cachedOcc is not null)
                            {
                                if (string.Equals(cachedOcc.Status, "skipped", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                isCompleted = string.Equals(cachedOcc.Status, "completed", StringComparison.OrdinalIgnoreCase);
                                if (!string.IsNullOrWhiteSpace(cachedOcc.EffectiveBody))
                                {
                                    bodyOverride = cachedOcc.EffectiveBody;
                                }
                            }

                            var (isSnoozed, snoozeInfo, isDismissed) = GetSnoozeState(entry.Reminder.Id);
                            var isPastTime = day < today || (day == today && occLocal.TimeOfDay <= nowTime);
                            dayRecurring.Add((
                                occLocal.TimeOfDay,
                                CreateReminderRow(
                                    entry.Reminder,
                                    occLocal,
                                    dayLabel: multiDaySelection ? Describe(day) : string.Empty,
                                    recurrenceDesc: Recurrence.Describe(entry.Reminder.Rrule, entry.Local, Use24HourClock),
                                    deviceLookup,
                                    isCompleted,
                                    isPast: !isSnoozed && (isPastTime || isCompleted),
                                    isSnoozed,
                                    snoozeInfo,
                                    isDismissed,
                                    isRecurring: true,
                                    bodyOverride: bodyOverride)));
                        }
                    }
                    else if (entry.StartDate == day)
                    {
                        var isCompleted = entry.Reminder.IsCompleted;
                        var (isSnoozed, snoozeInfo, isDismissed) = GetSnoozeState(entry.Reminder.Id);
                        var isPastTime = day < today || (day == today && entry.Local.TimeOfDay <= nowTime);
                        dayNonRecurring.Add((
                            entry.Local.TimeOfDay,
                            CreateReminderRow(
                                entry.Reminder,
                                entry.Local,
                                dayLabel: multiDaySelection ? Describe(day) : string.Empty,
                                recurrenceDesc: null,
                                deviceLookup,
                                isCompleted,
                                isPast: !isSnoozed && (isPastTime || isCompleted),
                                isSnoozed,
                                snoozeInfo,
                                isDismissed,
                                isRecurring: false)));
                    }
                }

                dayNonRecurring.Sort((a, b) => a.SortTime.CompareTo(b.SortTime));
                dayRecurring.Sort((a, b) => a.SortTime.CompareTo(b.SortTime));
                _allNonRecurringRows.AddRange(dayNonRecurring.Select(static x => x.Row));
                _allRecurringRows.AddRange(dayRecurring.Select(static x => x.Row));
            }

            ListTitle = !multiDaySelection ? Describe(days[0]) : $"{days.Count} days selected";
            HasCompletedReminders = false;
            OnPropertyChanged(nameof(HasPastReminders));
        }
        else
        {
            ListTitle = "All events";
            var pastCount = 0;

            var oneOffs = new List<(ReminderResponse Reminder, DateTimeOffset Local)>();
            var recurrings = new List<(ReminderResponse Reminder, DateTimeOffset Local)>();
            foreach (var r in Items)
            {
                var local = r.DueAt.ToLocalTime();
                if (string.IsNullOrEmpty(r.Rrule))
                {
                    oneOffs.Add((r, local));
                }
                else
                {
                    recurrings.Add((r, local));
                }
            }

            oneOffs.Sort((a, b) => a.Local.CompareTo(b.Local));
            recurrings.Sort((a, b) => a.Local.TimeOfDay.CompareTo(b.Local.TimeOfDay));

            // 1. One-time reminders
            foreach (var (reminder, local) in oneOffs)
            {
                var day = DateOnly.FromDateTime(local.Date);
                var isCompleted = reminder.IsCompleted;
                var (isSnoozed, snoozeInfo, isDismissed) = GetSnoozeState(reminder.Id);
                var isPastTime = day < today || (day == today && local.TimeOfDay <= nowTime);
                var isFilteredPast = !isSnoozed && (day < today || isCompleted);

                if (isFilteredPast)
                {
                    pastCount++;
                    if (!ShowCompletedReminders)
                    {
                        continue;
                    }
                }

                _allNonRecurringRows.Add(CreateReminderRow(
                    reminder,
                    local,
                    dayLabel: Describe(day),
                    recurrenceDesc: null,
                    deviceLookup,
                    isCompleted,
                    isPast: !isSnoozed && (isPastTime || isCompleted),
                    isSnoozed,
                    snoozeInfo,
                    isDismissed));
            }

            // 2. Recurring reminders (distinct definition for each recurring series)
            foreach (var (reminder, local) in recurrings)
            {
                var hasHitEndDate = HasRecurringSeriesEnded(reminder);
                if (hasHitEndDate)
                {
                    pastCount++;
                    if (!ShowCompletedReminders)
                    {
                        continue;
                    }
                }

                var (isSnoozed, snoozeInfo, isDismissed) = GetSnoozeState(reminder.Id);
                _allRecurringRows.Add(CreateReminderRow(
                    reminder,
                    local,
                    dayLabel: string.Empty,
                    recurrenceDesc: Recurrence.Describe(reminder.Rrule, local, Use24HourClock),
                    deviceLookup,
                    isCompleted: hasHitEndDate,
                    isPast: false,
                    isSnoozed,
                    snoozeInfo,
                    isDismissed,
                    isRecurring: true));
            }

            HasCompletedReminders = pastCount > 0;
            OnPropertyChanged(nameof(HasPastReminders));
        }

        HasRecurringReminders = _allRecurringRows.Count > 0;
        HasNonRecurringReminders = _allNonRecurringRows.Count > 0;

        var pageCount = Math.Min(PageSize, _allNonRecurringRows.Count);
        for (var i = 0; i < pageCount; i++)
        {
            var row = _allNonRecurringRows[i];
            NonRecurringRows.Add(row);
            Rows.Add(row);
        }

        _allRows.AddRange(_allNonRecurringRows);

        var includeRecurring = !HideRecurring;
        foreach (var row in _allRecurringRows)
        {
            RecurringRows.Add(row);
            if (includeRecurring)
            {
                _allRows.Add(row);
                Rows.Add(row);
            }
        }

        UpdateListSummary();
        OnPropertyChanged(nameof(ShowRecurringSection));
        OnPropertyChanged(nameof(ShowNonRecurringHeader));
        OnPropertyChanged(nameof(NonRecurringSectionTitle));
        OnPropertyChanged(nameof(RecurringSectionTitle));
        OnPropertyChanged(nameof(NonRecurringCountText));
        OnPropertyChanged(nameof(RecurringCountText));
        OnPropertyChanged(nameof(NoRows));
        OnPropertyChanged(nameof(HasMoreRows));
        RowsReset?.Invoke();
    }

    private (bool IsSnoozed, ReminderSnoozeInfo? Info, bool IsDismissed) GetSnoozeState(string reminderId)
    {
        ReminderSnoozeInfo? info = null;
        var isSnoozed = snoozeService is not null && snoozeService.IsSnoozed(reminderId, out info);
        var isDismissed = snoozeService is not null && snoozeService.IsDismissed(reminderId);
        return (isSnoozed, info, isDismissed);
    }

    private ReminderRow CreateReminderRow(
        ReminderResponse reminder,
        DateTimeOffset local,
        string dayLabel,
        string? recurrenceDesc,
        IReadOnlyDictionary<string, string>? deviceLookup,
        bool isCompleted,
        bool isPast,
        bool isSnoozed,
        ReminderSnoozeInfo? snoozeInfo,
        bool isDismissed,
        bool isRecurring = false,
        string? bodyOverride = null)
    {
        var targetDesc = DescribeTargets(reminder.DeviceIds, deviceLookup);
        var detail = string.IsNullOrEmpty(recurrenceDesc)
            ? targetDesc
            : string.IsNullOrEmpty(targetDesc)
                ? recurrenceDesc
                : $"{recurrenceDesc} · {targetDesc}";

        var snoozeLabel = isSnoozed && snoozeInfo is not null
            ? $"Snoozed for {snoozeInfo.FormattedDuration}"
            : null;
        var snoozeToolTip = isSnoozed && snoozeInfo is not null
            ? $"Snoozed for {snoozeInfo.FormattedDuration} (until {TimeFormatting.FormatTime(snoozeInfo.SnoozedUntil, Use24HourClock)})"
            : null;

        return new ReminderRow(
            Id: reminder.Id,
            Time: TimeFormatting.FormatTime(local, Use24HourClock),
            DayLabel: dayLabel,
            Body: bodyOverride ?? reminder.Body,
            Detail: detail,
            IsCompleted: isCompleted,
            IsPast: isPast,
            IsSnoozed: isSnoozed,
            SnoozeLabel: snoozeLabel,
            SnoozeToolTip: snoozeToolTip,
            IsDismissed: isDismissed,
            IsRecurring: isRecurring);
    }

    private void UpdateListSummary()
    {
        var total = _allNonRecurringRows.Count + (HideRecurring ? 0 : _allRecurringRows.Count);
        ListSummary = total == 1 ? "1 reminder" : $"{total} reminders";
    }

    internal Dictionary<string, string>? BuildDeviceLookup()
    {
        if (devices.Items.Count == 0)
        {
            return null;
        }

        var lookup = new Dictionary<string, string>(devices.Items.Count, StringComparer.Ordinal);
        foreach (var device in devices.Items)
        {
            if (!string.IsNullOrEmpty(device.DisplayName))
            {
                lookup[device.Id] = device.DisplayName;
            }
        }

        return lookup;
    }

    /// <summary>"all PCs", or the names of the ones it was aimed at.</summary>
    internal string DescribeTargets(IReadOnlyList<string>? deviceIds, IReadOnlyDictionary<string, string>? lookup = null)
    {
        if (deviceIds is null || deviceIds.Count == 0)
        {
            return "all PCs";
        }

        var names = new List<string>(deviceIds.Count);
        foreach (var id in deviceIds)
        {
            var name = lookup is not null
                ? (lookup.TryGetValue(id, out var found) ? found : null)
                : devices.Items.FirstOrDefault(d => d.Id == id)?.DisplayName;

            if (!string.IsNullOrEmpty(name))
            {
                names.Add(name);
            }
        }

        // A device that has since been revoked is no longer in the list; saying
        // how many are left is better than silently naming fewer PCs than the
        // reminder actually has.
        return names.Count == 0 ? "a PC that has been removed" : Recurrence.JoinNaturally(names);
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

    partial void OnRepeatChanged(RepeatKind value)
    {
        foreach (var opt in IntervalChoiceItems)
        {
            opt.RefreshLabel(value);
        }

        if (value == RepeatKind.Once)
        {
            foreach (var day in RepeatDays)
            {
                day.IsOn = false;
            }

            EndMode = RepeatEndMode.Never;
        }
        else if (value == RepeatKind.Weekly && RepeatDays.All(d => !d.IsOn))
        {
            var targetDay = DateOnly.FromDateTime(NewDate).DayOfWeek;
            var match = RepeatDays.FirstOrDefault(d => d.Day == targetDay);
            if (match is not null)
            {
                match.IsOn = true;
            }
        }
    }

    partial void OnIntervalWeeksChanged(int value)
    {
        if (value > 0 && IntervalChoiceItems.All(o => o.Value != value))
        {
            var opt = new IntervalOption { Value = value };
            opt.RefreshLabel(Repeat);
            IntervalChoiceItems.Insert(IntervalChoiceItems.TakeWhile(o => o.Value < value).Count(), opt);
        }
    }

    partial void OnOccurrenceCountChanged(int value)
    {
        if (value > 0 && !OccurrenceCountOptions.Contains(value))
        {
            OccurrenceCountOptions.Insert(OccurrenceCountOptions.TakeWhile(c => c < value).Count(), value);
        }
    }

    partial void OnEndModeChanged(RepeatEndMode value)
    {
        if (value == RepeatEndMode.OnDate)
        {
            Until ??= DefaultUntilFor(Repeat, IntervalWeeks, NewDate);
        }
        else
        {
            Until = null;
        }
    }

    private static DateTime DefaultUntilFor(RepeatKind kind, int interval, DateTime startDate)
    {
        var baseDate = startDate.Date;
        var step = Math.Max(interval, 1);
        return kind switch
        {
            RepeatKind.Hourly or RepeatKind.Daily => baseDate.AddDays(Math.Max(7, step * 5)),
            RepeatKind.Monthly => baseDate.AddMonths(Math.Max(6, step * 3)),
            RepeatKind.Yearly => baseDate.AddYears(step * 3),
            _ => baseDate.AddMonths(Math.Max(1, step)),
        };
    }

    [RelayCommand]
    private void ToggleRepeatDay(DayToggle? day)
    {
        if (day is null)
        {
            return;
        }

        day.IsOn = !day.IsOn;

        if (RepeatDays.Any(d => d.IsOn) && Repeat != RepeatKind.Weekly)
        {
            Repeat = RepeatKind.Weekly;
        }

        OnPropertyChanged(nameof(ScheduleSummary));
    }

    [RelayCommand]
    private void SelectWeekdays() => SetDays(d => d is not (DayOfWeek.Saturday or DayOfWeek.Sunday));

    [RelayCommand]
    private void SelectAllDays() => SetDays(_ => true);

    private void SetDays(Func<DayOfWeek, bool> predicate)
    {
        foreach (var day in RepeatDays)
        {
            day.IsOn = predicate(day.Day);
        }

        Repeat = RepeatKind.Weekly;
        OnPropertyChanged(nameof(ScheduleSummary));
    }

    [RelayCommand]
    private void AddTime()
    {
        var latest = AllTimes().Max();
        var next = latest.AddHours(1);
        var timeStr = TimeFormatting.FormatTime(next, Use24HourClock);
        var item = new ExtraTimeItem { TimeText = timeStr };
        item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ScheduleSummary));
        ExtraTimes.Add(item);
        OnPropertyChanged(nameof(HasExtraTimes));
        OnPropertyChanged(nameof(ScheduleSummary));
    }

    [RelayCommand]
    private void RemoveTime(object? time)
    {
        if (time is ExtraTimeItem item)
        {
            ExtraTimes.Remove(item);
            OnPropertyChanged(nameof(HasExtraTimes));
            OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    [RelayCommand]
    private void SetTime(string? time)
    {
        if (!string.IsNullOrWhiteSpace(time))
        {
            NewTime = time;
        }
    }

    private List<TimeOnly> AllTimes()
    {
        var times = new List<TimeOnly>();

        if (TimeFormatting.TryParse(NewTime, out var primary))
        {
            times.Add(primary);
        }

        foreach (var extra in ExtraTimes)
        {
            if (TimeFormatting.TryParse(extra.TimeText, out var t))
            {
                times.Add(t);
            }
        }

        return times.Count == 0 ? [new TimeOnly(9, 0)] : times.Distinct().Order().ToList();
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (string.IsNullOrWhiteSpace(NewBody))
        {
            ShowStatus("Type the reminder first.", true);
            return;
        }

        if (!TimeFormatting.TryParse(NewTime, out _))
        {
            ShowStatus(Use24HourClock ? "That time is not valid. Use HH:mm." : "That time is not valid. Use hh:mm AM/PM.", true);
            return;
        }

        if (Repeat == RepeatKind.Weekly && RepeatDays.All(d => !d.IsOn))
        {
            ShowStatus("Pick at least one day.", true);
            return;
        }

        IsBusy = true;

        try
        {
            var date = DateOnly.FromDateTime(NewDate);
            int? effectiveCount = IsRepeating && EndMode == RepeatEndMode.AfterOccurrences
                ? Math.Max(1, OccurrenceCount)
                : null;
            var rrule = Recurrence.ToRrule(
                Repeat,
                RepeatDays.Where(d => d.IsOn).Select(d => d.Day).ToList(),
                IntervalWeeks,
                date,
                effectiveCount);

            var timezone = ResolveLocalIanaTimezone();

            var until = rrule is not null && EndMode == RepeatEndMode.OnDate && Until is { } end
                ? new DateTimeOffset(DateTime.SpecifyKind(end.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Local))
                : (DateTimeOffset?)null;

            // Null means every PC, which is also what a server without the
            // capability does with any reminder it is sent.
            var deviceIds = IsTargetable && !ShowOnAllPcs
                ? Targets.Where(t => t.IsChosen).Select(t => t.DeviceId).ToList()
                : null;

            if (deviceIds is { Count: 0 })
            {
                ShowStatus("Pick at least one PC, or choose All PCs.", true);
                return;
            }

            var bodyText = NewBody.Trim();
            var allTimes = AllTimes();

            if (IsEditing && !string.IsNullOrEmpty(EditingReminderId))
            {
                var previous = Items.FirstOrDefault(r => r.Id == EditingReminderId);
                var updateReq = new UpdateReminderRequest(
                    Body: bodyText,
                    DueAt: ToUtcDueAt(date, allTimes[0]),
                    Timezone: timezone,
                    Rrule: rrule ?? string.Empty,
                    RecurrenceUntil: until,
                    DeviceIds: deviceIds,
                    ClearDeviceIds: deviceIds is null && previous?.DeviceIds is { Count: > 0 } ? true : null);

                var updated = await api.UpdateReminderAsync(EditingReminderId, updateReq);
                if (updated is not null)
                {
                    CancelEdit();
                    UpsertItem(updated);
                    if (previous is not null)
                    {
                        ShowStatus("Reminder updated.", false, async () =>
                        {
                            var revertReq = new UpdateReminderRequest(
                                Body: previous.Body,
                                DueAt: previous.DueAt,
                                Timezone: previous.Timezone,
                                Rrule: previous.Rrule ?? string.Empty,
                                RecurrenceUntil: previous.RecurrenceUntil,
                                DeviceIds: previous.DeviceIds,
                                ClearDeviceIds: previous.DeviceIds is null && updated.DeviceIds is { Count: > 0 } ? true : null);
                            var reverted = await api.UpdateReminderAsync(previous.Id, revertReq);
                            if (reverted is not null)
                            {
                                UpsertItem(reverted);
                                ShowStatus("Changes undone.", false);
                            }
                        });
                    }
                    else
                    {
                        ShowStatus("Reminder updated.", false);
                    }
                }
                return;
            }

            // The local wall time the user typed is converted to a UTC
            // instant here, with the machine's IANA zone travelling
            // alongside it. v1 stored a naive time and fired it at UK time
            // for everybody (S2-07).
            CreateReminderRequest BuildCreateRequest(TimeOnly time) => new(
                bodyText,
                ToUtcDueAt(date, time),
                timezone,
                rrule,
                until,
                deviceIds);

            List<ReminderResponse> created;
            if (allTimes.Count == 1)
            {
                var reminder = await api.CreateReminderAsync(BuildCreateRequest(allTimes[0]));
                created = reminder is not null ? [reminder] : [];
            }
            else
            {
                _ = await api.GetAccessTokenAsync();
                var results = await Task.WhenAll(allTimes.Select(t => api.CreateReminderAsync(BuildCreateRequest(t))));
                created = results.OfType<ReminderResponse>().ToList();
            }

            if (created.Count > 0)
            {
                ResetScheduleFields();
                UpsertItems(created);
                ShowStatus(created.Count == 1 ? "Reminder added." : $"{created.Count} reminders added.", false);
            }
        }
        catch (PcConnectApiException ex)
        {
            ShowStatus(ex.Message, true);
        }
        catch (HttpRequestException)
        {
            ShowStatus("Could not reach the PCConnect server.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static DateTimeOffset ToUtcDueAt(DateOnly date, TimeOnly time) =>
        new DateTimeOffset(DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Local)).ToUniversalTime();

    private static string ResolveLocalIanaTimezone() =>
        TimeZoneInfo.Local.HasIanaId
            ? TimeZoneInfo.Local.Id
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "Etc/UTC";

    [RelayCommand]
    public void StartEdit(ReminderRow? row)
    {
        if (row is null)
        {
            return;
        }

        DismissStatus();

        var reminder = Items.FirstOrDefault(r => r.Id == row.Id);
        if (reminder is null)
        {
            return;
        }

        IsEditing = true;
        EditingReminderId = reminder.Id;
        NewBody = reminder.Body;

        var localDue = reminder.DueAt.ToLocalTime();
        NewDate = localDue.Date;
        NewTime = TimeFormatting.FormatTime(localDue, Use24HourClock);
        ExtraTimes.Clear();
        OnPropertyChanged(nameof(HasExtraTimes));

        var (kind, days, interval) = Recurrence.FromRrule(reminder.Rrule);
        foreach (var dayToggle in RepeatDays)
        {
            dayToggle.IsOn = days.Contains(dayToggle.Day);
        }
        Repeat = kind;
        IntervalWeeks = interval;

        if (Recurrence.ParseCount(reminder.Rrule) is { } count)
        {
            OccurrenceCount = count;
            EndMode = RepeatEndMode.AfterOccurrences;
        }
        else if (reminder.RecurrenceUntil is { } recUntil)
        {
            Until = recUntil.ToLocalTime().DateTime;
            EndMode = RepeatEndMode.OnDate;
        }
        else
        {
            EndMode = RepeatEndMode.Never;
            Until = null;
        }

        ApplyTargetSelection(reminder.DeviceIds);
        NotifyFormScheduleChanged();
    }

    [RelayCommand]
    public void CancelEdit()
    {
        IsEditing = false;
        EditingReminderId = null;
        ResetScheduleFields();
        ApplyTargetSelection(null);
        DismissStatus();
        NotifyFormScheduleChanged();
    }

    private void ApplyTargetSelection(IReadOnlyList<string>? targetIds)
    {
        var hasTargets = targetIds is { Count: > 0 };
        ShowOnAllPcs = !hasTargets;
        foreach (var target in Targets)
        {
            target.IsChosen = hasTargets && targetIds!.Contains(target.DeviceId, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void NotifyFormScheduleChanged()
    {
        OnPropertyChanged(nameof(ScheduleSummary));
        OnPropertyChanged(nameof(DateLabel));
        OnPropertyChanged(nameof(PickedDateLabel));
    }

    private void ResetScheduleFields()
    {
        NewBody = string.Empty;
        ExtraTimes.Clear();
        OnPropertyChanged(nameof(HasExtraTimes));
        var nextDefault = DateTime.Now.AddHours(1);
        NewDate = nextDefault.Date;
        NewTime = TimeFormatting.FormatTime(nextDefault, Use24HourClock);
        Repeat = RepeatKind.Once;
        IntervalWeeks = 1;
        EndMode = RepeatEndMode.Never;
        OccurrenceCount = 5;
        Until = null;
        foreach (var day in RepeatDays)
        {
            day.IsOn = false;
        }
    }

    [RelayCommand]
    public async Task DeleteAsync(object? parameter)
    {
        var id = parameter switch
        {
            ReminderRow row => row.Id,
            string idString => idString,
            _ => EditingReminderId
        };

        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var snapshot = Items.FirstOrDefault(r => r.Id == id);
        var hasPastOccurrences = snapshot is not null && snapshot.DueAt <= DateTimeOffset.UtcNow;
        var isRecurringActiveWithPast = snapshot is not null &&
                                        !string.IsNullOrWhiteSpace(snapshot.Rrule) &&
                                        hasPastOccurrences &&
                                        !HasRecurringSeriesEnded(snapshot);
        var previousSnooze = snoozeService?.GetSnooze(id);
        var wasDismissed = snoozeService?.IsDismissed(id) == true;

        snoozeService?.CancelSnooze(id);
        snoozeService?.ClearDismissed(id);

        IsBusy = true;
        try
        {
            await api.DeleteReminderAsync(id);
            if (EditingReminderId == id)
            {
                CancelEdit();
            }

            if (isRecurringActiveWithPast)
            {
                var ended = snapshot! with
                {
                    RecurrenceUntil = DateTimeOffset.UtcNow,
                    IsCompleted = true,
                    CompletedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                UpsertItem(ended);
                ShowStatus("Future occurrences removed.", false, async () =>
                {
                    ReminderResponse? restored = null;
                    try
                    {
                        restored = await api.UpdateReminderAsync(id, new UpdateReminderRequest(
                            Rrule: snapshot.Rrule,
                            RecurrenceUntil: snapshot.RecurrenceUntil));
                        if (restored is not null && !snapshot.IsCompleted)
                        {
                            restored = await api.CompleteReminderAsync(id, false) ?? restored;
                        }
                    }
                    catch
                    {
                        // Fallback to restore if update failed
                    }

                    if (restored is not null)
                    {
                        UpsertItem(restored);
                        ShowStatus("Reminder restored.", false);
                    }
                });
            }
            else
            {
                RemoveItem(id);
                ShowStatus("Reminder deleted.", false, async () =>
                {
                    ReminderResponse? restored;
                    try
                    {
                        restored = await api.RestoreReminderAsync(id);
                    }
                    catch (PcConnectApiException ex) when (
                        snapshot is not null &&
                        ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
                    {
                        var recreated = await api.CreateReminderAsync(new CreateReminderRequest(
                            snapshot.Body,
                            snapshot.DueAt,
                            snapshot.Timezone,
                            snapshot.Rrule,
                            snapshot.RecurrenceUntil,
                            snapshot.DeviceIds));
                        restored = recreated is not null && snapshot.IsCompleted
                            ? (await api.CompleteReminderAsync(recreated.Id, true) ?? recreated)
                            : recreated;
                    }

                    if (restored is not null)
                    {
                        if (previousSnooze is not null)
                        {
                            var remaining = previousSnooze.SnoozedUntil - DateTimeOffset.Now;
                            if (remaining > TimeSpan.Zero)
                            {
                                snoozeService?.Snooze(restored.Id, restored.Body, restored.DueAt, remaining);
                            }
                        }
                        else if (wasDismissed)
                        {
                            snoozeService?.MarkDismissed(restored.Id);
                        }

                        UpsertItem(restored);
                        ShowStatus("Reminder restored.", false);
                    }
                });
            }
        }
        catch (PcConnectApiException ex)
        {
            ShowStatus(ex.Message, true);
        }
        catch (HttpRequestException)
        {
            ShowStatus("Could not reach the PCConnect server.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Func<ReminderRow, Task<ReopenChoice>>? RequestReopenChoice { get; set; }

    public async Task RescheduleForTodayAsync(string reminderId)
    {
        var previousSnooze = snoozeService?.GetSnooze(reminderId);
        var wasDismissed = snoozeService?.IsDismissed(reminderId) == true;

        snoozeService?.CancelSnooze(reminderId);
        snoozeService?.ClearDismissed(reminderId);

        var item = Items.FirstOrDefault(r => r.Id == reminderId);
        if (item is null)
        {
            return;
        }

        try
        {
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now.Date);
            if (!TimeOnly.TryParse(item.DueLocalTime, CultureInfo.CurrentCulture, out var time))
            {
                time = TimeOnly.FromDateTime(now.AddHours(1));
            }

            var localCandidate = DateTime.SpecifyKind(today.ToDateTime(time), DateTimeKind.Local);
            if (localCandidate <= now)
            {
                localCandidate = DateTime.SpecifyKind(now.AddHours(1), DateTimeKind.Local);
            }

            var updateReq = new UpdateReminderRequest(
                Body: item.Body,
                DueAt: new DateTimeOffset(localCandidate).ToUniversalTime(),
                Timezone: item.Timezone,
                Rrule: item.Rrule ?? string.Empty,
                RecurrenceUntil: item.RecurrenceUntil,
                DeviceIds: item.DeviceIds);

            var updated = await api.UpdateReminderAsync(item.Id, updateReq);
            if (updated is not null)
            {
                var reopened = await api.CompleteReminderAsync(item.Id, false);
                UpsertItem(reopened ?? updated);
                ShowStatus(
                    $"Reminder rescheduled for today at {TimeFormatting.FormatTime(localCandidate, Use24HourClock)}.",
                    false,
                    async () =>
                    {
                        var revertReq = new UpdateReminderRequest(
                            Body: item.Body,
                            DueAt: item.DueAt,
                            Timezone: item.Timezone,
                            Rrule: item.Rrule ?? string.Empty,
                            RecurrenceUntil: item.RecurrenceUntil,
                            DeviceIds: item.DeviceIds);
                        var reverted = await api.UpdateReminderAsync(item.Id, revertReq);
                        if (reverted is not null)
                        {
                            if (item.IsCompleted)
                            {
                                reverted = await api.CompleteReminderAsync(item.Id, true) ?? reverted;
                            }

                            if (previousSnooze is not null)
                            {
                                var remaining = previousSnooze.SnoozedUntil - DateTimeOffset.Now;
                                if (remaining > TimeSpan.Zero)
                                {
                                    snoozeService?.Snooze(reverted.Id, reverted.Body, reverted.DueAt, remaining);
                                }
                            }
                            else if (wasDismissed)
                            {
                                snoozeService?.MarkDismissed(reverted.Id);
                            }

                            UpsertItem(reverted);
                            ShowStatus("Changes undone.", false);
                        }
                    });
            }
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            ShowStatus("Could not reschedule that reminder.", true);
            logger.LogWarning(ex, "Reschedule failed");
        }
    }

    [RelayCommand]
    private async Task CompleteAsync(object? parameter)
    {
        var row = parameter as ReminderRow;
        var id = row?.Id ?? parameter as string;
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        row ??= Rows.FirstOrDefault(r => r.Id == id);
        var item = Items.FirstOrDefault(r => r.Id == id);
        if (row is null && item is null)
        {
            return;
        }

        var wasCompleted = row?.IsCompleted ?? item!.IsCompleted;
        var isPast = row?.IsPast ?? (item!.DueAt <= DateTimeOffset.UtcNow || item.IsCompleted);

        snoozeService?.CancelSnooze(id);
        snoozeService?.ClearDismissed(id);

        if (wasCompleted && isPast && RequestReopenChoice is not null)
        {
            var promptRow = row ?? new ReminderRow(
                id,
                TimeFormatting.FormatTime(item!.DueAt.ToLocalTime(), Use24HourClock),
                string.Empty,
                item.Body,
                DescribeTargets(item.DeviceIds),
                wasCompleted,
                isPast,
                IsRecurring: !string.IsNullOrEmpty(item.Rrule));

            var choice = await RequestReopenChoice(promptRow);
            if (choice == ReopenChoice.Cancel)
            {
                return;
            }

            if (choice == ReopenChoice.RescheduleForToday)
            {
                await RescheduleForTodayAsync(id);
                return;
            }

            if (choice == ReopenChoice.KeepOverdue)
            {
                snoozeService?.MarkDismissed(id);
            }
        }

        try
        {
            var updated = await api.CompleteReminderAsync(id, !wasCompleted);
            if (updated is not null)
            {
                UpsertItem(updated);
                ShowStatus(wasCompleted ? "Reminder reopened." : "Reminder completed.", false);
            }
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            ShowStatus("Could not update that reminder.", true);
            logger.LogWarning(ex, "Reminder completion failed");
        }
    }

    internal static bool HasRecurringSeriesEnded(ReminderResponse? reminder) =>
        ShellViewModel.HasRecurringSeriesEnded(reminder);
}
