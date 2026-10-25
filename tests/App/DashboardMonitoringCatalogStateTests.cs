using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
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
        Assert.Equal(first.CatalogGeneration, state.Catalog?.CatalogGeneration);
        Assert.Equal(0UL, state.Current!.CommittedGeneration);
        Assert.All(state.Current.Items.Values, value => Assert.Null(value.NumericValue));

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
    public async Task FailedRebuildWaitsForCatalogChangeAndLogsRepeatedFailureAtDebug()
    {
        var state = new DashboardMonitoringCatalogState();
        var source = new CatalogueSource(state);
        var coordinator = new RecordingRuntimeSpecializationCoordinator { Failure = "Plan rebuild failure." };
        var logger = new RecordingSynchronizerLogger();
        using var synchronizer = new DashboardMonitoringPlanSynchronizer(source, state,
            coordinator, logger);
        await synchronizer.StartAsync(CancellationToken.None);
        await source.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        source.Publish(CreateSnapshot(1, "gpu-dedicated", "gpu.0.usage"));
        await source.Released.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(LogLevel.Warning, await logger.Entries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, source.ActiveSubscriptions);
        await Task.Delay(DashboardMonitoringPlanSynchronizer.CatalogSubscriptionInterval + TimeSpan.FromMilliseconds(250));
        Assert.Single(coordinator.Reasons);
        source.Publish(CreateSnapshot(2, "gpu-dedicated", "gpu.0.usage"));
        Assert.Equal(LogLevel.Debug, await logger.Entries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        coordinator.Failure = "Different plan rebuild failure.";
        source.Publish(CreateSnapshot(3, "gpu-dedicated", "gpu.0.usage"));
        Assert.Equal(LogLevel.Warning, await logger.Entries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        coordinator.Failure = null;
        source.Publish(CreateSnapshot(4, "gpu-dedicated", "gpu.0.usage"));
        await coordinator.CatalogReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Failure = "Different plan rebuild failure.";
        source.Publish(CreateSnapshot(5, "gpu-dedicated", "gpu.0.usage"));
        Assert.Equal(LogLevel.Warning, await logger.Entries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(5, coordinator.Reasons.Count);
        Assert.All(coordinator.Reasons.Take(4), reason => Assert.Equal("monitoring-catalog-ready", reason));
        Assert.Equal("monitoring-catalog-changed", coordinator.Reasons[4]);
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
            static metricId => new MetricReadingValue(Id: metricId, NumericValue: 1, Unit: "1", Percent: null));
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
            new MemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsUsageAvailable: false),
            new VirtualMemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false),
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

        public string? Failure { get; set; }
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
            if (Failure is { } failure)
            {
                throw new IOException(failure);
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

    private sealed class RecordingSynchronizerLogger : ILogger<DashboardMonitoringPlanSynchronizer>
    {
        public Channel<LogLevel> Entries { get; } = Channel.CreateUnbounded<LogLevel>();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Writer.TryWrite(logLevel);
    }
}
