using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Native;

namespace Resource_Manager_APP.Tests;

public sealed class MetricWorkspaceLibraryIntegrationTests
{
    [Fact]
    public void NativeOwnerCatalogIdentityAndProjectionsBelongToIndependentLibrary()
    {
        var assembly = typeof(ReadWriteCore).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "ResourceManager"
            || reference.Name!.StartsWith("ResourceManager.Ledger", StringComparison.Ordinal));
        foreach (var type in new[] { typeof(NativeMetricSnapshotWorkspace), typeof(NativeMetricSnapshotCatalogProjection),
            typeof(NativeMetricSnapshotRequestProjection), typeof(NativeMetricSnapshotObservationProjection),
            typeof(NativeMetricSnapshotCommittedProjection), typeof(NativeMetricSnapshotCatalogHandleMap),
            typeof(NativeMetricSnapshotPersistenceImage), typeof(NativeMetricSnapshotCatalogConfiguration) })
            Assert.Equal(assembly, type.Assembly);
        var constructor = Assert.Single(typeof(NativeMetricSnapshotWorkspace).GetConstructors());
        Assert.Equal(typeof(NativeMetricSnapshotConfiguration), constructor.GetParameters()[0].ParameterType);
        Assert.DoesNotContain(constructor.GetParameters(), parameter => parameter.ParameterType.FullName!.Contains("CompiledHostManager"));
    }

    [Fact]
    public void SharedCatalogExpandsCanonicalFieldsAndRequestsSupportFromTheOriginalRegistry()
    {
        var first = NativeMetricSnapshotCatalogProjection.Create(Catalog(), [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty);
        var second = NativeMetricSnapshotCatalogProjection.Create(Catalog(), [], [], [], first.HandleMap);
        Assert.Equal(first.CatalogIdentitySha256, second.CatalogIdentitySha256);
        Assert.Equal(first.NativeRowFingerprint, second.NativeRowFingerprint);
        Assert.Equal<NativeMetricSnapshotCatalogHandleEntry>(first.HandleMap.Entries, second.HandleMap.Entries);
        var requested = NativeMetricSnapshotRequestProjection.CreateMetricHandles(MetricSampleRequest.ForIds(["memory.usage"]), first);
        Assert.Equal(2, requested.Length);
        Assert.Contains(requested, handle => first.MetricBindings[handle].FieldCode == MetricFieldCode.MemoryUsed);
        Assert.Contains(requested, handle => first.MetricBindings[handle].FieldCode == MetricFieldCode.MemoryTotal);
        Assert.All(first.MetricBindings.Values, binding => Assert.Equal("B", binding.Field!.Unit));
        Assert.Empty(NativeMetricSnapshotRequestProjection.CreateMetricHandles(MetricSampleRequest.CatalogProbe, first));
    }

    [Fact]
    public void ExplicitPersistenceCallbacksAndDisposedOwnerPreserveNativeLifetimeContract()
    {
        var loads = 0; var saves = 0; var discards = 0;
        using var owner = new NativeMetricSnapshotWorkspace(Configuration(), Catalog(), new(
            (generation, manifest, capacity) => { loads++; Assert.Equal(1UL, generation); Assert.Equal(Catalog().CatalogManifestSha256, manifest); return null; },
            (_, _) => saves++, () => discards++));
        Assert.Equal(1, loads);
        var catalog = NativeMetricSnapshotCatalogProjection.Create(Catalog(), [], [], [], owner.CreateCatalogHandleMap());
        owner.ReplaceCatalog(catalog, DateTimeOffset.FromUnixTimeMilliseconds(1_000));
        var first = owner.ReadCommitted();
        var second = owner.ReadCommitted();
        Assert.Equal(first.Header.StateRevision, second.Header.StateRevision);
        var exported = owner.ExportAndPersist(DateTimeOffset.FromUnixTimeMilliseconds(1_000));
        Assert.Equal(catalog.CatalogIdentitySha256, exported.Catalog.CatalogIdentitySha256);
        Assert.Equal(1, saves); Assert.Equal(0, discards);
        owner.Dispose(); owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owner.ReadHeader());
        Assert.Throws<ObjectDisposedException>(() => owner.ReadCatalog());
    }

    private static NativeMetricSnapshotCatalogConfiguration Catalog() => new(
        1, new string('A', 64), 8, 64, 32,
        [new(NativeMetricSnapshotSources.WindowsMemory, 1, 1, 100, 2, true, 1, 11),
         new(NativeMetricSnapshotSources.WindowsGpuAdapterOrder, 2, 2, 100, 2, true, 1, 12)],
        [Rule("memory.total", "memory.total", NativeMetricSnapshotMetricKind.MemoryTotal, NativeMetricSnapshotMetricFlags.TotalValue),
         Rule("memory.used", "memory.usage", NativeMetricSnapshotMetricKind.MemoryUsed, NativeMetricSnapshotMetricFlags.UsedValue)]);

    private static NativeMetricSnapshotRuleTemplate Rule(string id, string metric,
        NativeMetricSnapshotMetricKind kind, NativeMetricSnapshotMetricFlags flags) => new(
        id, metric, NativeMetricSnapshotSources.WindowsMemory, 1, 100, 3, (uint)kind,
        (uint)NativeMetricSnapshotScopeKind.Memory, (uint)NativeMetricSnapshotValueKind.Unsigned64,
        (uint)NativeMetricSnapshotRetentionPolicy.MarkUnavailable, 1, (uint)(flags | NativeMetricSnapshotMetricFlags.Nonnegative),
        0, ulong.MaxValue, 0, 0, 1, 22);

    private static NativeMetricSnapshotConfiguration Configuration() => new()
    {
        AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotConfiguration>(), Generation = 1,
        MaximumSourceCount = 8, MaximumMetricCount = 32, MaximumRuleCount = 64,
        MaximumRequestedCount = 32, MaximumObservationCount = 64, MaximumGpuAdapterCount = 8,
        MaximumPersistenceSourceCount = 8, MaximumPersistenceRuleCount = 64, MaximumPersistenceGpuCount = 8,
        SourceIndexCapacity = 16, MetricIndexCapacity = 64, RuleIndexCapacity = 128, GpuIndexCapacity = 16,
        MaximumFutureSkewMilliseconds = 30_000, ResidentByteBudget = 4 * 1024 * 1024,
        CatalogContractVersion = NativeMetricSnapshotAbi.CatalogContractVersion,
        ValueContractVersion = NativeMetricSnapshotAbi.ValueContractVersion,
        ObservationContractVersion = NativeMetricSnapshotAbi.ObservationContractVersion,
        InventoryContractVersion = NativeMetricSnapshotAbi.InventoryContractVersion,
        PersistenceContractVersion = NativeMetricSnapshotAbi.PersistenceContractVersion,
        MaximumPlanMetricCount = 32, MaximumSourceModeCount = 8, MaximumSourcePlanCount = 8, MaximumMetricPlanCount = 64,
        GpuLuidIndexCapacity = 16, GpuKeyIndexCapacity = 16
    };
    private static uint Size<T>() where T : unmanaged => checked((uint)Unsafe.SizeOf<T>());
}
