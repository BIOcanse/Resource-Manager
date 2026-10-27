using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockTreeReviewRegressionTests
{
    [Fact]
    public async Task SlowBranchDrainDoesNotDelaySiblingRevocationOrStop()
    {
        var release = BusinessBlockTestSupport.Signal();
        var a = new BusinessBlockTestSupport.Probe();
        var z = new BusinessBlockTestSupport.Probe();
        var aChild = new BusinessBlockTestSupport.Probe();
        var zChild = new BusinessBlockTestSupport.Probe(releaseStop: release.Task);
        await using var scheduler = BusinessBlockTestSupport.Create([
            BusinessBlockTestSupport.Register(101, a), BusinessBlockTestSupport.Register(109, z),
            BusinessBlockTestSupport.Register(102, aChild, 101), BusinessBlockTestSupport.Register(110, zChild, 109)]);
        try
        {
            await scheduler.StartAsync(101, BusinessBlockTestSupport.Reason());
            await scheduler.StartAsync(109, BusinessBlockTestSupport.Reason());
            await scheduler.StartAsync(110, BusinessBlockTestSupport.Reason());
            await zChild.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.ShutdownAsync(BusinessBlockTestSupport.Reason());
            await zChild.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            // An unrelated sibling must finish even while Z's whole branch is still draining.
            await a.Returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await scheduler.StartAsync(102, BusinessBlockTestSupport.Reason());
            Assert.Equal(0, aChild.Runs);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(102));
        }
        finally { release.TrySetResult(); await BusinessBlockTestSupport.StopAll(scheduler); }
    }

    [Fact]
    public async Task ReturnedChildDoesNotHideItsStillDrainingDescendants()
    {
        var returnMiddle = BusinessBlockTestSupport.Signal();
        var releaseLeaf = BusinessBlockTestSupport.Signal();
        var upper = new BusinessBlockTestSupport.Probe();
        var middle = new BusinessBlockTestSupport.Block(async context =>
        {
            context.ReportRunning();
            await returnMiddle.Task;
        });
        var leaf = new BusinessBlockTestSupport.Probe(releaseStop: releaseLeaf.Task);
        await using var scheduler = BusinessBlockTestSupport.Create([
            BusinessBlockTestSupport.Register(108, upper), BusinessBlockTestSupport.Register(107, middle, 108),
            BusinessBlockTestSupport.Register(106, leaf, 107)]);
        try
        {
            foreach (var id in new[] { 106, 107, 108 }) await scheduler.StartAsync(id, BusinessBlockTestSupport.Reason());
            await leaf.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            returnMiddle.TrySetResult();
            await BusinessBlockTestSupport.State(scheduler, 107, BusinessBlockState.Stopped);
            await leaf.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.StopAsync(108, BusinessBlockTestSupport.Reason());
            // Force a completed U-level evaluation while L is deliberately still cleaning up.
            await scheduler.StopAsync(107, BusinessBlockTestSupport.Reason());
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(106));
            Assert.False(upper.Notified.Task.IsCompleted);
            await scheduler.StartAsync(108, BusinessBlockTestSupport.Reason());
            Assert.Equal(1, upper.Runs);
            Assert.False(upper.Notified.Task.IsCompleted);
        }
        finally { returnMiddle.TrySetResult(); releaseLeaf.TrySetResult(); await BusinessBlockTestSupport.StopAll(scheduler); }
    }

    [Fact]
    public async Task ParentReturnDuringDecisionConvergesToAnEntirelyStoppedBranch()
    {
        var parentReturn = BusinessBlockTestSupport.Signal();
        var deciding = BusinessBlockTestSupport.Signal();
        using var releaseDecision = new ManualResetEventSlim();
        var parent = new BusinessBlockTestSupport.Block(async context =>
        {
            context.ReportRunning();
            await parentReturn.Task;
        });
        var child = new BusinessBlockTestSupport.Probe();
        await using var scheduler = BusinessBlockTestSupport.Create([
            BusinessBlockTestSupport.Register(107, parent), BusinessBlockTestSupport.Register(104, child, 107)],
            [new(107, [TestStateUids.StartChild], input =>
            {
                if (!input.Read<bool>(TestStateUids.StartChild).GetValueOrDefault(false)) return [];
                deciding.TrySetResult();
                if (!releaseDecision.Wait(BusinessBlockTestSupport.TestTimeout)) throw new TimeoutException("test decision not released");
                return [104];
            })]);
        var startChildWriter = scheduler.Authority.ClaimWrite<bool>(TestStateUids.StartChild);
        Task? decision = null;
        try
        {
            await scheduler.StartAsync(107, BusinessBlockTestSupport.Reason());
            decision = startChildWriter.PublishAsync(true, BusinessBlockTestSupport.Reason());
            await deciding.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            // Disable the next parent run, without stopping the current entry through the scheduler.
            await scheduler.StopAsync(107, BusinessBlockTestSupport.Reason());
            parentReturn.TrySetResult();
            await BusinessBlockTestSupport.State(scheduler, 107, BusinessBlockState.Stopped);
            releaseDecision.Set();
            await decision.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await BusinessBlockTestSupport.State(scheduler, 104, BusinessBlockState.Stopped);
            Assert.Equal(child.Runs, child.Returns);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(107));
        }
        finally
        {
            parentReturn.TrySetResult(); releaseDecision.Set();
            if (decision is not null) await decision.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await BusinessBlockTestSupport.StopAll(scheduler);
        }
    }

    [Fact]
    public async Task LateRunningNotificationAfterReturnDoesNotStartAnotherChild()
    {
        var entered = BusinessBlockTestSupport.Signal();
        var parentReturn = BusinessBlockTestSupport.Signal();
        var releaseChild = BusinessBlockTestSupport.Signal();
        var logging = BusinessBlockTestSupport.Signal();
        using var releaseLog = new ManualResetEventSlim();
        BusinessBlockRunContext? parentContext = null;
        var parent = new BusinessBlockTestSupport.Block(async context =>
        {
            parentContext = context;
            entered.TrySetResult();
            await parentReturn.Task;
        });
        var child = new BusinessBlockTestSupport.Probe(releaseStop: releaseChild.Task);
        var lateChild = new BusinessBlockTestSupport.Probe();
        var control = new BusinessBlockController(new BlockingLogger(logging, releaseLog, "107: Starting -> Running"), 128, _ => null);
        await using var scheduler = new BusinessBlockScheduler(new BusinessBlockTestSupport.EmptyServices(), [
            BusinessBlockTestSupport.Register(107, parent), BusinessBlockTestSupport.Register(104, child, 107),
            BusinessBlockTestSupport.Register(127, lateChild, 107)],
            Microsoft.Extensions.Options.Options.Create(new BusinessBlockOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BusinessBlockScheduler>.Instance, BusinessBlockTestSupport.CreateAuthority(), control);
        Task? runningReport = null;
        try
        {
            await scheduler.StartAsync(107, BusinessBlockTestSupport.Reason());
            await entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            runningReport = Task.Run(() => parentContext!.ReportRunning());
            await logging.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.StartAsync(104, BusinessBlockTestSupport.Reason());
            await child.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            parentReturn.TrySetResult();
            await child.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(107));
            releaseLog.Set();
            await runningReport.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.StartAsync(127, BusinessBlockTestSupport.Reason());
            Assert.Equal(0, lateChild.Runs);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(127));
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(107));
        }
        finally
        {
            releaseLog.Set(); parentReturn.TrySetResult();
            if (runningReport is not null) await runningReport.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.StopAsync(107, BusinessBlockTestSupport.Reason());
            releaseChild.TrySetResult();
            await BusinessBlockTestSupport.StopAll(scheduler);
        }
    }

    [Fact]
    public async Task BlockedControlLogInOneBranchDoesNotBlockAnotherBlocksStateOrStart()
    {
        var logging = BusinessBlockTestSupport.Signal();
        using var releaseLog = new ManualResetEventSlim();
        var releaseEntries = BusinessBlockTestSupport.Signal();
        var control = new BusinessBlockController(new BlockingLogger(logging, releaseLog), 128, _ => null);
        var aStart = Task.Run(() => control.Start(101, async context => { context.ReportRunning(); await releaseEntries.Task; }));
        Task? bStart = null;
        try
        {
            await logging.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            bStart = Task.Run(async () =>
            {
                Assert.Equal(BusinessBlockState.Stopped, control.ReadState(103));
                control.Start(103, async context => { context.ReportRunning(); await releaseEntries.Task; });
                await BusinessBlockTestSupport.Until(() => control.ReadState(103) == BusinessBlockState.Running);
                Assert.Equal(BusinessBlockState.Running, control.ReadState(103));
            });
            await bStart.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseLog.Set();
            try
            {
                await aStart.WaitAsync(BusinessBlockTestSupport.TestTimeout);
                if (bStart is not null) await bStart.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            }
            finally
            {
                releaseEntries.TrySetResult();
                await BusinessBlockTestSupport.Until(() => control.ReadState(101) == BusinessBlockState.Stopped
                    && control.ReadState(103) == BusinessBlockState.Stopped);
            }
        }
    }

    private sealed class BlockingLogger(TaskCompletionSource entered, ManualResetEventSlim release,
        string blockedTransition = "101: Stopped -> Starting") : ILogger<BusinessBlockController>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (!format(state, error).Contains(blockedTransition, StringComparison.Ordinal)) return;
            entered.TrySetResult();
            if (!release.Wait(BusinessBlockTestSupport.TestTimeout)) throw new TimeoutException("test logger not released");
        }
    }
}
