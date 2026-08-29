using System.Security.Cryptography;
using System.Text;

namespace PCConnect.Infrastructure.Security;

public sealed record EncryptedReminder(byte[] Ciphertext, byte[] Nonce, byte[] Tag, byte[] WrappedDataKey, string WrappingKeyId, short TextAadVersion = 1);

public interface IReminderCipher
{
    EncryptedReminder Encrypt(Guid reminderId, Guid ownerId, string plaintext);
    string Decrypt(Guid reminderId, Guid ownerId, EncryptedReminder encrypted);
    EncryptedReminder Rewrap(Guid reminderId, Guid ownerId, EncryptedReminder encrypted);
}

public sealed class ReminderCipher : IReminderCipher
{
    private readonly string _activeKeyId;
    private readonly IReadOnlyDictionary<string, byte[]> _keys;

    public ReminderCipher(SecurityOptions options)
    {
        _activeKeyId = options.ActiveReminderKeyId;
        _keys = options.DecodeReminderKeys();
        if (!_keys.ContainsKey(_activeKeyId)) throw new InvalidOperationException("The active reminder wrapping key is not configured.");
    }

    public EncryptedReminder Encrypt(Guid reminderId, Guid ownerId, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var dataKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var clear = Encoding.UTF8.GetBytes(plaintext);
            var cipher = new byte[clear.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(dataKey, 16)) aes.Encrypt(nonce, clear, cipher, tag, AssociatedData(reminderId, ownerId, 1));
            return new(cipher, nonce, tag, Wrap(dataKey, _activeKeyId), _activeKeyId, 1);
        }
        finally { CryptographicOperations.ZeroMemory(dataKey); }
    }

    public string Decrypt(Guid reminderId, Guid ownerId, EncryptedReminder encrypted)
    {
        var dataKey = Unwrap(encrypted.WrappedDataKey, encrypted.WrappingKeyId);
        try
        {
            var clear = new byte[encrypted.Ciphertext.Length];
            using (var aes = new AesGcm(dataKey, 16)) aes.Decrypt(encrypted.Nonce, encrypted.Ciphertext, encrypted.Tag, clear, AssociatedData(reminderId, ownerId, encrypted.TextAadVersion));
            return Encoding.UTF8.GetString(clear);
        }
        finally { CryptographicOperations.ZeroMemory(dataKey); }
    }

    public EncryptedReminder Rewrap(Guid reminderId, Guid ownerId, EncryptedReminder encrypted)
    {
        if (encrypted.WrappingKeyId == _activeKeyId) return encrypted;
        var dataKey = Unwrap(encrypted.WrappedDataKey, encrypted.WrappingKeyId);
        try { return encrypted with { WrappedDataKey = Wrap(dataKey, _activeKeyId), WrappingKeyId = _activeKeyId }; }
        finally { CryptographicOperations.ZeroMemory(dataKey); }
    }

    private byte[] Wrap(byte[] dataKey, string keyId)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[dataKey.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_keys[keyId], 16)) aes.Encrypt(nonce, dataKey, cipher, tag, Encoding.UTF8.GetBytes(keyId));
        return [.. nonce, .. cipher, .. tag];
    }

    private byte[] Unwrap(byte[] wrapped, string keyId)
    {
        if (!_keys.TryGetValue(keyId, out var key)) throw new CryptographicException($"Reminder wrapping key '{keyId}' is unavailable.");
        if (wrapped.Length != 60) throw new CryptographicException("Wrapped reminder data key has an invalid length.");
        var clear = new byte[32];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(12, 32), wrapped.AsSpan(44, 16), clear, Encoding.UTF8.GetBytes(keyId));
        return clear;
    }

    private static byte[] AssociatedData(Guid reminderId, Guid ownerId, short version)
    {
        if (version != 1) throw new CryptographicException($"Unsupported reminder text AAD version '{version}'.");
        return Encoding.UTF8.GetBytes($"pcconnect.reminder.v1|{reminderId:D}|{ownerId:D}");
    }
}
