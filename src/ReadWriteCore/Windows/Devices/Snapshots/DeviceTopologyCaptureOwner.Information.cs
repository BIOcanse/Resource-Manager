using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    public const string InformationCollectionItemId = "deviceTopology.snapshot";
    public SamplingPublicationSignal InformationPublications { get; } = new();
    private InformationReadWriteBackend? informationBackend;

    /// <summary>Passive default composition for current device-bound driver declarations.
    /// Discover/Collect explicitly use the original present-device native enumeration;
    /// no OEM/topology presentation policy or INF-store scan is required.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static DeviceTopologyCaptureOwner CreateDriverInformation()
        => CreateDriverInformation(WindowsDeviceTopologyNativeReader.ReadSnapshot);

    internal static DeviceTopologyCaptureOwner CreateDriverInformation(Func<DeviceTopologyNativeSnapshot> read)
        => new(new WindowsAcpiFirmwareTableReader(), new DeviceTopologyCapturePolicy(
            new DriverTopologyReader(read),
            static snapshot => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                snapshot.DriverInformation?.OrderBy(static row => row.DeviceIdentity, StringComparer.Ordinal).ToArray()))),
            static (snapshot, observedAt) => WindowsInformationFieldProjection.Drivers(snapshot?.DriverInformation, observedAt),
            static (_, _) => Task.CompletedTask,
            static _ => new("driver-directory-unavailable", []),
            static (_, _, _, _) => { }, static _ => { }, static () => { }));

    public InformationReadWriteBackend CreateBackend()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (topology is null) throw new InvalidOperationException("No topology policy is bound to this capture owner.");
        var existing = Volatile.Read(ref informationBackend);
        if (existing is not null) return existing;
        var next = new InformationReadWriteBackend(() => ReadState().InformationFields,
            token => CaptureInformationAsync(token), static _ => new(true, null, []), null,
            static definition => definition != DeviceInformationFieldRegistry.GpuNvidiaDriverProfileSettingValue);
        return Interlocked.CompareExchange(ref informationBackend, next, null) ?? next;
    }

    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
        => new InformationCollectionBorrow(CreateBackend(), InformationPublications, InformationCollectionItemId,
            async (selection, permit, token) =>
            {
                var committed = false;
                var sourceRead = false;
                var permission = new DeviceTopologyCapturePermission(() => permit.IsActive,
                    commit => { committed = permit.TryPublish(commit); return committed; }, () => true);
                var snapshot = await CaptureInformationAsync(token, permission, () => sourceRead = true);
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, sourceRead));
            }, FieldCollectionSourceKind.SourceQuery);

    private sealed class DriverTopologyReader(Func<DeviceTopologyNativeSnapshot> read) : IDeviceTopologyReader
    {
        public DeviceTopologySnapshot ReadSnapshot()
        {
            var native = read();
            // Partial directory failure is not a successful empty inventory.
            if (native.Error is not null) throw new InvalidOperationException(native.Error);
            var rows = native.Devices.Values.Select(static device => device.DriverInformation)
                .OfType<DeviceTopologyDriverInformation>().OrderBy(static row => row.DeviceIdentity, StringComparer.Ordinal).ToArray();
            return new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], [])
            { DriverInformation = rows };
        }
    }
}
