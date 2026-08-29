using System.Buffers.Binary;
using System.Text.Json;
using System.Security.Cryptography;
using PCConnect.Contracts.V2;
using PCConnect.Windows.Agent;
using PCConnect.Windows.Protocol;
using Xunit;

namespace PCConnect.WindowsTests;

public sealed class ProtocolSecurityTests
{
    [Fact]
    public async Task FrameCodecRoundTripsTypedRequest()
    {
        var request = new ExecuteRequestMessage(1, "execute_request", Guid.NewGuid(), DateTimeOffset.UtcNow,
            Guid.NewGuid(), "lock", DateTimeOffset.UtcNow.AddMinutes(1), Guid.NewGuid());
        await using var stream = new MemoryStream();
        await PipeFrameCodec.WriteAsync(stream, request, PipeJsonContext.Default.ExecuteRequestMessage, TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var document = await PipeFrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal("execute_request", document.RootElement.GetProperty("messageType").GetString());
    }

    [Fact]
    public async Task FrameCodecRejectsOversizedAndTruncatedFrames()
    {
        var oversized = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(oversized, PipeFrameCodec.MaximumFrameBytes + 1);
        await using var oversizedStream = new MemoryStream(oversized);
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeFrameCodec.ReadAsync(oversizedStream, TestContext.Current.CancellationToken));

        var truncated = new byte[6];
        BinaryPrimitives.WriteInt32BigEndian(truncated, 10);
        await using var truncatedStream = new MemoryStream(truncated);
        await Assert.ThrowsAsync<EndOfStreamException>(() => PipeFrameCodec.ReadAsync(truncatedStream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReplayCacheAcceptsAKeyOnlyOnceWithinRetention()
    {
        var cache = new ReplayCache(TimeSpan.FromHours(24));
        var key = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.True(cache.TryAccept(key, now));
        Assert.False(cache.TryAccept(key, now.AddMinutes(1)));
        Assert.True(cache.TryAccept(key, now.AddHours(25)));
    }

    [Theory]
    [InlineData(CommandType.Lock)]
    [InlineData(CommandType.SignOut)]
    public void MachineExecutorRejectsInteractiveCommands(CommandType type)
    {
        var executor = new WindowsFixedCommandExecutor();
        Assert.False(executor.Supports(type));
    }

    [Fact]
    public void ChallengeProofIsBoundToSidPidNonceAndRequest()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var hello = new HelloMessage(1, "hello", Guid.NewGuid(), DateTimeOffset.UtcNow, 42, "S-1-5-21-1", PipeProtocol.CreateNonce());
        var proof = PipeProtocol.CreateProof(secret, hello);
        Assert.True(PipeProtocol.VerifyProof(secret, hello, proof));
        Assert.False(PipeProtocol.VerifyProof(secret, hello with { ProcessId = 43 }, proof));
    }

    [Fact]
    public void HelloRejectsUnknownPropertiesAndClockReplayWindow()
    {
        using var unknown = JsonDocument.Parse("""{"protocolVersion":1,"messageType":"hello","requestId":"00000000-0000-0000-0000-000000000001","sentAt":"2026-01-01T00:00:00Z","processId":1,"userSid":"S-1-5-21-1","nonce":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","unexpected":true}""");
        Assert.Throws<InvalidDataException>(() => PipeProtocol.ReadAndValidateHello(unknown, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        using var stale = JsonDocument.Parse("""{"protocolVersion":1,"messageType":"hello","requestId":"00000000-0000-0000-0000-000000000001","sentAt":"2026-01-01T00:00:00Z","processId":1,"userSid":"S-1-5-21-1","nonce":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""");
        Assert.Throws<InvalidDataException>(() => PipeProtocol.ReadAndValidateHello(stale, new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero)));
    }
}
