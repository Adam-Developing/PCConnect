using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PCConnect.Client;
using PCConnect.Companion.Services;
using PCConnect.Core.Contracts;
using PCConnect.Core.Domain;

namespace PCConnect.Companion.ViewModels;

/// <summary>
/// How reminders look on this screen, what this PC will accept, and what has
/// happened to it.
/// </summary>
public sealed partial class SettingsViewModel(
    PcConnectClient api,
    CompanionSettings settings,
    StartupRegistration startup,
    DevicesViewModel devices,
    ILogger<SettingsViewModel> logger) : ObservableObject
{
    /// <summary>The presets, taken from the design's reminder-colour swatches.</summary>
    private static readonly string[] TextPresets = ["#F8FAFC", "#94A3B8", "#0F172A", "#2563EB"];

    private static readonly string[] BackgroundPresets = ["#0B1120", "#151E2E", "#EFF6FF", "#FFFFFF"];

    private DeviceItem? _thisPc;

    /// <summary>The last locally saved name, which the box is compared against.</summary>
    private string _savedPcName = string.Empty;

    [ObservableProperty]
    private string _reminderBackground = "#0B1120";

    [ObservableProperty]
    private string _reminderForeground = "#F8FAFC";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPcNameChanged))]
    private string _pcName = Environment.MachineName;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _use24HourClock = true;

    partial void OnUse24HourClockChanged(bool value)
    {
        settings.Use24HourClock = value;
    }

    [ObservableProperty]
    private bool _isActivityOpen;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>True once this PC has been recognised among the account's devices.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPcNameChanged))]
    [NotifyPropertyChangedFor(nameof(DeviceSettingsStatus))]
    private bool _hasThisPc;

    /// <summary>
    /// True when the box holds a name worth saving. The save button is only
    /// shown then, so an untouched name offers nothing to press.
    /// </summary>
    public bool IsPcNameChanged =>
        !string.IsNullOrWhiteSpace(PcName) && PcName.Trim() != _savedPcName;

    public string DeviceSettingsStatus =>
        settings.PcNameNeedsSync || settings.AllowedCommandsNeedSync || settings.PasswordRequiredCommandsNeedSync
            ? "Saved on this PC · sync pending"
            : HasThisPc ? "Synced with your account" : "Saved on this PC";

    public ObservableCollection<Swatch> TextSwatches { get; } = [];

    public ObservableCollection<Swatch> BackgroundSwatches { get; } = [];

    /// <summary>Every command, with whether this PC accepts it.</summary>
    public ObservableCollection<CommandRow> Commands { get; } = [];

    public ObservableCollection<ActivityRow> Activity { get; } = [];

    public string ActivityCount => Activity.Count.ToString();

    public bool HasActivity => Activity.Count > 0;

    public void Load()
    {
        ReminderBackground = settings.ReminderBackground;
        ReminderForeground = settings.ReminderForeground;
        PcName = settings.PcName;
        _savedPcName = settings.PcName;
        StartWithWindows = startup.IsEnabled;
        Use24HourClock = settings.Use24HourClock;

        RebuildSwatches();
        RebuildCommands(settings.AllowedCommands, settings.PasswordRequiredCommands);
        RebuildActivity();
    }

    /// <summary>Binds the settings page to whichever account device is this machine.</summary>
    public async Task AttachAsync(DeviceItem? thisPc)
    {
        _thisPc = thisPc;
        HasThisPc = thisPc is not null;

        if (thisPc is null)
        {
            PcName = settings.PcName;
            _savedPcName = settings.PcName;
            RebuildCommands(settings.AllowedCommands, settings.PasswordRequiredCommands);
            OnPropertyChanged(nameof(IsPcNameChanged));
            OnPropertyChanged(nameof(DeviceSettingsStatus));
            RebuildActivity();
            return;
        }

        var pendingName = settings.PcNameNeedsSync;
        var pendingCommands = settings.AllowedCommandsNeedSync;
        var pendingPasswords = settings.PasswordRequiredCommandsNeedSync;
        IReadOnlyList<string> serverAllowed = thisPc.AllowedCommands.Count == 0
            ? CommandTypes.All.ToArray()
            : thisPc.AllowedCommands;

        PcName = pendingName ? settings.PcName : thisPc.DisplayName;
        _savedPcName = PcName;
        var serverPasswords = thisPc.PasswordRequiredCommands;
        RebuildCommands(
            pendingCommands ? settings.AllowedCommands : serverAllowed,
            pendingPasswords ? settings.PasswordRequiredCommands : serverPasswords);

        if (!pendingName)
        {
            settings.SavePcName(PcName, needsSync: false);
        }

        if (!pendingCommands)
        {
            settings.SaveAllowedCommands(serverAllowed, needsSync: false);
        }

        if (!pendingPasswords)
        {
            settings.SavePasswordRequiredCommands(serverPasswords, needsSync: false);
        }

        if (pendingName || pendingCommands || pendingPasswords)
        {
            await SyncDeviceSettingsAsync(pendingName, pendingCommands, pendingPasswords);
        }

        OnPropertyChanged(nameof(IsPcNameChanged));
        OnPropertyChanged(nameof(DeviceSettingsStatus));
        RebuildActivity();
    }

    private void RebuildCommands(IReadOnlyList<string> allowed, IReadOnlyList<string> passwordRequired)
    {
        Commands.Clear();

        foreach (var type in CommandTypes.All)
        {
            Commands.Add(new CommandRow
            {
                Type = type,
                Name = ShellViewModel.Describe(type),
                IconKey = ShellViewModel.IconFor(type),
                Accepted = allowed.Contains(type),
                AsksForPassword = passwordRequired.Contains(type),
            });
        }
    }

    public void RebuildActivity()
    {
        Activity.Clear();

        foreach (var command in devices.RecentCommands.Take(50))
        {
            var device = devices.Items.FirstOrDefault(d => d.Id == command.DeviceId);

            Activity.Add(new ActivityRow(
                Event: ShellViewModel.Describe(command.Type),
                Time: ShellViewModel.DescribeMoment(command.IssuedAt),
                Source: device?.DisplayName ?? "That PC",
                Outcome: DevicesViewModel.OutcomeOf(command.Status, command.ResultMessage),
                Tone: DevicesViewModel.ToneOf(command.Status)));
        }

        OnPropertyChanged(nameof(ActivityCount));
        OnPropertyChanged(nameof(HasActivity));
    }

    partial void OnReminderBackgroundChanged(string value)
    {
        settings.ReminderBackground = value;
        RebuildSwatches();
    }

    partial void OnReminderForegroundChanged(string value)
    {
        settings.ReminderForeground = value;
        RebuildSwatches();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!startup.Set(value))
        {
            // The registry refused. Say so rather than leaving a switch on that
            // did nothing.
            StatusMessage = "Windows would not let PCConnect change its startup setting.";
            logger.LogWarning("Could not set the Run key to {Value}", value);
        }
        else
        {
            StatusMessage = string.Empty;
        }
    }

    private void RebuildSwatches()
    {
        Sync(TextSwatches, TextPresets, ReminderForeground);
        Sync(BackgroundSwatches, BackgroundPresets, ReminderBackground);
    }

    private static void Sync(ObservableCollection<Swatch> target, IReadOnlyList<string> presets, string selected)
    {
        if (target.Count == 0)
        {
            foreach (var colour in presets)
            {
                target.Add(new Swatch { Colour = colour });
            }
        }

        foreach (var swatch in target)
        {
            swatch.IsSelected = string.Equals(swatch.Colour, selected, StringComparison.OrdinalIgnoreCase);
        }
    }

    [RelayCommand]
    private void PickTextColour(string? colour)
    {
        if (!string.IsNullOrWhiteSpace(colour)) ReminderForeground = colour;
    }

    [RelayCommand]
    private void PickBackgroundColour(string? colour)
    {
        if (!string.IsNullOrWhiteSpace(colour)) ReminderBackground = colour;
    }

    [RelayCommand]
    private void ToggleActivity() => IsActivityOpen = !IsActivityOpen;

    [RelayCommand]
    private async Task RenameThisPcAsync()
    {
        if (!IsPcNameChanged)
        {
            return;
        }

        var name = PcName.Trim();
        settings.SavePcName(name, needsSync: true);
        _savedPcName = name;
        OnPropertyChanged(nameof(IsPcNameChanged));
        OnPropertyChanged(nameof(DeviceSettingsStatus));

        if (_thisPc is null)
        {
            StatusMessage = "Saved on this PC. It will sync when this PC is linked to the account.";
            return;
        }

        await SyncDeviceSettingsAsync(syncName: true, syncCommands: false, syncPasswords: false);
    }

    /// <summary>
    /// Turns a command on or off for this PC.
    ///
    /// This is the account's allow-list, sent to the server. The agent keeps its
    /// own, independent of it — a compromised server still cannot make an agent
    /// run something the agent does not allow (ADR-0012).
    /// </summary>
    [RelayCommand]
    private async Task ToggleAcceptedAsync(CommandRow? row)
    {
        if (row is null)
        {
            return;
        }

        row.Accepted = !row.Accepted;

        // A command this PC will not accept can never reach a password prompt.
        // Clear the dependent setting as well as disabling its switch in the UI.
        if (!row.Accepted)
        {
            row.AsksForPassword = false;
        }

        var allowed = Commands.Where(c => c.Accepted).Select(c => c.Type).ToList();
        var passwordRequired = Commands.Where(c => c.Accepted && c.AsksForPassword).Select(c => c.Type).ToList();
        settings.SaveAllowedCommands(allowed, needsSync: true);
        settings.SavePasswordRequiredCommands(passwordRequired, needsSync: true);
        OnPropertyChanged(nameof(DeviceSettingsStatus));

        if (_thisPc is null)
        {
            StatusMessage = "Saved on this PC. It will sync when this PC is linked to the account.";
            return;
        }

        await SyncDeviceSettingsAsync(syncName: false, syncCommands: true, syncPasswords: true);
    }

    [RelayCommand]
    private async Task TogglePasswordRequiredAsync(CommandRow? row)
    {
        if (row is null || !row.Accepted)
        {
            return;
        }

        row.AsksForPassword = !row.AsksForPassword;

        var passwordRequired = Commands.Where(c => c.AsksForPassword).Select(c => c.Type).ToList();
        settings.SavePasswordRequiredCommands(passwordRequired, needsSync: true);
        OnPropertyChanged(nameof(DeviceSettingsStatus));

        if (_thisPc is null)
        {
            StatusMessage = "Saved on this PC. It will sync when this PC is linked to the account.";
            return;
        }

        await SyncDeviceSettingsAsync(syncName: false, syncCommands: false, syncPasswords: true);
    }

    private async Task SyncDeviceSettingsAsync(bool syncName, bool syncCommands, bool syncPasswords)
    {
        if (_thisPc is null)
        {
            return;
        }

        try
        {
            await api.UpdateDeviceAsync(_thisPc.Id, new UpdateDeviceRequest(
                DisplayName: syncName ? settings.PcName : null,
                AllowedCommands: syncCommands ? settings.AllowedCommands : null,
                PasswordRequiredCommands: syncPasswords ? settings.PasswordRequiredCommands : null));

            if (syncName)
            {
                settings.SavePcName(settings.PcName, needsSync: false);
            }

            if (syncCommands)
            {
                settings.SaveAllowedCommands(settings.AllowedCommands, needsSync: false);
            }

            if (syncPasswords)
            {
                settings.SavePasswordRequiredCommands(settings.PasswordRequiredCommands, needsSync: false);
            }

            await devices.LoadAsync();
            StatusMessage = string.Empty;
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            // Keep the local value. It remains editable and is retried the next
            // time this PC is resolved instead of snapping the control back.
            StatusMessage = "Saved on this PC. Account sync will retry when the connection returns.";
            logger.LogWarning(ex, "Device settings sync failed");
        }

        OnPropertyChanged(nameof(DeviceSettingsStatus));
    }
}
