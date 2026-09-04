using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(Path)) ?? new State();
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

        public string? ThisDeviceId { get; init; }
    }
}
