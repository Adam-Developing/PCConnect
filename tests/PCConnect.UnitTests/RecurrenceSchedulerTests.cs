using PCConnect.Domain.Reminders;
using System.Globalization;
using Xunit;

namespace PCConnect.UnitTests;

public sealed class RecurrenceSchedulerTests
{
    [Fact]
    public void DstGapMovesToNextValidLocalInstant()
    {
        var occurrence = RecurrenceScheduler.Resolve(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Unspecified), TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
        Assert.Equal(new DateTime(2026, 3, 29, 2, 0, 0), occurrence.LocalOccurrence);
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T01:00:00Z", CultureInfo.InvariantCulture), occurrence.Instant);
    }

    [Fact]
    public void DstOverlapChoosesEarlierInstant()
    {
        var occurrence = RecurrenceScheduler.Resolve(new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Unspecified), TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:30:00Z", CultureInfo.InvariantCulture), occurrence.Instant);
    }

    [Fact]
    public void DailyCountIsBounded()
    {
        var result = RecurrenceScheduler.Generate(new DateTime(2026, 1, 1, 9, 0, 0), "Europe/London", "FREQ=DAILY;COUNT=3", DateTimeOffset.Parse("2026-02-01T00:00:00Z", CultureInfo.InvariantCulture));
        Assert.Equal(3, result.Count);
    }
}
