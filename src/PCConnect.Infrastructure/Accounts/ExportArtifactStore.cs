using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Accounts;

public interface IExportArtifactStore
{
    Task WriteAsync(Guid exportId, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken);
    Task<byte[]> ReadAsync(Guid exportId, CancellationToken cancellationToken);
    void Delete(Guid exportId);
}

public sealed class ExportArtifactStore(SecurityOptions security, IConfiguration configuration) : IExportArtifactStore
{
    private static readonly byte[] Magic = "PCEXPT01"u8.ToArray();
    private readonly byte[] key = security.DecodeExportKey();
    private readonly string directory = ResolveDirectory(configuration["Exports:Directory"]);

    public async Task WriteAsync(Guid exportId, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plaintext.Span, ciphertext, tag, AssociatedData(exportId));
        var artifact = new byte[Magic.Length + nonce.Length + tag.Length + ciphertext.Length];
        Magic.CopyTo(artifact, 0);
        nonce.CopyTo(artifact, Magic.Length);
        tag.CopyTo(artifact, Magic.Length + nonce.Length);
        ciphertext.CopyTo(artifact, Magic.Length + nonce.Length + tag.Length);
        var destination = PathFor(exportId);
        var temporary = destination + ".tmp";
        await File.WriteAllBytesAsync(temporary, artifact, cancellationToken);
        File.Move(temporary, destination, true);
        CryptographicOperations.ZeroMemory(artifact);
        CryptographicOperations.ZeroMemory(ciphertext);
    }

    public async Task<byte[]> ReadAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var artifact = await File.ReadAllBytesAsync(PathFor(exportId), cancellationToken);
        try
        {
            if (artifact.Length < 36 || !artifact.AsSpan(0, 8).SequenceEqual(Magic))
                throw new CryptographicException("Export artifact format is invalid.");
            var clear = new byte[artifact.Length - 36];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(artifact.AsSpan(8, 12), artifact.AsSpan(20, 16), artifact.AsSpan(36), clear, AssociatedData(exportId));
            return clear;
        }
        finally { CryptographicOperations.ZeroMemory(artifact); }
    }

    public void Delete(Guid exportId)
    {
        var path = PathFor(exportId);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(Guid exportId) => Path.Combine(directory, $"{exportId:D}.pcexport");
    private static byte[] AssociatedData(Guid exportId) => Encoding.UTF8.GetBytes($"pcconnect.export.v1|{exportId:D}");
    private static string ResolveDirectory(string? configured)
    {
        var path = string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "exports") : configured;
        return Path.GetFullPath(path);
    }
}
