using System.ComponentModel;
using System.Runtime.InteropServices;
using PCConnect.Contracts.V2;

namespace PCConnect.Windows.Agent;

public sealed record LocalExecutionResult(bool Succeeded, CommandFailureCode? FailureCode = null);

public interface IFixedCommandExecutor
{
    bool Supports(CommandType type);
    Task<LocalExecutionResult> ExecuteAsync(CommandType type, CancellationToken cancellationToken);
}

public sealed partial class WindowsFixedCommandExecutor : IFixedCommandExecutor
{
    public bool Supports(CommandType type) => type is CommandType.Sleep or CommandType.Hibernate or CommandType.Restart or CommandType.Shutdown;

    public Task<LocalExecutionResult> ExecuteAsync(CommandType type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Supports(type)) return Task.FromResult(new LocalExecutionResult(false, CommandFailureCode.Unsupported));
        try
        {
            var succeeded = type switch
            {
                CommandType.Sleep => SetSuspendState(false, false, false),
                CommandType.Hibernate => SetSuspendState(true, false, false),
                CommandType.Restart => InitiateSystemShutdownEx(null, "PCConnect authenticated restart", 0, true, true, 0x80040002),
                CommandType.Shutdown => InitiateSystemShutdownEx(null, "PCConnect authenticated shutdown", 0, true, false, 0x80040002),
                _ => false
            };
            return Task.FromResult(succeeded
                ? new LocalExecutionResult(true)
                : new LocalExecutionResult(false, Marshal.GetLastWin32Error() == 5 ? CommandFailureCode.PermissionDenied : CommandFailureCode.ExecutionFailed));
        }
        catch (Win32Exception)
        {
            return Task.FromResult(new LocalExecutionResult(false, CommandFailureCode.ExecutionFailed));
        }
    }

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate, [MarshalAs(UnmanagedType.Bool)] bool forceCritical, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    [LibraryImport("advapi32.dll", EntryPoint = "InitiateSystemShutdownExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitiateSystemShutdownEx(string? machineName, string? message, uint timeout, [MarshalAs(UnmanagedType.Bool)] bool forceAppsClosed, [MarshalAs(UnmanagedType.Bool)] bool rebootAfterShutdown, uint reason);
}
