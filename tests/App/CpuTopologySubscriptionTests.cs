using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class CpuTopologySubscriptionTests
{
    [Fact]
    public async Task SameCpuTopologyProviderRestartsAndPublishesFreshCapture()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        for (var run = 1; run <= 2; run++)
        {
            await fixture.Reader.StartAsync(default);
            try
            {
                await using var subscription = fixture.Subscribe("restart", TimeSpan.FromHours(1));
                var next = subscription.Next();
                Assert.Equal(run, await fixture.Sampler.WaitForCaptureAsync());
                var snapshot = CpuTopologyProviderTestFixture.CreateSnapshot(run * 20);
                fixture.Sampler.Complete(snapshot);
                Assert.Same(snapshot, await next.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
                Assert.Same(snapshot, fixture.Reader.GetSnapshot());
            }
            finally { await fixture.Reader.StopAsync(default); }
            Assert.Equal(0u, fixture.ReadDemand().SourceCount);
        }
    }

    [Fact]
    public void RegisteredCurrentValueReaderDoesNotCaptureOnRead()
    {
        var overrides = new CountingOverrideStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly);
        services.AddSingleton<ICpuCorePerformanceOverrideStore>(overrides);
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<ICpuTopologyReader>();

        var first = reader.GetSnapshot();
        var second = reader.GetSnapshot();

        Assert.Equal(0, overrides.LoadCount);
        Assert.Null(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task NativeDemandUsesOneItemAndTheHighestRequestedFrequency()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var slow = fixture.Subscribe("same-id", TimeSpan.FromSeconds(5));
        await using var fast = fixture.Subscribe("same-id", TimeSpan.FromSeconds(1));
        _ = slow.Next();
        _ = fast.Next();

        var demand = fixture.ReadDemand();
        Assert.Equal(2u, demand.SourceCount);
        Assert.Equal(2u, demand.PersistentSourceCount);
        Assert.Equal(1u, demand.ItemCount);
        Assert.Equal(1_000ul, demand.IntervalMilliseconds);
        Assert.Null(fixture.Reader.GetSnapshot());
        Assert.Equal(0, fixture.Sampler.CaptureCount);

        await fast.DisposeAsync();
        demand = fixture.ReadDemand();
        Assert.Equal(1u, demand.SourceCount);
        Assert.Equal(5_000ul, demand.IntervalMilliseconds);
        Assert.Equal(0, fixture.Sampler.CaptureCount);
    }

    [Fact]
    public async Task TopologyStartAndStopPreserveAllSixExistingNativeSessions()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        var existing = Enumerable.Range(1, 6)
            .Select(role => new NativeItemSamplingSubscriptionTracker<string>(
                fixture.Owner, role, static request => request, static request => [request],
                static (items, _) => items.FirstOrDefault(), coalesceItemsIntoLatestCapture: false))
            .ToArray();
        for (var index = 0; index < existing.Length; index++)
        {
            existing[index].TrackPersistent($"existing:{index + 1}", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        }

        await using var subscription = fixture.Subscribe();
        _ = subscription.Next();
        await fixture.Reader.StartAsync(CancellationToken.None);
        _ = await fixture.Sampler.WaitForCaptureAsync();
        for (var role = 1; role <= 6; role++)
        {
            Assert.Equal((1u, 1u, 1u, 5_000ul), fixture.ReadDemand(role));
        }

        var stopping = fixture.Reader.StopAsync(CancellationToken.None);
        fixture.Sampler.Complete(CpuTopologyProviderTestFixture.CreateSnapshot(34));
        await stopping.WaitAsync(CpuTopologyProviderTestFixture.Deadline);
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
        for (var role = 1; role <= 6; role++)
        {
            Assert.Equal((1u, 1u, 1u, 5_000ul), fixture.ReadDemand(role));
            existing[role - 1].Clear(DateTimeOffset.UtcNow);
        }
    }

    [Fact]
    public async Task MultipleSubscribersReceiveOneCompletedCapture()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var first = fixture.Subscribe("first", TimeSpan.FromSeconds(1));
        await using var second = fixture.Subscribe("second", TimeSpan.FromSeconds(2));
        var firstValue = first.Next();
        var secondValue = second.Next();
        await fixture.Reader.StartAsync(CancellationToken.None);
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        Assert.False(firstValue.IsCompleted);
        Assert.False(secondValue.IsCompleted);
        Assert.Null(fixture.Reader.GetSnapshot());

        var snapshot = CpuTopologyProviderTestFixture.CreateSnapshot(21);
        fixture.Sampler.Complete(snapshot);

        Assert.Same(snapshot, await firstValue.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Same(snapshot, await secondValue.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Same(snapshot, fixture.Reader.GetSnapshot());
    }

    [Fact]
    public async Task OrdinaryReadsRetainCurrentValueWhileTheNextCaptureIsIncomplete()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var subscription = fixture.Subscribe();
        var first = subscription.Next();
        await fixture.Reader.StartAsync(CancellationToken.None);
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        var saved = CpuTopologyProviderTestFixture.CreateSnapshot(12);
        fixture.Sampler.Complete(saved);
        Assert.Same(saved, await first.WaitAsync(CpuTopologyProviderTestFixture.Deadline));

        var next = subscription.Next();
        Assert.Equal(2, await fixture.Sampler.WaitForCaptureAsync());
        for (var index = 0; index < 100; index++)
        {
            Assert.Same(saved, fixture.Reader.GetSnapshot());
        }
        Assert.Equal(2, fixture.Sampler.CaptureCount);
        Assert.False(next.IsCompleted);

        var updated = CpuTopologyProviderTestFixture.CreateSnapshot(34);
        fixture.Sampler.Complete(updated);
        Assert.Same(updated, await next.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
    }

    [Fact]
    public async Task FailedCapturePublishesNullAndTheSameSubscriptionRecovers()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var subscription = fixture.Subscribe();
        var first = subscription.Next();
        await fixture.Reader.StartAsync(CancellationToken.None);
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        fixture.Sampler.Complete(CpuTopologyProviderTestFixture.CreateSnapshot(12));
        Assert.NotNull(await first.WaitAsync(CpuTopologyProviderTestFixture.Deadline));

        var empty = subscription.Next();
        Assert.Equal(2, await fixture.Sampler.WaitForCaptureAsync());
        fixture.Sampler.Complete(new InvalidOperationException("Native capture failed."));
        Assert.Null(await empty.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Null(fixture.Reader.GetSnapshot());

        var recovered = subscription.Next();
        Assert.Equal(3, await fixture.Sampler.WaitForCaptureAsync());
        var snapshot = CpuTopologyProviderTestFixture.CreateSnapshot(34);
        fixture.Sampler.Complete(snapshot);
        Assert.Same(snapshot, await recovered.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Equal(1u, fixture.ReadDemand().SourceCount);
    }

    [Fact]
    public async Task ReleaseAndReconnectChangeDemandWithoutInventingAPublication()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var subscription = fixture.Subscribe();
        var first = subscription.Next();
        await fixture.Reader.StartAsync(CancellationToken.None);
        _ = await fixture.Sampler.WaitForCaptureAsync();
        var saved = CpuTopologyProviderTestFixture.CreateSnapshot(12);
        fixture.Sampler.Complete(saved);
        Assert.Same(saved, await first.WaitAsync(CpuTopologyProviderTestFixture.Deadline));

        await subscription.DisposeAsync();
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
        Assert.Same(saved, fixture.Reader.GetSnapshot());

        await using var reconnected = fixture.Subscribe();
        var callback = reconnected.Next();
        Assert.Equal(1u, fixture.ReadDemand().SourceCount);
        Assert.Same(saved, fixture.Reader.GetSnapshot());
        Assert.False(callback.IsCompleted);
        _ = await fixture.Sampler.WaitForCaptureAsync();
        Assert.False(callback.IsCompleted);
        var updated = CpuTopologyProviderTestFixture.CreateSnapshot(34);
        fixture.Sampler.Complete(updated);
        Assert.Same(updated, await callback.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
    }

    [Fact]
    public async Task StopDoesNotPublishALateCaptureOrClearTheCurrentValue()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var subscription = fixture.Subscribe();
        var first = subscription.Next();
        await fixture.Reader.StartAsync(CancellationToken.None);
        _ = await fixture.Sampler.WaitForCaptureAsync();
        var saved = CpuTopologyProviderTestFixture.CreateSnapshot(12);
        fixture.Sampler.Complete(saved);
        Assert.Same(saved, await first.WaitAsync(CpuTopologyProviderTestFixture.Deadline));

        var pending = subscription.Next();
        _ = await fixture.Sampler.WaitForCaptureAsync();
        var stopping = fixture.Reader.StopAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);
        fixture.Sampler.Complete(CpuTopologyProviderTestFixture.CreateSnapshot(34));
        await stopping.WaitAsync(CpuTopologyProviderTestFixture.Deadline);

        Assert.False(pending.IsCompleted);
        Assert.Same(saved, fixture.Reader.GetSnapshot());
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
        Assert.Equal(2, fixture.Sampler.CaptureCount);
        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task WorkerWithNoSubscribersDoesNotCapture()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await fixture.Reader.StartAsync(CancellationToken.None);
        Assert.Null(fixture.Reader.GetSnapshot());
        await fixture.Reader.StopAsync(CancellationToken.None);

        Assert.Equal(0, fixture.Sampler.CaptureCount);
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopBeforeStartupReleasesRegisteredDemand(bool dispose)
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var subscription = fixture.Subscribe();
        _ = subscription.Next();
        Assert.Equal(1u, fixture.ReadDemand().SourceCount);

        if (dispose)
        {
            fixture.Reader.Dispose();
        }
        else
        {
            await fixture.Reader.StopAsync(CancellationToken.None);
        }
        await subscription.DisposeAsync();

        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
        Assert.Equal(0, fixture.Sampler.CaptureCount);
        Assert.Null(fixture.Reader.GetSnapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredStopSettlesAfterTheSamplingOwnerRetires(bool disposeOwner)
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var subscription = fixture.Subscribe();
        var first = subscription.Next();
        await fixture.Reader.StartAsync(CancellationToken.None);
        _ = await fixture.Sampler.WaitForCaptureAsync();
        var saved = CpuTopologyProviderTestFixture.CreateSnapshot(12);
        fixture.Sampler.Complete(saved);
        Assert.Same(saved, await first.WaitAsync(CpuTopologyProviderTestFixture.Deadline));

        var pending = subscription.Next();
        _ = await fixture.Sampler.WaitForCaptureAsync();
        await fixture.Reader.StopAsync(new CancellationToken(canceled: true));
        await fixture.Owner.StopAsync(CancellationToken.None);
        if (disposeOwner)
        {
            Assert.False(fixture.Reader.ExecuteTask!.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => fixture.Owner.Dispose());
        }
        fixture.Sampler.Complete(CpuTopologyProviderTestFixture.CreateSnapshot(34));
        await fixture.Reader.ExecuteTask!.WaitAsync(CpuTopologyProviderTestFixture.Deadline);
        if (disposeOwner)
        {
            fixture.Owner.Dispose();
        }

        Assert.Same(saved, fixture.Reader.GetSnapshot());
        Assert.False(pending.IsCompleted);
        Assert.True(fixture.Reader.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void RealStaticTopologyDoesNotAdvanceTheProcessorUsageBaseline()
    {
        var nativeReads = 0;
        var reader = new WindowsCpuTopologyReader(new CountingOverrideStore(), () =>
        {
            nativeReads++;
            return new Dictionary<int, WindowsCpuOsTopologyReader.ProcessorTimes>
            { [0] = new(nativeReads, nativeReads * 2, nativeReads) };
        });
        var topology = reader.CaptureTopology();

        Assert.NotEmpty(topology.LogicalProcessors);
        Assert.NotEmpty(topology.PhysicalCores);
        var reported = Assert.IsType<CpuOsTopologyInformation>(topology.OsTopologyInformation);
        Assert.NotNull(reported.PhysicalCores);
        Assert.NotNull(reported.Caches);
        Assert.NotNull(reported.NumaNodes);
        var numaProcessors = reported.NumaNodes.SelectMany(static node => node.LogicalProcessorIds).ToHashSet();
        Assert.All(reported.PhysicalCores.SelectMany(static core => core.LogicalProcessorIds),
            id => Assert.Contains(id, numaProcessors));
        Assert.All(topology.LogicalProcessors, processor => Assert.Null(processor.UsagePercent));
        Assert.Equal(0, nativeReads);
        var first = reader.CaptureSnapshot();
        Assert.Equal(1, nativeReads);
        Assert.All(first.OsTopologyInformation!.LogicalProcessorBusyPercent!, value => Assert.Null(value));

        _ = reader.CaptureTopology();
        Assert.Equal(1, nativeReads);
        var second = reader.CaptureSnapshot();
        Assert.Equal(2, nativeReads);
        Assert.Equal(200d / 3, second.OsTopologyInformation!.LogicalProcessorBusyPercent![0]);
    }

    private sealed class CountingOverrideStore : ICpuCorePerformanceOverrideStore
    {
        public CpuPerformanceOverrides LoadConfiguration(string cpuName) => new(LoadScores(cpuName), null);

        public void SaveBaselineRatio(double ratio) => throw new NotSupportedException();

        public void ResetBaselineRatio() => throw new NotSupportedException();

        public int LoadCount { get; private set; }

        public IReadOnlyDictionary<int, double> LoadScores(string cpuName)
        {
            LoadCount++;
            return new Dictionary<int, double>();
        }

        public CpuCorePerformanceOverrideResult Save(CpuCorePerformanceOverrideRequest request)
            => throw new NotSupportedException();

        public CpuCorePerformanceOverrideResult Reset(string cpuName)
            => throw new NotSupportedException();
    }
}
