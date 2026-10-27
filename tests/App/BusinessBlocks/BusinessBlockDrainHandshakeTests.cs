using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockDrainHandshakeTests
{
    [Fact]
    public async Task StopDecisionDoesNotWaitForChildDispatchAndWaitsForItsWholeBranchToDrain()
    {
        var childReturn = BusinessBlockTestSupport.Signal();
        var decisionEntered = BusinessBlockTestSupport.Signal();
        var returnCallbackEntered = BusinessBlockTestSupport.Signal();
        var returnCallbackExited = BusinessBlockTestSupport.Signal();
        var leafDispatchEntered = BusinessBlockTestSupport.Signal();
        var releaseLeafCleanup = BusinessBlockTestSupport.Signal();
        using var releaseDecision = new ManualResetEventSlim();
        using var releaseReturnCallback = new ManualResetEventSlim();
        using var releaseLeafDispatch = new ManualResetEventSlim();
        var decisionHeld = 0;
        var callbackHeld = 0;
        var grandparent = new BusinessBlockTestSupport.Probe();
        var parent = new BusinessBlockTestSupport.Probe();
        var child = new BusinessBlockTestSupport.Block(async context =>
        {
            context.ReportRunning();
            await childReturn.Task;
        });
        var leaf = new BusinessBlockTestSupport.Probe(releaseStop: releaseLeafCleanup.Task);
        var control = new BusinessBlockController(NullLogger<BusinessBlockController>.Instance, 128, _ => null);
        control.StateChanged += (id, state) =>
        {
            if (id == 104 && state == BusinessBlockState.Stopped && Interlocked.Exchange(ref callbackHeld, 1) == 0)
            {
                returnCallbackEntered.TrySetResult();
                try { Wait(releaseReturnCallback); }
                finally { returnCallbackExited.TrySetResult(); }
            }
            if (id == 106 && state == BusinessBlockState.Starting)
            {
                leafDispatchEntered.TrySetResult();
                Wait(releaseLeafDispatch);
            }
        };
        await using var scheduler = new TestBlockScheduler(
            [BusinessBlockTestSupport.Register(105, grandparent), BusinessBlockTestSupport.Register(107, parent, 105),
             BusinessBlockTestSupport.Register(104, child, 107), BusinessBlockTestSupport.Register(106, leaf, 104)],
            BusinessBlockTestSupport.CreateAuthority(), control: control,
            decisions: [new(107, [BusinessBlockTestSupport.DemandKey(104), TestStateUids.ParentProbe], input =>
                input.Read<bool>(BusinessBlockTestSupport.DemandKey(104)).GetValueOrDefault(false) ? [104] : []),
             new(104, [BusinessBlockTestSupport.DemandKey(106)], input =>
             {
                 if (!input.Read<bool>(BusinessBlockTestSupport.DemandKey(106)).GetValueOrDefault(false)) return [];
                 if (Interlocked.Exchange(ref decisionHeld, 1) == 0)
                 {
                     decisionEntered.TrySetResult();
                     Wait(releaseDecision);
                 }
                 return [106];
             })]);
        var parentProbeWriter = scheduler.Authority.ClaimWrite<bool>(TestStateUids.ParentProbe);
        Task? leafDemand = null;
        Task? ancestorStop = null;
        Task? parentDecision = null;
        try
        {
            foreach (var id in new[] { 105, 107, 104 }) await scheduler.StartAsync(id, BusinessBlockTestSupport.Reason());
            await BusinessBlockTestSupport.State(scheduler, 104, BusinessBlockState.Running);
            await BusinessBlockTestSupport.State(scheduler, 106, BusinessBlockState.Stopped);
            leafDemand = scheduler.StartAsync(106, BusinessBlockTestSupport.Reason());
            await decisionEntered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            childReturn.TrySetResult();
            await returnCallbackEntered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);

            // Let the old decision dispatch, but hold its synchronous Starting notification.
            // The ancestor must be able to withdraw permission without waiting for that dispatch.
            releaseDecision.Set();
            await leafDispatchEntered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            ancestorStop = scheduler.StopAsync(107, BusinessBlockTestSupport.Reason());
            parentDecision = parentProbeWriter.PublishAsync(true, BusinessBlockTestSupport.Reason());
            await Task.WhenAll(ancestorStop, parentDecision).WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(104));
            Assert.Equal(BusinessBlockState.Starting, scheduler.ReadState(106));
            Assert.False(parent.Notified.Task.IsCompleted);

            releaseLeafDispatch.Set();
            await leafDemand.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await leaf.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(106));
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(104));
            Assert.False(parent.Notified.Task.IsCompleted);

            releaseReturnCallback.Set();
            await returnCallbackExited.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            releaseLeafCleanup.TrySetResult();
            await parent.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await BusinessBlockTestSupport.State(scheduler, 107, BusinessBlockState.Stopped);
            var transitions = control.Read().History;
            var leafStopped = transitions.Single(item => item.BlockId == 106 && item.To == BusinessBlockState.Stopped);
            var parentStopping = transitions.Single(item => item.BlockId == 107 && item.To == BusinessBlockState.Stopping);
            Assert.True(leafStopped.Sequence < parentStopping.Sequence);
        }
        finally
        {
            childReturn.TrySetResult();
            releaseDecision.Set();
            releaseReturnCallback.Set();
            releaseLeafDispatch.Set();
            releaseLeafCleanup.TrySetResult();
            foreach (var operation in new[] { leafDemand, ancestorStop, parentDecision }.OfType<Task>())
                await operation.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            if (returnCallbackEntered.Task.IsCompleted) await returnCallbackExited.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await BusinessBlockTestSupport.StopAll(scheduler);
        }
    }

    [Fact]
    public async Task RestartWaitsForRevocationAcknowledgementWhileAnOldDecisionAndReturnCallbackAreHeld()
    {
        var parentReturn = BusinessBlockTestSupport.Signal();
        var decisionEntered = BusinessBlockTestSupport.Signal();
        var callbackEntered = BusinessBlockTestSupport.Signal();
        var callbackExited = BusinessBlockTestSupport.Signal();
        var releaseChild = BusinessBlockTestSupport.Signal();
        using var releaseDecision = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var parentRuns = 0;
        var decisionHeld = 0;
        var callbackHeld = 0;
        var parent = new BusinessBlockTestSupport.Block(async context =>
        {
            var run = Interlocked.Increment(ref parentRuns);
            context.ReportRunning();
            if (run == 1) await parentReturn.Task;
            else await WaitForStop(context.StopNotification);
        });
        var child = new BusinessBlockTestSupport.Probe(releaseStop: releaseChild.Task);
        var control = new BusinessBlockController(NullLogger<BusinessBlockController>.Instance, 128, _ => null);
        // Subscribe before the scheduler: the true Stopped state is visible, but neither node
        // has received the return notification. No production instrumentation is needed.
        control.StateChanged += (id, state) =>
        {
            if (id != 114 || state != BusinessBlockState.Stopped || Interlocked.Exchange(ref callbackHeld, 1) != 0) return;
            callbackEntered.TrySetResult();
            try { Wait(releaseCallback); }
            finally { callbackExited.TrySetResult(); }
        };
        await using var scheduler = new TestBlockScheduler(
            [BusinessBlockTestSupport.Register(114, parent), BusinessBlockTestSupport.Register(115, child, 114)],
            BusinessBlockTestSupport.CreateAuthority(), control: control,
            decisions: [new(114, [BusinessBlockTestSupport.DemandKey(115)], input =>
            {
                if (!input.Read<bool>(BusinessBlockTestSupport.DemandKey(115)).GetValueOrDefault(false)) return [];
                if (Interlocked.Exchange(ref decisionHeld, 1) == 0)
                {
                    decisionEntered.TrySetResult();
                    Wait(releaseDecision);
                }
                return [115];
            })]);
        Task? childDemand = null;
        try
        {
            await scheduler.StartAsync(114, BusinessBlockTestSupport.Reason());
            await BusinessBlockTestSupport.State(scheduler, 114, BusinessBlockState.Running);
            await BusinessBlockTestSupport.State(scheduler, 115, BusinessBlockState.Stopped);
            childDemand = scheduler.StartAsync(115, BusinessBlockTestSupport.Reason());
            await decisionEntered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            parentReturn.TrySetResult();
            await callbackEntered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);

            // An unchanged fact still wakes the root. Its completed decision is our barrier:
            // the root must revoke permission, not reuse the earlier "children stopped" report.
            await scheduler.SetDemandAsync("test:114", 114, BusinessBlockTestSupport.Reason(cause: "root-pulse"))
                .WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(114));
            Assert.Equal(1, Volatile.Read(ref parentRuns));

            releaseDecision.Set();
            await childDemand.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await child.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(114));
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(115));
            Assert.Equal(1, Volatile.Read(ref parentRuns));

            releaseCallback.Set();
            await callbackExited.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            releaseChild.TrySetResult();
            await BusinessBlockTestSupport.Until(() => Volatile.Read(ref parentRuns) == 2 && child.Runs == 2
                && scheduler.ReadState(114) == BusinessBlockState.Running && scheduler.ReadState(115) == BusinessBlockState.Running);
            var transitions = control.Read().History;
            var childStopped = transitions.First(item => item.BlockId == 115 && item.To == BusinessBlockState.Stopped);
            var parentRestarted = transitions.Where(item => item.BlockId == 114 && item.To == BusinessBlockState.Starting).Skip(1).Single();
            Assert.True(childStopped.Sequence < parentRestarted.Sequence);
        }
        finally
        {
            parentReturn.TrySetResult();
            releaseDecision.Set();
            releaseCallback.Set();
            releaseChild.TrySetResult();
            if (childDemand is not null) await childDemand.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            if (callbackEntered.Task.IsCompleted) await callbackExited.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await BusinessBlockTestSupport.StopAll(scheduler);
        }
    }

    private static void Wait(ManualResetEventSlim gate)
    {
        // Longer than the assertion deadline: a failing assertion, not a gate timeout,
        // releases the deliberately blocked production call in finally.
        if (!gate.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Deterministic handshake gate was not released.");
    }

    private static async Task WaitForStop(CancellationToken stop)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stop); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
