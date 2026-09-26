using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PCConnect.Client;
using PCConnect.Companion.ViewModels;
using PCConnect.Core.Contracts;
using Shouldly;

namespace PCConnect.UnitTests;

public sealed class ReminderCrudTests : IDisposable
{
    private readonly MockHttpMessageHandler _handler = new();
    private readonly HttpClient _http;
    private readonly PcConnectClient _api;
    private readonly RemindersViewModel _vm;

    public ReminderCrudTests()
    {
        _http = new HttpClient(_handler);
        _api = new PcConnectClient(_http, new PcConnectClientOptions { BaseAddress = "http://localhost" }, new InMemoryTokenStore());
        _vm = new RemindersViewModel(_api,
            new DevicesViewModel(_api, NullLogger<DevicesViewModel>.Instance),
            NullLogger<RemindersViewModel>.Instance);
        _vm.Initialise();
    }

    [Theory]
    [InlineData(null, RepeatKind.Once, 1)]
    [InlineData("", RepeatKind.Once, 1)]
    [InlineData("FREQ=WEEKLY", RepeatKind.Weekly, 1)]
    [InlineData("FREQ=MONTHLY", RepeatKind.Monthly, 1)]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR", RepeatKind.Custom, 2)]
    public void Recurrence_FromRrule_parses_rules_accurately(string? rrule, RepeatKind expectedKind, int expectedInterval)
    {
        var (kind, days, interval) = Recurrence.FromRrule(rrule);
        kind.ShouldBe(expectedKind);
        interval.ShouldBe(expectedInterval);

        if (rrule == "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR")
        {
            days.ShouldBe([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]);
        }
    }

    [Fact]
    public void StartEdit_populates_viewmodel_state()
    {
        var due = new DateTimeOffset(2026, 10, 15, 14, 30, 0, TimeSpan.Zero);
        var until = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);
        var item = new ReminderResponse(
            Id: "rem-123",
            Body: "Team review",
            DueAt: due,
            DueLocalTime: "14:30",
            Timezone: "UTC",
            Rrule: "FREQ=WEEKLY",
            RecurrenceUntil: until,
            IsCompleted: false,
            CompletedAt: null,
            CreatedAt: due,
            UpdatedAt: due,
            DeviceIds: ["dev-1"]);

        _vm.Items.Add(item);
        _vm.RebuildCalendar();

        var row = new ReminderRow("rem-123", "14:30", "Thu 15 Oct", "Team review", "Every week", false, false);

        _vm.IsEditing.ShouldBeFalse();
        _vm.FormTitle.ShouldBe("New reminder");
        _vm.SaveButtonText.ShouldBe("Save reminder");

        _vm.StartEditCommand.Execute(row);

