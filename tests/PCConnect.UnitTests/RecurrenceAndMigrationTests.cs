using Microsoft.Extensions.Logging.Abstractions;
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
