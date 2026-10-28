using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareBridgeControlPendingTests
{
    [Fact]
    public async Task AcceptedBridgeWriteKeepsPendingWhileSavingBeforeAnyQueueCommandExists()
    {
        using var queue = new HardwareCommandQueue();
        var store = new PausingStore();
        var writer = new RecordingWriter();
        var plane = new ControlPlane(store, writer, commands: queue);
        var apply = plane.SetObjectSettingsAsync("cpu:amd", [new("cpu.power-limit", Number: 45, Unit: "W")], default);
        try
        {
            await store.Saved.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.True(queue.HasPending);
            Assert.False(plane.AppliedControlIntentsExist);
            Assert.Equal(0, writer.Writes);
            // A queue drain has nothing to execute yet; the accepted control
            // operation itself still owns the same pending publication.
            await queue.DrainPendingAsync(default);
            Assert.True(queue.HasPending);
        }
        finally { store.Continue.TrySetResult(); }
        using var stop = new CancellationTokenSource();
        var runner = queue.RunAsync(stop.Token);
        try
        {
            Assert.Equal(ControlApplyStatuses.Applied, Assert.Single((await apply.WaitAsync(BusinessBlockTestSupport.TestTimeout)).LastApply.Outcomes).Status);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.WaitAsync(BusinessBlockTestSupport.TestTimeout));
        }
        Assert.True(plane.AppliedControlIntentsExist);
        Assert.False(queue.HasPending);
    }

    [Fact]
    public async Task WithdrawingParentPermitLetsWaitingControlOperationExpireWithoutStartingTheChild()
    {
        HardwareCommandQueue? queue = null;
        var starts = 0;
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, Func<IServiceProvider, IBusinessBlock>>
            { [BlockUids.HardwareBridgeProcessOwner] = _ => new QueueBlock(() =>
                { Interlocked.Increment(ref starts); return queue!; }) });
        await fixture.InitializeHostPermitsAsync();
        queue = new(fixture.Authority, requestTimeout: TimeSpan.FromMilliseconds(100));
        await fixture.SetHostPermitAsync(BlockUids.HardwareBridgeProcessOwner, false);
        await fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeAvailable).PublishAsync(true, Reason);
        var plane = new ControlPlane(new Store(), new NeverWriter(), commands: queue);
        var apply = plane.SetObjectSettingsAsync("cpu:amd", [new("cpu.power-limit", Number: 45, Unit: "W")], default);
        Assert.True(queue.HasPending);
        await fixture.SetHostPermitAsync(BlockUids.HardwareControl, false);
        await fixture.WaitForStateAsync(BlockUids.HardwareControl, BusinessBlockState.Stopped);
        await fixture.SetHostPermitAsync(BlockUids.HardwareBridgeProcessOwner, true);
        await Assert.ThrowsAsync<TimeoutException>(() => apply.WaitAsync(BusinessBlockTestSupport.TestTimeout));
        Assert.False(queue.HasPending);
        Assert.False(fixture.Authority.Read<bool>(StateItemUids.HardwareCommandPending).Value);
        Assert.False(plane.AppliedControlIntentsExist);
        Assert.False(plane.AppliedControlIntentsExist);
        Assert.Equal(0, starts);
        await fixture.WaitForStateAsync(BlockUids.HardwareBridgeProcessOwner, BusinessBlockState.Stopped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeReceiptRetainsPendingThroughControlPlanePublicationThenKeepsOrStopsRun(bool fail)
    {
        using var bridge = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture, HardwareBridgeSessionTests.Options);
        HardwareCommandQueue? queue = null;
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, Func<IServiceProvider, IBusinessBlock>>
            { [BlockUids.HardwareBridgeProcessOwner] = _ => new HardwareBridgeRunningBlock(bridge, queue!) });
        await fixture.InitializeHostPermitsAsync();
        queue = new(fixture.Authority, bridge.Owner);
        await fixture.InitializeHostPermitsAsync();
        await fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeAvailable).PublishAsync(true, Reason);
        var layer = new ReceiptLayer(bridge) { Fail = fail };
        var plane = new ControlPlane(new Store(), layer, commands: queue);
        HardwareBridgeControlTestFacts.Bind(plane, fixture.Authority);
        var observedPublication = false;
        plane.AppliedIntentsChanged += () =>
        {
            Assert.True(queue.HasPending);
            observedPublication = true;
        };
        var apply = plane.SetObjectSettingsAsync("cpu:amd", [new("cpu.power-limit", Number: 45, Unit: "W")], default);
        try
        {
            await layer.Completed.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.True(queue.HasPending);
            Assert.True(fixture.Authority.Read<bool>(StateItemUids.HardwareCommandPending).Value);
            Assert.False(plane.AppliedControlIntentsExist);
            Assert.False(observedPublication);
            Assert.NotNull(bridge.ReadCurrentReadings());
            Assert.Equal(BusinessBlockState.Running, fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner));
        }
        finally { layer.Publish.TrySetResult(); }
        var result = await apply.WaitAsync(BusinessBlockTestSupport.TestTimeout);
        Assert.Equal(fail ? ControlApplyStatuses.Failed : ControlApplyStatuses.Applied, Assert.Single(result.LastApply.Outcomes).Status);
        Assert.True(observedPublication);
        Assert.Equal(!fail, plane.AppliedControlIntentsExist);
        await BusinessBlockTestSupport.Until(() => !queue.HasPending);
        Assert.False(fixture.Authority.Read<bool>(StateItemUids.HardwareCommandPending).Value);
        await fixture.WaitForStateAsync(BlockUids.HardwareBridgeProcessOwner, BusinessBlockState.Stopped);
        Assert.Null(bridge.ReadCurrentReadings());
    }

    [Fact]
    public async Task MultipleNativeCommandsKeepOnePendingPublicationUntilTheWholeWritePublishes()
    {
        using var bridge = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture, HardwareBridgeSessionTests.Options);
        HardwareCommandQueue? queue = null;
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, Func<IServiceProvider, IBusinessBlock>>
            { [BlockUids.HardwareBridgeProcessOwner] = _ => new HardwareBridgeRunningBlock(bridge, queue!) });
        await fixture.InitializeHostPermitsAsync();
        queue = new(fixture.Authority, bridge.Owner);
        await fixture.InitializeHostPermitsAsync();
        await fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeAvailable).PublishAsync(true, Reason);
        var layer = new ReceiptLayer(bridge) { PauseBetweenCommands = true };
        var plane = new ControlPlane(new Store(), layer, commands: queue);
        HardwareBridgeControlTestFacts.Bind(plane, fixture.Authority);
        var apply = plane.SetObjectSettingsAsync("cpu:amd", [new("cpu.power-limit", Number: 45, Unit: "W")], default);
        try
        {
            await layer.FirstCompleted.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(1, layer.Commands);
            Assert.True(queue.HasPending);
            Assert.False(plane.AppliedControlIntentsExist);
            layer.Next.TrySetResult();
            await layer.Completed.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(2, layer.Commands);
            Assert.True(queue.HasPending);
            Assert.False(plane.AppliedControlIntentsExist);
            Assert.NotNull(bridge.ReadCurrentReadings());
        }
        finally { layer.Next.TrySetResult(); layer.Publish.TrySetResult(); }
        Assert.Equal(ControlApplyStatuses.Applied, Assert.Single((await apply).LastApply.Outcomes).Status);
        Assert.True(plane.AppliedControlIntentsExist);
        await BusinessBlockTestSupport.Until(() => !queue.HasPending);
        await fixture.WaitForStateAsync(BlockUids.HardwareBridgeProcessOwner, BusinessBlockState.Stopped);
        Assert.Null(bridge.ReadCurrentReadings());
    }

    private static BusinessBlockReason Reason => new(BusinessBlockRequestKind.Configuration, nameof(HardwareBridgeControlPendingTests), "control-write", 0);

    private sealed class NeverWriter : IControlWriteLayer
    {
        public bool UsesHardwareBridge(string objectId, string capabilityId) => true;
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token)
            => throw new InvalidOperationException("A command ran while its parent was stopped.");
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
            => throw new InvalidOperationException("A release ran while its parent was stopped.");
    }

    private sealed class RecordingWriter : IControlWriteLayer
    {
        internal int Writes;
        public bool UsesHardwareBridge(string objectId, string capabilityId) => true;
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token)
        {
            Writes++;
            return Task.FromResult(new ControlApplyReport(desired.Objects.SelectMany(target => target.Settings.Select(setting =>
                new ControlApplyOutcome(target.ObjectId, setting.CapabilityId, ControlApplyStatuses.Applied))).ToArray(), DateTimeOffset.UtcNow));
        }
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
            => Task.FromResult(ControlApplyReport.Empty);
    }

    private sealed class PausingStore : IControlDesiredStateStore
    {
        internal readonly TaskCompletionSource Saved = BusinessBlockTestSupport.Signal();
        internal readonly TaskCompletionSource Continue = BusinessBlockTestSupport.Signal();
        private ControlDesiredState value = ControlDesiredState.Empty;
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(value);
        public async Task SaveAsync(ControlDesiredState desired, CancellationToken token)
        {
            value = desired;
            Saved.TrySetResult();
            await Continue.Task.WaitAsync(token);
        }
    }

    private sealed class QueueBlock(Func<HardwareCommandQueue> queue) : IBusinessBlock
    {
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            var commands = queue();
            context.ReportRunning();
            try { await commands.RunAsync(context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        }
    }

    private sealed class Store : IControlDesiredStateStore
    {
        private ControlDesiredState value = ControlDesiredState.Empty;
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(value);
        public Task SaveAsync(ControlDesiredState desired, CancellationToken token) { value = desired; return Task.CompletedTask; }
    }

    private sealed class ReceiptLayer(HardwareBridgeProcessClient bridge) : IControlWriteLayer
    {
        internal readonly TaskCompletionSource FirstCompleted = BusinessBlockTestSupport.Signal();
        internal readonly TaskCompletionSource Next = BusinessBlockTestSupport.Signal();
        internal readonly TaskCompletionSource Completed = BusinessBlockTestSupport.Signal();
        internal readonly TaskCompletionSource Publish = BusinessBlockTestSupport.Signal();
        internal bool Fail;
        internal bool PauseBetweenCommands;
        internal int Commands;
        public bool UsesHardwareBridge(string objectId, string capabilityId) => objectId == "cpu:amd";
        public async Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token)
        {
            // A real helper receipt is enough to establish native completion;
            // this fixture sends describe only and never writes hardware.
            Assert.NotNull(await bridge.SendAsync("describe", null, token));
            Commands++;
            if (PauseBetweenCommands)
            {
                FirstCompleted.TrySetResult();
                await Next.Task.WaitAsync(token);
                Assert.NotNull(await bridge.SendAsync("describe", null, token));
                Commands++;
            }
            Completed.TrySetResult();
            await Publish.Task.WaitAsync(token);
            return new(desired.Objects.SelectMany(target => target.Settings.Select(setting =>
                new ControlApplyOutcome(target.ObjectId, setting.CapabilityId,
                    Fail ? ControlApplyStatuses.Failed : ControlApplyStatuses.Applied))).ToArray(), DateTimeOffset.UtcNow);
        }
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
            => Task.FromResult(ControlApplyReport.Empty);
    }
}

internal static class HardwareBridgeControlTestFacts
{
    internal static void Bind(ControlPlane plane, StateAuthority authority)
    {
        var cell = authority.ClaimCell<bool>(StateItemUids.AppliedControlIntentsExist);
        void Publish() => _ = cell.PublishAsync(plane.AppliedControlIntentsExist,
            new(BusinessBlockRequestKind.Configuration, nameof(HardwareBridgeControlTestFacts), "applied-control", 0));
        plane.AppliedIntentsChanged += Publish;
        Publish();
    }
}
