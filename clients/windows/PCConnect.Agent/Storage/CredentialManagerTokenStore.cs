using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PCConnect.Client;

namespace PCConnect.Agent.Storage;

/// <summary>
/// The device secret and refresh token live in Windows Credential Manager, not
/// in a JSON file under <c>%APPDATA%</c>.
///
/// v1 kept a password-equivalent hash in <c>My.Settings</c> and in Android
/// SharedPreferences, which is S1-04: malware on the PC could read a credential
/// that never expired and could not be revoked. Credential Manager encrypts at
/// rest under the user or machine account and is the right home for this
/// (06 §2.3).
/// </summary>
public sealed class CredentialManagerTokenStore(ILogger<CredentialManagerTokenStore> logger, string? targetName = null)
    : ITokenStore
{
    public const string DefaultTarget = "PCConnect/Agent";

    private readonly string targetName = targetName ?? DefaultTarget;

    public Task<StoredTokens?> ReadAsync(CancellationToken ct = default)
    {
        if (!NativeCredential.TryRead(targetName, out var blob))
        {
            return Task.FromResult<StoredTokens?>(null);
        }

        try
        {
            return Task.FromResult(JsonSerializer.Deserialize<StoredTokens>(blob));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The stored credential could not be read; treating the agent as unpaired");
            return Task.FromResult<StoredTokens?>(null);
        }
    }

    public Task WriteAsync(StoredTokens tokens, CancellationToken ct = default)
    {
        if (!NativeCredential.Write(targetName, JsonSerializer.Serialize(tokens)))
        {
            throw new InvalidOperationException(
                $"Could not write to Windows Credential Manager (error {Marshal.GetLastWin32Error()}).");
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        NativeCredential.Delete(targetName);
        return Task.CompletedTask;
    }
}

internal static class NativeCredential
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint reservedFlag);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    public static bool TryRead(string target, out string blob)
    {
        blob = string.Empty;

        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            return false;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0)
            {
                return false;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            blob = Encoding.UTF8.GetString(bytes);
            return true;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static bool Write(string target, string blob)
    {
        var bytes = Encoding.UTF8.GetBytes(blob);
        var blobPtr = Marshal.AllocHGlobal(bytes.Length);
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var userPtr = Marshal.StringToHGlobalUni("PCConnect");

        try
        {
            Marshal.Copy(bytes, 0, blobPtr, bytes.Length);

            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blobPtr,
                // LocalMachine: the service runs under a machine account and has
                // to read this without a user profile being loaded.
                Persist = CredPersistLocalMachine,
                UserName = userPtr,
            };

            return CredWrite(ref credential, 0);
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
        }
    }

    public static void Delete(string target) => CredDelete(target, CredTypeGeneric, 0);
}

/// <summary>
/// A file-backed store for development on a machine where Credential Manager is
/// not appropriate — a container, or a test. It is deliberately not the default
/// and says so loudly, because a token in a file is the failure being removed.
/// </summary>
public sealed class FileTokenStore(string path, ILogger logger) : ITokenStore
{
    public async Task<StoredTokens?> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredTokens>(await File.ReadAllTextAsync(path, ct));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Could not read the token file at {Path}", path);
            return null;
        }
    }

    public async Task WriteAsync(StoredTokens tokens, CancellationToken ct = default)
    {
        logger.LogWarning("Writing agent credentials to {Path}. This is for development only.", path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(tokens), ct);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}
