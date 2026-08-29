using System.Globalization;

namespace PCConnect.Domain.Reminders;

public sealed record ScheduledOccurrence(DateTime LocalOccurrence, DateTimeOffset Instant, int TimezoneOffsetSeconds);

public static class RecurrenceScheduler
{
    public static IReadOnlyList<ScheduledOccurrence> Generate(DateTime localStart, string timezoneId, string? recurrenceRule, DateTimeOffset windowEnd, int maximum = 10_000)
    {
        if (localStart.Kind != DateTimeKind.Unspecified) localStart = DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified);
        var timezone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        var rule = Parse(recurrenceRule);
        var items = new List<ScheduledOccurrence>();
        var local = localStart;
        for (var index = 0; index < maximum; index++)
        {
            var occurrence = Resolve(local, timezone);
            if (occurrence.Instant > windowEnd) break;
            if (rule.Until is not null && occurrence.Instant > rule.Until) break;
            items.Add(occurrence);
            if (rule.Frequency is null || (rule.Count is not null && items.Count >= rule.Count)) break;
            local = rule.Frequency switch
            {
                "DAILY" => local.AddDays(rule.Interval),
                "WEEKLY" => local.AddDays(7 * rule.Interval),
                "MONTHLY" => local.AddMonths(rule.Interval),
                "YEARLY" => local.AddYears(rule.Interval),
                _ => throw new ArgumentException("Unsupported recurrence frequency.", nameof(recurrenceRule))
            };
        }
        if (items.Count == maximum) throw new InvalidOperationException("Recurrence expansion exceeded its safety bound.");
        return items;
    }

    public static ScheduledOccurrence Resolve(DateTime local, TimeZoneInfo timezone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (timezone.IsInvalidTime(local)) local = local.AddMinutes(1);
        TimeSpan offset;
        if (timezone.IsAmbiguousTime(local))
            offset = timezone.GetAmbiguousTimeOffsets(local).Max(); // Larger offset produces the earlier UTC instant.
        else
            offset = timezone.GetUtcOffset(local);
        var instant = new DateTimeOffset(local, offset).ToUniversalTime();
        return new(local, instant, checked((int)offset.TotalSeconds));
    }

    private static ParsedRule Parse(string? recurrenceRule)
    {
        if (recurrenceRule is null) return new(null, 1, null, null);
        if (recurrenceRule.Length > 1000 || !recurrenceRule.StartsWith("FREQ=", StringComparison.Ordinal)) throw new ArgumentException("Recurrence rule must start with FREQ=.");
        var values = recurrenceRule.Split(';').Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => x[0].ToUpperInvariant(), x => x[1].ToUpperInvariant(), StringComparer.Ordinal);
        if (values.Keys.Any(x => x is not ("FREQ" or "INTERVAL" or "COUNT" or "UNTIL")))
            throw new ArgumentException("The recurrence rule contains an unsupported property.");
        if (!values.TryGetValue("FREQ", out var frequency) || frequency is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"))
            throw new ArgumentException("Only DAILY, WEEKLY, MONTHLY and YEARLY recurrence are supported.");
        var interval = values.TryGetValue("INTERVAL", out var rawInterval) && int.TryParse(rawInterval, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedInterval) ? parsedInterval : 1;
        if (interval is < 1 or > 1000) throw new ArgumentException("Recurrence interval is invalid.");
        int? count = null;
        if (values.TryGetValue("COUNT", out var rawCount))
        {
            if (!int.TryParse(rawCount, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCount) || parsedCount is < 1 or > 10_000) throw new ArgumentException("Recurrence count is invalid.");
            count = parsedCount;
        }
        DateTimeOffset? until = null;
        if (values.TryGetValue("UNTIL", out var rawUntil))
        {
            if (!DateTimeOffset.TryParseExact(rawUntil, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedUntil)) throw new ArgumentException("UNTIL must be a UTC RFC 5545 instant.");
            until = parsedUntil.ToUniversalTime();
        }
        return new(frequency, interval, count, until);
    }

    private sealed record ParsedRule(string? Frequency, int Interval, int? Count, DateTimeOffset? Until);
}
