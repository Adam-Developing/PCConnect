using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCConnect.Infrastructure.Security;

public sealed record EmailPayload(string Recipient, string Token);
public sealed record EncryptedEmailPayload(byte[] Ciphertext, byte[] Nonce, byte[] Tag, string KeyId);

public interface IEmailCipher
{
    EncryptedEmailPayload Encrypt(Guid messageId, string templateName, EmailPayload payload);
    EmailPayload Decrypt(Guid messageId, string templateName, EncryptedEmailPayload encrypted);
}

public sealed class EmailCipher : IEmailCipher
{
    private readonly string activeKeyId;
    private readonly IReadOnlyDictionary<string, byte[]> keys;

    public EmailCipher(SecurityOptions options)
    {
        activeKeyId = options.ActiveEmailKeyId;
        keys = options.DecodeEmailKeys();
        if (!keys.ContainsKey(activeKeyId)) throw new InvalidOperationException("The active email encryption key is not configured.");
    }

    public EncryptedEmailPayload Encrypt(Guid messageId, string templateName, EmailPayload payload)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[clear.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(keys[activeKeyId], 16);
            aes.Encrypt(nonce, clear, ciphertext, tag, AssociatedData(messageId, templateName, activeKeyId));
            return new(ciphertext, nonce, tag, activeKeyId);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public EmailPayload Decrypt(Guid messageId, string templateName, EncryptedEmailPayload encrypted)
    {
        if (!keys.TryGetValue(encrypted.KeyId, out var key)) throw new CryptographicException("Email encryption key is unavailable.");
        var clear = new byte[encrypted.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(encrypted.Nonce, encrypted.Ciphertext, encrypted.Tag, clear, AssociatedData(messageId, templateName, encrypted.KeyId));
            return JsonSerializer.Deserialize<EmailPayload>(clear, JsonOptions) ?? throw new CryptographicException("Email payload is invalid.");
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static byte[] AssociatedData(Guid id, string templateName, string keyId) =>
        Encoding.UTF8.GetBytes($"pcconnect.email.v1|{id:D}|{templateName}|{keyId}");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
