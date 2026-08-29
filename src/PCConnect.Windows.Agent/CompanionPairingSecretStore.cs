using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace PCConnect.Windows.Agent;

public sealed class CompanionPairingSecretStore
{
    private static readonly byte[] Entropy = "PCConnect.Companion.Pairing.v1"u8.ToArray();
    private readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCConnect", "CompanionKeys");
    private readonly object gate = new();

    public byte[] GetOrCreate(string sid)
    {
        lock (gate)
        {
            Directory.CreateDirectory(root);
            RestrictDirectory(root);
            var path = Path.Combine(root, sid + ".bin");
            if (File.Exists(path)) return Unprotect(File.ReadAllBytes(path));
            var secret = RandomNumberGenerator.GetBytes(32);
            var protectedSecret = ProtectedData.Protect(secret, Entropy, DataProtectionScope.LocalMachine);
            try
            {
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, protectedSecret);
                RestrictFile(temporary, new SecurityIdentifier(sid));
                File.Move(temporary, path, false);
                return secret;
            }
            catch { CryptographicOperations.ZeroMemory(secret); throw; }
            finally { CryptographicOperations.ZeroMemory(protectedSecret); }
        }
    }

    private static byte[] Unprotect(byte[] encrypted)
    {
        try { return ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    private static void RestrictDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        AddAdministrativeRules(security, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            FileSystemRights.Traverse | FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void RestrictFile(string path, SecurityIdentifier user)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Read, AccessControlType.Allow));
        foreach (var sid in AdministrativeSids()) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void AddAdministrativeRules(DirectorySecurity security, InheritanceFlags inheritance)
    {
        foreach (var sid in AdministrativeSids()) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
    }

    private static IEnumerable<SecurityIdentifier> AdministrativeSids()
    {
        yield return new(WellKnownSidType.LocalSystemSid, null);
        yield return new(WellKnownSidType.BuiltinAdministratorsSid, null);
        var current = WindowsIdentity.GetCurrent().User;
        if (current is not null) yield return current;
    }
}
