using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockDependencyTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task MultipleDependenciesKeepProducerUntilTheLastLeaseIsReleased()
    {
        var upstream = new CooperativeBlock();
        var producer = new CooperativeBlock();
        await using var controller = Create(
        [
            Registration("upstream", upstream),
            new BusinessBlockRegistration("gpu", ["upstream"], _ => producer)
        ]);

        var first = controller.AcquireDependency("gpu", DependencyReason("present"), TimeSpan.FromMilliseconds(100), ["allocation"]);
        var second = controller.AcquireDependency("gpu", DependencyReason("vidmm"), TimeSpan.FromSeconds(1), ["residual"]);
        await Task.WhenAll(first.Ready, second.Ready).WaitAsync(TestTimeout);

        var active = Facts(controller, "gpu").ActiveDependencies;
        Assert.Equal(2, active.Count);
        Assert.Contains(active, fact => fact.Owner == "present" && fact.Interval == TimeSpan.FromMilliseconds(100)
            && fact.Outputs.SequenceEqual(["allocation"]));
        Assert.Contains(active, fact => fact.Owner == "vidmm" && fact.Interval == TimeSpan.FromSeconds(1)
            && fact.Outputs.SequenceEqual(["residual"]));
        Assert.Equal(new[] { "upstream", "gpu" }, controller.Read().History
            .Where(transition => transition.To == BusinessBlockState.Running).Select(transition => transition.BlockId));
        Assert.Equal(1, producer.RunCount);
        Assert.Equal(1, upstream.RunCount);

        await first.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        Assert.Single(Facts(controller, "gpu").ActiveDependencies);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "gpu").State);
        Assert.Equal(0, producer.StopReturns);

        await second.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        Assert.Empty(Facts(controller, "gpu").ActiveDependencies);
        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "gpu").State);
        Assert.Equal(1, producer.StopReturns);
        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "upstream").State);
        Assert.Equal(1, upstream.StopReturns);
        Assert.Equal(new[] { "gpu", "upstream" }, controller.Read().History
            .Where(transition => transition.To == BusinessBlockState.Stopping).Select(transition => transition.BlockId));
    }

    [Fact]
    public async Task QueryDoesNotConstructOrStartAnyBusinessBlock()
    {
        var creations = 0;
        await using var controller = Create(new BusinessBlockRegistration("gpu", [], _ =>
        {
            Interlocked.Increment(ref creations);
            return new CooperativeBlock();
        }));

        var first = controller.Read();
        var second = controller.Read();

        Assert.Equal(BusinessBlockState.Stopped, Assert.Single(first.Blocks).State);
        Assert.Equal(BusinessBlockState.Stopped, Assert.Single(second.Blocks).State);
        Assert.Equal(0, Volatile.Read(ref creations));
    }

    [Fact]
    public async Task FailedProducerIsNotRestartedUntilAllDependencyLeasesAreWithdrawn()
    {
        var creations = 0;
        await using var controller = Create(new BusinessBlockRegistration("gpu", [], _ =>
        {
            Interlocked.Increment(ref creations);
            return new ImmediateFailureBlock();
        }));

        var first = controller.AcquireDependency("gpu", DependencyReason("first-consumer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Ready.WaitAsync(TestTimeout));
        Assert.Equal(BusinessBlockState.Failed, Facts(controller, "gpu").State);

        var second = controller.AcquireDependency("gpu", DependencyReason("second-consumer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.Ready.WaitAsync(TestTimeout));
        Assert.Equal(1, Volatile.Read(ref creations));

        await first.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        await second.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        var next = controller.AcquireDependency("gpu", DependencyReason("new-first-consumer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.Ready.WaitAsync(TestTimeout));
        Assert.Equal(2, Volatile.Read(ref creations));
        await next.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ReacquireQueuedAfterLastWithdrawalStartsAgainAfterTheStopCompletes()
    {
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new GatedStopBlock(releaseStop.Task);
        await using var controller = Create(Registration("gpu", producer));
        var first = controller.AcquireDependency("gpu", DependencyReason("first-consumer"));
        await first.Ready.WaitAsync(TestTimeout);

        first.Dispose();
        var second = controller.AcquireDependency("gpu", DependencyReason("second-consumer"));
        await producer.StopStarted.Task.WaitAsync(TestTimeout);
        Assert.False(first.Released.IsCompleted);

        releaseStop.TrySetResult();
        await first.Released.WaitAsync(TestTimeout);
        await second.Ready.WaitAsync(TestTimeout);
        Assert.Equal(2, producer.RunCount);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "gpu").State);

        await second.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ConfiguredStopBlockFailurePolicyCanStopProducerWhileLeaseRemainsActive()
    {
        var producer = new CooperativeBlock();
        await using var controller = Create(
            new BusinessBlockRegistration("gpu", [], _ => producer),
            new BusinessBlockPolicy { OnFailure = BusinessBlockFailurePolicy.StopBlock });
        var lease = controller.AcquireDependency("gpu", DependencyReason("present"));
        await lease.Ready.WaitAsync(TestTimeout);

        controller.ReportFailure("gpu", new InvalidOperationException("reported"), "gpu-consumer");
        await WaitUntilAsync(() => !Facts(controller, "gpu").ExecutionOutstanding);

        Assert.Equal(BusinessBlockState.Failed, Facts(controller, "gpu").State);
        Assert.Equal(1, producer.StopReturns);
        var later = controller.AcquireDependency("gpu", DependencyReason("vidmm"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => later.Ready.WaitAsync(TestTimeout));
        Assert.Equal(1, producer.RunCount);

        await lease.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        await later.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task StopDependentsWithdrawsOnlyDynamicRootsThatDependOnTheFailedBlock()
    {
        var producer = new CooperativeBlock();
        var consumer = new CooperativeBlock();
        var peer = new CooperativeBlock();
        await using var controller = Create(
            [
                Registration("producer", producer),
                new BusinessBlockRegistration("consumer", ["producer"], _ => consumer),
                Registration("peer", peer)
            ],
            new BusinessBlockPolicy { OnFailure = BusinessBlockFailurePolicy.StopDependents });
        var dependentLease = controller.AcquireDependency("consumer", DependencyReason("consumer-owner"));
        var peerLease = controller.AcquireDependency("peer", DependencyReason("peer-owner"));
        await Task.WhenAll(dependentLease.Ready, peerLease.Ready).WaitAsync(TestTimeout);

        controller.ReportFailure("producer", new InvalidOperationException("reported"), "fake-operation");
        await WaitUntilAsync(() => !Facts(controller, "consumer").ExecutionOutstanding
            && !Facts(controller, "producer").ExecutionOutstanding);

        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "consumer").State);
        Assert.Equal(BusinessBlockState.Failed, Facts(controller, "producer").State);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "peer").State);
        Assert.Empty(Facts(controller, "consumer").ActiveDependencies);
        Assert.Single(Facts(controller, "peer").ActiveDependencies);
        var released = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dependentLease.Released.WaitAsync(TestTimeout));
        Assert.Contains("StopDependents", released.Message);
        Assert.Contains("producer", released.Message);

        await peerLease.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ConfigurationDisablePreservesLeasesAndReenableStartsTheirDemand()
    {
        var producer = new CooperativeBlock();
        await using var controller = Create(Registration("present", producer));
        var lease = controller.AcquireDependency("present", DependencyReason("consumer"));
        await lease.Ready.WaitAsync(TestTimeout);

        await controller.SetEnabledAsync("present", false, ConfigurationReason()).WaitAsync(TestTimeout);
        Assert.False(Facts(controller, "present").Enabled);
        Assert.Single(Facts(controller, "present").ActiveDependencies);
        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "present").State);

        var disabledLease = controller.AcquireDependency("present", DependencyReason("new-consumer"));
        await disabledLease.Ready.WaitAsync(TestTimeout);
        Assert.Equal(1, producer.RunCount);
        Assert.Equal(2, Facts(controller, "present").ActiveDependencies.Count);

        await disabledLease.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        await controller.SetEnabledAsync("present", true, ConfigurationReason()).WaitAsync(TestTimeout);
        Assert.True(Facts(controller, "present").Enabled);
        Assert.Equal(BusinessBlockState.Running, Facts(controller, "present").State);
        Assert.Equal(2, producer.RunCount);

        await lease.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "present").State);
    }

    [Fact]
    public async Task AcquireAndDisposeInsideControllerStartAndStopCallbacksDoNotBlock()
    {
        BusinessBlockController? controller = null;
        IBusinessBlockDependency? dependency = null;
        var producer = new CooperativeBlock();
        var consumer = new CallbackHostedService(
            onStart: () =>
            {
                dependency = controller!.AcquireDependency("gpu", DependencyReason("callback-consumer"));
            },
            onStop: () => dependency!.Dispose());
        await using var ownedController = Create(
        [
            Registration("gpu", producer),
            new BusinessBlockRegistration("callback", [], _ => new HostedBusinessBlock(() => consumer))
            {
                HostedService = true
            }
        ]);
        controller = ownedController;

        await controller.StartAsync("callback", UserReason("callback")).WaitAsync(TestTimeout);
        await dependency!.Ready.WaitAsync(TestTimeout);
        await controller.StopAsync("callback", UserReason("callback")).WaitAsync(TestTimeout);
        await dependency.Released.WaitAsync(TestTimeout);

        Assert.Equal(BusinessBlockState.Stopped, Facts(controller, "gpu").State);
        Assert.Equal(1, producer.RunCount);
        Assert.Equal(1, producer.StopReturns);
    }

    private static BusinessBlockController Create(
        BusinessBlockRegistration registration, BusinessBlockPolicy? policy = null)
        => Create([registration], policy);

    private static BusinessBlockController Create(
        IEnumerable<BusinessBlockRegistration> registrations, BusinessBlockPolicy? policy = null)
        => new(new EmptyServiceProvider(), registrations,
            Options.Create(new BusinessBlockOptions
            {
                MetricsInterval = null,
                Policy = policy ?? new BusinessBlockPolicy(),
                HistoryLimit = 128
            }), NullLogger<BusinessBlockController>.Instance);

    private static BusinessBlockRegistration Registration(string id, IBusinessBlock block)
        => new(id, [], _ => block);

    private static BusinessBlockReason DependencyReason(string owner)
        => new(BusinessBlockRequestKind.Dependency, owner, "test-dependency", 3);

    private static BusinessBlockReason ConfigurationReason()
        => new(BusinessBlockRequestKind.Configuration, "test", "compiled-plan", 4);

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

    private sealed class CooperativeBlock : IBusinessBlock
    {
        private int runCount;
        private int stopReturns;
        public int RunCount => Volatile.Read(ref runCount);
        public int StopReturns => Volatile.Read(ref stopReturns);

        public async Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
        {
            Interlocked.Increment(ref runCount);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopNotification).ConfigureAwait(false);
                return BusinessBlockResult.Done;
            }
            catch (OperationCanceledException) when (stopNotification.IsCancellationRequested)
            {
                Interlocked.Increment(ref stopReturns);
                return BusinessBlockResult.Stopped;
            }
        }
    }

    private sealed class ImmediateFailureBlock : IBusinessBlock
    {
        public Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
            => Task.FromResult(BusinessBlockResult.Failed(new InvalidOperationException("producer failed")));
    }

    private sealed class GatedStopBlock(Task releaseStop) : IBusinessBlock
    {
        private int runCount;
        private int stopReturns;
        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RunCount => Volatile.Read(ref runCount);
        public int StopReturns => Volatile.Read(ref stopReturns);

        public async Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
        {
            Interlocked.Increment(ref runCount);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopNotification).ConfigureAwait(false);
                return BusinessBlockResult.Done;
            }
            catch (OperationCanceledException) when (stopNotification.IsCancellationRequested)
            {
                StopStarted.TrySetResult();
                await releaseStop.ConfigureAwait(false);
                Interlocked.Increment(ref stopReturns);
                return BusinessBlockResult.Stopped;
            }
        }
    }

    private sealed class CallbackHostedService(Action onStart, Action onStop) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            onStart();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            onStop();
            return Task.CompletedTask;
        }
    }
}
