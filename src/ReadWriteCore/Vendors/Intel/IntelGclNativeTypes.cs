using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Vendors.Intel;

// IGCL API 1.0, Version 0. Sizes/offsets checked against Intel's x64 C header.
// Unconsumed tail fields still occupy their official space, so native writes stay bounded.
[StructLayout(LayoutKind.Explicit, Size = 36)]
internal struct IntelGclInitArgs
{
    [FieldOffset(0)] public uint Size;
    [FieldOffset(4)] public byte Version;
    [FieldOffset(8)] public uint AppVersion;
    [FieldOffset(12)] public uint Flags;
    [FieldOffset(16)] public uint SupportedVersion;
    [FieldOffset(20)] public Guid ApplicationUid;
}

[StructLayout(LayoutKind.Explicit, Size = 320)]
internal struct IntelGclDeviceProperties
{
    [FieldOffset(0)] public uint Size;
    [FieldOffset(4)] public byte Version;
    [FieldOffset(8)] public IntPtr DeviceId;
    [FieldOffset(16)] public uint DeviceIdSize;
    [FieldOffset(20)] public int DeviceType;
    [FieldOffset(64)] public uint VendorId;
    [FieldOffset(68)] public uint DevicePciId;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct IntelGclTelemetryItem
{
    [FieldOffset(0)] public byte Supported;
    [FieldOffset(4)] public IntelGclUnit Unit;
    [FieldOffset(8)] public IntelGclDataType Type;
    [FieldOffset(16)] public sbyte Int8;
    [FieldOffset(16)] public byte UInt8;
    [FieldOffset(16)] public short Int16;
    [FieldOffset(16)] public ushort UInt16;
    [FieldOffset(16)] public int Int32;
    [FieldOffset(16)] public uint UInt32;
    [FieldOffset(16)] public long Int64;
    [FieldOffset(16)] public ulong UInt64;
    [FieldOffset(16)] public float Float;
    [FieldOffset(16)] public double Double;
}

[StructLayout(LayoutKind.Explicit, Size = 1024)]
internal struct IntelGclPowerTelemetry
{
    [FieldOffset(0)] public uint Size;
    [FieldOffset(4)] public byte Version;
    [FieldOffset(8)] public IntelGclTelemetryItem Timestamp;
    [FieldOffset(32)] public IntelGclTelemetryItem GpuEnergy;
    [FieldOffset(56)] public IntelGclTelemetryItem GpuVoltage;
    [FieldOffset(80)] public IntelGclTelemetryItem GraphicsClock;
    [FieldOffset(104)] public IntelGclTelemetryItem GpuTemperature;
    [FieldOffset(128)] public IntelGclTelemetryItem GlobalActivity;
    [FieldOffset(152)] public IntelGclTelemetryItem RenderComputeActivity;
    [FieldOffset(176)] public IntelGclTelemetryItem MediaActivity;
    [FieldOffset(200)] public byte PowerLimited;
    [FieldOffset(201)] public byte TemperatureLimited;
    [FieldOffset(202)] public byte CurrentLimited;
    [FieldOffset(203)] public byte VoltageLimited;
    [FieldOffset(204)] public byte UtilizationLimited;
    [FieldOffset(208)] public IntelGclTelemetryItem MemoryEnergy;
    [FieldOffset(232)] public IntelGclTelemetryItem MemoryVoltage;
    [FieldOffset(256)] public IntelGclTelemetryItem MemoryClock;
    [FieldOffset(304)] public IntelGclTelemetryItem MemoryReadBytes;
    [FieldOffset(328)] public IntelGclTelemetryItem MemoryWriteBytes;
    [FieldOffset(352)] public IntelGclTelemetryItem MemoryTemperature;
    [FieldOffset(384)] public IntelGclTelemetryItem BoardEnergy;
}

internal enum IntelGclUnit { Megahertz = 0, Volts = 3, Celsius = 5, Joules = 6, Seconds = 7, Bytes = 8 }
internal enum IntelGclDataType { Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double }
internal readonly record struct IntelGclIdentity(ulong Luid, uint VendorId, uint DeviceId);

internal interface IIntelGclSession : IDisposable
{
    bool TryGetDevices(out IntPtr[] devices, CancellationToken cancellationToken);
    bool TryGetIdentity(IntPtr device, out IntelGclIdentity identity);
    bool TryReadTelemetry(IntPtr device, out IntelGclPowerTelemetry telemetry);
    bool TryGetFans(IntPtr device, uint maximumCount, out IntPtr[] fans, CancellationToken cancellationToken)
    { fans = []; return false; }
    bool TryReadFanRpm(IntPtr fan, out int rpm) { rpm = -1; return false; }
    bool TryReadPciState(IntPtr device, out IntelGclPciState state);
    bool TryReadPciProperties(IntPtr device, out IntelGclPciProperties properties);
}
