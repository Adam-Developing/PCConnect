using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PCConnect.Core.Domain;

namespace PCConnect.Agent.Execution;

public enum ExecutionOutcome
{
    Succeeded,
    Failed,
    Rejected,
}

public sealed record ExecutionResult(ExecutionOutcome Outcome, string ResultCode, string ResultMessage)
{
    public static ExecutionResult Ok(string message = "executed") => new(ExecutionOutcome.Succeeded, "0", message);

    public static ExecutionResult Reject(string code, string message) => new(ExecutionOutcome.Rejected, code, message);

    public static ExecutionResult Fail(string code, string message) => new(ExecutionOutcome.Failed, code, message);
}

/// <summary>
/// The agent's own allow-list: the last line of defence, and the one that does
/// not trust the server (03 §3, checks 6-8).
///
/// Three properties matter here and are asserted by the strictest tests in the
/// codebase:
///
///   * <b>No shell, ever.</b> Each command type maps to a fixed Win32 call. The
///     v1 design shelled out to <c>shutdown.exe</c> with an interpolated string;
///     this design never constructs a command line at all, so there is no path
///     from an HTTP body to a process.
///   * <b>Reject by default.</b> An unrecognised type is refused and
///     acknowledged as <c>rejected</c>; it is never guessed at.
///   * <b>Freshness and replay.</b> A command past its TTL is refused even if
///     the server sent it, and a command id already executed is dropped.
/// </summary>
public sealed class CommandExecutor(ILogger<CommandExecutor> logger, ISessionCommandBridge sessionBridge)
{
    private const int ReplayGuardCapacity = 256;

    private readonly Lock _replayLock = new();
    private readonly LinkedList<string> _replayOrder = [];
    private readonly HashSet<string> _executed = new(StringComparer.Ordinal);

    /// <summary>
    /// Set from <see cref="PCConnect.Client.PcConnectRealtimeClient.ServerClockOffset"/>.
    /// Freshness is judged against server-anchored time, never the local wall
    /// clock: an agent whose clock is an hour fast would otherwise treat every
    /// command as expired (05 §8).
    /// </summary>
    public TimeSpan ServerClockOffset { get; set; }

    public DateTimeOffset ServerNow => DateTimeOffset.UtcNow + ServerClockOffset;

    public async Task<ExecutionResult> ExecuteAsync(
        string commandId, string commandType, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        // Check 8 — replay guard. A duplicate from a push racing a poll must not
        // shut the machine down twice.
        if (!TryClaim(commandId))
        {
            logger.LogInformation("Command {CommandId} was already executed; dropping the duplicate", commandId);
            return ExecutionResult.Reject("duplicate", "This command was already executed on this device.");
        }

        // Check 7 — freshness.
        if (ServerNow > expiresAt)
        {
            logger.LogWarning("Command {CommandId} ({Type}) arrived after its TTL and was refused", commandId, commandType);
            return ExecutionResult.Reject("expired", "The command's time-to-live had already passed.");
        }

        // Check 6 — the allow-list. Normalisation is deliberate and total: it
        // maps a small set of known aliases and rejects everything else. Unicode
        // case-folding tricks, shell metacharacters, path separators and null
        // bytes all land in the default branch.
        if (!CommandTypes.TryNormalise(commandType, out var normalised))
        {
            logger.LogWarning("Refusing unknown command type {Type}", commandType);
            return ExecutionResult.Reject("unsupported", $"'{commandType}' is not a command this agent will run.");
        }

        try
        {
            return normalised switch
            {
                CommandTypes.Shutdown => Power.Shutdown(logger),
                CommandTypes.Restart => Power.Restart(logger),
                CommandTypes.Sleep => Power.Suspend(hibernate: false, logger),
                CommandTypes.Hibernate => Power.Suspend(hibernate: true, logger),

                // Locking and signing out act on an interactive session, which a
                // Windows service does not have. They are handed to the
                // companion running in the user's own session (ADR-0012).
                CommandTypes.Lock => await sessionBridge.LockAsync(ct),
                CommandTypes.SignOut => await sessionBridge.SignOutAsync(ct),

                _ => ExecutionResult.Reject("unsupported", "Unrecognised command."),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException or TimeoutException)
        {
            logger.LogError(ex, "Command {CommandId} ({Type}) failed", commandId, normalised);
            return ExecutionResult.Fail("execution_failed", ex.Message);
        }
    }

    /// <summary>
    /// Bounded LRU of executed ids. Bounded because an agent that runs for
    /// months must not accumulate every command id it has ever seen.
    /// </summary>
    private bool TryClaim(string commandId)
    {
        lock (_replayLock)
        {
            if (!_executed.Add(commandId))
            {
                return false;
            }

            _replayOrder.AddLast(commandId);

            while (_replayOrder.Count > ReplayGuardCapacity)
            {
                var oldest = _replayOrder.First!.Value;
                _replayOrder.RemoveFirst();
                _executed.Remove(oldest);
            }

            return true;
        }
    }

    /// <summary>Exposed for the executor tests.</summary>
    internal bool HasExecuted(string commandId)
    {
        lock (_replayLock)
        {
            return _executed.Contains(commandId);
        }
    }
}

/// <summary>
/// Power state changes, through Win32 rather than through a process.
/// </summary>
internal static class Power
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const string SeShutdownName = "SeShutdownPrivilege";

