using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

/// <summary>Exact OpenCL identity for one device. UUID bytes are stored as uppercase hexadecimal without formatting.
/// LUID/node matching is the only Windows adapter association used by this capability reader.</summary>
public sealed record WindowsOpenClDeviceIdentity
{
    public WindowsOpenClAdapterTarget Adapter { get; }
    public string DeviceUuidHex { get; }
    public string DriverUuidHex { get; }

    public WindowsOpenClDeviceIdentity(WindowsOpenClAdapterTarget adapter, string deviceUuidHex, string driverUuidHex)
    {
        if (!IsUuidHex(deviceUuidHex)) throw new ArgumentException("A device UUID must be 16 bytes of uppercase hex.", nameof(deviceUuidHex));
        if (!IsUuidHex(driverUuidHex)) throw new ArgumentException("A driver UUID must be 16 bytes of uppercase hex.", nameof(driverUuidHex));
        Adapter = adapter;
        DeviceUuidHex = deviceUuidHex;
        DriverUuidHex = driverUuidHex;
    }

    internal static bool IsUuidHex(string? value)
        => value is { Length: 32 } && value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

/// <summary>Nullable selected values distinguish field-query failure from an empty successful extension list.</summary>
public sealed record WindowsOpenClDeviceCapabilityRead(
    WindowsOpenClDeviceIdentity Identity,
    string? DeviceVersion,
    ImmutableArray<string>? Extensions);

/// <summary>DirectorySucceeded means every enumerated GPU device could be identity-checked and the platform/device
/// enumeration completed. An empty successful directory means no matching OpenCL GPU device was found.</summary>
public sealed record WindowsOpenClCapabilityReadResult(
    bool DirectorySucceeded,
    ImmutableArray<WindowsOpenClDeviceCapabilityRead> Devices)
{
    internal static WindowsOpenClCapabilityReadResult Unavailable
        => new(false, []);
}

/// <summary>Provides read-only observations for an explicit OpenCL field and adapter scope.
/// External providers are passed to the original topology owner; cached reads never call this provider.</summary>
public interface IWindowsOpenClCapabilityReader
{
    WindowsOpenClCapabilityReadResult Read(WindowsOpenClCapabilityScope scope,
        WindowsOpenClCapabilityFields selectedFields, CancellationToken cancellationToken);
}
