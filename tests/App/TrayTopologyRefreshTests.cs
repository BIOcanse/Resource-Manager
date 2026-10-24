using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using static Resource_Manager_APP.Tests.MetricSnapshotOwnerTestFixture;

namespace Resource_Manager_APP.Tests;

public sealed class TrayTopologyRefreshTests
{
    [Theory]
    [InlineData(NativeMetricSnapshotSources.WindowsStorageSensors)]
    [InlineData(NativeMetricSnapshotSources.WindowsNvmeHealth)]
    [InlineData(NativeMetricSnapshotSources.WindowsAtaSmart)]
    [InlineData(NativeMetricSnapshotSources.WindowsDiskPerformance)]
    [InlineData(NativeMetricSnapshotSources.WindowsGpuAdapterOrder)]
    [InlineData(NativeMetricSnapshotSources.WindowsNetworkInterface)]
    [InlineData(NativeMetricSnapshotSources.WindowsBattery)]
    [InlineData(NativeMetricSnapshotSources.IntelMsr)]
    [InlineData(NativeMetricSnapshotSources.WindowsNpuEngine)]
    [InlineData(NativeMetricSnapshotSources.WindowsNpuMemory)]
    public void PersistentDisagreementRequestsOnceForUnchangedCatalogAndLogsStartAndStop(string source)
    {
        using var fixture = new Fixture(Topology([]));
        using var original = fixture.Owner.Acquire();
        var identity = original.WorkspaceIdentity;
        var messages = new List<string>();
        var requests = 0;
        var tracker = new TopologyRefreshRequestTracker(() => { requests++; fixture.Owner.RequestTopologyRefresh(); }, messages.Add);
        tracker.Observe(identity, source, "identity-changed", true);
        Assert.NotNull(fixture.Owner.RefreshTopologyIfRequested());
        Assert.Equal(original.WorkspaceIdentity, fixture.Owner.CurrentWorkspaceIdentity);
        var reads = fixture.TopologyReads;
        for (var i = 0; i < 50; i++)
        {
            tracker.Observe(identity, source, "identity-changed", true);
            Assert.Null(fixture.Owner.RefreshTopologyIfRequested());
        }
        Assert.Equal(1, requests); Assert.Equal(reads, fixture.TopologyReads);
        Assert.Equal($"Hardware sampling started requesting topology refresh from {source}: identity-changed.", Assert.Single(messages));
        tracker.Observe(identity, source, "identity-changed", false);
        tracker.Observe(identity, source, "identity-changed", false);
        Assert.Equal(2, messages.Count);
        Assert.Equal($"Hardware sampling stopped requesting topology refresh from {source}: identity-changed.", messages[1]);
        tracker.Observe(identity, source, "identity-changed", true);
        Assert.Equal(2, requests);
        tracker.Observe(identity + 1, source, "identity-changed", true);
        tracker.Observe(identity + 1, source, "identity-changed", true);
        Assert.Equal(3, requests);
    }

