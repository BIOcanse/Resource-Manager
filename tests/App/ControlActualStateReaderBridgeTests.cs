using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;

namespace Resource_Manager_APP.Tests;

public sealed partial class ControlActualStateReaderTests
{
    [Theory]
    [InlineData("cpu_amd_requested_stapm_power_limit_watts", true)]
    [InlineData("gpu_amd_requested_curve_optimizer_counts", true)]
    [InlineData("cpu_amd_requested_tdc_vdd_current_limit_amperes", false)]
    [InlineData("cpu_intel_requested_pl1_power_limit_watts", false)]
    [InlineData("gpu_amd_requested_graphics_clock_offset_mhz", false)]
    public void ActualSubscriptionsClassifyPublishedReadDependenciesWithoutProbing(string name, bool usesBridge)
    {
        var state = new HardwareMetricCatalogState();
        var entry = WindowsMetricControlBindings.FindByName(name)!;
        var binding = new MetricDeviceFieldBinding("recorded", entry.Definition, "recorded-field", entry.SourceId)
            { ControlTarget = new("recorded") { RawObjectId = "recorded-target" } };
        state.PublishCatalogProbe(new HardwareMetricCatalog(new("Recorded CPU", 0, "recorded"), [],
            new Dictionary<string, HardwareMetricItemDescriptor>(),
            new Dictionary<string, MetricDeviceFieldBinding> { [binding.RequestAddress] = binding }, 1, 1, 1).CreateSnapshot());
        using var fan = new FanControlCoreClient(new FanTestEnvironment(),
            (_, _) => throw new InvalidOperationException("Admission must not open FanCore or probe hardware."));
        var catalog = new WindowsControlObjectCatalog(state, [], null!, fan, null!, null!, null!);
        using var bridge = new HardwareBridgeClient(() => null, HardwareBridgeSessionTests.Options, null);
        using var reader = new ControlActualStateReader(catalog, [], bridge: bridge);
        var edges = new List<bool>();
        bridge.Owner.ReadingsInUseChanged += edges.Add;

        var first = reader.AcquireSubscription();
        var second = reader.AcquireSubscription();
        Assert.Equal(usesBridge, bridge.Owner.ReadingsInUse);
        Assert.False(reader.FanReadingsInUse);
        Assert.Null(bridge.ReadCurrentReadings());
        first.Dispose();
        Assert.Equal(usesBridge, bridge.Owner.ReadingsInUse);
        second.Dispose();
        second.Dispose();
        Assert.False(bridge.Owner.ReadingsInUse);
        Assert.Equal(usesBridge ? new[] { true, false } : [], edges);
    }

    [Fact]
    public void ActualReaderDisposalWithdrawsItsBridgeSubscriptionWithoutStartingTheWorker()
    {
        using var bridge = new HardwareBridgeClient(() => null, HardwareBridgeSessionTests.Options, null);
        var catalog = new Catalog { HasBridge = true };
        var reader = new ControlActualStateReader(catalog, [], bridge: bridge);
        var edges = new List<bool>();
        bridge.Owner.ReadingsInUseChanged += edges.Add;
        var lease = reader.AcquireSubscription();
        Assert.True(bridge.Owner.ReadingsInUse);
        reader.Dispose();
        lease.Dispose();
        Assert.False(bridge.Owner.ReadingsInUse);
        Assert.False(reader.ActualControlValuesInUse);
        Assert.Equal([true, false], edges);
        Assert.Equal(0, catalog.Reads);
        Assert.Null(bridge.ReadCurrentReadings());
    }

    [Fact]
    public async Task PeriodicActualReadsKeepBridgeRunningWithoutPendingCommandsAndReleaseOnLastUnsubscribe()
    {
        using var bridge = new HardwareBridgeClient(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options, null);
        HardwareCommandQueue? queue = null;
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, Func<IServiceProvider, IBusinessBlock>>
            { [BlockUids.HardwareBridgeProcessOwner] = _ => new HardwareBridgeRunningBlock(bridge, queue!) });
        await fixture.InitializeHostPermitsAsync();
        using var commands = queue = new(fixture.Authority, bridge.Owner);
        _ = new HardwareBridgeReadingSubscriptions(bridge.Owner, fixture.Authority);
        await fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeAvailable)
            .PublishAsync(true, BusinessBlockTestSupport.Reason());
        var edges = new List<bool>();
        bridge.Owner.ReadingsInUseChanged += edges.Add;
        var clock = new ControlledClock();
        var catalog = new Catalog();
        var writer = new CountingWriter
        {
            Read = _ =>
            {
                Assert.True(bridge.Owner.ReadingsInUse);
                Assert.False(commands.HasPending);
                return Task.FromResult<ControlActualValue?>(bridge.ReadCurrentReadings() is null
                    ? null : new("device", "power", 42));
            }
        };
        using var reader = new ControlActualStateReader(catalog, [writer], clock, bridge);
        await reader.StartAsync(default);
        using var first = reader.AcquireSubscription();
        using var second = reader.AcquireSubscription();
        // The first complete declaration can arrive after subscription admission.
        catalog.HasBridge = true;
        var startup = await clock.NextTimerAsync();
        startup.Fire();
        var periodic = await clock.NextTimerAsync();
        await BusinessBlockTestSupport.State(fixture.Scheduler, BlockUids.HardwareBridgeProcessOwner, BusinessBlockState.Running);
        // The first read may precede the controller starting its sibling. Later rounds use that run.
        periodic.Fire();
        await WaitUntilAsync(() => reader.Current.Values.Count == 1);
        var previous = reader.Current;
        first.Dispose();
        periodic.Fire();
        await WaitUntilAsync(() => !ReferenceEquals(previous, reader.Current));
        Assert.True(bridge.Owner.ReadingsInUse);
        Assert.False(commands.HasPending);
        Assert.False(fixture.Authority.Read<bool>(StateItemUids.HardwareCommandPending).Value);
        Assert.Equal([true], edges);
        second.Dispose();
        await reader.StopAsync(default);
        await BusinessBlockTestSupport.State(fixture.Scheduler, BlockUids.HardwareBridgeProcessOwner, BusinessBlockState.Stopped);
        Assert.Equal([true, false], edges);
        Assert.Null(bridge.ReadCurrentReadings());
    }
}
