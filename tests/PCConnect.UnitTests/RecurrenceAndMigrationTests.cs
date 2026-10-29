using Microsoft.Extensions.Logging.Abstractions;
using PCConnect.Companion.ViewModels;
using PCConnect.Infrastructure.Contexts.Commands;
using PCConnect.Infrastructure.Recurrence;
using PCConnect.LegacyMigrator;
using Shouldly;

namespace PCConnect.UnitTests;

public class RecurrenceExpanderTests
{
    private static RecurrenceExpander Create() => new(NullLogger<RecurrenceExpander>.Instance);

    [Fact]
    public void Expands_a_daily_rule()
    {
        var start = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

        var occurrences = Create().Expand("FREQ=DAILY", start, "Etc/UTC", start, start.AddDays(4), null);

        occurrences.Count.ShouldBe(5);
        occurrences[0].ShouldBe(start);
        occurrences[4].ShouldBe(start.AddDays(4));
    }

    [Fact]
    public void Expands_a_weekly_rule_on_named_days()
    {
        var start = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero); // a Monday

        var occurrences = Create().Expand("FREQ=WEEKLY;BYDAY=MO,WE", start, "Etc/UTC", start, start.AddDays(14), null);

        occurrences.ShouldAllBe(o => o.DayOfWeek == DayOfWeek.Monday || o.DayOfWeek == DayOfWeek.Wednesday);
        occurrences.Count.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void Keeps_a_daily_reminder_at_the_same_local_time_across_a_dst_change()
    {
        // 09:00 in London on 27 March 2026; the clocks go forward on 29 March.
        // A UTC-anchored expansion would slide this to 08:00 local; the point of
        // evaluating in the reminder's own zone is that it does not (S2-07).
        var start = new DateTimeOffset(2026, 3, 27, 9, 0, 0, TimeSpan.Zero);
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

        var occurrences = Create().Expand("FREQ=DAILY", start, "Europe/London", start, start.AddDays(6), null);

        occurrences.Count.ShouldBeGreaterThanOrEqualTo(6);
        foreach (var occurrence in occurrences)
        {
            TimeZoneInfo.ConvertTime(occurrence, london).Hour.ShouldBe(9);
        }
    }

    [Fact]
    public void Stops_at_the_recurrence_end_date()
    {
        var start = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

        var occurrences = Create().Expand("FREQ=DAILY", start, "Etc/UTC", start, start.AddDays(30), start.AddDays(3));

        occurrences.Count.ShouldBe(4);
        occurrences[^1].ShouldBe(start.AddDays(3));
    }

    [Fact]
    public void Stops_after_rrule_count_occurrences()
    {
        var start = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

        Create().TryValidate("FREQ=DAILY;COUNT=4", start, out _).ShouldBeTrue();
        var occurrences = Create().Expand("FREQ=DAILY;COUNT=4", start, "Etc/UTC", start, start.AddDays(30), null);

        occurrences.Count.ShouldBe(4);
        occurrences[^1].ShouldBe(start.AddDays(3));
    }

    [Fact]
    public void Returns_nothing_rather_than_throwing_on_a_rule_it_cannot_parse()
    {
        var start = DateTimeOffset.UtcNow;

        // An unparseable rule on one user's reminder must not stop the scheduler
        // for everybody else.
        Create().Expand("FREQ=NEVER;NONSENSE", start, "Etc/UTC", start, start.AddDays(7), null).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("FREQ=DAILY", true)]
    [InlineData("FREQ=WEEKLY;BYDAY=MO", true)]
    [InlineData("RRULE:FREQ=DAILY", true)]
    [InlineData("FREQ=NONSENSE", false)]
    [InlineData("not a rule at all", false)]
    public void Validates_a_rule_at_write_time(string rrule, bool valid) =>
        Create().TryValidate(rrule, DateTimeOffset.UtcNow, out _).ShouldBe(valid);

    [Fact]
    public void Refuses_a_rule_longer_than_the_column()
    {
        Create().TryValidate(new string('X', 300), DateTimeOffset.UtcNow, out var error).ShouldBeFalse();
        error.ShouldContain("255");
    }
}

