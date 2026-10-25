using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.ReadWrite.Windows.Pdh;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using ResourceManager.ReadWrite.Windows.Platform;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Vendors.Nvidia;

internal static class WindowsDiskPerformanceConsumer
{
    private const int DisplayIndex = 2;
    private static readonly (string Name, double Value)[] Expected =
    [
        ("storage_all_read_bytes_per_second_actual", 1234),
        ("storage_all_write_bytes_per_second_actual", 50),
        ("storage_all_read_iops_actual", 2),
        ("storage_all_write_iops_actual", 1),
        ("storage_all_active_percent_actual", 20),
        ("storage_all_queue_length_actual", 3),
        ("storage_all_latency_actual", 12)
    ];

    internal static async Task RunRecordedAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        var identityKey = WindowsStorageDeviceIdentity.Create("RECORDED-VPD-UNIQUE", "RECORDED-ATA-SERIAL");
        var sample = new WindowsDiskPerformanceMetrics(1234, 50, 2, 1, 20, 3, 12);
        var reads = 0;
        WindowsDiskPerformanceTarget[]? selectedTargets = null;
        DateTimeOffset? recordedObservedAt = null;
        var resources = new WindowsHardwareMetricResources
        {
            Controls = new WindowsMetricControlResources
            {
                ControlReadCpuIdentity = static () => null,
                ControlReadCpuTopology = static () => new(null, null)
            },
            ReadCpuCounters = static () => default,
            ReadMemory = static () => new(0, 0, 0, false),
            ReadVirtualMemory = static () => new(0, 0, 0, false),
            ReadNumaAvailable = static (_, _) => new Dictionary<string, ulong>(),
            ReadNumaIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadGpuInventory = static () => new(SamplingObservationStatus.Current, 1,
                DateTimeOffset.UtcNow.UtcTicks, 0, 0, 0, 1, []),
            ReadCpuFrequency = static () => new(0, 0, 0, "recorded"),
            ReadGpuUsage = static _ => PdhGpuEngineUsageRead.NotRequested,
            ReadNvmlAsync = static (_, _, _) => Task.FromResult<IReadOnlyList<GpuMetrics>>([]),
            ReadNvapiAsync = static (_, _, _) => Task.FromResult<IReadOnlyList<NvidiaNvapiGpuSensor>>([]),
            ReadAdlxAsync = static (_, _, _) => Task.FromResult<IReadOnlyList<GpuMetrics>>([]),
            ReadAmdSmu = static _ => throw new InvalidOperationException("The recorded disk capture must not read AMD SMU."),
            ReadIntelGpu = static (_, _, _) => [],
            ReadIntelMsr = static (_, _) => [],
            ReadBatteryIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadBattery = static (_, _) => [],
            ReadNetworkIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadNetworkInterfaces = static (_, _) => [],
            ReadSystemIo = static _ => new(PdhDiskMetrics.NotRequested, new(0, 0, 0, 0)),
            ReadStorageIdentities = generation => new(NativeMetricSnapshotCatalogTopologyStatus.Complete,
                [new NativeMetricSnapshotOrdinalCatalogIdentity(DisplayIndex, identityKey, generation) { SupportsAtaSmart = true }]),
            ReadFanIdentities = static generation => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadStorageSensors = static () => new(new("recorded", "not-requested", null), []),
            ReadNvmeSensors = static _ => new(new("recorded", "not-requested", null), []),
            ReadAtaSensors = static _ => new(new("recorded", "not-requested", null), []),
            ReadHardwareMonitor = static () => new(new("recorded", "not-requested", null), null, null, null,
                null, null, null, null, [], []),
            ReadNotebookFans = static () => new(new("recorded", "not-requested", null), null, null, []),
            SynchronizeNotebookDemand = static _ => { },
            ReadDiskPerformanceAsync = (targets, token) =>
            {
                token.ThrowIfCancellationRequested();
                reads++;
                selectedTargets = targets.ToArray();
                recordedObservedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var observed = sample with { ObservedAt = recordedObservedAt };
                IReadOnlyDictionary<WindowsDiskPerformanceTarget, WindowsDiskPerformanceMetrics?> values =
                    targets.ToDictionary(static target => target, _ => (WindowsDiskPerformanceMetrics?)observed);
                return Task.FromResult(new WindowsDiskPerformanceCaptureResult(values));
            }
        };

        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(), resources,
            new WindowsMetricResourceOptions());
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        var catalog = await core.DiscoverAsync();
        var selectedFields = catalog.Fields.Where(item => Expected.Any(expected => expected.Name == item.Definition.Name)).ToArray();
        Require(selectedFields.Length == Expected.Length, "The packaged catalog expands all seven canonical fields for the recorded storage identity.");
        Require(reads == 0 && owner.ReadCachedSnapshot() is null,
            "Catalog discovery and the initial cached getter do not query or publish disk performance.");
        foreach (var expected in Expected)
        {
            var field = selectedFields.Single(item => item.Definition.Name == expected.Name);
            var capability = core.Query(field.Field);
            Require(field.Field.FieldName == expected.Name && field.Field.DeviceIdentity == identityKey
                && capability.Read.Supported && capability.Read.Dependencies.IsEmpty && !capability.Write.Supported,
                $"{expected.Name} is readable on the exact device without an install dependency and remains read-only.");
        }

        var readings = await core.CollectAsync(selectedFields.Select(static item => item.Field).ToImmutableArray());
        Require(reads == 1 && selectedTargets is { Length: 1 }
            && selectedTargets[0].DisplayIndex == DisplayIndex
            && selectedTargets[0].IdentityKey == identityKey
            && selectedTargets[0].SupportsAtaSmart,
            "The explicit source selection passes one current exact storage identity to its typed producer.");
        foreach (var expected in Expected)
        {
            var reading = readings.Single(item => item.Field.FieldName == expected.Name
                && item.Field.DeviceIdentity == identityKey);
            Require(reading.Status == FieldObservationStatus.Current && reading.Value?.Number == expected.Value
                && recordedObservedAt is { } observedAt && reading.ObservedAt == observedAt,
                $"The recorded {expected.Name} is projected into current with its end-sample observation time.");
            Require(core.ReadCached(reading.Field) == reading,
                $"Cached {expected.Name} is the same original owner current and does not invoke another capture.");
        }

        var current = owner.ReadCachedSnapshot();
        Require(current is not null && MetricSnapshotFactAssertions.SameCurrent(current, owner.ReadCachedSnapshot()) && reads == 1,
            "Direct source collection and cached reads retain the same original owner current without a second producer call.");

        Console.WriteLine("[PHYSICAL-DISK-PACKAGE] seven exact-identity fields, end-sample timestamps and original current cache.\n[PHYSICAL-DISK-DEPENDENCIES] seven built-in reads need no install dependency; writes remain unsupported.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
