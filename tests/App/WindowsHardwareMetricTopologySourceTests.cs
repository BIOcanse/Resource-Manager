using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsHardwareMetricTopologySourceTests
{
    [Fact]
    public void PhysicalPnpCaseDoesNotChangeTopologyContent()
    {
        var identity = new WindowsGpuPhysicalIdentity(0, @"PCI\VEN_10DE&DEV_2D19\A",
            WindowsGpuPnpKind.Pci, true, true, 0, 1, 0, 0, 0x10DE, 0x2D19, 0x1462, 0x1234, 1,
            WindowsGpuIdentityEvidence.KmtLuidOpen, WindowsGpuPhysicalIdentityStatus.Bound, 0);
        var adapter = new WindowsGpuAdapter(0, "recorded", 0x10DE, 0x2D19, 0x1234,
            new() { LowPart = 23 }, false, 8192, WindowsGpuAdapterKind.Dedicated)
            { PhysicalIdentityStatus = SamplingObservationStatus.Current, PhysicalIdentities = [identity] };
        var pnp = identity.PnpInstanceId;
        var source = new WindowsHardwareMetricTopologySource(new(
            () => new(SamplingObservationStatus.Current, 1, 1, 1, 0, 0, 17,
                [adapter with { PhysicalIdentities = [identity with { PnpInstanceId = pnp }] }]),
            _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, [])) { ReadNvmlFanIdentities = _ => [] }, () => null);
        var initial = source.Capture();
        pnp = pnp.ToLowerInvariant();
        var recased = source.Capture();
        Assert.True(WindowsHardwareMetricTopologySource.SameContent(initial, recased));
        Assert.Equal(initial.Generation, recased.Generation);
        pnp += "-different-device";
        Assert.NotEqual(initial.Generation, source.Capture().Generation);
    }

    [Fact]
    public void FreshCpuMemberListsDoNotChangePublishedContentButMembershipChangesDo()
    {
        NativeMetricSnapshotCatalogTopologySnapshot Content(byte member)
            => new(1, NativeMetricSnapshotCatalogTopologyStatus.Complete, [],
                NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, [])
            {
                IntelCpuIdentities = [new(0, "intel-core", new(0, 0), 1, 0,
                    ResourceManager.ReadWrite.Vendors.Intel.Msr.IntelMsrTargetKind.PhysicalCore, true, true)
                    { ProcessorAddresses = [new(0, member)] }],
                AmdCcdIdentities = [new(new("amd-package", 25, 1, 1, [new(0, member)]), 0, 1)]
            };
        var first = Content(0);
        Assert.True(WindowsHardwareMetricTopologySource.SameContent(first, Content(0)));
        var changed = Content(1);
        Assert.False(WindowsHardwareMetricTopologySource.SameContent(first, changed with { IntelCpuIdentities = first.IntelCpuIdentities }));
        Assert.False(WindowsHardwareMetricTopologySource.SameContent(first, changed with { AmdCcdIdentities = first.AmdCcdIdentities }));
    }

    [Fact]
    public void TopologyOwnsContentGenerationAndGpuFailureDoesNotRenumberUnchangedOrdinals()
    {
        var inventoryGeneration = 41UL;
        var failGpu = true;
        var storageKey = "storage|one";
        var fanKey = "fan|one";
        WindowsGpuAdapterInventoryRead Inventory()
        {
            if (failGpu) return new(SamplingObservationStatus.Unavailable, 0, 0, 0, 0, 0, 0, []);
            var identity = new WindowsGpuPhysicalIdentity(0, @"PCI\VEN_10DE&DEV_2D19\A",
                WindowsGpuPnpKind.Pci, true, true, 0, 1, 0, 0, 0x10DE, 0x2D19, 0x1462, 0x1234, 1,
                WindowsGpuIdentityEvidence.KmtLuidOpen, WindowsGpuPhysicalIdentityStatus.Bound, 0);
            var adapter = new WindowsGpuAdapter(0, "recorded", 0x10DE, 0x2D19, 0x1234,
                new() { LowPart = 23 }, false, 8UL * 1024 * 1024 * 1024, WindowsGpuAdapterKind.Dedicated)
                { PhysicalIdentityStatus = SamplingObservationStatus.Current, PhysicalIdentities = [identity] };
            return new(SamplingObservationStatus.Current, inventoryGeneration, DateTimeOffset.UtcNow.UtcTicks, 1, 0, 0, 17, [adapter]);
        }
        var source = new WindowsHardwareMetricTopologySource(new(Inventory,
            generation => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, [new(0, storageKey, generation)]),
            generation => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, [new(0, fanKey, generation)]),
            () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []))
            { ReadNvmlFanIdentities = _ => [] }, () => null);
        var first = source.Capture();
        void AssertOrdinalPublications(NativeMetricSnapshotCatalogTopologySnapshot snapshot, ulong storage, ulong fan)
        {
            Assert.Equal(storage, snapshot.StorageGeneration);
            Assert.Equal(fan, snapshot.FanGeneration);
            var frame = WindowsHardwareMetricCapture.AddCatalogObservations(new(DateTimeOffset.UtcNow, HardwareMetricTestData.EmptyCpu, HardwareMetricTestData.EmptyMemory, HardwareMetricTestData.EmptyVirtualMemory, [], HardwareMetricTestData.EmptyGpuInventory, new Dictionary<string, MetricReadingValue>()), snapshot, DateTimeOffset.UtcNow);
            Assert.Equal(storage, frame.Datasets["storage.inventory"].SourceGeneration);
            Assert.Equal(fan, frame.Datasets["fan.inventory"].SourceGeneration);
            Assert.Equal(snapshot.Generation, frame.Datasets["system.metric.catalog"].SourceGeneration);
        }
        Assert.Equal(1UL, first.Generation);
        AssertOrdinalPublications(first, 1, 1);
        Assert.Equal(1UL, Assert.Single(first.StorageSensors).TopologyGeneration);
        Assert.Equal(1UL, Assert.Single(first.SystemFans).TopologyGeneration);
        Assert.Equal(first.Generation, source.Capture().Generation);
        NativeMetricSnapshotCatalogProjection Catalog(NativeMetricSnapshotCatalogTopologySnapshot snapshot)
            => WindowsMetricCatalogTopologyPublication.ProjectCatalog(WindowsMetricConfiguration.Default.CatalogConfiguration,
                snapshot, NativeMetricSnapshotCatalogHandleMap.Empty);
        var prior = Catalog(first); // Nonzero ordinal generations remain valid with no GPU inventory.

        failGpu = false;
        var recovered = source.Capture(0, 0, prior);
        Assert.Equal(2UL, recovered.Generation);
        AssertOrdinalPublications(recovered, 1, 1);
        Assert.Equal(41UL, Assert.Single(recovered.GpuAdapters).InventoryGeneration);
        Assert.Equal(first.StorageSensors, recovered.StorageSensors);
        Assert.Equal(first.SystemFans, recovered.SystemFans);
        prior = Catalog(recovered);
        Assert.Equal(recovered.Generation, source.Capture(0, 0, prior).Generation);

        failGpu = true;
        var failed = source.Capture(0, 0, prior);
        Assert.Equal(3UL, failed.Generation);
        AssertOrdinalPublications(failed, 1, 1);
        Assert.Equal(first.StorageSensors, failed.StorageSensors);
        Assert.Equal(first.SystemFans, failed.SystemFans);
        prior = Catalog(failed);
        failGpu = false;
        inventoryGeneration = 99;
        var recoveredAgain = source.Capture(0, 0, prior);
        Assert.Equal(4UL, recoveredAgain.Generation);
        AssertOrdinalPublications(recoveredAgain, 1, 1);
        Assert.Equal(99UL, Assert.Single(recoveredAgain.GpuAdapters).InventoryGeneration);
        Assert.Equal(first.StorageSensors, recoveredAgain.StorageSensors);
        Assert.Equal(first.SystemFans, recoveredAgain.SystemFans);

        storageKey = "storage|two";
        var storageChanged = source.Capture(0, 0, Catalog(recoveredAgain));
        Assert.Equal(5UL, storageChanged.Generation);
        AssertOrdinalPublications(storageChanged, 5, 1);
        Assert.Equal(5UL, Assert.Single(storageChanged.StorageSensors).TopologyGeneration);
        Assert.Equal(first.SystemFans, storageChanged.SystemFans);
        fanKey = "fan|two";
        var fanChanged = source.Capture(0, 0, Catalog(storageChanged));
        Assert.Equal(6UL, fanChanged.Generation);
        AssertOrdinalPublications(fanChanged, 5, 6);
        Assert.Equal(6UL, Assert.Single(fanChanged.SystemFans).TopologyGeneration);
        Assert.Equal(storageChanged.StorageSensors, fanChanged.StorageSensors);
    }
}
