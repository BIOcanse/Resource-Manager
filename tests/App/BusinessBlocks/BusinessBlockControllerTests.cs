using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockControllerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EachBlockCanRunIndependentlyAndExposeDoneStoppedAndFailedResults()
    {
        var done = new ImmediateBlock(BusinessBlockResult.Done);
        var stopped = new CooperativeBlock();
        var failed = new ImmediateBlock(BusinessBlockResult.Failed(new InvalidOperationException("block failed")));
        await using var controller = CreateController(
            Registration("done", done),
            Registration("stopped", stopped),
            Registration("failed", failed));

        await controller.StartAsync("done", UserReason("done"));
        var doneResult = await controller.GetCompletion("done").WaitAsync(TestTimeout);
        Assert.Equal(BusinessBlockOutcome.Done, doneResult.Outcome);
        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "done").State);
        Assert.Equal(BusinessBlockTransitionOutcome.Done, Facts(controller, "done").Outcome);
        Assert.False(Facts(controller, "done").ExecutionOutstanding);

        await controller.StartAsync("stopped", UserReason("stopped"));
        await stopped.Started.Task.WaitAsync(TestTimeout);
        await controller.StopAsync("stopped", UserReason("stopped"));
        var stoppedResult = await controller.GetCompletion("stopped").WaitAsync(TestTimeout);
        Assert.Equal(BusinessBlockOutcome.Stopped, stoppedResult.Outcome);
        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "stopped").State);
        Assert.Equal(BusinessBlockTransitionOutcome.Stopped, Facts(controller, "stopped").Outcome);

        await controller.StartAsync("failed", UserReason("failed"));
        var failedResult = await controller.GetCompletion("failed").WaitAsync(TestTimeout);
        Assert.Equal(BusinessBlockOutcome.Failed, failedResult.Outcome);
        Assert.Equal(BusinessBlockState.Failed, Facts(controller, "failed").State);
        Assert.Contains(controller.Read().History, transition =>
            transition.BlockId == "failed" && transition.FailureType == typeof(InvalidOperationException).FullName && transition.FailureStage == BusinessBlockFailureStage.Run);

        Assert.Equal(1, done.RunCount);
        Assert.Equal(1, stopped.RunCount);
        Assert.Equal(1, failed.RunCount);
    }

    [Fact]
    public async Task StopReturnsOnlyAfterTheBlockHasReleasedItsResource()
    {
        var resourceReleased = false;
        var block = new CooperativeBlock(onStop: () => resourceReleased = true);
        await using var controller = CreateController(Registration("worker", block));

        await controller.StartAsync("worker", UserReason("worker"));
        await block.Started.Task.WaitAsync(TestTimeout);
        await controller.StopAsync("worker", UserReason("worker"));

        Assert.True(resourceReleased);
        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "worker").State);
        Assert.False(Facts(controller, "worker").ExecutionOutstanding);
        Assert.Equal("Stopped", controller.Read().History[^1].To.ToString());
    }

    [Fact]
    public async Task DependenciesStartInOrderAndStopInReverseOrderAcrossADeepChain()
    {
        const int depth = 1024;
        var starts = new ConcurrentQueue<int>();
        var stops = new ConcurrentQueue<int>();
        var registrations = Enumerable.Range(0, depth).Reverse().Select(index =>
            new BusinessBlockRegistration(
                $"node-{index}",
                index == 0 ? Array.Empty<string>() : [$"node-{index - 1}"],
                _ => new OrderBlock(index, starts, stops)));
        await using var controller = CreateController(registrations, Options(historyLimit: depth * 4));

        var rootId = $"node-{depth - 1}";
        await controller.StartAsync(rootId, UserReason("deep-root"));
        Assert.Equal(Enumerable.Range(0, depth), starts);

        await controller.StopAsync(rootId, UserReason("deep-root"));
        Assert.Equal(Enumerable.Range(0, depth).Reverse(), stops);
        var snapshot = controller.Read();
        Assert.All(snapshot.Blocks, block => Assert.Equal(BusinessBlockState.Stopped, block.State));
        var dependencyStart = Assert.Single(snapshot.History, transition =>
            transition.BlockId == "node-0" && transition.To == BusinessBlockState.Running);
        Assert.Equal(BusinessBlockRequestKind.Dependency, dependencyStart.Reason.Kind);
        Assert.Equal("deep-root", dependencyStart.Reason.Owner);
        Assert.Equal(rootId, dependencyStart.Reason.ConfigurationOrDependency);
        Assert.Equal(7, dependencyStart.Reason.PlanVersion);
        var rootStart = Assert.Single(snapshot.History, transition =>
            transition.BlockId == rootId && transition.To == BusinessBlockState.Running);
        Assert.Equal(BusinessBlockRequestKind.User, rootStart.Reason.Kind);
        var dependencyStop = Assert.Single(snapshot.History, transition =>
            transition.BlockId == "node-0" && transition.To == BusinessBlockState.Stopping);
        Assert.Equal(BusinessBlockRequestKind.Dependency, dependencyStop.Reason.Kind);
    }

    [Fact]
    public async Task StoppingOneRootKeepsAnotherRootAndItsSharedDependencyRunning()
    {
        var shared = new CooperativeBlock();
        var first = new CooperativeBlock();
        var second = new CooperativeBlock();
        await using var controller = CreateController(
            Registration("shared", shared),
            Registration("first", first, "shared"),
            Registration("second", second, "shared"));

        await controller.StartAsync("first", UserReason("first"));
        await controller.StartAsync("second", UserReason("second"));
        await controller.StopAsync("first", UserReason("first"));

        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "first").State);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "second").State);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "shared").State);
        Assert.Equal(0, shared.StopReturns);

        await controller.StopAsync("second", UserReason("second"));
        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "shared").State);
        Assert.Equal(1, shared.StopReturns);
    }

    [Fact]
    public async Task IndependentBlocksCanBeStoppedSeparately()
    {
        var first = new CooperativeBlock();
        var second = new CooperativeBlock();
        await using var controller = CreateController(
            Registration("first", first), Registration("second", second));

        await controller.StartAsync("first", UserReason("first"));
        await controller.StartAsync("second", UserReason("second"));
        await controller.StopAsync("first", UserReason("first"));

        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "first").State);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "second").State);
        Assert.Equal(0, second.StopReturns);

        await controller.StopAsync("second", UserReason("second"));
        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "second").State);
    }

    [Fact]
    public async Task TimedOutStopCanBeMarkedFailedWhileExecutionRemainsOutstanding()
    {
        var block = new GatedBlock();
        await using var controller = CreateController(
            [Registration("worker", block)],
            Options(policy: new BusinessBlockPolicy
            {
                StopDeadline = TimeSpan.FromMilliseconds(40),
                OnTimeout = BusinessBlockTimeoutPolicy.MarkFailed
            }));

        await controller.StartAsync("worker", UserReason("worker"));
        await block.Started.Task.WaitAsync(TestTimeout);
        await controller.StopAsync("worker", UserReason("worker")).WaitAsync(TestTimeout);

        var timedOut = Facts(controller, "worker");
        Assert.Equal(BusinessBlockState.Failed, timedOut.State);
        Assert.Equal(BusinessBlockTransitionOutcome.StopTimeout, timedOut.Outcome);
        Assert.True(timedOut.ExecutionOutstanding);

        block.Complete(BusinessBlockResult.Stopped);
        Assert.Equal(BusinessBlockOutcome.Stopped,
            (await controller.GetCompletion("worker").WaitAsync(TestTimeout)).Outcome);
        Assert.False(Facts(controller, "worker").ExecutionOutstanding);
    }

    [Fact]
    public async Task ContinueWaitingRecordsDeadlineButDoesNotReturnUntilExecutionEnds()
    {
        var block = new GatedBlock();
        await using var controller = CreateController(
            [Registration("worker", block)],
            Options(policy: new BusinessBlockPolicy
            {
                StopDeadline = TimeSpan.FromMilliseconds(40),
                OnTimeout = BusinessBlockTimeoutPolicy.ContinueWaiting
            }));

        await controller.StartAsync("worker", UserReason("worker"));
        await block.Started.Task.WaitAsync(TestTimeout);
        var stopping = controller.StopAsync("worker", UserReason("worker"));
        await Task.Delay(120);

        Assert.False(stopping.IsCompleted);
        Assert.True(Facts(controller, "worker").ExecutionOutstanding);
        Assert.Contains(controller.Read().History, transition =>
            transition.BlockId == "worker" && transition.Outcome == BusinessBlockTransitionOutcome.StopTimeout);

        block.Complete(BusinessBlockResult.Stopped);
        await stopping.WaitAsync(TestTimeout);
        Assert.False(Facts(controller, "worker").ExecutionOutstanding);
    }

    [Fact]
    public async Task NullStopDeadlineDisablesTheDeadlineAndContinuesWaiting()
    {
        var block = new GatedBlock();
        await using var controller = CreateController(
            [Registration("worker", block)],
            Options(policy: new BusinessBlockPolicy { StopDeadline = null }));

        await controller.StartAsync("worker", UserReason("worker"));
        await block.Started.Task.WaitAsync(TestTimeout);
        var stopping = controller.StopAsync("worker", UserReason("worker"));
        await Task.Delay(120);

        Assert.False(stopping.IsCompleted);
        Assert.Equal(BusinessBlockState.Stopping, Facts(controller, "worker").State);
        Assert.True(Facts(controller, "worker").ExecutionOutstanding);
        Assert.DoesNotContain(controller.Read().History, transition =>
            transition.BlockId == "worker" && transition.Outcome == BusinessBlockTransitionOutcome.StopTimeout);

        block.Complete(BusinessBlockResult.Stopped);
        await stopping.WaitAsync(TestTimeout);
        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "worker").State);
    }

    [Fact]
    public async Task FailedDependentStartUnwindsNewDependenciesAndKeepsAnExplicitSharedRoot()
    {
        var shared = new CooperativeBlock();
        var temporary = new CooperativeBlock();
        var registration = new BusinessBlockRegistration("dependent", ["shared", "temporary"], _ =>
            throw new InvalidOperationException("dependent factory failed"));
        await using var controller = CreateController(
            Registration("shared", shared), Registration("temporary", temporary), registration);

        await controller.StartAsync("shared", UserReason("shared-root"));
        await shared.Started.Task.WaitAsync(TestTimeout);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.StartAsync("dependent", UserReason("dependent-root")));

        Assert.Equal("dependent factory failed", error.Message);
        Assert.Equal(BusinessBlockState.Failed, Facts(controller, "dependent").State);
        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "temporary").State);
        Assert.Equal(1, temporary.StopReturns);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "shared").State);
        Assert.Equal(0, shared.StopReturns);

        await controller.StopAsync("shared", UserReason("shared-root"));
    }

    [Fact]
    public async Task StartingAfterMarkFailedTimeoutIsRejectedUntilThePreviousRunEnds()
    {
        var block = new GatedBlock();
        await using var controller = CreateController(
            [Registration("worker", block)],
            Options(policy: new BusinessBlockPolicy
            {
                StopDeadline = TimeSpan.FromMilliseconds(40),
                OnTimeout = BusinessBlockTimeoutPolicy.MarkFailed
            }));

        await controller.StartAsync("worker", UserReason("first-start"));
        await block.Started.Task.WaitAsync(TestTimeout);
        await controller.StopAsync("worker", UserReason("first-stop")).WaitAsync(TestTimeout);
        Assert.True(Facts(controller, "worker").ExecutionOutstanding);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.StartAsync("worker", UserReason("premature-restart")));
        Assert.Equal(1, block.RunCount);
        Assert.True(Facts(controller, "worker").ExecutionOutstanding);

        block.Complete(BusinessBlockResult.Stopped);
        await controller.GetCompletion("worker").WaitAsync(TestTimeout);
        Assert.False(Facts(controller, "worker").ExecutionOutstanding);

        await controller.StartAsync("worker", UserReason("restart-after-drain"));
        Assert.Equal(2, block.RunCount);
        await controller.GetCompletion("worker").WaitAsync(TestTimeout);
        await controller.StopAsync("worker", UserReason("final-stop"));
    }

    [Fact]
    public async Task CallerCancellationOfStopPropagatesWithoutRecordingAStopTimeout()
    {
        var block = new GatedBlock();
        await using var controller = CreateController(
            [Registration("worker", block)],
            Options(policy: new BusinessBlockPolicy
            {
                StopDeadline = TimeSpan.FromMilliseconds(400),
                OnTimeout = BusinessBlockTimeoutPolicy.MarkFailed
            }));

        await controller.StartAsync("worker", UserReason("worker"));
        await block.Started.Task.WaitAsync(TestTimeout);
        using var cancellation = new CancellationTokenSource();
        var stopping = controller.StopAsync("worker", UserReason("cancelled-stop"), cancellation.Token);
        try
        {
            await block.StopRequested.Task.WaitAsync(TestTimeout);
            Assert.False(stopping.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
            Assert.Equal(BusinessBlockState.Stopping, Facts(controller, "worker").State);
            Assert.True(Facts(controller, "worker").ExecutionOutstanding);
            Assert.DoesNotContain(controller.Read().History, transition =>
                transition.BlockId == "worker" && transition.Outcome == BusinessBlockTransitionOutcome.StopTimeout);
        }
        finally
        {
            block.Complete(BusinessBlockResult.Stopped);
        }

        await controller.GetCompletion("worker").WaitAsync(TestTimeout);
        Assert.False(Facts(controller, "worker").ExecutionOutstanding);
        Assert.DoesNotContain(controller.Read().History, transition =>
            transition.BlockId == "worker" && transition.Outcome == BusinessBlockTransitionOutcome.StopTimeout);
    }

    [Fact]
    public async Task TimedOutCancellationCallbackRemainsExecutionOutstandingUntilItReturns()
    {
        using var releaseCallback = new ManualResetEventSlim();
        var block = new BlockingCancellationCallbackBlock(releaseCallback);
        await using var controller = CreateController(
            [Registration("worker", block)],
            Options(policy: new BusinessBlockPolicy
            {
                StopDeadline = TimeSpan.FromMilliseconds(40),
                OnTimeout = BusinessBlockTimeoutPolicy.MarkFailed
            }));

        await controller.StartAsync("worker", UserReason("worker"));
        var stopping = controller.StopAsync("worker", UserReason("worker-stop"));
        try
        {
            await block.CallbackEntered.Task.WaitAsync(TestTimeout);
            await controller.GetCompletion("worker").WaitAsync(TestTimeout);
            await stopping.WaitAsync(TestTimeout);

            Assert.True(block.RunCompleted);
            Assert.True(Facts(controller, "worker").ExecutionOutstanding);
            Assert.Contains(controller.Read().History, transition =>
                transition.BlockId == "worker" && transition.Outcome == BusinessBlockTransitionOutcome.StopTimeout);
        }
        finally
        {
            block.ReleaseCallback();
        }

        await WaitUntilAsync(() => !Facts(controller, "worker").ExecutionOutstanding);
    }

    [Fact]
    public async Task ReadingAndGettingCompletionDoNotConstructOrRunABlock()
    {
        var factoryCalls = 0;
        var registrations = new[]
        {
            new BusinessBlockRegistration("lazy", Array.Empty<string>(), _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new ImmediateBlock(BusinessBlockResult.Done);
            })
        };
        await using var controller = CreateController(registrations);

        var snapshot = controller.Read();
        Assert.Throws<InvalidOperationException>(() => { _ = controller.GetCompletion("lazy"); });
        var secondSnapshot = controller.Read();

        Assert.Equal(0, Volatile.Read(ref factoryCalls));
        Assert.Equal(128, snapshot.HistoryLimit);
        Assert.Equal(BusinessBlockState.Stopped, snapshot.Blocks.Single().State);
        Assert.Equal(BusinessBlockState.Stopped, secondSnapshot.Blocks.Single().State);
    }

    [Fact]
    public async Task FactoryFailureIsPropagatedAndRecordedWithoutStartingABlock()
    {
        var error = new InvalidOperationException("factory failed");
        var factoryCalls = 0;
        var registration = new BusinessBlockRegistration("worker", Array.Empty<string>(), _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            throw error;
        });
        await using var controller = CreateController([registration]);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.StartAsync("worker", UserReason("worker")));

        Assert.Same(error, thrown);
        Assert.Equal(1, Volatile.Read(ref factoryCalls));
        Assert.Equal(BusinessBlockState.Failed, Facts(controller, "worker").State);
        Assert.False(Facts(controller, "worker").ExecutionOutstanding);
        Assert.Contains(controller.Read().History, transition =>
            transition.BlockId == "worker" && transition.To == BusinessBlockState.Failed &&
            transition.Outcome == BusinessBlockTransitionOutcome.Failed && transition.FailureType == error.GetType().FullName && transition.FailureStage == BusinessBlockFailureStage.Start);
    }

    [Fact]
    public async Task TransitionHistoryIsGlobalAndBoundedAndEveryTransitionIsLogged()
    {
        var logger = new CapturingLogger<BusinessBlockController>();
        var block = new CooperativeBlock();
        await using var controller = CreateController(
            [Registration("first", block), Registration("second", new ImmediateBlock(BusinessBlockResult.Done))],
            Options(historyLimit: 3), logger);

        await controller.StartAsync("first", UserReason("first"));
        await block.Started.Task.WaitAsync(TestTimeout);
        await controller.StopAsync("first", UserReason("first"));
        await controller.StartAsync("second", UserReason("second"));
        await controller.GetCompletion("second").WaitAsync(TestTimeout);

        var snapshot = controller.Read();
        var transitionLogs = logger.Entries.Where(entry => entry.State.ContainsKey("BlockId")).ToArray();
        Assert.Equal(3, snapshot.HistoryLimit);
        Assert.Equal(3, snapshot.History.Count);
        Assert.True(transitionLogs.Length > snapshot.History.Count);
        Assert.Equal(snapshot.History.OrderBy(transition => transition.Sequence), snapshot.History);
        Assert.All(transitionLogs, entry =>
        {
            Assert.StartsWith("Business block ", entry.Message, StringComparison.Ordinal);
            Assert.Contains(" -> ", entry.Message, StringComparison.Ordinal);
            Assert.Contains("source=", entry.Message, StringComparison.Ordinal);
        });
        Assert.Contains(transitionLogs, entry => Equals(entry.State["BlockId"], "first"));
        Assert.Contains(transitionLogs, entry => Equals(entry.State["BlockId"], "second"));
    }

    [Fact]
    public async Task ZeroHistoryDisablesRetentionWithoutDisablingTransitionLogs()
    {
        var logger = new CapturingLogger<BusinessBlockController>();
        await using var controller = CreateController(
            [Registration("worker", new ImmediateBlock(BusinessBlockResult.Done))],
            Options(historyLimit: 0), logger);

        await controller.StartAsync("worker", UserReason("worker"));
        await controller.GetCompletion("worker").WaitAsync(TestTimeout);

        Assert.Empty(controller.Read().History);
        Assert.True(logger.Entries.Count(entry => entry.State.ContainsKey("BlockId")) > 0);
    }

    [Fact]
    public async Task RecordOnlyFailureLeavesTheBlockAndItsDependentsRunning()
    {
        var dependency = new CooperativeBlock();
        var dependent = new CooperativeBlock();
        var peer = new CooperativeBlock();
        await using var controller = CreateController(
            [Registration("dependency", dependency), Registration("dependent", dependent, "dependency"), Registration("peer", peer)],
            Options(blockPolicies: new Dictionary<string, BusinessBlockPolicy>
            {
                ["dependency"] = new() { OnFailure = BusinessBlockFailurePolicy.RecordOnly }
            }));

        await controller.StartAsync("dependent", UserReason("dependent"));
        await controller.StartAsync("peer", UserReason("peer"));
        controller.ReportFailure("dependency", new InvalidOperationException("reported"), "fake-operation");

        Assert.Equal(BusinessBlockState.Failed, Facts(controller, "dependency").State);
        Assert.True(Facts(controller, "dependency").ExecutionOutstanding);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "dependent").State);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "peer").State);

        await controller.StopAsync("dependent", UserReason("dependent"));
        await controller.StopAsync("peer", UserReason("peer"));
    }

    [Fact]
    public async Task StopBlockFailurePolicyStopsOnlyTheReportingBlock()
    {
        var dependency = new CooperativeBlock();
        var dependent = new CooperativeBlock();
        var peer = new CooperativeBlock();
        await using var controller = CreateController(
            [Registration("dependency", dependency), Registration("dependent", dependent, "dependency"), Registration("peer", peer)],
            Options(blockPolicies: new Dictionary<string, BusinessBlockPolicy>
            {
                ["dependency"] = new() { OnFailure = BusinessBlockFailurePolicy.StopBlock }
            }));

        await controller.StartAsync("dependent", UserReason("dependent"));
        await controller.StartAsync("peer", UserReason("peer"));
        controller.ReportFailure("dependency", new InvalidOperationException("reported"), "fake-operation");
        await WaitUntilAsync(() => !Facts(controller, "dependency").ExecutionOutstanding);

        Assert.Equal(BusinessBlockState.Failed, Facts(controller, "dependency").State);
        Assert.Equal(1, dependency.StopReturns);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "dependent").State);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "peer").State);

        await controller.StopAsync("dependent", UserReason("dependent"));
        await controller.StopAsync("peer", UserReason("peer"));
    }

    [Fact]
    public async Task StopDependentsPolicyStopsTheFailedBlockAndDependentRootsOnly()
    {
        var dependency = new CooperativeBlock();
        var dependent = new CooperativeBlock();
        var peer = new CooperativeBlock();
        await using var controller = CreateController(
            [Registration("dependency", dependency), Registration("dependent", dependent, "dependency"), Registration("peer", peer)],
            Options(blockPolicies: new Dictionary<string, BusinessBlockPolicy>
            {
                ["dependency"] = new() { OnFailure = BusinessBlockFailurePolicy.StopDependents }
            }));

        await controller.StartAsync("dependent", UserReason("dependent"));
        await controller.StartAsync("peer", UserReason("peer"));
        controller.ReportFailure("dependency", new InvalidOperationException("reported"), "fake-operation");
        await WaitUntilAsync(() =>
            !Facts(controller, "dependency").ExecutionOutstanding &&
            !Facts(controller, "dependent").ExecutionOutstanding);

        Assert.Equal(BusinessBlockState.Failed, Facts(controller, "dependency").State);
        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "dependent").State);
        Assert.Equal(1, dependency.StopReturns);
        Assert.Equal(1, dependent.StopReturns);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "peer").State);

        await controller.StopAsync("peer", UserReason("peer"));
    }

    [Fact]
    public async Task FailurePolicyPreservesASharedDependencyThatIsStillAnExplicitRoot()
    {
        var shared = new CooperativeBlock();
        var failed = new CooperativeBlock();
        var dependent = new CooperativeBlock();
        await using var controller = CreateController(
            [
                Registration("shared", shared),
                Registration("failed", failed),
                Registration("dependent", dependent, "shared", "failed")
            ],
            Options(blockPolicies: new Dictionary<string, BusinessBlockPolicy>
            {
                ["failed"] = new() { OnFailure = BusinessBlockFailurePolicy.StopDependents }
            }));

        await controller.StartAsync("shared", UserReason("shared-root"));
        await controller.StartAsync("dependent", UserReason("dependent-root"));
        controller.ReportFailure("failed", new InvalidOperationException("reported"), "fake-operation");
        // The failed block's own stop runs asynchronously; wait for both executions, as the test above does.
        await WaitUntilAsync(() =>
            !Facts(controller, "failed").ExecutionOutstanding &&
            !Facts(controller, "dependent").ExecutionOutstanding);

        Assert.Equal(BusinessBlockState.Failed, Facts(controller, "failed").State);
        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "dependent").State);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "shared").State);
        Assert.Equal(0, shared.StopReturns);

        await controller.StopAsync("shared", UserReason("shared-root"));
    }

    private static BusinessBlockController CreateController(
        IEnumerable<BusinessBlockRegistration> registrations,
        BusinessBlockOptions? options = null,
        ILogger<BusinessBlockController>? logger = null)
        => new(new EmptyServiceProvider(), registrations, Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            logger ?? new CapturingLogger<BusinessBlockController>());

    private static BusinessBlockController CreateController(
        BusinessBlockRegistration first,
        params BusinessBlockRegistration[] remaining)
        => CreateController([first, .. remaining]);

    private static BusinessBlockRegistration Registration(string id, IBusinessBlock block, params string[] dependencies)
        => new(id, dependencies, _ => block);

    private static BusinessBlockOptions Options(
        int historyLimit = 128,
        BusinessBlockPolicy? policy = null,
        Dictionary<string, BusinessBlockPolicy>? blockPolicies = null)
        => new()
        {
            HistoryLimit = historyLimit,
            MetricsInterval = null,
            Policy = policy ?? new BusinessBlockPolicy { StopDeadline = TestTimeout },
            Blocks = blockPolicies ?? new Dictionary<string, BusinessBlockPolicy>(StringComparer.Ordinal)
        };

    private static BusinessBlockReason UserReason(string owner)
        => new(BusinessBlockRequestKind.User, owner, "explicit-test-request", 7);

    private static BusinessBlockFacts Facts(BusinessBlockController controller, string id)
        => controller.Read().Blocks.Single(block => block.Id == id);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The expected business-block state was not reached.");
            await Task.Delay(5);
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class ImmediateBlock(BusinessBlockResult result) : IBusinessBlock
    {
        private int runCount;
        public int RunCount => Volatile.Read(ref runCount);

        public Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
        {
            Interlocked.Increment(ref runCount);
            return Task.FromResult(result);
        }
    }

    private sealed class CooperativeBlock(Action? onStop = null) : IBusinessBlock
    {
        private int runCount;
        private int stopReturns;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RunCount => Volatile.Read(ref runCount);
        public int StopReturns => Volatile.Read(ref stopReturns);

        public async Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
        {
            Interlocked.Increment(ref runCount);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopNotification).ConfigureAwait(false);
                return BusinessBlockResult.Done;
            }
            catch (OperationCanceledException) when (stopNotification.IsCancellationRequested)
            {
                onStop?.Invoke();
                Interlocked.Increment(ref stopReturns);
                return BusinessBlockResult.Stopped;
            }
        }
    }

    private sealed class GatedBlock : IBusinessBlock
    {
        private readonly TaskCompletionSource<BusinessBlockResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int runCount;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RunCount => Volatile.Read(ref runCount);

        public Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
        {
            Interlocked.Increment(ref runCount);
            Started.TrySetResult();
            _ = stopNotification.Register(() => StopRequested.TrySetResult());
            return completion.Task;
        }

        public void Complete(BusinessBlockResult result) => completion.TrySetResult(result);
    }

    private sealed class BlockingCancellationCallbackBlock(ManualResetEventSlim releaseCallback) : IBusinessBlock
    {
        private readonly TaskCompletionSource<BusinessBlockResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration registration;

        public TaskCompletionSource CallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool RunCompleted => completion.Task.IsCompleted;

        public Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
        {
            registration = stopNotification.Register(() =>
            {
                CallbackEntered.TrySetResult();
                completion.TrySetResult(BusinessBlockResult.Stopped);
                releaseCallback.Wait();
            });
            return completion.Task;
        }

        public void ReleaseCallback()
        {
            releaseCallback.Set();
            registration.Dispose();
        }
    }

    private sealed class OrderBlock(int id, ConcurrentQueue<int> starts, ConcurrentQueue<int> stops) : IBusinessBlock
    {
        public Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
        {
            starts.Enqueue(id);
            return WaitForStopAsync(stopNotification);
        }

        private async Task<BusinessBlockResult> WaitForStopAsync(CancellationToken stopNotification)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopNotification).ConfigureAwait(false);
                return BusinessBlockResult.Done;
            }
            catch (OperationCanceledException) when (stopNotification.IsCancellationRequested)
            {
                stops.Enqueue(id);
                return BusinessBlockResult.Stopped;
            }
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<CapturedLog> entries = new();
        public IReadOnlyCollection<CapturedLog> Entries => entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            entries.Enqueue(new CapturedLog(logLevel, formatter(state, exception), values));
        }
    }

    private sealed record CapturedLog(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> State);
}
