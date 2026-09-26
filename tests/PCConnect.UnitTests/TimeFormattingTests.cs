using PCConnect.Companion.Services;
using PCConnect.Companion.ViewModels;
using Shouldly;

namespace PCConnect.UnitTests;

public sealed class TimeFormattingTests
{
    [Theory]
    [InlineData(14, 5, true, "14:05")]
    [InlineData(9, 0, true, "09:00")]
    [InlineData(0, 0, true, "00:00")]
    [InlineData(23, 59, true, "23:59")]
    [InlineData(14, 5, false, "2:05 PM")]
    [InlineData(9, 0, false, "9:00 AM")]
    [InlineData(0, 0, false, "12:00 AM")]
    [InlineData(12, 0, false, "12:00 PM")]
    [InlineData(12, 30, false, "12:30 PM")]
    [InlineData(23, 59, false, "11:59 PM")]
    public void FormatTime_formats_consistently_with_clock_setting(int hour, int minute, bool use24Hour, string expected)
    {
        var time = new TimeOnly(hour, minute);
        TimeFormatting.FormatTime(time, use24Hour).ShouldBe(expected);

        var dtLocal = new DateTimeOffset(2026, 9, 25, hour, minute, 0, DateTimeOffset.Now.Offset);
        TimeFormatting.FormatTime(dtLocal, use24Hour).ShouldBe(expected);
    }

    [Theory]
    [InlineData(14, 5, 9, true, "14:05:09")]
    [InlineData(9, 0, 0, true, "09:00:00")]
    [InlineData(14, 5, 9, false, "2:05:09 PM")]
    [InlineData(9, 0, 0, false, "9:00:00 AM")]
    [InlineData(0, 0, 0, false, "12:00:00 AM")]
    public void FormatWithSeconds_formats_consistently(int hour, int minute, int second, bool use24Hour, string expected)
    {
        var dt = new DateTime(2026, 9, 25, hour, minute, second);
        TimeFormatting.FormatWithSeconds(dt, use24Hour).ShouldBe(expected);
    }

    [Theory]
    [InlineData("14:30", 14, 30)]
    [InlineData("09:15", 9, 15)]
    [InlineData("00:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    [InlineData("1430", 14, 30)]
    [InlineData("0915", 9, 15)]
    [InlineData("930", 9, 30)]
    [InlineData("2:30 PM", 14, 30)]
    [InlineData("2:30 pm", 14, 30)]
    [InlineData("9:15 AM", 9, 15)]
    [InlineData("12:00 AM", 0, 0)]
    [InlineData("12:00 PM", 12, 0)]
    [InlineData("12:15 AM", 0, 15)]
    [InlineData("12:15 PM", 12, 15)]
    [InlineData("230pm", 14, 30)]
    [InlineData("930am", 9, 30)]
    [InlineData("2pm", 14, 0)]
    [InlineData("9am", 9, 0)]
    [InlineData("12am", 0, 0)]
    [InlineData("12pm", 12, 0)]
    public void TryParse_parses_valid_time_formats(string input, int expectedHour, int expectedMinute)
    {
        TimeFormatting.TryParse(input, out var time).ShouldBeTrue();
        time.Hour.ShouldBe(expectedHour);
        time.Minute.ShouldBe(expectedMinute);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid")]
    [InlineData("25:00")]
    [InlineData("14:60")]
    [InlineData("99999")]
    [InlineData("abc pm")]
    public void TryParse_rejects_invalid_inputs(string input)
    {
        TimeFormatting.TryParse(input, out _).ShouldBeFalse();
    }

    [Fact]
    public void Monday_to_Sunday_range_covers_current_week()
    {
        // Test with every possible DayOfWeek
        for (var dayOffset = 0; dayOffset < 7; dayOffset++)
        {
            var testDate = new DateOnly(2026, 9, 21).AddDays(dayOffset); // 2026-09-21 is a Monday
            var daysFromMonday = ((int)testDate.DayOfWeek + 6) % 7;
            var monday = testDate.AddDays(-daysFromMonday);
            var sunday = monday.AddDays(6);

            monday.DayOfWeek.ShouldBe(DayOfWeek.Monday);
            sunday.DayOfWeek.ShouldBe(DayOfWeek.Sunday);
            monday.ShouldBe(new DateOnly(2026, 9, 21));
            sunday.ShouldBe(new DateOnly(2026, 9, 27));
        }
    }

    [Fact]
    public void WeekItem_record_stores_modal_properties()
    {
        var item = new WeekItem(
            Time: "14:30",
            Body: "Dentist appointment",
            Id: "test-reminder-1",
            DayLabel: "Fri",
            FullDate: "Friday 25 September 2026",
            RecurrenceText: "Every Friday",
            TargetText: "This PC",
            IsCompleted: false,
            IsSnoozed: true,
            SnoozeLabel: "Snoozed for 10 mins",
            IsDismissed: false);

        item.Id.ShouldBe("test-reminder-1");
        item.Time.ShouldBe("14:30");
        item.Body.ShouldBe("Dentist appointment");
        item.DayLabel.ShouldBe("Fri");
        item.FullDate.ShouldBe("Friday 25 September 2026");
        item.RecurrenceText.ShouldBe("Every Friday");
        item.TargetText.ShouldBe("This PC");
        item.IsCompleted.ShouldBeFalse();
        item.IsSnoozed.ShouldBeTrue();
        item.SnoozeLabel.ShouldBe("Snoozed for 10 mins");
        item.IsDismissed.ShouldBeFalse();
        item.IsSnoozedBadgeVisible.ShouldBeTrue();
        item.IsCompletedBadgeVisible.ShouldBeFalse();
        item.IsWonTRerunBadgeVisible.ShouldBeFalse();
        item.IsActiveBadgeVisible.ShouldBeFalse();
    }

    [Fact]
    public void EventDetailModalViewModel_initializes_with_provided_properties()
    {
        var model = new EventDetailModalViewModel
        {
            Id = "test-reminder-1",
            Title = "Dentist appointment",
            TimeAndDate = "14:30 · Friday 25 September 2026",
            Recurrence = "Every Friday",
            Targets = "This PC",
            StatusText = "Won't rerun",
            StatusTone = "Danger",
            IsCompleted = false,
            IsSnoozed = false,
            IsDismissed = true,
            CanComplete = true,
            CanEdit = true,
            CanDelete = true
        };

        model.Id.ShouldBe("test-reminder-1");
        model.Title.ShouldBe("Dentist appointment");
        model.TimeAndDate.ShouldBe("14:30 · Friday 25 September 2026");
        model.Recurrence.ShouldBe("Every Friday");
        model.Targets.ShouldBe("This PC");
        model.StatusText.ShouldBe("Won't rerun");
        model.StatusTone.ShouldBe("Danger");
        model.IsCompleted.ShouldBeFalse();
        model.IsDismissed.ShouldBeTrue();
        model.IsWonTRerunBadgeVisible.ShouldBeTrue();
        model.IsCompletedBadgeVisible.ShouldBeFalse();
        model.IsSnoozedBadgeVisible.ShouldBeFalse();
        model.IsActiveBadgeVisible.ShouldBeFalse();
        model.CanComplete.ShouldBeTrue();
        model.CanEdit.ShouldBeTrue();
        model.CanDelete.ShouldBeTrue();
    }
}
