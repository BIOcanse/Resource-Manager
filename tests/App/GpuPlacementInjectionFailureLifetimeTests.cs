using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class GpuPlacementInjectionFailureLifetimeTests
{
    [Fact]
    public void EmptyFailureCacheDoesNotReadOrProjectTheInventory()
    {
        var injector = CreateInjector();
        injector.ReconcileExitedProcessInstances(CompleteInventory(new UnreadableInventory()),
            _ => throw new InvalidOperationException("No failed process requires an identity read."));
    }

    [Fact]
    public void ExitedProcessChurnDoesNotAccumulateCachedFailures()
    {
        var injector = CreateInjector();
        for (var index = 0; index < 256; index++)
        {
            var instance = new GpuPlacementProcessInstance(4000 + index, (ulong)index + 1, "test", @"C:\test.exe");
            SeedFailure(injector, instance);
            Assert.NotNull(injector.GetKnownProcessFailure(instance));
            injector.ReconcileExitedProcessInstances(CompleteInventory([]), _ => Exited());
            Assert.Null(injector.GetKnownProcessFailure(instance));
        }
        Assert.Equal(0, FailureCount(injector));
    }

    [Fact]
    public void ExitConfirmationCannotRemoveConcurrentlyReplacedFailure()
    {
        var injector = CreateInjector();
        var original = new GpuPlacementProcessInstance(4000, 1, "test", @"C:\test.exe");
        var replacement = original with { ProcessStartKey = 2 };
        SeedFailure(injector, original);
        injector.ReconcileExitedProcessInstances(CompleteInventory([]), _ =>
        {
            SeedFailure(injector, replacement);
            return Exited();
        });
        Assert.Null(injector.GetKnownProcessFailure(original));
        Assert.NotNull(injector.GetKnownProcessFailure(replacement));
        Assert.Equal(1, FailureCount(injector));
    }

    internal static WindowsGpuPlacementInjector CreateInjector()
        => new(NullLogger<WindowsGpuPlacementInjector>.Instance);

    private static SchedulingProcessFactSnapshot CompleteInventory(IReadOnlyList<SchedulingProcessFact> processes)
        => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks,
            0, 0, 0, 0, SchedulingProcessMetricMask.None, SchedulingProcessMetricMask.None, processes);

    internal static void SeedFailure(WindowsGpuPlacementInjector injector, GpuPlacementProcessInstance instance)
    {
        var cache = FailureCache(injector);
        var valueType = cache.GetType().GetGenericArguments()[1];
        var failure = Activator.CreateInstance(valueType, instance.ProcessStartKey,
            new RuntimeGpuProviderInjectionResult(instance.ProcessId, false, false, "test-failure", "failed", null, null, 0, null));
        cache.GetType().GetProperty("Item")!.SetValue(cache, failure, [instance.ProcessId]);
    }

    internal static RecoveryReadResult<ProcessInstanceRecoverySnapshot> Exited()
        => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(0, "exited");

    internal static RecoveryReadResult<ProcessInstanceRecoverySnapshot> Live(int processId, ulong startKey)
        => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
            new(processId, DateTimeOffset.FromFileTime(checked((long)startKey))));

    private static object FailureCache(WindowsGpuPlacementInjector injector)
        => typeof(WindowsGpuPlacementInjector).GetField("failuresByProcessId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(injector)!;

    private static int FailureCount(WindowsGpuPlacementInjector injector)
    {
        var cache = FailureCache(injector);
        return (int)cache.GetType().GetProperty("Count")!.GetValue(cache)!;
    }

    private sealed class UnreadableInventory : IReadOnlyList<SchedulingProcessFact>
    {
        public int Count => throw new InvalidOperationException("An empty cache must not project inventory.");
        public SchedulingProcessFact this[int index] => throw new InvalidOperationException();
        public IEnumerator<SchedulingProcessFact> GetEnumerator() => throw new InvalidOperationException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
