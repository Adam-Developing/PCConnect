using Microsoft.Extensions.Logging.Abstractions;
using PCConnect.Agent.Execution;
using PCConnect.Client;
using PCConnect.Companion.Services;
using PCConnect.Companion.ViewModels;
using PCConnect.Core.Contracts;
using Shouldly;

namespace PCConnect.UnitTests;

public sealed class ShellWeekTests : IDisposable
{
    private readonly HttpClient _http = new();

    public void Dispose() => _http.Dispose();

    [Fact]
    public void TodayColumn_matches_today_and_contains_only_todays_reminders()
    {
        var api = new PcConnectClient(_http, new PcConnectClientOptions { BaseAddress = "http://localhost" }, new InMemoryTokenStore());
        var realtime = new PcConnectRealtimeClient(api, NullLogger<PcConnectRealtimeClient>.Instance);
        var settings = new CompanionSettings(NullLogger<CompanionSettings>.Instance);
        var startup = new StartupRegistration(NullLogger<StartupRegistration>.Instance);
        var provisioning = new ProvisioningPipeClient(NullLogger<ProvisioningPipeClient>.Instance);
        var devices = new DevicesViewModel(api, NullLogger<DevicesViewModel>.Instance);
        var reminders = new RemindersViewModel(api, devices, NullLogger<RemindersViewModel>.Instance);
        var account = new AccountViewModel(api, NullLogger<AccountViewModel>.Instance);
        var appSettings = new SettingsViewModel(api, settings, startup, devices, NullLogger<SettingsViewModel>.Instance);
        var shell = new ShellViewModel(api, realtime, settings, provisioning, devices, reminders, account, appSettings, NullLogger<ShellViewModel>.Instance);

        var today = DateTime.Today;
        var notToday = today.AddDays(today.DayOfWeek == DayOfWeek.Monday ? 1 : -1);

        reminders.Items.Add(new ReminderResponse(
            Id: "rem-today",
            Body: "Today reminder",
            DueAt: new DateTimeOffset(today.Year, today.Month, today.Day, 10, 0, 0, TimeSpan.Zero),
            DueLocalTime: "10:00",
            Timezone: "UTC",
            Rrule: null,
            RecurrenceUntil: null,
            IsCompleted: false,
            CompletedAt: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            DeviceIds: []));

        reminders.Items.Add(new ReminderResponse(
            Id: "rem-other",
            Body: "Other day reminder",
            DueAt: new DateTimeOffset(notToday.Year, notToday.Month, notToday.Day, 14, 0, 0, TimeSpan.Zero),
            DueLocalTime: "14:00",
            Timezone: "UTC",
            Rrule: null,
            RecurrenceUntil: null,
            IsCompleted: false,
            CompletedAt: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            DeviceIds: []));

        shell.RebuildWeek();

        shell.TodayColumn.ShouldNotBeNull();
        shell.TodayColumn.IsToday.ShouldBeTrue();
        shell.TodayColumn.Label.ShouldBe(today.ToString("ddd", System.Globalization.CultureInfo.CurrentCulture));
        shell.TodayColumn.IsEmpty.ShouldBeFalse();
        shell.TodayColumn.Items.Count.ShouldBe(1);
        shell.TodayColumn.Items[0].Id.ShouldBe("rem-today");
    }

