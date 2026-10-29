using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationBusinessBlockTests
{
    [Fact]
    public async Task ProductionConstructorKeepsBlockDiagnosticsSeparateFromScheduling()
    {
        await using var harness = new GpuAllocationTestSessionFactory().Harness();
        var collector = new GpuAllocationCollector(NullLogger<GpuAllocationCollector>.Instance,
            new ConfigurationBuilder().Build(), harness.Diagnostics, harness.Availability);
        var fields = typeof(GpuAllocationCollector).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, field => ReferenceEquals(field.GetValue(collector), harness.Controller));
        Assert.Contains(fields, field => ReferenceEquals(field.GetValue(collector), harness.Diagnostics));
    }

    [Fact]
    public async Task ReadsDoNotStartTheSessionAndTheFieldOwnerControlsItsLifetime()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.Equal(0, factory.Attempts);

        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        Assert.Equal(0, factory.Attempts);
        Assert.Empty(factory.Sessions);
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        Assert.Equal(0, factory.Attempts);

        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.Equal(1, factory.Current.Captures);
        var published = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        Assert.NotNull(published);
        Assert.Equal(1, factory.Current.Stops);
        Assert.True(factory.Current.Joined);
        Assert.Same(published, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await harness.StopAsync();
        Assert.Equal(1, factory.Current.Stops);
        Assert.Equal(Timeout.InfiniteTimeSpan, factory.Current.StopTimeout);
        Assert.Empty(factory.Current.Enabled);
        Assert.Same(published, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(BusinessBlockOutcome.Stopped, (await harness.NextResultAsync()).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessingReturnReleasesTheOldSessionAndTheParentRestartsWhileTheFieldIsInUse(bool fail)
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        var published = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        var old = factory.Current;
        old.End(fail ? new IOException("injected GPU consumer failure") : null);
        var result = await harness.NextResultAsync();
        Assert.Equal(fail ? BusinessBlockOutcome.Failed : BusinessBlockOutcome.Done, result.Outcome);
        Assert.Equal(1, old.Stops);
        Assert.Same(published, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await harness.WaitRestartedAsync();
        Assert.Equal(1, factory.Attempts);
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.NotNull(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(1, factory.Current.Captures);
        await harness.StopAsync();
    }

    [Fact]
    public async Task AnEarlierFastCaptureDoesNotSetFrequencyForANewSlowRun()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        await harness.StopAsync();
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.Equal(1, factory.Current.Captures);
        Assert.Empty(factory.Current.Enabled);
        await harness.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCaptureOrDisableFailureRecordsTheBlockFailureAndPublishesEmpty(bool disableFailure)
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        Assert.NotNull(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        var old = factory.Current;
        old.FailDisable = disableFailure;
        old.FailCapture = !disableFailure;
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        var result = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Run, result.FailureStage);
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(1, old.Stops);
        await harness.WaitRestartedAsync();
        Assert.Equal(1, factory.Attempts);
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.NotNull(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await harness.StopAsync();
    }

}