    // Reason codes recorded in the Windows event log, so an administrator can
    // see that PCConnect asked for the shutdown and not guess at it.
    private const uint ShutdownReasonMajorApplication = 0x00040000;
    private const uint ShutdownReasonMinorMaintenance = 0x00000001;
    private const uint ShutdownReasonFlagPlanned = 0x80000000;

    private const uint ShutdownReason =
        ShutdownReasonMajorApplication | ShutdownReasonMinorMaintenance | ShutdownReasonFlagPlanned;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitiateSystemShutdownExW(
        string? machineName, string? message, uint timeout,
        [MarshalAs(UnmanagedType.Bool)] bool forceAppsClosed,
        [MarshalAs(UnmanagedType.Bool)] bool rebootAfterShutdown,
        uint reason);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    public static ExecutionResult Shutdown(ILogger logger) => Halt(logger, reboot: false);

    public static ExecutionResult Restart(ILogger logger) => Halt(logger, reboot: true);

    private static ExecutionResult Halt(ILogger logger, bool reboot)
    {
        if (!EnableShutdownPrivilege(logger))
        {
            return ExecutionResult.Fail("privilege", "The agent does not hold the shutdown privilege.");
        }

        // A short grace period, not zero: the user asked from their phone and
        // may be sitting at the machine. Forcing apps closed is deliberate —
        // a shutdown that hangs on an unsaved document is a shutdown that did
        // not happen, and the phone would report success.
        var ok = InitiateSystemShutdownExW(null, "PCConnect: shutting down at your request.", 10,
            forceAppsClosed: true, rebootAfterShutdown: reboot, ShutdownReason);

        if (!ok)
        {
            var error = Marshal.GetLastWin32Error();
            logger.LogError("InitiateSystemShutdownEx failed with Win32 error {Error}", error);
            return ExecutionResult.Fail("win32_" + error, "Windows refused the shutdown request.");
        }

        return ExecutionResult.Ok(reboot ? "restarting" : "shutting down");
    }

    public static ExecutionResult Suspend(bool hibernate, ILogger logger)
    {
        if (!SetSuspendState(hibernate, forceCritical: false, disableWakeEvent: false))
        {
            var error = Marshal.GetLastWin32Error();
            logger.LogError("SetSuspendState failed with Win32 error {Error}", error);
            return ExecutionResult.Fail("win32_" + error, "Windows refused the suspend request.");
        }

        return ExecutionResult.Ok(hibernate ? "hibernating" : "sleeping");
    }

    private static bool EnableShutdownPrivilege(ILogger logger)
    {
        if (!OpenProcessToken(Environment.ProcessId >= 0 ? GetCurrentProcess() : IntPtr.Zero,
                TokenAdjustPrivileges | TokenQuery, out var token))
        {
            logger.LogError("OpenProcessToken failed with {Error}", Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            if (!LookupPrivilegeValueW(null, SeShutdownName, out var luid))
            {
                logger.LogError("LookupPrivilegeValue failed with {Error}", Marshal.GetLastWin32Error());
                return false;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };

            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                logger.LogError("AdjustTokenPrivileges failed with {Error}", Marshal.GetLastWin32Error());
                return false;
            }

            // AdjustTokenPrivileges reports success even when it changed nothing.
            var last = Marshal.GetLastWin32Error();
            if (last != 0)
            {
                logger.LogError("The shutdown privilege was not assigned (error {Error})", last);
                return false;
            }

            return true;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
