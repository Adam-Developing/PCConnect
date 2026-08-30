using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace PCConnect.Windows.Agent;

public sealed record StoredDeviceCredential(Guid DeviceId, string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt);

public interface IDeviceCredentialStore
{
    Task<StoredDeviceCredential?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(StoredDeviceCredential credential, CancellationToken cancellationToken);
    Task DeleteAsync(CancellationToken cancellationToken);
}

public sealed class DpapiDeviceCredentialStore : IDeviceCredentialStore
{
    private static readonly byte[] Entropy = "PCConnect.Windows.Agent.v2"u8.ToArray();
    private readonly string path;

    public DpapiDeviceCredentialStore(IConfiguration configuration)
    {
        var configured = configuration["PCConnect:CredentialPath"];
        path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCConnect", "agent-credential.bin")
            : configured;
    }

    public async Task<StoredDeviceCredential?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
        try
        {
            var clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine);
            try { return JsonSerializer.Deserialize<StoredDeviceCredential>(clear, JsonOptions) ?? throw new CryptographicException("Stored credential is invalid."); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    public async Task SaveAsync(StoredDeviceCredential credential, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);
        var clear = JsonSerializer.SerializeToUtf8Bytes(credential, JsonOptions);
        try
        {
            var encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.LocalMachine);
            try
            {
                var temporary = path + ".tmp";
                await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken);
                File.Move(temporary, path, true);
                RestrictFile(path);
            }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private static void RestrictDirectory(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        AddCurrentIdentity(security, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void RestrictFile(string file)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        var current = WindowsIdentity.GetCurrent().User;
        if (current is not null) security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }

    private static void AddCurrentIdentity(DirectorySecurity security, InheritanceFlags inheritanceFlags)
    {
        var current = WindowsIdentity.GetCurrent().User;
        if (current is not null) security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, inheritanceFlags, PropagationFlags.None, AccessControlType.Allow));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
