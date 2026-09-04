using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace PCConnect.Infrastructure.Security;

public sealed class Argon2Options
{
    /// <summary>OWASP 2024 baseline: 19 MiB, t=2, p=1 (03 §2.5).</summary>
    public int MemoryKib { get; set; } = 19456;

    public int TimeCost { get; set; } = 2;

    public int Parallelism { get; set; } = 1;

    public int SaltBytes { get; set; } = 16;

    public int HashBytes { get; set; } = 32;

    /// <summary>
    /// The floor below which the process refuses to start. Tuning down to make a
    /// small VM feel faster is exactly how a password hash stops being one.
    /// </summary>
    public void Validate()
    {
        if (MemoryKib < 19456)
        {
            throw new InvalidOperationException(
                $"ARGON2_MEMORY_KIB={MemoryKib} is below the OWASP floor of 19456 KiB.");
        }

        if (TimeCost < 2)
        {
            throw new InvalidOperationException($"ARGON2_TIME_COST={TimeCost} is below the floor of 2.");
        }

        if (Parallelism < 1)
        {
            throw new InvalidOperationException($"ARGON2_PARALLELISM={Parallelism} must be at least 1.");
        }
    }
}

/// <summary>
/// Argon2id, encoded as a PHC string so the parameters travel with the hash and
/// can be raised later without a flag day (ADR-0002).
/// </summary>
public sealed class Argon2PasswordHasher(IOptions<Argon2Options> options) : Core.IPasswordHasher
{
    private const string Prefix = "$argon2id$";
    private readonly Argon2Options _options = options.Value;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(_options.SaltBytes);
        var hash = Derive(password, salt, _options.MemoryKib, _options.TimeCost, _options.Parallelism, _options.HashBytes);

        return string.Create(CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={_options.MemoryKib},t={_options.TimeCost},p={_options.Parallelism}${B64(salt)}${B64(hash)}");
    }

    public bool Verify(string password, string phcString)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(phcString))
        {
            return false;
        }

        if (!TryParse(phcString, out var p))
        {
            return false;
        }

        var computed = Derive(password, p.Salt, p.MemoryKib, p.TimeCost, p.Parallelism, p.Hash.Length);
        return CryptographicOperations.FixedTimeEquals(computed, p.Hash);
    }

    public bool NeedsRehash(string phcString)
    {
        if (!TryParse(phcString, out var p))
        {
            return true;
        }

        return p.MemoryKib < _options.MemoryKib
            || p.TimeCost < _options.TimeCost
            || p.Parallelism != _options.Parallelism;
    }

    public static bool IsArgon2(string? hash) =>
        hash is not null && hash.StartsWith(Prefix, StringComparison.Ordinal);

    private static byte[] Derive(string password, byte[] salt, int memoryKib, int timeCost, int parallelism, int outputBytes)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = timeCost,
            DegreeOfParallelism = parallelism,
        };

        return argon.GetBytes(outputBytes);
    }

    private static string B64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=');

    private static byte[] FromB64(string value)
    {
        var padded = (value.Length % 4) switch
        {
            2 => value + "==",
            3 => value + "=",
            _ => value,
        };

        return Convert.FromBase64String(padded);
    }

    private readonly record struct Parsed(int MemoryKib, int TimeCost, int Parallelism, byte[] Salt, byte[] Hash);

    private static bool TryParse(string phc, out Parsed parsed)
    {
        parsed = default;

        // $argon2id$v=19$m=19456,t=2,p=1$<salt>$<hash>
        var parts = phc.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || !string.Equals(parts[0], "argon2id", StringComparison.Ordinal))
        {
            return false;
        }

        int memory = 0, time = 0, parallel = 0;
        foreach (var kv in parts[2].Split(','))
        {
            var pair = kv.Split('=', 2);
            if (pair.Length != 2 || !int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            switch (pair[0])
            {
                case "m": memory = value; break;
                case "t": time = value; break;
                case "p": parallel = value; break;
                default: return false;
            }
        }

        if (memory <= 0 || time <= 0 || parallel <= 0)
        {
            return false;
        }

        try
        {
            parsed = new Parsed(memory, time, parallel, FromB64(parts[3]), FromB64(parts[4]));
            return parsed.Salt.Length > 0 && parsed.Hash.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
