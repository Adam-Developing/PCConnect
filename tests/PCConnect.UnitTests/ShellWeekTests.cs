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
        shell.TodayColumn.Label.ShouldBe("Today");
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
}
