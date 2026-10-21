using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaNvmlFanIntegrationTests
{
    [Fact]
    public void CatalogCarriesExactSelectorsAndKeepsThreeFanSourcesSeparate()
    {
        var fixture = new NvidiaFanFixture();
        var topology = fixture.Topology().Capture();
        var catalog = WindowsMetricCatalogTopologyPublication.ProjectCatalog(WindowsMetricConfiguration.Default.CatalogConfiguration,
            topology, NativeMetricSnapshotCatalogHandleMap.Empty);
        var fanBindings = catalog.RuleBindings.Values.Where(static binding => binding.NvmlFan is not null).ToArray();
        Assert.Equal(6, fanBindings.Length);
        Assert.Equal(3, fanBindings.Select(static binding => binding.ScopeHandle).Distinct().Count());
        Assert.All(fanBindings, binding =>
        {
            Assert.Equal(NativeMetricSnapshotSources.NvidiaNvml, binding.SourceId);
            Assert.Equal(binding.NvmlFan!.ExactIdentityKey, binding.DeviceIdentity);
            Assert.Equal(MetricResource.Fan, binding.Field!.Resource);
            Assert.Contains("NVML intended", binding.Field.Meaning);
        });
        Assert.All(catalog.RuleBindings.Values.Where(static binding => binding.SourceId == NativeMetricSnapshotSources.HardwareMonitorWmi
                && binding.Field?.Resource == MetricResource.Fan), binding => Assert.Equal("wmi-fan", binding.DeviceIdentity));
        Assert.All(catalog.RuleBindings.Values.Where(static binding => binding.SourceId == NativeMetricSnapshotSources.NotebookOemFan
                && binding.Field?.Resource == MetricResource.Fan), binding => Assert.Equal("oem-fan", binding.DeviceIdentity));
        Assert.DoesNotContain(fanBindings, static binding => binding.FieldCode is MetricFieldCode.GpuFanPercent or MetricFieldCode.GpuFanRpm);
        var templates = WindowsMetricConfiguration.Default.CatalogConfiguration.RuleTemplates
            .Where(static template => template.TemplateId.StartsWith("fan.nvml", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, templates.Length);
        Assert.All(templates, template =>
        {
            Assert.Equal((uint)NvidiaNvmlReader.MaxFanCount, template.MaximumInstanceCount);
            Assert.Equal(0U, template.MetricFlags & (uint)NativeMetricSnapshotMetricFlags.Percentage);
            Assert.Equal(double.MaxValue, BitConverter.UInt64BitsToDouble(template.MaximumValueBits));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownAndConfirmedEmptyRemainIndependentForWmiAndNvml(bool nvmlFails)
    {
        var fixture = new NvidiaFanFixture();
        var source = fixture.Topology();
        var first = source.Capture();
        var prior = WindowsMetricCatalogTopologyPublication.ProjectCatalog(WindowsMetricConfiguration.Default.CatalogConfiguration,
            first, NativeMetricSnapshotCatalogHandleMap.Empty);
        if (nvmlFails) fixture.Fans = null;
        else fixture.WmiStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
        var failed = source.Capture();
        var retained = WindowsMetricCatalogTopologyPublication.RetainUnavailableOptionalIdentities(failed, prior);
        Assert.Equal(5, retained.SystemFans.Count);
        Assert.Equal(3, retained.SystemFans.Count(static fan => fan.NvmlFan is not null));
        Assert.Single(retained.SystemFans.Where(static fan => fan.NotebookOemRole is not null));
        Assert.Single(retained.SystemFans.Where(static fan => fan.NvmlFan is null && fan.NotebookOemRole is null));
        if (nvmlFails) fixture.Fans = [];
        else { fixture.WmiStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete; fixture.IncludeWmi = false; }
        var empty = WindowsMetricCatalogTopologyPublication.RetainUnavailableOptionalIdentities(source.Capture(), prior);
        Assert.Equal(nvmlFails ? 0 : 3, empty.SystemFans.Count(static fan => fan.NvmlFan is not null));
        Assert.Equal(nvmlFails ? 1 : 0, empty.SystemFans.Count(static fan => fan.NvmlFan is null && fan.NotebookOemRole is null));
        Assert.Single(empty.SystemFans.Where(static fan => fan.NotebookOemRole is not null));
    }

    [Fact]
    public void ExcessiveInjectedDirectoryIsUnavailableWithoutPartialFansOrLossOfOtherSources()
    {
        var fixture = new NvidiaFanFixture();
        fixture.Fans = Enumerable.Range(0, NvidiaNvmlReader.MaxFanCount + 1)
            .Select(index => new NvidiaNvmlFanIdentity(NvidiaFanFixture.ParentA + index / NvidiaNvmlReader.MaxFansPerDevice,
                (uint)index % NvidiaNvmlReader.MaxFansPerDevice)).ToArray();
        var topology = fixture.Topology().Capture();
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, topology.NvmlFanStatus);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, topology.FanStatus);
        Assert.Equal(2, topology.SystemFans.Count);
        Assert.DoesNotContain(topology.SystemFans, static fan => fan.NvmlFan is not null);
    }

    [Fact]
    public void ThrowingLegacyDirectoryRetainsKnownOemAndWmiWhileNvmlCanChange()
    {
        var fixture = new NvidiaFanFixture();
        var source = fixture.Topology();
        var prior = WindowsMetricCatalogTopologyPublication.ProjectCatalog(WindowsMetricConfiguration.Default.CatalogConfiguration,
            source.Capture(), NativeMetricSnapshotCatalogHandleMap.Empty);
        fixture.ThrowWmi = true; fixture.Fans = [new(NvidiaFanFixture.ParentB, 0)];
        var failed = source.Capture();
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, failed.NotebookOemFanStatus);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, failed.NvmlFanStatus);
        var retained = WindowsMetricCatalogTopologyPublication.RetainUnavailableOptionalIdentities(failed, prior);
        Assert.Single(retained.SystemFans.Where(static fan => fan.NvmlFan is not null));
        Assert.Contains(retained.SystemFans, static fan => fan.ExactIdentityKey == "oem-fan");
        Assert.Contains(retained.SystemFans, static fan => fan.ExactIdentityKey == "wmi-fan");
        fixture.ThrowWmi = false; fixture.IncludeOem = false;
        var disabled = WindowsMetricCatalogTopologyPublication.RetainUnavailableOptionalIdentities(source.Capture(), prior);
        Assert.DoesNotContain(disabled.SystemFans, static fan => fan.NotebookOemRole is not null);
        Assert.Single(disabled.SystemFans.Where(static fan => fan.NvmlFan is not null));
    }

    [Fact]
    public async Task OriginalOwnerSelectsOnlyRequestedFanAndPreservesZeroOver100AndMissingRpm()
    {
        var fixture = new NvidiaFanFixture();
        using var owner = fixture.Owner();
        var backend = owner.CreateBackend();
        var catalog = await backend.DiscoverAsync(default);
        var fields = catalog.Fields.Where(static field => field.Field.FieldName is NvidiaFanFixture.Percent or NvidiaFanFixture.Rpm).ToArray();
        Assert.Equal(6, fields.Length);
        Assert.All(catalog.Devices.Where(device => fields.Any(field => field.Field.DeviceIdentity == device.Identity)),
            device => Assert.Equal("gpu_fan", device.Resource));
        Assert.Empty(fixture.Targets);
        var a0 = fields.Single(field => field.Field.DeviceIdentity == new NvidiaNvmlFanIdentity(NvidiaFanFixture.ParentA, 0).ExactIdentityKey
            && field.Field.FieldName == NvidiaFanFixture.Percent).Field;
        using var borrow = owner.BorrowSubscriptionCapture(); borrow.Bind([a0]); borrow.SynchronizeDemand([a0]);
        var result = await borrow.CollectAsync([a0], new NvidiaFanFixture.Permit());
        Assert.True(result.Committed);
        Assert.Equal(0d, Assert.Single(result.Readings).Value!.Number);
        var target = Assert.Single(Assert.Single(fixture.Targets));
        Assert.Equal(0U, target.Fan.FanIndex); Assert.Equal(NvidiaFanFixture.ParentA, target.Fan.GpuPnpInstanceId);
        Assert.True(target.IncludePercent); Assert.False(target.IncludeRpm);
        fixture.Targets.Clear();
        ImmutableArray<FieldReference> all = [.. fields.Select(static field => field.Field)];
        borrow.Bind(all); borrow.SynchronizeDemand(all);
        result = await borrow.CollectAsync(all, new NvidiaFanFixture.Permit());
        Assert.True(result.Committed); Assert.Equal(6, result.Readings.Length);
        Assert.Equal(3, Assert.Single(fixture.Targets).Count);
        Assert.All(fixture.Targets[0], static target => Assert.True(target.IncludePercent && target.IncludeRpm));
        Assert.Equal(105d, result.Readings.Single(reading => reading.Field.DeviceIdentity == new NvidiaNvmlFanIdentity(NvidiaFanFixture.ParentA, 1).ExactIdentityKey
            && reading.Field.FieldName == NvidiaFanFixture.Percent).Value!.Number);
        Assert.Null(result.Readings.Single(reading => reading.Field.DeviceIdentity == new NvidiaNvmlFanIdentity(NvidiaFanFixture.ParentA, 1).ExactIdentityKey
            && reading.Field.FieldName == NvidiaFanFixture.Rpm).Value);
        Assert.Equal(0d, result.Readings.Single(reading => reading.Field.DeviceIdentity == new NvidiaNvmlFanIdentity(NvidiaFanFixture.ParentB, 0).ExactIdentityKey
            && reading.Field.FieldName == NvidiaFanFixture.Rpm).Value!.Number);
        var count = fixture.Targets.Count;
        Assert.Equal(0d, backend.ReadCached(a0).Value!.Number);
        Assert.Equal(count, fixture.Targets.Count);
    }

    [Fact]
    public async Task RejectedCanceledAndRetiredBorrowCannotReplaceOriginalCurrent()
    {
        var fixture = new NvidiaFanFixture();
        using var owner = fixture.Owner();
        var catalog = await owner.CreateBackend().DiscoverAsync(default);
        var field = catalog.Fields.First(static field => field.Field.FieldName == NvidiaFanFixture.Percent).Field;
        using var borrow = owner.BorrowSubscriptionCapture(); borrow.Bind([field]);
        await borrow.CollectAsync([field], new NvidiaFanFixture.Permit());
        var current = owner.ReadCachedSnapshot();
        Assert.Same(current, owner.ReadCachedSnapshot());
        var result = await borrow.CollectAsync([field], new NvidiaFanFixture.Permit { Active = false });
        Assert.False(result.Committed); Assert.Same(current, owner.ReadCachedSnapshot());
        using var cancel = new CancellationTokenSource(); fixture.OnRead = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => borrow.CollectAsync([field], new NvidiaFanFixture.Permit(), cancel.Token).AsTask());
        Assert.Same(current, owner.ReadCachedSnapshot());
        fixture.OnRead = null; borrow.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => borrow.CollectAsync([field], new NvidiaFanFixture.Permit()).AsTask());
        Assert.Same(current, owner.ReadCachedSnapshot());
    }
}

