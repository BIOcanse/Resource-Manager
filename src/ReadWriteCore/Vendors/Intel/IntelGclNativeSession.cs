using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Vendors.Intel;

internal sealed class IntelGclNativeSession : IIntelGclSession
{
    internal const int MaximumDeviceCount = 256;
    private static readonly object libraryGate = new();
    private static IntelGclNativeFunctions? processFunctions;
    private readonly IntelGclNativeFunctions functions;
    private IntPtr api;

    private IntelGclNativeSession(IntelGclNativeFunctions functions, IntPtr api)
        => (this.functions, this.api) = (functions, api);

    internal static IntelGclNativeSession? Create()
        => Create(1 /* Level Zero telemetry */, 1U << 16);

    internal static IntelGclNativeSession? Create(uint flags, uint appVersion)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return null;
        IntelGclNativeFunctions? functions;
        lock (libraryGate)
        {
            functions = processFunctions ??= LoadFunctions();
        }
        // Like a CLR P/Invoke module, one process reference keeps SDK callbacks mapped.
        // ctlClose may report another caller still open; it does not permit DLL unload then.
        // API sessions are owned by this sample and closed at its end, including failures.
        return functions is null ? null : Open(functions, flags, appVersion);
    }

    private static IntelGclNativeFunctions? LoadFunctions()
    {
        // Match Intel's release loader: no working-directory or user-supplied DLL search.
        var library = LoadLibraryExW("ControlLib.dll", IntPtr.Zero, 0x00000800 /* SYSTEM32 */);
        if (library == IntPtr.Zero) return null;
        IntelGclNativeFunctions functions;
        try
        {
            T Export<T>(string name) where T : Delegate
                => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            T? Optional<T>(string name) where T : Delegate
                => NativeLibrary.TryGetExport(library, name, out var entry)
                    ? Marshal.GetDelegateForFunctionPointer<T>(entry) : null;
            var pciState = NativeLibrary.TryGetExport(library, "ctlPciGetState", out var pciEntry)
                ? Marshal.GetDelegateForFunctionPointer<IntelGclPciStateGet>(pciEntry) : null;
            var pciProperties = NativeLibrary.TryGetExport(library, "ctlPciGetProperties", out var pciPropertiesEntry)
                ? Marshal.GetDelegateForFunctionPointer<IntelGclPciPropertiesGet>(pciPropertiesEntry) : null;
            var fans = NativeLibrary.TryGetExport(library, "ctlEnumFans", out var fanEntry)
                ? Marshal.GetDelegateForFunctionPointer<IntelGclEnumerate>(fanEntry) : null;
            var fanState = NativeLibrary.TryGetExport(library, "ctlFanGetState", out var fanStateEntry)
                ? Marshal.GetDelegateForFunctionPointer<IntelGclFanStateGet>(fanStateEntry) : null;
            functions = new IntelGclNativeFunctions(Export<IntelGclInit>("ctlInit"), Export<IntelGclClose>("ctlClose"),
                Export<IntelGclEnumerate>("ctlEnumerateDevices"), Export<IntelGclProperties>("ctlGetDeviceProperties"),
                Export<IntelGclTelemetry>("ctlPowerTelemetryGet"), pciState, pciProperties, fans, fanState,
                Optional<IntelGclOverclockWaiver>("ctlOverclockWaiverSet"),
                Optional<IntelGclFrequencyOffsetGet>("ctlOverclockGpuFrequencyOffsetGet"),
                Optional<IntelGclFrequencyOffsetSet>("ctlOverclockGpuFrequencyOffsetSet"));
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
        return functions;
    }

    // The same native boundary is used by pure managed SDK substitutes.
    internal static IntelGclNativeSession? Open(IntelGclNativeFunctions functions, uint flags = 1, uint appVersion = 1U << 16)
    {
        var args = new IntelGclInitArgs { Size = 36, AppVersion = appVersion, Flags = flags };
        return functions.Init(ref args, out var api) == 0 && api != IntPtr.Zero ? new(functions, api) : null;
    }

    internal unsafe IntelGraphicsControlBridge.DeviceIdentity? ReadControlIdentity(IntPtr device)
    {
        ulong luid = 0;
        var properties = new IntelGclDeviceProperties { Size = 320, DeviceId = (IntPtr)(&luid), DeviceIdSize = 8 };
        return functions.Properties(device, ref properties) == 0 && properties.Size == 320 && properties.Version == 0
            && properties.DeviceId == (IntPtr)(&luid) && properties.DeviceIdSize == 8 && luid != 0
            ? new(device, luid, properties.VendorId, properties.DeviceType, properties.GraphicsAdapterProperties) : null;
    }

    internal int AcceptOverclockWaiver(IntPtr device) => functions.OverclockWaiver?.Invoke(device) ?? -1;
    internal double? ReadFrequencyOffset(IntPtr device)
        => functions.FrequencyOffsetGet is { } read && read(device, out var offset) == 0 && double.IsFinite(offset) ? offset : null;
    internal int WriteFrequencyOffset(IntPtr device, double offset) => functions.FrequencyOffsetSet?.Invoke(device, offset) ?? -1;

    public bool TryGetDevices(out IntPtr[] devices, CancellationToken cancellationToken)
    {
        devices = [];
        cancellationToken.ThrowIfCancellationRequested();
        uint count = 0;
        if (api == IntPtr.Zero || functions.Enumerate(api, ref count, null) != 0
            || count is 0 or > MaximumDeviceCount) return false;
        var allocated = new IntPtr[count];
        var expected = count;
        cancellationToken.ThrowIfCancellationRequested();
        if (functions.Enumerate(api, ref count, allocated) != 0 || count != expected
            || allocated.Any(static value => value == IntPtr.Zero)
            || allocated.Distinct().Count() != allocated.Length) return false;
        devices = allocated;
        return true;
    }

    public unsafe bool TryGetIdentity(IntPtr device, out IntelGclIdentity identity)
    {
        identity = default;
        if (api == IntPtr.Zero || device == IntPtr.Zero) return false;
        ulong luid = 0;
        var pointer = (IntPtr)(&luid);
        var properties = new IntelGclDeviceProperties { Size = 320, DeviceId = pointer, DeviceIdSize = 8 };
        if (functions.Properties(device, ref properties) != 0 || properties.Size != 320 || properties.Version != 0
            || properties.DeviceId != pointer || properties.DeviceIdSize != 8 || properties.DeviceType != 1
            || properties.VendorId != 0x8086 || properties.DevicePciId is 0 or > 0xFFFF || luid == 0) return false;
        identity = new(luid, properties.VendorId, properties.DevicePciId);
        return true;
    }

    public bool TryReadTelemetry(IntPtr device, out IntelGclPowerTelemetry telemetry)
    {
        telemetry = new() { Size = 1024 };
        if (api != IntPtr.Zero && device != IntPtr.Zero && functions.Telemetry(device, ref telemetry) == 0
            && telemetry.Size == 1024 && telemetry.Version == 0) return true;
        telemetry = default;
        return false;
    }

    public bool TryReadPciState(IntPtr device, out IntelGclPciState state)
    {
        state = new() { Size = 32, Speed = new() { Size = 24, Generation = -1, Width = -1, MaximumBandwidth = -1 } };
        if (api != IntPtr.Zero && device != IntPtr.Zero && functions.PciState is { } read
            && read(device, ref state) == 0 && state.Size == 32 && state.Version == 0
            && state.Speed.Size == 24 && state.Speed.Version == 0) return true;
        state = default;
        return false;
    }

    public bool TryReadPciProperties(IntPtr device, out IntelGclPciProperties properties)
    {
        properties = new() { Size = 64, Address = new() { Size = 24 },
            MaximumSpeed = new() { Size = 24, Generation = -1, Width = -1, MaximumBandwidth = -1 } };
        if (api != IntPtr.Zero && device != IntPtr.Zero && functions.PciProperties is { } read
            && read(device, ref properties) == 0 && properties.Size == 64 && properties.Version == 0
            && properties.MaximumSpeed.Size == 24 && properties.MaximumSpeed.Version == 0) return true;
        properties = default;
        return false;
    }

    public bool TryGetFans(IntPtr device, uint maximumCount, out IntPtr[] fans, CancellationToken cancellationToken)
    {
        fans = [];
        cancellationToken.ThrowIfCancellationRequested();
        if (api == IntPtr.Zero || device == IntPtr.Zero || maximumCount == 0 || functions.Fans is not { } enumerate)
            return false;
        uint count = 0;
        if (enumerate(device, ref count, null) != 0 || count > maximumCount || count > int.MaxValue) return false;
        if (count == 0) return true;
        var expected = count;
        var handles = new IntPtr[checked((int)count)];
        cancellationToken.ThrowIfCancellationRequested();
        if (enumerate(device, ref count, handles) != 0 || count != expected
            || handles.Any(static handle => handle == IntPtr.Zero) || handles.Distinct().Count() != handles.Length)
            return false;
        cancellationToken.ThrowIfCancellationRequested();
        fans = handles;
        return true;
    }

    public bool TryReadFanRpm(IntPtr fan, out int rpm)
    {
        rpm = -1;
        if (api == IntPtr.Zero || fan == IntPtr.Zero || functions.FanState is not { } read) return false;
        // ctl_fan_speed_units_t::CTL_FAN_SPEED_UNITS_RPM = 0; -1 means not measurable.
        if (read(fan, 0, out var value) != 0 || value < 0) return false;
        rpm = value;
        return true;
    }

    internal int? Close()
    {
        var handle = Interlocked.Exchange(ref api, IntPtr.Zero);
        if (handle == IntPtr.Zero) return null;
        var result = functions.Close(handle);
        return result is 0 or 1 /* SUCCESS_STILL_OPEN_BY_ANOTHER_CALLER */ ? null : result;
    }

    public void Dispose()
    {
        if (Close() is { } result)
            throw new ExternalException("IGCL could not close the sample API session.", result);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryExW(string name, IntPtr file, uint flags);
}

internal sealed record IntelGclNativeFunctions(IntelGclInit Init, IntelGclClose Close, IntelGclEnumerate Enumerate,
    IntelGclProperties Properties, IntelGclTelemetry Telemetry, IntelGclPciStateGet? PciState = null, IntelGclPciPropertiesGet? PciProperties = null,
    IntelGclEnumerate? Fans = null, IntelGclFanStateGet? FanState = null,
    IntelGclOverclockWaiver? OverclockWaiver = null, IntelGclFrequencyOffsetGet? FrequencyOffsetGet = null,
    IntelGclFrequencyOffsetSet? FrequencyOffsetSet = null);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IntelGclInit(ref IntelGclInitArgs args, out IntPtr api);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IntelGclClose(IntPtr api);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IntelGclEnumerate(IntPtr api, ref uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[]? devices);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IntelGclProperties(IntPtr device, ref IntelGclDeviceProperties properties);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IntelGclTelemetry(IntPtr device, ref IntelGclPowerTelemetry telemetry);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IntelGclPciStateGet(IntPtr device, ref IntelGclPciState state);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IntelGclPciPropertiesGet(IntPtr device, ref IntelGclPciProperties properties);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IntelGclFanStateGet(IntPtr fan, uint units, out int rpm);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IntelGclOverclockWaiver(IntPtr device);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IntelGclFrequencyOffsetGet(IntPtr device, out double offset);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IntelGclFrequencyOffsetSet(IntPtr device, double offset);
