using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal interface IWindowsDiskLayoutNativeCalls
{
    SafeFileHandle Open(string devicePath);
    bool Number(SafeFileHandle handle, byte[] buffer, out uint returned);
    bool Layout(SafeFileHandle handle, byte[] buffer, out uint returned);
    bool AccessAlignment(SafeFileHandle handle, byte[] query, byte[] buffer, out uint returned);
    bool DeviceIdentity(SafeFileHandle handle, byte[] query, byte[] buffer, out uint returned);
    bool Performance(SafeFileHandle handle, byte[] buffer, out uint returned) { returned = 0; return false; }
    bool PerformanceOff(SafeFileHandle handle) => false;
    int LastError();
}

internal sealed partial class WindowsDiskLayoutInformationNativeApi(IWindowsDiskLayoutNativeCalls calls) : IWindowsDiskLayoutInformationApi
{
    internal const int MaximumAttempts = 16;
    internal const uint NumberIoctl = 0x002d1080;
    internal const uint LayoutIoctl = 0x00070050;
    internal const uint PropertyIoctl = 0x002d1400;
    internal const uint DiskPerformanceIoctl = 0x00070020;
    internal const uint DiskPerformanceOffIoctl = 0x00070060;
    internal const uint AccessAlignmentProperty = 6;
    internal const int MaximumAlignmentAttempts = 13;
    internal WindowsDiskLayoutInformationNativeApi() : this(new NativeCalls()) { }
    public IReadOnlyList<WindowsDiskInterface>? Enumerate() => WindowsDeviceTopologyNativeReader.ReadDiskInterfaces();

    public WindowsDiskLayoutCapture? Read(WindowsDiskInterface device) => Read(device, includeTrim: false);
    public WindowsDiskLayoutCapture? Read(WindowsDiskInterface device, bool includeTrim)
    {
        using var handle = calls.Open(device.DevicePath);
        if (handle.IsInvalid || handle.IsClosed) return null;
        var before = Number(handle);
        if (before is null) return null;
        DeviceTopologyDiskLayout? layout;
        DiskAccessAlignmentInformation? alignment;
        try { layout = ReadLayout(handle); }
        catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { layout = null; }
        try { alignment = ReadAlignment(handle); }
        catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { alignment = null; }
        byte[]? descriptor;
        try { descriptor = ReadDeviceDescriptor(handle); }
        catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { descriptor = null; }
        DiskHardwareIdentification? identification;
        try { identification = ReadHardwareIdentification(handle, descriptor); }
        catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { identification = null; }
        bool? trim;
        try { trim = includeTrim ? WindowsStorageTrimQuery.Read((input, capacity) =>
            {
                var output = new byte[capacity];
                return calls.DeviceIdentity(handle, input, output, out var returned) && returned <= output.Length
                    ? output[..(int)returned] : null;
            }) : null; }
        catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { trim = null; }
        var after = Number(handle);
        return after == before ? new(before.Value, layout) { AccessAlignment = alignment, HardwareIdentification = identification,
            BusTypeReported = descriptor is null ? null : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(28)), TrimEnabledReported = trim } : null;
    }

    private uint? Number(SafeFileHandle handle)
    {
        var buffer = new byte[12];
        return calls.Number(handle, buffer, out var returned) && returned <= buffer.Length
            ? WindowsDiskLayoutInformationDecoder.DiskNumber(buffer.AsSpan(0, (int)returned)) : null;
    }

    private ResourceManager.ReadWrite.Windows.Devices.Models.DeviceTopologyDiskLayout? ReadLayout(SafeFileHandle handle)
    {
        var size = WindowsDiskLayoutInformationDecoder.HeaderSize + WindowsDiskLayoutInformationDecoder.PartitionSize;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var buffer = new byte[size];
            var success = calls.Layout(handle, buffer, out var returned);
            if (returned > buffer.Length) return null;
            if (success) return WindowsDiskLayoutInformationDecoder.Layout(buffer.AsSpan(0, (int)returned));
            // Failure does not promise a complete count header. Grow within the fixed partition limit.
            if (calls.LastError() is not (122 or 234) || size == WindowsDiskLayoutInformationDecoder.MaximumBytes) return null;
            size = Math.Min(size * 2, WindowsDiskLayoutInformationDecoder.MaximumBytes);
        }
        return null;
    }

    private DiskAccessAlignmentInformation? ReadAlignment(SafeFileHandle handle)
    {
        var query = WindowsStoragePropertyQuery.Create(AccessAlignmentProperty);
        var size = WindowsDiskAccessAlignmentDecoder.DescriptorSize;
        for (var attempt = 0; attempt < MaximumAlignmentAttempts; attempt++)
        {
            var buffer = new byte[size];
            var success = calls.AccessAlignment(handle, query, buffer, out var returned);
            if (returned > buffer.Length) return null;
            if (success) return WindowsDiskAccessAlignmentDecoder.Decode(buffer.AsSpan(0, (int)returned));
            if (calls.LastError() is not (122 or 234) || size == WindowsDiskAccessAlignmentDecoder.MaximumBytes) return null;
            // A failed property query does not guarantee a descriptor header, even when it filled the buffer.
            size = Math.Min(size * 2, WindowsDiskAccessAlignmentDecoder.MaximumBytes);
        }
        return null;
    }

    private sealed class NativeCalls : IWindowsDiskLayoutNativeCalls
    {
        public SafeFileHandle Open(string devicePath) => CreateFileW(devicePath, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        public bool Number(SafeFileHandle handle, byte[] buffer, out uint returned) => DeviceIoControl(handle, NumberIoctl, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out returned, IntPtr.Zero);
        public bool Layout(SafeFileHandle handle, byte[] buffer, out uint returned) => DeviceIoControl(handle, LayoutIoctl, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out returned, IntPtr.Zero);
        public bool AccessAlignment(SafeFileHandle handle, byte[] query, byte[] buffer, out uint returned)
            => QueryStorageProperty(handle, PropertyIoctl, query, (uint)query.Length, buffer, (uint)buffer.Length, out returned, IntPtr.Zero);
        public bool DeviceIdentity(SafeFileHandle handle, byte[] query, byte[] buffer, out uint returned)
            => QueryStorageProperty(handle, PropertyIoctl, query, (uint)query.Length, buffer, (uint)buffer.Length, out returned, IntPtr.Zero);
        public bool Performance(SafeFileHandle handle, byte[] buffer, out uint returned)
            => DeviceIoControl(handle, DiskPerformanceIoctl, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out returned, IntPtr.Zero);
        public bool PerformanceOff(SafeFileHandle handle)
            => DeviceIoControlNoOutput(handle, DiskPerformanceOffIoctl, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        public int LastError() => Marshal.GetLastPInvokeError();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlNoOutput(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryStorageProperty(SafeFileHandle handle, uint code, [In] byte[] input, uint inputSize,
        [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