        _vm.IsEditing.ShouldBeTrue();
        _vm.EditingReminderId.ShouldBe("rem-123");
        _vm.FormTitle.ShouldBe("Edit reminder");
        _vm.SaveButtonText.ShouldBe("Save changes");
        _vm.NewBody.ShouldBe("Team review");
        _vm.Repeat.ShouldBe(RepeatKind.Weekly);
        _vm.Until.ShouldBe(until.ToLocalTime().DateTime);
    }

    [Fact]
    public void CancelEdit_resets_viewmodel_to_creation_mode()
    {
        var due = DateTimeOffset.Now;
        var item = new ReminderResponse("rem-456", "Clean desk", due, "10:00", "UTC", null, null, false, null, due, due);
        _vm.Items.Add(item);

        var row = new ReminderRow("rem-456", "10:00", "Today", "Clean desk", "", false, false);
        _vm.StartEditCommand.Execute(row);

        _vm.IsEditing.ShouldBeTrue();

        _vm.CancelEditCommand.Execute(null);

        _vm.IsEditing.ShouldBeFalse();
        _vm.EditingReminderId.ShouldBeNull();
        _vm.FormTitle.ShouldBe("New reminder");
        _vm.SaveButtonText.ShouldBe("Save reminder");
        _vm.NewBody.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_calls_api_and_cleans_edit_state()
    {
        _handler.DeleteHandler = req =>
        {
            req.Method.ShouldBe(HttpMethod.Delete);
            req.RequestUri!.AbsolutePath.ShouldBe("/v2/reminders/rem-789");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };
        _handler.GetHandler = req =>
        {
            var page = new Page<ReminderResponse>([], null);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(page))
            };
        };

        var due = DateTimeOffset.Now;
        var item = new ReminderResponse("rem-789", "Buy milk", due, "08:00", "UTC", null, null, false, null, due, due);
        _vm.Items.Add(item);

        var row = new ReminderRow("rem-789", "08:00", "Today", "Buy milk", "", false, false);
        _vm.StartEditCommand.Execute(row);

        await _vm.DeleteCommand.ExecuteAsync(row);

        _vm.IsEditing.ShouldBeFalse();
        _vm.EditingReminderId.ShouldBeNull();
        _vm.StatusMessage.ShouldBe("Reminder deleted.");
        _vm.IsStatusError.ShouldBeFalse();
    }

    [Fact]
    public async Task AddAsync_with_zero_targets_selected_sets_status_error()
    {
        _vm.IsTargetable = true;
        _vm.ShowOnAllPcs = false;
        _vm.Targets.Clear();
        _vm.NewBody = "Test reminder";

        await _vm.AddCommand.ExecuteAsync(null);

        _vm.IsStatusError.ShouldBeTrue();
        _vm.StatusMessage.ShouldBe("Pick at least one PC, or choose All PCs.");
    }

    [Fact]
    public void TargetToggle_Tag_returns_thisPc_when_IsThisPc_is_true()
    {
        var targetThisPc = new TargetToggle
        {
            DeviceId = "dev-1",
            Name = "ADAM-PC",
            IsOnline = false,
            IsThisPc = true
        };
        targetThisPc.Tag.ShouldBe("this PC");

        var targetOnline = new TargetToggle
        {
            DeviceId = "dev-2",
            Name = "WORK-PC",
            IsOnline = true,
            IsThisPc = false
        };
        targetOnline.Tag.ShouldBe("online");

        var targetOffline = new TargetToggle
        {
            DeviceId = "dev-3",
            Name = "OFFICE-PC",
            IsOnline = false,
            IsThisPc = false
        };
        targetOffline.Tag.ShouldBe("offline");
    }

    [Fact]
    public void ShowStatus_initializes_message_error_flag_and_full_progress()
    {
        _vm.ShowStatus("Reminder saved.", false);

        _vm.StatusMessage.ShouldBe("Reminder saved.");
        _vm.IsStatusError.ShouldBeFalse();
        _vm.StatusProgress.ShouldBe(1.0);

        _vm.ShowStatus("Something went wrong.", true);

        _vm.StatusMessage.ShouldBe("Something went wrong.");
        _vm.IsStatusError.ShouldBeTrue();
        _vm.StatusProgress.ShouldBe(1.0);
    }

    [Fact]
    public void DismissStatus_clears_status_and_resets_progress()
    {
        _vm.ShowStatus("Active banner", false);
        _vm.StatusMessage.ShouldBe("Active banner");

        _vm.DismissStatus();

        _vm.StatusMessage.ShouldBeEmpty();
        _vm.StatusProgress.ShouldBe(1.0);
    }

    [Fact]
    public void ApplyPresence_updates_target_online_status()
    {
        var target = new TargetToggle
        {
            DeviceId = "dev-target-1",
            Name = "DESKTOP",
            IsOnline = false,
            IsThisPc = false
        };
        _vm.Targets.Add(target);

        _vm.ApplyPresence(new DevicePresenceEvent("dev-target-1", true));
        target.IsOnline.ShouldBeTrue();
        target.Tag.ShouldBe("online");

        _vm.ApplyPresence(new DevicePresenceEvent("dev-target-1", false));
        target.IsOnline.ShouldBeFalse();
        target.Tag.ShouldBe("offline");
    }

    [Fact]
    public void ReminderRow_dismissed_badge_returns_true_only_when_dismissed_and_incomplete()
    {
        var pastIncomplete = new ReminderRow("1", "10:00", "Yesterday", "Fix bug", "", false, true);
        pastIncomplete.IsDismissedBadgeVisible.ShouldBeFalse();

        var dismissed = pastIncomplete with { IsDismissed = true };
        dismissed.IsDismissedBadgeVisible.ShouldBeTrue();
        dismissed.DismissedLabel.ShouldBe("Won't rerun");
        dismissed.DismissedToolTip.ShouldContain("will not alert again");

        var completedPast = new ReminderRow("2", "10:00", "Yesterday", "Fix bug", "", true, true, IsDismissed: true);
        completedPast.IsDismissedBadgeVisible.ShouldBeFalse();

        var upcoming = new ReminderRow("3", "10:00", "Tomorrow", "Fix bug", "", false, false, IsDismissed: true);
        upcoming.IsDismissedBadgeVisible.ShouldBeTrue();
    }

    [Fact]
    public async Task CompleteAsync_when_cancelling_reopen_does_not_modify_reminder()
    {
        var row = new ReminderRow("rem-past", "10:00", "Yesterday", "Past task", "", true, true);
        _vm.RequestReopenChoice = _ => Task.FromResult(ReopenChoice.Cancel);

        var apiCalled = false;
        _handler.PostHandler = _ =>
        {
            apiCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        await _vm.CompleteCommand.ExecuteAsync(row);

        apiCalled.ShouldBeFalse();
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
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
