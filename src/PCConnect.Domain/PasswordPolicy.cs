namespace PCConnect.Domain;

public static class PasswordPolicy
{
    public const int MinimumLength = 12;
    public const int MaximumLength = 1024;

    public static void ValidateNew(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length is < MinimumLength or > MaximumLength)
            throw new ArgumentException($"Password must be {MinimumLength}-{MaximumLength} characters.", nameof(password));
        if (password.Any(char.IsControl))
            throw new ArgumentException("Password cannot contain control characters.", nameof(password));
    }

    public static void ValidatePresented(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length is < 1 or > MaximumLength)
            throw new ArgumentException("Password has an invalid length.", nameof(password));
    }
}