public class LegacyRecurrenceTranslationTests
{
    [Theory]
    [InlineData("Daily", null, null, "FREQ=DAILY")]
    [InlineData("Yes", "daily", null, "FREQ=DAILY")]
    [InlineData("Yes", "weekly", "Monday", "FREQ=WEEKLY;BYDAY=MO")]
    [InlineData("Yes", "weekly", "fri", "FREQ=WEEKLY;BYDAY=FR")]
    [InlineData("Yes", "weekly", null, "FREQ=WEEKLY")]
    [InlineData("Yes", "monthly", null, "FREQ=MONTHLY")]
    [InlineData("Yes", "yearly", null, "FREQ=YEARLY")]
    [InlineData("Yes", "weekdays", null, "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR")]
    public void Translates_the_five_v1_columns_into_one_rrule(
        string? recurrence, string? frequency, string? day, string expected) =>
        LegacyImporter.TranslateRecurrence(recurrence, frequency, day).ShouldBe(expected);

    [Theory]
    [InlineData("none", null, null)]
    [InlineData("None", null, null)]
    [InlineData("0", null, null)]
    [InlineData("", null, null)]
    [InlineData(null, null, null)]
    public void Treats_the_many_spellings_of_no_recurrence_as_none(
        string? recurrence, string? frequency, string? day) =>
        LegacyImporter.TranslateRecurrence(recurrence, frequency, day).ShouldBeNull();

    [Fact]
    public void Returns_null_for_something_it_cannot_translate() =>
        // The caller records this as a migration exception rather than guessing.
        LegacyImporter.TranslateRecurrence("Yes", "fortnightly-ish", null).ShouldBeNull();
}

public class CursorTests
{
    [Fact]
    public void Round_trips_an_id()
    {
        var cursor = Cursor.Encode(4096);
        Cursor.Decode(cursor).ShouldBe(4096);
    }

    [Fact]
    public void Is_opaque_so_the_shape_can_change()
    {
        Cursor.Encode(1).ShouldNotContain("1:");
        Cursor.Encode(1).ShouldNotContain("=");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Treats_an_absent_cursor_as_the_first_page(string? cursor) =>
        Cursor.Decode(cursor).ShouldBeNull();

    [Theory]
    [InlineData("!!!not-base64!!!")]
    [InlineData("bm90LWFuLWlk")]
    public void Rejects_a_cursor_it_did_not_issue(string cursor)
    {
        var error = Should.Throw<PCConnect.Core.AppException>(() => Cursor.Decode(cursor));
        error.Code.ShouldBe(PCConnect.Core.ErrorCodes.ValidationFailed);
    }
}

public class CompanionRecurrenceTests
{
    [Fact]
    public void Generates_rrule_for_each_repeat_kind()
    {
        var start = new DateOnly(2026, 9, 26); // Saturday

        Recurrence.ToRrule(RepeatKind.Once, [], 1, start).ShouldBeNull();
        Recurrence.ToRrule(RepeatKind.Hourly, [], 1, start).ShouldBe("FREQ=HOURLY");
        Recurrence.ToRrule(RepeatKind.Hourly, [], 3, start).ShouldBe("FREQ=HOURLY;INTERVAL=3");
        Recurrence.ToRrule(RepeatKind.Daily, [], 1, start).ShouldBe("FREQ=DAILY");
        Recurrence.ToRrule(RepeatKind.Daily, [], 2, start).ShouldBe("FREQ=DAILY;INTERVAL=2");
        Recurrence.ToRrule(RepeatKind.Weekly, [DayOfWeek.Monday, DayOfWeek.Wednesday], 1, start)
            .ShouldBe("FREQ=WEEKLY;BYDAY=MO,WE");
        Recurrence.ToRrule(RepeatKind.Weekly, [DayOfWeek.Friday], 2, start)
            .ShouldBe("FREQ=WEEKLY;INTERVAL=2;BYDAY=FR");
        Recurrence.ToRrule(RepeatKind.Monthly, [], 1, start).ShouldBe("FREQ=MONTHLY");
        Recurrence.ToRrule(RepeatKind.Monthly, [], 6, start).ShouldBe("FREQ=MONTHLY;INTERVAL=6");
        Recurrence.ToRrule(RepeatKind.Yearly, [], 1, start).ShouldBe("FREQ=YEARLY");
        Recurrence.ToRrule(RepeatKind.Yearly, [], 2, start).ShouldBe("FREQ=YEARLY;INTERVAL=2");
    }

