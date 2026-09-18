using Microsoft.Extensions.Logging.Abstractions;
using PCConnect.Client;
using PCConnect.Companion.ViewModels;
using PCConnect.Core.Contracts;
using Shouldly;

namespace PCConnect.UnitTests;

public sealed class CalendarSelectionTests : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly RemindersViewModel _calendar;
    private readonly DateOnly _month = DateOnly.FromDateTime(DateTime.Today);

    public CalendarSelectionTests()
    {
        var api = new PcConnectClient(_http, new PcConnectClientOptions(), new InMemoryTokenStore());
        _calendar = new RemindersViewModel(api,
            new DevicesViewModel(api, NullLogger<DevicesViewModel>.Instance),
            NullLogger<RemindersViewModel>.Instance);
        _calendar.RebuildCalendar();
    }

    [Fact]
    public void Click_filters_events_and_replaces_previous_selection()
    {
        AddEvent("later", _month, 16);
        AddEvent("earlier", _month, 9);
        AddEvent("other day", _month.AddDays(1));

        Click(_month);
        _calendar.Rows.Select(row => row.Id).ShouldBe(["earlier", "later"]);
        Click(_month.AddDays(1));
        SelectedDays().ShouldBe([_month.AddDays(1)]);
        _calendar.Rows.Single().Id.ShouldBe("other day");
        Click(_month.AddDays(1));
        _calendar.HasSelection.ShouldBeTrue();
    }

    [Fact]
    public void Ctrl_click_adds_and_removes_individual_days()
    {
        AddEvent("first", _month);
        AddEvent("middle", _month.AddDays(1));
        AddEvent("last", _month.AddDays(2));
        Click(_month);
        Click(_month.AddDays(2), toggle: true);

        SelectedDays().ShouldBe([_month, _month.AddDays(2)]);
        _calendar.Rows.Select(row => row.Id).ShouldBe(["first", "last"]);
        Click(_month, toggle: true);
        SelectedDays().ShouldBe([_month.AddDays(2)]);
        Click(_month.AddDays(2), toggle: true);
        _calendar.HasSelection.ShouldBeFalse();
        _calendar.Rows.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(2, 5)]
    [InlineData(5, 2)]
    public void Drag_selects_an_inclusive_range_in_either_direction(int start, int end)
    {
        _calendar.BeginDaySelection(_month.AddDays(start), extend: false, toggle: false);
        _calendar.ExtendDaySelection(_month.AddDays(end));
        _calendar.EndDaySelection();

        SelectedDays().ShouldBe(Enumerable.Range(2, 4).Select(_month.AddDays));
        _calendar.ListTitle.ShouldBe("4 days selected");
    }

    [Fact]
    public void Reversing_a_drag_shrinks_the_range_and_release_stops_selection()
    {
        _calendar.BeginDaySelection(_month, extend: false, toggle: false);
        _calendar.ExtendDaySelection(_month.AddDays(8));
        _calendar.ExtendDaySelection(_month.AddDays(2));
        _calendar.EndDaySelection();
        _calendar.ExtendDaySelection(_month.AddDays(5));

        SelectedDays().ShouldBe(Enumerable.Range(0, 3).Select(_month.AddDays));
    }

    [Fact]
    public void Shift_click_extends_from_the_anchor_across_months()
    {
        var nextMonth = _month.AddMonths(1);
        var anchor = nextMonth.AddDays(-2);
        AddEvent("previous month", anchor);
        AddEvent("next month", nextMonth.AddDays(1));
        Click(anchor);
        _calendar.NextMonthCommand.Execute(null);
        Click(nextMonth.AddDays(1), extend: true);

        _calendar.ListTitle.ShouldBe("4 days selected");
        _calendar.Rows.Select(row => row.Id).ShouldBe(["previous month", "next month"]);
        Click(nextMonth, extend: true);
        _calendar.ListTitle.ShouldBe("3 days selected");
        _calendar.Rows.Single().Id.ShouldBe("previous month");
    }

    [Fact]
    public void Ctrl_drag_preserves_other_selected_days()
    {
        Click(_month);
        _calendar.BeginDaySelection(_month.AddDays(4), extend: false, toggle: true);
        _calendar.ExtendDaySelection(_month.AddDays(6));
        _calendar.EndDaySelection();

        SelectedDays().ShouldBe([_month, _month.AddDays(4), _month.AddDays(5), _month.AddDays(6)]);
    }

    [Fact]
    public void Show_all_clears_selection_and_anchor_and_includes_the_whole_month()
    {
        AddEvent("start", _month);
        AddEvent("end", _month.AddMonths(1).AddDays(-1));
        Click(_month.AddDays(3));
        _calendar.NoRows.ShouldBeTrue();
        _calendar.ClearSelectionCommand.Execute(null);

        SelectedDays().ShouldBeEmpty();
        _calendar.HasSelection.ShouldBeFalse();
        _calendar.ListTitle.ShouldBe("All events");
        _calendar.Rows.Select(row => row.Id).ShouldBe(["start", "end"]);
        Click(_month.AddDays(7), extend: true);
        SelectedDays().ShouldBe([_month.AddDays(7)]);
    }

    [Fact]
    public void Calendar_rebuild_preserves_selection_and_marks_recurring_events()
    {
        AddEvent("weekly", _month, rule: "FREQ=WEEKLY");
        Click(_month.AddDays(7));
        _calendar.RebuildCalendar();

        SelectedDays().ShouldBe([_month.AddDays(7)]);
        _calendar.Days.Single(day => day.Date == _month).HasEvents.ShouldBeTrue();
        _calendar.Days.Single(day => day.Date == _month.AddDays(7)).HasEvents.ShouldBeTrue();
        _calendar.Rows.Single().Id.ShouldBe("weekly");
        Click(_month.AddDays(1));
        _calendar.Days.Single(day => day.Date == _month.AddDays(1)).HasEvents.ShouldBeFalse();
        _calendar.NoRows.ShouldBeTrue();
    }

    [Fact]
    public void Show_all_includes_events_across_multiple_months_and_not_just_the_current_month()
    {
        var prevMonth = _month.AddMonths(-1);
        var nextMonth = _month.AddMonths(2);
        AddEvent("previous", prevMonth);
        AddEvent("current", _month.AddDays(5));
        AddEvent("future", nextMonth.AddDays(2));

        Click(_month.AddDays(5));
        _calendar.Rows.Single().Id.ShouldBe("current");

        _calendar.ClearSelectionCommand.Execute(null);

        _calendar.HasSelection.ShouldBeFalse();
        _calendar.ListTitle.ShouldBe("All events");
        // "previous" is in the past, so hidden by default in All events
        _calendar.Rows.Select(r => r.Id).ShouldBe(["current", "future"]);
        _calendar.HasCompletedReminders.ShouldBeTrue();
        _calendar.CompletedRemindersButtonText.ShouldBe("Show completed reminders");

        // When toggled, past/completed reminders are included
        _calendar.ToggleShowCompletedRemindersCommand.Execute(null);
        _calendar.Rows.Select(r => r.Id).ShouldBe(["previous", "current", "future"]);
        _calendar.CompletedRemindersButtonText.ShouldBe("Hide completed reminders");
    }

    [Fact]
    public void Show_all_shows_initial_20_and_lazy_loads_more_on_scroll()
    {
        for (var i = 0; i < 25; i++)
        {
            AddEvent($"ev-{i:D2}", _month.AddDays(i));
        }

        _calendar.ClearSelectionCommand.Execute(null);

        _calendar.Rows.Count.ShouldBe(20);
        _calendar.HasMoreRows.ShouldBeTrue();
        _calendar.ListSummary.ShouldBe("25 reminders");

        _calendar.LoadMoreRowsCommand.Execute(null);

        _calendar.Rows.Count.ShouldBe(25);
        _calendar.HasMoreRows.ShouldBeFalse();
        _calendar.ListSummary.ShouldBe("25 reminders");
    }

    [Fact]
    public void Set_time_updates_new_time()
    {
        _calendar.SetTimeCommand.Execute("15:30");
        _calendar.NewTime.ShouldBe("15:30");
    }

    [Fact]
    public void Multiple_consecutive_shift_clicks_adjust_the_range()
    {
        Click(_month.AddDays(5));
        SelectedDays().ShouldBe([_month.AddDays(5)]);

        Click(_month.AddDays(10), extend: true);
        SelectedDays().ShouldBe(Enumerable.Range(5, 6).Select(_month.AddDays));

        Click(_month.AddDays(8), extend: true);
        SelectedDays().ShouldBe(Enumerable.Range(5, 4).Select(_month.AddDays));

        Click(_month.AddDays(12), extend: true);
        SelectedDays().ShouldBe(Enumerable.Range(5, 8).Select(_month.AddDays));
    }

    [Fact]
    public void Shift_click_selection_and_control_click_selection_can_be_combined_and_extended()
    {
        // 1. Select initial range: 2..5
        Click(_month.AddDays(2));
        Click(_month.AddDays(5), extend: true);
        SelectedDays().ShouldBe(Enumerable.Range(2, 4).Select(_month.AddDays));

        // 2. Ctrl+click 10: anchor becomes 10, range base captures 2..5 and 10
        Click(_month.AddDays(10), toggle: true);
        SelectedDays().ShouldBe([
            _month.AddDays(2), _month.AddDays(3), _month.AddDays(4), _month.AddDays(5),
            _month.AddDays(10)
        ]);

        // 3. Shift+click 14: extends from 10 to 14 while preserving 2..5
        Click(_month.AddDays(14), extend: true);
        SelectedDays().ShouldBe([
            _month.AddDays(2), _month.AddDays(3), _month.AddDays(4), _month.AddDays(5),
            _month.AddDays(10), _month.AddDays(11), _month.AddDays(12), _month.AddDays(13), _month.AddDays(14)
        ]);

        // 4. Shift+click 12: adjusts the second range to 10..12 while preserving 2..5
        Click(_month.AddDays(12), extend: true);
        SelectedDays().ShouldBe([
            _month.AddDays(2), _month.AddDays(3), _month.AddDays(4), _month.AddDays(5),
            _month.AddDays(10), _month.AddDays(11), _month.AddDays(12)
        ]);

        // 5. Ctrl+click 4: toggles 4 off from selection
        Click(_month.AddDays(4), toggle: true);
        SelectedDays().ShouldBe([
            _month.AddDays(2), _month.AddDays(3), _month.AddDays(5),
            _month.AddDays(10), _month.AddDays(11), _month.AddDays(12)
        ]);
    }

    [Fact]
    public void Go_to_today_resets_month_and_updates_is_current_month()
    {
        _calendar.IsCurrentMonth.ShouldBeTrue();

        _calendar.NextMonthCommand.Execute(null);
        _calendar.IsCurrentMonth.ShouldBeFalse();

        _calendar.GoToTodayCommand.Execute(null);
        _calendar.IsCurrentMonth.ShouldBeTrue();
    }

    [Fact]
    public void Completed_reminders_are_excluded_from_all_events_by_default_and_shown_when_toggled()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        AddEvent("active-ev", today.AddDays(1));
        AddEvent("completed-ev", today.AddDays(2), isCompleted: true);

        _calendar.ClearSelectionCommand.Execute(null);

        // Active event is present, completed event is excluded from all events by default
        _calendar.Rows.Any(r => r.Id == "active-ev").ShouldBeTrue();
        _calendar.Rows.Any(r => r.Id == "completed-ev").ShouldBeFalse();
        _calendar.HasCompletedReminders.ShouldBeTrue();
        _calendar.CompletedRemindersButtonText.ShouldBe("Show completed reminders");

        // Toggle show completed reminders
        _calendar.ToggleShowCompletedRemindersCommand.Execute(null);
        _calendar.Rows.Any(r => r.Id == "completed-ev").ShouldBeTrue();
        _calendar.CompletedRemindersButtonText.ShouldBe("Hide completed reminders");
    }

    [Fact]
    public void Selected_day_shows_completed_reminders_without_completed_button_bar()
    {
        var day = DateOnly.FromDateTime(DateTime.Today).AddDays(1);
        AddEvent("active-ev", day);
        AddEvent("completed-ev", day, isCompleted: true);

        Click(day);

        _calendar.HasSelection.ShouldBeTrue();
        // Completed reminders on selected day ARE shown
        _calendar.Rows.Select(r => r.Id).ShouldBe(["active-ev", "completed-ev"]);
        _calendar.Rows.Single(r => r.Id == "completed-ev").IsCompleted.ShouldBeTrue();
        // Button bar at top of list is NOT shown
        _calendar.HasCompletedReminders.ShouldBeFalse();
    }

    [Fact]
    public void Selected_past_day_shows_past_reminders_without_completed_button_bar()
    {
        var pastDay = DateOnly.FromDateTime(DateTime.Today).AddDays(-3);
        AddEvent("past-ev", pastDay);

        Click(pastDay);

        _calendar.HasSelection.ShouldBeTrue();
        _calendar.Rows.Single().Id.ShouldBe("past-ev");
        _calendar.Rows.Single().IsPast.ShouldBeTrue();
        _calendar.HasCompletedReminders.ShouldBeFalse();
    }

    [Fact]
    public void Month_year_picker_allows_navigating_years_and_selecting_month()
    {
        _calendar.IsMonthYearPickerOpen.ShouldBeFalse();

        _calendar.ToggleMonthYearPickerCommand.Execute(null);
        _calendar.IsMonthYearPickerOpen.ShouldBeTrue();
        var initialYear = _calendar.PickerYear;

        _calendar.NextYearCommand.Execute(null);
        _calendar.PickerYear.ShouldBe(initialYear + 1);

        _calendar.SelectMonthCommand.Execute(5); // May
        _calendar.IsMonthYearPickerOpen.ShouldBeFalse();
        _calendar.MonthTitle.ShouldContain("May");
        _calendar.MonthTitle.ShouldContain((initialYear + 1).ToString());
    }

    [Fact]
    public void Year_selector_allows_selecting_year_and_decade_navigation()
    {
        _calendar.IsYearsView.ShouldBeFalse();

        // 1. Click header title once -> Months view
        _calendar.HeaderTitleClickCommand.Execute(null);
        _calendar.IsMonthsView.ShouldBeTrue();
        _calendar.CalendarHeaderTitle.ShouldBe(_calendar.PickerYear.ToString());

        // 2. Click header title again -> Years view
        _calendar.HeaderTitleClickCommand.Execute(null);
        _calendar.IsYearsView.ShouldBeTrue();
        _calendar.CalendarHeaderTitle.ShouldContain("–");
        var initialDecadeStart = _calendar.DecadeStart;

        // 3. Navigate decade forward
        _calendar.HeaderNextCommand.Execute(null);
        _calendar.DecadeStart.ShouldBe(initialDecadeStart + 12);

        // 4. Select a year -> returns to Months view
        var targetYear = initialDecadeStart + 15;
        _calendar.SelectYearCommand.Execute(targetYear);
        _calendar.IsMonthsView.ShouldBeTrue();
        _calendar.PickerYear.ShouldBe(targetYear);
        _calendar.CalendarHeaderTitle.ShouldBe(targetYear.ToString());

        // 5. Select a month -> returns to Days view
        _calendar.SelectMonthCommand.Execute(8); // August
        _calendar.IsDaysView.ShouldBeTrue();
        _calendar.MonthTitle.ShouldContain("August");
        _calendar.MonthTitle.ShouldContain(targetYear.ToString());
    }

    private void Click(DateOnly day, bool extend = false, bool toggle = false)
    {
        _calendar.BeginDaySelection(day, extend, toggle);
        _calendar.EndDaySelection();
    }

    private IEnumerable<DateOnly> SelectedDays() =>
        _calendar.Days.Where(day => day.IsSelected).Select(day => day.Date);

    private void AddEvent(string id, DateOnly day, int hour = 9, string? rule = null, bool isCompleted = false)
    {
        var due = new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Local));
        _calendar.Items.Add(new ReminderResponse(id, id, due, "", TimeZoneInfo.Local.Id,
            rule, null, isCompleted, null, due, due));
        _calendar.RebuildCalendar();
    }

    public void Dispose() => _http.Dispose();
}
