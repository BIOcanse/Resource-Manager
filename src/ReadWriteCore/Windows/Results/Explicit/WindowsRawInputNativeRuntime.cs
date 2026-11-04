using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Results.Explicit;

/// <summary>Production Win32 implementation. No native call occurs until a method is explicitly called.</summary>
public sealed class WindowsRawInputNativeRuntime : IWindowsRawInputSessionRuntime
{
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidiDeviceInfo = 0x2000000B;

    public static WindowsRawInputNativeRuntime Instance { get; } = new();
    private WindowsRawInputNativeRuntime() { }

    public ImmutableArray<WindowsRawInputDeviceDescriptor> EnumerateDevices(
        int maximumDevices, int maximumDevicePathCharacters)
    {
        EnsureWindows();
        if (maximumDevices <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDevices));
        if (maximumDevicePathCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDevicePathCharacters));

        var nativeDevices = ReadDeviceList(maximumDevices);
        var result = ImmutableArray.CreateBuilder<WindowsRawInputDeviceDescriptor>();
        foreach (var native in nativeDevices)
        {
            if (native.Type is not (RimTypeMouse or RimTypeKeyboard)) continue;
            var descriptor = QueryDevice(ToPublicHandle(native.Device), maximumDevicePathCharacters);
            if (descriptor.Information.NativeType != native.Type)
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                    "The Raw Input device list changed while device information was queried.");
            result.Add(descriptor);
        }
        return result.ToImmutable();
    }

    public WindowsRawInputDeviceDescriptor QueryDevice(ulong deviceHandle, int maximumDevicePathCharacters)
    {
        EnsureWindows();
        if (deviceHandle == 0) throw new ArgumentOutOfRangeException(nameof(deviceHandle));
        if (maximumDevicePathCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDevicePathCharacters));

        var nativeHandle = ToNativeHandle(deviceHandle);
        var info = ReadDeviceInformation(nativeHandle);
        var kind = info.NativeType switch
        {
            RimTypeMouse => WindowsRawInputDeviceKind.Mouse,
            RimTypeKeyboard => WindowsRawInputDeviceKind.Keyboard,
            _ => throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                "The selected hDevice is not a Raw Input mouse or keyboard.")
        };
        var path = ReadDeviceInterfacePath(nativeHandle, maximumDevicePathCharacters);
        return new(deviceHandle, kind, path, info);
    }

    public long QueryPerformanceFrequency()
    {
        EnsureWindows();
        if (!QueryPerformanceFrequencyNative(out var frequency) || frequency <= 0)
            throw NativeFailure(WindowsRawInputCaptureFailureCode.QpcUnavailable, "QueryPerformanceFrequency failed.");
        return frequency;
    }

    public bool TryQueryPerformanceCounter(out long counter)
    {
        EnsureWindows();
        return QueryPerformanceCounterNative(out counter);
    }

    public IDisposable StartCapture(WindowsRawInputDeviceKind kind, ulong selectedDeviceHandle,
        uint maximumRawInputBytes, Action<WindowsRawInputNativeEvent> onInput,
        Action<WindowsRawInputCaptureFault> onFault)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(onInput);
        ArgumentNullException.ThrowIfNull(onFault);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (selectedDeviceHandle == 0) throw new ArgumentOutOfRangeException(nameof(selectedDeviceHandle));
        if (maximumRawInputBytes is < 40 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(maximumRawInputBytes));
        return WindowsRawInputCaptureHostManager.Add(kind, selectedDeviceHandle, maximumRawInputBytes,
            onInput, onFault);
    }

    private static ImmutableArray<NativeRawInputDeviceListEntry> ReadDeviceList(int maximumDevices)
    {
        var entrySize = checked((uint)Marshal.SizeOf<NativeRawInputDeviceListEntry>());
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint count = 0;
            if (GetRawInputDeviceList(IntPtr.Zero, ref count, entrySize) != 0)
                throw NativeFailure(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                    "GetRawInputDeviceList size query failed.");
            if (count > maximumDevices)
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                    "Raw Input device enumeration exceeded its configured device bound.");
            if (count == 0) return [];

            var buffer = Marshal.AllocHGlobal(checked((int)(count * entrySize)));
            try
            {
                var capacity = count;
                var copied = GetRawInputDeviceList(buffer, ref capacity, entrySize);
                if (copied == uint.MaxValue && Marshal.GetLastWin32Error() == ErrorInsufficientBuffer) continue;
                if (copied == uint.MaxValue)
                    throw NativeFailure(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                        "GetRawInputDeviceList fill failed.");
                if (copied > count || capacity < copied) continue;

                var result = ImmutableArray.CreateBuilder<NativeRawInputDeviceListEntry>(checked((int)copied));
                for (var index = 0; index < copied; index++)
                    result.Add(Marshal.PtrToStructure<NativeRawInputDeviceListEntry>(
                        IntPtr.Add(buffer, checked((int)(index * entrySize)))));
                return result.ToImmutable();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
            "Raw Input devices changed repeatedly during bounded enumeration.");
    }

    private static string ReadDeviceInterfacePath(IntPtr deviceHandle, int maximumCharacters)
    {
        uint characters = 0;
        var probe = GetRawInputDeviceInfoW(deviceHandle, RidiDeviceName, IntPtr.Zero, ref characters);
        if (probe != 0 || characters == 0)
            throw NativeFailure(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                "GetRawInputDeviceInfoW(RIDI_DEVICENAME) size query failed.");
        if (characters > maximumCharacters)
            throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                "The Raw Input device interface path exceeded its configured character bound.");

        var capacity = checked((int)characters);
        var buffer = Marshal.AllocHGlobal(checked(capacity * sizeof(char)));
        try
        {
            for (var index = 0; index < capacity; index++)
                Marshal.WriteInt16(buffer, checked(index * sizeof(char)), 0);
            var fillCharacters = characters;
            var copied = GetRawInputDeviceInfoW(deviceHandle, RidiDeviceName, buffer, ref fillCharacters);
            if (copied == uint.MaxValue || copied > characters || fillCharacters > characters || fillCharacters == 0)
                throw NativeFailure(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                    "GetRawInputDeviceInfoW(RIDI_DEVICENAME) fill failed.");

            var length = 0;
            while (length < capacity && Marshal.ReadInt16(buffer, checked(length * sizeof(char))) != 0) length++;
            if (length == 0 || length == capacity)
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                    "The Raw Input device path was empty or not terminated within its bound.");
            return Marshal.PtrToStringUni(buffer, length)
                ?? throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                    "The Raw Input device path could not be decoded.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static WindowsRawInputDeviceInformation ReadDeviceInformation(IntPtr deviceHandle)
    {
        var size = checked((uint)Marshal.SizeOf<NativeRawDeviceInfo>());
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            Marshal.StructureToPtr(new NativeRawDeviceInfo { Size = size }, buffer, false);
            var supplied = size;
            var copied = GetRawInputDeviceInfoW(deviceHandle, RidiDeviceInfo, buffer, ref supplied);
            if (copied == uint.MaxValue || copied != size || supplied != size)
                throw NativeFailure(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                    "GetRawInputDeviceInfoW(RIDI_DEVICEINFO) query failed.");
            var native = Marshal.PtrToStructure<NativeRawDeviceInfo>(buffer);
            if (native.Size != size)
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                    "RIDI_DEVICEINFO returned an unexpected cbSize.");

            return native.Type switch
            {
                RimTypeMouse => new(native.Type, native.Data.Mouse.Id, native.Data.Mouse.ButtonCount,
                    native.Data.Mouse.SampleRate, native.Data.Mouse.HasHorizontalWheel != 0),
                RimTypeKeyboard => new(native.Type, KeyboardType: native.Data.Keyboard.Type,
                    KeyboardSubtype: native.Data.Keyboard.Subtype, KeyboardMode: native.Data.Keyboard.Mode,
                    KeyboardFunctionKeyCount: native.Data.Keyboard.FunctionKeys,
                    KeyboardIndicatorCount: native.Data.Keyboard.Indicators,
                    KeyboardTotalKeyCount: native.Data.Keyboard.TotalKeys),
                _ => new(native.Type, HidVendorId: native.Data.Hid.VendorId,
                    HidProductId: native.Data.Hid.ProductId, HidVersionNumber: native.Data.Hid.VersionNumber,
                    HidUsagePage: native.Data.Hid.UsagePage, HidUsage: native.Data.Hid.Usage)
            };
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static bool QueryPerformanceCounterNative(out long counter)
        => QueryPerformanceCounter(out counter);

    internal static ulong ToPublicHandle(IntPtr handle)
        => IntPtr.Size == 4
            ? unchecked((uint)handle.ToInt32())
            : unchecked((ulong)handle.ToInt64());

    internal static IntPtr ToNativeHandle(ulong handle)
    {
        if (IntPtr.Size == 4)
        {
            if (handle > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(handle));
            return new IntPtr(unchecked((int)(uint)handle));
        }
        return new IntPtr(unchecked((long)handle));
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Win32 Raw Input is available only on Windows.");
    }

    private static WindowsRawInputCaptureException NativeFailure(
        WindowsRawInputCaptureFailureCode code, string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new(code, $"{operation} Win32 error {error}.", error, new Win32Exception(error));
    }

    private const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRawInputDeviceListEntry { public IntPtr Device; public uint Type; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRawDeviceInfo { public uint Size; public uint Type; public NativeRawDeviceInfoUnion Data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeRawDeviceInfoUnion
    {
        [FieldOffset(0)] public NativeRawMouseDeviceInfo Mouse;
        [FieldOffset(0)] public NativeRawKeyboardDeviceInfo Keyboard;
        [FieldOffset(0)] public NativeRawHidDeviceInfo Hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRawMouseDeviceInfo { public uint Id; public uint ButtonCount; public uint SampleRate; public int HasHorizontalWheel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRawKeyboardDeviceInfo
    {
        public uint Type; public uint Subtype; public uint Mode; public uint FunctionKeys; public uint Indicators; public uint TotalKeys;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRawHidDeviceInfo { public uint VendorId; public uint ProductId; public uint VersionNumber; public ushort UsagePage; public ushort Usage; }

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceList", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetRawInputDeviceList(IntPtr devices, ref uint deviceCount, uint deviceListSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("kernel32.dll", EntryPoint = "QueryPerformanceFrequency", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryPerformanceFrequencyNative(out long frequency);

    [DllImport("kernel32.dll", EntryPoint = "QueryPerformanceCounter", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryPerformanceCounter(out long counter);
}
