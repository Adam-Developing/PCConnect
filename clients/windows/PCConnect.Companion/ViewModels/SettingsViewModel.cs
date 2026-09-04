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

    [ObservableProperty]
    private string _reminderBackground = "#0B1120";

    [ObservableProperty]
    private string _reminderForeground = "#F8FAFC";

    [ObservableProperty]
    private string _pcName = Environment.MachineName;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _isActivityOpen;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>True once this PC has been recognised among the paired devices.</summary>
    [ObservableProperty]
    private bool _hasThisPc;

    public ObservableCollection<Swatch> TextSwatches { get; } = [];

    public ObservableCollection<Swatch> BackgroundSwatches { get; } = [];

    /// <summary>Every command, with whether this PC accepts it.</summary>
    public ObservableCollection<CommandRow> Commands { get; } = [];

    public ObservableCollection<ActivityRow> Activity { get; } = [];

    public string ActivityCount => Activity.Count.ToString();

    public void Load()
    {
        ReminderBackground = settings.ReminderBackground;
        ReminderForeground = settings.ReminderForeground;
        StartWithWindows = startup.IsEnabled;

        RebuildSwatches();
    }

    /// <summary>Binds the settings page to whichever paired device is this machine.</summary>
    public void Attach(DeviceItem? thisPc)
    {
        _thisPc = thisPc;
        HasThisPc = thisPc is not null;
        PcName = thisPc?.DisplayName ?? Environment.MachineName;

        Commands.Clear();

        var allowed = thisPc?.AllowedCommands ?? [];

        foreach (var type in CommandTypes.All)
        {
            var row = new CommandRow
            {
                Type = type,
                Name = ShellViewModel.Describe(type),
                IconKey = ShellViewModel.IconFor(type),
                // An empty allow-list from the server means "everything", which
                // is what a freshly paired device has.
                Accepted = allowed.Count == 0 || allowed.Contains(type),
            };

            Commands.Add(row);
        }

        RebuildActivity();
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
        if (_thisPc is null || string.IsNullOrWhiteSpace(PcName) || PcName.Trim() == _thisPc.DisplayName)
        {
            return;
        }

        try
        {
            await api.UpdateDeviceAsync(_thisPc.Id, new UpdateDeviceRequest(DisplayName: PcName.Trim()));
            await devices.LoadAsync();
            StatusMessage = "Renamed.";
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            StatusMessage = "Could not rename this PC.";
            logger.LogWarning(ex, "Rename failed");
        }
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
        if (row is null || _thisPc is null)
        {
            return;
        }

        row.Accepted = !row.Accepted;

        var allowed = Commands.Where(c => c.Accepted).Select(c => c.Type).ToList();

        try
        {
            await api.UpdateDeviceAsync(_thisPc.Id, new UpdateDeviceRequest(AllowedCommands: allowed));
            await devices.LoadAsync();
            StatusMessage = string.Empty;
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
        {
            // Put the switch back: it must show what the server actually holds.
            row.Accepted = !row.Accepted;
            StatusMessage = "Could not change what this PC accepts.";
            logger.LogWarning(ex, "Allowed-command update failed");
        }
    }
}
