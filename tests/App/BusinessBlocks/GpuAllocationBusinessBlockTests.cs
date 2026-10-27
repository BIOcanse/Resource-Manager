using System.Diagnostics.Tracing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationBusinessBlockTests
{
    [Fact]
    public async Task ProductionConstructorRequiresSchedulingAndSeparateBlockDiagnostics()
    {
        await using var harness = new GpuAllocationTestSessionFactory().Harness();
        var collector = new GpuAllocationCollector(NullLogger<GpuAllocationCollector>.Instance,
            new ConfigurationBuilder().Build(), harness.Controller, harness.Diagnostics, harness.Availability);
        var fields = typeof(GpuAllocationCollector).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.Contains(fields, field => ReferenceEquals(field.GetValue(collector), harness.Controller));
        Assert.Contains(fields, field => ReferenceEquals(field.GetValue(collector), harness.Diagnostics));
    }

    [Fact]
    public async Task ReadsDoNotStartTheSessionAndMultipleDependenciesStopOnlyAfterTheLastRelease()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(0, factory.Attempts);

        var slow = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await EtwTestRunRecorder.ReadyAsync(slow);
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(0, factory.Current.Captures);
        Assert.Contains(BoundaryProvider(), factory.Current.Enabled);
        Assert.DoesNotContain(GpuAllocationCollector.Provider, factory.Current.Enabled);
        var fast = collector.AcquireSubscription(TimeSpan.FromSeconds(1), "resource-breakdown:gpu.0.vram");
        await EtwTestRunRecorder.ReadyAsync(fast);
        var demands = harness.Controller.Read().Demands
            .Where(demand => demand.BlockId == BackendUids.GpuAllocationCollector).ToArray();
        Assert.Equal(2, demands.Length);
        Assert.Contains(demands, demand => demand.Reason.Owner == "resource-sampling"
            && demand.Reason.ConfigurationOrDependency == "gpu-allocation-subscription");
        Assert.Contains(demands, demand => demand.Reason.Owner == "resource-breakdown:gpu.0.vram");

        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Current.Captures);
        var published = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        Assert.NotNull(published);
        await EtwTestRunRecorder.ReleaseAsync(slow);
        Assert.Equal(0, factory.Current.Stops);
        Assert.Same(published, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await EtwTestRunRecorder.ReleaseAsync(fast);
        Assert.Equal(1, factory.Current.Stops);
        Assert.Equal(Timeout.InfiniteTimeSpan, factory.Current.StopTimeout);
        Assert.Empty(factory.Current.Enabled);
        Assert.Same(published, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(BusinessBlockOutcome.Stopped, (await harness.NextResultAsync()).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessingReturnReleasesTheOldSessionAndTheParentRestartsCurrentDemand(bool fail)
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        var subscription = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await EtwTestRunRecorder.ReadyAsync(subscription);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var published = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        var old = factory.Current;
        old.End(fail ? new IOException("injected GPU consumer failure") : null);
        var result = await harness.NextResultAsync();
        Assert.Equal(fail ? BusinessBlockOutcome.Failed : BusinessBlockOutcome.Done, result.Outcome);
        Assert.Equal(1, old.Stops);
        Assert.Same(published, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await EtwTestRunRecorder.UntilAsync(() => factory.Attempts == 2);
        await EtwTestRunRecorder.ReadyAsync(subscription);
        Assert.False(subscription.Released.IsCompleted);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.NotNull(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(1, factory.Current.Captures);
        await EtwTestRunRecorder.ReleaseAsync(subscription);
    }

    [Fact]
    public async Task ReleasedFastSubscriptionDoesNotSetFrequencyForANewSlowSubscription()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var fast = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(1), "fast-consumer");
        await EtwTestRunRecorder.ReadyAsync(fast);
        await EtwTestRunRecorder.ReleaseAsync(fast);
        var slow = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(30), "slow-consumer");
        await EtwTestRunRecorder.ReadyAsync(slow);
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Current.Captures);
        Assert.Empty(factory.Current.Enabled);
        await EtwTestRunRecorder.ReleaseAsync(slow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCaptureOrDisableFailureRecordsTheBlockFailureAndPublishesEmpty(bool disableFailure)
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        var subscription = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await EtwTestRunRecorder.ReadyAsync(subscription);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.NotNull(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        var old = factory.Current;
        old.FailDisable = disableFailure;
        old.FailCapture = !disableFailure;
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var result = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Run, result.FailureStage);
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(1, old.Stops);
        await EtwTestRunRecorder.UntilAsync(() => factory.Attempts == 2);
        await EtwTestRunRecorder.ReadyAsync(subscription);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.NotNull(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await EtwTestRunRecorder.ReleaseAsync(subscription);
    }

    private static Guid BoundaryProvider() => EventSource.GetGuid(typeof(GpuAllocationBoundary));
}
