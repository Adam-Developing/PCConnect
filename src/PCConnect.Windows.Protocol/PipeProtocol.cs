using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCConnect.Windows.Protocol;

public static class PipeProtocol
{
    public const int Version = 1;
    public const string PipeName = "pcconnect-agent-v1";
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> HelloProperties = ["protocolVersion", "messageType", "requestId", "sentAt", "processId", "userSid", "nonce"];

    public static HelloMessage ReadAndValidateHello(JsonDocument document, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(property => !HelloProperties.Contains(property.Name)))
            throw new InvalidDataException("The hello frame contains an unknown property.");
        var hello = root.Deserialize(PipeJsonContext.Default.HelloMessage) ?? throw new InvalidDataException("The hello frame is invalid.");
        if (hello.ProtocolVersion != Version || hello.MessageType != "hello" || hello.RequestId == Guid.Empty || hello.ProcessId <= 0)
            throw new InvalidDataException("The hello frame has an unsupported version or shape.");
        if (!hello.UserSid.StartsWith("S-1-", StringComparison.Ordinal) || hello.UserSid.Length > 184)
            throw new InvalidDataException("The hello frame contains an invalid SID.");
        if ((now - hello.SentAt).Duration() > MaximumClockSkew) throw new InvalidDataException("The hello frame is outside the allowed clock skew.");
        if (hello.Nonce.Length is < 43 or > 128) throw new InvalidDataException("The hello nonce has an invalid length.");
        try { _ = Base64UrlDecode(hello.Nonce); }
        catch (FormatException exception) { throw new InvalidDataException("The hello nonce is not Base64url.", exception); }
        return hello;
    }

    public static string CreateNonce() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string CreateProof(ReadOnlySpan<byte> secret, HelloMessage hello)
    {
        var input = Encoding.UTF8.GetBytes($"{hello.Nonce}|{hello.ProcessId}|{hello.UserSid}|{hello.RequestId:D}");
        try { return Base64Url(HMACSHA256.HashData(secret, input)); }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    public static bool VerifyProof(ReadOnlySpan<byte> secret, HelloMessage hello, string proof)
    {
        var expected = CreateProof(secret, hello);
        var left = Encoding.ASCII.GetBytes(expected);
        var right = Encoding.ASCII.GetBytes(proof);
        try { return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right); }
        finally { CryptographicOperations.ZeroMemory(left); CryptographicOperations.ZeroMemory(right); }
    }

    public static void EnsureExactProperties(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The pipe message must be a JSON object.");
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        if (element.EnumerateObject().Any(property => !names.Contains(property.Name)))
            throw new InvalidDataException("The pipe message contains an unknown property.");
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
