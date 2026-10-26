using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using static Resource_Manager_APP.Tests.BusinessBlockTestSupport;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockDependencyTests
{
    [Fact]
    public async Task ConcurrentRetargetsOfOneDemandLeaveOnlyTheFinalTargetsFactAndExecutionActive()
    {
        var ids = new[] { "first", "second", "third" };
        await using var scheduler = Create(ids.Select(id => Register(id, new Probe())));
        try
        {
            for (var round = 0; round < 32; round++)
            {
                var releasePublishers = Signal();
                var updates = Enumerable.Range(0, 12).Select(index => Task.Run(async () =>
                {
                    await releasePublishers.Task;
                    await scheduler.SetDemandAsync("one-owner", ids[index % ids.Length], Reason(owner: "publisher-" + index));
                })).ToArray();
                releasePublishers.TrySetResult();
                await Task.WhenAll(updates).WaitAsync(TestTimeout);
                var snapshot = scheduler.Read();
                var finalTarget = Assert.Single(snapshot.Demands).BlockId;
                foreach (var id in ids)
                    Assert.Equal(id == finalTarget, snapshot.Facts.Single(item => item.Key == BusinessBlockScheduler.DemandFact(id)).Value);
                await Until(() => ids.All(id => scheduler.ReadState(id) ==
                    (id == finalTarget ? BusinessBlockState.Running : BusinessBlockState.Stopped)));
            }
            await scheduler.SetDemandAsync("one-owner", null, Reason());
            await Until(() => scheduler.Read().Blocks.All(item => item.State == BusinessBlockState.Stopped));
        }
        finally { await StopAll(scheduler); }
    }

    [Fact]
    public async Task RuleReadsItsImmediateChildStatesWithoutOrderingTheirStartup()
    {
        var first = new Probe(reportRunning: false);
        var next = new Probe();
        await using var scheduler = Create([Register("first", first), Register("next", next)],
            [new(BackendBlockGraph.Program, ["requested"], input =>
            {
                Assert.Equal(new[] { "first", "next" }, input.Children.Keys.Order());
                return input.Facts["requested"].Value ? ["first", "next"] : [];
            })]);
        try
        {
            await scheduler.SetFactAsync("requested", true, Reason());
            await first.Entered.Task.WaitAsync(TestTimeout);
            Assert.Equal(BusinessBlockState.Starting, scheduler.ReadState("first"));
            await next.Entered.Task.WaitAsync(TestTimeout);
            Assert.Equal(1, next.Runs);
            first.Context!.ReportRunning();
            await next.Entered.Task.WaitAsync(TestTimeout);
            Assert.Contains(scheduler.Read().Decisions, item => item.ShouldRun.SequenceEqual(["first", "next"]));
            var history = scheduler.Read().History.ToArray();
            await scheduler.SetFactAsync("requested", true, Reason());
            Assert.Equal(history, scheduler.Read().History);
            Assert.Equal(1, first.Runs);
            Assert.Equal(1, next.Runs);
        }
        finally { await StopAll(scheduler); }
    }

    [Fact]
    public async Task MultipleExternalNeedsRemainFactsUntilTheLastLeaseIsReleased()
    {
        var producer = new Probe();
        await using var scheduler = Create([Register("producer", producer)]);
        var firstReason = Reason(BusinessBlockRequestKind.Dependency, "first-reader", "needs-value");
        var secondReason = Reason(BusinessBlockRequestKind.Dependency, "second-reader", "needs-value");
        var first = scheduler.AcquireDependency("producer", firstReason);
        var second = scheduler.AcquireDependency("producer", secondReason);
        try
        {
            await Task.WhenAll(first.Ready, second.Ready).WaitAsync(TestTimeout);
            await producer.Entered.Task.WaitAsync(TestTimeout);
            Assert.Equal(1, producer.Runs);
            var fact = scheduler.Read().Facts.Single(item => item.Key == BusinessBlockScheduler.DemandFact("producer"));
            Assert.True(fact.Value);
            Assert.Contains(firstReason, fact.Reasons);
            Assert.Contains(secondReason, fact.Reasons);
            first.Dispose();
            await first.Released.WaitAsync(TestTimeout);
            Assert.Single(scheduler.Read().Demands);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState("producer"));
            Assert.Equal(0, producer.Returns);
            await second.DisposeAsync();
            await State(scheduler, "producer", BusinessBlockState.Stopped);
            Assert.Empty(scheduler.Read().Demands);
            Assert.False(scheduler.Read().Facts.Single(item => item.Key == BusinessBlockScheduler.DemandFact("producer")).Value);
        }
        finally { first.Dispose(); second.Dispose(); await StopAll(scheduler); }
    }

    [Fact]
    public async Task CrossBranchDataInputNeverStartsItsProducerOrParent()
    {
        var producerParent = new Probe();
        var consumerParent = new Probe();
        var producer = new Probe();
        var consumer = new Probe();
        await using var scheduler = Create([
            Register("producer-parent", producerParent), Register("consumer-parent", consumerParent),
            Register("producer", producer, "producer-parent"), Register("consumer", consumer, "consumer-parent", inputs: ["producer"])]);
        try
        {
            await scheduler.StartAsync("consumer", Reason());
            await scheduler.StartAsync("consumer-parent", Reason());
            await consumer.Entered.Task.WaitAsync(TestTimeout);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState("consumer"));
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState("producer-parent"));
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState("producer"));
            Assert.Equal(0, producer.Runs);
            Assert.Equal(0, producerParent.Runs);
            Assert.Equal(["producer"], scheduler.Read().Graph.Single(item => item.Id == "consumer").Inputs);
        }
        finally { await StopAll(scheduler); }
    }

    [Fact]
    public async Task DecisionRulesSeeOnlyDeclaredFactsAndImmediateChildrenAndDoNotWakeSiblings()
    {
        var leftCalls = 0;
        var rightCalls = 0;
        var left = new Probe(); var right = new Probe();
        BusinessBlockDecisionRegistration Rule(string parent, string child, string key, Action called)
            => new(parent, [key], input =>
            {
                called();
                Assert.Equal([child], input.Children.Keys);
                Assert.Equal(new[] { key, BusinessBlockScheduler.EnabledFact(child) }.Order(), input.Facts.Keys.Order());
                return input.Facts[key].Value ? [child] : [];
            });
        await using var scheduler = Create([
            Register("left", new Probe()), Register("right", new Probe()), Register("left-child", left, "left"), Register("right-child", right, "right")],
            [Rule("left", "left-child", "left-fact", () => Interlocked.Increment(ref leftCalls)),
             Rule("right", "right-child", "right-fact", () => Interlocked.Increment(ref rightCalls))]);
        try
        {
            await scheduler.StartAsync("left", Reason());
            await scheduler.StartAsync("right", Reason());
            await State(scheduler, "right", BusinessBlockState.Running);
            await scheduler.SetFactAsync("left-fact", false, Reason());
            await scheduler.SetFactAsync("right-fact", false, Reason());
            var unrelatedCalls = Volatile.Read(ref rightCalls);
            var unrelatedHistory = scheduler.Read().Decisions.Where(item => item.ParentId == "right").ToArray();
            var reason = Reason(BusinessBlockRequestKind.Configuration, "authority", "left-fact", 12);
            await scheduler.SetFactAsync("left-fact", true, reason);
            await left.Entered.Task.WaitAsync(TestTimeout);
            await scheduler.SetFactAsync("left-fact", true, reason);
            Assert.Equal(1, left.Runs);
            Assert.Equal(0, left.Returns);
            Assert.Equal(0, right.Runs);
            Assert.Equal(unrelatedCalls, Volatile.Read(ref rightCalls));
            Assert.Equal(unrelatedHistory, scheduler.Read().Decisions.Where(item => item.ParentId == "right"));
            var decision = scheduler.Read().Decisions.Last(item => item.ParentId == "left");
            Assert.Equal(reason, decision.Reason);
            Assert.Equal(["left-child"], decision.ShouldRun);
            Assert.Equal(reason, Assert.Single(decision.Facts.Single(item => item.Key == "left-fact").Reasons));
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
        var demandKey = BusinessBlockScheduler.DemandFact("worker");
        await using var scheduler = Create([Register("worker", worker)],
            [new(BackendBlockGraph.Program, [demandKey, "block-rule"], input =>
            {
                if (input.Facts["block-rule"].Value && Interlocked.Exchange(ref blockOnce, 1) == 0)
                {
                    enteredRule.TrySetResult();
                    if (!releaseRule.Wait(TestTimeout)) throw new TimeoutException("The test did not release its rule.");
                }
                return input.Facts[demandKey].Value ? ["worker"] : [];
            })]);
        try
        {
            await scheduler.SetDemandAsync("lease", "worker", Reason());
            await worker.Entered.Task.WaitAsync(TestTimeout);
            var blocked = scheduler.SetFactAsync("block-rule", true, Reason());
            await enteredRule.Task.WaitAsync(TestTimeout);
            var withdrawal = scheduler.SetDemandAsync("lease", null, Reason());
            var reacquire = scheduler.SetDemandAsync("lease", "worker", Reason());
            releaseRule.Set();
            await Task.WhenAll(blocked, withdrawal, reacquire).WaitAsync(TestTimeout);
            Assert.Equal(1, worker.Runs);
            Assert.Equal(0, worker.Returns);
            Assert.False(worker.Notified.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState("worker"));
            Assert.Single(scheduler.Read().Demands);
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
            Register("left", new Probe()), Register("right", new Probe()), Register("left-child", new Probe(), "left"), Register("right-child", right, "right")],
            [new("left", ["left-fact"], input =>
            {
                if (input.Facts["left-fact"].Value)
                {
                    enteredRule.TrySetResult();
                    if (!releaseRule.Wait(TestTimeout)) throw new TimeoutException("The test did not release its branch.");
                }
                return [];
            }), new("right", ["right-fact"], input => input.Facts["right-fact"].Value ? ["right-child"] : [])]);
        try
        {
            await scheduler.StartAsync("left", Reason());
            await scheduler.StartAsync("right", Reason());
            await State(scheduler, "left", BusinessBlockState.Running);
            await State(scheduler, "right", BusinessBlockState.Running);
            var blocked = scheduler.SetFactAsync("left-fact", true, Reason());
            await enteredRule.Task.WaitAsync(TestTimeout);
            Assert.False(blocked.IsCompleted);
            await scheduler.SetFactAsync("right-fact", true, Reason()).WaitAsync(TestTimeout);
            await right.Entered.Task.WaitAsync(TestTimeout);
            Assert.False(blocked.IsCompleted);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState("right-child"));
            releaseRule.Set();
            await blocked.WaitAsync(TestTimeout);
        }
        finally { releaseRule.Set(); await StopAll(scheduler); }
    }

    [Fact]
    public async Task RuleCannotControlANonChild()
    {
        await using var scheduler = Create([Register("parent", new Probe()), Register("own-child", new Probe(), "parent"), Register("sibling", new Probe())],
            [new("parent", ["bad-rule"], input => input.Facts["bad-rule"].Value ? ["sibling"] : [])]);
        try
        {
            await scheduler.StartAsync("parent", Reason());
            await State(scheduler, "parent", BusinessBlockState.Running);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.SetFactAsync("bad-rule", true, Reason()));
            Assert.Contains("non-child sibling", error.Message);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState("sibling"));
        }
        finally { await StopAll(scheduler); }
    }
}
