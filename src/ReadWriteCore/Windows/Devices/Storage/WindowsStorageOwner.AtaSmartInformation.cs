using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using static ResourceManager.ReadWrite.Windows.Platform.PlatformSensorWmiUtilities;

namespace ResourceManager.ReadWrite.Windows.Devices;

public sealed partial class WindowsStorageOwner
{
    public DeviceInformationSnapshot ReadAtaSmartAttributeInformationFields(
        AtaSmartAttributeInformationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Devices);
        if (request.MaximumDeviceCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.MaximumDeviceCount));
        if (request.Devices.Count > request.MaximumDeviceCount)
            throw new ArgumentOutOfRangeException(nameof(request.Devices), "The selected disk count exceeds MaximumDeviceCount.");
        if (request.Devices.Count == 0) return DeviceInformationSnapshot.Empty;
        const AtaSmartAttributeInformationFields all = AtaSmartAttributeInformationFields.Normalized
            | AtaSmartAttributeInformationFields.Raw | AtaSmartAttributeInformationFields.Threshold;
        foreach (var (identity, fields) in request.Devices)
        {
            if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("A disk identity is required.", nameof(request));
            if ((fields & ~all) != 0) throw new ArgumentOutOfRangeException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var selected = request.Devices.Where(static pair => pair.Value != AtaSmartAttributeInformationFields.None).ToArray();
        if (selected.Length == 0)
            return CreateAtaSmartAttributeInformationSnapshot(request.Devices, null);

        var physical = query(@"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, FriendlyName, SerialNumber, UniqueId, UniqueIdFormat, Size, BusType FROM MSFT_PhysicalDisk");
        cancellationToken.ThrowIfCancellationRequested();
        var osDisks = query(@"root\Microsoft\Windows\Storage",
            "SELECT Number, SerialNumber, UniqueId, Size, BusType FROM MSFT_Disk");
        cancellationToken.ThrowIfCancellationRequested();
        if (physical.Error is not null || osDisks.Error is not null)
            return CreateAtaSmartAttributeInformationSnapshot(request.Devices, null);

        var identities = ProjectIdentities(physical.Rows);
        var reads = new Dictionary<int, AtaSmartDeviceReadRequest>();
        foreach (var (deviceIdentity, fields) in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = identities.Where(identity => identity.ExactIdentityKey == deviceIdentity && identity.SupportsAtaSmart).ToArray();
            if (matches.Length != 1) continue;
            var pages = AtaSmartPages.None;
            if ((fields & (AtaSmartAttributeInformationFields.Normalized | AtaSmartAttributeInformationFields.Raw)) != 0)
                pages |= AtaSmartPages.Data;
            if ((fields & AtaSmartAttributeInformationFields.Threshold) != 0)
                pages |= AtaSmartPages.Thresholds;
            reads.Add(matches[0].DisplayIndex, new(deviceIdentity, pages));
        }

        var sensorSnapshot = reads.Count == 0
            ? new StorageOwnerSensorSnapshot(new("Windows ATA SMART", HardwareSensorProviderStates.NotRequested, ""), []) { Identities = identities }
            : ReadAtaSensorsCore(new AtaSmartSensorReadRequest(reads), physical, osDisks);
        cancellationToken.ThrowIfCancellationRequested();
        var byIndex = sensorSnapshot.Disks.ToDictionary(static disk => disk.Index);
        var capturedAt = DateTimeOffset.UtcNow;
        var objects = request.Devices.Select(pair =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<MetricFieldCode, byte[]>? pages = null;
            var identity = identities.Where(item => item.ExactIdentityKey == pair.Key && item.SupportsAtaSmart).ToArray();
            if (identity.Length == 1 && byIndex.TryGetValue(identity[0].DisplayIndex, out var disk)
                && disk.IdentityKey == pair.Key)
                pages = disk.BytesValues;
            var fields = WindowsInformationFieldProjection.AtaSmartAttributeFields(pages, pair.Value, capturedAt);
            return new DeviceInformationObject(pair.Key, "physical_disk", fields);
        }).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset? observedAt = objects.SelectMany(static item => item.Fields).Any(static field => field.Value is not null)
            ? capturedAt : null;
        return new(observedAt, objects);
    }

    private static DeviceInformationSnapshot CreateAtaSmartAttributeInformationSnapshot(
        IReadOnlyDictionary<string, AtaSmartAttributeInformationFields> devices,
        DateTimeOffset? observedAt)
        => new(observedAt, devices.Select(pair => new DeviceInformationObject(pair.Key, "physical_disk",
            WindowsInformationFieldProjection.AtaSmartAttributeFields(null, pair.Value, observedAt))).ToArray());
}