internal sealed class NvidiaFanFixture
{
    internal const string ParentA = @"PCI\VEN_10DE&DEV_2484&SUBSYS_000110DE&REV_A1\A";
    internal const string ParentB = @"PCI\VEN_10DE&DEV_2484&SUBSYS_000110DE&REV_A1\B";
    internal const string Percent = "gpu_nvidia_fan_speed_percent_of_max";
    internal const string Rpm = "gpu_nvidia_fan_speed_rpm";
    internal IReadOnlyList<NvidiaNvmlFanIdentity>? Fans = [new(ParentA, 0), new(ParentA, 1), new(ParentB, 0)];
    internal NativeMetricSnapshotCatalogTopologyStatus WmiStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete;
    internal bool IncludeWmi = true;
    internal bool IncludeOem = true;
    internal bool ThrowWmi;
    internal Action? OnRead;
    internal List<IReadOnlyList<NvidiaNvmlFanReadTarget>> Targets { get; } = [];
    internal WindowsGpuAdapterInventoryRead Inventory { get; } = new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks,
        2, 0, 0, 1, [Adapter(9, 0xA1, ParentA), Adapter(3, 0xB1, ParentB)]);

    internal WindowsHardwareMetricTopologySource Topology() => new(new(() => Inventory, _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        Wmi, () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []))
        { ReadNvmlFanIdentities = _ => Fans }, () => null);

    internal NativeMetricSnapshotOrdinalTopologyRead Wmi(ulong generation)
    {
        if (ThrowWmi) throw new InvalidOperationException("recorded WMI directory failure");
        var rows = new List<NativeMetricSnapshotOrdinalCatalogIdentity>();
        if (IncludeWmi && WmiStatus == NativeMetricSnapshotCatalogTopologyStatus.Complete) rows.Add(new(0, "wmi-fan", generation));
        if (IncludeOem) rows.Add(new(rows.Count, "oem-fan", generation) { NotebookOemRole = NotebookOemPhysicalFanRole.Cpu });
        return new(WmiStatus, rows);
    }

    internal Task<IReadOnlyList<NvidiaNvmlFanReading>> Read(IReadOnlyList<NvidiaNvmlFanReadTarget> targets,
        WindowsGpuAdapterInventoryRead inventory, CancellationToken token)
    {
        Assert.Same(Inventory, inventory); Targets.Add(targets); OnRead?.Invoke(); token.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<NvidiaNvmlFanReading>>(targets.Select(target => new NvidiaNvmlFanReading(target.Fan,
            target.IncludePercent ? target.Fan.FanIndex == 1 ? 105U : target.Fan.GpuPnpInstanceId == ParentA ? 0U : 70U : null,
            target.IncludeRpm ? target.Fan.FanIndex == 1 ? null : target.Fan.GpuPnpInstanceId == ParentB ? 0U : 1400U : null)).ToArray());
    }

    internal WindowsHardwareMetricOwner Owner() => new(WindowsMetricConfiguration.Default, Resources(), new());
    internal WindowsHardwareMetricResources Resources() => new()
    {
        Controls = new() { ControlReadCpuTopology = () => null! },
        CpuName = "recorded", MemoryDescription = "recorded",
        ReadCpuCounters = () => throw new InvalidOperationException(), ReadMemory = () => throw new InvalidOperationException(),
        ReadVirtualMemory = () => throw new InvalidOperationException(), ReadNumaAvailable = (_, _) => new Dictionary<string, ulong>(),
        ReadNumaIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadGpuInventory = () => Inventory,
        ReadCpuFrequency = () => throw new InvalidOperationException(), ReadGpuUsage = _ => throw new InvalidOperationException(),
        ReadNvmlAsync = (_, _, _) => throw new InvalidOperationException("Unselected GPU telemetry was queried."),
        ReadNvmlFanIdentities = _ => Fans, ReadNvmlFansAsync = Read,
        ReadNvapiAsync = (_, _, _) => throw new InvalidOperationException(), ReadAdlxAsync = (_, _, _) => throw new InvalidOperationException(),
        ReadAmdSmu = _ => throw new InvalidOperationException(), ReadIntelGpu = (_, _, _) => throw new InvalidOperationException(),
        ReadIntelMsr = (_, _) => throw new InvalidOperationException(), ReadBatteryIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadBattery = (_, _) => throw new InvalidOperationException(), ReadNetworkIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNetworkInterfaces = (_, _) => throw new InvalidOperationException(), ReadSystemIo = _ => throw new InvalidOperationException(),
        ReadStorageIdentities = _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadFanIdentities = Wmi,
        ReadStorageSensors = () => throw new InvalidOperationException(), ReadNvmeSensors = _ => throw new InvalidOperationException(),
        ReadAtaSensors = _ => throw new InvalidOperationException(), ReadHardwareMonitor = () => throw new InvalidOperationException(),
        ReadNotebookFans = () => throw new InvalidOperationException(), SynchronizeNotebookDemand = _ => { }
    };

    internal static WindowsGpuAdapter Adapter(int index, uint luid, string parent) => new(index, "Same NVIDIA GPU", 0x10DE, 0x2484, 0,
        new() { LowPart = luid }, false, 8UL << 30, WindowsGpuAdapterKind.Dedicated)
    {
        PhysicalIdentityStatus = SamplingObservationStatus.Current,
        PhysicalIdentities = [new(0, parent, WindowsGpuPnpKind.Pci, true, false, 0, (uint)index, 0, 0, 0x10DE, 0x2484,
            0x10DE, 1, 0xA1, WindowsGpuIdentityEvidence.CmExactDevNode, WindowsGpuPhysicalIdentityStatus.Bound, 0)]
    };

    internal sealed class Permit : IMetricPublicationPermit
    {
        internal bool Active = true;
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => Active;
        public bool TryPublish(Action commit) { if (!Active) return false; commit(); return true; }
    }
}
