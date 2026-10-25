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

public sealed partial class HardwareMetricCatalogStateTests
{
    [Fact]
    public void Publish_OnlySignalsCatalogIdentityChanges()
    {
        var state = new HardwareMetricCatalogState();
        var changed = 0;
        state.Changed += () => changed++;

        var initialRevision = state.Revision;
        Assert.NotEqual(initialRevision, new HardwareMetricCatalogState().Revision);
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
        var state = new HardwareMetricCatalogState();
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

}
