using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Platform;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using static ResourceManager.ReadWrite.Windows.Platform.PlatformSensorWmiUtilities;

namespace ResourceManager.ReadWrite.Windows.Devices;

/// <summary>Original storage capture and identity matcher; does not own polling or host ordinal generations.</summary>
public sealed partial class WindowsStorageOwner
{
    private readonly Func<string, string, QueryResult> query;
    private readonly Func<int, string, string?, int?, NvmeHealthRead?> readNvme;
    private readonly Func<int, string, AtaSmartPages, string?, int?, AtaSmartRead?> readAta;
    private readonly Func<int, StorageIdentifyProtocol, string, string?, int?, StorageIdentifyRead?> readIdentify;
    private readonly Func<int, string, string?, int?, uint?, bool?> readTrim;
    private readonly Func<string, ulong?, string> diskDetail;
    private readonly Func<StorageOwnerMessage, string> message;

    public WindowsStorageOwner(TimeSpan cacheLifetime, TimeProvider clock, Func<bool, BackendMessage> sourceMessage,
        Func<string, string, DeviceTopologyWmiQueryResult>? capabilityQuery = null,
        Func<string, StorageVolumeObservation>? volumeQuery = null,
        Func<string, string, QueryResult>? query = null,
        Func<int, string, string?, int?, NvmeHealthRead?>? readNvme = null,
        Func<int, string, AtaSmartPages, string?, int?, AtaSmartRead?>? readAta = null,
        Func<int, StorageIdentifyProtocol, string, string?, int?, StorageIdentifyRead?>? readIdentify = null,
        Func<string, ulong?, string>? diskDetail = null, Func<StorageOwnerMessage, string>? message = null,
        Func<int, string, string?, int?, uint?, bool?>? readTrim = null)
    {
        if (cacheLifetime < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cacheLifetime));
        this.cacheLifetime = () => cacheLifetime;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.sourceMessage = sourceMessage ?? throw new ArgumentNullException(nameof(sourceMessage));
        this.capabilityQuery = capabilityQuery ?? DeviceTopologyWmiUtilities.QueryObjects;
        usesWindowsCapabilityQuery = capabilityQuery is null;
        this.volumeQuery = volumeQuery ?? ReadVolume;
        this.query = query ?? QueryStorage;
        this.readNvme = readNvme ?? WindowsNvmeHealthReader.Read;
        this.readAta = readAta ?? WindowsAtaSmartReader.Read;
        this.readIdentify = readIdentify ?? WindowsStorageIdentifyReader.Read;
        this.readTrim = readTrim ?? WindowsStorageIdentifyReader.ReadTrim;
        this.diskDetail = diskDetail ?? ((name, _) => name);
        this.message = message ?? (code => code.ToString());
    }

    private static StorageVolumeObservation ReadVolume(string driveLetter)
    {
        var drive = new DriveInfo($"{driveLetter}\\");
        return drive.IsReady
            ? new(true, drive.DriveFormat, checked((ulong)drive.TotalSize), checked((ulong)drive.AvailableFreeSpace))
            : new(false, null, null, null);
    }

    [ResourceManager.ReadWrite.RuntimeReadTransport]
    private static QueryResult QueryStorage(string path, string command)
        => QueryObjects(path, command, TimeSpan.FromSeconds(2));

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows storage topology changed")]
    public StorageIdentityRead ReadIdentities()
    {
        var result = query(@"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, FriendlyName, UniqueId, SerialNumber, BusType FROM MSFT_PhysicalDisk");
        return new(result.Error, result.Error is null ? ProjectIdentities(result.Rows) : []);
    }

    public static IReadOnlyList<StorageOwnerIdentity> ProjectIdentities(
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var declared = rows.Select(row => new
            {
                Index = ParseInt(row.GetValueOrDefault("DeviceId")),
                UniqueId = ReadString(row, "UniqueId"),
                SerialNumber = ReadString(row, "SerialNumber"),
                Name = ReadString(row, "FriendlyName"),
                Identity = WindowsStorageDeviceIdentity.Create(ReadString(row, "UniqueId"), ReadString(row, "SerialNumber")),
                Nvme = ParseInt(row.GetValueOrDefault("BusType")) == 17,
                Ata = WindowsAtaSmartReader.IsAtaBus(ParseInt(row.GetValueOrDefault("BusType")))
            }).ToArray();
        var identities = declared.GroupBy(static row => row.Identity, StringComparer.Ordinal)
            .Where(static group => group.Key.Length > 0 && group.Count() == 1).Select(static group => group.Key).ToHashSet(StringComparer.Ordinal);
        var indexes = declared.Where(static row => row.Index is >= 0).GroupBy(static row => row.Index!.Value)
            .Where(static group => group.Count() == 1).Select(static group => group.Key).ToHashSet();
        return declared.Where(row => row.Index is { } index && indexes.Contains(index) && identities.Contains(row.Identity))
            .Select(row => new StorageOwnerIdentity(row.Index!.Value, row.Identity)
            {
                UniqueId = row.UniqueId,
                SerialNumber = row.SerialNumber,
                NameDeclared = row.Name,
                SupportsNvmeHealth = row.Nvme,
                SupportsAtaSmart = row.Ata
            })
            .OrderBy(static row => row.DisplayIndex).ToArray();
    }

    public StorageOwnerSensorSnapshot ReadStorageSensors()
    {
        var physicalDisks = query(
            @"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, FriendlyName, SerialNumber, UniqueId, Size, HealthStatus, BusType FROM MSFT_PhysicalDisk");

        if (physicalDisks.Error is not null)
        {
            return new StorageOwnerSensorSnapshot(
                new HardwareSensorProviderState(
                    "Windows Storage Reliability Counter",
                    HardwareSensorProviderStates.Unavailable,
                    physicalDisks.Error),
                []);
        }

        var identities = ProjectIdentities(physicalDisks.Rows);
        var byIndex = identities.ToDictionary(static disk => disk.DisplayIndex, static disk => disk.ExactIdentityKey);
        var disks = physicalDisks.Rows
            .Where(row => ParseInt(row.GetValueOrDefault("DeviceId")) is { } index
                && byIndex.TryGetValue(index, out var identity)
                && identity == WindowsStorageDeviceIdentity.Create(ReadString(row, "UniqueId"), ReadString(row, "SerialNumber")))
            .Select(static row => new PhysicalDiskInfo(
                ParseInt(row.GetValueOrDefault("DeviceId")) ?? -1,
                ReadString(row, "DeviceId") ?? string.Empty,
                ReadString(row, "FriendlyName") ?? "Disk",
                ReadString(row, "SerialNumber"),
                ReadString(row, "UniqueId"),
                ParseUlong(row.GetValueOrDefault("Size")),
                ParseUlong(row.GetValueOrDefault("HealthStatus"))))
            .OrderBy(static disk => disk.Index)
            .ToArray();

        if (disks.Length == 0)
        {
            return new StorageOwnerSensorSnapshot(
                new HardwareSensorProviderState(
                    "Windows Storage Reliability Counter",
                    HardwareSensorProviderStates.Unavailable,
                    message(StorageOwnerMessage.NoPhysicalDisks)),
                []) { Identities = identities };
        }

        var counters = query(
            @"root\Microsoft\Windows\Storage",
            "SELECT * FROM MSFT_StorageReliabilityCounter");

        if (counters.Error is not null)
        {
            return new StorageOwnerSensorSnapshot(
                new HardwareSensorProviderState(
                    "Windows Storage Reliability Counter",
                    HardwareSensorProviderStates.Unavailable,
                    counters.Error),
                disks.Select(disk => CreateDiskSensor(disk, null, message(StorageOwnerMessage.CounterUnavailable))).ToArray())
            { Identities = identities };
        }

        var rows = counters.Rows;
        var sensors = disks
            .Select(disk =>
            {
                var row = FindReliabilityCounter(rows, disk);
                var temperature = row is null ? null : ParseTemperature(row.GetValueOrDefault("Temperature"));
                var detail = row is null
                    ? message(StorageOwnerMessage.CounterMissing)
                    : "Windows Storage Reliability Counter";
                return CreateDiskSensor(disk, temperature, detail, row);
            })
            .ToArray();

        var state = sensors.Any(static sensor => sensor.TemperatureCelsius is not null
            || sensor.IntegerValues.Count > 0 || sensor.Values.Count > 0)
            ? new HardwareSensorProviderState("Windows Storage Reliability Counter", HardwareSensorProviderStates.Active, message(StorageOwnerMessage.Active))
            : new HardwareSensorProviderState("Windows Storage Reliability Counter", HardwareSensorProviderStates.Unavailable, message(StorageOwnerMessage.NoValues));

        return new StorageOwnerSensorSnapshot(state, sensors) { Identities = identities };
    }
    private DiskTemperatureSensor CreateDiskSensor(PhysicalDiskInfo disk, double? temperature, string detail,
        IReadOnlyDictionary<string, object?>? row = null)
    {
        var integers = ReadReliabilityValues(row);
        if (disk.HealthStatus is { } health) integers[MetricFieldCode.DiskHealthStatus] = health;
        var values = new Dictionary<MetricFieldCode, double>();
        if (row is not null && ParseUlong(row.GetValueOrDefault("TemperatureMax")) is <= byte.MaxValue and var maximum)
            values[MetricFieldCode.DiskTemperatureMaximum] = maximum;
        return new DiskTemperatureSensor(
            disk.Index,
            disk.FriendlyName,
            temperature,
            diskDetail(disk.FriendlyName, disk.SizeBytes))
        {
            IdentityKey = WindowsStorageDeviceIdentity.Create(disk.UniqueId, disk.SerialNumber),
            Values = values,
            IntegerValues = integers
        };
    }

    public static Dictionary<MetricFieldCode, ulong> ReadReliabilityValues(IReadOnlyDictionary<string, object?>? row)
    {
        var values = new Dictionary<MetricFieldCode, ulong>();
        if (row is null) return values;
        foreach (var (code, property) in ReliabilityFields)
            if (ParseUlong(row.GetValueOrDefault(property)) is { } value) values[code] = value;
        return values;
    }

    private static readonly (MetricFieldCode Code, string Property)[] ReliabilityFields =
    [
        (MetricFieldCode.DiskWear, "Wear"),
        (MetricFieldCode.DiskReadErrorsTotal, "ReadErrorsTotal"),
        (MetricFieldCode.DiskReadErrorsCorrected, "ReadErrorsCorrected"),
        (MetricFieldCode.DiskReadErrorsUncorrected, "ReadErrorsUncorrected"),
        (MetricFieldCode.DiskWriteErrorsTotal, "WriteErrorsTotal"),
        (MetricFieldCode.DiskWriteErrorsCorrected, "WriteErrorsCorrected"),
        (MetricFieldCode.DiskWriteErrorsUncorrected, "WriteErrorsUncorrected"),
        (MetricFieldCode.DiskPowerOnHours, "PowerOnHours"),
        (MetricFieldCode.DiskStartStopCycles, "StartStopCycleCount"),
        (MetricFieldCode.DiskStartStopCyclesMax, "StartStopCycleCountMax"),
        (MetricFieldCode.DiskLoadUnloadCycles, "LoadUnloadCycleCount"),
        (MetricFieldCode.DiskLoadUnloadCyclesMax, "LoadUnloadCycleCountMax"),
        (MetricFieldCode.DiskReadLatencyMax, "ReadLatencyMax"),
        (MetricFieldCode.DiskWriteLatencyMax, "WriteLatencyMax"),
        (MetricFieldCode.DiskFlushLatencyMax, "FlushLatencyMax")
    ];

    private static Dictionary<string, object?>? FindReliabilityCounter(
        IReadOnlyList<Dictionary<string, object?>> counters,
        PhysicalDiskInfo disk)
    {
        var matches = counters.Where(row => ValueMatches(row, "DeviceId", disk.DeviceId)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private sealed record PhysicalDiskInfo(
        int Index,
        string DeviceId,
        string FriendlyName,
        string? SerialNumber,
        string? UniqueId,
        ulong? SizeBytes,
        ulong? HealthStatus);

}
