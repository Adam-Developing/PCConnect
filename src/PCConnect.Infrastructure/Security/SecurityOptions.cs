using System.ComponentModel.DataAnnotations;

namespace PCConnect.Infrastructure.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    [Required, MinLength(43)]
    public string TokenHashingKey { get; init; } = string.Empty;

    [Required, MinLength(43)]
    public string LegacyCredentialHashingKey { get; init; } = string.Empty;

    [Required, MinLength(1)]
    public string ActiveReminderKeyId { get; init; } = string.Empty;

    [Required]
    public Dictionary<string, string> ReminderWrappingKeys { get; init; } = [];

    [Required, MinLength(1)]
    public string ActiveEmailKeyId { get; init; } = string.Empty;

    [Required]
    public Dictionary<string, string> EmailEncryptionKeys { get; init; } = [];

    [Required, MinLength(43)]
    public string DeletionTombstoneKey { get; init; } = string.Empty;

    [Required, MinLength(43)]
    public string ExportEncryptionKey { get; init; } = string.Empty;

    public string WebAuthnRpId { get; init; } = "pcconnect.adamdeveloping.co.uk";
    public string WebAuthnRpName { get; init; } = "PCConnect";
    public HashSet<string> WebAuthnOrigins { get; init; } = ["https://pcconnect.adamdeveloping.co.uk"];

    public byte[] DecodeTokenKey() => Decode32(TokenHashingKey, nameof(TokenHashingKey));
    public byte[] DecodeLegacyKey() => Decode32(LegacyCredentialHashingKey, nameof(LegacyCredentialHashingKey));
    public byte[] DecodeDeletionKey() => Decode32(DeletionTombstoneKey, nameof(DeletionTombstoneKey));
    public byte[] DecodeExportKey() => Decode32(ExportEncryptionKey, nameof(ExportEncryptionKey));

    public IReadOnlyDictionary<string, byte[]> DecodeReminderKeys() => ReminderWrappingKeys
        .ToDictionary(x => x.Key, x => Decode32(x.Value, $"ReminderWrappingKeys:{x.Key}"), StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> DecodeEmailKeys() => EmailEncryptionKeys
        .ToDictionary(x => x.Key, x => Decode32(x.Value, $"EmailEncryptionKeys:{x.Key}"), StringComparer.Ordinal);

    private static byte[] Decode32(string encoded, string name)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException exception) { throw new InvalidOperationException($"{name} must be Base64.", exception); }
        if (bytes.Length != 32) throw new InvalidOperationException($"{name} must decode to exactly 32 bytes.");
        return bytes;
    }
}