    [Fact]
    public void Parses_rrule_for_all_frequencies_and_intervals()
    {
        var (hKind, _, hInt) = Recurrence.FromRrule("FREQ=HOURLY;INTERVAL=4");
        hKind.ShouldBe(RepeatKind.Hourly);
        hInt.ShouldBe(4);

        var (dKind, _, dInt) = Recurrence.FromRrule("FREQ=DAILY;INTERVAL=2");
        dKind.ShouldBe(RepeatKind.Daily);
        dInt.ShouldBe(2);

        var (wKind, wDays, wInt) = Recurrence.FromRrule("FREQ=WEEKLY;INTERVAL=3;BYDAY=TU,TH");
        wKind.ShouldBe(RepeatKind.Weekly);
        wInt.ShouldBe(3);
        wDays.ShouldBe([DayOfWeek.Tuesday, DayOfWeek.Thursday]);

        var (mKind, _, mInt) = Recurrence.FromRrule("FREQ=MONTHLY;INTERVAL=2");
        mKind.ShouldBe(RepeatKind.Monthly);
        mInt.ShouldBe(2);

        var (yKind, _, yInt) = Recurrence.FromRrule("FREQ=YEARLY");
        yKind.ShouldBe(RepeatKind.Yearly);
        yInt.ShouldBe(1);
    }

    [Fact]
    public void Describes_frequencies_in_natural_english()
    {
        Recurrence.Describe("FREQ=HOURLY").ShouldBe("Every hour");
        Recurrence.Describe("FREQ=HOURLY;INTERVAL=2").ShouldBe("Every 2 hours");
        Recurrence.Describe("FREQ=DAILY").ShouldBe("Every day");
        Recurrence.Describe("FREQ=DAILY;INTERVAL=3").ShouldBe("Every 3 days");
        Recurrence.Describe("FREQ=WEEKLY;BYDAY=MO,WE").ShouldBe("Every Mon and Wed");
        Recurrence.Describe("FREQ=WEEKLY;INTERVAL=2;BYDAY=FR").ShouldBe("Every 2 weeks on Fri");
        Recurrence.Describe("FREQ=MONTHLY").ShouldBe("Every month");
        Recurrence.Describe("FREQ=MONTHLY;INTERVAL=2").ShouldBe("Every 2 months");
        Recurrence.Describe("FREQ=YEARLY").ShouldBe("Every year");
    }

    [Fact]
    public void Describes_recurrence_pattern_with_date_and_time_naturally()
    {
        static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
            new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

        var thursday2pm = Local(2026, 10, 8, 14, 0);
        Recurrence.Describe("FREQ=WEEKLY;BYDAY=TH", thursday2pm, use24Hour: false).ShouldBe("Every Thursday at 2 pm");
        Recurrence.Describe("FREQ=WEEKLY;BYDAY=TH", thursday2pm, use24Hour: true).ShouldBe("Every Thursday at 14:00");
        Recurrence.Describe("FREQ=WEEKLY", thursday2pm, use24Hour: false).ShouldBe("Every Thursday at 2 pm");

        var oct83pm = Local(2026, 10, 8, 15, 0);
        Recurrence.Describe("FREQ=YEARLY", oct83pm, use24Hour: false).ShouldBe("Every 8 October at 3 pm");
        Recurrence.Describe("FREQ=YEARLY", oct83pm, use24Hour: true).ShouldBe("Every 8 October at 15:00");

        var sep27midnight10 = Local(2026, 9, 27, 0, 10);
        Recurrence.Describe("FREQ=YEARLY", sep27midnight10, use24Hour: false).ShouldBe("Every 27 September at 12:10 am");
        Recurrence.Describe("FREQ=YEARLY", sep27midnight10, use24Hour: true).ShouldBe("Every 27 September at 00:10");

        Recurrence.Describe("FREQ=MONTHLY", oct83pm, use24Hour: false).ShouldBe("Every month on the 8th at 3 pm");
        Recurrence.Describe("FREQ=DAILY", Local(2026, 10, 8, 8, 0), use24Hour: false).ShouldBe("Every day at 8 am");
        Recurrence.Describe("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", Local(2026, 10, 8, 9, 0), use24Hour: false).ShouldBe("Every weekday at 9 am");
        Recurrence.Describe("FREQ=WEEKLY;BYDAY=MO,WE", Local(2026, 10, 8, 10, 30), use24Hour: false).ShouldBe("Every Monday and Wednesday at 10:30 am");
        Recurrence.Describe("FREQ=HOURLY", oct83pm, use24Hour: false).ShouldBe("Every hour");
        Recurrence.Describe("FREQ=HOURLY;INTERVAL=2", oct83pm, use24Hour: false).ShouldBe("Every 2 hours");
    }

