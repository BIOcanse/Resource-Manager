using ResourceManager.Shared.BusinessBlocks.State;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.StateItems;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareBridgeReviewTests
{
    [Fact]
    public async Task MissingHelperCommandsAnswerInlineAfterFailedEntry()
    {
        using var bridge = new HardwareBridgeProcessClient(() => null, HardwareBridgeSessionTests.Options);
        var queue = new HardwareCommandQueue(bridge: bridge.Owner);
        var block = new HardwareBridgeRunningBlock(bridge, queue);
        await Assert.ThrowsAsync<FileNotFoundException>(() => block.RunAsync(new(default, () => { })));
        try
        {
            Assert.Equal("typed-unavailable", await queue.ExecuteAsync(async token =>
                await bridge.SendAsync("describe", null, token) is null ? "typed-unavailable" : "unexpected", default)
                .WaitAsync(TimeSpan.FromMilliseconds(500)));
            Assert.False(queue.HasPending);
        }
        finally { queue.FailPending(new IOException("test complete")); }
    }

    [Fact]
    public async Task NvidiaOnlySettingsNeverEnterBridgeQueueOrStartTheNativeBlock()
    {
        var starts = 0;
        using var bridge = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture, HardwareBridgeSessionTests.Options,
            startFactory: _ => { Interlocked.Increment(ref starts); throw new Exception("NVIDIA setting started bridge"); });
        HardwareCommandQueue? queue = null;
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, Func<IServiceProvider, IBusinessBlock>>
            { [BlockUids.HardwareBridgeProcessOwner] = _ => new HardwareBridgeRunningBlock(bridge, queue!) });
        await fixture.InitializeHostPermitsAsync();
        queue = new(fixture.Authority, bridge.Owner);
        await fixture.InitializeHostPermitsAsync();
        var reason = new BusinessBlockReason(BusinessBlockRequestKind.Configuration, "nvidia-test", "bridge-availability", 0);
        await fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeAvailable).PublishAsync(true, reason);
        var plane = new ControlPlane(new Store(), new InlineLayer(), commands: queue);

        var view = await plane.SetObjectSettingsAsync("gpu:nvidia", [new("gpu.power-limit", Number:150, Unit:"W")], default)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ControlApplyStatuses.Applied, Assert.Single(view.LastApply.Outcomes).Status);
        Assert.True(plane.AppliedControlIntentsExist);
        Assert.False(queue.HasPending);
        await Task.Delay(50);
        Assert.Equal(BusinessBlockState.Stopped, fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner));
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task SavedRemovalPublishesIntentBeforeCancelledApply()
    {
        var authority = new BackendStateItems().Authority;
        var store = new Store();
        var plane = new ControlPlane(store, new InlineLayer(usesBridge: true));

        await plane.SetObjectSettingsAsync("gpu:nvidia", [new("gpu.power-limit", Number:150, Unit:"W")], default);
        Assert.True(plane.AppliedControlIntentsExist);
        using var caller = new CancellationTokenSource();
        store.AfterSave = caller.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plane.SetObjectSettingsAsync("gpu:nvidia", [], caller.Token));
        Assert.Empty(store.Value.Objects);
        Assert.False(plane.AppliedControlIntentsExist);
    }
    [Fact]
    public async Task MixedBatchAndReassertApplyUnrelatedTargetBeforeWaitingForBridge()
    {
        var queue = new HardwareCommandQueue();
        var layer = new SelectiveLayer();
        var authority = new BackendStateItems().Authority;
        var plane = new ControlPlane(new Store(), layer, commands:queue);

        var applying = plane.ApplyDesiredStateAsync(new([new("cpu:amd",[new("cpu.power-limit",Number:45,Unit:"W")]),
            new("gpu:nvidia",[new("gpu.power-limit",Number:150,Unit:"W")])]),default);
        Assert.Equal(["gpu:nvidia"],layer.Written);
        Assert.True(queue.HasPending);
        await queue.DrainPendingAsync(default);
        Assert.Equal(2,(await applying).LastApply.Outcomes.Count);
        Assert.True(plane.AppliedControlIntentsExist);
        layer.Written.Clear();
        var reasserting = plane.ReassertAsync(default);
        Assert.Equal(["gpu:nvidia"],layer.Written);
        await queue.DrainPendingAsync(default);
        Assert.Equal(2,(await reasserting).Outcomes.Count);
        Assert.False(queue.HasPending);
    }
    private sealed class SelectiveLayer : IControlWriteLayer
    {
        public List<string> Written { get; } = [];
        public bool UsesHardwareBridge(string objectId,string capabilityId) => objectId == "cpu:amd";
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState state,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Written.AddRange(state.Objects.Select(target => target.ObjectId));
            return Task.FromResult(new ControlApplyReport(state.Objects.SelectMany(target => target.Settings
                .Select(setting => new ControlApplyOutcome(target.ObjectId,setting.CapabilityId,ControlApplyStatuses.Applied,null))).ToArray(),DateTimeOffset.UtcNow));
        }
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed,ControlDesiredState remaining,CancellationToken token)
            => WriteAsync(removed,token);
    }
    private sealed class Store : IControlDesiredStateStore
    {
        public ControlDesiredState Value = ControlDesiredState.Empty;
        public Action? AfterSave;
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(ControlDesiredState value, CancellationToken token) { Value=value; AfterSave?.Invoke(); return Task.CompletedTask; }
    }
    private sealed class InlineLayer(bool usesBridge = false) : IControlWriteLayer
    {
        public bool UsesHardwareBridge(string objectId, string capabilityId) => usesBridge;
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState state, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new ControlApplyReport(state.Objects.SelectMany(t => t.Settings.Select(s => new ControlApplyOutcome(t.ObjectId,s.CapabilityId,ControlApplyStatuses.Applied,null))).ToArray(),DateTimeOffset.UtcNow)); }
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token) => WriteAsync(removed,token);
    }
}
