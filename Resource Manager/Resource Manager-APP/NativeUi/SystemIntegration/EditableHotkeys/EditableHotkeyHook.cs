using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;

internal sealed class EditableHotkeyHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;

    private readonly Action shortcutPressed;
    private readonly LowLevelKeyboardProc hookProc;
    private readonly LowLevelMouseProc mouseHookProc;
    private readonly EditableHotkeyMatcher matcher;
    private readonly bool usesKeyboardKeys;
    private readonly bool usesMouseButtons;
    private IntPtr keyboardHookHandle;
    private IntPtr mouseHookHandle;
    private bool disposed;

    public EditableHotkeyHook(EditableHotkeyDefinition definition, Action shortcutPressed)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.IsEmpty)
        {
            throw new ArgumentException("快捷键至少需要一个按键。", nameof(definition));
        }
        if (!definition.IsSafeForDestructiveGlobalAction)
        {
            throw new ArgumentException("强制结束快捷键必须同时包含 Ctrl、Alt 或 Win 修饰键以及一个非修饰键。", nameof(definition));
        }

        this.shortcutPressed = shortcutPressed;
        matcher = new EditableHotkeyMatcher(definition);
        usesKeyboardKeys = definition.UsesKeyboardKeys;
        usesMouseButtons = definition.UsesMouseButtons;
        hookProc = KeyboardHookCallback;
        mouseHookProc = MouseHookCallback;
    }

    public void Enable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (keyboardHookHandle != IntPtr.Zero || mouseHookHandle != IntPtr.Zero)
        {
            return;
        }

        var module = ResolveCurrentModuleHandle();
        if (usesKeyboardKeys)
        {
            keyboardHookHandle = SetWindowsHookExKeyboard(WhKeyboardLl, hookProc, module, 0);
            if (keyboardHookHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册自定义全局键盘快捷键钩子。");
            }
        }

        if (usesMouseButtons)
        {
            mouseHookHandle = SetWindowsHookExMouse(WhMouseLl, mouseHookProc, module, 0);
            if (mouseHookHandle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Unhook(ref keyboardHookHandle);
                throw new Win32Exception(error, "无法注册自定义全局鼠标快捷键钩子。");
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Unhook(ref keyboardHookHandle);
        Unhook(ref mouseHookHandle);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            var keyboard = Marshal.PtrToStructure<KeyboardHookStruct>(lParam);
            if (message is WmKeyUp or WmSysKeyUp)
            {
                matcher.HandleKeyUp(keyboard.VirtualKeyCode);
            }
            else if (message is WmKeyDown or WmSysKeyDown
                     && matcher.HandleKeyDown(keyboard.VirtualKeyCode))
            {
                return TriggerShortcut();
            }
        }

        return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            var mouse = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            var virtualKey = ResolveMouseVirtualKey(message, mouse.MouseData);
            if (virtualKey != 0)
            {
                if (message is WmLButtonUp or WmRButtonUp or WmMButtonUp or WmXButtonUp)
                {
                    matcher.HandleKeyUp(virtualKey);
                }
                else if (matcher.HandleKeyDown(virtualKey))
                {
                    return TriggerShortcut();
                }
            }
        }

        return CallNextHookEx(mouseHookHandle, nCode, wParam, lParam);
    }

    internal static int ResolveMouseVirtualKey(int message, uint mouseData)
    {
        return message switch
        {
            WmLButtonDown or WmLButtonUp => 1,
            WmRButtonDown or WmRButtonUp => 2,
            WmMButtonDown or WmMButtonUp => 4,
            WmXButtonDown or WmXButtonUp => ((mouseData >> 16) & 0xffff) switch
            {
                1 => 5,
                2 => 6,
                _ => 0
            },
            _ => 0
        };
    }

    private IntPtr TriggerShortcut()
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

    private static void Unhook(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var current = handle;
        handle = IntPtr.Zero;
        _ = UnhookWindowsHookEx(current);
    }

    private static IntPtr ResolveCurrentModuleHandle()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var moduleName = process.MainModule?.ModuleName;
            return string.IsNullOrWhiteSpace(moduleName) ? IntPtr.Zero : GetModuleHandle(moduleName);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KeyboardHookStruct
    {
        public readonly int VirtualKeyCode;
        public readonly int ScanCode;
        public readonly int Flags;
        public readonly int Time;
        public readonly IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MouseHookStruct
    {
        public readonly int X;
        public readonly int Y;
        public readonly uint MouseData;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExKeyboard(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExMouse(int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string moduleName);
}
