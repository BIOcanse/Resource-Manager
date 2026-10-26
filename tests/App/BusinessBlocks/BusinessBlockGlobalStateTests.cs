using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockControlSystemTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourStatesStopCannotBeRefusedAndReadsArePure(bool stopWhileStarting)
    {
        var logger = new CapturedLogger<BusinessBlockController>();
        var control = new BusinessBlockController(logger, 4);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BusinessBlockRunContext? context = null;
        var calls = 0;
        var entered = BusinessBlockTestSupport.Signal();
        Assert.Equal([BusinessBlockState.Stopped, BusinessBlockState.Starting, BusinessBlockState.Running, BusinessBlockState.Stopping], Enum.GetValues<BusinessBlockState>());
        control.Start("one", async value =>
        {
            context = value;
            Interlocked.Increment(ref calls);
            using var registration = value.StopNotification.Register(() => notified.TrySetResult());
            entered.TrySetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
        Assert.Equal(BusinessBlockState.Starting, control.Read().States["one"]);
        var first = control.Read();
        Assert.Equal(first.History, control.Read().History);
        Assert.Equal(1, calls);
        if (!stopWhileStarting)
        {
            context!.ReportRunning();
            Assert.Equal(BusinessBlockState.Running, control.Read().States["one"]);
        }
        await control.Stop("one");
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BusinessBlockState.Stopping, control.Read().States["one"]);
        context!.ReportRunning();
        Assert.Equal(BusinessBlockState.Stopping, control.Read().States["one"]);
        Assert.Equal(BusinessBlockState.Stopping, control.ReadState("one"));
        release.SetResult();
        await BusinessBlockTestSupport.Until(() => control.ReadState("one") == BusinessBlockState.Stopped);
        Assert.Equal(BusinessBlockState.Stopped, control.Read().States["one"]);
        Assert.Equal(stopWhileStarting ? 3 : 4, control.Read().History.Count);
        Assert.Contains(logger.Messages, value => value.Contains("Stopped -> Starting"));
        Assert.Contains(logger.Messages, value => value.Contains("Stopping -> Stopped"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntryReturnIsStoppedRegardlessOfItsOutcome(bool throws)
    {
        var control = new BusinessBlockController(NullLogger<BusinessBlockController>.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.StateChanged += (id, state) => { if (state == BusinessBlockState.Stopped) returned.TrySetResult(id); };
        control.Start("one", async context =>
        {
            context.ReportRunning();
            await release.Task;
            if (throws) throw new IOException("entry failed");
        });
        await BusinessBlockTestSupport.Until(() => control.ReadState("one") == BusinessBlockState.Running);
        Assert.Equal(BusinessBlockState.Running, control.Read().States["one"]);
        release.SetResult();
        Assert.Equal("one", await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BusinessBlockState.Stopped, control.Read().States["one"]);
        Assert.DoesNotContain(control.Read().History, value => value.To == BusinessBlockState.Stopping);
    }
    internal sealed class CapturedLogger<T> : ILogger<T>
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
            => Messages.Enqueue(format(state, error));
    }
}

public sealed class BusinessBlockGlobalStateTests
{
    [Fact]
    public async Task ConcurrentChildReturnAndDemandWithdrawalAlwaysDrainBeforeTheNextParentRun()
    {
        const int rounds = 48;
        var parent = new BusinessBlockTestSupport.Probe();
        TaskCompletionSource? childReturn = null;
        var child = new BusinessBlockTestSupport.Block(async context =>
        {
            var release = BusinessBlockTestSupport.Signal();
            Volatile.Write(ref childReturn, release);
            using var stop = context.StopNotification.Register(() => release.TrySetResult());
            context.ReportRunning();
            await release.Task;
        });
        await using var scheduler = BusinessBlockTestSupport.Create([
            BusinessBlockTestSupport.Register("parent", parent), BusinessBlockTestSupport.Register("child", child, "parent")]);
        var reason = BusinessBlockTestSupport.Reason();
        try
        {
            for (var round = 0; round < rounds; round++)
            {
                await scheduler.SetDemandAsync("child-demand", "child", reason);
                await scheduler.SetDemandAsync("parent-demand", "parent", reason);
                await BusinessBlockTestSupport.Until(() => scheduler.ReadState("parent") == BusinessBlockState.Running
                    && scheduler.ReadState("child") == BusinessBlockState.Running);
                var returnThisChild = Volatile.Read(ref childReturn)!;
                var releasePublishers = BusinessBlockTestSupport.Signal();
                var operations = new[]
                {
                    Task.Run(async () => { await releasePublishers.Task; await scheduler.SetDemandAsync("parent-demand", null, reason); }),
                    Task.Run(async () => { await releasePublishers.Task; await scheduler.SetDemandAsync("child-demand", null, reason); }),
                    Task.Run(async () => { await releasePublishers.Task; returnThisChild.TrySetResult(); })
                };
                releasePublishers.TrySetResult();
                await Task.WhenAll(operations).WaitAsync(BusinessBlockTestSupport.TestTimeout);
                await BusinessBlockTestSupport.Until(() => scheduler.Read().Blocks.All(item => item.State == BusinessBlockState.Stopped));
                Assert.Empty(scheduler.Read().Demands);
            }
            Assert.Equal(rounds, parent.Runs);
            Assert.Equal(rounds, parent.Returns);
        }
        finally { Volatile.Read(ref childReturn)?.TrySetResult(); await BusinessBlockTestSupport.StopAll(scheduler); }
    }

    [Fact]
    public async Task WithdrawalWhileStartingImmediatelySendsStopAndWaitsForTheEntryToReturn()
    {
        var release = BusinessBlockTestSupport.Signal();
        var block = new BusinessBlockTestSupport.Probe(reportRunning: false, releaseStop: release.Task);
        await using var scheduler = BusinessBlockTestSupport.Create([BusinessBlockTestSupport.Register("worker", block)]);
        try
        {
            await scheduler.StartAsync("worker", BusinessBlockTestSupport.Reason());
            await block.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Starting, scheduler.ReadState("worker"));
            await scheduler.StopAsync("worker", BusinessBlockTestSupport.Reason());
            await block.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState("worker"));
            Assert.False(block.Returned.Task.IsCompleted);
            release.TrySetResult();
            await BusinessBlockTestSupport.State(scheduler, "worker", BusinessBlockState.Stopped);
            Assert.Contains(scheduler.Read().History, item => item.From == BusinessBlockState.Starting && item.To == BusinessBlockState.Stopping);
        }
        finally { release.TrySetResult(); await BusinessBlockTestSupport.StopAll(scheduler); }
    }

    [Theory]
    [InlineData(BusinessBlockRequestKind.Configuration)]
    [InlineData(BusinessBlockRequestKind.User)]
    [InlineData(BusinessBlockRequestKind.Dependency)]
    public async Task RootAcceptsAllThreeDemandKindsAndLastWithdrawalStopsTheBlock(BusinessBlockRequestKind kind)
    {
        var block = new BusinessBlockTestSupport.Probe();
        await using var scheduler = BusinessBlockTestSupport.Create([BusinessBlockTestSupport.Register("worker", block)]);
        var reason = BusinessBlockTestSupport.Reason(kind, "external-owner", "needs-output", 42);
        Assert.Empty(scheduler.Read().Demands);
        Assert.All(scheduler.Read().Blocks, item => Assert.Equal(BusinessBlockState.Stopped, item.State));
        try
        {
            await scheduler.SetDemandAsync("first", "worker", reason);
            await block.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.SetDemandAsync("second", "worker", reason);
            await scheduler.SetDemandAsync("first", null, reason);
            Assert.Equal(1, block.Runs);
            Assert.Equal(0, block.Returns);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState("worker"));
            var fact = scheduler.Read().Facts.Single(item => item.Key == BusinessBlockScheduler.DemandFact("worker"));
            Assert.True(fact.Value);
            Assert.Equal(reason, Assert.Single(fact.Reasons));
            Assert.Contains(scheduler.Read().Decisions, item => item.ParentId == BackendBlockGraph.Program && item.Reason == reason && item.ShouldRun.SequenceEqual(["worker"]));
            await scheduler.SetDemandAsync("second", null, reason);
            await BusinessBlockTestSupport.State(scheduler, "worker", BusinessBlockState.Stopped);
            Assert.Equal(1, block.Returns);
            Assert.Empty(scheduler.Read().Demands);
        }
        finally { await BusinessBlockTestSupport.StopAll(scheduler); }
    }

    [Fact]
    public async Task ParentMustReportRunningBeforeItsChildCanStart()
    {
        var parent = new BusinessBlockTestSupport.Probe(reportRunning: false);
        var child = new BusinessBlockTestSupport.Probe();
        await using var scheduler = BusinessBlockTestSupport.Create([
            BusinessBlockTestSupport.Register("parent", parent), BusinessBlockTestSupport.Register("child", child, "parent")]);
        try
        {
            await scheduler.StartAsync("child", BusinessBlockTestSupport.Reason());
            Assert.Equal(0, parent.Runs);
            Assert.Equal(0, child.Runs);
            await scheduler.StartAsync("parent", BusinessBlockTestSupport.Reason());
            await parent.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Starting, scheduler.ReadState("parent"));
            // The child's demand was evaluated, but its parent is still Starting.
            await scheduler.StartAsync("child", BusinessBlockTestSupport.Reason());
            Assert.Equal(0, child.Runs);
            parent.Context!.ReportRunning();
            await child.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState("child"));
        }
        finally { await BusinessBlockTestSupport.StopAll(scheduler); }
    }

    [Fact]
    public async Task ParentReceivesStopOnlyAfterEveryDescendantHasReturned()
    {
        var release = BusinessBlockTestSupport.Signal();
        var events = new ConcurrentQueue<string>();
        var parent = new BusinessBlockTestSupport.Probe("parent", events);
        var child = new BusinessBlockTestSupport.Probe("child", events);
        var leaf = new BusinessBlockTestSupport.Probe("leaf", events, releaseStop: release.Task);
        await using var scheduler = BusinessBlockTestSupport.Create([
            BusinessBlockTestSupport.Register("parent", parent), BusinessBlockTestSupport.Register("child", child, "parent"),
            BusinessBlockTestSupport.Register("leaf", leaf, "child")]);
        try
        {
            await scheduler.StartAsync("leaf", BusinessBlockTestSupport.Reason());
            await scheduler.StartAsync("child", BusinessBlockTestSupport.Reason());
            await scheduler.StartAsync("parent", BusinessBlockTestSupport.Reason());
            await leaf.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(["parent:start", "child:start", "leaf:start"], events);
            await scheduler.StopAsync("parent", BusinessBlockTestSupport.Reason());
            await leaf.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState("leaf"));
            Assert.False(child.Notified.Task.IsCompleted);
            Assert.False(parent.Notified.Task.IsCompleted);
            release.TrySetResult();
            await BusinessBlockTestSupport.State(scheduler, "parent", BusinessBlockState.Stopped);
            Assert.Equal(["parent:start", "child:start", "leaf:start", "leaf:return", "child:return", "parent:return"], events);
            Assert.All(scheduler.Read().Blocks, item => Assert.Equal(BusinessBlockState.Stopped, item.State));
        }
        finally { release.TrySetResult(); await BusinessBlockTestSupport.StopAll(scheduler); }
    }

    [Fact]
    public async Task SiblingsDoNotWaitForAnotherSiblingsPreparationOrCleanup()
    {
        using var prepare = new ManualResetEventSlim();
        var preparing = BusinessBlockTestSupport.Signal();
        var cleanup = BusinessBlockTestSupport.Signal();
        var slow = new BusinessBlockTestSupport.Probe(releaseStop: cleanup.Task);
        var fast = new BusinessBlockTestSupport.Probe();
        var preparingBlock = new BusinessBlockTestSupport.Block(async context =>
        {
            preparing.TrySetResult();
            if (!prepare.Wait(BusinessBlockTestSupport.TestTimeout)) throw new TimeoutException("preparation not released");
            await slow.RunAsync(context);
        });
        await using var scheduler = BusinessBlockTestSupport.Create([
            BusinessBlockTestSupport.Register("slow", preparingBlock), BusinessBlockTestSupport.Register("fast", fast)],
            [new(BackendBlockGraph.Program, ["group"], input => input.Facts["group"].Value ? ["slow", "fast"] : [])]);
        try
        {
            await scheduler.SetFactAsync("group", true, BusinessBlockTestSupport.Reason());
            await preparing.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await fast.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Starting, scheduler.ReadState("slow"));
            prepare.Set();
            await slow.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.SetFactAsync("group", false, BusinessBlockTestSupport.Reason());
            await slow.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await fast.Returned.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState("slow"));
        }
        finally { prepare.Set(); cleanup.TrySetResult(); await BusinessBlockTestSupport.StopAll(scheduler); }
    }

    [Fact]
    public void ControlTreeRejectsDuplicateUnknownParentMultipleParentAndCycle()
    {
        BusinessBlockRegistration Node(string id, string parent = BackendBlockGraph.Program, params string[] inputs)
            => new(id, inputs, _ => throw new InvalidOperationException("Validation must not construct a block.")) { ParentId = parent };
        Assert.Throws<ArgumentException>(() => new BackendBlockGraph([Node("same"), Node("same")]));
        Assert.Throws<KeyNotFoundException>(() => new BackendBlockGraph([Node("child", "missing")]));
        Assert.Throws<ArgumentException>(() => new BackendBlockGraph([Node("a"), Node("b"), Node("shared", "a"), Node("shared", "b")]));
        Assert.Throws<InvalidOperationException>(() => new BackendBlockGraph([Node("a", "b"), Node("b", "a")]));
        Assert.Throws<KeyNotFoundException>(() => new BackendBlockGraph([Node("consumer", BackendBlockGraph.Program, "missing-input")]));
    }

    [Fact]
    public void GraphCopiesDeclarationsAndAllowsCrossBranchDataCycles()
    {
        var inputs = new[] { "producer" };
        var outputs = new[] { "result" };
        var graph = new BackendBlockGraph([
            new("producer", ["consumer"], _ => null!), new("consumer", inputs, _ => null!) { Outputs = outputs }]);
        inputs[0] = "missing"; outputs[0] = "changed";
        Assert.Equal(["producer"], graph.Nodes.Single(item => item.Id == "consumer").Inputs);
        Assert.Equal(["result"], graph.Nodes.Single(item => item.Id == "consumer").Outputs);
        Assert.Equal(["producer", "consumer"], graph.Children(BackendBlockGraph.Program));
    }

    [Theory]
    [InlineData("full")]
    [InlineData("normal-read-only")]
    public void ProductionGraphIsClosedForEachStartupProfile(string profile)
    {
        var services = new ServiceCollection();
        ResourceManager.App.Hosting.ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(services, ["--startup-profile=" + profile]);
        var graph = new BackendBlockGraph(services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>());
        Assert.True(graph.Nodes.Count > 20);
        Assert.All(graph.Nodes, node => Assert.All(node.Inputs.Concat(node.Outputs), value => Assert.False(string.IsNullOrWhiteSpace(value))));
        Assert.DoesNotContain(graph.Nodes, node => node.Id.Contains("Frontend", StringComparison.OrdinalIgnoreCase) || node.Id.Contains("NativeUi", StringComparison.OrdinalIgnoreCase));
        if (profile == "normal-read-only") Assert.DoesNotContain(graph.Nodes, node => node.Id == "PortableSoftwareDiscoveryService");
    }

    [Fact]
    public void OnlyDecisionNodesCallControlAndNoGenericLayerOwnsAlternateCancellation()
    {
        var core = Path.Combine(SourceRoot(), "src", "Core");
        var files = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories).ToArray();
        foreach (var operation in new[] { "control.Start(", "control.Stop(" })
        {
            var callers = files.Where(file => File.ReadAllText(file).Contains(operation, StringComparison.Ordinal)).ToArray();
            Assert.Equal("BusinessBlockDecisionNode.cs", Path.GetFileName(Assert.Single(callers)));
        }
        var owners = files.Where(file => File.ReadAllText(file).Contains("BusinessBlockController", StringComparison.Ordinal)).Select(Path.GetFileName);
        Assert.All(owners, name => Assert.Contains(name, new[] { "BusinessBlockController.cs", "BusinessBlockDecisionNode.cs", "BusinessBlockScheduler.cs", "BusinessBlockServiceRegistration.cs" }));
        // All generic lifecycle layers are covered, including any newly added execution wrapper.
        // Legacy ETW code's internal session cancellation and judgments are explicitly outside this migration.
        var genericLayers = Directory.EnumerateFiles(Path.Combine(core, "Infrastructure", "RuntimeSpecialization", "BusinessBlocks"), "*.cs")
            .Where(file => Path.GetFileName(file) != "BusinessBlockController.cs");
        foreach (var file in genericLayers)
        {
            var code = File.ReadAllText(file);
            Assert.DoesNotContain("CancellationTokenSource", code);
            Assert.DoesNotMatch(@"\.\s*Cancel(?:Async|After)?\s*\(", code);
        }
    }
    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string file = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../.."));
}