    [Fact]
    public void Summarises_schedule_with_times_and_dates()
    {
        var start = new DateOnly(2026, 9, 26);
        var today = new DateOnly(2026, 9, 26);
        var times = new List<TimeOnly> { new(9, 30), new(14, 0) };

        var summary = Recurrence.Summarise(RepeatKind.Hourly, [], 2, start, times, null, today);
        summary.ShouldContain("every 2 hours");
        summary.ShouldContain("09:30 and 14:00");

        var dailySummary = Recurrence.Summarise(RepeatKind.Daily, [], 1, start, [new(10, 0)], new DateOnly(2026, 12, 31), today);
        dailySummary.ShouldContain("Every day");
        dailySummary.ShouldContain("31 December 2026");
        dailySummary.ShouldContain("10:00");

        var summary12Hour = Recurrence.Summarise(RepeatKind.Hourly, [], 2, start, times, null, today, use24Hour: false);
        summary12Hour.ShouldContain("every 2 hours");
        summary12Hour.ShouldContain("9:30 AM and 2:00 PM");

        var once12Hour = Recurrence.Summarise(RepeatKind.Once, [], 1, start, [new(17, 0)], null, today, use24Hour: false);
        once12Hour.ShouldContain("Once — today");
        once12Hour.ShouldContain("5:00 PM");
    }

    [Fact]
    public void Determines_occurson_accurately()
    {
        var start = new DateOnly(2026, 9, 26); // Saturday
        Recurrence.OccursOn("FREQ=DAILY", start, new DateOnly(2026, 9, 27)).ShouldBeTrue();
        Recurrence.OccursOn("FREQ=DAILY", start, new DateOnly(2026, 9, 25)).ShouldBeFalse();

        Recurrence.OccursOn("FREQ=WEEKLY;BYDAY=MO", start, new DateOnly(2026, 9, 28)).ShouldBeTrue(); // Monday
        Recurrence.OccursOn("FREQ=WEEKLY;BYDAY=MO", start, new DateOnly(2026, 9, 29)).ShouldBeFalse(); // Tuesday

        Recurrence.OccursOn("FREQ=MONTHLY", start, new DateOnly(2026, 10, 26)).ShouldBeTrue();
        Recurrence.OccursOn("FREQ=MONTHLY", start, new DateOnly(2026, 10, 27)).ShouldBeFalse();
    }

    [Fact]
    public void Supports_occurrence_count_in_rrule_generation_parsing_summary_and_occurson()
    {
        var start = new DateOnly(2026, 10, 28); // Wednesday
        var today = start;

        var yearlyRule = Recurrence.ToRrule(RepeatKind.Yearly, [], 5, start, count: 3);
        yearlyRule.ShouldBe("FREQ=YEARLY;INTERVAL=5;COUNT=3");
        Recurrence.ParseCount(yearlyRule).ShouldBe(3);
        Recurrence.ParseCount("FREQ=DAILY").ShouldBeNull();
        Recurrence.ParseCount(null).ShouldBeNull();

        Recurrence.Describe(yearlyRule).ShouldBe("Every 5 years (3 times)");
        Recurrence.Summarise(RepeatKind.Yearly, [], 5, start, [new(15, 0)], null, today, use24Hour: false, count: 3)
            .ShouldContain("Ends after 3 occurrences.");
        Recurrence.Summarise(RepeatKind.Daily, [], 1, start, [new(9, 0)], null, today, use24Hour: true, count: 1)
            .ShouldContain("Ends after 1 occurrence.");

        Recurrence.OccursOn(yearlyRule, start, new DateOnly(2026, 10, 28)).ShouldBeTrue();
        Recurrence.OccursOn(yearlyRule, start, new DateOnly(2031, 10, 28)).ShouldBeTrue();
        Recurrence.OccursOn(yearlyRule, start, new DateOnly(2036, 10, 28)).ShouldBeTrue();
        Recurrence.OccursOn(yearlyRule, start, new DateOnly(2041, 10, 28)).ShouldBeFalse();

        var dailyRule = Recurrence.ToRrule(RepeatKind.Daily, [], 2, start, count: 2);
        dailyRule.ShouldBe("FREQ=DAILY;INTERVAL=2;COUNT=2");
        Recurrence.OccursOn(dailyRule, start, new DateOnly(2026, 10, 28)).ShouldBeTrue();
        Recurrence.OccursOn(dailyRule, start, new DateOnly(2026, 10, 30)).ShouldBeTrue();
        Recurrence.OccursOn(dailyRule, start, new DateOnly(2026, 11, 1)).ShouldBeFalse();

        // Mid-week start (Wednesday Oct 28) on MO,WE,FR with COUNT=4 -> Wed Oct 28, Fri Oct 30, Mon Nov 2, Wed Nov 4
        var weeklyRule = Recurrence.ToRrule(
            RepeatKind.Weekly,
            [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
            1,
            start,
            count: 4);
        Recurrence.OccursOn(weeklyRule, start, new DateOnly(2026, 10, 28)).ShouldBeTrue();
        Recurrence.OccursOn(weeklyRule, start, new DateOnly(2026, 10, 30)).ShouldBeTrue();
        Recurrence.OccursOn(weeklyRule, start, new DateOnly(2026, 11, 2)).ShouldBeTrue();
        Recurrence.OccursOn(weeklyRule, start, new DateOnly(2026, 11, 4)).ShouldBeTrue();
        Recurrence.OccursOn(weeklyRule, start, new DateOnly(2026, 11, 6)).ShouldBeFalse();
    }
}

public class OccurrenceViewModelTests
{
    [Fact]
    public void OccurrenceItemViewModel_status_and_custom_body_flags()
    {
        var item = new OccurrenceItemViewModel
        {
            ReminderId = "rem_1",
            OccursAt = DateTimeOffset.UtcNow,
            Body = "Take medication",
            Status = "pending",
        };

        item.IsPending.ShouldBeTrue();
        item.IsCompleted.ShouldBeFalse();
        item.IsSkipped.ShouldBeFalse();
        item.HasCustomText.ShouldBeFalse();

        // Complete it
        item.Status = "completed";
        item.IsPending.ShouldBeFalse();
        item.IsCompleted.ShouldBeTrue();
        item.IsSkipped.ShouldBeFalse();

        // Skip / stop it
        item.Status = "skipped";
        item.IsPending.ShouldBeFalse();
        item.IsCompleted.ShouldBeFalse();
        item.IsSkipped.ShouldBeTrue();

        // Set custom text
        item.CustomBody = "Take 2 pills instead of 1";
        item.HasCustomText.ShouldBeTrue();
    }

