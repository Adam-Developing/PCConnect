using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PCConnect.Client;
using PCConnect.Companion.Services;
using PCConnect.Companion.ViewModels;
using PCConnect.Core.Contracts;
using Shouldly;

namespace PCConnect.UnitTests;

public sealed class ReminderSnoozeTests : IDisposable
{
    private readonly MockHttpMessageHandler _handler = new();
    private readonly HttpClient _http;
    private readonly PcConnectClient _api;
    private readonly ReminderSnoozeService _snoozeService;
    private readonly RemindersViewModel _vm;
    private Action? _capturedTimerTick;

    public ReminderSnoozeTests()
    {
        _http = new HttpClient(_handler);
        _api = new PcConnectClient(_http, new PcConnectClientOptions { BaseAddress = "http://localhost" }, new InMemoryTokenStore());
        _snoozeService = new ReminderSnoozeService((_, onTick) =>
        {
            _capturedTimerTick = onTick;
            return new DisposableAction(() => _capturedTimerTick = null);
        });
        _vm = new RemindersViewModel(_api,
            new DevicesViewModel(_api, NullLogger<DevicesViewModel>.Instance),
            NullLogger<RemindersViewModel>.Instance,
            _snoozeService);
        _vm.Initialise();
    }

    [Theory]
    [InlineData(10, "10 mins")]
    [InlineData(30, "30 mins")]
    [InlineData(60, "1 hour")]
    [InlineData(120, "2 hours")]
    public void ReminderSnoozeInfo_FormatDuration_formats_naturally(int minutes, string expected)
    {
        var duration = TimeSpan.FromMinutes(minutes);
        ReminderSnoozeInfo.FormatDuration(duration).ShouldBe(expected);
    }

    [Fact]
    public void Snooze_sets_active_snooze_and_fires_ReminderSnoozed_event()
    {
        ReminderSnoozeInfo? eventInfo = null;
        _snoozeService.ReminderSnoozed += info => eventInfo = info;

        var due = DateTimeOffset.Now.AddMinutes(-5);
        _snoozeService.Snooze("rem-1", "Doctor appointment", due, TimeSpan.FromMinutes(10));

        _snoozeService.IsSnoozed("rem-1", out var info).ShouldBeTrue();
        info.ShouldNotBeNull();
        info.ReminderId.ShouldBe("rem-1");
        info.Body.ShouldBe("Doctor appointment");
        info.Duration.ShouldBe(TimeSpan.FromMinutes(10));
        info.FormattedDuration.ShouldBe("10 mins");

        eventInfo.ShouldNotBeNull();
        eventInfo.ReminderId.ShouldBe("rem-1");
        eventInfo.FormattedDuration.ShouldBe("10 mins");
    }

    [Fact]
    public void CancelSnooze_clears_active_snooze_and_fires_ReminderUnsnoozed_event()
    {
        string? unsnoozedId = null;
        _snoozeService.ReminderUnsnoozed += id => unsnoozedId = id;

        _snoozeService.Snooze("rem-2", "Buy bread", DateTimeOffset.Now, TimeSpan.FromMinutes(30));
        _snoozeService.IsSnoozed("rem-2", out _).ShouldBeTrue();

        _snoozeService.CancelSnooze("rem-2");
        _snoozeService.IsSnoozed("rem-2", out _).ShouldBeFalse();
        unsnoozedId.ShouldBe("rem-2");
    }

    [Fact]
    public void Snooze_expiration_fires_ReminderSnoozeExpired_and_unsnoozes()
    {
        ReminderSnoozeInfo? expiredInfo = null;
        string? unsnoozedId = null;

        _snoozeService.ReminderSnoozeExpired += info => expiredInfo = info;
        _snoozeService.ReminderUnsnoozed += id => unsnoozedId = id;

        _snoozeService.Snooze("rem-3", "Water plants", DateTimeOffset.Now, TimeSpan.FromMinutes(10));
        _capturedTimerTick.ShouldNotBeNull();

        _capturedTimerTick();

        _snoozeService.IsSnoozed("rem-3", out _).ShouldBeFalse();
        unsnoozedId.ShouldBe("rem-3");
        expiredInfo.ShouldNotBeNull();
        expiredInfo.ReminderId.ShouldBe("rem-3");
        expiredInfo.FormattedDuration.ShouldBe("10 mins");
    }

