Imports System.Runtime.InteropServices
Imports System.Reflection

Public Class KeyboardBlocker
    Private Const WH_KEYBOARD_LL As Integer = 13
    Private Const WM_KEYDOWN As Integer = &H100
    Private Const WM_KEYUP As Integer = &H101

    Private Delegate Function LowLevelKeyboardProc(ByVal nCode As Integer, ByVal wParam As IntPtr, ByVal lParam As IntPtr) As IntPtr

    Private Shared hookID As IntPtr = IntPtr.Zero
    Private Shared keyboardProc As LowLevelKeyboardProc = AddressOf HookCallback

    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function SetWindowsHookEx(ByVal idHook As Integer, ByVal lpfn As LowLevelKeyboardProc, ByVal hMod As IntPtr, ByVal dwThreadId As UInteger) As IntPtr
    End Function

    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function UnhookWindowsHookEx(ByVal hhk As IntPtr) As Boolean
    End Function

    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function CallNextHookEx(ByVal hhk As IntPtr, ByVal nCode As Integer, ByVal wParam As IntPtr, ByVal lParam As IntPtr) As IntPtr
    End Function

    Private Shared Function HookCallback(ByVal nCode As Integer, ByVal wParam As IntPtr, ByVal lParam As IntPtr) As IntPtr
        If nCode >= 0 AndAlso (wParam = CType(WM_KEYDOWN, IntPtr) OrElse wParam = CType(WM_KEYUP, IntPtr)) Then
            ' Block the key input by returning a non-zero value.
            Return CType(1, IntPtr)
        End If

        ' Allow other keyboard events to pass through.
        Return CallNextHookEx(hookID, nCode, wParam, lParam)
    End Function

    Public Shared Sub BlockKeyboard()
        hookID = SetHook(keyboardProc)
    End Sub

    Public Shared Sub UnblockKeyboard()
        UnhookWindowsHookEx(hookID)
    End Sub

    Private Shared Function SetHook(ByVal proc As LowLevelKeyboardProc) As IntPtr
        Dim moduleHandle As IntPtr = Marshal.GetHINSTANCE(System.Reflection.Assembly.GetExecutingAssembly.GetModules()(0))
        Return SetWindowsHookEx(WH_KEYBOARD_LL, proc, moduleHandle, 0)
    End Function
End Class
