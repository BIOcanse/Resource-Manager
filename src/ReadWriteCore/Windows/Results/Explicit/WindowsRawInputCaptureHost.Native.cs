using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Results.Explicit;

internal sealed partial class WindowsRawInputCaptureHost
{
    private const uint RidInput = 0x10000003;
    private const uint RidHeader = 0x10000005;
    private const uint RidTypeMouse = 0;
    private const uint RidTypeKeyboard = 1;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumNativePacketBytes = 1_048_576;
    private static readonly int HeaderSize = Marshal.SizeOf<NativeRawInputHeader>();

    private NativeRawInputHeader ReadHeader(IntPtr rawInputHandle)
    {
        uint required = 0;
        var probe = GetRawInputData(rawInputHandle, RidHeader, IntPtr.Zero, ref required,
            checked((uint)HeaderSize));
        if (probe != 0 || required != checked((uint)HeaderSize))
            throw NativeFailure(WindowsRawInputCaptureFailureCode.NativeReadFailure,
                "GetRawInputData(RID_HEADER) size query failed.");

        var buffer = Marshal.AllocHGlobal(HeaderSize);
        try
        {
            var capacity = required;
            var copied = GetRawInputData(rawInputHandle, RidHeader, buffer, ref capacity,
                checked((uint)HeaderSize));
            if (copied == uint.MaxValue || copied != required || capacity != required)
                throw NativeFailure(WindowsRawInputCaptureFailureCode.NativeReadFailure,
                    "GetRawInputData(RID_HEADER) fill failed.");
            return Marshal.PtrToStructure<NativeRawInputHeader>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static WindowsRawInputNativeEvent ReadFullPacket(IntPtr rawInputHandle,
        NativeRawInputHeader expectedHeader, uint maximumPacketBytes)
    {
        uint required = 0;
        var probe = GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref required,
            checked((uint)HeaderSize));
        if (probe != 0 || required < checked((uint)HeaderSize))
            throw NativeFailure(WindowsRawInputCaptureFailureCode.NativeReadFailure,
                "GetRawInputData(RID_INPUT) size query failed.");
        if (required > maximumPacketBytes || required > MaximumNativePacketBytes)
            throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.PacketTooLarge,
                "The Raw Input packet exceeded the configured byte bound.");

        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            var capacity = required;
            var copied = GetRawInputData(rawInputHandle, RidInput, buffer, ref capacity,
                checked((uint)HeaderSize));
            if (copied == uint.MaxValue || copied != required || capacity != required)
                throw NativeFailure(WindowsRawInputCaptureFailureCode.NativeReadFailure,
                    "GetRawInputData(RID_INPUT) fill failed.");

            var header = Marshal.PtrToStructure<NativeRawInputHeader>(buffer);
            if (header.Type != expectedHeader.Type || header.Size != expectedHeader.Size
                || header.Device != expectedHeader.Device || header.WParam != expectedHeader.WParam
                || header.Size != copied)
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.InvalidRawPacket,
                    "The RAWINPUT header changed between its header and full-data reads.");