    [Fact]
    public void FailedCaptureSettlesTheRequestUntilNewExplicitEvidence()
    {
        using var fixture = new Fixture(Topology([]));
        fixture.TopologyFailure = new IOException("injected topology capture failure");
        fixture.Owner.RequestTopologyRefresh();
        Assert.NotNull(fixture.Owner.RefreshTopologyIfRequested());
        var reads = fixture.TopologyReads;
        for (var i = 0; i < 20; i++) Assert.Null(fixture.Owner.RefreshTopologyIfRequested());
        Assert.Equal(reads, fixture.TopologyReads);
        fixture.TopologyFailure = null;
        fixture.Owner.RequestTopologyRefresh();
        Assert.NotNull(fixture.Owner.RefreshTopologyIfRequested());
        Assert.Equal(reads + 1, fixture.TopologyReads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StorageCapabilityMetadataCannotRemainStaleWhenNativeRowsMatch(bool nvme)
    {
        using var fixture = new Fixture(Topology([new(2, "disk", 1)]));
        using var lease = fixture.Owner.Acquire();
        var prior = lease.Workspace.ReadCatalog();
        var disk = prior.StorageIdentities[0];
        var next = prior with { StorageIdentities = [disk with { SupportsNvmeHealth = nvme, SupportsAtaSmart = !nvme }] };
        Assert.Equal(prior.NativeRowFingerprint, next.NativeRowFingerprint);
        Assert.False(HostManagerMetricSnapshotOwner.CanRetainCatalog(prior, next));
        Assert.True(HostManagerMetricSnapshotOwner.CanRetainCatalog(next, next));
        var sensors = new StorageSensorSnapshot(new("recorded", HardwareSensorProviderStates.Active, null), [])
        { Identities = next.StorageIdentities };
        Assert.True(WindowsHardwareMetricCapture.MatchesStorageInventory(next, sensors));
        fixture.Topology = Topology(next.StorageIdentities);
        fixture.Owner.RequestTopologyRefresh();
        Assert.NotNull(fixture.Owner.RefreshTopologyIfRequested());
        using var accepted = fixture.Owner.Acquire();
        Assert.True(WindowsHardwareMetricCapture.MatchesStorageInventory(accepted.Workspace.ReadCatalog(), sensors));
        fixture.Owner.RequestTopologyRefresh();
        Assert.NotNull(fixture.Owner.RefreshTopologyIfRequested());
        Assert.Equal(accepted.WorkspaceIdentity, fixture.Owner.CurrentWorkspaceIdentity);
        Assert.True(WindowsHardwareMetricCapture.MatchesStorageInventory(accepted.Workspace.ReadCatalog(), sensors));
    }

    [Fact]
    public void BatteryRelativeUnitsCannotRemainStaleWhenNativeRowsMatch()
    {
        var battery = new BatteryCatalogIdentity(0, "battery", "unique", new(0, "LION", 100, 100, 1));
        using var fixture = new Fixture(Topology([]) with { BatteryStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete, Batteries = [battery] });
        using var lease = fixture.Owner.Acquire();
        var prior = lease.Workspace.ReadCatalog();
        var desired = prior with { BatteryIdentities = [battery with { Information = battery.Information with { Capabilities = 0x40000000 } }] };
        Assert.Equal(prior.NativeRowFingerprint, desired.NativeRowFingerprint);
        Assert.False(HostManagerMetricSnapshotOwner.CanRetainCatalog(prior, desired));
        Assert.True(HostManagerMetricSnapshotOwner.CanRetainCatalog(desired, desired));
        fixture.Topology = Topology([]) with { BatteryStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete, Batteries = desired.BatteryIdentities };
        fixture.Owner.RequestTopologyRefresh();
        Assert.NotNull(fixture.Owner.RefreshTopologyIfRequested());
        using var accepted = fixture.Owner.Acquire();
        Assert.True(Assert.Single(accepted.Workspace.ReadCatalog().BatteryIdentities).Information.IsRelative);
        fixture.Owner.RequestTopologyRefresh();
        Assert.NotNull(fixture.Owner.RefreshTopologyIfRequested());
        Assert.Equal(accepted.WorkspaceIdentity, fixture.Owner.CurrentWorkspaceIdentity);
        Assert.True(Assert.Single(accepted.Workspace.ReadCatalog().BatteryIdentities).Information.IsRelative);
    }

    [Fact]
    public void StorageCaseAndWhitespaceHaveTheSameOwnerProjectionForCatalogAndSensors()
    {
        IReadOnlyList<Dictionary<string, object?>> Rows(string unique)
            => [new() { ["DeviceId"] = 2, ["UniqueId"] = unique, ["SerialNumber"] = "serial", ["BusType"] = 7 }];
        var first = WindowsStorageOwner.ProjectIdentities(Rows(" {e22e0ed7-ce03-4869-a451-ded7ad18dbd9} "));
        var second = WindowsStorageOwner.ProjectIdentities(Rows("{E22E0ED7-CE03-4869-A451-DED7AD18DBD9}"));
        Assert.Equal(first[0].ExactIdentityKey, second[0].ExactIdentityKey);
        var topology = WindowsStorageSensorsMonitoringZone.ProjectIdentities(Rows("{E22E0ED7-CE03-4869-A451-DED7AD18DBD9}"), 1);
        using var fixture = new Fixture(Topology(topology));
        using var lease = fixture.Owner.Acquire();
        var sensors = ResourceManager.ReadWrite.Windows.Metrics.Resources.WindowsHardwareMetricResources.ProjectStorageSensors(
            new(new("recorded", HardwareSensorProviderStates.Active, null), []) { Identities = first });
        Assert.True(WindowsHardwareMetricCapture.MatchesStorageInventory(lease.Workspace.ReadCatalog(), sensors));
    }

    [Fact]
    public void GpuTopologyAndLiveCheckUseTheSameFilteringAndDisplayIdentity()
    {
        var physical = new WindowsGpuPhysicalIdentity(0, @"PCI\VEN_10DE&DEV_2D19\A",
            WindowsGpuPnpKind.Pci, true, true, 0, 1, 0, 0, 0x10DE, 0x2D19, 0x1462, 0x1234, 1,
            WindowsGpuIdentityEvidence.KmtLuidOpen, WindowsGpuPhysicalIdentityStatus.Bound, 0);
        var adapter = new WindowsGpuAdapter(0, "recorded", 0x10DE, 0x2D19, 0x1234,
            new() { LowPart = 23 }, false, 8UL * 1024 * 1024 * 1024, WindowsGpuAdapterKind.Dedicated)
            { PhysicalIdentityStatus = SamplingObservationStatus.Current, PhysicalIdentities = [physical] };
        var software = adapter with { Index = 1, IsSoftware = true, PhysicalIdentities = [] };
        WindowsGpuAdapterInventoryRead Read(params WindowsGpuAdapter[] adapters)
            => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, (uint)adapters.Length, 0, 0, 0, adapters);
        var captured = WindowsHardwareMetricTopologySource.CaptureGpuIdentities(Read(adapter, software));
        Assert.Null(captured.FailureCode); Assert.Single(captured.Rows);
        using var fixture = new Fixture(Topology([]) with { GpuAdapters = captured.Rows });
        using var lease = fixture.Owner.Acquire();
        var catalog = lease.Workspace.ReadCatalog();
        fixture.Owner.RequestTopologyRefresh();
        Assert.NotNull(fixture.Owner.RefreshTopologyIfRequested());
        Assert.Equal(lease.WorkspaceIdentity, fixture.Owner.CurrentWorkspaceIdentity);
        for (var i = 0; i < 20; i++) Assert.True(WindowsHardwareMetricCapture.MatchesExactGpuInventory(catalog, [adapter, software]));
        var moved = adapter with { Index = 3 };
        Assert.False(WindowsHardwareMetricCapture.MatchesExactGpuInventory(catalog, [moved, software]));
        fixture.Topology = Topology([]) with { GpuAdapters = WindowsHardwareMetricTopologySource.CaptureGpuIdentities(Read(moved, software)).Rows };
        fixture.Owner.RequestTopologyRefresh(); fixture.Owner.RefreshTopologyIfRequested();
        using var refreshed = fixture.Owner.Acquire();
        Assert.True(WindowsHardwareMetricCapture.MatchesExactGpuInventory(refreshed.Workspace.ReadCatalog(), [moved, software]));
    }
}
