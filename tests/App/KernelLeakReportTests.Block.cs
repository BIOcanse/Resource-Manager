using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Infrastructure.KernelLeak;
using ResourceManager.Shared.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed partial class KernelLeakReportTests
{
    [Fact]
    public async Task PlanFactControlsObservationWhileReportPlanUpdatesAreDetached()
    {
        using var fixture = new Fixture(options: Fast with { Enabled = false });
        var observer = Assert.IsType<KernelLeakObserver>(fixture.Reports);
        await observer.StopAsync(default);
        Assert.Equal(0, fixture.Processes.Starts);
        Assert.False(fixture.Plans.DecisionFacts.KernelLeakObservationEnabled);
        Assert.Empty(fixture.Plans.Publish(fixture.Plan with { Version = 2,
            HostManager = fixture.Plan.HostManager with { KernelLeak = Fast with { Enabled = true } }
        }).DeliveryFailures);
        await fixture.Processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(fixture.Plans.DecisionFacts.KernelLeakObservationEnabled);
        Assert.Equal(BusinessBlockState.Running, fixture.Scheduler.ReadState(BackendUids.KernelLeakObservation));
        await fixture.StopAsync();
        Assert.False(fixture.Plans.DecisionFacts.KernelLeakObservationEnabled);
        Assert.Equal(KernelLeakReportStatus.NotObserved, fixture.Reports.Read().Status);
        Assert.Equal(0, fixture.Processes.Active);
    }

    [Fact]
    public async Task HostedLifetimeOnlyRefreshesStoppedReportRequirements()
    {
        using var fixture = new Fixture(deferPublication: true);
        var observer = Assert.IsType<KernelLeakObserver>(fixture.Reports);
        Assert.Equal(0, fixture.Reports.Read().Observation.RequiredSampleCount);
        var options = Fast with { Enabled = false, MinimumSamples = 4, MinimumObservationSeconds = 3 };
        Assert.Empty(fixture.Plans.Publish(fixture.Plan with {
            HostManager = fixture.Plan.HostManager with { KernelLeak = options }
        }).DeliveryFailures);
        Assert.Equal(4, fixture.Reports.Read().Observation.RequiredSampleCount);
        Assert.Equal(3, fixture.Reports.Read().Observation.RequiredSeconds);
        await observer.StopAsync(default);
        Assert.Empty(fixture.Plans.Publish(fixture.Plan with { Version = 2,
            HostManager = fixture.Plan.HostManager with { KernelLeak = Fast with { Enabled = false } }
        }).DeliveryFailures);
        Assert.Equal(4, fixture.Reports.Read().Observation.RequiredSampleCount);
        await observer.StartAsync(default);
        Assert.Equal(3, fixture.Reports.Read().Observation.RequiredSampleCount);
        Assert.Equal(2, fixture.Reports.Read().Observation.RequiredSeconds);
        Assert.Equal(KernelLeakReportStatus.NotObserved, fixture.Reports.Read().Status);
        Assert.Equal(0, fixture.Processes.Starts);
    }

    [Fact]
    public async Task PublishedPlanAloneEnablesStopsAndRestartsObservation()
    {
        using var fixture = new Fixture(deferPublication: true);
        Assert.Equal(BusinessBlockState.Stopped, fixture.Scheduler.ReadState(BackendUids.KernelLeakObservation));
        Assert.Equal(KernelLeakReportStatus.NotObserved, fixture.Reports.Read().Status);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reader = fixture.Reports.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(0, fixture.Processes.Starts);
        await reader.DisposeAsync();
        Assert.Equal(BusinessBlockState.Stopped, fixture.Scheduler.ReadState(BackendUids.KernelLeakObservation));

        void Publish(bool value, long version) => Assert.Empty(fixture.Plans.Publish(fixture.Plan with
        {
            Version = version,
            HostManager = fixture.Plan.HostManager with { KernelLeak = Fast with { Enabled = value } }
        }).DeliveryFailures);
        Publish(false, 1);
        Assert.Equal(0, fixture.Processes.Starts);
        Publish(true, 2);
        await fixture.Processes.Started.Task.WaitAsync(timeout.Token);
        for (var i = 0; i < 3; i++) await DeliverWithoutReader(fixture, i, 1000 + i * 100);
        Assert.Equal(KernelLeakReportStatus.Suspected, fixture.Reports.Read().Status);
        Assert.True(fixture.SamplerInUse);
        await BusinessBlockTestSupport.Until(() => fixture.Scheduler.ReadState(BackendUids.WindowsResourceBreakdownSampler) == BusinessBlockState.Running);
        Publish(false, 3);
        await BusinessBlockTestSupport.Until(() => fixture.Scheduler.ReadState(BackendUids.KernelLeakObservation) == BusinessBlockState.Stopped);
        Assert.Equal(0, fixture.Processes.Active);
        Assert.False(fixture.SamplerInUse);
        await BusinessBlockTestSupport.Until(() => fixture.Scheduler.ReadState(BackendUids.WindowsResourceBreakdownSampler) == BusinessBlockState.Stopped);
        Assert.Equal(0, fixture.Reports.ReadDiagnostics().SeriesBytes);
        Assert.Equal(KernelLeakReportStatus.NotObserved, fixture.Reports.Read().Status);
        Assert.Equal(KernelLeakFailure.None, fixture.Reports.Read().Failure);
        Publish(true, 4);
        await BusinessBlockTestSupport.Until(() => fixture.Processes.Starts == 2);
        await DeliverWithoutReader(fixture, 10, 1500);
        Assert.Equal(1, fixture.Reports.Read().Observation.SampleCount);
        Assert.Null(fixture.Reports.Read().Observation.PreviousRunFailure);
        Assert.Equal(KernelLeakItemState.Active, Assert.Single(fixture.Reports.Read().Items).State);
    }

    [Fact]
    public async Task AReaderOfTheStoppedReportCannotAcquireSubscription()
    {
        using var fixture = new Fixture();
        await fixture.Processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await fixture.StopAsync();
        var starts = fixture.Processes.Starts;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reader = fixture.Reports.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("NotObserved", reader.Current.Status.ToString());
        Assert.Equal(KernelLeakFailure.None, reader.Current.Failure);
        Assert.Null(reader.Current.Observation.StartedAt);
        Assert.Empty(reader.Current.Items);
        await reader.DisposeAsync();
        Assert.Equal(starts, fixture.Processes.Starts);
        Assert.Equal(0, fixture.Processes.Active);
    }

    [Theory]
    [InlineData(KernelLeakFailure.SourceUnavailable)]
    [InlineData(KernelLeakFailure.CapacityExceeded)]
    [InlineData(KernelLeakFailure.InvalidObservation)]
    public async Task FrameFailuresPublishAndContinueWithinTheSameSubscription(KernelLeakFailure failure)
    {
        using var fixture = new Fixture(rawSamples: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var reader = fixture.Reports.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        fixture.Raw!.Push(new(DateTimeOffset.UnixEpoch, true, []));
        Assert.True(await reader.MoveNextAsync());
        var at = DateTimeOffset.UnixEpoch.AddSeconds(1);
        KernelLeakSample[] samples = failure == KernelLeakFailure.CapacityExceeded
            ? Enumerable.Range(1, 9).Select(pid => new KernelLeakSample(pid, "42", KernelLeakSampleKind.ProcessHandles, 1500)).ToArray()
            : failure == KernelLeakFailure.InvalidObservation
                ? [new(7, "42", KernelLeakSampleKind.ProcessHandles, 1500), new(7, "42", KernelLeakSampleKind.ProcessHandles, 1500)]
                : [];
        fixture.Raw.Push(new(at, failure != KernelLeakFailure.SourceUnavailable, samples));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(KernelLeakReportStatus.Unavailable, reader.Current.Status);
        Assert.Equal(failure, reader.Current.Failure);
        Assert.Equal(1, fixture.Raw.Starts);
        Assert.Equal(1, fixture.Raw.Active);
        Assert.Equal(BusinessBlockState.Running, fixture.Scheduler.ReadState(BackendUids.KernelLeakObservation));
        fixture.Raw.Push(new(DateTimeOffset.UnixEpoch.AddSeconds(2), true, []));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(KernelLeakFailure.None, reader.Current.Failure);
        Assert.Equal(KernelLeakReportStatus.Observing, reader.Current.Status);
        Assert.Equal(1, fixture.Raw.Starts);
        Assert.Single(fixture.Scheduler.Read().History, row => row.BlockId == BackendUids.KernelLeakObservation && row.To == BusinessBlockState.Starting);
    }

    [Fact]
    public async Task UnexpectedSourceExceptionReturnsFromTheBlock()
    {
        using var fixture = new Fixture(options: Fast with { Enabled = false }, sampleSource: new UnexpectedSource());
        var entered = false;
        var observer = Assert.IsType<KernelLeakObserver>(fixture.Reports);
        await Assert.ThrowsAsync<InvalidOperationException>(() => observer.RunAsync(new(default, () => entered = true)));
        Assert.True(entered);
        Assert.Equal(KernelLeakReportStatus.NotObserved, observer.Read().Status);
    }

    [Fact]
    public async Task StopNotificationCancelsAttributionAndDrainsTheSubscription()
    {
        var attribution = new CancellableAttribution();
        using var fixture = new Fixture(rawSamples: true, attribution: attribution);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var reader = fixture.Reports.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        for (var i = 0; i < 2; i++)
        {
            fixture.Raw!.Push(new(DateTimeOffset.UnixEpoch.AddSeconds(i), true,
                [new(7, "42", KernelLeakSampleKind.ProcessHandles, (ulong)(1000 + i * 100))]));
            Assert.True(await reader.MoveNextAsync());
        }
        fixture.Raw!.Push(new(DateTimeOffset.UnixEpoch.AddSeconds(2), true,
            [new(7, "42", KernelLeakSampleKind.ProcessHandles, 1200)]));
        await attribution.Entered.Task.WaitAsync(timeout.Token);
        await fixture.StopAsync();
        Assert.True(attribution.Cancelled);
        Assert.Equal(0, fixture.Raw.Active);
        Assert.Equal(0, fixture.Reports.ReadDiagnostics().SeriesBytes);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(KernelLeakReportStatus.NotObserved, reader.Current.Status);
    }

    private sealed class CancellableAttribution : IKernelLeakAttributionReader
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cancelled;
        public IReadOnlyList<KernelLeakHolder> ReadBatch(IReadOnlyList<KernelLeakHolder> holders, CancellationToken cancellationToken = default)
        {
            if (holders.Count == 0) return [];
            Entered.TrySetResult();
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
            Cancelled = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Attribution was not cancelled by the block's stop notification.");
        }
    }

    private sealed class UnexpectedSource : IKernelLeakSampleSource
    {
        public IAsyncEnumerable<KernelLeakSampleFrame> SubscribeAsync(string holder, TimeSpan interval, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unexpected source defect.");
    }
}
