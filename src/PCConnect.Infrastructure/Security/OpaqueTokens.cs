using System.Security.Cryptography;
using System.Text;

namespace PCConnect.Infrastructure.Security;

public interface IOpaqueTokenService
{
    string Create();
    byte[] Hash(string token);
    string HashLegacyCredential(string credential);
}

public sealed class OpaqueTokenService(SecurityOptions options) : IOpaqueTokenService
{
    private readonly byte[] _tokenKey = options.DecodeTokenKey();
    private readonly byte[] _legacyKey = options.DecodeLegacyKey();

    public string Create() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public byte[] Hash(string token) => HMACSHA256.HashData(_tokenKey, Encoding.UTF8.GetBytes(token));
    public string HashLegacyCredential(string credential) => Convert.ToHexString(HMACSHA256.HashData(_legacyKey, Encoding.UTF8.GetBytes(credential))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
