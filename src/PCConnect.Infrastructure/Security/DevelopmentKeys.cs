using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace PCConnect.Infrastructure.Security;

/// <summary>
/// Fills in a signing key and a key encryption key when running in Development,
/// so the system starts with no configuration at all.
///
/// The keys are generated once and kept in a file under the current user's
/// local application data, rather than invented per process. That matters for
/// two reasons the first version got wrong:
///
///  - **Every process must agree.** The worker decrypts the same reminder bodies
///    the API encrypts. A key invented inside the API can never be shared with
///    the worker, so the worker had no fallback at all and refused to start
///    until both keys were set by hand — which meant pasting a multi-line PEM
///    into a shell.
///  - **A restart must not destroy the data.** A per-run key signed everyone out
///    and made yesterday's reminders unreadable on every restart.
///
/// This runs only in Development, and the file it writes is per user and outside
/// the repository, so no key is ever committed and no key ever reaches
/// production. A deployment supplies both through configuration; if it does not,
/// the options validator refuses to start rather than inventing anything.
/// </summary>
public static class DevelopmentKeys
{
    /// <summary>The key id the generated KEK is stored under.</summary>
    public const string DevelopmentKekId = "dev";

    public static void FillMissing(ConfigurationManager configuration, bool isDevelopment)
    {
        if (!isDevelopment)
        {
            return;
        }

        var needsSigningKey = string.IsNullOrWhiteSpace(configuration["Jwt:PrivateKeyPem"]);
        var needsKek = string.IsNullOrWhiteSpace(configuration[$"Kek:Keys:{DevelopmentKekId}"]);

        if (!needsSigningKey && !needsKek)
        {
            return;
        }

        var keys = LoadOrCreate();
        var generated = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (needsSigningKey)
        {
            generated["Jwt:PrivateKeyPem"] = keys.JwtPrivateKeyPem;
        }

        if (needsKek)
        {
            generated[$"Kek:Keys:{DevelopmentKekId}"] = keys.Kek;

            // Only when this process is actually supplying the key. Overriding it
            // unconditionally would point a deployment's configured id at a key
            // that is not there.
            generated["Kek:CurrentKekId"] = DevelopmentKekId;
        }

        configuration.AddInMemoryCollection(generated);

        Console.WriteLine(
            $"Development keys loaded from {Path()}. They are shared by every PCConnect process on " +
            "this machine and survive restarts. Set PCCONNECT_JWT__PRIVATEKEYPEM and " +
            "PCCONNECT_KEK__KEYS__<id> to override them; a deployment must.");
    }

    private static string Path()
    {
        var directory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCConnect");

        return System.IO.Path.Combine(directory, "development-keys.json");
    }

    private static DevelopmentKeyFile LoadOrCreate()
    {
        var path = Path();

        if (File.Exists(path))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<DevelopmentKeyFile>(File.ReadAllText(path));

                if (existing is { JwtPrivateKeyPem.Length: > 0, Kek.Length: > 0 })
                {
                    return existing;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A damaged dev key file is replaced, not fatal. The cost is the
                // same as the cost of the old per-run behaviour: sign in again.
            }
        }

        using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var created = new DevelopmentKeyFile
        {
            JwtPrivateKeyPem = signing.ExportECPrivateKeyPem(),
            Kek = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            // Written exclusively, so two processes starting at once cannot each
            // write a different key and disagree about which is current. The
            // loser reads what the winner wrote.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, created, new JsonSerializerOptions { WriteIndented = true });
            }
        }
        catch (IOException)
        {
            var raced = JsonSerializer.Deserialize<DevelopmentKeyFile>(File.ReadAllText(path));

            if (raced is { JwtPrivateKeyPem.Length: > 0, Kek.Length: > 0 })
            {
                return raced;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Nowhere to persist them. The process still starts; it just goes
            // back to the old behaviour of keys that do not outlive it.
            Console.WriteLine($"WARNING: development keys could not be saved to {path}. " +
                "They will not survive a restart and are not shared with other processes.");
        }

        return created;
    }

    private sealed record DevelopmentKeyFile
    {
        public string JwtPrivateKeyPem { get; init; } = string.Empty;

        public string Kek { get; init; } = string.Empty;
    }
}
