using System.Security.Cryptography;
using System.Security.Principal;
using System.IO;

namespace PCConnect.Windows.Companion;

public static class CompanionPairingSecret
{
    private static readonly byte[] Entropy = "PCConnect.Companion.Pairing.v1"u8.ToArray();

    public static byte[] ReadForCurrentUser()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The current process has no Windows SID.");
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCConnect", "CompanionKeys", sid + ".bin");
        var encrypted = File.ReadAllBytes(path);
        try { return ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }
}