    [Fact]
    public void ReminderRow_IsDismissedBadgeVisible_is_false_when_IsSnoozed_is_true()
    {
        // When a reminder is past due and dismissed but snoozed, it is in a snoozed state, not won't rerun.
        var snoozedRow = new ReminderRow(
            Id: "rem-snoozed",
            Time: "22:00",
            DayLabel: "Today",
            Body: "Late task",
            Detail: "all PCs",
            IsCompleted: false,
            IsPast: true,
            IsSnoozed: true,
            SnoozeLabel: "Snoozed for 10 mins",
            IsDismissed: true);

        snoozedRow.IsPast.ShouldBeTrue();
        snoozedRow.IsSnoozed.ShouldBeTrue();
        snoozedRow.IsDismissedBadgeVisible.ShouldBeFalse();
        snoozedRow.SnoozeLabel.ShouldBe("Snoozed for 10 mins");

        // When not snoozed, a dismissed reminder shows the Won't rerun badge
        var dismissedRow = snoozedRow with { IsSnoozed = false, SnoozeLabel = null };
        dismissedRow.IsDismissedBadgeVisible.ShouldBeTrue();
        dismissedRow.DismissedLabel.ShouldBe("Won't rerun");
    }

    [Fact]
    public void RebuildRows_renders_snooze_badge_and_label_for_snoozed_reminders()
    {
        var due = DateTimeOffset.Now.AddHours(-1);
        var item = new ReminderResponse(
            Id: "rem-100",
            Body: "Team meeting",
            DueAt: due,
            DueLocalTime: due.ToLocalTime().ToString("HH:mm"),
            Timezone: "UTC",
            Rrule: null,
            RecurrenceUntil: null,
            IsCompleted: false,
            CompletedAt: null,
            CreatedAt: due,
            UpdatedAt: due,
            DeviceIds: null);

        _vm.Items.Add(item);

        // Before snoozing: regular row, overdue if past
        _vm.RebuildRows();
        var rowBefore = _vm.Rows.Single(r => r.Id == "rem-100");
        rowBefore.IsSnoozed.ShouldBeFalse();
        rowBefore.SnoozeLabel.ShouldBeNull();

        // Snooze for 10 mins
        _snoozeService.Snooze("rem-100", "Team meeting", due, TimeSpan.FromMinutes(10));
        _vm.RebuildRows();

        var rowAfter = _vm.Rows.Single(r => r.Id == "rem-100");
        rowAfter.IsSnoozed.ShouldBeTrue();
        rowAfter.SnoozeLabel.ShouldBe("Snoozed for 10 mins");
        rowAfter.SnoozeToolTip.ShouldNotBeNull();
        rowAfter.SnoozeToolTip.ShouldContain("Snoozed for 10 mins");
        rowAfter.IsOverdue.ShouldBeFalse();
    }

    [Fact]
    public void Snoozed_reminder_remains_visible_in_all_events_even_when_completed_hidden()
    {
        var due = DateTimeOffset.Now.AddDays(-1);
        var item = new ReminderResponse(
            Id: "rem-past-snoozed",
            Body: "Yesterday task",
            DueAt: due,
            DueLocalTime: "10:00",
            Timezone: "UTC",
            Rrule: null,
            RecurrenceUntil: null,
            IsCompleted: false,
            CompletedAt: null,
            CreatedAt: due,
            UpdatedAt: due);

        _vm.Items.Add(item);
        _vm.ShowCompletedReminders = false;

        _snoozeService.Snooze("rem-past-snoozed", "Yesterday task", due, TimeSpan.FromMinutes(30));
        _vm.RebuildRows();

        var row = _vm.Rows.FirstOrDefault(r => r.Id == "rem-past-snoozed");
        row.ShouldNotBeNull();
        row.IsSnoozed.ShouldBeTrue();
        row.SnoozeLabel.ShouldBe("Snoozed for 30 mins");
    }

