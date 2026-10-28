using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;
using SharedScheduler = ResourceManager.Shared.BusinessBlocks.BusinessBlockScheduler;
using BackendRegistry = ResourceManager.App.StateItems.StateRegistry;

namespace Resource_Manager_APP.Tests;

public sealed class NativeEtwFrozenRuleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly BusinessBlockReason Reason = new(BusinessBlockRequestKind.User, "native-frozen-test", "explicit", 7);
    [Theory]
    [InlineData(BackendUids.PresentFrameEtwSource, BackendItems.PresentUnfrozen)]
    [InlineData(BackendUids.DxgkrnlVidMmEtwTelemetryZone, BackendItems.VidMmUnfrozen)]
    public async Task ProductionNativeRuleStopsOnlyTheExplicitlyFrozenProducerAndLeavesGpuRunning(int target, int modeUid)
    {
        var blocks = new[] { BackendUids.NativeEtwLifetimeService, BackendUids.PresentFrameEtwSource,
            BackendUids.GpuAllocationCollector, BackendUids.DxgkrnlVidMmEtwTelemetryZone }.ToDictionary(id => id, _ => new HoldingBlock());
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            blocks.ToDictionary(value => value.Key, value => (IBusinessBlock)value.Value));
        var authority = fixture.Authority;
        var presentMode = authority.ClaimCell<bool>(BackendItems.PresentUnfrozen);
        var vidMmMode = authority.ClaimCell<bool>(BackendItems.VidMmUnfrozen);
        await presentMode.PublishAsync(true, Reason);
        await vidMmMode.PublishAsync(true, Reason);
        var mode = modeUid == BackendItems.PresentUnfrozen ? presentMode : vidMmMode;
        var scheduler = fixture.Scheduler;
        await fixture.InitializeHostPermitsAsync();
        try
        {
            await fixture.SetNativeSubscriptionInUseAsync(target, true);
            await fixture.SetNativeSubscriptionInUseAsync(BackendUids.GpuAllocationCollector, true);
            await Until(() => scheduler.ReadState(target) == BusinessBlockState.Running
                && scheduler.ReadState(BackendUids.GpuAllocationCollector) == BusinessBlockState.Running);
            await mode.PublishAsync(false, Reason).WaitAsync(Timeout);
            await Until(() => scheduler.ReadState(target) == BusinessBlockState.Stopped);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BackendUids.GpuAllocationCollector));
            Assert.Equal(1, blocks[BackendUids.GpuAllocationCollector].Runs);
            var activityUid = target == BackendUids.PresentFrameEtwSource ? StateItemUids.GraphicsApiObservationInUse : StateItemUids.VramResidualInUse;
            Assert.True(authority.Read<bool>(activityUid).Value);
            Assert.DoesNotContain(scheduler.Read().History, transition => transition.BlockId == BackendUids.GpuAllocationCollector
                && transition.To == BusinessBlockState.Stopping);
            await mode.PublishAsync(true, Reason).WaitAsync(Timeout);
            await Until(() => scheduler.ReadState(target) == BusinessBlockState.Running && blocks[target].Runs == 2);
            Assert.Equal(1, blocks[BackendUids.GpuAllocationCollector].Runs);
        }
        finally { await Stop(scheduler); }
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }
    private static async Task Stop(SharedScheduler scheduler)
    {
        await scheduler.ShutdownAsync(Reason).WaitAsync(Timeout);
        await Until(() => scheduler.Read().Blocks.All(block => block.State == BusinessBlockState.Stopped));
    }
    private sealed class HoldingBlock : IBusinessBlock
    {
        private int runs;
        internal int Runs => Volatile.Read(ref runs);
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            Interlocked.Increment(ref runs);
            context.ReportRunning();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        }
    }
}
