using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Threading;

namespace PCConnect.Companion.Services;

public sealed record ReminderSnoozeInfo(
    string ReminderId,
    string Body,
    DateTimeOffset DueAt,
    TimeSpan Duration,
    DateTimeOffset SnoozedAt)
{
    public DateTimeOffset SnoozedUntil => SnoozedAt + Duration;
    public string FormattedDuration => FormatDuration(Duration);

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 60)
        {
            var mins = (int)Math.Round(duration.TotalMinutes);
            return mins == 1 ? "1 min" : $"{mins} mins";
        }

        var hours = (int)Math.Round(duration.TotalHours);
        return hours == 1 ? "1 hour" : $"{hours} hours";
    }
}

/// <summary>
/// Tracks reminders that have been snoozed locally on this PC.
/// When snoozed, holds a timer that alerts when the snooze duration expires.
/// Also notifies the rest of the companion so the UI can clearly reflect
/// that a reminder was snoozed and for how long.
/// </summary>
public sealed class ReminderSnoozeService
{
    private readonly CompanionSettings? _settings;
    private readonly Func<TimeSpan, Action, IDisposable> _timerFactory;
    private readonly Dictionary<string, (ReminderSnoozeInfo Info, IDisposable Timer)> _snoozes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dismissed = new(StringComparer.OrdinalIgnoreCase);

    public event Action<ReminderSnoozeInfo>? ReminderSnoozed;
    public event Action<string>? ReminderUnsnoozed;
    public event Action<ReminderSnoozeInfo>? ReminderSnoozeExpired;
    public event Action<string>? ReminderDismissed;

    public ReminderSnoozeService(Func<TimeSpan, Action, IDisposable>? timerFactory)
        : this(null, timerFactory)
    {
    }

    public ReminderSnoozeService(CompanionSettings? settings = null, Func<TimeSpan, Action, IDisposable>? timerFactory = null)
    {
        _settings = settings;
        _timerFactory = timerFactory ?? DefaultCreateTimer;

        if (_settings is not null)
        {
            _settings.Loaded += SyncDismissedFromSettings;
            SyncDismissedFromSettings();
        }
    }

    private void SyncDismissedFromSettings()
    {
        if (_settings is null) return;
        foreach (var id in _settings.DismissedReminders)
        {
            if (_dismissed.Add(id))
            {
                ReminderDismissed?.Invoke(id);
            }
        }
    }

    public void MarkDismissed(string reminderId)
    {
        CancelSnooze(reminderId);
        _dismissed.Add(reminderId);
        _settings?.SaveDismissedReminders(_dismissed);
        ReminderDismissed?.Invoke(reminderId);
    }

    public bool IsDismissed(string reminderId)
    {
        if (_dismissed.Contains(reminderId))
        {
            return true;
        }

        if (_settings is not null && _settings.DismissedReminders.Contains(reminderId, StringComparer.OrdinalIgnoreCase))
        {
            _dismissed.Add(reminderId);
            return true;
        }

        return false;
    }

    public void ClearDismissed(string reminderId)
    {
        var removedLocal = _dismissed.Remove(reminderId);
        var inSettings = _settings is not null && _settings.DismissedReminders.Contains(reminderId, StringComparer.OrdinalIgnoreCase);
        if (removedLocal || inSettings)
        {
            _settings?.SaveDismissedReminders(_dismissed);
        }
    }

    public void Snooze(string reminderId, string body, DateTimeOffset dueAt, TimeSpan duration)
    {
        ClearDismissed(reminderId);
        CancelSnooze(reminderId);

        var info = new ReminderSnoozeInfo(reminderId, body, dueAt, duration, DateTimeOffset.Now);

        var timer = _timerFactory(duration, () =>
        {
            _snoozes.Remove(reminderId);
            ReminderUnsnoozed?.Invoke(reminderId);
            ReminderSnoozeExpired?.Invoke(info);
        });

        _snoozes[reminderId] = (info, timer);
        ReminderSnoozed?.Invoke(info);
    }

    public bool IsSnoozed(string reminderId, [NotNullWhen(true)] out ReminderSnoozeInfo? info)
    {
        if (_snoozes.TryGetValue(reminderId, out var entry))
        {
            info = entry.Info;
            return true;
        }

        info = null;
        return false;
    }

    public ReminderSnoozeInfo? GetSnooze(string reminderId) =>
        _snoozes.TryGetValue(reminderId, out var entry) ? entry.Info : null;

    public void CancelSnooze(string reminderId)
    {
        if (_snoozes.Remove(reminderId, out var entry))
        {
            entry.Timer.Dispose();
            ReminderUnsnoozed?.Invoke(reminderId);
        }
    }

    public void TriggerExpiredForTest(string reminderId)
    {
        if (_snoozes.Remove(reminderId, out var entry))
        {
            entry.Timer.Dispose();
            ReminderUnsnoozed?.Invoke(reminderId);
            ReminderSnoozeExpired?.Invoke(entry.Info);
        }
    }

    private static TimerHandle DefaultCreateTimer(TimeSpan interval, Action onTick)
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        {
            Interval = interval
        };

        void Handler(object? sender, EventArgs e)
        {
            timer.Stop();
            timer.Tick -= Handler;
            onTick();
        }

        timer.Tick += Handler;
        timer.Start();

        return new TimerHandle(timer, Handler);
    }

    private sealed class TimerHandle(DispatcherTimer timer, EventHandler handler) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            timer.Stop();
            timer.Tick -= handler;
        }
    }
}
