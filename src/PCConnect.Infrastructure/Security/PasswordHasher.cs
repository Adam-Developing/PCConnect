using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace PCConnect.Infrastructure.Security;

public interface IPasswordHasher
{
    Task<string> HashAsync(string password, CancellationToken cancellationToken = default);
    Task<bool> VerifyAsync(string password, string encodedHash, CancellationToken cancellationToken = default);
    bool VerifyLegacySha256(string password, string legacyHex);
}

public sealed class Argon2IdPasswordHasher : IPasswordHasher
{
    public const int MemoryKiB = 65_536;
    public const int Iterations = 3;
    public const int Parallelism = 1;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;

    public async Task<string> HashAsync(string password, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = await DeriveAsync(password, salt, MemoryKiB, Iterations, Parallelism, HashBytes, cancellationToken);
        return $"$argon2id$v=19$m={MemoryKiB},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(hash).TrimEnd('=')}";
    }

    public async Task<bool> VerifyAsync(string password, string encodedHash, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        if (!TryParse(encodedHash, out var parsed)) return false;
        var actual = await DeriveAsync(password, parsed.Salt, parsed.MemoryKiB, parsed.Iterations, parsed.Parallelism, parsed.Hash.Length, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(actual, parsed.Hash);
    }

    public bool VerifyLegacySha256(string password, string legacyHex)
    {
        ValidatePassword(password);
        if (legacyHex.Length != 64) return false;
        byte[] expected;
        try { expected = Convert.FromHexString(legacyHex); }
        catch (FormatException) { return false; }
        if (expected.Length != 32) return false;
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static async Task<byte[]> DeriveAsync(string password, byte[] salt, int memory, int iterations, int parallelism, int bytes, CancellationToken cancellationToken)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memory,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };
        cancellationToken.ThrowIfCancellationRequested();
        var result = await argon.GetBytesAsync(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static bool TryParse(string encoded, out ParsedHash parsed)
    {
        parsed = default;
        var parts = encoded.Split('$', StringSplitOptions.None);
        if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19") return false;
        var parameters = parts[3].Split(',').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
        if (!TryInt(parameters, "m", out var memory) || !TryInt(parameters, "t", out var iterations) || !TryInt(parameters, "p", out var parallelism)) return false;
        if (memory < MemoryKiB || iterations < Iterations || parallelism < 1 || memory > 1_048_576 || iterations > 20 || parallelism > 16) return false;
        try
        {
            var salt = DecodeUnpadded(parts[4]);
            var hash = DecodeUnpadded(parts[5]);
            if (salt.Length < SaltBytes || hash.Length < HashBytes) return false;
            parsed = new(memory, iterations, parallelism, salt, hash);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static bool TryInt(Dictionary<string, string> values, string key, out int value)
    {
        value = 0;
        return values.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static byte[] DecodeUnpadded(string value) => Convert.FromBase64String(value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '='));

    private static void ValidatePassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length is < 12 or > 1024) throw new ArgumentException("Password length must be between 12 and 1024 characters.", nameof(password));
    }

    private readonly record struct ParsedHash(int MemoryKiB, int Iterations, int Parallelism, byte[] Salt, byte[] Hash);
}
