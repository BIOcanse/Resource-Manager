using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.ReadWrite.Windows.Pdh;

internal static class IntelFanConsumer
{
    internal static async Task RunRecordedAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = new RecordedIntelFanResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(), fixture.Create(), new(),
            modeProvider: fixture.Mode, unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Require(core.Catalog.Fields.IsEmpty && fixture.Reads == 0 && fixture.DirectoryReads == 0, "passive construction");
        var catalog = await core.DiscoverAsync(timeout.Token);
        var fields = Fields(catalog);
        Require(fields.Length == 2 && catalog.Devices.Count(device => device.Resource == "gpu_fan" && device.Vendor == "intel") == 2,
            "two independent Intel GPU fan objects");
        foreach (var field in fields)
        {
            var capability = core.Query(field);
            Require(capability.Read.Supported && !capability.Write.Supported && capability.ReadSource == NativeMetricSnapshotSources.IntelGcl
                && capability.Definition.Unit == "RPM" && capability.Definition.ValueKind == "float64", "source/unit/kind/read-only");
        }
        Require(fixture.Reads == 0, "directory discovery never samples RPM");
        var readings = await core.CollectAsync(fields, timeout.Token);
        Require(readings.Length == 2 && readings.All(reading => reading.Status == FieldObservationStatus.Current && reading.ObservedAt is not null), "real committed frame");
        Require(readings.Single(reading => reading.Field.DeviceIdentity == fixture.Fans[0].ExactIdentityKey).Value?.Number == 0
            && readings.Single(reading => reading.Field.DeviceIdentity == fixture.Fans[1].ExactIdentityKey).Value?.Number == 1450, "zero and positive RPM");
        var current = owner.ReadCachedSnapshot();
        foreach (var reading in readings) Require(core.ReadCached(reading.Field) == reading, "same current");
        Require(ReferenceEquals(current, owner.ReadCachedSnapshot()) && fixture.Reads == 1 && fixture.UnselectedReads == 0, "passive cache and selected source only");
        var selected = fields.Single(field => field.DeviceIdentity == fixture.Fans[1].ExactIdentityKey);
        await core.CollectAsync([selected], timeout.Token);
        Require(fixture.LastTargets is [var fan] && ReferenceEquals(fan, fixture.Fans[1]) && fixture.LastMaximum == 16, "typed target and operation template budget");
        using (var borrow = owner.BorrowSubscriptionCapture())
        {
            borrow.Bind([selected]); borrow.SynchronizeDemand([selected]);
            var before = owner.ReadCachedSnapshot()!;
            Require(!(await borrow.CollectAsync([selected], new RetiredPermit(before.WorkspaceIdentity, before.ConfigurationGeneration), timeout.Token)).Committed
                && ReferenceEquals(before, owner.ReadCachedSnapshot()), "retired permit cannot publish");
        }
        var beforeCancellation = owner.ReadCachedSnapshot();
        using (var cancellation = new CancellationTokenSource())
        {
            fixture.OnRead = cancellation.Cancel;
            try { await core.CollectAsync([selected], cancellation.Token); throw new InvalidDataException("Cancellation published."); }
            catch (OperationCanceledException) { }
            finally { fixture.OnRead = null; }
        }
        Require(ReferenceEquals(beforeCancellation, owner.ReadCachedSnapshot()), "in-flight cancellation preserves current");
        fixture.Fail = true;
        var failed = await core.CollectAsync(fields, timeout.Token);
        Require(failed.All(reading => reading is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null })
            && Fields(core.Catalog).Length == 2, "failure is null with declarations retained");
        fixture.Fail = false;
        var beforeProbe = owner.ReadCachedSnapshot()!;
        var repeated = Fields(await core.DiscoverAsync(timeout.Token));
        Require(repeated.SequenceEqual(fields) && owner.ReadCachedSnapshot()!.WorkspaceIdentity == beforeProbe.WorkspaceIdentity,
            "unchanged recorded directory keeps field references and workspace");
        var directoryReads = fixture.DirectoryReads;
        fixture.Frozen = true;
        await core.DiscoverAsync(timeout.Token);
        Require(fixture.DirectoryReads == directoryReads, "frozen source does not reopen/probe directory");
        Console.WriteLine("[INTEL-FAN-PACKAGE-RECORDED] gpu_fan; RPM=0/1450; exact-session-targets; selected-only; same-current; null-failure; cancellation/retirement; stable-catalog-probe; no-live-capture.");
    }
    internal static ImmutableArray<FieldReference> Fields(ReadWriteCatalog catalog)
        => catalog.Fields.Where(field => field.Field.FieldName == RecordedIntelFanResources.Name)
            .Select(field => field.Field).OrderBy(field => field.DeviceIdentity, StringComparer.Ordinal).ToImmutableArray();
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException("Intel fan recorded consumer: " + message); }
    private sealed class RetiredPermit(ulong workspace, ulong generation) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => workspace;
        public ulong ConfigurationGeneration => generation;
        public bool IsActive => false;
        public bool TryPublish(Action commit) => false;
    }
}

