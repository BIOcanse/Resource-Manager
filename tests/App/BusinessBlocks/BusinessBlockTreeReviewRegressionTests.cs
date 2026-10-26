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
            BusinessBlockTestSupport.Register("A", a), BusinessBlockTestSupport.Register("Z", z),
            BusinessBlockTestSupport.Register("A-child", aChild, "A"), BusinessBlockTestSupport.Register("Z-child", zChild, "Z")]);
        try
        {
            await scheduler.StartAsync("A", BusinessBlockTestSupport.Reason());
            await scheduler.StartAsync("Z", BusinessBlockTestSupport.Reason());
            await scheduler.StartAsync("Z-child", BusinessBlockTestSupport.Reason());
            await zChild.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.ShutdownAsync(BusinessBlockTestSupport.Reason());
            await zChild.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            // An unrelated sibling must finish even while Z's whole branch is still draining.
            await a.Returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await scheduler.StartAsync("A-child", BusinessBlockTestSupport.Reason());
            Assert.Equal(0, aChild.Runs);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState("A-child"));
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
            BusinessBlockTestSupport.Register("U", upper), BusinessBlockTestSupport.Register("P", middle, "U"),
            BusinessBlockTestSupport.Register("L", leaf, "P")]);
        try
        {
            foreach (var id in new[] { "L", "P", "U" }) await scheduler.StartAsync(id, BusinessBlockTestSupport.Reason());
            await leaf.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            returnMiddle.TrySetResult();
            await BusinessBlockTestSupport.State(scheduler, "P", BusinessBlockState.Stopped);
            await leaf.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.StopAsync("U", BusinessBlockTestSupport.Reason());
            // Force a completed U-level evaluation while L is deliberately still cleaning up.
            await scheduler.StopAsync("P", BusinessBlockTestSupport.Reason());
            Assert.False(scheduler.Read().Facts.Single(fact => fact.Key == BusinessBlockScheduler.ChildrenStoppedFact("U")).Value);
            Assert.False(upper.Notified.Task.IsCompleted);
            await scheduler.StartAsync("U", BusinessBlockTestSupport.Reason());
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
            BusinessBlockTestSupport.Register("P", parent), BusinessBlockTestSupport.Register("C", child, "P")],
            [new("P", ["start-child"], input =>
            {
                if (!input.Facts["start-child"].Value) return [];
                deciding.TrySetResult();
                if (!releaseDecision.Wait(BusinessBlockTestSupport.TestTimeout)) throw new TimeoutException("test decision not released");
                return ["C"];
            })]);
        Task? decision = null;
        try
        {
            await scheduler.StartAsync("P", BusinessBlockTestSupport.Reason());
            decision = scheduler.SetFactAsync("start-child", true, BusinessBlockTestSupport.Reason());
            await deciding.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            // Disable the next parent run, without stopping the current entry through the scheduler.
            await scheduler.StopAsync("P", BusinessBlockTestSupport.Reason());
            parentReturn.TrySetResult();
            await BusinessBlockTestSupport.State(scheduler, "P", BusinessBlockState.Stopped);
            releaseDecision.Set();
            await decision.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await BusinessBlockTestSupport.State(scheduler, "C", BusinessBlockState.Stopped);
            Assert.Equal(child.Runs, child.Returns);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState("P"));
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
        var control = new BusinessBlockController(new BlockingLogger(logging, releaseLog, "P: Starting -> Running"));
        await using var scheduler = new BusinessBlockScheduler(new BusinessBlockTestSupport.EmptyServices(), [
            BusinessBlockTestSupport.Register("P", parent), BusinessBlockTestSupport.Register("C", child, "P"),
            BusinessBlockTestSupport.Register("late", lateChild, "P")],
            Microsoft.Extensions.Options.Options.Create(new BusinessBlockOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BusinessBlockScheduler>.Instance, control);
        Task? runningReport = null;
        try
        {
            await scheduler.StartAsync("P", BusinessBlockTestSupport.Reason());
            await entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            runningReport = Task.Run(() => parentContext!.ReportRunning());
            await logging.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.StartAsync("C", BusinessBlockTestSupport.Reason());
            await child.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            parentReturn.TrySetResult();
            await child.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState("P"));
            releaseLog.Set();
            await runningReport.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.StartAsync("late", BusinessBlockTestSupport.Reason());
            Assert.Equal(0, lateChild.Runs);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState("late"));
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState("P"));
        }
        finally
        {
            releaseLog.Set(); parentReturn.TrySetResult();
            if (runningReport is not null) await runningReport.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.StopAsync("P", BusinessBlockTestSupport.Reason());
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
        var control = new BusinessBlockController(new BlockingLogger(logging, releaseLog));
        var aStart = Task.Run(() => control.Start("A", async context => { context.ReportRunning(); await releaseEntries.Task; }));
        Task? bStart = null;
        try
        {
            await logging.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            bStart = Task.Run(async () =>
            {
                Assert.Equal(BusinessBlockState.Stopped, control.ReadState("B"));
                control.Start("B", async context => { context.ReportRunning(); await releaseEntries.Task; });
                await BusinessBlockTestSupport.Until(() => control.ReadState("B") == BusinessBlockState.Running);
                Assert.Equal(BusinessBlockState.Running, control.ReadState("B"));
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
                await BusinessBlockTestSupport.Until(() => control.ReadState("A") == BusinessBlockState.Stopped
                    && control.ReadState("B") == BusinessBlockState.Stopped);
            }
        }
    }

    private sealed class BlockingLogger(TaskCompletionSource entered, ManualResetEventSlim release,
        string blockedTransition = "A: Stopped -> Starting") : ILogger<BusinessBlockController>
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
