using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.ReadWrite;

namespace Resource_Manager_APP.Tests;

public sealed partial class DashboardMonitoringCatalogStateTests
{
    [Fact]
    public void Publish_OnlySignalsCatalogIdentityChanges()
    {
        var state = new DashboardMonitoringCatalogState();
        var changed = 0;
        state.Changed += () => changed++;

        var initialRevision = state.Revision;
        Assert.NotEqual(initialRevision, new DashboardMonitoringCatalogState().Revision);
        var first = CreateSnapshot(1, "gpu-dedicated", "gpu.0.usage");
        state.PublishCatalogProbe(first);
        var firstRevision = state.Revision;
        Assert.NotEqual(initialRevision, firstRevision);
        state.PublishCatalogProbe(first with
        {
            CapturedAt = first.CapturedAt.AddSeconds(1),
            CommittedGeneration = first.CommittedGeneration + 1
        });

        Assert.Equal(firstRevision, state.Revision);
        Assert.Equal(1, changed);
        Assert.Equal(first.CommittedGeneration + 1, state.Current?.CommittedGeneration);

        state.PublishCatalogProbe(CreateSnapshot(2, "gpu-dedicated", "gpu.0.usage", "gpu.0.vram"));
        Assert.Equal(2, changed);
        Assert.NotEqual(firstRevision, state.Revision);
    }

