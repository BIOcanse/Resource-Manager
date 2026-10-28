using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareBridgeCatalogStartupTests
{
    [Fact]
    public void WriteLayerUsesCatalogDeclarationWithoutReadingObjectsOrConsultingWriters()
    {
        var layer = new ControlWriteLayer(new DeclaredCatalog(), [], new Access());

        Assert.True(layer.UsesHardwareBridge("cpu:recorded", "cpu.power-limit"));
        Assert.False(layer.UsesHardwareBridge("cpu:recorded", "cpu.core-voltage-offset"));
        Assert.False(layer.UsesHardwareBridge("gpu:nvidia", "gpu.power-limit"));
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("apply")]
    [InlineData("set")]
    public async Task SavedAmdTargetWaitsForDeclarationThenQueuesAndStartsOfflineChild(string operation)
    {
        var catalogState = new HardwareMetricCatalogState();
        Assert.Null(catalogState.Catalog);
        using var bridge = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture, HardwareBridgeSessionTests.Options);
        HardwareCommandQueue? queue = null;
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, Func<IServiceProvider, IBusinessBlock>>
            { [BlockUids.HardwareBridgeProcessOwner] = _ => new HardwareBridgeRunningBlock(bridge, queue!) });
        await fixture.InitializeHostPermitsAsync();
        queue = new(fixture.Authority, bridge.Owner);
        await fixture.InitializeHostPermitsAsync();
        var reason = new BusinessBlockReason(BusinessBlockRequestKind.Configuration, "catalog-startup-test", "available", 0);
        await fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeAvailable).PublishAsync(true, reason);
        var catalog = new WindowsControlObjectCatalog(catalogState, [], new Access(), null!, null!, null!, null!);
        var classifier = new ControlWriteLayer(catalog, [], new Access());
        var layer = new OfflineLayer(classifier, bridge);
        var saved = new ControlDesiredState([new("cpu:recorded", [new("cpu.power-limit", Number: 20, Unit: "W")])]);
        var store = new Store { Value = saved };
        var plane = new ControlPlane(store, layer, catalog: catalogState, commands: queue);
        HardwareBridgeControlTestFacts.Bind(plane, fixture.Authority);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task applying = operation switch
        {
            "initialize" => plane.InitializeAsync(deadline.Token),
            "apply" => plane.ApplyDesiredStateAsync(saved, deadline.Token),
            _ => plane.SetObjectSettingsAsync("cpu:recorded", saved.Objects[0].Settings, deadline.Token)
        };
        try
        {
            Assert.False(applying.IsCompleted);
            Assert.Equal(0, layer.Writes);
            Assert.False(queue.HasPending);
            Assert.Null(bridge.ReadCurrentReadings());

            var declaration = WindowsMetricControlBindings.FindByName("cpu_amd_requested_stapm_power_limit_watts")!;
            var binding = new MetricDeviceFieldBinding("recorded-cpu", declaration.Definition, "recorded-amd-power", declaration.SourceId)
                { ControlTarget = new("recorded-cpu") { RawObjectId = "cpu:recorded" } };
            var published = new HardwareMetricCatalog(new("Recorded AMD CPU", 0, "recorded"), [],
                new Dictionary<string, HardwareMetricItemDescriptor>(),
                new Dictionary<string, MetricDeviceFieldBinding> { [binding.RequestAddress] = binding }, 1, 1, 1);
            catalogState.PublishCatalogProbe(published.CreateSnapshot());

            await applying.WaitAsync(deadline.Token);
            Assert.Equal(1, layer.Writes);
            Assert.Equal(ControlApplyStatuses.Applied, Assert.Single((await plane.ReadStateAsync(deadline.Token)).LastApply.Outcomes).Status);
            Assert.True(fixture.Authority.Read<bool>(StateItemUids.AppliedControlIntentsExist).Value);
            await fixture.WaitForStateAsync(BlockUids.HardwareBridgeProcessOwner, BusinessBlockState.Stopped);
            Assert.Null(bridge.ReadCurrentReadings());
        }
        finally
        {
            deadline.Cancel();
            queue.FailPending(new IOException("test complete"));
            await applying.ContinueWith(_ => { });
        }
    }

    [Theory]
    [InlineData("cpu_intel_requested_pl1_power_limit_watts", "cpu:recorded", "cpu.power-limit")]
    [InlineData("gpu_amd_requested_graphics_clock_offset_mhz", "gpu:recorded", "gpu.core-clock-offset")]
    public void PublishedNonBridgeDeclarationsNeverUseBridge(string name, string objectId, string capabilityId)
    {
        var entry = WindowsMetricControlBindings.FindByName(name)!;
        var binding = new MetricDeviceFieldBinding("recorded", entry.Definition, "recorded-field", entry.SourceId)
            { ControlTarget = new("recorded") { RawObjectId = objectId } };
        var state = new HardwareMetricCatalogState();
        state.PublishCatalogProbe(new HardwareMetricCatalog(new("Recorded CPU", 0, "recorded"), [],
            new Dictionary<string, HardwareMetricItemDescriptor>(),
            new Dictionary<string, MetricDeviceFieldBinding> { [binding.RequestAddress] = binding }, 1, 1, 1).CreateSnapshot());
        var catalog = new WindowsControlObjectCatalog(state, [], new Access(), null!, null!, null!, null!);
        Assert.False(new ControlWriteLayer(catalog, [], new Access()).UsesHardwareBridge(objectId, capabilityId));
    }

    private sealed class Access : IControlAccessLevel
    {
        public string Current => ControlAccessLevels.Root;
        public Task SetAsync(string level, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class DeclaredCatalog : IControlObjectCatalog
    {
        public bool UsesHardwareBridge(string objectId, string capabilityId)
            => objectId == "cpu:recorded" && capabilityId == "cpu.power-limit";
        public ControlObjectCatalog ReadObjects()
            => throw new InvalidOperationException("Dependency classification must not read objects or writer availability.");
    }
    private sealed class Store : IControlDesiredStateStore
    {
        public ControlDesiredState Value = ControlDesiredState.Empty;
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(ControlDesiredState value, CancellationToken token) { Value = value; return Task.CompletedTask; }
    }
    private sealed class OfflineLayer(ControlWriteLayer classifier, HardwareBridgeProcessClient bridge) : IControlWriteLayer
    {
        public int Writes;
        public bool UsesHardwareBridge(string objectId, string capabilityId) => classifier.UsesHardwareBridge(objectId, capabilityId);
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token)
        {
            Writes++;
            Assert.NotNull(bridge.ReadCurrentReadings());
            return Task.FromResult(new ControlApplyReport(desired.Objects.SelectMany(target => target.Settings.Select(setting =>
                new ControlApplyOutcome(target.ObjectId, setting.CapabilityId, ControlApplyStatuses.Applied, null))).ToArray(), DateTimeOffset.UtcNow));
        }
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
            => Task.FromResult(ControlApplyReport.Empty);
    }
}
