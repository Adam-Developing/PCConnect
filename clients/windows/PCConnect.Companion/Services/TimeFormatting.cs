using System.Globalization;
using System.Text.RegularExpressions;

namespace PCConnect.Companion.Services;

/// <summary>
/// Centralized formatting and parsing for times across the Companion app,
/// respecting the user's 24-hour vs 12-hour preference.
/// </summary>
public static partial class TimeFormatting
{
    public static string FormatTime(TimeOnly time, bool use24Hour) =>
        time.ToString(use24Hour ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);

    public static string FormatTime(DateTimeOffset dateTime, bool use24Hour) =>
        dateTime.ToLocalTime().ToString(use24Hour ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);

    public static string FormatTime(DateTime dateTime, bool use24Hour) =>
        dateTime.ToString(use24Hour ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);

    public static string FormatWithSeconds(DateTimeOffset dateTime, bool use24Hour) =>
        dateTime.ToLocalTime().ToString(use24Hour ? "HH:mm:ss" : "h:mm:ss tt", CultureInfo.CurrentCulture);

    public static bool TryParse(string? text, out TimeOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        // 1. Standard localized and invariant parsing (e.g. 14:30, 2:30 PM, 2:30pm)
        if (TimeOnly.TryParse(trimmed, CultureInfo.CurrentCulture, out result) ||
            TimeOnly.TryParse(trimmed, CultureInfo.InvariantCulture, out result))
        {
            return true;
        }

        // 2. Format: "HHmm" or "Hmm" e.g. "930" -> 09:30, "1430" -> 14:30
        var matchDigits = DigitsOnlyRegex().Match(trimmed);
        if (matchDigits.Success &&
            int.TryParse(matchDigits.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) &&
            int.TryParse(matchDigits.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minute))
        {
            if (hour >= 0 && hour < 24 && minute >= 0 && minute < 60)
            {
                result = new TimeOnly(hour, minute);
                return true;
            }
        }

        // 3. Format: "9am", "2 pm", "11pm", "2:30am", "230pm", "930am"
        var matchAmPm = AmPmRegex().Match(trimmed);
        if (matchAmPm.Success)
        {
            int h12;
            var m12 = 0;
            if (matchAmPm.Groups[1].Success)
            {
                _ = int.TryParse(matchAmPm.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out h12);
                _ = int.TryParse(matchAmPm.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out m12);
            }
            else if (matchAmPm.Groups[3].Success)
            {
                _ = int.TryParse(matchAmPm.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out h12);
                _ = int.TryParse(matchAmPm.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out m12);
            }
            else
            {
                _ = int.TryParse(matchAmPm.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out h12);
            }

            var isPm = matchAmPm.Groups[6].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            if (h12 >= 1 && h12 <= 12 && m12 >= 0 && m12 < 60)
            {
                if (isPm && h12 < 12)
                {
                    h12 += 12;
                }
                else if (!isPm && h12 == 12)
                {
                    h12 = 0;
                }

                result = new TimeOnly(h12, m12);
                return true;
            }
        }

        // 4. Single hour number: "9", "14"
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var hourOnly) &&
            hourOnly >= 0 && hourOnly < 24)
        {
            result = new TimeOnly(hourOnly, 0);
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^(\d{1,2})([0-5]\d)$")]
    private static partial Regex DigitsOnlyRegex();

    [GeneratedRegex(@"^(?:(\d{1,2}):([0-5]\d)|(\d{1,2})([0-5]\d)|(\d{1,2}))\s*(am|pm)$", RegexOptions.IgnoreCase)]
    private static partial Regex AmPmRegex();
}