    [Fact]
    public void PublishCatalogProbe_SignalsEverySelectableMetricChange()
    {
        var state = new DashboardMonitoringCatalogState();
        var changed = 0;
        state.Changed += () => changed++;

        state.PublishCatalogProbe(CreateSnapshot(
            1,
            "gpu-dedicated",
            "gpu.0.usage"));
        state.PublishCatalogProbe(CreateSnapshot(
            1,
            "gpu-dedicated",
            "gpu.0.usage",
            "cpu.temperature"));

        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task SynchronizerReleasesBootstrapDemandAndRebuildsOnConsumerCatalogPublication()
    {
        var state = new DashboardMonitoringCatalogState();
        var source = new CatalogueSource(state);
        var coordinator = new RecordingRuntimeSpecializationCoordinator();
        using var synchronizer = new DashboardMonitoringPlanSynchronizer(source, state,
            coordinator, NullLogger<DashboardMonitoringPlanSynchronizer>.Instance);
        await synchronizer.StartAsync(CancellationToken.None);
        Assert.True((await source.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).IsCatalogProbe);
        var first = CreateSnapshot(1, "gpu-dedicated", "gpu.0.usage");
        source.Publish(first);
        await source.Released.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.CatalogReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, source.ActiveSubscriptions);
        source.Publish(first with { CapturedAt = first.CapturedAt.AddSeconds(1) });
        Assert.Single(coordinator.Reasons);
        source.RefreshSnapshot = CreateSnapshot(2, "gpu-dedicated", "gpu.0.usage", "gpu.0.vram");
        var backend = new HostReadWriteBackend(source, source, state, new EmptyControlCatalog(), null!);
        await backend.DiscoverAsync(CancellationToken.None);
        await coordinator.CatalogChanged.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, source.CaptureCount);
        Assert.Equal(2UL, state.Current!.CatalogGeneration);
        Assert.Equal(["monitoring-catalog-ready", "monitoring-catalog-changed"], coordinator.Reasons);
        Assert.False(source.Started.Reader.TryRead(out _));
        await synchronizer.StopAsync(CancellationToken.None);
        await synchronizer.StartAsync(CancellationToken.None);
        await synchronizer.StopAsync(CancellationToken.None);
        Assert.Equal(0, source.ActiveSubscriptions);
        Assert.False(source.Started.Reader.TryRead(out _));
    }

    [Fact]
    public async Task SynchronizerReacquiresFailedBootstrapAndReleasesAfterPublication()
    {
        var state = new DashboardMonitoringCatalogState();
        var source = new CatalogueSource(state) { FailSubscriptionOnce = true };
        var coordinator = new RecordingRuntimeSpecializationCoordinator();
        using var synchronizer = new DashboardMonitoringPlanSynchronizer(source, state,
            coordinator, NullLogger<DashboardMonitoringPlanSynchronizer>.Instance);
        await synchronizer.StartAsync(CancellationToken.None);
        await source.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await source.Released.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await source.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        source.Publish(CreateSnapshot(1, "gpu-dedicated", "gpu.0.usage"));
        await source.Released.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.CatalogReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, source.ActiveSubscriptions);
        await synchronizer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailedInitialRebuildRetriesWithoutKeepingSamplingDemand()
    {
        var state = new DashboardMonitoringCatalogState();
        var source = new CatalogueSource(state);
        var coordinator = new RecordingRuntimeSpecializationCoordinator { FailOnce = true };
        using var synchronizer = new DashboardMonitoringPlanSynchronizer(source, state,
            coordinator, NullLogger<DashboardMonitoringPlanSynchronizer>.Instance);
        await synchronizer.StartAsync(CancellationToken.None);
        await source.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        source.Publish(CreateSnapshot(1, "gpu-dedicated", "gpu.0.usage"));
        await source.Released.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, source.ActiveSubscriptions);
        await coordinator.CatalogReady.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(["monitoring-catalog-ready", "monitoring-catalog-ready"], coordinator.Reasons);
        Assert.False(source.Started.Reader.TryRead(out _));
        await synchronizer.StopAsync(CancellationToken.None);
    }

    private static HardwareMetricSnapshot CreateSnapshot(
        ulong catalogGeneration,
        string gpuIdentity,
        params string[] gpuMetricIds)
    {
        var items = gpuMetricIds.ToDictionary(
            static metricId => metricId,
            static metricId => new MetricValue(
                metricId,
                metricId,
                "GPU",
                metricId.EndsWith("vram", StringComparison.Ordinal) ? "B" : "%",
                1,
                "1",
                null,
                ""));
        return new HardwareMetricSnapshot(
            DateTimeOffset.UtcNow,
            new CpuMetrics(
                "CPU",
                0,
                false,
                CpuMetricObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                "",
                new CpuSensorMetrics(
                    new HardwareSensorProviderState("", "", null),
                    null,
                    null,
                    null,
                    null)),
            new MemoryMetrics(0, 0, 0, false, ""),
            new VirtualMemoryMetrics(0, 0, 0, "", false),
            [
                new GpuMetrics(
                    0,
                    "GPU",
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    new GpuSensorMetrics(
                        new HardwareSensorProviderState("", "", null),
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null),
                    IdentityKey: gpuIdentity)
            ],
            new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                0,
                []),
            items)
        {
            WorkspaceIdentity = 1,
            ConfigurationGeneration = 1,
            CatalogGeneration = catalogGeneration,
            CommittedGeneration = catalogGeneration
        };
    }

    private sealed class EmptyControlCatalog : IControlObjectCatalog
    {
        public ControlObjectCatalog ReadObjects() => new([], DateTimeOffset.UnixEpoch);
    }

    private sealed class CatalogueSource(DashboardMonitoringCatalogState state)
        : IMetricSnapshotPushSource, IMetricSampler, IMetricSnapshotObservationSource
    {
        private readonly Channel<HardwareMetricSnapshot> updates = Channel.CreateUnbounded<HardwareMetricSnapshot>();
        public Channel<MetricSampleRequest> Started { get; } = Channel.CreateUnbounded<MetricSampleRequest>();
        public Channel<bool> Released { get; } = Channel.CreateUnbounded<bool>();
        public int ActiveSubscriptions { get; private set; }
        public bool FailSubscriptionOnce { get; set; }
        public HardwareMetricSnapshot? RefreshSnapshot { get; set; }
        public int CaptureCount { get; private set; }
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(MetricSampleRequest request, CancellationToken token)
            => throw new NotSupportedException();
        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(MetricSampleRequest request, CancellationToken token)
        {
            Assert.True(request.IsCatalogProbe);
            token.ThrowIfCancellationRequested();
            CaptureCount++;
            return Task.FromResult(RefreshSnapshot!);
        }
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => throw new NotSupportedException();
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval)
            => throw new NotSupportedException();
        public void Publish(HardwareMetricSnapshot snapshot)
        {
            state.PublishCatalogProbe(snapshot);
            Assert.True(updates.Writer.TryWrite(snapshot));
        }
        public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(string id, MetricSampleRequest request,
            TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Assert.Equal(DashboardMonitoringPlanSynchronizer.CatalogSubscriptionInterval, interval);
            ActiveSubscriptions++;
            Started.Writer.TryWrite(request);
            try
            {
                if (FailSubscriptionOnce)
                {
                    FailSubscriptionOnce = false;
                    throw new IOException("Transient subscription acquisition failure.");
                }
                await foreach (var snapshot in updates.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return snapshot;
                }
            }
            finally { ActiveSubscriptions--; Released.Writer.TryWrite(true); }
        }
    }

    private sealed class RecordingRuntimeSpecializationCoordinator
        : IRuntimeSpecializationCoordinator
    {
        public Task<ResourceManager.App.Domain.CpuTopology.CpuBaselineRatioUpdateResult> ApplyCpuBaselineRatioAsync(
            double? overrideRatio, CancellationToken cancellationToken) => throw new NotSupportedException();

        public bool FailOnce { get; set; }
        private readonly List<string> reasons = [];

        public IReadOnlyList<string> Reasons
        {
            get
            {
                lock (reasons)
                {
                    return reasons.ToArray();
                }
            }
        }

        public TaskCompletionSource<string> CatalogChanged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> CatalogReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RuntimePlanPublicationResult> RebuildAsync(
            string reason,
            CancellationToken cancellationToken)
        {
            lock (reasons)
            {
                reasons.Add(reason);
            }
            if (FailOnce)
            {
                FailOnce = false;
                throw new IOException("Transient plan rebuild failure.");
            }
            if (reason == "monitoring-catalog-changed")
            {
                CatalogChanged.TrySetResult(reason);
            }
            if (reason == "monitoring-catalog-ready")
            {
                CatalogReady.TrySetResult(reason);
            }
            return Task.FromResult<RuntimePlanPublicationResult>(null!);
        }

        public Task<AppSettingsUpdateResult> ApplyAppSettingsAsync(
            AppSettings settings,
            string reason,
            CancellationToken cancellationToken) =>
            Task.FromResult<AppSettingsUpdateResult>(null!);

        public Task<AppSettingsUpdateResult> ApplyAppSettingsPatchAsync(
            AppSettingsPatchRequest patch,
            string reason,
            CancellationToken cancellationToken) =>
            Task.FromResult<AppSettingsUpdateResult>(null!);
    }
}
