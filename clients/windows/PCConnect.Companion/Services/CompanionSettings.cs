using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PCConnect.Core.Domain;

namespace PCConnect.Companion.Services;

/// <summary>
/// The settings a person changes in the app, kept beside the app rather than in
/// its configuration file.
///
/// <see cref="CompanionOptions"/> is deployment configuration — the backend
/// address, the version — and is written by whoever installs the app. This is
/// the other half: the reminder colours, which PC this is, and whether to start
/// with Windows. A settings screen that could only read its values would not be
/// a settings screen.
/// </summary>
public sealed class CompanionSettings(ILogger<CompanionSettings> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PCConnect",
        "companion.json");

    private State _state = new();

    /// <summary>The v1 client let people pick these to cope with eye strain.</summary>
    public string ReminderBackground
    {
        get => _state.ReminderBackground;
        set => Update(_state with { ReminderBackground = value });
    }

    public string ReminderForeground
    {
        get => _state.ReminderForeground;
        set => Update(_state with { ReminderForeground = value });
    }

    /// <summary>
    /// Device preferences are stored locally as well as on the account. That
    /// makes every control on the desktop settings page usable before the agent
    /// is paired and while the server is unreachable. Pending values are sent
    /// to the account the next time this PC is resolved.
    /// </summary>
    public string PcName => _state.PcName;

    public IReadOnlyList<string> AllowedCommands => _state.AllowedCommands ?? CommandTypes.All.ToArray();

    public IReadOnlyList<string> PasswordRequiredCommands =>
        _state.PasswordRequiredCommands ?? CommandTypes.Destructive.ToArray();

    public bool PcNameNeedsSync => _state.PcNameNeedsSync;

    public bool AllowedCommandsNeedSync => _state.AllowedCommandsNeedSync;

    public bool PasswordRequiredCommandsNeedSync => _state.PasswordRequiredCommandsNeedSync;

    public void SavePcName(string value, bool needsSync) =>
        Update(_state with { PcName = value, PcNameNeedsSync = needsSync });

    public void SaveAllowedCommands(IEnumerable<string> value, bool needsSync) =>
        Update(_state with
        {
            AllowedCommands = value.Where(CommandTypes.All.Contains).Distinct(StringComparer.Ordinal).ToArray(),
            AllowedCommandsNeedSync = needsSync,
        });

    public void SavePasswordRequiredCommands(IEnumerable<string> value, bool needsSync) =>
        Update(_state with
        {
            PasswordRequiredCommands = value.Where(CommandTypes.All.Contains).Distinct(StringComparer.Ordinal).ToArray(),
            PasswordRequiredCommandsNeedSync = needsSync,
        });

    /// <summary>
    /// Which paired device is the machine this is running on.
    ///
    /// The companion holds a user credential, not a device one — the service in
    /// session 0 owns the device identity — so it cannot ask "which am I". It is
    /// resolved by machine name on first run and remembered here, because a
    /// renamed PC must not become a different PC.
    /// </summary>
    public string? ThisDeviceId
    {
        get => _state.ThisDeviceId;
        set => Update(_state with { ThisDeviceId = value });
    }

    public void Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var loaded = JsonSerializer.Deserialize<State>(File.ReadAllText(Path)) ?? new State();
                _state = loaded with
                {
                    PcName = string.IsNullOrWhiteSpace(loaded.PcName) ? Environment.MachineName : loaded.PcName,
                    AllowedCommands = (loaded.AllowedCommands ?? CommandTypes.All.ToArray())
                        .Where(CommandTypes.All.Contains)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    PasswordRequiredCommands = (loaded.PasswordRequiredCommands ?? CommandTypes.Destructive.ToArray())
                        .Where(CommandTypes.All.Contains)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable settings are defaults, not a crash on startup.
            logger.LogWarning(ex, "Could not read {Path}; using defaults", Path);
            _state = new State();
        }
    }

    private void Update(State next)
    {
        _state = next;

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(_state, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write {Path}", Path);
        }
    }

    private sealed record State
    {
        public string ReminderBackground { get; init; } = "#0B1120";

        public string ReminderForeground { get; init; } = "#F8FAFC";

        public string PcName { get; init; } = Environment.MachineName;

        public string[]? AllowedCommands { get; init; } = CommandTypes.All.ToArray();

        public string[]? PasswordRequiredCommands { get; init; } = CommandTypes.Destructive.ToArray();

        public bool PcNameNeedsSync { get; init; }

        public bool AllowedCommandsNeedSync { get; init; }

        public bool PasswordRequiredCommandsNeedSync { get; init; }

        public string? ThisDeviceId { get; init; }
    }
}
