using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace PCConnect.Companion.Services;

/// <summary>
/// "Start with Windows" for the companion.
///
/// Per-user, under HKCU, so it needs no elevation and affects nobody else who
/// signs in on this PC. It governs the companion only: the agent is a service
/// and starts regardless, which is the whole point of it being one — a command
/// reaches a PC that is on with nobody signed in.
/// </summary>
public sealed class StartupRegistration(ILogger<StartupRegistration> logger)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PCConnect";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string existing && existing.Contains("PCConnect.Companion");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                logger.LogWarning(ex, "Could not read the Run key");
                return false;
            }
        }
    }

    /// <summary>Returns whether the change took, so the UI can show the truth.</summary>
    public bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKey);

            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path))
                {
                    return false;
                }

                // Quoted: an unquoted path with a space in it is the oldest
                // Run-key defect there is.
                key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return IsEnabled == enabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogWarning(ex, "Could not write the Run key");
            return false;
        }
    }
}
