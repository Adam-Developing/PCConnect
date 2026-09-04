using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PCConnect.Core.Domain;

/// <summary>
/// Token scopes (03 §2.3). <see cref="CommandIssue"/> and <see cref="CommandReceive"/>
/// are deliberately disjoint: nothing holds both, so a stolen phone token can ask
/// for a shutdown but can never receive or execute one.
/// </summary>
public static class Scopes
{
    public const string ReminderRead = "reminder:read";
    public const string ReminderWrite = "reminder:write";
    public const string DeviceRead = "device:read";
    public const string DeviceManage = "device:manage";
    public const string CommandIssue = "command:issue";
    public const string CommandReceive = "command:receive";
    public const string CommandAck = "command:ack";
    public const string AccountManage = "account:manage";

    /// <summary>What a user session (mobile, web, desktop companion) is granted.</summary>
    public static readonly string[] UserSession =
    [
        ReminderRead, ReminderWrite, DeviceRead, DeviceManage, CommandIssue, AccountManage,
    ];

    /// <summary>
    /// What a paired agent is granted. It cannot read a reminder, cannot rename a
    /// device, cannot change the password, and cannot issue a command — including
    /// to itself.
    /// </summary>
    public static readonly string[] DeviceSession = [CommandReceive, CommandAck];

    /// <summary>
    /// What the legacy compatibility shim mints. The installed clients both issue
    /// commands and poll for them from the same credential, which is the weak
    /// behaviour being carried forward deliberately, confined here, logged, and
    /// dying with the shim (ADR-0008).
    /// </summary>
    public static readonly string[] LegacyCompat =
    [
        ReminderRead, ReminderWrite, DeviceRead, CommandIssue, CommandReceive, CommandAck,
    ];
}

public static class ClientKinds
{
    public const string DesktopAgent = "desktop_agent";
    public const string Mobile = "mobile";
    public const string Web = "web";
    public const string Legacy = "legacy";

    public static bool IsValid(string kind) =>
        kind is DesktopAgent or Mobile or Web or Legacy;
}

public static class PasswordAlgorithms
{
    public const string Argon2id = "argon2id";
    public const string LegacySha256Unsalted = "legacy_sha256_unsalted";
}

public static class UserStatuses
{
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string PendingVerification = "pending_verification";
}

/// <summary>
/// One password policy, called by registration, change and reset alike. S1-10 was
/// exactly the absence of this: the rule existed on signup and nowhere else.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;
    public const int MaximumLength = 256;

    public static void Validate(string? password, params string[] personalData)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw Fail("A password is required.");
        }

        if (password.Length < MinimumLength)
        {
            throw Fail($"Password must be at least {MinimumLength} characters.");
        }

        if (password.Length > MaximumLength)
        {
            throw Fail($"Password must be at most {MaximumLength} characters.");
        }

        // A password that contains the account's own identifiers survives every
        // length rule and none of the guessing attacks that matter.
        foreach (var value in personalData)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                value.Length >= 4 &&
                password.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                throw Fail("Password must not contain your username or email address.");
            }
        }

        if (IsSingleRepeatedCharacter(password))
        {
            throw Fail("Password must not be a single repeated character.");
        }
    }

    /// <summary>
    /// The k-anonymity prefix for the Pwned Passwords range API: the first five
    /// hex characters of SHA-1(password). Only the prefix ever leaves the server;
    /// the full digest never does.
    /// </summary>
    public static (string Prefix, string Suffix) PwnedRange(string password)
    {
        var digest = SHA1.HashData(Encoding.UTF8.GetBytes(password));
        var hex = Convert.ToHexString(digest);
        return (hex[..5], hex[5..]);
    }

    private static bool IsSingleRepeatedCharacter(string password)
    {
        for (var i = 1; i < password.Length; i++)
        {
            if (password[i] != password[0])
            {
                return false;
            }
        }

        return true;
    }

    private static AppException Fail(string message) =>
        new(ErrorCodes.AuthPasswordPolicy, message, HttpStatusCode.UnprocessableEntity,
            [new ErrorDetail("password", "policy")]);
}

/// <summary>
/// Pairing codes are read aloud and typed by a human, so the alphabet excludes
/// the characters people confuse (0/O, 1/I/L, 2/Z, 5/S, 8/B). 8 characters over
/// a 26-symbol alphabet is ~37.6 bits, which is only safe because claims are
/// rate-limited and attempt-counted (03 §2.6).
/// </summary>
public static class PairingCode
{
    public const string Alphabet = "ACDEFGHJKMNPQRTUVWXY34679";
    public const int Length = 8;

    public static string Generate()
    {
        Span<char> buffer = stackalloc char[Length + 1];
        var index = 0;
        for (var i = 0; i < Length; i++)
        {
            if (i == Length / 2)
            {
                buffer[index++] = '-';
            }

            buffer[index++] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(buffer[..index]);
    }

    /// <summary>
    /// Accepts what a person actually types: lower case, missing or extra
    /// hyphens, surrounding whitespace.
    /// </summary>
    public static string Normalise(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        var chars = new List<char>(Length);
        foreach (var c in code.ToUpperInvariant())
        {
            if (Alphabet.Contains(c, StringComparison.Ordinal))
            {
                chars.Add(c);
            }
        }

        if (chars.Count != Length)
        {
            return string.Empty;
        }

        return string.Concat(
            new string(chars.ToArray()[..(Length / 2)]),
            "-",
            new string(chars.ToArray()[(Length / 2)..]));
    }
}

/// <summary>Normalisation rules shared by registration, login and the importer.</summary>
public static class Normalise
{
    public static string Email(string email) =>
        email.Trim().ToLowerInvariant();

    public static string Username(string username) =>
        username.Trim().ToLowerInvariant();

    public static bool LooksLikeEmail(string value) =>
        value.Contains('@', StringComparison.Ordinal) &&
        value.IndexOf('@', StringComparison.Ordinal) > 0 &&
        value.LastIndexOf('.') > value.IndexOf('@', StringComparison.Ordinal);

    /// <summary>
    /// True when the value is a lowercase 64-character hex string: the shape of
    /// the unsalted SHA-256 the legacy clients send instead of a password.
    /// </summary>
    public static bool LooksLikeLegacyHash(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    public static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string IanaTimeZoneOrDefault(string? timezone, string fallback = "Europe/London")
    {
        if (string.IsNullOrWhiteSpace(timezone))
        {
            return fallback;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            return timezone;
        }
        catch (TimeZoneNotFoundException)
        {
            return fallback;
        }
        catch (InvalidTimeZoneException)
        {
            return fallback;
        }
    }

    public static string FormatUtc(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
