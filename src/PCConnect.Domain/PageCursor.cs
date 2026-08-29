using System.Globalization;
using System.Text;

namespace PCConnect.Domain;

public readonly record struct PagePosition(DateTimeOffset Timestamp, Guid Id);

public static class PageCursor
{
    private const string Version = "1";

    public static string Encode(DateTimeOffset timestamp, Guid id)
    {
        var clear = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Version}|{timestamp.UtcTicks}|{id:D}"));
        return Convert.ToBase64String(clear).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static PagePosition? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        if (cursor.Length > 512) throw new ArgumentException("Cursor is too long.", nameof(cursor));
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('|');
            if (parts.Length != 3 || parts[0] != Version || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParseExact(parts[2], "D", out var id))
                throw new FormatException();
            return new(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
        {
            throw new ArgumentException("Cursor is invalid.", nameof(cursor), exception);
        }
    }
}
