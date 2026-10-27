using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockControlSystemTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourStatesStopCannotBeRefusedAndReadsArePure(bool stopWhileStarting)
    {
        var logger = new CapturedLogger<BusinessBlockController>();
        var control = new BusinessBlockController(logger, 4, _ => null);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BusinessBlockRunContext? context = null;
        var calls = 0;
        var entered = BusinessBlockTestSupport.Signal();
        Assert.Equal([BusinessBlockState.Stopped, BusinessBlockState.Starting, BusinessBlockState.Running, BusinessBlockState.Stopping], Enum.GetValues<BusinessBlockState>());
        control.Start(137, async value =>
        {
            context = value;
            Interlocked.Increment(ref calls);
            using var registration = value.StopNotification.Register(() => notified.TrySetResult());
            entered.TrySetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
        Assert.Equal(BusinessBlockState.Starting, control.Read().States[137]);
        var first = control.Read();
        Assert.Equal(first.History, control.Read().History);
        Assert.Equal(1, calls);
        if (!stopWhileStarting)
        {
            context!.ReportRunning();
            Assert.Equal(BusinessBlockState.Running, control.Read().States[137]);
        }
        await control.Stop(137);
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BusinessBlockState.Stopping, control.Read().States[137]);
        context!.ReportRunning();
        Assert.Equal(BusinessBlockState.Stopping, control.Read().States[137]);
        Assert.Equal(BusinessBlockState.Stopping, control.ReadState(137));
        release.SetResult();
        await BusinessBlockTestSupport.Until(() => control.ReadState(137) == BusinessBlockState.Stopped);
        Assert.Equal(BusinessBlockState.Stopped, control.Read().States[137]);
        Assert.Equal(stopWhileStarting ? 3 : 4, control.Read().History.Count);
        Assert.Contains(logger.Messages, value => value.Contains("Stopped -> Starting"));
        Assert.Contains(logger.Messages, value => value.Contains("Stopping -> Stopped"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntryReturnIsStoppedRegardlessOfItsOutcome(bool throws)
    {
        var control = new BusinessBlockController(NullLogger<BusinessBlockController>.Instance, 128, _ => null);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.StateChanged += (id, state) => { if (state == BusinessBlockState.Stopped) returned.TrySetResult(id); };
        control.Start(137, async context =>
        {
            context.ReportRunning();
            await release.Task;
            if (throws) throw new IOException("entry failed");
        });
        await BusinessBlockTestSupport.Until(() => control.ReadState(137) == BusinessBlockState.Running);
        Assert.Equal(BusinessBlockState.Running, control.Read().States[137]);
        release.SetResult();
        Assert.Equal(137, await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BusinessBlockState.Stopped, control.Read().States[137]);
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
            BusinessBlockTestSupport.Register(139, parent), BusinessBlockTestSupport.Register(118, child, 139)]);
        var reason = BusinessBlockTestSupport.Reason();
        try
        {
            for (var round = 0; round < rounds; round++)
            {
                await scheduler.SetDemandAsync("child-demand", 118, reason);
                await scheduler.SetDemandAsync("parent-demand", 139, reason);
                await BusinessBlockTestSupport.Until(() => scheduler.ReadState(139) == BusinessBlockState.Running
                    && scheduler.ReadState(118) == BusinessBlockState.Running);
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
        await using var scheduler = BusinessBlockTestSupport.Create([BusinessBlockTestSupport.Register(154, block)]);
        try
        {
            await scheduler.StartAsync(154, BusinessBlockTestSupport.Reason());
            await block.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Starting, scheduler.ReadState(154));
            await scheduler.StopAsync(154, BusinessBlockTestSupport.Reason());
            await block.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(154));
            Assert.False(block.Returned.Task.IsCompleted);
            release.TrySetResult();
            await BusinessBlockTestSupport.State(scheduler, 154, BusinessBlockState.Stopped);
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
        await using var scheduler = BusinessBlockTestSupport.Create([BusinessBlockTestSupport.Register(154, block)]);
        var reason = BusinessBlockTestSupport.Reason(kind, "external-owner", "needs-output", 42);
        Assert.Empty(scheduler.Read().Demands);
        Assert.All(scheduler.Read().Blocks, item => Assert.Equal(BusinessBlockState.Stopped, item.State));
        try
        {
            await scheduler.SetDemandAsync("first", 154, reason);
            await block.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.SetDemandAsync("second", 154, reason);
            await scheduler.SetDemandAsync("first", null, reason);
            Assert.Equal(1, block.Runs);
            Assert.Equal(0, block.Returns);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(154));
            var fact = scheduler.Read().Items.Single(item => item.ItemUid == BackendItems.BlockDemand && item.BlockUid == 154);
            Assert.True(Assert.IsType<bool>(fact.Value));
            Assert.Equal(reason, Assert.Single(scheduler.Read().Demands, item => item.BlockId == 154).Reason);
            Assert.Contains(scheduler.Read().Decisions, item => item.ParentId == BackendUids.Program && item.Reason == reason && item.ShouldRun.SequenceEqual([154]));
            await scheduler.SetDemandAsync("second", null, reason);
            await BusinessBlockTestSupport.State(scheduler, 154, BusinessBlockState.Stopped);
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
            BusinessBlockTestSupport.Register(139, parent), BusinessBlockTestSupport.Register(118, child, 139)]);
        try
        {
            await scheduler.StartAsync(118, BusinessBlockTestSupport.Reason());
            Assert.Equal(0, parent.Runs);
            Assert.Equal(0, child.Runs);
            await scheduler.StartAsync(139, BusinessBlockTestSupport.Reason());
            await parent.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Starting, scheduler.ReadState(139));
            // The child's demand was evaluated, but its parent is still Starting.
            await scheduler.StartAsync(118, BusinessBlockTestSupport.Reason());
            Assert.Equal(0, child.Runs);
            parent.Context!.ReportRunning();
            await child.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(118));
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
            BusinessBlockTestSupport.Register(139, parent), BusinessBlockTestSupport.Register(118, child, 139),
            BusinessBlockTestSupport.Register(130, leaf, 118)]);
        try
        {
            await scheduler.StartAsync(130, BusinessBlockTestSupport.Reason());
            await scheduler.StartAsync(118, BusinessBlockTestSupport.Reason());
            await scheduler.StartAsync(139, BusinessBlockTestSupport.Reason());
            await leaf.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(["parent:start", "child:start", "leaf:start"], events);
            await scheduler.StopAsync(139, BusinessBlockTestSupport.Reason());
            await leaf.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(130));
            Assert.False(child.Notified.Task.IsCompleted);
            Assert.False(parent.Notified.Task.IsCompleted);
            release.TrySetResult();
            await BusinessBlockTestSupport.State(scheduler, 139, BusinessBlockState.Stopped);
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
            BusinessBlockTestSupport.Register(150, preparingBlock), BusinessBlockTestSupport.Register(123, fast)],
            [new(BackendUids.Program, [TestStateUids.Group], input => input.Read<bool>(TestStateUids.Group).GetValueOrDefault(false) ? [150, 123] : [])]);
        var groupWriter = scheduler.Authority.ClaimWrite<bool>(TestStateUids.Group);
        try
        {
            await groupWriter.PublishAsync(true, BusinessBlockTestSupport.Reason());
            await preparing.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await fast.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Starting, scheduler.ReadState(150));
            prepare.Set();
            await slow.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await groupWriter.PublishAsync(false, BusinessBlockTestSupport.Reason());
            await slow.Notified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await fast.Returned.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(150));
        }
        finally { prepare.Set(); cleanup.TrySetResult(); await BusinessBlockTestSupport.StopAll(scheduler); }
    }

    [Fact]
    public void ControlTreeRejectsDuplicateUnknownParentMultipleParentAndCycle()
    {
        TestBlockRegistration Node(int id, int parent = BackendUids.Program, params int[] inputs)
            => new(id, inputs, _ => throw new InvalidOperationException("Validation must not construct a block.")) { ParentId = parent };
        Assert.Throws<ArgumentException>(() => TestBlockScheduler.CreateGraph([Node(145), Node(145)]));
        Assert.Throws<KeyNotFoundException>(() => TestBlockScheduler.CreateGraph([Node(118, 133)]));
        Assert.Throws<ArgumentException>(() => TestBlockScheduler.CreateGraph([Node(111), Node(113), Node(148, 111), Node(148, 113)]));
        Assert.Throws<InvalidOperationException>(() => TestBlockScheduler.CreateGraph([Node(111, 113), Node(113, 111)]));
        Assert.Throws<KeyNotFoundException>(() => TestBlockScheduler.CreateGraph([Node(120, BackendUids.Program, 158)]));
    }

    [Fact]
    public void GraphCopiesDeclarationsAndAllowsCrossBranchDataCycles()
    {
        var inputs = new[] { 141 };
        var graph = new BusinessBlockGraph(BackendUids.Program,
        [
            new(141, BackendUids.Program, [120]), new(120, BackendUids.Program, inputs)
        ]);
        inputs[0] = 999;
        Assert.Equal([141], graph.Nodes.Single(item => item.Id == 120).Inputs);
        Assert.Equal([141, 120], graph.Children(BackendUids.Program));
    }

    [Theory]
    [InlineData("full", false, 47)]
    [InlineData("full", true, 48)]
    [InlineData("normal-read-only", false, 31)]
    public async Task ProductionGraphIsClosedForEachStartupProfile(string profile, bool windowsService, int expectedCount)
    {
        var services = new ServiceCollection();
        ResourceManager.App.Infrastructure.Security.LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(services);
        ResourceManager.App.Hosting.ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(services, ["--startup-profile=" + profile],
            profile == "full" ? ResourceManager.App.Hosting.StartupCapabilities.StartupCapabilitySet.Full
                : ResourceManager.App.Hosting.StartupCapabilities.StartupCapabilitySet.NormalReadOnly,
            ResourceManager.App.Hosting.BackendHostEnvironment.Compile(windowsService, windowsService));
        var registrations = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();
        var nativeProfile = profile switch
        {
            "full" => windowsService ? 2u : 1u,
            "normal-read-only" => 3u,
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown backend profile.")
        };
        await using var native = NativeBackendBlockTestSupport.CreateControl(registrations, nativeProfile);
        var graph = native.Graph;
        var selectedIds = BackendNativeControlFactory.WithWrappers(registrations, graph.BlockIds)
            .Select(declaration => declaration.Id).Order().ToArray();
        Assert.Equal(BackendUids.Program, graph.Program);
        Assert.Equal(expectedCount, graph.Nodes.Count);
        Assert.Equal(selectedIds, graph.BlockIds.Order().ToArray());
        Assert.DoesNotContain(graph.BlockIds, id => id == graph.Program);
        var expectedParents = BackendExpectedProductionTree.Parents(nativeProfile);
        Assert.Equal(expectedParents.Keys.Order(), graph.BlockIds.Order());
        Assert.Equal([BackendUids.Backend], graph.Children(BackendUids.Program));
        Assert.All(graph.Nodes, node => Assert.Equal(expectedParents[node.Id], node.ParentId));
        Assert.All(graph.Nodes, node => Assert.All(node.Inputs, value => Assert.True(value > 0)));
        var uiIds = typeof(UiUids).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Select(field => (int)field.GetRawConstantValue()!);
        Assert.DoesNotContain(graph.BlockIds, uiIds.Contains);
        if (profile == "normal-read-only")
            Assert.DoesNotContain(registrations, declaration => declaration.Id == BackendUids.PortableSoftwareDiscoveryService);
    }

    [Fact]
    public void OnlyDecisionNodesCallControlAndNoGenericLayerOwnsAlternateCancellation()
    {
        var core = Path.Combine(SourceRoot(), "src", "Core");
        var shared = Path.Combine(SourceRoot(), "src", "Shared", "BusinessBlocks");
        var files = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(shared, "*.cs")).ToArray();
        foreach (var operation in new[] { "control.Start(", "control.Stop(" })
        {
            var callers = files.Where(file => File.ReadAllText(file).Contains(operation, StringComparison.Ordinal)).ToArray();
            Assert.Equal("BusinessBlockDecisionNode.cs", Path.GetFileName(Assert.Single(callers)));
        }
        var owners = files.Where(file => File.ReadAllText(file).Contains("BusinessBlockController", StringComparison.Ordinal)).Select(Path.GetFileName);
        Assert.All(owners, name => Assert.Contains(name, new[] { "BusinessBlockController.cs", "BusinessBlockDecisionNode.cs",
            "BusinessBlockScheduler.cs", "BusinessBlockServiceRegistration.cs", "ManagedBusinessBlockRuntime.cs" }));
        var lifecycleFiles = Directory.EnumerateFiles(Path.Combine(core, "Infrastructure", "RuntimeSpecialization", "BusinessBlocks"), "*.cs")
            .Concat(Directory.EnumerateFiles(shared, "*.cs")).ToArray();
        var cancellationOwners = lifecycleFiles.Where(file =>
        {
            var code = File.ReadAllText(file);
            return code.Contains("CancellationTokenSource", StringComparison.Ordinal)
                || System.Text.RegularExpressions.Regex.IsMatch(code, @"\.\s*Cancel(?:Async|After)?\s*\(");
        }).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Assert.True(new[] { "BusinessBlockController.cs", "NativeControl.Callbacks.cs" }
            .ToHashSet(StringComparer.Ordinal).SetEquals(cancellationOwners));
        // Managed block control and native host callbacks own their run cancellation tokens.
        // Every other generic lifecycle layer must remain free of cancellation policy.
        var genericLayers = Directory.EnumerateFiles(Path.Combine(core, "Infrastructure", "RuntimeSpecialization", "BusinessBlocks"), "*.cs")
            .Concat(Directory.EnumerateFiles(shared, "*.cs"))
            .Where(file => Path.GetFileName(file) is not "BusinessBlockController.cs" and not "NativeControl.Callbacks.cs");
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
