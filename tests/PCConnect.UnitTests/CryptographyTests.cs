using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PCConnect.Infrastructure.Security;
using Shouldly;

namespace PCConnect.UnitTests;

public class EnvelopeEncryptorTests
{
    private static EnvelopeEncryptor Create(params (string Id, string Key)[] keys)
    {
        var dictionary = keys.Length == 0
            ? new Dictionary<string, string> { ["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
            : keys.ToDictionary(k => k.Id, k => k.Key, StringComparer.Ordinal);

        return new EnvelopeEncryptor(Options.Create(new EnvelopeOptions
        {
            Keys = dictionary,
            CurrentKekId = dictionary.Keys.First(),
        }));
    }

    [Fact]
    public void Round_trips_text_including_emoji_and_scripts_outside_the_bmp()
    {
        var encryptor = Create();
        var (wrapped, kekId) = encryptor.CreateDataKey();
        var dek = encryptor.UnwrapDataKey(wrapped, kekId);

        // utf8mb3 could not hold these; that was S2-08. PostgreSQL is UTF-8
        // throughout, and the ciphertext is bytes either way.
        const string plaintext = "Take the bins out 🗑️ · देवनागरी · 日本語";

        var ciphertext = encryptor.Encrypt(dek, plaintext, "aad");
        encryptor.Decrypt(dek, ciphertext, "aad").ShouldBe(plaintext);
    }

    [Fact]
    public void Produces_a_different_ciphertext_every_time()
    {
        var encryptor = Create();
        var (wrapped, kekId) = encryptor.CreateDataKey();
        var dek = encryptor.UnwrapDataKey(wrapped, kekId);

        // A random 96-bit nonce per write. Reuse with one key is the failure
        // mode GCM cannot survive (03 §4.1).
        var first = encryptor.Encrypt(dek, "same", "aad");
        var second = encryptor.Encrypt(dek, "same", "aad");

        first.ShouldNotBe(second);
        first[..12].ShouldNotBe(second[..12]);
    }

    [Fact]
    public void Rejects_a_tampered_ciphertext()
    {
        var encryptor = Create();
        var (wrapped, kekId) = encryptor.CreateDataKey();
        var dek = encryptor.UnwrapDataKey(wrapped, kekId);

        var ciphertext = encryptor.Encrypt(dek, "buy milk", "aad");
        ciphertext[^1] ^= 0xFF;

        // GCM authenticates; CBC did not. This is S1-07: tampering used to
        // produce attacker-influenced plaintext, and now it fails loudly.
        Should.Throw<CryptographicException>(() => encryptor.Decrypt(dek, ciphertext, "aad"));
    }

    [Fact]
    public void Rejects_a_ciphertext_moved_between_users()
    {
        var encryptor = Create();
        var (wrapped, kekId) = encryptor.CreateDataKey();
        var dek = encryptor.UnwrapDataKey(wrapped, kekId);

        var ciphertext = encryptor.Encrypt(dek, "private", "pcconnect:reminder:1");

        // The owner is bound into the associated data, so a row copied into
        // another user's list fails to authenticate rather than decrypting.
        Should.Throw<CryptographicException>(() => encryptor.Decrypt(dek, ciphertext, "pcconnect:reminder:2"));
    }

    [Fact]
    public void Each_user_gets_a_different_data_key()
    {
        var encryptor = Create();

        var (firstWrapped, firstKek) = encryptor.CreateDataKey();
        var (secondWrapped, secondKek) = encryptor.CreateDataKey();

        var first = encryptor.UnwrapDataKey(firstWrapped, firstKek);
        var second = encryptor.UnwrapDataKey(secondWrapped, secondKek);

        first.ShouldNotBe(second);

        // One compromised DEK exposes one user, which is the point of the
        // per-user layer (ADR-0004).
        var ciphertext = encryptor.Encrypt(first, "mine", "aad");
        Should.Throw<CryptographicException>(() => encryptor.Decrypt(second, ciphertext, "aad"));
    }

    [Fact]
    public void Supports_kek_rotation_without_touching_reminder_rows()
    {
        var oldKek = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var newKek = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var before = Create(("k1", oldKek));
        var (wrapped, kekId) = before.CreateDataKey();
        var dek = before.UnwrapDataKey(wrapped, kekId);
        var ciphertext = before.Encrypt(dek, "survives rotation", "aad");

        // Both slots configured: the new key becomes current, the old one stays
        // available to unwrap until every DEK is rewrapped (08 §3).
        var after = new EnvelopeEncryptor(Options.Create(new EnvelopeOptions
        {
            Keys = new Dictionary<string, string> { ["k1"] = oldKek, ["k2"] = newKek },
            CurrentKekId = "k2",
        }));

        after.CurrentKekId.ShouldBe("k2");
        var unwrapped = after.UnwrapDataKey(wrapped, "k1");
        after.Decrypt(unwrapped, ciphertext, "aad").ShouldBe("survives rotation");
    }

    [Fact]
    public void Refuses_to_start_without_a_key()
    {
        var error = Should.Throw<InvalidOperationException>(() =>
            new EnvelopeEncryptor(Options.Create(new EnvelopeOptions())));

        error.Message.ShouldContain("No key encryption key configured");
    }

    [Fact]
    public void Refuses_a_key_of_the_wrong_length()
    {
        var error = Should.Throw<InvalidOperationException>(() => new EnvelopeEncryptor(Options.Create(new EnvelopeOptions
        {
            Keys = new Dictionary<string, string> { ["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)) },
            CurrentKekId = "k1",
        })));

        error.Message.ShouldContain("32 bytes");
    }

    [Fact]
    public void Says_which_key_is_missing_rather_than_failing_obscurely()
    {
        var encryptor = Create(("k1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

        var error = Should.Throw<InvalidOperationException>(() =>
            encryptor.UnwrapDataKey(RandomNumberGenerator.GetBytes(60), "k9"));

        error.Message.ShouldContain("k9");
    }
}

public class LegacyReminderCipherTests
{
    /// <summary>
    /// Reproduces exactly what v1 wrote: AES-256-CBC keyed by the raw bytes of
    /// the API key, stored as base64(iv) + ':' + base64(ciphertext).
    /// </summary>
    private static string EncryptLikeV1(string apiKey, string plaintext, bool doubleBase64 = false)
    {
        var key = new byte[32];
        var raw = Encoding.UTF8.GetBytes(apiKey);
        Array.Copy(raw, key, Math.Min(raw.Length, 32));

        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = encryptor.TransformFinalBlock(bytes, 0, bytes.Length);

        var body = Convert.ToBase64String(cipher);
        if (doubleBase64)
        {
            body = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        }

        return $"{Convert.ToBase64String(aes.IV)}:{body}";
    }

    [Fact]
    public void Decrypts_what_v1_wrote()
    {
        const string apiKey = "0123456789abcdef0123456789abcdef";
        var stored = EncryptLikeV1(apiKey, "Collect the parcel");

        LegacyReminderCipher.TryDecrypt(apiKey, stored, out var plaintext).ShouldBeTrue();
        plaintext.ShouldBe("Collect the parcel");
    }

    [Fact]
    public void Handles_the_speculative_double_base64_branch_v1_carried()
    {
        const string apiKey = "0123456789abcdef0123456789abcdef";
        var stored = EncryptLikeV1(apiKey, "Ring the dentist", doubleBase64: true);

        LegacyReminderCipher.TryDecrypt(apiKey, stored, out var plaintext).ShouldBeTrue();
        plaintext.ShouldBe("Ring the dentist");
    }

    [Fact]
    public void Handles_an_api_key_shorter_than_32_bytes_the_way_v1_did()
    {
        const string apiKey = "short-key";
        var stored = EncryptLikeV1(apiKey, "Pad it like node did");

        LegacyReminderCipher.TryDecrypt(apiKey, stored, out var plaintext).ShouldBeTrue();
        plaintext.ShouldBe("Pad it like node did");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-separator")]
    [InlineData(":")]
    [InlineData("not-base64:also-not-base64")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==:")]
    public void Returns_false_rather_than_throwing_on_unreadable_input(string stored) =>
        LegacyReminderCipher.TryDecrypt("0123456789abcdef0123456789abcdef", stored, out _).ShouldBeFalse();

    [Fact]
    public void Returns_false_for_the_wrong_key()
    {
        var stored = EncryptLikeV1("0123456789abcdef0123456789abcdef", "secret");
        LegacyReminderCipher.TryDecrypt("fedcba9876543210fedcba9876543210", stored, out _).ShouldBeFalse();
    }
}