            var handle = WindowsRawInputNativeRuntime.ToPublicHandle(header.Device);
            var messageCode = header.WParam.ToUInt64();
            if (messageCode > uint.MaxValue)
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.InvalidRawPacket,
                    "The RAWINPUT message code did not fit its documented unsigned field.");
            var payload = IntPtr.Add(buffer, HeaderSize);
            if (header.Type == RidTypeMouse)
            {
                var minimum = checked((uint)(HeaderSize + Marshal.SizeOf<NativeRawMouse>()));
                if (header.Size < minimum)
                    throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.InvalidRawPacket,
                        "RAWMOUSE data was shorter than the SDK structure.");
                var mouse = Marshal.PtrToStructure<NativeRawMouse>(payload);
                return new(handle, WindowsRawInputPacketKind.Mouse, header.Size,
                    checked((uint)messageCode), mouse.Flags, mouse.Buttons.Buttons,
                    mouse.Buttons.ButtonFlags, mouse.Buttons.ButtonData, mouse.RawButtons,
                    mouse.LastX, mouse.LastY, mouse.ExtraInformation);
            }

            if (header.Type == RidTypeKeyboard)
            {
                var minimum = checked((uint)(HeaderSize + Marshal.SizeOf<NativeRawKeyboard>()));
                if (header.Size < minimum)
                    throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.InvalidRawPacket,
                        "RAWKEYBOARD data was shorter than the SDK structure.");
                var keyboard = Marshal.PtrToStructure<NativeRawKeyboard>(payload);
                return new(handle, WindowsRawInputPacketKind.Keyboard, header.Size,
                    checked((uint)messageCode), KeyboardMakeCode: keyboard.MakeCode,
                    KeyboardFlags: keyboard.Flags, KeyboardReserved: keyboard.Reserved,
                    VirtualKey: keyboard.VirtualKey, KeyboardMessage: keyboard.Message,
                    KeyboardExtraInformation: keyboard.ExtraInformation);
            }

            return new(handle, WindowsRawInputPacketKind.Other, header.Size, checked((uint)messageCode));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static NativeRegisteredRawInputDevice[] ReadRegisteredDevices()
    {
        var size = checked((uint)Marshal.SizeOf<NativeRegisteredRawInputDevice>());
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint count = 0;
            var probe = GetRegisteredRawInputDevices(IntPtr.Zero, ref count, size);
            if (probe == 0 && count == 0) return [];
            if (probe != uint.MaxValue || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                throw NativeFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                    "GetRegisteredRawInputDevices size query failed.");
            if (count > MaximumRegisteredDevices)
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                    "The process Raw Input registration list exceeded its bound.");
            if (count == 0) return [];

            var buffer = Marshal.AllocHGlobal(checked((int)(count * size)));
            try
            {
                var capacity = count;
                var copied = GetRegisteredRawInputDevices(buffer, ref capacity, size);
                if (copied == uint.MaxValue && Marshal.GetLastWin32Error() == ErrorInsufficientBuffer) continue;
                if (copied == uint.MaxValue)
                    throw NativeFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                        "GetRegisteredRawInputDevices fill failed.");
                if (copied > count || capacity < copied) continue;

                var result = new NativeRegisteredRawInputDevice[checked((int)copied)];
                for (var index = 0; index < result.Length; index++)
                    result[index] = Marshal.PtrToStructure<NativeRegisteredRawInputDevice>(
                        IntPtr.Add(buffer, checked(index * (int)size)));
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.RegistrationFailure,
            "The process Raw Input registration list changed repeatedly during bounded query.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeWindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr TargetWindow; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRegisteredRawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr TargetWindow; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRawInputHeader { public uint Type; public uint Size; public IntPtr Device; public UIntPtr WParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRawMouse
    {
        public ushort Flags;
        public NativeRawMouseButtons Buttons;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeRawMouseButtons
    {
        [FieldOffset(0)] public uint Buttons;
        [FieldOffset(0)] public ushort ButtonFlags;
        [FieldOffset(2)] public ushort ButtonData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", EntryPoint = "GetRegisteredRawInputDevices", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices(IntPtr devices, ref uint deviceCount, uint deviceSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputData", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "RegisterRawInputDevices", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(ref NativeRawInputDevice devices, uint deviceCount, uint deviceSize);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", ExactSpelling = true, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref NativeWindowClassEx windowClass);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "IsWindow", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", ExactSpelling = true, SetLastError = true)]
    private static extern int GetMessageW(out NativeMessage message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)]
    private static extern IntPtr DispatchMessageW(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostQuitMessage", ExactSpelling = true)]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", EntryPoint = "SetTimer", ExactSpelling = true, SetLastError = true)]
    private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr timerId, uint intervalMilliseconds, IntPtr timerCallback);

    [DllImport("user32.dll", EntryPoint = "KillTimer", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr hwnd, UIntPtr timerId);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
