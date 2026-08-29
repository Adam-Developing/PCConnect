using PCConnect.Domain;
using Xunit;

namespace PCConnect.UnitTests;

public sealed class PageCursorTests
{
    [Fact]
    public void CursorRoundTripsTimestampAndIdentifier()
    {
        var timestamp = new DateTimeOffset(2026, 8, 26, 12, 30, 0, TimeSpan.Zero).AddTicks(42);
        var id = Guid.NewGuid();
        var decoded = PageCursor.Decode(PageCursor.Encode(timestamp, id));
        Assert.Equal(timestamp, decoded!.Value.Timestamp);
        Assert.Equal(id, decoded.Value.Id);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("MQ==")]
    public void CursorRejectsMalformedInput(string cursor) =>
        Assert.Throws<ArgumentException>(() => PageCursor.Decode(cursor));
}
