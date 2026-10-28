using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using static Resource_Manager_APP.Tests.BusinessBlockTestSupport;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockDependencyTests
{
    [Fact]
    public async Task RuleReadsItsImmediateChildStatesWithoutOrderingTheirStartup()
    {
        var first = new Probe(reportRunning: false);
        var next = new Probe();
        await using var scheduler = Create([Register(124, first), Register(135, next)],
            [new(BackendUids.Program, [TestStateUids.Requested], input =>
            {
                Assert.Equal(new[] { 124, 135 }, input.Children.Keys.Order());
                return input.Read<bool>(TestStateUids.Requested).GetValueOrDefault(false) ? [124, 135] : [];
            })]);
        var requestedWriter = scheduler.Authority.ClaimWrite<bool>(TestStateUids.Requested);
        try
        {
            await requestedWriter.PublishAsync(true, Reason());
            await first.Entered.Task.WaitAsync(TestTimeout);
            Assert.Equal(BusinessBlockState.Starting, scheduler.ReadState(124));
            await next.Entered.Task.WaitAsync(TestTimeout);
            Assert.Equal(1, next.Runs);
            first.Context!.ReportRunning();
            await next.Entered.Task.WaitAsync(TestTimeout);
            Assert.Contains(scheduler.Read().Decisions, item => item.ShouldRun.SequenceEqual([124, 135]));
            var history = scheduler.Read().History.ToArray();
            await requestedWriter.PublishAsync(true, Reason());
            Assert.Equal(history, scheduler.Read().History);
            Assert.Equal(1, first.Runs);
            Assert.Equal(1, next.Runs);
        }
        finally { await StopAll(scheduler); }
    }

    [Fact]
    public async Task CrossBranchDataInputNeverStartsItsProducerOrParent()
    {
        var producerParent = new Probe();
        var consumerParent = new Probe();
        var producer = new Probe();
        var consumer = new Probe();
        await using var scheduler = Create([
            Register(142, producerParent), Register(121, consumerParent),
            Register(141, producer, 142), Register(120, consumer, 121, inputs: [141])]);
        try
        {
            await scheduler.StartAsync(120, Reason());
            await scheduler.StartAsync(121, Reason());
            await consumer.Entered.Task.WaitAsync(TestTimeout);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(120));
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(142));
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(141));
            Assert.Equal(0, producer.Runs);
            Assert.Equal(0, producerParent.Runs);
            Assert.Equal([141], scheduler.Read().Graph.Single(item => item.Id == 120).Inputs);
        }
        finally { await StopAll(scheduler); }
    }

    [Fact]
    public async Task DecisionRulesSeeOnlyDeclaredFactsAndImmediateChildrenAndDoNotWakeSiblings()
    {
        var leftCalls = 0;
        var rightCalls = 0;
        var left = new Probe(); var right = new Probe();
        BusinessBlockDecisionRegistration Rule(int parent, int child, StateItemKey key, Action called)
            => new(parent, [key], input =>
            {
                called();
                Assert.Equal([child], input.Children.Keys);
                Assert.Equal([key], input.ItemKeys);
                return input.Read<bool>(key).GetValueOrDefault(false) ? [child] : [];
            });
        await using var scheduler = Create([
            Register(131, new Probe()), Register(143, new Probe()), Register(132, left, 131), Register(144, right, 143)],
            [Rule(131, 132, TestStateUids.Left, () => Interlocked.Increment(ref leftCalls)),
             Rule(143, 144, TestStateUids.Right, () => Interlocked.Increment(ref rightCalls))]);
        var leftWriter = scheduler.Authority.ClaimWrite<bool>(TestStateUids.Left);
        var rightWriter = scheduler.Authority.ClaimWrite<bool>(TestStateUids.Right);
        try
        {
            await scheduler.StartAsync(131, Reason());
            await scheduler.StartAsync(143, Reason());
            await State(scheduler, 143, BusinessBlockState.Running);
            await leftWriter.PublishAsync(false, Reason());
            await rightWriter.PublishAsync(false, Reason());
            var unrelatedCalls = Volatile.Read(ref rightCalls);
            var unrelatedHistory = scheduler.Read().Decisions.Where(item => item.ParentId == 143).ToArray();
            var reason = Reason(BusinessBlockRequestKind.Configuration, "authority", "left-fact", 12);
            await leftWriter.PublishAsync(true, reason);
            await left.Entered.Task.WaitAsync(TestTimeout);
            await leftWriter.PublishAsync(true, reason);
            Assert.Equal(1, left.Runs);
            Assert.Equal(0, left.Returns);
            Assert.Equal(0, right.Runs);
            Assert.Equal(unrelatedCalls, Volatile.Read(ref rightCalls));
            Assert.Equal(unrelatedHistory, scheduler.Read().Decisions.Where(item => item.ParentId == 143));
            var decision = scheduler.Read().Decisions.Last(item => item.ParentId == 131);
            Assert.Equal(reason, decision.Reason);
            Assert.Equal([132], decision.ShouldRun);
            Assert.True(Assert.IsType<bool>(Assert.Single(decision.Items, item => item.ItemUid == TestStateUids.Left).Value));
        }
        finally { await StopAll(scheduler); }
    }

    [Fact]
    public async Task WithdrawalAndReacquireBeforeDecisionUseCurrentFactsWithoutRestart()
    {
        using var releaseRule = new ManualResetEventSlim();
        var enteredRule = Signal();
        var blockOnce = 0;
        var worker = new Probe();
        var executionKey = BusinessBlockTestSupport.ExecutionKey(154);
        await using var scheduler = Create([Register(154, worker)],
            [new(BackendUids.Program, [executionKey, TestStateUids.BlockRule], input =>
            {
                if (input.Read<bool>(TestStateUids.BlockRule).GetValueOrDefault(false) && Interlocked.Exchange(ref blockOnce, 1) == 0)
                {
                    enteredRule.TrySetResult();
                    if (!releaseRule.Wait(TestTimeout)) throw new TimeoutException("The test did not release its rule.");
                }
                return input.Read<bool>(executionKey).GetValueOrDefault(false) ? [154] : [];
            })]);
        var blockRuleWriter = scheduler.Authority.ClaimWrite<bool>(TestStateUids.BlockRule);
        try
        {
            await scheduler.StartAsync(154, Reason());
            await worker.Entered.Task.WaitAsync(TestTimeout);
            var blocked = blockRuleWriter.PublishAsync(true, Reason());
            await enteredRule.Task.WaitAsync(TestTimeout);
            var withdrawal = scheduler.StopAsync(154, Reason());
            var reacquire = scheduler.StartAsync(154, Reason());
            releaseRule.Set();
            await Task.WhenAll(blocked, withdrawal, reacquire).WaitAsync(TestTimeout);
            Assert.Equal(1, worker.Runs);
            Assert.Equal(0, worker.Returns);
            Assert.False(worker.Notified.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(154));
            Assert.True(scheduler.Authority.Read<bool>(ExecutionKey(154)).Value);
        }
        finally { releaseRule.Set(); await StopAll(scheduler); }
    }

    [Fact]
    public async Task BlockedDecisionInOneBranchDoesNotDelayAnotherBranch()
    {
        using var releaseRule = new ManualResetEventSlim();
        var enteredRule = Signal();
        var right = new Probe();
        await using var scheduler = Create([
            Register(131, new Probe()), Register(143, new Probe()), Register(132, new Probe(), 131), Register(144, right, 143)],
            [new(131, [TestStateUids.Left], input =>
            {
                if (input.Read<bool>(TestStateUids.Left).GetValueOrDefault(false))
                {
                    enteredRule.TrySetResult();
                    if (!releaseRule.Wait(TestTimeout)) throw new TimeoutException("The test did not release its branch.");
                }
                return [];
            }), new(143, [TestStateUids.Right], input => input.Read<bool>(TestStateUids.Right).GetValueOrDefault(false) ? [144] : [])]);
        var leftWriter = scheduler.Authority.ClaimWrite<bool>(TestStateUids.Left);
        var rightWriter = scheduler.Authority.ClaimWrite<bool>(TestStateUids.Right);
        try
        {
            await scheduler.StartAsync(131, Reason());
            await scheduler.StartAsync(143, Reason());
            await State(scheduler, 131, BusinessBlockState.Running);
            await State(scheduler, 143, BusinessBlockState.Running);
            var blocked = leftWriter.PublishAsync(true, Reason());
            await enteredRule.Task.WaitAsync(TestTimeout);
            Assert.False(blocked.IsCompleted);
            await rightWriter.PublishAsync(true, Reason()).WaitAsync(TestTimeout);
            await right.Entered.Task.WaitAsync(TestTimeout);
            Assert.False(blocked.IsCompleted);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(144));
            releaseRule.Set();
            await blocked.WaitAsync(TestTimeout);
        }
        finally { releaseRule.Set(); await StopAll(scheduler); }
    }

    [Fact]
    public async Task RuleCannotControlANonChild()
    {
        await using var scheduler = Create([Register(139, new Probe()), Register(138, new Probe(), 139), Register(149, new Probe())],
            [new(139, [TestStateUids.BadRule], input => input.Read<bool>(TestStateUids.BadRule).GetValueOrDefault(false) ? [149] : [])]);
        var badRuleWriter = scheduler.Authority.ClaimWrite<bool>(TestStateUids.BadRule);
        try
        {
            await scheduler.StartAsync(139, Reason());
            await State(scheduler, 139, BusinessBlockState.Running);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => badRuleWriter.PublishAsync(true, Reason()));
            Assert.Contains("non-child 149", error.Message);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(149));
        }
        finally { await StopAll(scheduler); }
    }
}