    [Fact]
    public void OnClockTick_raises_property_changed_when_today_string_changes()
    {
        var api = new PcConnectClient(_http, new PcConnectClientOptions { BaseAddress = "http://localhost" }, new InMemoryTokenStore());
        var realtime = new PcConnectRealtimeClient(api, NullLogger<PcConnectRealtimeClient>.Instance);
        var settings = new CompanionSettings(NullLogger<CompanionSettings>.Instance);
        var startup = new StartupRegistration(NullLogger<StartupRegistration>.Instance);
        var provisioning = new ProvisioningPipeClient(NullLogger<ProvisioningPipeClient>.Instance);
        var devices = new DevicesViewModel(api, NullLogger<DevicesViewModel>.Instance);
        var reminders = new RemindersViewModel(api, devices, NullLogger<RemindersViewModel>.Instance);
        var account = new AccountViewModel(api, NullLogger<AccountViewModel>.Instance);
        var appSettings = new SettingsViewModel(api, settings, startup, devices, NullLogger<SettingsViewModel>.Instance);
        var shell = new ShellViewModel(api, realtime, settings, provisioning, devices, reminders, account, appSettings, NullLogger<ShellViewModel>.Instance);

        var propertyChangedFired = false;
        shell.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(shell.Today))
            {
                propertyChangedFired = true;
            }
        };

        shell.OnClockTick();
        propertyChangedFired.ShouldBeTrue();
    }

    [Fact]
    public void ShowEventDetail_opens_details_for_both_one_time_and_recurring_today_items()
    {
        var api = new PcConnectClient(_http, new PcConnectClientOptions { BaseAddress = "http://localhost" }, new InMemoryTokenStore());
        var realtime = new PcConnectRealtimeClient(api, NullLogger<PcConnectRealtimeClient>.Instance);
        var settings = new CompanionSettings(NullLogger<CompanionSettings>.Instance);
        var startup = new StartupRegistration(NullLogger<StartupRegistration>.Instance);
        var provisioning = new ProvisioningPipeClient(NullLogger<ProvisioningPipeClient>.Instance);
        var devices = new DevicesViewModel(api, NullLogger<DevicesViewModel>.Instance);
        var reminders = new RemindersViewModel(api, devices, NullLogger<RemindersViewModel>.Instance);
        var account = new AccountViewModel(api, NullLogger<AccountViewModel>.Instance);
        var appSettings = new SettingsViewModel(api, settings, startup, devices, NullLogger<SettingsViewModel>.Instance);
        var shell = new ShellViewModel(api, realtime, settings, provisioning, devices, reminders, account, appSettings, NullLogger<ShellViewModel>.Instance);

        var today = DateTime.Today;
        devices.Items.Add(new DeviceItem(new DeviceResponse(
            Id: "pc-1",
            DisplayName: "ADAM-PC",
            Platform: "windows",
            OsVersion: "Windows 11",
            AgentVersion: "2.0.0",
            Status: "active",
            IsOnline: true,
            LastSeenAt: DateTimeOffset.UtcNow,
            PairedAt: DateTimeOffset.UtcNow,
            AllowedCommands: ["lock", "restart"],
            PasswordRequiredCommands: ["restart"])));

        reminders.Items.Add(new ReminderResponse(
            Id: "rem-once",
            Body: "One-time task",
            DueAt: new DateTimeOffset(today.Year, today.Month, today.Day, 11, 0, 0, TimeSpan.Zero),
            DueLocalTime: "11:00",
            Timezone: "UTC",
            Rrule: null,
            RecurrenceUntil: null,
            IsCompleted: false,
            CompletedAt: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            DeviceIds: ["pc-1"]));

        reminders.Items.Add(new ReminderResponse(
            Id: "rem-rec",
            Body: "Recurring task",
            DueAt: new DateTimeOffset(today.Year, today.Month, today.Day, 9, 0, 0, TimeSpan.Zero),
            DueLocalTime: "09:00",
            Timezone: "UTC",
            Rrule: "FREQ=DAILY",
            RecurrenceUntil: null,
            IsCompleted: false,
            CompletedAt: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            DeviceIds: null));

        shell.RebuildWeek();

        var oneTimeItem = shell.TodayColumn.Items.Single(i => i.Id == "rem-once");
        shell.ShowEventDetail(oneTimeItem);
        shell.IsEventDetailOpen.ShouldBeTrue();
        shell.ActiveEventDetail.ShouldNotBeNull();
        shell.ActiveEventDetail!.Title.ShouldBe("One-time task");
        shell.ActiveEventDetail.IsRecurring.ShouldBeFalse();
        shell.ActiveEventDetail.Targets.ShouldBe("ADAM-PC");

        var recurringItem = shell.TodayColumn.Items.Single(i => i.Id == "rem-rec");
        shell.ShowEventDetail(recurringItem);
        shell.IsEventDetailOpen.ShouldBeTrue();
        shell.ActiveEventDetail.ShouldNotBeNull();
        shell.ActiveEventDetail!.Title.ShouldBe("Recurring task");
        shell.ActiveEventDetail.IsRecurring.ShouldBeTrue();
    }

    [Fact]
    public void SortOccurrencesOldestToNewest_orders_chronologically_and_parses_hourly_interval()
    {
        var items = new[]
        {
            new ReminderOccurrenceResponse(3, "r1", DateTimeOffset.Parse("2026-11-03T07:00:00Z"), "pending", null, null, "rhhggd"),
            new ReminderOccurrenceResponse(1, "r1", DateTimeOffset.Parse("2026-10-30T19:00:00Z"), "completed", null, null, "rhhggd"),
            new ReminderOccurrenceResponse(2, "r1", DateTimeOffset.Parse("2026-11-01T13:00:00Z"), "pending", null, null, "rhhggd"),
        };

        var sorted = ShellViewModel.SortOccurrencesOldestToNewest(items);
        sorted.Select(o => o.Id).ShouldBe([1L, 2L, 3L]);

        ShellViewModel.ParseHourlyInterval("FREQ=HOURLY;INTERVAL=21").ShouldBe(21);
        ShellViewModel.ParseHourlyInterval("FREQ=HOURLY").ShouldBe(1);
    }

    [Fact]
    public void Target_desktop_preferences_govern_available_and_protected_commands_per_desktop()
    {
        var desktopA = new DeviceItem(new DeviceResponse(
            Id: "pc-a",
            DisplayName: "ADAM-PC",
            Platform: "windows",
            OsVersion: "Windows 11",
            AgentVersion: "2.0.0",
            Status: "active",
            IsOnline: true,
            LastSeenAt: DateTimeOffset.UtcNow,
            PairedAt: DateTimeOffset.UtcNow,
            AllowedCommands: ["lock", "restart"],
            PasswordRequiredCommands: ["restart"]));

        var desktopB = new DeviceItem(new DeviceResponse(
            Id: "pc-b",
            DisplayName: "SECOND-PC",
            Platform: "windows",
            OsVersion: "Windows 11",
            AgentVersion: "2.0.0",
            Status: "active",
            IsOnline: true,
            LastSeenAt: DateTimeOffset.UtcNow,
            PairedAt: DateTimeOffset.UtcNow,
            AllowedCommands: ["lock", "sleep", "shutdown"],
            PasswordRequiredCommands: ["lock", "shutdown"]));

        DevicesViewModel.IsCommandAllowed(desktopA, "lock").ShouldBeTrue();
        DevicesViewModel.IsCommandAllowed(desktopA, "shutdown").ShouldBeFalse();
        DevicesViewModel.IsCommandProtected(desktopA, "restart").ShouldBeTrue();
        DevicesViewModel.IsCommandProtected(desktopA, "lock").ShouldBeFalse();
        DevicesViewModel.IsCommandProtected(desktopA, "shutdown").ShouldBeFalse();

        // Preferences from desktopA do not affect desktopB
        DevicesViewModel.IsCommandAllowed(desktopB, "shutdown").ShouldBeTrue();
        DevicesViewModel.IsCommandAllowed(desktopB, "restart").ShouldBeFalse();
        DevicesViewModel.IsCommandProtected(desktopB, "lock").ShouldBeTrue();
    }

    [Fact]
    public void RebuildWeek_tracks_completion_per_occurrence_and_never_marks_future_occurrences_completed_from_series_flag()
    {
        var api = new PcConnectClient(_http, new PcConnectClientOptions { BaseAddress = "http://localhost" }, new InMemoryTokenStore());
        var realtime = new PcConnectRealtimeClient(api, NullLogger<PcConnectRealtimeClient>.Instance);
        var settings = new CompanionSettings(NullLogger<CompanionSettings>.Instance);
        var startup = new StartupRegistration(NullLogger<StartupRegistration>.Instance);
        var provisioning = new ProvisioningPipeClient(NullLogger<ProvisioningPipeClient>.Instance);
        var devices = new DevicesViewModel(api, NullLogger<DevicesViewModel>.Instance);
        var reminders = new RemindersViewModel(api, devices, NullLogger<RemindersViewModel>.Instance, settings: settings);
        var account = new AccountViewModel(api, NullLogger<AccountViewModel>.Instance);
        var appSettings = new SettingsViewModel(api, settings, startup, devices, NullLogger<SettingsViewModel>.Instance);
        var shell = new ShellViewModel(api, realtime, settings, provisioning, devices, reminders, account, appSettings, NullLogger<ShellViewModel>.Instance);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var daysFromMonday = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.AddDays(-daysFromMonday);
        var monday9amLocal = new DateTimeOffset(DateTime.SpecifyKind(monday.ToDateTime(new TimeOnly(9, 0)), DateTimeKind.Local));
        var tuesday9amLocal = new DateTimeOffset(DateTime.SpecifyKind(monday.AddDays(1).ToDateTime(new TimeOnly(9, 0)), DateTimeKind.Local));
        var wednesday9amLocal = new DateTimeOffset(DateTime.SpecifyKind(monday.AddDays(2).ToDateTime(new TimeOnly(9, 0)), DateTimeKind.Local));

        // Simulate a recurring series whose parent IsCompleted was polluted to true.
        reminders.Items.Add(new ReminderResponse(
            Id: "rem-series",
            Body: "rhhggd",
            DueAt: monday9amLocal.ToUniversalTime(),
            DueLocalTime: "09:00",
            Timezone: TimeZoneInfo.Local.Id,
            Rrule: "FREQ=DAILY",
            RecurrenceUntil: null,
            IsCompleted: true,
            CompletedAt: monday9amLocal.ToUniversalTime(),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            DeviceIds: null));

        // Before occurrences load, NO recurring occurrence is falsely marked completed.
        shell.RebuildWeek();
        shell.Week.Count.ShouldBe(7);
        shell.Week.SelectMany(d => d.Items).ShouldAllBe(i => !i.IsCompleted);

        // Mark Monday completed, Tuesday pending with custom text, Wednesday skipped.
        reminders.SetOccurrences("rem-series",
        [
            new ReminderOccurrenceResponse(1, "rem-series", monday9amLocal.ToUniversalTime(), "completed", monday9amLocal, null, "rhhggd"),
            new ReminderOccurrenceResponse(2, "rem-series", tuesday9amLocal.ToUniversalTime(), "pending", null, "Custom Tuesday", "Custom Tuesday"),
            new ReminderOccurrenceResponse(3, "rem-series", wednesday9amLocal.ToUniversalTime(), "skipped", null, null, "rhhggd"),
        ]);
        shell.RebuildWeek();

        shell.Week[0].Items.Single().IsCompleted.ShouldBeTrue();
        shell.Week[1].Items.Single().IsCompleted.ShouldBeFalse();
        shell.Week[1].Items.Single().Body.ShouldBe("Custom Tuesday");
        shell.Week[2].Items.ShouldBeEmpty();
        for (var idx = 3; idx < 7; idx++)
        {
            shell.Week[idx].Items.Single().IsCompleted.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ShowEventDetail_for_recurring_row_opens_active_series_and_syncs_toggled_occurrence_to_week()
    {
        var api = new PcConnectClient(_http, new PcConnectClientOptions { BaseAddress = "http://localhost" }, new InMemoryTokenStore());
        var realtime = new PcConnectRealtimeClient(api, NullLogger<PcConnectRealtimeClient>.Instance);
        var settings = new CompanionSettings(NullLogger<CompanionSettings>.Instance) { Use24HourClock = true };
        var startup = new StartupRegistration(NullLogger<StartupRegistration>.Instance);
        var provisioning = new ProvisioningPipeClient(NullLogger<ProvisioningPipeClient>.Instance);
        var devices = new DevicesViewModel(api, NullLogger<DevicesViewModel>.Instance);
        var reminders = new RemindersViewModel(api, devices, NullLogger<RemindersViewModel>.Instance, settings: settings);
        var account = new AccountViewModel(api, NullLogger<AccountViewModel>.Instance);
        var appSettings = new SettingsViewModel(api, settings, startup, devices, NullLogger<SettingsViewModel>.Instance);
        var shell = new ShellViewModel(api, realtime, settings, provisioning, devices, reminders, account, appSettings, NullLogger<ShellViewModel>.Instance);

        var startWall = new DateTime(2026, 10, 28, 4, 0, 0, DateTimeKind.Local);
        var nov2 = new DateOnly(2026, 11, 2);
        var nov5 = new DateOnly(2026, 11, 5);
        var nov2Instants = Recurrence.OccurrencesOn("FREQ=HOURLY;INTERVAL=21", new DateTimeOffset(startWall), nov2);
        var nov5Instants = Recurrence.OccurrencesOn("FREQ=HOURLY;INTERVAL=21", new DateTimeOffset(startWall), nov5);
        nov2Instants.Select(i => i.Hour).ShouldBe([10]);
        nov5Instants.Select(i => i.Hour).ShouldBe([1, 22]);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var daysFromMonday = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.AddDays(-daysFromMonday);
        var mondayDue = new DateTimeOffset(DateTime.SpecifyKind(monday.ToDateTime(new TimeOnly(8, 30)), DateTimeKind.Local));

        reminders.Items.Add(new ReminderResponse(
            Id: "rem-hourly",
            Body: "Hourly check",
            DueAt: mondayDue.ToUniversalTime(),
            DueLocalTime: "08:30",
            Timezone: TimeZoneInfo.Local.Id,
            Rrule: "FREQ=DAILY",
            RecurrenceUntil: null,
            IsCompleted: true,
            CompletedAt: DateTimeOffset.UtcNow,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            DeviceIds: null));
        reminders.RebuildRows();
        shell.RebuildWeek();

        var recurringRow = reminders.RecurringRows.Single();
        recurringRow.IsCompleted.ShouldBeFalse();

        shell.ShowEventDetailCommand.Execute(recurringRow);
        shell.IsEventDetailOpen.ShouldBeTrue();
        shell.ActiveEventDetail.ShouldNotBeNull();
        shell.ActiveEventDetail!.IsRecurring.ShouldBeTrue();
        shell.ActiveEventDetail.IsCompleted.ShouldBeFalse();

        var todayOccTime = new DateTimeOffset(DateTime.SpecifyKind(today.ToDateTime(new TimeOnly(8, 30)), DateTimeKind.Local));
        var occVm = new OccurrenceItemViewModel
        {
            ReminderId = "rem-hourly",
            OccursAt = todayOccTime,
            SeriesBody = "Hourly check",
            Body = "Hourly check",
            Status = "pending",
        };

        await shell.ToggleOccurrenceCompleteCommand.ExecuteAsync(occVm);

        shell.TodayColumn.Items.Single().IsCompleted.ShouldBeTrue();
        var otherDayIdx = today == monday ? 1 : 0;
        shell.Week[otherDayIdx].Items.Single().IsCompleted.ShouldBeFalse();
    }
}
