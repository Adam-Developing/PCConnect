using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PCConnect.Agent.Execution;
using PCConnect.Client;
using PCConnect.Companion.Services;
using PCConnect.Companion.ViewModels;
using PCConnect.Core.Contracts;
using Shouldly;

namespace PCConnect.UnitTests;

public sealed class ReminderCrudTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

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
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR", RepeatKind.Weekly, 2)]
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

    [Fact]
    public void Repeat_updates_interval_labels_in_place_without_resetting_interval()
    {
        _vm.IntervalWeeks = 5;
        _vm.Repeat = RepeatKind.Yearly;

        _vm.IntervalWeeks.ShouldBe(5);
        _vm.IntervalChoiceItems.First(o => o.Value == 5).Label.ShouldBe("Every 5 years");

        _vm.Repeat = RepeatKind.Monthly;

        _vm.IntervalWeeks.ShouldBe(5);
        _vm.IntervalChoiceItems.First(o => o.Value == 5).Label.ShouldBe("Every 5 months");
    }

    [Fact]
    public void EndMode_controls_until_and_occurrence_count_visibility_and_summary()
    {
        _vm.Repeat = RepeatKind.Daily;

        _vm.EndMode = RepeatEndMode.OnDate;
        _vm.Until.ShouldNotBeNull();
        _vm.IsEndOnDate.ShouldBeTrue();

        _vm.EndMode = RepeatEndMode.Never;
        _vm.Until.ShouldBeNull();
        _vm.IsEndOnDate.ShouldBeFalse();

        _vm.OccurrenceCount = 4;
        _vm.EndMode = RepeatEndMode.AfterOccurrences;
        _vm.Until.ShouldBeNull();
        _vm.IsEndAfterOccurrences.ShouldBeTrue();
        _vm.ScheduleSummary.ShouldContain("Ends after 4 occurrences.");
    }

    [Fact]
    public void StartEdit_with_count_rrule_populates_occurrence_end_mode()
    {
        var due = new DateTimeOffset(2026, 10, 15, 14, 30, 0, TimeSpan.Zero);
        var item = new ReminderResponse(
            Id: "rem-count",
            Body: "Passport check",
            DueAt: due,
            DueLocalTime: "14:30",
            Timezone: "UTC",
            Rrule: "FREQ=YEARLY;INTERVAL=5;COUNT=3",
            RecurrenceUntil: null,
            IsCompleted: false,
            CompletedAt: null,
            CreatedAt: due,
            UpdatedAt: due);

        _vm.Items.Add(item);
        var row = new ReminderRow("rem-count", "14:30", "", "Passport check", "Every 5 years", false, false, IsRecurring: true);

        _vm.StartEditCommand.Execute(row);

        _vm.Repeat.ShouldBe(RepeatKind.Yearly);
        _vm.IntervalWeeks.ShouldBe(5);
        _vm.EndMode.ShouldBe(RepeatEndMode.AfterOccurrences);
        _vm.OccurrenceCount.ShouldBe(3);
        _vm.Until.ShouldBeNull();

        _vm.CancelEditCommand.Execute(null);
        _vm.EndMode.ShouldBe(RepeatEndMode.Never);
    }

    [Fact]
    public void SelectWeekdaysCommand_selects_monday_through_friday()
    {
        _vm.Repeat = RepeatKind.Weekly;
        _vm.SelectWeekdaysCommand.Execute(null);

        _vm.RepeatDays.Where(d => d.IsOn).Select(d => d.Day).ShouldBe(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);
    }

    [Fact]
    public async Task AddCommand_updates_items_from_post_response_without_get_reload()
    {
        var getCalls = 0;
        _handler.GetHandler = _ =>
        {
            Interlocked.Increment(ref getCalls);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        };

        var now = DateTimeOffset.UtcNow;
        _handler.PostHandler = req =>
        {
            req.RequestUri!.AbsolutePath.ShouldBe("/v2/reminders");
            var created = new ReminderResponse(
                Id: "rem-fast-1",
                Body: "Call dentist",
                DueAt: now.AddHours(2),
                DueLocalTime: "15:00",
                Timezone: "UTC",
                Rrule: null,
                RecurrenceUntil: null,
                IsCompleted: false,
                CompletedAt: null,
                CreatedAt: now,
                UpdatedAt: now,
                DeviceIds: ["dev-1"]);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(JsonSerializer.Serialize(created, WebJson))
            };
        };

        var itemsChangedCount = 0;
        _vm.ItemsChanged += () => itemsChangedCount++;

        _vm.NewBody = "Call dentist";
        _vm.NewTime = "15:00";

        await _vm.AddCommand.ExecuteAsync(null);

        getCalls.ShouldBe(0);
        itemsChangedCount.ShouldBe(1);
        _vm.Items.Count.ShouldBe(1);
        _vm.Items[0].Id.ShouldBe("rem-fast-1");
        _vm.NewBody.ShouldBeEmpty();
        _vm.StatusMessage.ShouldBe("Reminder added.");
        _vm.IsStatusError.ShouldBeFalse();

        // When the matching realtime event arrives afterwards with a separate DeviceIds list instance,
        // ApplyReminderChangedAsync is a no-op: zero GETs and zero redundant rebuilds.
        var duplicateEvent = new ReminderChangedEvent(
            "created",
            new ReminderResponse(
                Id: "rem-fast-1",
                Body: "Call dentist",
                DueAt: now.AddHours(2),
                DueLocalTime: "15:00",
                Timezone: "UTC",
                Rrule: null,
                RecurrenceUntil: null,
                IsCompleted: false,
                CompletedAt: null,
                CreatedAt: now,
                UpdatedAt: now,
                DeviceIds: new List<string> { "dev-1" }),
            "rem-fast-1");

        await _vm.ApplyReminderChangedAsync(duplicateEvent);

        getCalls.ShouldBe(0);
        itemsChangedCount.ShouldBe(1);
        _vm.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ApplyReminderChangedAsync_applies_remote_changes_without_get_reload()
    {
        var getCalls = 0;
        _handler.GetHandler = _ =>
        {
            Interlocked.Increment(ref getCalls);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        };

        var now = DateTimeOffset.UtcNow;
        var remoteCreated = new ReminderResponse(
            "rem-remote", "Stretch", now.AddHours(1), "12:00", "UTC", null, null, false, null, now, now);

        await _vm.ApplyReminderChangedAsync(new ReminderChangedEvent("created", remoteCreated, "rem-remote"));
        _vm.Items.Count.ShouldBe(1);
        _vm.Items[0].Body.ShouldBe("Stretch");

        var remoteUpdated = remoteCreated with { Body = "Stretch & walk", UpdatedAt = now.AddMinutes(1) };
        await _vm.ApplyReminderChangedAsync(new ReminderChangedEvent("updated", remoteUpdated, "rem-remote"));
        _vm.Items.Count.ShouldBe(1);
        _vm.Items[0].Body.ShouldBe("Stretch & walk");

        await _vm.ApplyReminderChangedAsync(new ReminderChangedEvent("deleted", null, "rem-remote"));
        _vm.Items.ShouldBeEmpty();
        getCalls.ShouldBe(0);
    }

    [Fact]
    public async Task LoadAsync_coalesces_concurrent_requests()
    {
        var getCalls = 0;
        var release = new TaskCompletionSource();

        _handler.AsyncGetHandler = async _ =>
        {
            Interlocked.Increment(ref getCalls);
            await release.Task;
            var page = new Page<ReminderResponse>([], null);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(page, WebJson))
            };
        };

        var first = _vm.LoadAsync();
        var second = _vm.LoadAsync();
        release.SetResult();

        await Task.WhenAll(first, second);
        getCalls.ShouldBe(1);
    }

    [Fact]
    public async Task CompleteAsync_by_id_updates_in_place_without_get_reload()
    {
        var getCalls = 0;
        _handler.GetHandler = _ =>
        {
            getCalls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        };

        var due = DateTimeOffset.UtcNow.AddHours(3);
        var original = new ReminderResponse("rem-id-only", "Call dentist", due, "15:00", "UTC", null, null, false, null, due, due);
        _vm.Items.Add(original);
        // Leave _vm.Rows empty (e.g., another calendar date is selected) and complete by string ID.
        _vm.Rows.ShouldBeEmpty();

        _handler.PostHandler = req =>
        {
            req.RequestUri!.AbsolutePath.ShouldBe("/v2/reminders/rem-id-only/complete");
            var completed = original with { IsCompleted = true, CompletedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(completed, WebJson))
            };
        };

        await _vm.CompleteCommand.ExecuteAsync("rem-id-only");

        getCalls.ShouldBe(0);
        _vm.Items.Single().IsCompleted.ShouldBeTrue();
        _vm.StatusMessage.ShouldBe("Reminder completed.");
    }

    [Fact]
    public async Task DeleteAsync_enables_undo_and_UndoAsync_restores_deleted_reminder()
    {
        var due = DateTimeOffset.UtcNow.AddHours(2);
        var original = new ReminderResponse(
            Id: "rem-undo-del",
            Body: "Submit expense report",
            DueAt: due,
            DueLocalTime: "16:00",
            Timezone: "Europe/London",
            Rrule: "FREQ=WEEKLY;BYDAY=FR",
            RecurrenceUntil: null,
            IsCompleted: false,
            CompletedAt: null,
            CreatedAt: due.AddDays(-1),
            UpdatedAt: due.AddDays(-1),
            DeviceIds: ["dev-1"]);
        _vm.Items.Add(original);

        _handler.DeleteHandler = req =>
        {
            req.RequestUri!.AbsolutePath.ShouldBe("/v2/reminders/rem-undo-del");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };

        var restoreCalled = false;
        _handler.PostHandler = req =>
        {
            req.RequestUri!.AbsolutePath.ShouldBe("/v2/reminders/rem-undo-del/restore");
            restoreCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(original, WebJson))
            };
        };

        await _vm.DeleteCommand.ExecuteAsync("rem-undo-del");

        _vm.Items.ShouldBeEmpty();
        _vm.StatusMessage.ShouldBe("Reminder deleted.");
        _vm.CanUndo.ShouldBeTrue();

        await _vm.UndoCommand.ExecuteAsync(null);

        restoreCalled.ShouldBeTrue();
        _vm.CanUndo.ShouldBeFalse();
        _vm.StatusMessage.ShouldBe("Reminder restored.");
        _vm.Items.Single().Id.ShouldBe("rem-undo-del");
        _vm.Items.Single().Body.ShouldBe("Submit expense report");
        _vm.Items.Single().DeviceIds.ShouldBe(["dev-1"]);
    }

    [Fact]
    public async Task DeleteAsync_recurring_reminder_removes_future_occurrences_and_keeps_past()
    {
        var due = DateTimeOffset.UtcNow.AddDays(-2);
        var original = new ReminderResponse(
            Id: "rem-rec-del",
            Body: "Daily standup",
            DueAt: due,
            DueLocalTime: "09:00",
            Timezone: "Europe/London",
            Rrule: "FREQ=DAILY",
            RecurrenceUntil: null,
            IsCompleted: false,
            CompletedAt: null,
            CreatedAt: due.AddDays(-1),
            UpdatedAt: due.AddDays(-1),
            DeviceIds: ["dev-1"]);
        _vm.Items.Add(original);

        var deleteCalled = false;
        _handler.DeleteHandler = req =>
        {
            req.RequestUri!.AbsolutePath.ShouldBe("/v2/reminders/rem-rec-del");
            deleteCalled = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };

        await _vm.DeleteCommand.ExecuteAsync("rem-rec-del");

        deleteCalled.ShouldBeTrue();
        _vm.StatusMessage.ShouldBe("Future occurrences removed.");
        _vm.CanUndo.ShouldBeTrue();
        _vm.Items.Count.ShouldBe(1);
        _vm.Items.Single().Id.ShouldBe("rem-rec-del");
        _vm.Items.Single().IsCompleted.ShouldBeTrue();
        _vm.Items.Single().RecurrenceUntil.ShouldNotBeNull();
    }

    [Fact]
    public async Task Edit_via_AddAsync_enables_undo_and_UndoAsync_reverts_all_fields_including_targets()
    {
        var due = DateTimeOffset.UtcNow.AddHours(4);
        var original = new ReminderResponse(
            Id: "rem-undo-edit",
            Body: "Original reminder text",
            DueAt: due,
            DueLocalTime: "14:00",
            Timezone: "Europe/London",
            Rrule: null,
            RecurrenceUntil: null,
            IsCompleted: false,
            CompletedAt: null,
            CreatedAt: due.AddDays(-2),
            UpdatedAt: due.AddDays(-2),
            DeviceIds: null);
        _vm.Items.Add(original);

        _vm.IsTargetable = true;
        _vm.Targets.Add(new TargetToggle { DeviceId = "dev-work", Name = "WORK-PC", IsOnline = true });

        var row = new ReminderRow("rem-undo-edit", "14:00", "Today", "Original reminder text", "All PCs", false, false);
        _vm.StartEditCommand.Execute(row);

        _vm.NewBody = "Edited reminder text";
        _vm.Repeat = RepeatKind.Daily;
        _vm.ShowOnAllPcs = false;
        _vm.Targets[0].IsChosen = true;

        UpdateReminderRequest? capturedEditRequest = null;
        UpdateReminderRequest? capturedRevertRequest = null;
        var patchCount = 0;

        _handler.PatchHandler = req =>
        {
            patchCount++;
            req.RequestUri!.AbsolutePath.ShouldBe("/v2/reminders/rem-undo-edit");
            var json = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var body = JsonSerializer.Deserialize<UpdateReminderRequest>(json, WebJson)!;

            if (patchCount == 1)
            {
                capturedEditRequest = body;
                var edited = original with
                {
                    Body = body.Body!,
                    DueAt = body.DueAt!.Value,
                    Rrule = body.Rrule,
                    DeviceIds = body.DeviceIds,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(edited, WebJson))
                };
            }

            capturedRevertRequest = body;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(original, WebJson))
            };
        };

        await _vm.AddCommand.ExecuteAsync(null);

        patchCount.ShouldBe(1);
        capturedEditRequest.ShouldNotBeNull();
        capturedEditRequest!.Body.ShouldBe("Edited reminder text");
        capturedEditRequest.DeviceIds.ShouldBe(["dev-work"]);
        _vm.Items.Single().Body.ShouldBe("Edited reminder text");
        _vm.StatusMessage.ShouldBe("Reminder updated.");
        _vm.CanUndo.ShouldBeTrue();

        await _vm.UndoCommand.ExecuteAsync(null);

        patchCount.ShouldBe(2);
        capturedRevertRequest.ShouldNotBeNull();
        capturedRevertRequest!.Body.ShouldBe("Original reminder text");
        capturedRevertRequest.DueAt.ShouldBe(original.DueAt);
        capturedRevertRequest.Rrule.ShouldBe(string.Empty);
        capturedRevertRequest.DeviceIds.ShouldBeNull();
        capturedRevertRequest.ClearDeviceIds.ShouldBe(true);

        _vm.CanUndo.ShouldBeFalse();
        _vm.StatusMessage.ShouldBe("Changes undone.");
        _vm.Items.Single().Body.ShouldBe("Original reminder text");
        _vm.Items.Single().DeviceIds.ShouldBeNull();
    }

    [Fact]
    public async Task DismissStatus_clears_CanUndo_and_prevents_UndoAsync_from_calling_server()
    {
        var due = DateTimeOffset.UtcNow.AddHours(1);
        _vm.Items.Add(new ReminderResponse("rem-dismiss-undo", "Temporary", due, "12:00", "UTC", null, null, false, null, due, due));

        _handler.DeleteHandler = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        var postCalled = false;
        _handler.PostHandler = _ =>
        {
            postCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        await _vm.DeleteCommand.ExecuteAsync("rem-dismiss-undo");
        _vm.CanUndo.ShouldBeTrue();

        _vm.DismissStatus();
        _vm.CanUndo.ShouldBeFalse();

        await _vm.UndoCommand.ExecuteAsync(null);
        postCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task ShellViewModel_occurrence_edit_and_stop_enable_undo_and_floating_toast()
    {
        var tempSettingsPath = Path.Combine(Path.GetTempPath(), $"pcconnect-test-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new CompanionSettings(NullLogger<CompanionSettings>.Instance, tempSettingsPath);
            var devices = new DevicesViewModel(_api, NullLogger<DevicesViewModel>.Instance);
            var shell = new ShellViewModel(
                _api,
                new PcConnectRealtimeClient(_api, NullLogger<PcConnectRealtimeClient>.Instance),
                settings,
                new ProvisioningPipeClient(NullLogger<ProvisioningPipeClient>.Instance),
                devices,
                _vm,
                new AccountViewModel(_api, NullLogger<AccountViewModel>.Instance),
                new SettingsViewModel(_api, settings, new StartupRegistration(NullLogger<StartupRegistration>.Instance), devices, NullLogger<SettingsViewModel>.Instance),
                NullLogger<ShellViewModel>.Instance)
            {
                IsSignedIn = true,
                Page = CompanionPage.Reminders,
                IsEventDetailOpen = true,
            };

            var occursAt = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
            var occ = new OccurrenceItemViewModel
            {
                ReminderId = "rem-series-1",
                OccursAt = occursAt,
                SeriesBody = "Weekly team sync",
                Body = "Weekly team sync",
                CustomBody = null,
                Status = "pending",
            };

            UpdateReminderOccurrenceRequest? lastOccurrencePost = null;
            _handler.PostHandler = req =>
            {
                req.RequestUri!.AbsolutePath.ShouldBe("/v2/reminders/rem-series-1/occurrences");
                var json = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                lastOccurrencePost = JsonSerializer.Deserialize<UpdateReminderOccurrenceRequest>(json, WebJson)!;

                var effectiveCustom = lastOccurrencePost.ClearCustomBody == true
                    ? null
                    : (lastOccurrencePost.CustomBody ?? occ.CustomBody);
                var effectiveBody = effectiveCustom ?? "Weekly team sync";
                var effectiveStatus = lastOccurrencePost.Status ?? occ.Status;
                var resp = new ReminderOccurrenceResponse(1, "rem-series-1", occursAt, effectiveStatus, null, effectiveCustom, effectiveBody);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resp, WebJson))
                };
            };

            _handler.DeleteHandler = req =>
            {
                req.RequestUri!.AbsolutePath.ShouldBe("/v2/reminders/rem-series-1/occurrences");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            };

            // 1. Edit occurrence text -> undo clears customBody back to series text.
            shell.StartEditOccurrence(occ);
            occ.EditText = "Weekly sync (Q4 planning)";
            await shell.SaveEditOccurrenceCommand.ExecuteAsync(occ);

            occ.Body.ShouldBe("Weekly sync (Q4 planning)");
            occ.HasCustomText.ShouldBeTrue();
            _vm.CanUndo.ShouldBeTrue();
            shell.ShowFloatingReminderToast.ShouldBeTrue();

            await _vm.UndoCommand.ExecuteAsync(null);
            lastOccurrencePost.ShouldNotBeNull();
            lastOccurrencePost!.ClearCustomBody.ShouldBe(true);
            occ.CustomBody.ShouldBeNull();
            occ.Body.ShouldBe("Weekly team sync");
            _vm.CanUndo.ShouldBeFalse();

            // 2. Stop occurrence -> undo restores occurrence status to pending.
            await shell.StopOccurrenceCommand.ExecuteAsync(occ);
            occ.Status.ShouldBe("skipped");
            _vm.StatusMessage.ShouldBe("Occurrence stopped.");
            _vm.CanUndo.ShouldBeTrue();
            shell.ShowFloatingReminderToast.ShouldBeTrue();

            await _vm.UndoCommand.ExecuteAsync(null);
            lastOccurrencePost!.Status.ShouldBe("pending");
            occ.Status.ShouldBe("pending");
            _vm.StatusMessage.ShouldBe("Occurrence restored.");
            _vm.CanUndo.ShouldBeFalse();
        }
        finally
        {
            if (File.Exists(tempSettingsPath))
            {
                File.Delete(tempSettingsPath);
            }
        }
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
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? AsyncGetHandler { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? PostHandler { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? PatchHandler { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete && DeleteHandler != null)
            {
                return Task.FromResult(DeleteHandler(request));
            }
            if (request.Method == HttpMethod.Get && AsyncGetHandler != null)
            {
                return AsyncGetHandler(request);
            }
            if (request.Method == HttpMethod.Get && GetHandler != null)
            {
                return Task.FromResult(GetHandler(request));
            }
            if (request.Method == HttpMethod.Post && PostHandler != null)
            {
                return Task.FromResult(PostHandler(request));
            }
            if (request.Method == HttpMethod.Patch && PatchHandler != null)
            {
                return Task.FromResult(PatchHandler(request));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
