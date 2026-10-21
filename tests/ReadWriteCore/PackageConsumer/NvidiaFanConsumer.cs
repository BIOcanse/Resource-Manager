using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.ReadWrite.Windows.Pdh;

internal static class NvidiaFanConsumer
{
    internal static async Task RunRecordedAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = new RecordedNvidiaFanResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(), fixture.Create(), new(),
            unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Require(core.Catalog.Fields.IsEmpty && fixture.Reads == 0 && fixture.DirectoryReads == 0,
            "construction and passive catalog perform no enumeration or capture");
        var catalog = await core.DiscoverAsync(timeout.Token);
        var fields = RecordedNvidiaFanResources.Fields(catalog);
        Require(fields.Length == 4 && catalog.Devices.Count(device => device.Resource == "gpu_fan") == 2,
            "two fan selectors on one exact parent publish independent gpu_fan objects");
        Require(fields.Select(field => field.DeviceIdentity).ToHashSet().SetEquals(fixture.Fans.Select(fan => fan.ExactIdentityKey)),
            "public identities retain exact parent and explicit fanIndex");
        foreach (var field in fields)
        {
            var capability = core.Query(field);
            Require(capability.Read.Supported && !capability.Write.Supported
                && capability.ReadSource == NativeMetricSnapshotSources.NvidiaNvml
                && capability.Definition.ValueKind == "float64"
                && capability.Definition.Unit == (field.FieldName == RecordedNvidiaFanResources.PercentName ? "%" : "RPM"),
                "intended speed fields preserve NVML source, unit, numeric kind and read-only capability");
        }
        Require(fixture.Reads == 0 && owner.ReadCachedSnapshot() is null && fixture.CatalogEngineProbeCount == 1,
            "discovery performs the original single engine directory probe without sampling fan values");
        var readings = await core.CollectAsync(fields, timeout.Token);
        RecordedNvidiaFanResources.RequireValues(readings, fixture.Fans);
        Require(fixture.Reads == 1 && fixture.CatalogEngineProbeCount == 1 && fixture.LastTargets.Length == 2
            && fixture.LastTargets.All(target => target.IncludePercent && target.IncludeRpm)
            && fixture.LastTargets.Select(target => target.Fan).ToHashSet().SetEquals(fixture.Fans),
            "one selected NVML call receives both exact fan selectors and requested quantities");
        var current = owner.ReadCachedSnapshot();
        foreach (var reading in readings) Require(core.ReadCached(reading.Field) == reading, "collection and cache share current");
        Require(ReferenceEquals(current, owner.ReadCachedSnapshot()) && fixture.Reads == 1 && fixture.UnselectedReads == 0
            && fixture.CatalogEngineProbeCount == 1,
            "passive current does not capture any source");

        var selected = new FieldReference(fixture.Fans[1].ExactIdentityKey, RecordedNvidiaFanResources.PercentName);
        var zero = await core.CollectAsync([selected], timeout.Token);
        Require(zero.Single() is { Status: FieldObservationStatus.Current, Value.Number: 0 }
            && fixture.LastTargets is [{ IncludePercent: true, IncludeRpm: false }]
            && fixture.LastTargets[0].Fan == fixture.Fans[1] && fixture.CatalogEngineProbeCount == 1,
            "single-field capture queries only fan1 percent and preserves zero");
        using (var borrow = owner.BorrowSubscriptionCapture())
        {
            borrow.Bind([selected]); borrow.SynchronizeDemand([selected]);
            var beforeRetirement = owner.ReadCachedSnapshot()!;
            var rejected = await borrow.CollectAsync([selected], new RetiredPermit(beforeRetirement.WorkspaceIdentity,
                beforeRetirement.ConfigurationGeneration), timeout.Token);
            Require(!rejected.Committed && ReferenceEquals(beforeRetirement, owner.ReadCachedSnapshot())
                && fixture.CatalogEngineProbeCount == 1,
                "retired borrowed permit cannot replace original current");
        }
        var beforeCancellation = owner.ReadCachedSnapshot();
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await core.CollectAsync(fields, canceled.Token); throw new InvalidDataException("Canceled fan capture completed."); }
            catch (OperationCanceledException) { }
        }
        Require(ReferenceEquals(beforeCancellation, owner.ReadCachedSnapshot()) && fixture.CatalogEngineProbeCount == 1,
            "cancellation preserves original current");

        fixture.Fail = true;
        var failed = await core.CollectAsync(fields, timeout.Token);
        Require(failed.All(reading => reading is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null })
            && RecordedNvidiaFanResources.Fields(core.Catalog).Length == 4 && fixture.CatalogEngineProbeCount == 1,
            "failed source clears selected values and preserves definitions");
        foreach (var reading in failed) Require(core.ReadCached(reading.Field) == reading, "failed current has no last-good values");
        fixture.Fail = false;
        RecordedNvidiaFanResources.RequireValues(await core.CollectAsync(fields, timeout.Token), fixture.Fans);
        Require(fixture.UnselectedReads == 0 && fixture.CatalogEngineProbeCount == 1,
            "fan selection never repeats the directory probe or queries other NVML/GPU/WMI/OEM sources");
        Console.WriteLine("[NVIDIA-FAN-PACKAGE-RECORDED] two-selectors; percent=135/0; rpm-optional; selected-only; same-current; failure-clears; cancellation/retirement; no-live-capture.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException("NVIDIA fan recorded consumer: " + message); }
    private sealed class RetiredPermit(ulong workspace, ulong generation) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => workspace;
        public ulong ConfigurationGeneration => generation;
        public bool IsActive => false;
        public bool TryPublish(Action commit) => false;
    }
}

