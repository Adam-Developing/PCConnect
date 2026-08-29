using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PCConnect.Windows.Protocol;

public static class PipeFrameCodec
{
    public const int MaximumFrameBytes = 65_536;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task WriteAsync<T>(Stream stream, T message, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        if (payload.Length is < 2 or > MaximumFrameBytes) throw new InvalidDataException("Named-pipe frame has an invalid length.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is < 2 or > MaximumFrameBytes) throw new InvalidDataException("Named-pipe frame has an invalid length.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        _ = Utf8.GetString(payload);
        return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException("Named-pipe frame was truncated.");
            offset += read;
        }
    }
}
