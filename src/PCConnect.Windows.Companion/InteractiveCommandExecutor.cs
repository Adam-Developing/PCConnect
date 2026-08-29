using System.Runtime.InteropServices;

namespace PCConnect.Windows.Companion;

public sealed partial class InteractiveCommandExecutor
{
    private const uint EwXLogoff = 0x00000000;
    private const uint EwXForceIfHung = 0x00000010;

    public static string Execute(string command)
    {
        if (command == "lock") return LockWorkStation() ? "succeeded" : "failed";
        return "unsupported";
    }

    public static bool SignOut() => ExitWindowsEx(EwXLogoff | EwXForceIfHung, 0);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockWorkStation();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ExitWindowsEx(uint flags, uint reason);
}
