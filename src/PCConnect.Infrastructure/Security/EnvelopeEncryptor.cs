using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PCConnect.Core;

namespace PCConnect.Infrastructure.Security;

public sealed class EnvelopeOptions
{
    /// <summary>
    /// Base64 32-byte key encryption keys, keyed by id. Two slots exist so a KEK
    /// rotation is not a flag day: the new key becomes <see cref="CurrentKekId"/>
    /// and the old one stays available for unwrapping until every DEK is rewrapped.
    /// Never stored in the database (03 §4.1).
    /// </summary>
    public Dictionary<string, string> Keys { get; init; } = [];

    public string CurrentKekId { get; set; } = "k1";
}

/// <summary>
/// AES-256-GCM envelope encryption. Replaces AES-256-CBC keyed by the user's API
/// key, which made credential rotation and data preservation mutually exclusive
/// (S1-06) and left ciphertext unauthenticated (S1-07).
/// </summary>
public sealed class EnvelopeEncryptor : IEnvelopeEncryptor
{
    private const int NonceBytes = 12;   // 96-bit random nonce, never reused with one key
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    private readonly Dictionary<string, byte[]> _keks;

    public EnvelopeEncryptor(IOptions<EnvelopeOptions> options)
    {
        var value = options.Value;

        // Case-insensitive: environment variables are conventionally upper case
        // (PCCONNECT_KEK__KEYS__K1) while the id in configuration reads better
        // lower case, and a mismatch between the two would surface as "no
        // matching key" at boot for no good reason.
        _keks = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, base64) in value.Keys)
        {
            // An empty slot is the resting state of the rotation slot, not a
            // misconfiguration: KEK_PREVIOUS is set only while a rotation is in
            // progress (08 §3).
            if (string.IsNullOrWhiteSpace(base64))
            {
                continue;
            }

            byte[] key;
            try
            {
                key = Convert.FromBase64String(base64);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"KEK '{id}' is not valid base64.", ex);
            }

            if (key.Length != KeyBytes)
            {
                throw new InvalidOperationException($"KEK '{id}' must be exactly {KeyBytes} bytes, was {key.Length}.");
            }

            _keks[id] = key;
        }

        if (_keks.Count == 0)
        {
            throw new InvalidOperationException(
                "No key encryption key configured. Set PCCONNECT_KEK__KEYS__k1 to a base64 32-byte key.");
        }

        if (!_keks.ContainsKey(value.CurrentKekId))
        {
            throw new InvalidOperationException($"CurrentKekId '{value.CurrentKekId}' has no matching key.");
        }

        CurrentKekId = value.CurrentKekId;
    }

    public string CurrentKekId { get; }

    public (byte[] Wrapped, string KekId) CreateDataKey()
    {
        var dek = RandomNumberGenerator.GetBytes(KeyBytes);
        try
        {
            return WrapDataKey(dek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>
    /// Wraps a data key that already exists with whichever KEK is current.
    ///
    /// This is what finishes a KEK rotation. Without it the previous key can
    /// never be retired: every user created before the rotation still has a DEK
    /// wrapped with it, so removing it locks them out of their own reminders.
    /// The data key itself does not change, so nothing encrypted under it has
    /// to be re-encrypted — that is the point of the envelope (ADR-0004).
    /// </summary>
    public (byte[] Wrapped, string KekId) WrapDataKey(byte[] dataKey)
    {
        if (dataKey.Length != KeyBytes)
        {
            throw new ArgumentException($"A data key must be {KeyBytes} bytes.", nameof(dataKey));
        }

        return (Seal(_keks[CurrentKekId], dataKey, Encoding.UTF8.GetBytes("pcconnect:dek")), CurrentKekId);
    }

    /// <summary>
    /// Whether a data key wrapped with this KEK could be opened at all.
    /// Asking beforehand lets a bulk operation skip and report what it cannot
    /// read, instead of failing on the first row it meets.
    /// </summary>
    public bool CanUnwrapWith(string kekId) => _keks.ContainsKey(kekId);

    public byte[] UnwrapDataKey(byte[] wrapped, string kekId)
    {
        if (!_keks.TryGetValue(kekId, out var kek))
        {
            throw new InvalidOperationException(
                $"Data key was wrapped with KEK '{kekId}', which is not configured. Restore it before serving this user.");
        }

        return Open(kek, wrapped, Encoding.UTF8.GetBytes("pcconnect:dek"));
    }

    public byte[] Encrypt(byte[] dataKey, string plaintext, string associatedData) =>
        Seal(dataKey, Encoding.UTF8.GetBytes(plaintext), Encoding.UTF8.GetBytes(associatedData));

    public string Decrypt(byte[] dataKey, byte[] ciphertext, string associatedData) =>
        Encoding.UTF8.GetString(Open(dataKey, ciphertext, Encoding.UTF8.GetBytes(associatedData)));

    /// <summary>Layout: <c>[12B nonce][ciphertext][16B tag]</c>.</summary>
    private static byte[] Seal(byte[] key, byte[] plaintext, byte[] associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var output = new byte[NonceBytes + plaintext.Length + TagBytes];
        nonce.CopyTo(output, 0);

        using var gcm = new AesGcm(key, TagBytes);
        gcm.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(NonceBytes, plaintext.Length),
            output.AsSpan(NonceBytes + plaintext.Length, TagBytes),
            associatedData);

        return output;
    }

    private static byte[] Open(byte[] key, byte[] sealedBytes, byte[] associatedData)
    {
        if (sealedBytes.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("Ciphertext is shorter than the nonce and tag it must contain.");
        }

        var cipherLength = sealedBytes.Length - NonceBytes - TagBytes;
        var plaintext = new byte[cipherLength];

        using var gcm = new AesGcm(key, TagBytes);
        gcm.Decrypt(
            sealedBytes.AsSpan(0, NonceBytes),
            sealedBytes.AsSpan(NonceBytes, cipherLength),
            sealedBytes.AsSpan(NonceBytes + cipherLength, TagBytes),
            plaintext,
            associatedData);

        return plaintext;
    }
}

/// <summary>
/// Decrypts the v1 reminder format so the importer can re-encrypt it. This is
/// the only place AES-256-CBC survives, and it exists purely to be able to read
/// what the old system wrote (02 §7).
/// </summary>
public static class LegacyReminderCipher
{
    /// <summary>
    /// v1 stored <c>base64(iv) + ':' + base64(ciphertext)</c>, encrypted with
    /// AES-256-CBC keyed by the raw bytes of <c>users.api_key</c>. Returns false
    /// rather than throwing on anything unparseable: an unreadable legacy row is
    /// a migration exception to record, not a crash.
    /// </summary>
    public static bool TryDecrypt(string apiKey, string stored, out string plaintext)
    {
        plaintext = string.Empty;

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(stored))
        {
            return false;
        }

        var separator = stored.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        try
        {
            var iv = Convert.FromBase64String(stored[..separator]);
            var body = stored[(separator + 1)..];

            if (iv.Length != 16)
            {
                return false;
            }

            var key = DeriveLegacyKey(apiKey);

            // v1's helper speculatively base64-decoded the body a second time, so
            // the stored data contains both shapes. Each candidate is tried in
            // turn rather than guessing which generation wrote the row.
            foreach (var cipherBytes in CandidateCiphertexts(body))
            {
                if (cipherBytes.Length == 0 || cipherBytes.Length % 16 != 0)
                {
                    continue;
                }

                try
                {
                    using var aes = Aes.Create();
                    aes.Key = key;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using var decryptor = aes.CreateDecryptor();
                    var clear = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                    plaintext = Encoding.UTF8.GetString(clear);
                    return true;
                }
                catch (CryptographicException)
                {
                    // Wrong shape for this candidate; try the next.
                }
            }

            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static IEnumerable<byte[]> CandidateCiphertexts(string body)
    {
        byte[]? once = null;

        try
        {
            once = Convert.FromBase64String(body);
        }
        catch (FormatException)
        {
            // Not base64 at all: the raw bytes are the only candidate.
        }

        if (once is not null)
        {
            yield return once;

            // The double-encoded shape: the first decode yields the inner base64
            // text, which decodes again to the ciphertext.
            byte[]? twice = null;
            try
            {
                twice = Convert.FromBase64String(Encoding.UTF8.GetString(once));
            }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
            {
                // Single-encoded after all.
            }

            if (twice is not null)
            {
                yield return twice;
            }
        }
        else
        {
            yield return Encoding.UTF8.GetBytes(body);
        }
    }

    /// <summary>
    /// v1 passed the API key string straight to <c>createDecipheriv</c>, which
    /// takes the raw UTF-8 bytes and requires exactly 32 of them. Keys that were
    /// not 32 bytes are padded or truncated here the same way, so the importer
    /// reproduces v1's behaviour rather than a corrected version of it.
    /// </summary>
    private static byte[] DeriveLegacyKey(string apiKey)
    {
        var raw = Encoding.UTF8.GetBytes(apiKey);
        var key = new byte[32];
        Array.Copy(raw, key, Math.Min(raw.Length, 32));
        return key;
    }
}
