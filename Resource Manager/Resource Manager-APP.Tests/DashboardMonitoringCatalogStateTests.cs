using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class DashboardMonitoringCatalogStateTests
{
    [Fact]
    public void Publish_OnlySignalsCatalogIdentityChanges()
    {
        var state = new DashboardMonitoringCatalogState();
        var changed = 0;
        state.Changed += () => changed++;

        var first = CreateSnapshot(1, "gpu-dedicated", "gpu.0.usage");
        state.PublishCatalogProbe(first);
        state.PublishCatalogProbe(first with
        {
            CapturedAt = first.CapturedAt.AddSeconds(1),
            CommittedGeneration = first.CommittedGeneration + 1
        });

        Assert.Equal(1, changed);
        Assert.Equal(first.CommittedGeneration + 1, state.Current?.CommittedGeneration);

        state.PublishCatalogProbe(CreateSnapshot(2, "gpu-dedicated", "gpu.0.usage", "gpu.0.vram"));
        Assert.Equal(2, changed);
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
    public async Task Synchronizer_AwaitsInitialRebuildAndRebuildsOnLaterCatalogChange()
    {
        var state = new DashboardMonitoringCatalogState();
        var sampler = new PublishingMetricSampler(
            state,
            CreateSnapshot(1, "gpu-dedicated", "gpu.0.usage"));
        var coordinator = new RecordingRuntimeSpecializationCoordinator();
        using var synchronizer = new DashboardMonitoringPlanSynchronizer(
            sampler,
            state,
            coordinator,
            NullLogger<DashboardMonitoringPlanSynchronizer>.Instance);

        await synchronizer.StartAsync(CancellationToken.None);

        Assert.Equal(["monitoring-catalog-ready"], coordinator.Reasons);
        state.PublishCatalogProbe(CreateSnapshot(2, "gpu-dedicated", "gpu.0.usage", "gpu.0.vram"));
        var changedReason = await coordinator.CatalogChanged.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("monitoring-catalog-changed", changedReason);

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

    private sealed class PublishingMetricSampler(
        DashboardMonitoringCatalogState state,
        HardwareMetricSnapshot snapshot) : IMetricSampler
    {
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            GetSnapshotAsync(MetricSampleRequest.CatalogProbe, cancellationToken);

        public Task<HardwareMetricSnapshot> GetSnapshotAsync(
            MetricSampleRequest request,
            CancellationToken cancellationToken)
        {
            state.PublishCatalogProbe(snapshot);
            return Task.FromResult(snapshot);
        }

        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(
            MetricSampleRequest request,
            CancellationToken cancellationToken) =>
            GetSnapshotAsync(request, cancellationToken);
    }

    private sealed class RecordingRuntimeSpecializationCoordinator
        : IRuntimeSpecializationCoordinator
    {
        public Task<ResourceManager.App.Domain.CpuTopology.CpuBaselineRatioUpdateResult> ApplyCpuBaselineRatioAsync(
            double? overrideRatio, CancellationToken cancellationToken) => throw new NotSupportedException();

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

        public Task<RuntimePlanPublicationResult> RebuildAsync(
            string reason,
            CancellationToken cancellationToken)
        {
            lock (reasons)
            {
                reasons.Add(reason);
            }
            if (reason == "monitoring-catalog-changed")
            {
                CatalogChanged.TrySetResult(reason);
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
