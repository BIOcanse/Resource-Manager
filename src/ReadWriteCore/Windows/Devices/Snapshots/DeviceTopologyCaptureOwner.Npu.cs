using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Npu;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    /// <summary>Passive default composition for hardware NPU declarations. Discover and Collect
    /// explicitly query DXCore and the same present-device snapshot through the original owner.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static DeviceTopologyCaptureOwner CreateNpuInformation()
        => CreateNpuInformation(WindowsDeviceTopologyNativeReader.ReadSnapshot, new WindowsNpuInformationReader().Read);

    internal static DeviceTopologyCaptureOwner CreateNpuInformation(Func<DeviceTopologyNativeSnapshot> read,
        Func<DeviceTopologyNativeSnapshot, IReadOnlyList<DeviceTopologyNpuInformation>?> readNpus)
        => new(new WindowsAcpiFirmwareTableReader(), new DeviceTopologyCapturePolicy(
            new NpuTopologyReader(read, readNpus),
            static snapshot => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                snapshot.NpuInformation?.Select(static row => row with {
                    LocationPathsDeclared = row.LocationPathsDeclared?.Order(StringComparer.Ordinal).ToArray()
                }).OrderBy(static row => row.DeviceIdentity, StringComparer.Ordinal).ToArray(),
                DeviceFingerprintJsonContext.Default.DeviceTopologyNpuInformationArray))),
            static (snapshot, observedAt) => WindowsInformationFieldProjection.Npus(snapshot?.NpuInformation, observedAt),
            static (_, _) => Task.CompletedTask,
            static _ => new("npu-directory-unavailable", []),
            static (_, _, _, _) => { }, static _ => { }, static () => { }));

    private sealed class NpuTopologyReader(Func<DeviceTopologyNativeSnapshot> read,
        Func<DeviceTopologyNativeSnapshot, IReadOnlyList<DeviceTopologyNpuInformation>?> readNpus) : IDeviceTopologyReader
    {
        public DeviceTopologySnapshot ReadSnapshot()
        {
            var native = read();
            if (native.Error is not null) throw new InvalidOperationException(native.Error);
            var rows = readNpus(native) ?? throw new InvalidOperationException("NPU directory unavailable.");
            return new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], [])
            { NpuInformation = rows };
        }
    }
}
