using System.Diagnostics;
using System.Text.Json;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlySegmentFailureClearsCurrentDecisionsAndCancellationRetainsPublication(bool canceled)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var previous = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        Assert.NotNull(previous.ActionBudget);
        Exception exception = canceled ? new OperationCanceledException() : new InvalidOperationException("before sampling");
        fixture.Coordinator.OuterLoopProbe = new ThrowingObservationRound(exception);
        try
        {
            Assert.Same(exception, await Record.ExceptionAsync(() => fixture.Coordinator.RunOnceAsync(CancellationToken.None)));
            var current = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
            if (canceled)
            {
                Assert.Same(previous, current);
                Assert.Null((await fixture.Coordinator.GetStatusAsync(CancellationToken.None)).Failure);
            }
            else
            {
                Assert.NotSame(previous, current);
                Assert.Equal(nameof(InvalidOperationException), current.Failure!.ExceptionType);
                Assert.Equal(HostManagerCycleFailureKind.CycleFailed, current.Failure.Kind);
                var status = await fixture.Coordinator.GetStatusAsync(CancellationToken.None);
                Assert.Same(current.Failure, status.Failure);
                Assert.Equal(0, status.FactExclusionCount);
                Assert.Empty(status.FactExclusions);
                Assert.Null(current.Failure.NativePlanStatus);
                Assert.Null(current.NativeCoordinator);
                Assert.Null(current.ActionBudget);
                Assert.Empty(current.FactExclusions);
            }
        }
        finally { fixture.Coordinator.OuterLoopProbe = null; }
    }

    private sealed class ThrowingObservationRound(Exception exception) : IHostManagerSmartCoordinatorOuterLoopProbe
    {
        public void Observe(in HostManagerSmartCoordinatorOuterLoopEvent value)
        {
            if (value.Phase == HostManagerSmartCoordinatorOuterLoopPhase.CoreEntered) throw exception;
        }
    }

    internal static async Task<(HostManagerSmartCoordinator Coordinator, IAsyncDisposable Lifetime)> CreateDecisionSubscriptionFixtureAsync()
    {
        var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            optimizationMode: ResourceManager.App.Domain.Settings.AppOptimizationModes.Normal);
        return (fixture.Coordinator, fixture);
    }

    [Fact]
    public async Task DecisionSubscriptionReadsWithoutGateAndWaitsForSettledPublication()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var previous = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var probe = new PausedObservationRound();
        fixture.Coordinator.OuterLoopProbe = probe;
        var round = Task.Run(() => fixture.Coordinator.RunOnceAsync(cancellation.Token));
        await using var stream = fixture.Coordinator.SubscribeDecisionDiagnosticsAsync(cancellation.Token).GetAsyncEnumerator();
        try
        {
            await probe.Entered.Task.WaitAsync(cancellation.Token);
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Same(previous, stream.Current);
            var next = stream.MoveNextAsync().AsTask();
            Assert.False(next.IsCompleted);
            probe.Release.Set();
            await round;
            Assert.True(await next.WaitAsync(cancellation.Token));
            Assert.Same(await fixture.Coordinator.GetDecisionDiagnosticsAsync(cancellation.Token), stream.Current);
            Assert.NotSame(previous, stream.Current);
            var canceled = stream.MoveNextAsync().AsTask();
            Assert.False(canceled.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        }
        finally
        {
            probe.Release.Set();
            await round.WaitAsync(TimeSpan.FromSeconds(20));
            fixture.Coordinator.OuterLoopProbe = null;
        }
    }

    [Theory]
    [InlineData("state")]
    [InlineData("status")]
    [InlineData("decisions")]
    public async Task ObservationReturnsPublishedValueWhileRoundHoldsGate(string observation)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var previous = await fixture.Coordinator.GetStateAsync(CancellationToken.None);
        using var probe = new PausedObservationRound();
        fixture.Coordinator.OuterLoopProbe = probe;
        var round = Task.Run(() => fixture.Coordinator.RunOnceAsync(CancellationToken.None));
        Task? read = null;
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, ReadPrivateField<SemaphoreSlim>(fixture.Coordinator, "gate")!.CurrentCount);
            var elapsed = Stopwatch.StartNew();
            read = observation switch
            {
                "state" => fixture.Coordinator.GetStateAsync(CancellationToken.None),
                "status" => fixture.Coordinator.GetStatusAsync(CancellationToken.None),
                _ => fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None)
            };
            await read.WaitAsync(TimeSpan.FromSeconds(2));
            output.WriteLine("{0} read while round holds gate: {1:F3} ms", observation, elapsed.Elapsed.TotalMilliseconds);
            Assert.False(round.IsCompleted);
            var state = await fixture.Coordinator.GetStateAsync(CancellationToken.None);
            Assert.Equal(previous.Coordinator, state.Coordinator);
            Assert.Equal(previous.LastRunAt, state.LastRunAt);
        }
        finally
        {
            probe.Release.Set();
            await round.WaitAsync(TimeSpan.FromSeconds(20));
            if (read is not null) await read.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Coordinator.OuterLoopProbe = null;
        }
    }

    [Fact]
    public async Task ObservationPublishesNativeDataOnlyAfterSegmentSettlementAndReadsCurrentStore()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            performanceLogEnabled: true);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var previous = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        using var probe = new PausedObservationRound(HostManagerSmartCoordinatorOuterLoopPhase.CoreTerminal);
        fixture.Coordinator.OuterLoopProbe = probe;
        var round = Task.Run(() => fixture.Coordinator.RunOnceAsync(CancellationToken.None));
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(previous.NativeCoordinator!.CycleSequence,
                fixture.Workspace.Snapshot.CycleSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var current = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Same(previous, current);
            var restoredAt = DateTimeOffset.UtcNow;
            fixture.StateStore.Current = fixture.StateStore.Current with { LastRestoreAt = restoredAt };
            var state = await fixture.Coordinator.GetStateAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            var status = await fixture.Coordinator.GetStatusAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(restoredAt, state.LastRestoreAt);
            Assert.Equal(restoredAt, status.LastRestoreAt);
            Assert.Equal(previous.NativeCoordinator.StateRevision,
                state.Coordinator!.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(round.IsCompleted);
        }
        finally
        {
            probe.Release.Set();
            await round.WaitAsync(TimeSpan.FromSeconds(20));
            fixture.Coordinator.OuterLoopProbe = null;
        }
        var published = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        Assert.NotSame(previous, published);
        Assert.Equal(fixture.Workspace.Snapshot.CycleSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            published.NativeCoordinator!.CycleSequence);
        output.WriteLine("Controlled fixture round timing (not installed game load): {0}",
            JsonSerializer.Serialize(fixture.DebugLogWriter.Records[0].Properties));
    }

    private sealed class PausedObservationRound(
        HostManagerSmartCoordinatorOuterLoopPhase phase = HostManagerSmartCoordinatorOuterLoopPhase.CoreEntered)
        : IHostManagerSmartCoordinatorOuterLoopProbe, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();

        public void Observe(in HostManagerSmartCoordinatorOuterLoopEvent value)
        {
            if (value.Phase != phase) return;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Test round was not released.");
        }

        public void Dispose() => Release.Dispose();
    }
}