    [Fact]
    public async Task CompleteAsync_cancels_active_snooze()
    {
        _handler.PostHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new ReminderResponse(
                "rem-done", "Task", DateTimeOffset.Now, "10:00", "UTC", null, null, true, DateTimeOffset.Now, DateTimeOffset.Now, DateTimeOffset.Now)))
        };
        _handler.GetHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new Page<ReminderResponse>([], null)))
        };

        var row = new ReminderRow("rem-done", "10:00", "Today", "Task", "all PCs", false, false, true, "Snoozed for 10 mins");
        _snoozeService.Snooze("rem-done", "Task", DateTimeOffset.Now, TimeSpan.FromMinutes(10));
        _snoozeService.IsSnoozed("rem-done", out _).ShouldBeTrue();

        await _vm.CompleteCommand.ExecuteAsync(row);

        _snoozeService.IsSnoozed("rem-done", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteAsync_cancels_active_snooze()
    {
        _handler.DeleteHandler = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        _handler.GetHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new Page<ReminderResponse>([], null)))
        };

        var row = new ReminderRow("rem-delete", "10:00", "Today", "Task", "all PCs", false, false, true, "Snoozed for 10 mins");
        _snoozeService.Snooze("rem-delete", "Task", DateTimeOffset.Now, TimeSpan.FromMinutes(10));
        _snoozeService.IsSnoozed("rem-delete", out _).ShouldBeTrue();

        await _vm.DeleteCommand.ExecuteAsync(row);

        _snoozeService.IsSnoozed("rem-delete", out _).ShouldBeFalse();
    }

    [Fact]
    public void Overdue_reminder_on_today_shows_wont_rerun_badge_only_when_dismissed()
    {
        var pastTimeToday = DateTime.Now.AddHours(-1);
        var due = new DateTimeOffset(pastTimeToday);
        _vm.Items.Add(new ReminderResponse(
            "rem-overdue-today",
            "rem-overdue-today",
            due,
            "Past task",
            TimeZoneInfo.Local.Id,
            null,
            null,
            false,
            null,
            due,
            due));

        _vm.RebuildRows();

        var row = _vm.Rows.FirstOrDefault(r => r.Id == "rem-overdue-today");
        row.ShouldNotBeNull();
        row.IsPast.ShouldBeTrue();
        row.IsDismissed.ShouldBeFalse();
        row.IsDismissedBadgeVisible.ShouldBeFalse();

        // Explicitly dismiss as "Don't remind me"
        _snoozeService.MarkDismissed("rem-overdue-today");

        var dismissedRow = _vm.Rows.FirstOrDefault(r => r.Id == "rem-overdue-today");
        dismissedRow.ShouldNotBeNull();
        dismissedRow.IsDismissed.ShouldBeTrue();
        dismissedRow.IsDismissedBadgeVisible.ShouldBeTrue();
        dismissedRow.DismissedLabel.ShouldBe("Won't rerun");
        dismissedRow.DismissedToolTip.ShouldContain("will not alert again");
    }

    [Fact]
    public void Dismissed_reminders_persist_across_reopen_and_settings_reloads()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pcconnect_settings_test_{Guid.NewGuid():N}.json");
        try
        {
            var settings = new CompanionSettings(NullLogger<CompanionSettings>.Instance, tempFile);
            var service1 = new ReminderSnoozeService(settings, (_, _) => new DisposableAction(() => { }));

            // Mark a reminder dismissed
            const string reminderId = "01a0d667-D1F4-71d5-bb79-41c63e34f9f5";
            service1.MarkDismissed(reminderId);
            service1.IsDismissed(reminderId).ShouldBeTrue();

            // Simulate app restart: new CompanionSettings reading from the same file, new ReminderSnoozeService
            var reloadedSettings = new CompanionSettings(NullLogger<CompanionSettings>.Instance, tempFile);
            var service2 = new ReminderSnoozeService(reloadedSettings, (_, _) => new DisposableAction(() => { }));

            // Verify it is recognized immediately and case-insensitively
            service2.IsDismissed(reminderId.ToLowerInvariant()).ShouldBeTrue();
            service2.IsDismissed(reminderId.ToUpperInvariant()).ShouldBeTrue();

            // When vm rebuilds rows with this service, row shows won't rerun
            var due = DateTimeOffset.Now.AddHours(-2);
            var vm2 = new RemindersViewModel(_api,
                new DevicesViewModel(_api, NullLogger<DevicesViewModel>.Instance),
                NullLogger<RemindersViewModel>.Instance,
                service2);
            vm2.Items.Add(new ReminderResponse(
                reminderId,
                reminderId,
                due,
                "gdfgdfgdf",
                TimeZoneInfo.Local.Id,
                null,
                null,
                false,
                null,
                due,
                due));

            vm2.RebuildRows();

            var row = vm2.Rows.Single(r => r.Id.Equals(reminderId, StringComparison.OrdinalIgnoreCase));
            row.IsDismissed.ShouldBeTrue();
            row.IsDismissedBadgeVisible.ShouldBeTrue();
            row.DismissedLabel.ShouldBe("Won't rerun");
            row.DismissedToolTip.ShouldContain("will not alert again");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    private sealed class DisposableAction(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? DeleteHandler { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? GetHandler { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? PostHandler { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete && DeleteHandler != null)
            {
                return Task.FromResult(DeleteHandler(request));
            }
            if (request.Method == HttpMethod.Get && GetHandler != null)
            {
                return Task.FromResult(GetHandler(request));
            }
            if (request.Method == HttpMethod.Post && PostHandler != null)
            {
                return Task.FromResult(PostHandler(request));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
