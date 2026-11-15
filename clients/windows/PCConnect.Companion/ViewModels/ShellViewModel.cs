using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PCConnect.Client;
using PCConnect.Agent.Execution;
using PCConnect.Companion.Services;
using PCConnect.Core.Contracts;
using PCConnect.Core.Domain;

namespace PCConnect.Companion.ViewModels;

public partial class ShellViewModel(
    PcConnectClient api,
    PcConnectRealtimeClient realtime,
    CompanionSettings settings,
    ProvisioningPipeClient provisioning,
    DevicesViewModel devices,
    RemindersViewModel reminders,
    AccountViewModel account,
    SettingsViewModel appSettings,
    ILogger<ShellViewModel> logger,
    ReminderSnoozeService? snoozeService = null) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFloatingReminderToast))]
    private bool _isSignedIn;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _login = string.Empty;

    [ObservableProperty]
    private string _updateNotice = string.Empty;

    [ObservableProperty]
    private bool _isRealtimeConnected;

    /// <summary>Which of the four sidebar pages is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFloatingReminderToast))]
    private CompanionPage _page = CompanionPage.ThisPc;

    /// <summary>The PC this app is running on, once it has been recognised.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThisPcName))]
    [NotifyPropertyChangedFor(nameof(HasThisPc))]
    private DeviceItem? _thisPc;

    [ObservableProperty]
    private string _weekSummary = string.Empty;

    [ObservableProperty]
    private DayColumn _todayColumn = new(DateTime.Today.ToString("ddd", CultureInfo.CurrentCulture), "", true, []);

    [ObservableProperty]
    private string _lastCommandSummary = "Nothing yet";

    [ObservableProperty]
    private bool _isAccountMenuOpen;

    [ObservableProperty]
    private EventDetailModalViewModel? _activeEventDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFloatingReminderToast))]
    private bool _isEventDetailOpen;

    public bool ShowFloatingReminderToast =>
        IsSignedIn &&
        !string.IsNullOrEmpty(reminders.StatusMessage) &&
        (Page != CompanionPage.Reminders || IsEventDetailOpen);

    public event Action? EventDetailOpenRequested;
    public event Action? EventDetailCloseRequested;

    public DevicesViewModel Devices => devices;

    public RemindersViewModel Reminders => reminders;

    public AccountViewModel Account => account;

    public SettingsViewModel AppSettings => appSettings;

    /// <summary>The seven days of reminders that will show on this screen.</summary>
    public ObservableCollection<DayColumn> Week { get; } = [];

    public string ThisPcName => ThisPc?.DisplayName ?? Environment.MachineName;

    public bool HasThisPc => ThisPc is not null;

    public string Today => $"{DateTime.Now.ToString("dddd d MMMM", CultureInfo.CurrentCulture)} · {TimeFormatting.FormatTime(DateTime.Now, settings.Use24HourClock)}";

    private DispatcherTimer? _clockTimer;
    private string _lastFormattedToday = string.Empty;
    private DateOnly _lastDate = DateOnly.FromDateTime(DateTime.Today);
    private bool _isRemindersHooked;
    private bool _isDevicesHooked;
    private bool _isLiveDeviceRefreshHooked;
    private bool _isSettingsHooked;
    private bool _isRealtimeSubscribed;

    public void WarmSession()
    {
        if (IsSignedIn)
        {
            _ = api.WarmAccessTokenAsync();
        }
    }

    private void EnsureRemindersHooked()
    {
        if (_isRemindersHooked)
        {
            return;
        }

        _isRemindersHooked = true;
        reminders.ItemsChanged += () => UiDispatcher.Run(() =>
        {
            RebuildWeek();
            if (IsSignedIn)
            {
                _ = RefreshWeekOccurrencesAsync();
            }
        });
        reminders.OccurrencesChanged += () => UiDispatcher.Run(RebuildWeek);
        reminders.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RemindersViewModel.StatusMessage))
            {
                UiDispatcher.Run(() => OnPropertyChanged(nameof(ShowFloatingReminderToast)));
            }
        };
    }

    public void StartClock()
    {
        if (_clockTimer is not null) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => OnClockTick(), dispatcher);
        _clockTimer.Start();
    }

    public void StopClock()
    {
        _clockTimer?.Stop();
        _clockTimer = null;
    }

    public void OnClockTick()
    {
        var currentFormatted = Today;
        if (currentFormatted != _lastFormattedToday)
        {
            _lastFormattedToday = currentFormatted;
            OnPropertyChanged(nameof(Today));
        }

        var currentDate = DateOnly.FromDateTime(DateTime.Today);
        if (currentDate != _lastDate)
        {
            _lastDate = currentDate;
            RebuildWeek();
        }
    }

    /// <summary>
    /// Startup: resolve the backend, check whether this build is still
    /// supported, and restore the session if there is one (06 §1).
    /// </summary>
    public async Task InitialiseAsync()
    {
        EnsureRemindersHooked();
        settings.Load();
        appSettings.Load();
        StartClock();

        if (!_isSettingsHooked)
        {
            _isSettingsHooked = true;
            settings.TimeFormatChanged += () => UiDispatcher.Run(() =>
            {
                OnPropertyChanged(nameof(Today));
                RefreshLastCommand();
                RebuildWeek();
            });

            api.SignedOut += () => _ = UiDispatcher.RunAsync(async () =>
            {
                IsSignedIn = false;
                IsAccountMenuOpen = false;
                StatusMessage = "Your session has ended. Sign in again.";

                try { await realtime.DisposeAsync(); }
                catch { /* best-effort */ }
            });
        }

        try
        {
            var discovery = await api.GetDiscoveryAsync();

            // The reminder sheet only offers "Choose PCs" when the server can
            // actually honour it (04 §2).
            reminders.IsTargetable = discovery?.Capabilities.Contains("reminders.targets") ?? false;

            if (discovery is not null &&
                PcConnectClient.IsBelowMinimum(discovery, "desktop", api.Options.ClientVersion))
            {
                UpdateNotice =
                    $"This version of PCConnect ({api.Options.ClientVersion}) is no longer supported. " +
                    $"Install {discovery.RecommendedClient.GetValueOrDefault("desktop", "the latest version")} to keep using it.";
            }
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            StatusMessage = "Could not reach the PCConnect server.";
            logger.LogWarning(ex, "Discovery failed at startup");
        }

        try
        {
            // Restoring a session needs the network, so it fails the same way
            // discovery does when the server is unreachable. Left unguarded it
            // took the whole app down on startup rather than showing the sign-in
            // screen and the reason.
            if (await api.GetAccessTokenAsync() is not null)
            {
                await OnSignedInAsync();
            }
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException or TaskCanceledException)
        {
            StatusMessage = "Could not reach the PCConnect server.";
            logger.LogWarning(ex, "Could not restore the session at startup");
        }
    }

    [RelayCommand]
    private void Navigate(CompanionPage page)
    {
        Page = page;
        IsAccountMenuOpen = false;
    }

    [RelayCommand]
    private void ToggleAccountMenu() => IsAccountMenuOpen = !IsAccountMenuOpen;

    [RelayCommand]
    private async Task SignInAsync(object? parameter)
    {
        var password = parameter as string ?? string.Empty;

        if (string.IsNullOrWhiteSpace(Login) || password.Length == 0)
        {
            StatusMessage = "Enter your username and password.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            // The plaintext goes over TLS. This client does not hash it: while a
            // client hashes, the hash is the password (S1-03).
            await api.LoginAsync(Login.Trim(), password);
            await OnSignedInAsync();
        }
        catch (PcConnectApiException ex)
        {
            StatusMessage = ex.Message;
            logger.LogInformation("Sign-in failed: {Code}", ex.Code);
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
    private async Task SignOutAsync()
    {
        await api.LogoutAsync();
        IsSignedIn = false;
        IsAccountMenuOpen = false;
        StatusMessage = "Signed out.";
    }

    private async Task OnSignedInAsync()
    {
        EnsureRemindersHooked();
        IsSignedIn = true;

        await account.LoadAsync();
        await devices.LoadAsync();
        await reminders.LoadAsync();
        await RefreshWeekOccurrencesAsync();

        await ResolveThisPcAsync();
        RebuildWeek();
        reminders.RefreshTargets();

        // This host holds a user credential, not a device one, so recovering
        // means re-reading what the window shows — never claiming commands,
        // which is a device's job and would be refused.
        realtime.RecoverState = _ => UiDispatcher.RunAsync(async () =>
        {
            await devices.LoadAsync();
            await reminders.LoadAsync();
            await RefreshWeekOccurrencesAsync();
            await ResolveThisPcAsync();
            RebuildWeek();
            reminders.RefreshTargets();
        });

        if (!_isDevicesHooked)
        {
            _isDevicesHooked = true;
            devices.Refreshed += () => UiDispatcher.Run(() =>
            {
                RefreshLastCommand();
                reminders.RefreshTargets();
            });
        }

        if (!_isLiveDeviceRefreshHooked)
        {
            _isLiveDeviceRefreshHooked = true;
            reminders.ItemsChanged += () =>
            {
                if (IsSignedIn)
                {
                    _ = UiDispatcher.RunAsync(async () =>
                    {
                        await devices.LoadAsync();
                        await ResolveThisPcAsync();
                    });
                }
            };
        }

        if (!_isRealtimeSubscribed)
        {
            _isRealtimeSubscribed = true;

            // Subscribed before connecting, so the first transition is not missed.
            realtime.ConnectionStateChanged += connected =>
                UiDispatcher.Run(() => IsRealtimeConnected = connected);

            realtime.DevicePresenceChanged += presence =>
                UiDispatcher.RunAsync(() =>
                {
                    devices.ApplyPresence(presence);
                    reminders.ApplyPresence(presence);
                });

            realtime.CommandStatusChanged += status =>
                UiDispatcher.RunAsync(async () =>
                {
                    devices.ApplyCommandStatus(status);
                    await devices.LoadAsync();
                    RefreshLastCommand();
                });

            realtime.ReminderChanged += change =>
                UiDispatcher.RunAsync(async () =>
                {
                    await reminders.ApplyReminderChangedAsync(change);
                    if (!string.Equals(change.Type, "deleted", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(change.ReminderId))
                    {
                        await RefreshReminderOccurrencesAsync(change.ReminderId);
                        if (IsEventDetailOpen &&
                            ActiveEventDetail is { IsRecurring: true } detail &&
                            string.Equals(detail.Id, change.ReminderId, StringComparison.Ordinal))
                        {
                            var rem = reminders.Items.FirstOrDefault(r => r.Id == change.ReminderId);
                            await LoadOccurrencesForActiveDetailAsync(
                                detail,
                                change.ReminderId,
                                rem?.Rrule,
                                rem?.DueAt,
                                rem?.Body ?? detail.Title,
                                rem?.RecurrenceUntil);
                        }
                    }
                });
        }

        try
        {
            await realtime.StartAsync();
            IsRealtimeConnected = realtime.IsConnected;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
        {
            // Not fatal: everything the window shows also works over HTTP.
            logger.LogWarning(ex, "Realtime connection failed; the companion will still work over HTTP");
        }

        RefreshLastCommand();
    }

    /// <summary>
    /// Works out which device this machine is, and adds it to the account if it
    /// is not on one yet.
    ///
    /// The agent is asked rather than guessed at: it is the process that holds
    /// the device credential, so it is the only thing that actually knows. If it
    /// is running and unclaimed, signing in here proves ownership and adds the
    /// PC now (ADR-0013). A PC can only be added by signing in on that PC.
    /// </summary>
    public async Task ResolveThisPcAsync()
    {
        var agent = await provisioning.WhoAmIAsync();

        if (agent.NeedsProvisioning)
        {
            await ProvisionThisPcAsync();
            agent = await provisioning.WhoAmIAsync();
        }
        else if (agent.DeviceId is { Length: > 0 } foreignId && !devices.Items.Any(d => d.Id == foreignId))
        {
            // The agent on this PC holds credentials for a device not in the signed-in user's account
            // (e.g. from a previous test user or wiped DB). Since the user is interactively logged in,
            // re-provision for this account.
            logger.LogInformation("Agent has device {DeviceId} which does not belong to signed-in account. Re-provisioning.", foreignId);
            if (await provisioning.UnregisterAsync())
            {
                await ProvisionThisPcAsync();
                agent = await provisioning.WhoAmIAsync();
            }
        }

        if (agent.DeviceId is { Length: > 0 } identified)
        {
            settings.ThisDeviceId = identified;
        }

        ThisPc = devices.Items.FirstOrDefault(d => d.Id == settings.ThisDeviceId)
            // Only as a fallback for a PC registered before provisioning
            // existed: a name is a label, never an identity (S1-08).
            ?? devices.Items.FirstOrDefault(d =>
                string.Equals(d.DisplayName, Environment.MachineName, StringComparison.OrdinalIgnoreCase));

        if (ThisPc is not null)
        {
            settings.ThisDeviceId = ThisPc.Id;
        }

        devices.SetThisPc(ThisPc?.Id);
        await appSettings.AttachAsync(ThisPc);
        OnPropertyChanged(nameof(ThisPcName));
        OnPropertyChanged(nameof(HasThisPc));
    }

    [RelayCommand]
    public async Task JoinThisPcAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await provisioning.UnregisterAsync();
            await ProvisionThisPcAsync();
            await ResolveThisPcAsync();
            reminders.RefreshTargets();
            StatusMessage = HasThisPc ? "This PC was joined to your account." : "Could not join this PC.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to join this PC");
            StatusMessage = "Failed to join this PC.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ProvisionThisPcAsync()
    {
        try
        {
            var provisioned = await api.ProvisionDeviceAsync(Environment.MachineName, api.Options.ClientVersion);
            if (provisioned is null)
            {
                return;
            }

            // The ticket is all the companion ever holds. The agent redeems it
            // and keeps the device secret, which this process must never see.
            var deviceId = await provisioning.ProvisionAsync(provisioned.ProvisioningTicket);

            if (deviceId is null)
            {
                logger.LogWarning("This PC was registered but the agent did not collect its credential");
                return;
            }

            logger.LogInformation("This PC was added to the account as {DisplayName}", provisioned.DisplayName);
            StatusMessage = $"This PC was added as {provisioned.DisplayName}.";

            await devices.LoadAsync();
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            logger.LogWarning(ex, "Could not add this PC automatically");
        }
    }

    /// <summary>The Monday–Sunday week strip on "This PC": what will appear on this screen.</summary>
    public void RebuildWeek()
    {
        Week.Clear();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var daysFromMonday = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.AddDays(-daysFromMonday);
        var total = 0;

        var deviceLookup = reminders.BuildDeviceLookup();
        var projected = new List<(
            ReminderResponse Reminder,
            DateTimeOffset Local,
            string RecurrenceText,
            string TargetText,
            bool IsRecurring,
            bool IsSnoozed,
            string? SnoozeLabel,
            bool IsDismissed)>(reminders.Items.Count);

        foreach (var r in reminders.Items)
        {
            var local = r.DueAt.ToLocalTime();
            var isRecurring = !string.IsNullOrEmpty(r.Rrule);
            ReminderSnoozeInfo? sInfo = null;
            var isSnoozed = snoozeService is not null && snoozeService.IsSnoozed(r.Id, out sInfo);
            var isDismissed = snoozeService is not null && snoozeService.IsDismissed(r.Id);

            projected.Add((
                Reminder: r,
                Local: local,
                RecurrenceText: isRecurring
                    ? Recurrence.Describe(r.Rrule, local, settings.Use24HourClock)
                    : string.Empty,
                TargetText: reminders.DescribeTargets(r.DeviceIds, deviceLookup),
                IsRecurring: isRecurring,
                IsSnoozed: isSnoozed,
                SnoozeLabel: isSnoozed && sInfo is not null ? $"Snoozed for {sInfo.FormattedDuration}" : null,
                IsDismissed: isDismissed));
        }

        for (var offset = 0; offset < 7; offset++)
        {
            var day = monday.AddDays(offset);
            var isToday = day == today;
            var shortDay = day.ToString("ddd", CultureInfo.CurrentCulture);
            var fullDate = day.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);
            var dayOccurrences = new List<(TimeSpan SortTime, WeekItem Item)>();

            foreach (var entry in projected)
            {
                var instants = Recurrence.OccurrencesOn(
                    entry.Reminder.Rrule,
                    entry.Reminder.DueAt,
                    day,
                    entry.Reminder.RecurrenceUntil,
                    entry.Reminder.Timezone);

                foreach (var occLocal in instants)
                {
                    var body = entry.Reminder.Body;
                    bool isCompleted;

                    if (entry.IsRecurring)
                    {
                        if (reminders.TryGetOccurrence(entry.Reminder.Id, occLocal, out var cachedOcc) && cachedOcc is not null)
                        {
                            if (string.Equals(cachedOcc.Status, "skipped", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            isCompleted = string.Equals(cachedOcc.Status, "completed", StringComparison.OrdinalIgnoreCase);
                            if (!string.IsNullOrWhiteSpace(cachedOcc.EffectiveBody))
                            {
                                body = cachedOcc.EffectiveBody;
                            }
                        }
                        else
                        {
                            isCompleted = false;
                        }
                    }
                    else
                    {
                        isCompleted = entry.Reminder.IsCompleted;
                    }

                    dayOccurrences.Add((
                        occLocal.TimeOfDay,
                        new WeekItem(
                            Time: TimeFormatting.FormatTime(occLocal, settings.Use24HourClock),
                            Body: body,
                            Id: entry.Reminder.Id,
                            DayLabel: shortDay,
                            FullDate: fullDate,
                            RecurrenceText: entry.RecurrenceText,
                            TargetText: entry.TargetText,
                            IsCompleted: isCompleted,
                            IsSnoozed: entry.IsSnoozed,
                            SnoozeLabel: entry.SnoozeLabel,
                            IsDismissed: entry.IsDismissed)));
                }
            }

            dayOccurrences.Sort((a, b) => a.SortTime.CompareTo(b.SortTime));
            var items = dayOccurrences.Select(static x => x.Item).ToList();

            total += items.Count;

            Week.Add(new DayColumn(
                Label: shortDay,
                Number: day.ToString("d MMM", CultureInfo.CurrentCulture),
                IsToday: isToday,
                Items: items));
        }

        WeekSummary = total == 1 ? "1 reminder this week" : $"{total} reminders this week";
        TodayColumn = Week.FirstOrDefault(d => d.IsToday) ?? new DayColumn(DateTime.Today.ToString("ddd", CultureInfo.CurrentCulture), "", true, []);
    }

    public async Task RefreshWeekOccurrencesAsync()
    {
        var recurringIds = reminders.Items
            .Where(static r => !string.IsNullOrEmpty(r.Rrule))
            .Select(static r => r.Id)
            .ToList();

        if (recurringIds.Count == 0)
        {
            return;
        }

        var (weekStartUtc, weekEndUtc) = CurrentWeekBoundsUtc();
        foreach (var id in recurringIds)
        {
            await FetchAndCacheOccurrencesAsync(id, weekStartUtc, weekEndUtc);
        }
    }

    private async Task RefreshReminderOccurrencesAsync(string reminderId)
    {
        var rem = reminders.Items.FirstOrDefault(r => r.Id == reminderId);
        if (rem is null || string.IsNullOrEmpty(rem.Rrule))
        {
            return;
        }

        var (weekStartUtc, weekEndUtc) = CurrentWeekBoundsUtc();
        await FetchAndCacheOccurrencesAsync(reminderId, weekStartUtc, weekEndUtc);
    }

    private async Task FetchAndCacheOccurrencesAsync(string reminderId, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        try
        {
            var items = await api.GetReminderOccurrencesAsync(reminderId, from: fromUtc, to: toUtc, limit: 100);
            if (items is { Count: > 0 })
            {
                UiDispatcher.Run(() => reminders.SetOccurrences(reminderId, items));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not fetch week occurrences for {ReminderId}", reminderId);
        }
    }

    private static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) CurrentWeekBoundsUtc()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var daysFromMonday = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.AddDays(-daysFromMonday);
        var sunday = monday.AddDays(6);
        var startLocal = new DateTimeOffset(DateTime.SpecifyKind(monday.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local));
        var endLocal = new DateTimeOffset(DateTime.SpecifyKind(sunday.ToDateTime(new TimeOnly(23, 59, 59)), DateTimeKind.Local));
        return (startLocal.ToUniversalTime(), endLocal.ToUniversalTime());
    }

    internal static bool HasRecurringSeriesEnded(ReminderResponse? reminder) =>
        reminder?.RecurrenceUntil is { } until &&
        (until <= DateTimeOffset.UtcNow || DateOnly.FromDateTime(until.ToLocalTime().Date) < DateOnly.FromDateTime(DateTime.Today));

    private void RefreshLastCommand()
    {
        var last = devices.RecentCommands.FirstOrDefault();

        LastCommandSummary = last is null
            ? "Nothing yet"
            : $"{Describe(last.Type)} · {TimeFormatting.FormatTime(last.IssuedAt, settings.Use24HourClock)}";
    }

    [RelayCommand]
    public void ShowEventDetail(object? item)
    {
        switch (item)
        {
            case WeekItem wi:
            {
                var reminder = reminders.Items.FirstOrDefault(r => r.Id == wi.Id);
                var isRecurring = !string.IsNullOrWhiteSpace(wi.RecurrenceText) || !string.IsNullOrEmpty(reminder?.Rrule);
                var recurrence = isRecurring
                    ? (!string.IsNullOrWhiteSpace(wi.RecurrenceText) ? wi.RecurrenceText : (reminder is not null
                        ? Recurrence.Describe(reminder.Rrule, reminder.DueAt.ToLocalTime(), settings.Use24HourClock)
                        : "Does not repeat"))
                    : "Does not repeat";

                OpenEventDetail(
                    id: wi.Id,
                    title: isRecurring && reminder is not null ? reminder.Body : wi.Body,
                    timeAndDate: isRecurring ? recurrence : $"{wi.FullDate} · {wi.Time}",
                    recurrence: recurrence,
                    targets: string.IsNullOrWhiteSpace(wi.TargetText)
                        ? (reminder is not null ? reminders.DescribeTargets(reminder.DeviceIds) : "All PCs")
                        : wi.TargetText,
                    isCompleted: isRecurring ? HasRecurringSeriesEnded(reminder) : wi.IsCompleted,
                    isSnoozed: wi.IsSnoozed,
                    snoozeLabel: wi.SnoozeLabel,
                    isDismissed: wi.IsDismissed,
                    isRecurring: isRecurring,
                    reminder: reminder);
                break;
            }
            case ReminderRow rr:
            {
                var reminder = reminders.Items.FirstOrDefault(r => r.Id == rr.Id);
                var isRecurring = !string.IsNullOrEmpty(reminder?.Rrule) || rr.IsRecurring;
                var recurrenceDesc = isRecurring && reminder is not null
                    ? Recurrence.Describe(reminder.Rrule, reminder.DueAt.ToLocalTime(), settings.Use24HourClock)
                    : (reminder is not null ? Recurrence.Describe(reminder.Rrule) : "Does not repeat");
                var fullDate = reminder is not null
                    ? reminder.DueAt.ToLocalTime().ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture)
                    : rr.DayLabel;

                OpenEventDetail(
                    id: rr.Id,
                    title: isRecurring && reminder is not null ? reminder.Body : rr.Body,
                    timeAndDate: isRecurring ? recurrenceDesc : $"{fullDate} · {rr.Time}",
                    recurrence: recurrenceDesc,
                    targets: reminder is not null ? reminders.DescribeTargets(reminder.DeviceIds) : "All PCs",
                    isCompleted: isRecurring ? HasRecurringSeriesEnded(reminder) : rr.IsCompleted,
                    isSnoozed: rr.IsSnoozed,
                    snoozeLabel: rr.SnoozeLabel,
                    isDismissed: rr.IsDismissed,
                    isRecurring: isRecurring,
                    reminder: reminder);
                break;
            }
        }
    }

    private void OpenEventDetail(
        string id,
        string title,
        string timeAndDate,
        string recurrence,
        string targets,
        bool isCompleted,
        bool isSnoozed,
        string? snoozeLabel,
        bool isDismissed,
        bool isRecurring,
        ReminderResponse? reminder)
    {
        var (statusText, statusTone) = DescribeEventStatus(isCompleted, isSnoozed, snoozeLabel, isDismissed);
        ActiveEventDetail = new EventDetailModalViewModel
        {
            Id = id,
            Title = title,
            TimeAndDate = timeAndDate,
            Recurrence = recurrence,
            Targets = targets,
            StatusText = statusText,
            StatusTone = statusTone,
            IsCompleted = isCompleted,
            IsSnoozed = isSnoozed,
            IsDismissed = isDismissed,
            IsRecurring = isRecurring,
        };

        if (isRecurring)
        {
            _ = LoadOccurrencesForActiveDetailAsync(ActiveEventDetail, id, reminder?.Rrule, reminder?.DueAt, title, reminder?.RecurrenceUntil);
        }

        IsEventDetailOpen = true;
        EventDetailOpenRequested?.Invoke();
    }

    private static (string Text, string Tone) DescribeEventStatus(
        bool isCompleted,
        bool isSnoozed,
        string? snoozeLabel,
        bool isDismissed) =>
        isCompleted ? ("Completed", "Online")
        : isSnoozed ? (snoozeLabel ?? "Snoozed", "Warn")
        : isDismissed ? ("Won't rerun", "Danger")
        : ("Scheduled", "Offline");

    private async Task LoadOccurrencesForActiveDetailAsync(
        EventDetailModalViewModel detail,
        string reminderId,
        string? rrule,
        DateTimeOffset? dueAt,
        string body,
        DateTimeOffset? recurrenceUntil)
    {
        OccurrenceItemViewModel CreateOccurrenceItem(
            DateTimeOffset occursAt,
            string effectiveBody,
            string? customBody,
            string status)
        {
            var localTime = occursAt.ToLocalTime();
            var timeStr = TimeFormatting.FormatTime(localTime.DateTime, settings.Use24HourClock);
            return new OccurrenceItemViewModel
            {
                ReminderId = reminderId,
                OccursAt = occursAt,
                SeriesBody = body,
                Body = effectiveBody,
                CustomBody = customBody,
                Status = status,
                DateLabel = $"{localTime.ToString("ddd d MMM", CultureInfo.CurrentCulture)} · {timeStr}",
                EditText = effectiveBody,
            };
        }

        detail.IsLoadingOccurrences = true;
        detail.Occurrences.Clear();

        try
        {
            var serverOccurrences = await api.GetReminderOccurrencesAsync(reminderId);
            if (serverOccurrences is not null && serverOccurrences.Count > 0)
            {
                reminders.SetOccurrences(reminderId, serverOccurrences);
                foreach (var occ in SortOccurrencesOldestToNewest(serverOccurrences))
                {
                    detail.Occurrences.Add(CreateOccurrenceItem(occ.OccursAt, occ.EffectiveBody, occ.CustomBody, occ.Status));
                }
                detail.IsLoadingOccurrences = false;
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not fetch occurrences from API for {ReminderId}, generating locally", reminderId);
        }

        // Local fallback expansion if offline or no server response
        if (!string.IsNullOrEmpty(rrule) && dueAt.HasValue)
        {
            void AddFallbackOccurrence(DateTimeOffset dto)
            {
                if (reminders.TryGetOccurrence(reminderId, dto, out var cached) && cached is not null)
                {
                    detail.Occurrences.Add(CreateOccurrenceItem(dto, cached.EffectiveBody, cached.CustomBody, cached.Status));
                }
                else
                {
                    detail.Occurrences.Add(CreateOccurrenceItem(dto, body, null, "pending"));
                }
            }

            var localDue = dueAt.Value.ToLocalTime();
            var startDate = DateOnly.FromDateTime(localDue.Date);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var targetCount = Recurrence.TargetOccurrenceCount(rrule);

            var maxYears = rrule.Contains("FREQ=YEARLY", StringComparison.OrdinalIgnoreCase) ? 6 : 2;
            var maxDays = rrule.Contains("FREQ=HOURLY", StringComparison.OrdinalIgnoreCase) ? 30 : (maxYears * 365);
            var fromDate = startDate > today ? startDate : today;
            var horizon = today.AddDays(maxDays);
            var untilDate = recurrenceUntil.HasValue ? DateOnly.FromDateTime(recurrenceUntil.Value.ToLocalTime().Date) : horizon;
            var toDate = untilDate < horizon ? untilDate : horizon;

            var timeOfDay = TimeOnly.FromTimeSpan(localDue.TimeOfDay);
            var isHourly = rrule.Contains("FREQ=HOURLY", StringComparison.OrdinalIgnoreCase);

            if (isHourly)
            {
                var intervalHours = ParseHourlyInterval(rrule);
                var curDt = localDue.DateTime;
                var cutoffDt = DateTime.Now.AddMinutes(-5);
                if (curDt < cutoffDt)
                {
                    var elapsedHours = (cutoffDt - curDt).TotalHours;
                    var steps = (long)Math.Ceiling(elapsedHours / intervalHours);
                    if (steps > 0)
                    {
                        curDt = curDt.AddHours(steps * intervalHours);
                    }
                }

                var untilLocal = recurrenceUntil?.ToLocalTime().DateTime;
                var count = 0;
                while (count < targetCount && count < 50 && (!untilLocal.HasValue || curDt <= untilLocal.Value))
                {
                    var dto = new DateTimeOffset(DateTime.SpecifyKind(curDt, DateTimeKind.Local));
                    AddFallbackOccurrence(dto);
                    count++;
                    curDt = curDt.AddHours(intervalHours);
                }
            }
            else
            {
                var cur = fromDate;
                var count = 0;
                while (cur <= toDate && count < targetCount)
                {
                    if (Recurrence.OccursOn(rrule, startDate, cur))
                    {
                        var dto = new DateTimeOffset(DateTime.SpecifyKind(cur.ToDateTime(timeOfDay), DateTimeKind.Local));
                        AddFallbackOccurrence(dto);
                        count++;
                    }
                    cur = cur.AddDays(1);
                }
            }
        }

        detail.IsLoadingOccurrences = false;
    }

    private void SyncCachedOccurrence(OccurrenceItemViewModel item) =>
        reminders.UpsertOccurrence(new ReminderOccurrenceResponse(
            Id: 0,
            ReminderId: item.ReminderId,
            OccursAt: item.OccursAt,
            Status: item.Status,
            CompletedAt: item.IsCompleted ? DateTimeOffset.UtcNow : null,
            CustomBody: item.CustomBody,
            EffectiveBody: item.Body));

    public static IReadOnlyList<ReminderOccurrenceResponse> SortOccurrencesOldestToNewest(
        IEnumerable<ReminderOccurrenceResponse> items) =>
        items.OrderBy(static o => o.OccursAt.ToUniversalTime()).ToList();

    public static int ParseHourlyInterval(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return 1;
        }

        foreach (var part in rrule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("INTERVAL=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(part["INTERVAL=".Length..], CultureInfo.InvariantCulture, out var parsed) &&
                parsed >= 1)
            {
                return parsed;
            }
        }

        return 1;
    }

    [RelayCommand]
    public async Task ToggleOccurrenceCompleteAsync(OccurrenceItemViewModel? item)
    {
        if (item is null) return;
        EnsureRemindersHooked();
        var nextStatus = item.IsCompleted ? "pending" : "completed";
        item.Status = nextStatus;
        SyncCachedOccurrence(item);

        try
        {
            var updated = await api.UpdateReminderOccurrenceAsync(item.ReminderId,
                new UpdateReminderOccurrenceRequest(item.OccursAt, Status: nextStatus));
            if (updated is not null)
            {
                reminders.UpsertOccurrence(updated);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to toggle occurrence complete for {ReminderId}", item.ReminderId);
        }
    }

    [RelayCommand]
    public void StartEditOccurrence(OccurrenceItemViewModel? item)
    {
        if (item is null) return;
        item.EditText = item.Body;
        item.IsEditing = true;
    }

    [RelayCommand]
    public async Task SaveEditOccurrenceAsync(OccurrenceItemViewModel? item)
    {
        if (item is null) return;
        var newText = item.EditText.Trim();
        if (string.IsNullOrEmpty(newText)) return;

        var previousBody = item.Body;
        var previousCustomBody = item.CustomBody;
        if (string.Equals(newText, previousBody, StringComparison.Ordinal))
        {
            item.IsEditing = false;
            return;
        }

        EnsureRemindersHooked();
        item.Body = newText;
        item.CustomBody = newText;
        item.IsEditing = false;
        SyncCachedOccurrence(item);

        try
        {
            var saved = await api.UpdateReminderOccurrenceAsync(item.ReminderId,
                new UpdateReminderOccurrenceRequest(item.OccursAt, CustomBody: newText));
            if (saved is not null)
            {
                reminders.UpsertOccurrence(saved);
            }
            reminders.ShowStatus("Occurrence updated.", false, async () =>
            {
                var reverted = await api.UpdateReminderOccurrenceAsync(
                    item.ReminderId,
                    new UpdateReminderOccurrenceRequest(
                        item.OccursAt,
                        CustomBody: previousCustomBody,
                        ClearCustomBody: previousCustomBody is null));
                item.CustomBody = reverted?.CustomBody ?? previousCustomBody;
                item.Body = reverted?.EffectiveBody ?? previousBody;
                item.EditText = item.Body;
                if (reverted is not null)
                {
                    reminders.UpsertOccurrence(reverted);
                }
                else
                {
                    SyncCachedOccurrence(item);
                }
                reminders.ShowStatus("Changes undone.", false);
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save occurrence text for {ReminderId}", item.ReminderId);
        }
    }

    [RelayCommand]
    public async Task ResetOccurrenceTextAsync(OccurrenceItemViewModel? item)
    {
        if (item is null || !item.HasCustomText) return;
        var previousCustomBody = item.CustomBody;
        var previousBody = item.Body;

        EnsureRemindersHooked();
        item.CustomBody = null;
        if (!string.IsNullOrEmpty(item.SeriesBody))
        {
            item.Body = item.SeriesBody;
            item.EditText = item.SeriesBody;
        }
        item.IsEditing = false;
        SyncCachedOccurrence(item);

        try
        {
            var reset = await api.UpdateReminderOccurrenceAsync(item.ReminderId,
                new UpdateReminderOccurrenceRequest(item.OccursAt, ClearCustomBody: true));
            if (reset is not null)
            {
                item.Body = reset.EffectiveBody;
                item.EditText = reset.EffectiveBody;
                reminders.UpsertOccurrence(reset);
            }
            reminders.ShowStatus("Occurrence reset to series text.", false, async () =>
            {
                var reverted = await api.UpdateReminderOccurrenceAsync(
                    item.ReminderId,
                    new UpdateReminderOccurrenceRequest(item.OccursAt, CustomBody: previousCustomBody));
                item.CustomBody = reverted?.CustomBody ?? previousCustomBody;
                item.Body = reverted?.EffectiveBody ?? previousBody;
                item.EditText = item.Body;
                if (reverted is not null)
                {
                    reminders.UpsertOccurrence(reverted);
                }
                else
                {
                    SyncCachedOccurrence(item);
                }
                reminders.ShowStatus("Changes undone.", false);
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to reset occurrence text for {ReminderId}", item.ReminderId);
        }
    }

    [RelayCommand]
    public void CancelEditOccurrence(OccurrenceItemViewModel? item)
    {
        if (item is null) return;
        item.IsEditing = false;
    }

    [RelayCommand]
    public async Task StopOccurrenceAsync(OccurrenceItemViewModel? item)
    {
        if (item is null) return;
        var previousStatus = item.Status;
        EnsureRemindersHooked();
        item.Status = "skipped";
        SyncCachedOccurrence(item);

        try
        {
            await api.DeleteReminderOccurrenceAsync(item.ReminderId, item.OccursAt);
            reminders.ShowStatus("Occurrence stopped.", false, async () =>
            {
                var restoreStatus = string.Equals(previousStatus, "skipped", StringComparison.OrdinalIgnoreCase)
                    ? "pending"
                    : previousStatus;
                var restored = await api.UpdateReminderOccurrenceAsync(
                    item.ReminderId,
                    new UpdateReminderOccurrenceRequest(item.OccursAt, Status: restoreStatus));
                item.Status = restored?.Status ?? restoreStatus;
                if (restored is not null)
                {
                    reminders.UpsertOccurrence(restored);
                }
                else
                {
                    SyncCachedOccurrence(item);
                }
                reminders.ShowStatus("Occurrence restored.", false);
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop occurrence for {ReminderId}", item.ReminderId);
        }
    }

    [RelayCommand]
    public async Task RestoreOccurrenceAsync(OccurrenceItemViewModel? item)
    {
        if (item is null) return;
        EnsureRemindersHooked();
        item.Status = "pending";
        SyncCachedOccurrence(item);

        try
        {
            var restored = await api.UpdateReminderOccurrenceAsync(item.ReminderId,
                new UpdateReminderOccurrenceRequest(item.OccursAt, Status: "pending"));
            if (restored is not null)
            {
                reminders.UpsertOccurrence(restored);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to restore occurrence for {ReminderId}", item.ReminderId);
        }
    }

    [RelayCommand]
    public void CloseEventDetail()
    {
        if (EventDetailCloseRequested is not null)
        {
            EventDetailCloseRequested.Invoke();
        }
        else
        {
            IsEventDetailOpen = false;
        }
    }

    [RelayCommand]
    public async Task ToggleActiveEventCompleteAsync()
    {
        if (ActiveEventDetail is null || string.IsNullOrEmpty(ActiveEventDetail.Id)) return;
        EnsureRemindersHooked();
        await reminders.CompleteCommand.ExecuteAsync(ActiveEventDetail.Id);
        CloseEventDetail();
    }

    [RelayCommand]
    public void EditActiveEvent()
    {
        if (ActiveEventDetail is null || string.IsNullOrEmpty(ActiveEventDetail.Id)) return;
        var row = reminders.Rows.FirstOrDefault(r => r.Id == ActiveEventDetail.Id)
            ?? new ReminderRow(ActiveEventDetail.Id, "", "", ActiveEventDetail.Title, "", ActiveEventDetail.IsCompleted, false);

        CloseEventDetail();
        Page = CompanionPage.Reminders;
        reminders.StartEdit(row);
    }

    [RelayCommand]
    public async Task DeleteActiveEventAsync()
    {
        if (ActiveEventDetail is null || string.IsNullOrEmpty(ActiveEventDetail.Id)) return;
        EnsureRemindersHooked();
        await reminders.DeleteCommand.ExecuteAsync(ActiveEventDetail.Id);
        CloseEventDetail();
    }

    internal static string Describe(string commandType) => commandType switch
    {
        CommandTypes.Shutdown => "Shut down",
        CommandTypes.Restart => "Restart",
        CommandTypes.SignOut => "Sign out",
        CommandTypes.Lock => "Lock",
        CommandTypes.Sleep => "Sleep",
        CommandTypes.Hibernate => "Hibernate",
        _ => commandType,
    };

    internal static string IconFor(string commandType) => commandType switch
    {
        CommandTypes.Shutdown => "Icon.PowerSettingsNew",
        CommandTypes.Restart => "Icon.RestartAlt",
        CommandTypes.SignOut => "Icon.Logout",
        CommandTypes.Lock => "Icon.Lock",
        CommandTypes.Sleep => "Icon.Bedtime",
        CommandTypes.Hibernate => "Icon.NightsStay",
        _ => "Icon.Computer",
    };

    /// <summary>"Today 09:14", or the day when it was not today.</summary>
    internal static string DescribeMoment(DateTimeOffset moment)
    {
        var local = moment.ToLocalTime();
        var day = DateOnly.FromDateTime(local.Date);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var prefix = day == today ? string.Empty
            : day == today.AddDays(-1) ? "Yesterday "
            : local.ToString("ddd d MMM ", CultureInfo.CurrentCulture);

        return prefix + local.ToString("HH:mm", CultureInfo.CurrentCulture);
    }
}

public partial class DevicesViewModel(
    PcConnectClient api,
    ILogger<DevicesViewModel> logger) : ObservableObject
{
    private string? _thisPcId;

    public string? ThisPcId => _thisPcId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedDeviceStatus))]
    private DeviceItem? _selectedDevice;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The activity log for the selected PC, shown over the page.</summary>
    [ObservableProperty]
    private bool _isLogOpen;

    [ObservableProperty]
    private string _renameTo = string.Empty;

    /// <summary>Every PC registered to the account, including this one.</summary>
    public ObservableCollection<DeviceItem> Items { get; } = [];

    /// <summary>Every PC but the one being sat at — this PC cannot control itself.</summary>
    public ObservableCollection<DeviceItem> Others { get; } = [];

    public ObservableCollection<CommandItem> RecentCommands { get; } = [];

    /// <summary>The commands the selected PC accepts, as buttons.</summary>
    public ObservableCollection<CommandRow> SelectedCommands { get; } = [];

    /// <summary>The selected PC's own log.</summary>
    public ObservableCollection<ActivityRow> SelectedActivity { get; } = [];

    public string SelectedDeviceStatus => SelectedDevice?.LastSeenDescription ?? string.Empty;

    /// <summary>
    /// Asks the user to confirm a destructive command. Set by the view, because
    /// the prompt is a window and the view model must not create one.
    /// </summary>
    public Func<string, string, Task<string?>>? RequestStepUpPassword { get; set; }

    public event Action? Refreshed;

    public void SetThisPc(string? deviceId)
    {
        _thisPcId = deviceId;
        RebuildOthers();
    }

    public async Task LoadAsync()
    {
        try
        {
            var list = await api.ListDevicesAsync();

            // Remember which PC was chosen, not which object represented it.
            // Rebuilding the collection leaves SelectedDevice pointing at an item
            // that is no longer in it: the list shows nothing selected while the
            // command buttons stay enabled, which is exactly the state a reload
            // on reconnect used to produce.
            var previous = SelectedDevice?.Id;

            Items.Clear();

            foreach (var device in list)
            {
                Items.Add(new DeviceItem(device));
            }

            RebuildOthers();

            SelectedDevice = Others.FirstOrDefault(item => item.Id == previous) ?? Others.FirstOrDefault();

            RecentCommands.Clear();
            foreach (var command in await api.ListCommandsAsync(50))
            {
                RecentCommands.Add(new CommandItem(command));
            }

            RebuildSelectedDetail();
            Refreshed?.Invoke();
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            StatusMessage = "Could not load your PCs.";
            logger.LogWarning(ex, "Device list failed");
        }
    }

    private void RebuildOthers()
    {
        Others.Clear();

        foreach (var device in Items.Where(d => d.Id != _thisPcId))
        {
            Others.Add(device);
        }

        if (SelectedDevice is null || Others.All(d => d.Id != SelectedDevice.Id))
        {
            SelectedDevice = Others.FirstOrDefault();
        }
    }

    partial void OnSelectedDeviceChanged(DeviceItem? value)
    {
        RenameTo = value?.DisplayName ?? string.Empty;
        IsLogOpen = false;
        RebuildSelectedDetail();
    }

    public static bool IsCommandAllowed(DeviceItem device, string type) =>
        device.AllowedCommands.Count == 0 || device.AllowedCommands.Contains(type, StringComparer.Ordinal);

    public static bool IsCommandProtected(DeviceItem device, string type) =>
        IsCommandAllowed(device, type) && device.PasswordRequiredCommands.Contains(type, StringComparer.Ordinal);

    private void RebuildSelectedDetail()
    {
        SelectedCommands.Clear();
        SelectedActivity.Clear();

        if (SelectedDevice is null)
        {
            return;
        }

        foreach (var type in CommandTypes.All)
        {
            if (!IsCommandAllowed(SelectedDevice, type))
            {
                continue;
            }

            SelectedCommands.Add(new CommandRow
            {
                Type = type,
                Name = ShellViewModel.Describe(type),
                IconKey = ShellViewModel.IconFor(type),
                Accepted = true,
                AsksForPassword = IsCommandProtected(SelectedDevice, type),
            });
        }

        foreach (var command in RecentCommands.Where(c => c.DeviceId == SelectedDevice.Id).Take(20))
        {
            SelectedActivity.Add(new ActivityRow(
                Event: ShellViewModel.Describe(command.Type),
                Time: ShellViewModel.DescribeMoment(command.IssuedAt),
                Source: "from this PC",
                Outcome: OutcomeOf(command.Status, command.ResultMessage),
                Tone: ToneOf(command.Status)));
        }
    }

    internal static string OutcomeOf(string status, string? resultMessage) => status switch
    {
        "succeeded" => "Done",
        "expired" => "Expired · offline",
        "failed" => string.IsNullOrWhiteSpace(resultMessage) ? "Failed" : resultMessage,
        "pending" => "Sending",
        "delivered" => "Delivered",
        _ => char.ToUpperInvariant(status[0]) + status[1..],
    };

    internal static Tone ToneOf(string status) => status switch
    {
        "succeeded" => Tone.Good,
        "expired" or "failed" or "rejected" => Tone.Bad,
        _ => Tone.Neutral,
    };

    /// <summary>
    /// No PC selected, no command. The guard inside <see cref="SendAsync"/>
    /// already stopped anything being sent, but it did so silently: the buttons
    /// looked usable, and pressing "Shut down" with an empty list did nothing at
    /// all, which reads as a broken application rather than as a precondition.
    /// </summary>
    private bool CanSend(string? commandType) =>
        SelectedDevice is not null && !string.IsNullOrWhiteSpace(commandType);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(string? commandType)
    {
        if (SelectedDevice is null || string.IsNullOrWhiteSpace(commandType))
        {
            return;
        }

        if (!CommandTypes.TryNormalise(commandType, out var normalised))
        {
            return;
        }

        if (!IsCommandAllowed(SelectedDevice, normalised))
        {
            StatusMessage = $"{ShellViewModel.Describe(normalised)} is disabled in {SelectedDevice.DisplayName}'s desktop preferences.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            string? stepUpToken = null;

            // The target PC decides which commands need fresh confirmation.
            // Prompt before sending so the user sees one dialog rather than a
            // rejected request followed by a dialog.
            if (IsCommandProtected(SelectedDevice, normalised) && RequestStepUpPassword is not null)
            {
                var password = await RequestStepUpPassword(normalised, SelectedDevice.DisplayName);
                if (password is null)
                {
                    StatusMessage = "Cancelled.";
                    return;
                }

                var challenge = await api.StartStepUpAsync();
                var verified = await api.VerifyStepUpWithPasswordAsync(challenge!.ChallengeId, password);
                stepUpToken = verified?.StepUpToken;
            }

            var targetDisplayName = SelectedDevice.DisplayName;
            var command = await api.IssueCommandAsync(new IssueCommandRequest(
                Guid.CreateVersion7().ToString(),
                SelectedDevice.Id,
                normalised,
                null,
                null,
                stepUpToken));

            if (command is not null)
            {
                RecentCommands.Insert(0, new CommandItem(command));
                RebuildSelectedDetail();
                await LoadAsync();
                StatusMessage = $"{ShellViewModel.Describe(normalised)} sent to {targetDisplayName}.";
            }
        }
        catch (PcConnectApiException ex)
        {
            StatusMessage = ex.Message;
            logger.LogInformation("Command failed: {Code}", ex.Code);
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
    private async Task RenameAsync()
    {
        if (SelectedDevice is null || string.IsNullOrWhiteSpace(RenameTo) || RenameTo == SelectedDevice.DisplayName)
        {
            return;
        }

        try
        {
            await api.UpdateDeviceAsync(SelectedDevice.Id, new UpdateDeviceRequest(DisplayName: RenameTo.Trim()));
            await LoadAsync();
            StatusMessage = $"Renamed to {RenameTo.Trim()}.";
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            StatusMessage = "Could not rename that PC.";
            logger.LogWarning(ex, "Rename failed");
        }
    }

    [RelayCommand]
    private void ShowLog() => IsLogOpen = true;

    [RelayCommand]
    private void CloseLog() => IsLogOpen = false;

    public void ApplyPresence(DevicePresenceEvent presence)
    {
        foreach (var item in Items.Where(i => i.Id == presence.DeviceId))
        {
            item.IsOnline = presence.IsOnline;
        }

        OnPropertyChanged(nameof(SelectedDeviceStatus));
    }

    public void ApplyCommandStatus(CommandStatusEvent status)
    {
        foreach (var item in RecentCommands.Where(c => c.Id == status.Id))
        {
            item.Status = status.Status;
        }

        StatusMessage = status.Status switch
        {
            "succeeded" => "Done.",
            "expired" => "Not delivered — that PC was offline.",
            "failed" => $"The PC could not do that{(status.ResultMessage is null ? "." : $": {status.ResultMessage}")}",
            _ => StatusMessage,
        };

        RebuildSelectedDetail();
    }
}

public partial class DeviceItem(DeviceResponse device) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastSeenDescription))]
    private bool _isOnline = device.IsOnline;

    public string Id { get; } = device.Id;

    public string DisplayName { get; } = device.DisplayName;

    public string Platform { get; } = device.Platform;

    public string OsVersion { get; } = device.OsVersion;

    public IReadOnlyList<string> AllowedCommands { get; } = device.AllowedCommands;

    public IReadOnlyList<string> PasswordRequiredCommands { get; } = device.PasswordRequiredCommands;

    public DateTimeOffset? LastSeenAt { get; } = device.LastSeenAt;

    public string LastSeenDescription => IsOnline
        ? "Online"
        : LastSeenAt is { } seen
            ? $"Last seen {ShellViewModel.DescribeMoment(seen)}"
            : "Never seen";

    /// <summary>"Online · Windows 11" — the line under a PC's name.</summary>
    public string Summary => string.IsNullOrWhiteSpace(OsVersion)
        ? LastSeenDescription
        : $"{LastSeenDescription} · {OsVersion}";
}

public partial class CommandItem(CommandResponse command) : ObservableObject
{
    [ObservableProperty]
    private string _status = command.Status;

    public string Id { get; } = command.Id;

    public string DeviceId { get; } = command.DeviceId;

    public string Type { get; } = command.Type;

    public string? ResultMessage { get; } = command.ResultMessage;

    public DateTimeOffset IssuedAt { get; } = command.IssuedAt;

    public string Summary => $"{ShellViewModel.Describe(Type)} · {IssuedAt.ToLocalTime():HH:mm:ss}";
}
