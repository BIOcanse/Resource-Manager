using ResourceManager.NativeUi.Localization;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResourceManager.NativeUi.SystemIntegration;

internal sealed class TaskManagerShortcutHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkEscape = 0x1B;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    private readonly Action shortcutPressed;
    private readonly LowLevelKeyboardProc hookProc;
    private IntPtr hookHandle;
    private bool disposed;

    public TaskManagerShortcutHook(Action shortcutPressed)
    {
        this.shortcutPressed = shortcutPressed;
        hookProc = HookCallback;
    }

    public bool Enabled => hookHandle != IntPtr.Zero;

    public void Enable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Enabled)
        {
            return;
        }

        hookHandle = SetWindowsHookEx(WhKeyboardLl, hookProc, ResolveCurrentModuleHandle(), 0);
        if (hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), NativeUiText.Current.TaskManagerShortcutHookRegisterFailed);
        }
    }

    public void Disable()
    {
        if (!Enabled)
        {
            return;
        }

        var handle = hookHandle;
        hookHandle = IntPtr.Zero;
        if (!UnhookWindowsHookEx(handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), NativeUiText.Current.TaskManagerShortcutHookRemoveFailed);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (Enabled)
        {
            try
            {
                Disable();
            }
            catch (Win32Exception)
            {
            }
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0
            && (wParam.ToInt32() == WmKeyDown || wParam.ToInt32() == WmSysKeyDown)
            && IsCtrlShiftEscape(lParam))
        {
            try
            {
                shortcutPressed();
            }
            catch
            {
            }

            return new IntPtr(1);
        }

        return CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    private static bool IsCtrlShiftEscape(IntPtr lParam)
    {
        var keyboard = Marshal.PtrToStructure<KeyboardHookStruct>(lParam);
        return keyboard.VirtualKeyCode == VkEscape
            && IsKeyDown(VkControl)
            && IsKeyDown(VkShift)
            && !IsKeyDown(VkMenu);
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;

    private static IntPtr ResolveCurrentModuleHandle()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var moduleName = process.MainModule?.ModuleName;
            return string.IsNullOrWhiteSpace(moduleName)
                ? IntPtr.Zero
                : GetModuleHandle(moduleName);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KeyboardHookStruct
    {
        public readonly int VirtualKeyCode;
        public readonly int ScanCode;
        public readonly int Flags;
        public readonly int Time;
        public readonly IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
