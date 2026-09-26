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
            }
        }
    }
    private static readonly string[] DayInitials = ["M", "T", "W", "T", "F", "S", "S"];
    private bool _isSnoozeHooked;

    private DateOnly _viewMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    private DispatcherTimer? _statusTimer;
    private double _remainingSeconds;
    private const double TotalStatusDuration = 5.0;
    private const double TimerIntervalMs = 50.0;
    private bool _isStatusTimerPaused;

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
    private bool _isStatusError;

    [ObservableProperty]
    private double _statusProgress = 1.0;

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

    public string CompletedRemindersButtonText => ShowCompletedReminders ? "Hide completed reminders" : "Show completed reminders";

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

    public ObservableCollection<DayCell> Days { get; } = [];

    public ObservableCollection<RepeatChip> RepeatChips { get; } = [];

    public ObservableCollection<DayToggle> RepeatDays { get; } = [];

    /// <summary>Additional times, each of which becomes its own series.</summary>
    public ObservableCollection<TimeOnly> ExtraTimes { get; } = [];

    /// <summary>The PCs on the account, each with whether this reminder names it.</summary>
    public ObservableCollection<TargetToggle> Targets { get; } = [];

    public IReadOnlyList<int> IntervalOptions { get; } = [1, 2, 3, 4];

    public bool IsCustomRepeat => Repeat == RepeatKind.Custom;

    public bool HasSelection => _selectedDays.Count > 0;

    public bool HasMoreRows => Rows.Count < _allRows.Count;

    public bool IsCurrentMonth => _viewMonth.Year == DateTime.Today.Year && _viewMonth.Month == DateTime.Today.Month;

    public bool NoRows => Rows.Count == 0;

    public bool ShowOnChosenPcs => !ShowOnAllPcs;

    public string MonthTitle => _viewMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
    public DateOnly ViewMonth => _viewMonth;

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
        if (snoozeService is not null && !_isSnoozeHooked)
        {
            _isSnoozeHooked = true;
            snoozeService.ReminderSnoozed += info =>
            {
                void Update()
                {
                    ShowStatus($"Reminder snoozed for {info.FormattedDuration}.", false);
                    RebuildRows();
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess())
                {
                    Update();
                }
                else
                {
                    dispatcher.InvokeAsync(Update);
                }
            };

            snoozeService.ReminderUnsnoozed += _ =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess())
                {
                    RebuildRows();
                }
                else
                {
                    dispatcher.InvokeAsync(RebuildRows);
                }
            };

            snoozeService.ReminderDismissed += _ =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess())
                {
                    RebuildRows();
                }
                else
                {
                    dispatcher.InvokeAsync(RebuildRows);
                }
            };
        }

        if (settings is not null)
        {
            settings.TimeFormatChanged += () =>
            {
                void Update()
                {
                    OnPropertyChanged(nameof(Use24HourClock));
                    if (TimeFormatting.TryParse(NewTime, out var t))
                    {
                        NewTime = TimeFormatting.FormatTime(t, Use24HourClock);
                    }
                    RebuildRows();
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess())
                {
                    Update();
                }
                else
                {
                    dispatcher.InvokeAsync(Update);
                }
            };
        }

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

    public void ShowStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
        _remainingSeconds = TotalStatusDuration;
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
            StatusMessage = string.Empty;
            StatusProgress = 1.0;
        }
        else
        {
            StatusProgress = Math.Clamp(_remainingSeconds / TotalStatusDuration, 0.0, 1.0);
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
        StatusMessage = string.Empty;
        StatusProgress = 1.0;
        _isStatusTimerPaused = false;
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
            ShowStatus("Could not load your reminders.", true);
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

        var nextBatch = _allRows.Skip(Rows.Count).Take(PageSize).ToList();
        foreach (var row in nextBatch)
        {
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
        _allRows.Clear();

        var today = DateOnly.FromDateTime(DateTime.Today);

        if (HasSelection)
        {
            var days = _selectedDays.Order().ToList();
            foreach (var day in days)
            {
                foreach (var reminder in Items.OrderBy(r => r.DueAt.ToLocalTime().TimeOfDay))
                {
                    var seriesStart = DateOnly.FromDateTime(reminder.DueAt.ToLocalTime().Date);
                    if (!Recurrence.OccursOn(reminder.Rrule, seriesStart, day))
                    {
                        continue;
                    }
                    if (reminder.RecurrenceUntil is { } until && day > DateOnly.FromDateTime(until.ToLocalTime().Date))
                    {
                        continue;
                    }

                    var local = reminder.DueAt.ToLocalTime();
                    var isCompleted = string.IsNullOrEmpty(reminder.Rrule)
                        ? reminder.IsCompleted
                        : (reminder.IsCompleted && day == today);
                    ReminderSnoozeInfo? snoozeInfo = null;
                    var isSnoozed = snoozeService is not null && snoozeService.IsSnoozed(reminder.Id, out snoozeInfo);
                    var isDismissed = snoozeService is not null && snoozeService.IsDismissed(reminder.Id);
                    var isPastTime = day < today || (day == today && local.TimeOfDay <= DateTime.Now.TimeOfDay);
                    var past = !isSnoozed && (isPastTime || isCompleted);
                    var snoozeLabel = isSnoozed && snoozeInfo is not null ? $"Snoozed for {snoozeInfo.FormattedDuration}" : null;
                    var snoozeToolTip = isSnoozed && snoozeInfo is not null ? $"Snoozed for {snoozeInfo.FormattedDuration} (until {TimeFormatting.FormatTime(snoozeInfo.SnoozedUntil, Use24HourClock)})" : null;

                    _allRows.Add(new ReminderRow(
                        Id: reminder.Id,
                        Time: TimeFormatting.FormatTime(local, Use24HourClock),
                        DayLabel: Describe(day),
                        Body: reminder.Body,
                        Detail: string.Join(" · ", new[]
                        {
                            Recurrence.Describe(reminder.Rrule),
                            DescribeTargets(reminder.DeviceIds),
                        }.Where(s => s.Length > 0)),
                        IsCompleted: isCompleted,
                        IsPast: past,
                        IsSnoozed: isSnoozed,
                        SnoozeLabel: snoozeLabel,
                        SnoozeToolTip: snoozeToolTip,
                        IsDismissed: isDismissed));
                }
            }

            ListTitle = _selectedDays.Count == 1 ? Describe(days[0]) : $"{days.Count} days selected";
            HasCompletedReminders = false;
            OnPropertyChanged(nameof(HasPastReminders));
        }
        else
        {
            ListTitle = "All events";

            if (Items.Count > 0)
            {
                var earliestDate = Items.Select(r =>
                {
                    var start = DateOnly.FromDateTime(r.DueAt.ToLocalTime().Date);
                    return string.IsNullOrEmpty(r.Rrule)
                        ? start
                        : (start < today.AddDays(-30) ? today.AddDays(-30) : start);
                }).Min();

                var hasIndefiniteRecurring = Items.Any(r => !string.IsNullOrEmpty(r.Rrule) && r.RecurrenceUntil is null);
                var maxFixedDate = Items.Select(r => r.RecurrenceUntil is { } u
                    ? DateOnly.FromDateTime(u.ToLocalTime().Date)
                    : DateOnly.FromDateTime(r.DueAt.ToLocalTime().Date)).Max();

                var endDate = hasIndefiniteRecurring
                    ? (maxFixedDate > today.AddYears(1) ? maxFixedDate : today.AddYears(1))
                    : maxFixedDate;

                var pastCount = 0;
                for (var day = earliestDate; day <= endDate; day = day.AddDays(1))
                {
                    foreach (var reminder in Items.OrderBy(r => r.DueAt.ToLocalTime().TimeOfDay))
                    {
                        var seriesStart = DateOnly.FromDateTime(reminder.DueAt.ToLocalTime().Date);
                        if (reminder.Rrule is not null && day < seriesStart)
                        {
                            continue;
                        }
                        if (reminder.RecurrenceUntil is { } until && day > DateOnly.FromDateTime(until.ToLocalTime().Date))
                        {
                            continue;
                        }
                        if (!Recurrence.OccursOn(reminder.Rrule, seriesStart, day))
                        {
                            continue;
                        }

                        var local = reminder.DueAt.ToLocalTime();
                        var isCompleted = string.IsNullOrEmpty(reminder.Rrule)
                            ? reminder.IsCompleted
                            : (reminder.IsCompleted && day == today);
                        ReminderSnoozeInfo? snoozeInfo = null;
                        var isSnoozed = snoozeService is not null && snoozeService.IsSnoozed(reminder.Id, out snoozeInfo);
                        var isDismissed = snoozeService is not null && snoozeService.IsDismissed(reminder.Id);
                        var isPastTime = day < today || (day == today && local.TimeOfDay <= DateTime.Now.TimeOfDay);
                        var isFilteredPast = !isSnoozed && (day < today || isCompleted);
                        var past = !isSnoozed && (isPastTime || isCompleted);

                        if (isFilteredPast)
                        {
                            pastCount++;
                            if (!ShowCompletedReminders)
                            {
                                continue;
                            }
                        }

                        var snoozeLabel = isSnoozed && snoozeInfo is not null ? $"Snoozed for {snoozeInfo.FormattedDuration}" : null;
                        var snoozeToolTip = isSnoozed && snoozeInfo is not null ? $"Snoozed for {snoozeInfo.FormattedDuration} (until {TimeFormatting.FormatTime(snoozeInfo.SnoozedUntil, Use24HourClock)})" : null;

                        _allRows.Add(new ReminderRow(
                            Id: reminder.Id,
                            Time: TimeFormatting.FormatTime(local, Use24HourClock),
                            DayLabel: Describe(day),
                            Body: reminder.Body,
                            Detail: string.Join(" · ", new[]
                            {
                                Recurrence.Describe(reminder.Rrule),
                                DescribeTargets(reminder.DeviceIds),
                            }.Where(s => s.Length > 0)),
                            IsCompleted: isCompleted,
                            IsPast: past,
                            IsSnoozed: isSnoozed,
                            SnoozeLabel: snoozeLabel,
                            SnoozeToolTip: snoozeToolTip,
                            IsDismissed: isDismissed));
                    }
                }

                HasCompletedReminders = pastCount > 0;
                OnPropertyChanged(nameof(HasPastReminders));
            }
            else
            {
                HasCompletedReminders = false;
                OnPropertyChanged(nameof(HasPastReminders));
            }
        }

        foreach (var row in _allRows.Take(PageSize))
        {
            Rows.Add(row);
        }

        UpdateListSummary();
        OnPropertyChanged(nameof(NoRows));
        OnPropertyChanged(nameof(HasMoreRows));
        RowsReset?.Invoke();
    }

    private void UpdateListSummary()
    {
        ListSummary = _allRows.Count == 1 ? "1 reminder" : $"{_allRows.Count} reminders";
    }


    /// <summary>"all PCs", or the names of the ones it was aimed at.</summary>
    internal string DescribeTargets(IReadOnlyList<string>? deviceIds)
    {
        if (deviceIds is null || deviceIds.Count == 0)
        {
            return "all PCs";
        }

        var names = deviceIds
            .Select(id => devices.Items.FirstOrDefault(d => d.Id == id)?.DisplayName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();

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

        times.AddRange(ExtraTimes);

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

        if (Repeat == RepeatKind.Custom && RepeatDays.All(d => !d.IsOn))
        {
            ShowStatus("Pick at least one day.", true);
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

            if (IsEditing && !string.IsNullOrEmpty(EditingReminderId))
            {
                var times = AllTimes();
                var local = DateTime.SpecifyKind(date.ToDateTime(times[0]), DateTimeKind.Local);
                var updateReq = new UpdateReminderRequest(
                    Body: NewBody.Trim(),
                    DueAt: new DateTimeOffset(local).ToUniversalTime(),
                    Timezone: timezone,
                    Rrule: rrule ?? string.Empty,
                    RecurrenceUntil: until,
                    DeviceIds: deviceIds);

                var updated = await api.UpdateReminderAsync(EditingReminderId, updateReq);
                if (updated is not null)
                {
                    CancelEdit();
                    await LoadAsync();
                    ShowStatus("Reminder updated.", false);
                }
                return;
            }

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
                    until,
                    deviceIds));

                if (reminder is not null)
                {
                    created++;
                }
            }

            if (created > 0)
            {
                NewBody = string.Empty;
                ExtraTimes.Clear();
                NewDate = DateTime.Today;
                NewTime = TimeFormatting.FormatTime(DateTime.Now.AddHours(1), Use24HourClock);
                await LoadAsync();
                ShowStatus(created == 1 ? "Reminder added." : $"{created} reminders added.", false);
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

        var (kind, days, interval) = Recurrence.FromRrule(reminder.Rrule);
        Repeat = kind;
        IntervalWeeks = interval;

        foreach (var chip in RepeatChips)
        {
            chip.IsSelected = chip.Kind == kind;
        }

        foreach (var dayToggle in RepeatDays)
        {
            dayToggle.IsOn = days.Contains(dayToggle.Day);
        }

        Until = reminder.RecurrenceUntil?.ToLocalTime().DateTime;

        if (reminder.DeviceIds is { Count: > 0 } targetIds)
        {
            ShowOnAllPcs = false;
            foreach (var target in Targets)
            {
                target.IsChosen = targetIds.Contains(target.DeviceId, StringComparer.OrdinalIgnoreCase);
            }
        }
        else
        {
            ShowOnAllPcs = true;
            foreach (var target in Targets)
            {
                target.IsChosen = false;
            }
        }

        DismissStatus();
        OnPropertyChanged(nameof(ScheduleSummary));
        OnPropertyChanged(nameof(EndsLabel));
        OnPropertyChanged(nameof(DateLabel));
        OnPropertyChanged(nameof(PickedDateLabel));
    }

    [RelayCommand]
    public void CancelEdit()
    {
        IsEditing = false;
        EditingReminderId = null;
        NewBody = string.Empty;
        ExtraTimes.Clear();
        NewDate = DateTime.Today;
        NewTime = TimeFormatting.FormatTime(DateTime.Now.AddHours(1), Use24HourClock);
        Repeat = RepeatKind.Once;
        IntervalWeeks = 1;
        Until = null;
        foreach (var chip in RepeatChips)
        {
            chip.IsSelected = chip.Kind == RepeatKind.Once;
        }
        foreach (var day in RepeatDays)
        {
            day.IsOn = false;
        }
        ShowOnAllPcs = true;
        foreach (var target in Targets)
        {
            target.IsChosen = false;
        }
        DismissStatus();
        OnPropertyChanged(nameof(ScheduleSummary));
        OnPropertyChanged(nameof(EndsLabel));
        OnPropertyChanged(nameof(DateLabel));
        OnPropertyChanged(nameof(PickedDateLabel));
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
            await LoadAsync();
            ShowStatus("Reminder deleted.", false);
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
                Rrule: item.Rrule,
                RecurrenceUntil: item.RecurrenceUntil,
                DeviceIds: item.DeviceIds);

            var updated = await api.UpdateReminderAsync(item.Id, updateReq);
            if (updated is not null)
            {
                await api.CompleteReminderAsync(item.Id, false);
                await LoadAsync();
                ShowStatus($"Reminder rescheduled for today at {TimeFormatting.FormatTime(localCandidate, Use24HourClock)}.", false);
            }
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            ShowStatus("Could not reschedule that reminder.", true);
            logger.LogWarning(ex, "Reschedule failed");
        }
    }

    [RelayCommand]
    private async Task CompleteAsync(ReminderRow? row)
    {
        if (row is null)
        {
            return;
        }

        snoozeService?.CancelSnooze(row.Id);
        snoozeService?.ClearDismissed(row.Id);

        if (row.IsCompleted && row.IsPast && RequestReopenChoice is not null)
        {
            var choice = await RequestReopenChoice(row);
            if (choice == ReopenChoice.Cancel)
            {
                return;
            }

            if (choice == ReopenChoice.RescheduleForToday)
            {
                await RescheduleForTodayAsync(row.Id);
                return;
            }

            if (choice == ReopenChoice.KeepOverdue)
            {
                snoozeService?.MarkDismissed(row.Id);
            }
        }

        try
        {
            var updated = await api.CompleteReminderAsync(row.Id, !row.IsCompleted);
            if (updated is not null)
            {
                await LoadAsync();
                ShowStatus(row.IsCompleted ? "Reminder reopened." : "Reminder completed.", false);
            }
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            ShowStatus("Could not update that reminder.", true);
            logger.LogWarning(ex, "Reminder completion failed");
        }
    }
}
