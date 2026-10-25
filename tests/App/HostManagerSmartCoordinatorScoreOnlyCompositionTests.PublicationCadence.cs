using System.Runtime.CompilerServices;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task BackgroundStartupWaitsForResidencyPublicationAndEmptyOutputUsesProcessCpuFallback()
    {
        var reader = new PublicationControlledCpuResidencyReader();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: CreateRoundProcessFacts(),
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            normalIntervalMilliseconds: 500,
            eventIntervalMilliseconds: 100,
            cpuCoreReader: reader);

        // The fixture's native cycle raises its real event boost before the hosted coordinator starts.
        Assert.Equal(TimeSpan.FromMilliseconds(100), await fixture.RunRealtimeCycleAsync("manual"));
        Assert.Equal(10U, fixture.Workspace.Snapshot.EventBoostRoundsRemaining);
        var sequenceBeforeStart = fixture.Workspace.Snapshot.CycleSequence;
        var readsBeforeStart = reader.ReadCount;
        var processReadsBeforeStart = fixture.ProcessFacts.ReadLatestRequests.Count;
        var trace = new OuterLoopTraceProbe();
        fixture.Coordinator.OuterLoopProbe = trace;

        await fixture.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            await reader.WaitForSubscriptionAsync();
            Assert.Equal(("host-manager-smart-coordinator:cpu-cores", TimeSpan.FromMilliseconds(100)),
                Assert.Single(reader.Subscriptions));
            Assert.Equal(sequenceBeforeStart, fixture.Workspace.Snapshot.CycleSequence);
            Assert.Equal(readsBeforeStart, reader.ReadCount);
            Assert.Equal(processReadsBeforeStart, fixture.ProcessFacts.ReadLatestRequests.Count);

            reader.Publish(null);
            await WaitForHostedScoreOnlyCycleAsync(trace, 1);

            Assert.Equal(HostManagerCpuOccupancySource.ProcessCpuUsage,
                fixture.Coordinator.SchedulingAuthority.Compute!.CpuOccupancySource);
            Assert.Contains(
                fixture.ProcessFacts.ReadLatestRequests.Skip(processReadsBeforeStart),
                static request => request.RequestedMetricMask.HasFlag(
                    SchedulingProcessMetricMask.CpuUsage));

            var completedSequence = fixture.Workspace.Snapshot.CycleSequence;
            // More than two normal planner intervals pass with no later residency publication.
            await Task.Delay(TimeSpan.FromMilliseconds(1_100));
            Assert.Equal(completedSequence, fixture.Workspace.Snapshot.CycleSequence);
        }
        finally
        {
            await fixture.Coordinator.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, reader.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task ManualRunOnceReadsCurrentResidencyWithoutWaitingForPublication()
    {
        var processFacts = CreateRoundProcessFacts();
        var current = CpuCoreResidencyTestValues.Create(
            1,
            DateTimeOffset.UtcNow,
            processFacts.Processes.Select(process => CpuCoreResidencyTestValues.Process(
                process.ProcessId,
                process.ProcessStartKey,
                ("core:0", process.CpuUsagePercent))).ToArray());
        var reader = new PublicationControlledCpuResidencyReader(current);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: processFacts,
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            cpuCoreReader: reader);

        await fixture.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            await reader.WaitForSubscriptionAsync();
            Assert.Equal(0, reader.PublicationCount);
            var readsBeforeRunOnce = reader.ReadCount;

            await fixture.Coordinator.RunOnceAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(reader.ReadCount > readsBeforeRunOnce);
            Assert.Equal(0, reader.PublicationCount);
            Assert.Equal(HostManagerCpuOccupancySource.CoreResidency,
                fixture.Coordinator.SchedulingAuthority.Compute!.CpuOccupancySource);
        }
        finally
        {
            await fixture.Coordinator.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class PublicationControlledCpuResidencyReader(
        CpuCoreResidencySnapshot? current = null) : ICpuCoreResidencyReader
    {
        private readonly object gate = new();
        private readonly CurrentValuePublicationSignal publications = new();
        private readonly TaskCompletionSource<bool> subscriptionStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(string Id, TimeSpan Interval)> subscriptions = [];
        private CpuCoreResidencySnapshot? latest = current;
        private int activeSubscriptionCount;
        private int publicationCount;
        private int readCount;

        internal int ActiveSubscriptionCount => Volatile.Read(ref activeSubscriptionCount);
        internal int PublicationCount => Volatile.Read(ref publicationCount);
        internal int ReadCount => Volatile.Read(ref readCount);

        internal IReadOnlyList<(string Id, TimeSpan Interval)> Subscriptions
        {
            get
            {
                lock (gate)
                {
                    return [.. subscriptions];
                }
            }
        }

        public CpuCoreResidencySnapshot? Read()
        {
            Interlocked.Increment(ref readCount);
            lock (gate)
            {
                return latest;
            }
        }

        public IDisposable AcquireSubscription(string subscriptionId, TimeSpan refreshInterval)
        {
            lock (gate)
            {
                subscriptions.Add((subscriptionId, refreshInterval));
            }
            Interlocked.Increment(ref activeSubscriptionCount);
            return new Subscription(this);
        }

        public async IAsyncEnumerable<CpuCoreResidencySnapshot?> SubscribeAsync(
            string subscriptionId,
            TimeSpan interval,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var demand = AcquireSubscription(subscriptionId, interval);
            using var subscription = publications.Subscribe();
            subscriptionStarted.TrySetResult(true);
            while (true)
            {
                await subscription.WaitAsync(cancellationToken);
                yield return Read();
            }
        }

        internal Task WaitForSubscriptionAsync()
            => subscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        internal void Publish(CpuCoreResidencySnapshot? value)
        {
            lock (gate)
            {
                latest = value;
            }
            Interlocked.Increment(ref publicationCount);
            publications.Publish();
        }

        private void ReleaseSubscription()
            => Interlocked.Decrement(ref activeSubscriptionCount);

        private sealed class Subscription(PublicationControlledCpuResidencyReader owner) : IDisposable
        {
            private int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    owner.ReleaseSubscription();
                }
            }
        }
    }
}
