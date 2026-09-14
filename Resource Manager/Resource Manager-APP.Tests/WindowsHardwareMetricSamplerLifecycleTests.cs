using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsHardwareMetricSamplerLifecycleTests
{
    [Fact]
    public async Task LatePlanCallbackCannotReplaceCurrentHistoryRequirements()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        var provider = subscriptions.Provider;
        var current = provider.Current;
        var older = current with
        {
            Version = current.Version + 1,
            HostManager = current.HostManager with
            {
                DataHistory = CompiledDataHistoryPlan.Compile([new("cpu.usage", 2)])
            }
        };
        var newer = older with
        {
            Version = older.Version + 1,
            HostManager = older.HostManager with
            {
                DataHistory = CompiledDataHistoryPlan.Compile([new("cpu.usage", 5)])
            }
        };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.Published += plan =>
        {
            if (ReferenceEquals(plan, older))
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        using var sampler = CreateSampler(subscriptions, new DashboardMonitoringCatalogState());
        var delayed = Task.Run(() => provider.Publish(older));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            provider.Publish(newer);
        }
        finally
        {
            release.Set();
            await delayed.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Same(newer, provider.Current);
        var owner = Assert.IsType<LastSuccessfulHardwareMetricSnapshot>(
            typeof(WindowsHardwareMetricSampler).GetField("publishedSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sampler));
        for (ulong round = 1; round <= 6; round++)
        {
            var raw = CreateCatalogSnapshot() with
            {
                Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
                {
                    ["cpu.usage"] = new("cpu.usage", SamplingObservationStatus.Current, round,
                        DateTimeOffset.UtcNow.UtcTicks, 1, 1, 1, round)
                }
            };
            owner.Publish(raw, MetricSampleRequest.ForIds(["cpu.usage"]));
        }
        Assert.Equal(5, owner.Read()!.History["cpu.usage"].Length);
    }

    [Fact]
    public async Task SubscribeAsync_WaitsForBackendPublicationBeforeFirstValue()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        var catalog = new DashboardMonitoringCatalogState();
        catalog.PublishCatalogProbe(CreateCatalogSnapshot());
        using var sampler = CreateSampler(subscriptions, catalog);
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = sampler.SubscribeAsync(
                "no-bootstrap",
                MetricSampleRequest.ForIds(["cpu.usage"]),
                TimeSpan.FromSeconds(1),
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        var first = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(100);

        Assert.False(first.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await first);
    }

    [Fact]
    public void GpuMetricPublicationNotificationsExcludeInternalInventoryDependency()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = CreateSampler(
            subscriptions,
            new DashboardMonitoringCatalogState());

        var publicationIds = sampler.GetPublicationItemIds(
                MetricSampleRequest.ForIds(["gpu.0.power"]))
            .ToArray();

        Assert.Equal(["gpu.0.power"], publicationIds);
        Assert.DoesNotContain(
            SamplingDatasetIds.SystemGpuInventory,
            publicationIds,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateStartIsIdempotentAndStoppedInstanceCanRestart()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = new WindowsHardwareMetricSampler(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new MonitoringSourceZoneRegistry([]),
            subscriptions.Owner,
            subscriptions.Provider,
            NullLogger<WindowsHardwareMetricSampler>.Instance);

        await sampler.StartAsync(CancellationToken.None);
        var first = Assert.IsAssignableFrom<Task>(
            sampler.CaptureSamplingWorkerTask());

        await sampler.StartAsync(CancellationToken.None);
        Assert.Same(first, sampler.CaptureSamplingWorkerTask());

        await sampler.StopAsync(CancellationToken.None);
        Assert.Null(sampler.CaptureSamplingWorkerTask());

        await sampler.StartAsync(CancellationToken.None);
        var restarted = Assert.IsAssignableFrom<Task>(
            sampler.CaptureSamplingWorkerTask());
        Assert.NotSame(first, restarted);

        await sampler.StopAsync(CancellationToken.None);
        Assert.Null(sampler.CaptureSamplingWorkerTask());
    }

    private static WindowsHardwareMetricSampler CreateSampler(
        HostManagerSamplingSubscriptionTestFixture subscriptions,
        DashboardMonitoringCatalogState catalog)
        => new(
            cpuZone: null!,
            memoryZone: null!,
            virtualMemoryZone: null!,
            gpuAdapterOrderZone: null!,
            cpuFrequencyZone: null!,
            gpuEngineZone: null!,
            systemIoZone: null!,
            nvidiaZone: null!,
            nvidiaNvapiZone: null!,
            amdAdlxZone: null!,
            amdSmuZone: null!,
            platformSensorReader: null!,
            metricSnapshotOwner: null!,
            dashboardMonitoringCatalog: catalog,
            monitoringSourceZoneRegistry: new MonitoringSourceZoneRegistry([]),
            samplingSubscriptionOwner: subscriptions.Owner,
            runtimePlanProvider: subscriptions.Provider,
            logger: NullLogger<WindowsHardwareMetricSampler>.Instance);

    private static HardwareMetricSnapshot CreateCatalogSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return new HardwareMetricSnapshot(
            now,
            new CpuMetrics(
                "test",
                0,
                true,
                CpuMetricObservationStatus.Complete,
                1,
                1,
                1,
                1,
                100,
                "test",
                new CpuSensorMetrics(
                    new HardwareSensorProviderState("test", "Unavailable", null),
                    null,
                    null,
                    null,
                    null)),
            new MemoryMetrics(0, 0, 0, false, "test"),
            new VirtualMemoryMetrics(0, 0, 0, "test", false),
            [],
            new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                0,
                []),
            new Dictionary<string, MetricValue>());
    }
}
