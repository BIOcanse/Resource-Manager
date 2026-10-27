using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.Shared.BusinessBlocks;
using BackendBlocks = ResourceManager.App.StateItems.BlockUids;
using BackendRegistry = ResourceManager.App.StateItems.StateRegistry;

namespace Resource_Manager_APP.Tests;

public sealed class NativeControlAdapterLifecycleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(NativeControl.MinimumReasonTextCapacity - 1)]
    public void CapacityThatCannotHoldTheShellsCleanupReasonIsRejectedBeforeCreation(int capacity)
    {
        var options = Options() with { ReasonTextCapacity = capacity };
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeControl(HostUids(), options, NullLogger.Instance));
    }

    [Fact]
    public async Task DisposalDoesNotQueryAnAlreadyDisposedPlanProvider()
    {
        var control = new NativeControl(HostUids(), Options() with
        {
            ReasonTextCapacity = NativeControl.MinimumReasonTextCapacity
        }, NullLogger.Instance);
        control.CreateAuthority(BackendRegistry.Items, BackendRegistry.Blocks);
        control.Activate(_ => new HoldingBlock(),
            () => throw new ObjectDisposedException("host-service-provider"));
        await control.DisposeAsync();
        Assert.True(control.Handle.IsClosed);
    }

    [Fact]
    public async Task LatestNativeAlignmentRemainsQueryableWhenHistoryIsDisabled()
    {
        await using var control = new NativeControl(HostUids(), Options() with { HistoryCapacity = 0 }, NullLogger.Instance);
        var authority = control.CreateAuthority(BackendRegistry.Items, BackendRegistry.Blocks);
        await using var scheduler = new ResourceManager.Shared.BusinessBlocks.BusinessBlockScheduler(control, authority, _ => new HoldingBlock());
        var target = BackendBlocks.ControlActualStateReader;
        var why = new BusinessBlockReason(BusinessBlockRequestKind.User, "operator", "observe", 19);
        await scheduler.SetDemandAsync("observe", target, why);
        await BusinessBlockTestSupport.Until(() => scheduler.ReadState(target) == BusinessBlockState.Running);
        var snapshot = scheduler.Read();
        Assert.Empty(snapshot.History);
        Assert.Empty(snapshot.Decisions);
        var current = Assert.Single(snapshot.CurrentDecisions, value => value.ParentId == BackendBlocks.HardwareControl);
        Assert.Contains(target, current.ShouldRun);
        var currentItem = Assert.Single(snapshot.Items, value => value.ItemUid == BackendItems.BlockDemand && value.BlockUid == target);
        var capturedItem = Assert.Single(current.Items, value => value.ItemUid == BackendItems.BlockDemand && value.BlockUid == target);
        Assert.True(currentItem.Value is true);
        Assert.True(capturedItem.Value is true);
#if RM_STATE_NAMES
        var expectedBlockName = ResourceManager.App.StateItems.StateNames.Name(target);
        Assert.Equal("ControlActualStateReader", expectedBlockName);
        Assert.Equal(expectedBlockName, currentItem.BlockName);
        Assert.Equal(expectedBlockName, capturedItem.BlockName);
#else
        Assert.Null(currentItem.BlockName);
        Assert.Null(capturedItem.BlockName);
#endif
        await scheduler.SetDemandAsync("observe", null, why);
        await BusinessBlockTestSupport.Until(() => scheduler.ReadState(target) == BusinessBlockState.Stopped);
        Assert.DoesNotContain(target, Assert.Single(scheduler.Read().CurrentDecisions,
            value => value.ParentId == BackendBlocks.HardwareControl).ShouldRun);
    }

    [Fact]
    public async Task NativeRoundWaitsForStopRequestButNotStopCallbackDrainOrRunReturn()
    {
        await using var control = new NativeControl(HostUids(), Options(), NullLogger.Instance);
        var authority = control.CreateAuthority(BackendRegistry.Items, BackendRegistry.Blocks);
        var block = new GatedStopCallbackBlock();
        var target = BackendBlocks.ControlActualStateReader;
        await using var scheduler = new ResourceManager.Shared.BusinessBlocks.BusinessBlockScheduler(
            control, authority, uid => uid == target ? block : new HoldingBlock());
        var why = new BusinessBlockReason(BusinessBlockRequestKind.User, "operator", "cancel-delivery", 19);
        await scheduler.SetDemandAsync("cancel-delivery", target, why);
        await BusinessBlockTestSupport.Until(() => scheduler.ReadState(target) == BusinessBlockState.Running);

        try
        {
            var withdrawal = scheduler.SetDemandAsync("cancel-delivery", null, why);
            await block.StopCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await withdrawal.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(block.StopToken.IsCancellationRequested);
            Assert.False(block.ReleaseStopCallback.Task.IsCompleted);
            Assert.False(block.RunCompleted.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(target));
        }
        finally { block.ReleaseStopCallback.TrySetResult(); }

        await BusinessBlockTestSupport.Until(() => scheduler.ReadState(target) == BusinessBlockState.Stopped);
    }

    [Fact]
    public void SynchronousScopeDisposalReleasesAnUnactivatedStateDirectory()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new NativeControl(HostUids(), Options(), NullLogger.Instance));
        services.AddSingleton(provider => provider.GetRequiredService<NativeControl>()
            .CreateAuthority(BackendRegistry.Items, BackendRegistry.Blocks));
        NativeControl control;
        using (var provider = services.BuildServiceProvider())
        {
            _ = provider.GetRequiredService<ResourceManager.Shared.BusinessBlocks.State.StateAuthority>();
            control = provider.GetRequiredService<NativeControl>();
        }
        Assert.True(control.Handle.IsClosed);
    }

    [Fact]
    public async Task AuthorityPerBlockKeysMatchTheCreatedProfile()
    {
        await using var control = new NativeControl(HostUids(), Options(), NullLogger.Instance);
        var authority = control.CreateAuthority(BackendRegistry.Items, BackendRegistry.Blocks);
        var rows = authority.ReadAll(static _ => null);
        var demands = rows.Where(row => row.ItemUid == BackendItems.BlockDemand).ToArray();
        Assert.Equal(control.Graph.BlockIds.Count + 1, demands.Length);
        Assert.DoesNotContain(demands, row => row.BlockUid == BackendBlocks.AutomaticProductUpdateService);
        Assert.Throws<KeyNotFoundException>(() => authority.Read<bool>(new(BackendItems.BlockDemand,
            BackendBlocks.AutomaticProductUpdateService)));
    }

    private sealed class HoldingBlock : IBusinessBlock
    {
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            context.ReportRunning();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        }
    }

    private sealed class GatedStopCallbackBlock : IBusinessBlock
    {
        internal TaskCompletionSource StopCallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseStopCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RunCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken StopToken { get; private set; }

        public async Task RunAsync(BusinessBlockRunContext context)
        {
            StopToken = context.StopNotification;
            using var registration = context.StopNotification.Register(() =>
            {
                StopCallbackEntered.TrySetResult();
                ReleaseStopCallback.Task.GetAwaiter().GetResult();
            });
            context.ReportRunning();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
            await ReleaseStopCallback.Task.ConfigureAwait(false);
            RunCompleted.TrySetResult();
        }
    }

    private static int[] HostUids() => BackendRegistry.Blocks.Where(uid => uid != BackendBlocks.Program
        && uid != BackendBlocks.AutomaticProductUpdateService).ToArray();
    private static NativeControlOptions Options() => new()
    {
        Profile = 1, DemandItemUid = BackendItems.BlockDemand, ShutdownItemUid = BackendItems.ProgramShuttingDown
    };
}
