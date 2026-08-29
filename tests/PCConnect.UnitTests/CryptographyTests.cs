using System.Security.Cryptography;
using PCConnect.Infrastructure.Security;
using Xunit;

namespace PCConnect.UnitTests;

public sealed class CryptographyTests
{
    [Fact]
    public async Task Argon2idRoundTripUsesArchitectureParameters()
    {
        var hasher = new Argon2IdPasswordHasher();
        var encoded = await hasher.HashAsync("correct horse battery staple", TestContext.Current.CancellationToken);
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", encoded, StringComparison.Ordinal);
        Assert.True(await hasher.VerifyAsync("correct horse battery staple", encoded, TestContext.Current.CancellationToken));
        Assert.False(await hasher.VerifyAsync("wrong password value", encoded, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LegacySha256IsComparedWithoutBecomingARequestCredential()
    {
        var hasher = new Argon2IdPasswordHasher();
        var legacy = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("legacy password"))).ToLowerInvariant();
        Assert.True(hasher.VerifyLegacySha256("legacy password", legacy));
        Assert.False(hasher.VerifyLegacySha256("different password", legacy));
    }

    [Fact]
    public void OpaqueTokensAreRandomAndKeyedAtRest()
    {
        var options = Options();
        var service = new OpaqueTokenService(options);
        var first = service.Create();
        var second = service.Create();
        Assert.NotEqual(first, second);
        Assert.Equal(32, service.Hash(first).Length);
        Assert.NotEqual(service.Hash(first), service.Hash(second));
    }

    [Fact]
    public void ReminderCipherRejectsTamperingAndWrongOwnership()
    {
        var cipher = new ReminderCipher(Options());
        var reminderId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var encrypted = cipher.Encrypt(reminderId, ownerId, "Private reminder text");
        Assert.Equal("Private reminder text", cipher.Decrypt(reminderId, ownerId, encrypted));
        encrypted.Ciphertext[0] ^= 1;
        Assert.Throws<AuthenticationTagMismatchException>(() => cipher.Decrypt(reminderId, ownerId, encrypted));
        Assert.Throws<AuthenticationTagMismatchException>(() => cipher.Decrypt(reminderId, Guid.NewGuid(), cipher.Encrypt(reminderId, ownerId, "Bound")));
    }

    [Fact]
    public void ReminderKeyRotationRewrapsWithoutChangingCiphertext()
    {
        var oldOptions = Options();
        var oldCipher = new ReminderCipher(oldOptions);
        var reminderId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var encrypted = oldCipher.Encrypt(reminderId, ownerId, "Rotating secret");
        var newKey = Convert.ToBase64String(Enumerable.Range(97, 32).Select(x => (byte)x).ToArray());
        var rotatedOptions = new SecurityOptions
        {
            TokenHashingKey = oldOptions.TokenHashingKey,
            LegacyCredentialHashingKey = oldOptions.LegacyCredentialHashingKey,
            ActiveReminderKeyId = "test-v2",
            ReminderWrappingKeys = new() { ["test-v1"] = oldOptions.ReminderWrappingKeys["test-v1"], ["test-v2"] = newKey }
        };
        var rotatedCipher = new ReminderCipher(rotatedOptions);
        var rewrapped = rotatedCipher.Rewrap(reminderId, ownerId, encrypted);
        Assert.Equal(encrypted.Ciphertext, rewrapped.Ciphertext);
        Assert.Equal(encrypted.Nonce, rewrapped.Nonce);
        Assert.Equal(encrypted.Tag, rewrapped.Tag);
        Assert.NotEqual(encrypted.WrappedDataKey, rewrapped.WrappedDataKey);
        Assert.Equal("Rotating secret", rotatedCipher.Decrypt(reminderId, ownerId, rewrapped));
    }

    [Fact]
    public void EmailCipherBindsMessageTemplateAndRejectsTampering()
    {
        var cipher = new EmailCipher(Options());
        var id = Guid.NewGuid();
        var encrypted = cipher.Encrypt(id, "verify_email", new("person@example.invalid", "one-time-token"));
        var payload = cipher.Decrypt(id, "verify_email", encrypted);
        Assert.Equal("person@example.invalid", payload.Recipient);
        Assert.Equal("one-time-token", payload.Token);
        Assert.Throws<AuthenticationTagMismatchException>(() => cipher.Decrypt(id, "reset_password", encrypted));
        encrypted.Tag[0] ^= 1;
        Assert.Throws<AuthenticationTagMismatchException>(() => cipher.Decrypt(id, "verify_email", encrypted));
    }

    private static SecurityOptions Options()
    {
        var token = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var legacy = Convert.ToBase64String(Enumerable.Range(33, 32).Select(x => (byte)x).ToArray());
        var reminder = Convert.ToBase64String(Enumerable.Range(65, 32).Select(x => (byte)x).ToArray());
        var email = Convert.ToBase64String(Enumerable.Range(97, 32).Select(x => (byte)x).ToArray());
        return new()
        {
            TokenHashingKey = token,
            LegacyCredentialHashingKey = legacy,
            ActiveReminderKeyId = "test-v1",
            ReminderWrappingKeys = new() { ["test-v1"] = reminder },
            ActiveEmailKeyId = "email-v1",
            EmailEncryptionKeys = new() { ["email-v1"] = email }
        };
    }
}