    [Fact]
    public void OccurrenceItemViewModel_editing_lifecycle()
    {
        var item = new OccurrenceItemViewModel
        {
            ReminderId = "rem_2",
            OccursAt = DateTimeOffset.UtcNow,
            Body = "Weekly sync",
            EditText = "Weekly sync",
        };

        item.IsEditing.ShouldBeFalse();
        item.IsEditing = true;
        item.EditText = "Sync with design team";
        item.EditText.ShouldBe("Sync with design team");
    }

    [Fact]
    public void EventDetailModalViewModel_handles_recurring_occurrences()
    {
        var modal = new EventDetailModalViewModel
        {
            Id = "rem_recur",
            Title = "Standup",
            IsRecurring = true,
            Recurrence = "Every day at 9 am",
        };

        modal.IsRecurring.ShouldBeTrue();
        modal.Occurrences.ShouldBeEmpty();

        var occ1 = new OccurrenceItemViewModel
        {
            ReminderId = modal.Id,
            OccursAt = DateTimeOffset.UtcNow,
            Body = modal.Title,
            DateLabel = "Thu 8 Oct · 09:00",
        };
        modal.Occurrences.Add(occ1);

        modal.Occurrences.Count.ShouldBe(1);
        modal.Occurrences[0].Body.ShouldBe("Standup");
    }

    [Fact]
    public void TargetOccurrenceCount_returns_10_hourly_7_daily_3_otherwise()
    {
        Recurrence.TargetOccurrenceCount("FREQ=HOURLY").ShouldBe(10);
        Recurrence.TargetOccurrenceCount("FREQ=HOURLY;INTERVAL=2").ShouldBe(10);
        Recurrence.TargetOccurrenceCount("FREQ=DAILY").ShouldBe(7);
        Recurrence.TargetOccurrenceCount("FREQ=DAILY;INTERVAL=3").ShouldBe(7);
        Recurrence.TargetOccurrenceCount("FREQ=WEEKLY;BYDAY=MO").ShouldBe(3);
        Recurrence.TargetOccurrenceCount("FREQ=MONTHLY").ShouldBe(3);
        Recurrence.TargetOccurrenceCount("FREQ=YEARLY").ShouldBe(3);

        RecurrenceExpander.TargetOccurrenceCount("FREQ=HOURLY").ShouldBe(10);
        RecurrenceExpander.TargetOccurrenceCount("FREQ=DAILY").ShouldBe(7);
        RecurrenceExpander.TargetOccurrenceCount("FREQ=YEARLY").ShouldBe(3);
    }
}


