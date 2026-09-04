using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using PCConnect.Client;
using PCConnect.Companion.Views;

namespace PCConnect.Companion.Services;

/// <summary>
/// Shows the full-screen reminder when one comes due.
///
/// The window is raised by a realtime event from the server, which is the
/// authority on when a reminder fires — the client does not schedule anything
/// locally, so a reminder is not missed because the PC's clock drifted or the
/// app was restarted (05 §3).
/// </summary>
public sealed class ReminderPresenter(
    PcConnectRealtimeClient realtime,
    PcConnectClient api,
    CompanionSettings settings,
    ILogger<ReminderPresenter> logger)
{
    private static readonly TimeSpan SnoozeFor = TimeSpan.FromMinutes(10);

    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);

    public void Start()
    {
        realtime.ReminderDue += due => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // The same reminder can arrive twice if the socket reconnects mid
            // delivery; showing it once is the point of the guard.
            if (!_shown.Add(due.ReminderId))
            {
                return;
            }

            Show(due.ReminderId, due.Body, due.DueAt);
        }).Task;
    }

    private void Show(string reminderId, string body, DateTimeOffset dueAt)
    {
        logger.LogInformation("Showing reminder {ReminderId}", reminderId);

        var window = new ReminderWindow(
            body,
            dueAt,
            settings.ReminderBackground,
            settings.ReminderForeground,
            Environment.MachineName,
            async () =>
            {
                try
                {
                    await api.CompleteReminderAsync(reminderId);
                }
                catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
                {
                    logger.LogWarning(ex, "Could not mark reminder {ReminderId} as done", reminderId);
                }
            },
            () => Snooze(reminderId, body, dueAt));

        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Brings the window back in ten minutes.
    ///
    /// Snooze lives entirely on this PC: the reminder itself is untouched, so it
    /// still shows on every other screen at its own time, and nothing about the
    /// series changes on the server. A snoozed reminder does not survive a
    /// restart of the app, which is the honest consequence of it being local.
    /// </summary>
    private void Snooze(string reminderId, string body, DateTimeOffset dueAt)
    {
        logger.LogInformation("Snoozing reminder {ReminderId} for {Minutes} minutes", reminderId, SnoozeFor.TotalMinutes);

        var timer = new DispatcherTimer { Interval = SnoozeFor };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Show(reminderId, body, dueAt);
        };

        timer.Start();
    }
}
