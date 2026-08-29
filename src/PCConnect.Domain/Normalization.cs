using System.Globalization;
using System.Text;

namespace PCConnect.Domain;

public static class Normalization
{
    public static string AccountIdentifier(string value) => Required(value).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    public static string DeviceName(string value) => Required(value).Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    private static string Required(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0) throw new ArgumentException("A non-empty value is required.", nameof(value));
        return normalized;
    }
}
