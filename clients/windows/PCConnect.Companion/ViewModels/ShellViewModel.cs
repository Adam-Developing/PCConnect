using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PCConnect.Client;
using PCConnect.Companion.Services;
using PCConnect.Core.Contracts;
using PCConnect.Core.Domain;

namespace PCConnect.Companion.ViewModels;

public partial class ShellViewModel(
    PcConnectClient api,
    PcConnectRealtimeClient realtime,
    CompanionSettings settings,
    DevicesViewModel devices,
    RemindersViewModel reminders,
    AccountViewModel account,
    SettingsViewModel appSettings,
    ILogger<ShellViewModel> logger) : ObservableObject
{
    [ObservableProperty]
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
    private CompanionPage _page = CompanionPage.ThisPc;

    /// <summary>The PC this app is running on, once it has been recognised.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThisPcName))]
    private DeviceItem? _thisPc;

    [ObservableProperty]
    private string _weekSummary = string.Empty;

    [ObservableProperty]
    private string _lastCommandSummary = "Nothing yet";

    [ObservableProperty]
    private bool _isAccountMenuOpen;

    public DevicesViewModel Devices => devices;

    public RemindersViewModel Reminders => reminders;

    public AccountViewModel Account => account;

    public SettingsViewModel AppSettings => appSettings;

    /// <summary>The next seven days of reminders that will show on this screen.</summary>
    public ObservableCollection<DayColumn> Week { get; } = [];

    public string ThisPcName => ThisPc?.DisplayName ?? Environment.MachineName;

    public string Today => DateTime.Now.ToString("dddd d MMMM · HH:mm", CultureInfo.CurrentCulture);

    /// <summary>
    /// Startup: resolve the backend, check whether this build is still
    /// supported, and restore the session if there is one (06 §1).
    /// </summary>
    public async Task InitialiseAsync()
    {
        settings.Load();
        appSettings.Load();

        try
        {
            var discovery = await api.GetDiscoveryAsync();

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
        IsSignedIn = true;

        await account.LoadAsync();
        await devices.LoadAsync();
        await reminders.LoadAsync();

        ResolveThisPc();
        RebuildWeek();

        // Subscribed before connecting, so the first transition is not missed.
        realtime.ConnectionStateChanged += connected =>
            Application.Current.Dispatcher.InvokeAsync(() => IsRealtimeConnected = connected);

        // This host holds a user credential, not a device one, so recovering
        // means re-reading what the window shows — never claiming commands,
        // which is a device's job and would be refused.
        realtime.RecoverState = async _ =>
        {
            await devices.LoadAsync();
            await reminders.LoadAsync();
            Application.Current.Dispatcher.Invoke(() =>
            {
                ResolveThisPc();
                RebuildWeek();
            });
        };

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

        realtime.DevicePresenceChanged += presence =>
            Application.Current.Dispatcher.InvokeAsync(() => devices.ApplyPresence(presence)).Task;

        realtime.CommandStatusChanged += status =>
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                devices.ApplyCommandStatus(status);
                RefreshLastCommand();
            }).Task;

        realtime.ReminderChanged += _ =>
            Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await reminders.LoadAsync();
                RebuildWeek();
            }).Task;

        RefreshLastCommand();
    }

    /// <summary>
    /// Works out which paired device is this machine.
    ///
    /// The companion holds a user credential, not a device one — the service in
    /// session 0 owns the device identity — so it cannot simply ask. The agent
    /// registers under <see cref="Environment.MachineName"/> by default, so
    /// that is the first guess; the answer is then remembered, because renaming
    /// a PC must not turn it into a different PC.
    /// </summary>
    public void ResolveThisPc()
    {
        ThisPc = devices.Items.FirstOrDefault(d => d.Id == settings.ThisDeviceId)
            ?? devices.Items.FirstOrDefault(d =>
                string.Equals(d.DisplayName, Environment.MachineName, StringComparison.OrdinalIgnoreCase));

        if (ThisPc is not null)
        {
            settings.ThisDeviceId = ThisPc.Id;
        }

        devices.SetThisPc(ThisPc?.Id);
        appSettings.Attach(ThisPc);
        OnPropertyChanged(nameof(ThisPcName));
    }

    /// <summary>The seven-day strip on "This PC": what will appear on this screen.</summary>
    public void RebuildWeek()
    {
        Week.Clear();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var total = 0;

        for (var offset = 0; offset < 7; offset++)
        {
            var day = today.AddDays(offset);

            var items = reminders.Items
                .Where(r => !(offset == 0 && r.IsCompleted))
                .Where(r => Recurrence.OccursOn(r.Rrule, DateOnly.FromDateTime(r.DueAt.ToLocalTime().Date), day))
                .OrderBy(r => r.DueAt.ToLocalTime().TimeOfDay)
                .Select(r => new WeekItem(r.DueAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture), r.Body))
                .ToList();

            total += items.Count;

            Week.Add(new DayColumn(
                Label: offset == 0 ? "Today" : day.ToString("ddd", CultureInfo.CurrentCulture),
                Number: day.ToString("d MMM", CultureInfo.CurrentCulture),
                IsToday: offset == 0,
                Items: items));
        }

        WeekSummary = total == 1 ? "1 reminder in the next 7 days" : $"{total} reminders in the next 7 days";
    }

    private void RefreshLastCommand()
    {
        var last = devices.RecentCommands.FirstOrDefault();

        LastCommandSummary = last is null
            ? "Nothing yet"
            : $"{Describe(last.Type)} · {last.IssuedAt.ToLocalTime():HH:mm}";
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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedDeviceStatus))]
    private DeviceItem? _selectedDevice;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _pairingCode = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The activity log for the selected PC, shown over the page.</summary>
    [ObservableProperty]
    private bool _isLogOpen;

    [ObservableProperty]
    private string _renameTo = string.Empty;

    /// <summary>Every paired PC, including this one.</summary>
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

    private void RebuildSelectedDetail()
    {
        SelectedCommands.Clear();
        SelectedActivity.Clear();

        if (SelectedDevice is null)
        {
            return;
        }

        var allowed = SelectedDevice.AllowedCommands;

        foreach (var type in CommandTypes.All)
        {
            if (allowed.Count > 0 && !allowed.Contains(type))
            {
                continue;
            }

            SelectedCommands.Add(new CommandRow
            {
                Type = type,
                Name = ShellViewModel.Describe(type),
                IconKey = ShellViewModel.IconFor(type),
                Accepted = true,
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

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            string? stepUpToken = null;

            // Destructive commands are confirmed by the person, not by the
            // session (ADR-0011). The prompt is raised before the request so the
            // user sees one dialog rather than a failure followed by a dialog.
            if (CommandTypes.Destructive.Contains(normalised) && RequestStepUpPassword is not null)
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
                StatusMessage = $"{ShellViewModel.Describe(normalised)} sent to {SelectedDevice.DisplayName}.";
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

    [RelayCommand]
    private async Task ClaimPairingAsync()
    {
        if (string.IsNullOrWhiteSpace(PairingCode))
        {
            StatusMessage = "Type the code the other PC is showing.";
            return;
        }

        IsBusy = true;

        try
        {
            var claimed = await api.ClaimPairingAsync(PairingCode.Trim());
            StatusMessage = claimed is null ? "That code was not accepted." : $"Added {claimed.DisplayName}.";
            PairingCode = string.Empty;
            await LoadAsync();
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

    public DateTimeOffset? LastSeenAt { get; } = device.LastSeenAt;

    public string LastSeenDescription => IsOnline
        ? "Online"
        : LastSeenAt is { } seen
            ? $"Seen {ShellViewModel.DescribeMoment(seen)}"
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
