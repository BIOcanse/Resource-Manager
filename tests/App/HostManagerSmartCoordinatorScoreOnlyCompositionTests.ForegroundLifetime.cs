using System.Reflection;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public async Task ForegroundLifetimeShutdownJoinsOriginalOperationsWithoutHoldingGate(int count, bool directDispose)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        var probe = new BlockingTransitionProbe(HostManagerSmartCoordinatorTransitionPoint.BeforeNativeCall);
        fixture.Coordinator.TransitionProbe = probe;
        var operations = Enumerable.Range(0, count).Select(_ => EnterOriginalForegroundOperation(fixture.Coordinator)).ToArray();
        var shutdown = directDispose ? Task.Run(fixture.Coordinator.Dispose)
            : fixture.Coordinator.StopAsync(CancellationToken.None);
        try
        {
            await probe.AdmissionClosed.WaitAsync(TimeSpan.FromSeconds(10));
            // Taking the original shutdown path again also serializes with BeginShutdown publication.
            var sameShutdown = fixture.Coordinator.StopAsync(CancellationToken.None);
            Assert.False(sameShutdown.IsCompleted);
            Assert.Equal(HostManagerSmartCoordinatorLifecycleState.Closing, fixture.Coordinator.LifecycleState);
            Assert.NotNull(ReadPrivateField<NativeSmartCoordinatorWorkspace>(fixture.Coordinator, "nativeWorkspace"));
            var gate = ReadPrivateField<SemaphoreSlim>(fixture.Coordinator, "gate")!;
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(10)));
            try
            {
                // An admitted operation must still be able to re-enter for cleanup while Closing.
                Assert.NotNull(ReadPrivateField<NativeSmartCoordinatorWorkspace>(fixture.Coordinator, "nativeWorkspace"));
            }
            finally { gate.Release(); }
            for (var index = 0; index < operations.Length - 1; index++) operations[index].Dispose();
            Assert.Equal(1, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
            Assert.False(sameShutdown.IsCompleted);
        }
        finally
        {
            foreach (var operation in operations) operation.Dispose();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(0, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
        probe.AssertShutdownOrder();
        AssertOwnedResourcesCleared(fixture.Coordinator);
    }

    [Fact]
    public async Task ForegroundLifetimeCancelledStopWaitDoesNotReleaseAnActiveOperation()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        using var operation = EnterOriginalForegroundOperation(fixture.Coordinator);
        using var cancellation = new CancellationTokenSource();
        var stop = fixture.Coordinator.StopAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        var shared = fixture.Coordinator.StopAsync(CancellationToken.None);
        try
        {
            Assert.False(shared.IsCompleted);
            Assert.NotNull(ReadPrivateField<NativeSmartCoordinatorWorkspace>(fixture.Coordinator, "nativeWorkspace"));
        }
        finally
        {
            operation.Dispose();
            await shared.WaitAsync(TimeSpan.FromSeconds(10));
        }
        AssertOwnedResourcesCleared(fixture.Coordinator);
    }

    [Fact]
    public async Task ForegroundLifetimeClosingRejectsAllNewControlOperationsBeforeStateAccess()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        using var operation = EnterOriginalForegroundOperation(fixture.Coordinator);
        var shutdown = fixture.Coordinator.StopAsync(CancellationToken.None);
        try
        {
            var loads = fixture.StateStore.LoadCalls;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Coordinator.RunOnceAsync(CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Coordinator.SetModeAsync(AppOptimizationModes.Smart, CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Coordinator.RestoreNormalModeAsync(CancellationToken.None));
            Assert.Equal(loads, fixture.StateStore.LoadCalls);
            Assert.Equal(1, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
            Assert.False(shutdown.IsCompleted);
        }
        finally
        {
            operation.Dispose();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ForegroundLifetimeNewBatchCannotReuseThePreviousCompletionSignal()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        using (var first = EnterOriginalForegroundOperation(fixture.Coordinator))
        {
            first.Dispose();
            first.Dispose();
        }
        Assert.Equal(0, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
        using var second = EnterOriginalForegroundOperation(fixture.Coordinator);
        var shutdown = fixture.Coordinator.StopAsync(CancellationToken.None);
        try { Assert.False(shutdown.IsCompleted); }
        finally
        {
            second.Dispose();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(0, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
        AssertOwnedResourcesCleared(fixture.Coordinator);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("mode")]
    [InlineData("restore")]
    public async Task ForegroundLifetimeRealControlCallCancelledAtGateSettlesItsLease(string entry)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        var gate = ReadPrivateField<SemaphoreSlim>(fixture.Coordinator, "gate")!;
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        Task operation;
        try
        {
            operation = entry switch
            {
                "manual" => fixture.Coordinator.RunOnceAsync(cancellation.Token),
                "mode" => fixture.Coordinator.SetModeAsync(AppOptimizationModes.Smart, cancellation.Token),
                _ => fixture.Coordinator.RestoreNormalModeAsync(cancellation.Token)
            };
            Assert.Equal(1, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(0, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
        }
        finally { gate.Release(); }
        await fixture.Coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        AssertOwnedResourcesCleared(fixture.Coordinator);
    }

    private static IDisposable EnterOriginalForegroundOperation(HostManagerSmartCoordinator coordinator)
        => (IDisposable)typeof(HostManagerSmartCoordinator)
            .GetMethod("EnterForegroundControlRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, null)!;
}
