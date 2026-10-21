using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NvidiaDrsGlobalCatalogTests
{
    [Fact]
    public async Task OrdinaryOwnerCatalogExcludesDrsWhileScopedAccessAndBorrowStillUseOriginalCurrent()
    {
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var global = owner.CreateBackend();
        var scope = new NvidiaDrsInformationRequest([new("Game", new uint[] { 7 })], new(1, 1, 1, 1, 20_000));
        var calls = 0;
        var scoped = owner.CreateNvidiaDrsInformation(scope, (_, _, _) =>
        { calls++; return new([new("Game", 7, 0, 0, 1, [new(7, "setting", 0, 0, 0, 1, new NvidiaDrsDwordValue(0))])]); });
        _ = await scoped.Backend.CollectAsync(scope.Fields, default);
        Assert.NotNull(owner.ReadState().InformationFields.FindField(scope.Fields[0]));
        Assert.Single(scoped.Backend.Catalog.Fields);
        Assert.DoesNotContain(global.Catalog.Fields, capability => capability.Field == scope.Fields[0]);
        Assert.Throws<ArgumentException>(() => global.ReadCached(scope.Fields[0]));
        await Assert.ThrowsAsync<ArgumentException>(() => global.CollectAsync(scope.Fields, default).AsTask());
        Assert.Equal(1, calls);
        using var borrow = scoped.BorrowSubscriptionCapture();
        var result = await borrow.CollectAsync(scope.Fields, new Permit());
        Assert.True(result.Committed); Assert.Equal(2, calls);
        Assert.Equal<FieldReference>(scope.Fields, result.QueriedFields);
    }

    [Fact]
    public void DefinitionFilterIsFixedAndCatalogStillCachesTheOriginalSnapshotReference()
    {
        var normal = DeviceInformationFieldRegistry.CpuAllSocketDesignation;
        var drs = DeviceInformationFieldRegistry.GpuNvidiaDriverProfileSettingValue;
        var snapshot = new DeviceInformationSnapshot(DateTimeOffset.UtcNow,
            [new("cpu", "cpu", [new(normal, JsonSerializer.SerializeToElement("socket"))]),
             new("drs", "gpu_driver_profile_setting", [new(drs, JsonSerializer.SerializeToElement(new { currentValue = 0 }))])]);
        var filters = 0; var sources = 0;
        var backend = new InformationReadWriteBackend(() => snapshot,
            _ => { sources++; return ValueTask.FromResult(snapshot); }, static _ => new(true, null, []), null,
            definition => { filters++; return definition != drs; });
        Assert.Single(backend.Catalog.Fields); Assert.Single(backend.Catalog.Devices);
        Assert.Equal("cpu", backend.Catalog.Devices[0].Identity);
        var firstFilters = filters;
        _ = backend.Catalog; _ = backend.ReadCached(new("cpu", normal.Name));
        Assert.Equal(firstFilters, filters); Assert.Equal(0, sources);
        Assert.Throws<ArgumentException>(() => backend.Select([new("drs", drs.Name)]));
    }

    private sealed class Permit : ResourceManager.App.Infrastructure.Monitoring.IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action publish) { publish(); return true; }
    }
}
