using System.Security.Cryptography;

namespace PCConnect.Api.Configuration;

/// <summary>
/// Generates ephemeral signing and envelope keys for a local development run.
///
/// The alternative — a committed development key — is how a "development only"
/// secret ends up in production, and it is the class of problem this
/// modernisation exists to remove (S1-01). These keys live in memory for the
/// life of the process: restarting invalidates every token issued before it,
/// which is the correct trade for local work and impossible to mistake for a
/// production configuration.
/// </summary>
public static class DevelopmentKeys
{
    public static void FillMissing(WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment())
        {
            return;
        }

        var generated = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(builder.Configuration["Jwt:PrivateKeyPem"]))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            generated["Jwt:PrivateKeyPem"] = key.ExportECPrivateKeyPem();
        }

        if (string.IsNullOrWhiteSpace(builder.Configuration["Kek:Keys:dev"]))
        {
            generated["Kek:Keys:dev"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            generated["Kek:CurrentKekId"] = "dev";
        }

        if (generated.Count == 0)
        {
            return;
        }

        builder.Configuration.AddInMemoryCollection(generated);

        Console.WriteLine(
            "WARNING: development keys were generated in memory for this run. " +
            "Tokens and encrypted reminders will not survive a restart. " +
            "Set PCCONNECT_JWT__PRIVATEKEYPEM and PCCONNECT_KEK__KEYS__<id> for anything persistent.");
    }
}