internal sealed class RecordedNvidiaFanResources
{
    internal const string PercentName = "gpu_nvidia_fan_speed_percent_of_max";
    internal const string RpmName = "gpu_nvidia_fan_speed_rpm";
    private const string Parent = @"PCI\VEN_10DE&DEV_2684\RECORDED-FAN-GPU";
    internal NvidiaNvmlFanIdentity[] Fans { get; } = [new(Parent, 0), new(Parent, 1)];
    internal int Reads { get; private set; }
    internal int DirectoryReads { get; private set; }
    internal int CatalogEngineProbeCount { get; private set; }
    internal int UnselectedReads { get; private set; }
    internal bool Fail { get; set; }
    internal NvidiaNvmlFanReadTarget[] LastTargets { get; private set; } = [];

    internal WindowsHardwareMetricResources Create() => new()
    {
        CpuName = "Recorded CPU", MemoryDescription = "Recorded memory",
        Controls = new WindowsMetricControlResources
        {
            ControlReadCpuIdentity = static () => null,
            ControlReadCpuTopology = static () => new CpuOsTopologyInformation(null, null)
        },
        ReadCpuCounters = () => throw Unexpected(),
        ReadMemory = () => throw Unexpected(), ReadVirtualMemory = () => throw Unexpected(),
        ReadNumaAvailable = (_, _) => throw Unexpected(),
        ReadNumaIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadGpuInventory = Inventory,
        ReadCpuFrequency = () => throw Unexpected(),
        ReadGpuUsage = _ => { CatalogEngineProbeCount++; return PdhGpuEngineUsageRead.NotRequested; },
        ReadNvmlAsync = (_, _, _) => throw Unexpected(), ReadNvapiAsync = (_, _, _) => throw Unexpected(),
        ReadAdlxAsync = (_, _, _) => throw Unexpected(), ReadAmdSmu = _ => throw Unexpected(),
        ReadIntelGpu = (_, _, _) => throw Unexpected(), ReadIntelMsr = (_, _) => throw Unexpected(),
        ReadBatteryIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadBattery = (_, _) => throw Unexpected(),
        ReadNetworkIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNetworkInterfaces = (_, _) => throw Unexpected(), ReadSystemIo = _ => throw Unexpected(),
        ReadStorageIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadFanIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadStorageSensors = () => throw Unexpected(), ReadNvmeSensors = _ => throw Unexpected(),
        ReadAtaSensors = _ => throw Unexpected(), ReadHardwareMonitor = () => throw Unexpected(),
        ReadNotebookFans = () => throw Unexpected(), SynchronizeNotebookDemand = static _ => { },
        ReadNvmlFanIdentities = _ => { DirectoryReads++; return Fans; },
        ReadNvmlFansAsync = (targets, _, token) =>
        {
            token.ThrowIfCancellationRequested(); Reads++; LastTargets = targets.ToArray();
            if (targets.Any(target => !Fans.Contains(target.Fan))) throw new InvalidDataException("Unexpected exact fan target.");
            return Task.FromResult<IReadOnlyList<NvidiaNvmlFanReading>>(targets.Select(target => new NvidiaNvmlFanReading(target.Fan,
                Fail || !target.IncludePercent ? null : target.Fan.FanIndex == 0 ? 135u : 0u,
                Fail || !target.IncludeRpm || target.Fan.FanIndex == 1 ? null : 2100u)).ToArray());
        }
    };

    private Exception Unexpected()
    { UnselectedReads++; return new InvalidOperationException("Unselected recorded source was queried."); }
    private static WindowsGpuAdapterInventoryRead Inventory()
    {
        var physical = new WindowsGpuPhysicalIdentity(0, Parent, WindowsGpuPnpKind.Pci, true, true, 0, 1, 0, 0,
            0x10DE, 0x2684, 0x10DE, 1, 1,
            WindowsGpuIdentityEvidence.KmtHardwarePnpKey | WindowsGpuIdentityEvidence.CmExactDevNode,
            WindowsGpuPhysicalIdentityStatus.Bound, 0);
        WindowsGpuAdapter[] adapters = [new(0, "Recorded GPU", 0x10DE, 0x2684, 1,
            new AdapterLuid { LowPart = 1, HighPart = 8 }, false, 8UL * 1024 * 1024 * 1024, WindowsGpuAdapterKind.Dedicated)
            { PhysicalIdentityStatus = SamplingObservationStatus.Current, PhysicalIdentities = [physical] }];
        return new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 1, 0, 0,
            WindowsGpuAdapterIdentity.ComputeTopologyFingerprint(adapters), adapters);
    }
    internal static ImmutableArray<FieldReference> Fields(ReadWriteCatalog catalog)
        => catalog.Fields.Where(field => field.Field.FieldName is PercentName or RpmName)
            .Select(field => field.Field).OrderBy(field => field.DeviceIdentity, StringComparer.Ordinal)
            .ThenBy(field => field.FieldName, StringComparer.Ordinal).ToImmutableArray();
    internal static void RequireValues(ImmutableArray<FieldReading> readings, IReadOnlyList<NvidiaNvmlFanIdentity> fans)
    {
        foreach (var reading in readings)
        {
            var fan = fans.Single(fan => fan.ExactIdentityKey == reading.Field.DeviceIdentity);
            double? expected = reading.Field.FieldName == PercentName ? fan.FanIndex == 0 ? 135 : 0
                : fan.FanIndex == 0 ? 2100 : null;
            if (reading.SourceId != NativeMetricSnapshotSources.NvidiaNvml
                || (expected is null ? reading is not { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
                    : reading.Status != FieldObservationStatus.Current || reading.Value?.Number != expected || reading.ObservedAt is null))
                throw new InvalidDataException("Recorded NVML fan intended speed/optional RPM contract failed.");
        }
    }
}
