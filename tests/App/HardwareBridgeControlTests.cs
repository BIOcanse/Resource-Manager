using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Diagnostics;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareBridgeControlTests
{
    private static BusinessBlockReason Reason => new(BusinessBlockRequestKind.Configuration, "bridge-test", "facts", 0);

    [Fact]
    public async Task CommandsAndReadingsOwnFreshRunsAndAppliedSettingsDoNotHoldTheIdleHelper()
    {
        var children = new ConcurrentQueue<Process>();
        var timeline = new List<(string Phase, int Runs, bool Section)>();
        using var client = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options, startFactory: start =>
            {
                var child = Process.Start(start)!;
                var observed = Process.GetProcessById(child.Id);
                _ = observed.SafeHandle;
                children.Enqueue(observed);
                return child;
            });
        HardwareCommandQueue? queue = null;
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, Func<IServiceProvider, IBusinessBlock>>
            { [BlockUids.HardwareBridgeProcessOwner] = _ => new HardwareBridgeRunningBlock(client, queue!) });
        await fixture.InitializeHostPermitsAsync();
        queue = new(fixture.Authority, client.Owner);
        await fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeAvailable).PublishAsync(true, Reason);
        var readings = fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeReadingsInUse);
        client.Owner.ReadingsInUseChanged += value => _ = readings.PublishAsync(value, Reason);
        var store = new Store();
        var plane = new ControlPlane(store, new Layer(client, store), commands: queue);
        HardwareBridgeControlTestFacts.Bind(plane, fixture.Authority);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            Assert.Empty(children);
            Assert.Null(client.ReadCurrentReadings());
            timeline.Add(("idle", 0, false));
            await plane.SetObjectSettingsAsync("cpu:test", [new("cpu.power-limit", Number: 45, Unit: "W")], deadline.Token);
            Assert.True(plane.AppliedControlIntentsExist);
            await Stopped("applied-idle", 1);
            await plane.ReassertAsync(deadline.Token);
            Assert.True(plane.AppliedControlIntentsExist);
            await Stopped("reasserted-idle", 2);
            await plane.SetObjectSettingsAsync("cpu:test", [], deadline.Token);
            Assert.False(plane.AppliedControlIntentsExist);
            await Stopped("released-idle", 3);
            using (client.SubscribeReadings())
            {
                await fixture.WaitForStateAsync(BlockUids.HardwareBridgeProcessOwner, BusinessBlockState.Running);
                Assert.False(queue.HasPending);
                Assert.NotNull(client.ReadCurrentReadings());
                Assert.Equal(4, children.Count);
                Assert.False(children.Last().HasExited);
                timeline.Add(("readings", 4, true));
            }
            await Stopped("unsubscribed-idle", 4);
            Assert.Equal(new[] { ("idle", 0, false), ("applied-idle", 1, false),
                ("reasserted-idle", 2, false), ("released-idle", 3, false), ("readings", 4, true),
                ("unsubscribed-idle", 4, false) }, timeline);
            Assert.Equal(4, children.Select(child => child.Id).Distinct().Count());
        }
        finally
        {
            deadline.Cancel();
            foreach (var child in children) child.Dispose();
        }

        async Task Stopped(string phase, int runs)
        {
            await fixture.WaitForStateAsync(BlockUids.HardwareBridgeProcessOwner, BusinessBlockState.Stopped);
            Assert.False(queue.HasPending);
            Assert.False(client.Owner.ReadingsInUse);
            Assert.Equal(runs, children.Count);
            Assert.All(children, child => Assert.True(child.HasExited));
            Assert.Null(client.ReadCurrentReadings());
            timeline.Add((phase, runs, false));
        }
    }

    [Fact]
    public async Task TrayHasNoBridgeStartupAliasAndEitherUseFactAloneStartsAndStopsBlock()
    {
        var services = new ServiceCollection();
        ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(services, []);
        var declaration = Assert.Single(services.Select(row => row.ImplementationInstance)
            .OfType<BusinessBlockRegistration>(), row => row.Id == BlockUids.HardwareBridgeProcessOwner);
        Assert.Null(declaration.HostedServiceType);
        var probe = new Probe();
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, IBusinessBlock> { [BlockUids.HardwareBridgeProcessOwner] = probe });
        await fixture.InitializeHostPermitsAsync();
        var authority = fixture.Authority;
        await authority.ClaimCell<bool>(StateItemUids.HardwareBridgeAvailable).PublishAsync(true, Reason);
        var facts = new[] { StateItemUids.HardwareCommandPending, StateItemUids.HardwareBridgeReadingsInUse }
            .Select(uid => authority.ClaimCell<bool>(uid)).ToArray();
        Assert.Equal(BusinessBlockState.Stopped, fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner));
        foreach (var cell in facts) await cell.PublishAsync(false, Reason);
        foreach (var cell in facts)
        {
            await cell.PublishAsync(true, Reason);
            await BusinessBlockTestSupport.Until(() => fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner) == BusinessBlockState.Running);
            await cell.PublishAsync(false, Reason);
            await BusinessBlockTestSupport.Until(() => fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner) == BusinessBlockState.Stopped);
        }
        // Any true fact dominates missing/failed values from the other owners.
        await facts[0].PublishNoValueAsync(NoValueReason.ReadFailed, Reason);
        await facts[1].PublishAsync(true, Reason);
        await BusinessBlockTestSupport.Until(() => fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner) == BusinessBlockState.Running);
        await facts[1].PublishNoValueAsync(NoValueReason.NotPublished, Reason);
        await BusinessBlockTestSupport.Until(() => fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner) == BusinessBlockState.Stopped);
        Assert.Equal(3, probe.Starts);
    }

    [Fact]
    public async Task SavedSettingReassertionAndReleaseStartOnlyTheirCommandRuns()
    {
        using var client = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture, HardwareBridgeSessionTests.Options);
        HardwareCommandQueue? queue = null;
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, Func<IServiceProvider, IBusinessBlock>>
            { [BlockUids.HardwareBridgeProcessOwner] = _ => new HardwareBridgeRunningBlock(client, queue!) });
        await fixture.InitializeHostPermitsAsync();
        queue = new(fixture.Authority, client.Owner);
        await fixture.InitializeHostPermitsAsync();
        var available = fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeAvailable);
        client.Owner.AvailabilityChanged += value => _ = available.PublishAsync(value, Reason);
        await available.PublishAsync(client.Owner.IsAvailable, Reason);
        var reading = fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeReadingsInUse);
        client.Owner.ReadingsInUseChanged += value => _ = reading.PublishAsync(value, Reason);
        var store = new Store();
        var writes = new Layer(client, store);
        var plane = new ControlPlane(store, writes, commands: queue);
        HardwareBridgeControlTestFacts.Bind(plane, fixture.Authority);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var state = await plane.SetObjectSettingsAsync("cpu:test", [new("cpu.power-limit", Number:45, Unit:"W")], deadline.Token);
        Assert.Equal(ControlApplyStatuses.Applied, Assert.Single(state.LastApply.Outcomes).Status);
        await BusinessBlockTestSupport.Until(() => !queue.HasPending);
        Assert.True(plane.AppliedControlIntentsExist);
        await fixture.WaitForStateAsync(BlockUids.HardwareBridgeProcessOwner, BusinessBlockState.Stopped);
        await plane.ReassertAsync(deadline.Token);
        Assert.Equal(2, writes.Writes);
        await fixture.WaitForStateAsync(BlockUids.HardwareBridgeProcessOwner, BusinessBlockState.Stopped);
        await plane.SetObjectSettingsAsync("cpu:test", [], deadline.Token);
        Assert.False(plane.AppliedControlIntentsExist);
        await BusinessBlockTestSupport.Until(() => fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner) == BusinessBlockState.Stopped);
        Assert.Null(client.ReadCurrentReadings());
        using (client.SubscribeReadings())
        {
            await BusinessBlockTestSupport.Until(() => fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner) == BusinessBlockState.Running);
            Assert.NotNull(client.ReadCurrentReadings());
        }
        await BusinessBlockTestSupport.Until(() => fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner) == BusinessBlockState.Stopped);
        Assert.Null(client.ReadCurrentReadings());
    }

    private sealed class Store : IControlDesiredStateStore
    {
        public ControlDesiredState Value { get; private set; } = ControlDesiredState.Empty;
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(ControlDesiredState desired, CancellationToken token) { Value = desired; return Task.CompletedTask; }
    }

    [Fact]
    public async Task SuccessfulOldOwnershipReleaseCannotRetainFailedReplacementIntent()
    {
        var authority = new BackendStateItems().Authority;
        var store = new Store();
        var writes = new OwnershipLayer();
        var plane = new ControlPlane(store, writes);
        HardwareBridgeControlTestFacts.Bind(plane, authority);
        await plane.SetObjectSettingsAsync("fan:test", [new("fan.curve", Curve:[new(60,35)], CurveExecution:"firmware")], default);
        Assert.True(plane.AppliedControlIntentsExist);
        writes.Fail = true;
        var replacement = await plane.SetObjectSettingsAsync("fan:test", [new("fan.curve", Curve:[new(60,35)], CurveExecution:"software")], default);
        Assert.Equal(ControlApplyStatuses.Failed, Assert.Single(replacement.LastApply.Outcomes).Status);
        Assert.False(plane.AppliedControlIntentsExist);
        await plane.ReassertAsync(default);
        Assert.Equal(2, writes.Writes);
    }

    [Fact]
    public async Task MissingOptionalHelperStillLetsQueuedWritersReturnTheirOwnTypedResult()
    {
        using var bridge = new HardwareBridgeProcessClient(() => null, HardwareBridgeSessionTests.Options);
        var queue = new HardwareCommandQueue(bridge: bridge.Owner);
        var result = queue.ExecuteAsync(_ => Task.FromResult("other-writer-applied"), default);
        var block = new HardwareBridgeRunningBlock(bridge, queue);
        await Assert.ThrowsAsync<FileNotFoundException>(() => block.RunAsync(new(default, () => throw new Exception("must not report Running"))));
        Assert.Equal("other-writer-applied", await result);
        Assert.False(queue.HasPending);
        Assert.Null(bridge.ReadCurrentReadings());
    }

    private sealed class OwnershipLayer : IControlWriteLayer
    {
        public bool Fail;
        public int Writes;
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token)
        { Writes++; return Task.FromResult(Report(desired, Fail ? ControlApplyStatuses.Failed : ControlApplyStatuses.Applied)); }
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
            => Task.FromResult(Report(removed, ControlApplyStatuses.Applied));
        private static ControlApplyReport Report(ControlDesiredState desired, string status) => new(desired.Objects.SelectMany(target => target.Settings
            .Select(setting => new ControlApplyOutcome(target.ObjectId, setting.CapabilityId, status, null))).ToArray(), DateTimeOffset.UtcNow);
    }
    private sealed class Layer(HardwareBridgeProcessClient client, Store store) : IControlWriteLayer
    {
        public bool UsesHardwareBridge(string objectId, string capabilityId) => objectId == "cpu:test";
        public int Writes;
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token)
        {
            if (desired.Objects.Count == 0) return Task.FromResult(Report(desired));
            Assert.NotNull(client.ReadCurrentReadings());
            Assert.NotEmpty(store.Value.Objects); // Save precedes execution; no hardware is written.
            Writes++;
            return Task.FromResult(Report(desired));
        }
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
            => Task.FromResult(Report(removed));
        private static ControlApplyReport Report(ControlDesiredState desired) => new(desired.Objects.SelectMany(target => target.Settings
            .Select(setting => new ControlApplyOutcome(target.ObjectId, setting.CapabilityId, ControlApplyStatuses.Applied, null))).ToArray(), DateTimeOffset.UtcNow);
    }
    private sealed class Probe : IBusinessBlock
    {
        public int Starts;
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            Interlocked.Increment(ref Starts); context.ReportRunning();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        }
    }
}