internal sealed class RecordedIntelFanResources
{
    internal const string Parent = @"PCI\VEN_8086&DEV_56A0\9";
    internal const string Name = "gpu_intel_fan_speed_rpm";
    internal IntelGclFanIdentity[] Fans = [new(Parent, Guid.Parse("3bb148e3-95f3-458a-b25f-26432699fe64"), 0), new(Parent, Guid.Parse("3bb148e3-95f3-458a-b25f-26432699fe64"), 1)];
    internal int Reads, DirectoryReads, UnselectedReads;
    internal bool Fail = false, Frozen = false, DirectoryUnavailable = false;
    internal uint LastMaximum;
    internal IntelGclFanIdentity[] LastTargets = [];
    internal Action? OnRead;
    internal Func<WindowsGpuAdapterInventoryRead, uint, IntelGclFanDirectoryLease?, IntelFanTopologyRead>? OpenOverride = null;
    internal Func<IReadOnlyList<IntelGclFanIdentity>, WindowsGpuAdapterInventoryRead, uint, CancellationToken, IReadOnlyList<IntelGclFanReading>>? ReadOverride = null;
    internal NativeMetricSnapshotZoneMode Mode(string source)
        => Frozen && source == NativeMetricSnapshotSources.IntelGcl ? NativeMetricSnapshotZoneMode.Freeze : NativeMetricSnapshotZoneMode.Normal;
    internal WindowsHardwareMetricResources Create() => new()
    {
        CpuName = "Recorded CPU", MemoryDescription = "Recorded memory",
        Controls = new WindowsMetricControlResources { ControlReadCpuIdentity = static () => null,
            ControlReadCpuTopology = static () => new CpuOsTopologyInformation(null, null) },
        ReadCpuCounters = () => throw Unexpected(), ReadMemory = () => throw Unexpected(), ReadVirtualMemory = () => throw Unexpected(),
        ReadNumaAvailable = (_, _) => throw Unexpected(), ReadNumaIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadGpuInventory = Inventory, ReadCpuFrequency = () => throw Unexpected(), ReadGpuUsage = _ => PdhGpuEngineUsageRead.NotRequested,
        ReadNvmlAsync = (_, _, _) => throw Unexpected(), ReadNvapiAsync = (_, _, _) => throw Unexpected(), ReadAdlxAsync = (_, _, _) => throw Unexpected(),
        ReadAmdSmu = _ => throw Unexpected(), ReadIntelGpu = (_, _, _) => throw Unexpected(), ReadIntelMsr = (_, _) => throw Unexpected(),
        ReadBatteryIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadBattery = (_, _) => throw Unexpected(),
        ReadNetworkIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadNetworkInterfaces = (_, _) => throw Unexpected(),
        ReadSystemIo = _ => throw Unexpected(), ReadStorageIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadFanIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadStorageSensors = () => throw Unexpected(),
        ReadNvmeSensors = _ => throw Unexpected(), ReadAtaSensors = _ => throw Unexpected(), ReadHardwareMonitor = () => throw Unexpected(),
        ReadNotebookFans = () => throw Unexpected(), SynchronizeNotebookDemand = static _ => { }, ReadNvmlFanIdentities = static _ => [],
        OpenIgclFanDirectory = (inventory, maximum, prior) =>
        {
            DirectoryReads++;
            if (OpenOverride is not null) return OpenOverride(inventory, maximum, prior);
            return new(DirectoryUnavailable || Fans.Length > maximum ? NativeMetricSnapshotCatalogTopologyStatus.Unavailable : NativeMetricSnapshotCatalogTopologyStatus.Complete,
                DirectoryUnavailable || Fans.Length > maximum ? [] : Fans);
        },
        ReadIgclFans = (targets, inventory, maximum, token) =>
        {
            token.ThrowIfCancellationRequested(); Reads++; LastMaximum = maximum; LastTargets = targets.ToArray(); OnRead?.Invoke();
            if (ReadOverride is not null) return ReadOverride(targets, inventory, maximum, token);
            return targets.Select(target => new IntelGclFanReading(target, Fail ? null : target.FanIndex == 0 ? 0 : 1450)).ToArray();
        }
    };
    private Exception Unexpected() { UnselectedReads++; return new InvalidOperationException("An unselected source was queried."); }
    internal static WindowsGpuAdapterInventoryRead Inventory() => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks,
        1, 0, 0, 1, [new WindowsGpuAdapter(9, "Intel", 0x8086, 0x56A0, 0, new AdapterLuid { LowPart = 7 }, false, 8UL << 30, WindowsGpuAdapterKind.Dedicated)
        { PhysicalIdentityStatus = SamplingObservationStatus.Current,
          PhysicalIdentities = [new(0, Parent, WindowsGpuPnpKind.Pci, true, false, 0, 9, 0, 0, 0x8086, 0x56A0, 0, 0, 1,
            WindowsGpuIdentityEvidence.KmtLuidOpen | WindowsGpuIdentityEvidence.KmtHardwarePnpKey | WindowsGpuIdentityEvidence.CmExactDevNode
            | WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip | WindowsGpuIdentityEvidence.KmtDeviceIds | WindowsGpuIdentityEvidence.CmPciAddress,
            WindowsGpuPhysicalIdentityStatus.Bound, 0)] }]);
}
