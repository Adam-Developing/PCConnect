using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PCConnect.Client;

namespace PCConnect.Companion.Services;

/// <summary>
/// The companion's refresh token, encrypted with DPAPI under the current user.
///
/// The v1 client kept <c>My.Settings.Password</c> — a password-equivalent
/// SHA-256 — in a plaintext user.config, which is S1-04. This stores a rotating
/// refresh token instead, encrypted so that reading the file as another user, or
/// lifting it off a backup, yields nothing.
/// </summary>
public sealed class DpapiTokenStore(ILogger<DpapiTokenStore> logger, string? path = null) : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PCConnect.Companion.v2");

    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCConnect", "session.bin");

    public async Task<StoredTokens?> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_path, ct);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredTokens>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // An unreadable session is a signed-out session, not a crash.
            logger.LogWarning(ex, "The stored session could not be read; signing out");
            return null;
        }
    }

    public async Task WriteAsync(StoredTokens tokens, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var plain = JsonSerializer.SerializeToUtf8Bytes(tokens);
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(_path, protectedBytes, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }
}
