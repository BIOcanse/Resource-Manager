using System.Diagnostics.Tracing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationBusinessBlockTests
{
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
        await slow.Ready;
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(0, factory.Current.Captures);
        Assert.Contains(BoundaryProvider(), factory.Current.Enabled);
        Assert.DoesNotContain(GpuAllocationCollector.Provider, factory.Current.Enabled);

        var fast = collector.AcquireSubscription(TimeSpan.FromSeconds(1), "resource-breakdown:gpu.0.vram");
        await fast.Ready;
        Assert.Equal(1, factory.Attempts);
        var facts = Assert.Single(harness.Controller.Read().Blocks, block => block.Id == "gpu-allocation-etw");
        Assert.Equal(2, facts.ActiveDependencies.Count);
        Assert.Contains(facts.ActiveDependencies, dependency => dependency.Owner == "resource-sampling"
            && dependency.Reason.Kind == BusinessBlockRequestKind.Dependency
            && dependency.Reason.ConfigurationOrDependency == "gpu-allocation-subscription"
            && dependency.Reason.PlanVersion == harness.Controller.CurrentPlanVersion
            && dependency.Interval == TimeSpan.FromSeconds(30) && dependency.Outputs.Contains("gpu-allocation"));
        Assert.Contains(facts.ActiveDependencies, dependency => dependency.Owner == "resource-breakdown:gpu.0.vram"
            && dependency.Interval == TimeSpan.FromSeconds(1) && dependency.Outputs.Contains("gpu-allocation"));

        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Current.Captures);
        var published = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        Assert.NotNull(published);

        await slow.DisposeAsync();
        Assert.Equal(0, factory.Current.Stops);
        Assert.Same(published, collector.ReadCurrent(GpuAllocationTestSession.Processes));

        await fast.DisposeAsync();
        Assert.Equal(1, factory.Current.Stops);
        Assert.Equal(Timeout.InfiniteTimeSpan, factory.Current.StopTimeout);
        Assert.Empty(factory.Current.Enabled);
        Assert.Same(published, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(BusinessBlockOutcome.Stopped,
            (await harness.Controller.GetCompletion("gpu-allocation-etw")).Outcome);
    }

    [Fact]
    public async Task DefaultRecordOnlyFailureInvalidatesAtTheNextDueCaptureWithoutRetry()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        var subscription = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await subscription.Ready;
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var published = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        var block = Assert.Single(harness.Controller.Read().Blocks, item => item.Id == "gpu-allocation-etw");
        Assert.Equal(BusinessBlockFailurePolicy.RecordOnly, block.Policy.OnFailure);

        factory.Current.End(new IOException("injected GPU consumer failure"));
        var result = await harness.Controller.GetCompletion("gpu-allocation-etw");
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Same(published, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Attempts);
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
    }

    [Fact]
    public async Task NaturalProcessingCompletionReturnsDoneWithoutRestarting()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        var subscription = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await subscription.Ready;
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var published = collector.ReadCurrent(GpuAllocationTestSession.Processes);

        factory.Current.End();
        Assert.Equal(BusinessBlockOutcome.Done,
            (await harness.Controller.GetCompletion("gpu-allocation-etw")).Outcome);
        Assert.Same(published, collector.ReadCurrent(GpuAllocationTestSession.Processes));

        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Attempts);
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
    }

    [Fact]
    public async Task BlockSpecificFailurePolicyOverrideIsVisibleAndDoesNotAddACollectorRetry()
    {
        var policy = new BusinessBlockPolicy { OnFailure = BusinessBlockFailurePolicy.StopBlock };
        var options = new BusinessBlockOptions
        {
            Blocks = new(StringComparer.Ordinal) { ["gpu-allocation-etw"] = policy }
        };
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness(blockOptions: options);
        var collector = harness.Collector;
        var subscription = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await subscription.Ready;

        var facts = Assert.Single(harness.Controller.Read().Blocks, item => item.Id == "gpu-allocation-etw");
        Assert.Equal(BusinessBlockFailurePolicy.StopBlock, facts.Policy.OnFailure);
        factory.Current.End(new IOException("injected GPU consumer failure"));
        Assert.Equal(BusinessBlockOutcome.Failed,
            (await harness.Controller.GetCompletion("gpu-allocation-etw")).Outcome);

        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Attempts);
    }

    [Fact]
    public async Task RevokedFastDependencyDoesNotSetFrequencyForANewSlowDependency()
    {
        var options = new BusinessBlockOptions
        {
            Blocks = new(StringComparer.Ordinal)
            {
                ["gpu-allocation-etw"] = new() { OnFailure = BusinessBlockFailurePolicy.StopDependents }
            }
        };
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness(blockOptions: options);
        var collector = harness.Collector;
        var fast = collector.AcquireSubscription(TimeSpan.FromSeconds(1), "fast-consumer");
        await fast.Ready;

        factory.Current.End(new IOException("injected session failure"));
        Assert.Equal(BusinessBlockOutcome.Failed,
            (await harness.Controller.GetCompletion("gpu-allocation-etw")).Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fast.Released);

        var slow = collector.AcquireSubscription(TimeSpan.FromSeconds(30), "slow-consumer");
        await slow.Ready;
        Assert.Equal(2, factory.Attempts);

        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Current.Captures);
        Assert.DoesNotContain(GpuAllocationCollector.Provider, factory.Current.Enabled);
        Assert.Empty(factory.Current.Enabled);
    }

    [Fact]
    public async Task ExplicitConfigurationRetryCanUseCurrentRunAfterInitialDependencyReadyFailed()
    {
        var factory = new GpuAllocationTestSessionFactory { Failures = 1 };
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        var subscription = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<IOException>(() => subscription.Ready);
        Assert.Equal(1, factory.Attempts);

        var reason = new BusinessBlockReason(BusinessBlockRequestKind.Configuration,
            "gpu-allocation-test", "explicit-retry", harness.Controller.CurrentPlanVersion);
        await harness.Controller.SetEnabledAsync("gpu-allocation-etw", false, reason);
        await harness.Controller.SetEnabledAsync("gpu-allocation-etw", true, reason);
        Assert.Equal(2, factory.Attempts);
        await Assert.ThrowsAsync<IOException>(() => subscription.Ready);

        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);

        Assert.Equal(1, factory.Current.Captures);
        Assert.NotNull(collector.ReadCurrent(GpuAllocationTestSession.Processes));
    }

    [Fact]
    public async Task ProviderDisableFailureFailsTheBlockAndPublishesEmptyForThatDue()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        var subscription = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await subscription.Ready;
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.NotNull(collector.ReadCurrent(GpuAllocationTestSession.Processes));

        factory.Current.FailDisable = true;
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(BusinessBlockOutcome.Failed,
            (await harness.Controller.GetCompletion("gpu-allocation-etw")).Outcome);
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Attempts);
    }

    [Fact]
    public async Task CaptureStateFailureFailsTheBlockAndCannotRetryOnTheNextDue()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        var subscription = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await subscription.Ready;
        factory.Current.FailCapture = true;

        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(BusinessBlockOutcome.Failed,
            (await harness.Controller.GetCompletion("gpu-allocation-etw")).Outcome);
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, factory.Current.Captures);
    }

    [Fact]
    public async Task ProductionConstructorRequiresTheControllerAndCompilesVisibleConfiguration()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        GpuAllocationCollector? assigned = null;
        var registration = new BusinessBlockRegistration("gpu-allocation-etw", [], _ => assigned!);
        var controller = new BusinessBlockController(services, [registration], Options.Create(new BusinessBlockOptions()),
            NullLogger<BusinessBlockController>.Instance);
        assigned = new GpuAllocationCollector(NullLogger<GpuAllocationCollector>.Instance,
            new ConfigurationBuilder().Build(), controller);
        Assert.Same(controller, ReadField(assigned, "controller"));
        await controller.DisposeAsync();
    }

    private static Guid BoundaryProvider()
        => EventSource.GetGuid(typeof(GpuAllocationBoundary));

    private static object ReadField(object instance, string name)
        => instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException($"Expected private field '{name}'.");
}
