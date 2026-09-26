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
    ReminderSnoozeService snoozeService,
    ILogger<ReminderPresenter> logger)
{
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);

    public void Start()
    {
        snoozeService.ReminderSnoozeExpired += info => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Show(info.ReminderId, info.Body, info.DueAt, snoozedFor: info.Duration);
        });

        realtime.ReminderDue += due => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (snoozeService.IsSnoozed(due.ReminderId, out _))
            {
                return;
            }

            if (snoozeService.IsDismissed(due.ReminderId))
            {
                return;
            }

            // The same reminder can arrive twice if the socket reconnects mid
            // delivery; showing it once is the point of the guard.
            if (!_shown.Add(due.ReminderId))
            {
                return;
            }

            // The event is fanned out to the account, so this PC decides whether
            // the reminder is for the screen it is sitting on. No targets means
            // every PC. An unknown device id means show it: a reminder that
            // silently never appears is the worse failure.
            if (due.DeviceIds is { Count: > 0 } targets &&
                settings.ThisDeviceId is { Length: > 0 } here &&
                !targets.Contains(here, StringComparer.OrdinalIgnoreCase))
            {
                logger.LogDebug("Reminder {ReminderId} is not for this PC", due.ReminderId);
                return;
            }

            Show(due.ReminderId, due.Body, due.DueAt, snoozedFor: null);
        }).Task;
    }

    private void Show(string reminderId, string body, DateTimeOffset dueAt, TimeSpan? snoozedFor)
    {
        logger.LogInformation("Showing reminder {ReminderId} (snoozed: {Snoozed})", reminderId, snoozedFor.HasValue);

        var window = new ReminderWindow(
            body,
            dueAt,
            settings.ReminderBackground,
            settings.ReminderForeground,
            Environment.MachineName,
            async () =>
            {
                snoozeService.CancelSnooze(reminderId);
                snoozeService.ClearDismissed(reminderId);
                try
                {
                    await api.CompleteReminderAsync(reminderId);
                }
                catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException)
                {
                    logger.LogWarning(ex, "Could not mark reminder {ReminderId} as done", reminderId);
                }
            },
            duration =>
            {
                logger.LogInformation("Snoozing reminder {ReminderId} for {Minutes} minutes", reminderId, duration.TotalMinutes);
                snoozeService.Snooze(reminderId, body, dueAt, duration);
            },
            snoozedFor,
            onDismiss: () =>
            {
                logger.LogInformation("Reminder {ReminderId} dismissed as 'Don't remind me'", reminderId);
                snoozeService.MarkDismissed(reminderId);
            },
            use24HourClock: settings.Use24HourClock);

        window.Show();
        window.Activate();
    }
}
