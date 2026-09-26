using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class SystemMemoryUsageDependencyTests
{
    [Fact]
    public void CurrentHardwareDatasetProducesExactDependency()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(now, SamplingObservationStatus.Current);

        Assert.True(SystemMemoryUsageDependency.TryCreate(
            snapshot,
            now,
            out var dependency));
        Assert.Equal(SamplingDatasetIds.SystemMemoryUsage, dependency.DatasetId);
        Assert.Equal(16UL * 1024 * 1024 * 1024, dependency.DenominatorBytes);
        Assert.Equal(11UL, dependency.SourceGeneration);
        Assert.Equal(now.UtcTicks, dependency.ObservedAtUtcTicks);
        Assert.Equal(7UL, dependency.WorkspaceIdentity);
        Assert.Equal(8UL, dependency.ConfigurationGeneration);
        Assert.Equal(9UL, dependency.CatalogGeneration);
        Assert.Equal(10UL, dependency.CommittedGeneration);
        Assert.Equal(0, dependency.ReadyUntilUtcTicks);
    }

    [Fact]
    public void RetainedCapacityIsEmptyButDeadlinesDoNotExpireCurrentValues()
    {
        var now = DateTimeOffset.UtcNow;
        var retained = Snapshot(now, SamplingObservationStatus.RetainedLastGood);
        var expired = Snapshot(now, SamplingObservationStatus.Current) with
        {
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(
                StringComparer.OrdinalIgnoreCase)
            {
                [SamplingDatasetIds.SystemMemoryUsage] =
                    Observation(
                        now,
                        SamplingObservationStatus.Current) with
                    {
                        ReadyUntilUtcTicks = now.AddTicks(-1).UtcTicks
                    }
            }
        };

        Assert.False(SystemMemoryUsageDependency.TryCreate(
            retained,
            now,
            out _));
        Assert.True(SystemMemoryUsageDependency.TryCreate(
            expired,
            now,
            out _));
    }

    [Fact]
    public void CurrentProcessMemoryObservationCannotBeCreatedWithoutDependency()
    {
        Assert.Throws<ArgumentException>(() =>
            SchedulingProcessDatasetObservation.CreateCurrent(
                SchedulingProcessMetricMask.MemoryUsage,
                1,
                1,
                1,
                1));
    }

    private static HardwareMetricSnapshot Snapshot(
        DateTimeOffset now,
        SamplingObservationStatus status)
        => new(
            now,
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
                    new HardwareSensorProviderState("test", "Unavailable", null),
                    null,
                    null,
                    null,
                    null)),
            new MemoryMetrics(
                8UL * 1024 * 1024 * 1024,
                16UL * 1024 * 1024 * 1024,
                50,
                true,
                "test")
            {
                ObservationStatus = status
            },
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
            new Dictionary<string, MetricValue>())
        {
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(
                StringComparer.OrdinalIgnoreCase)
            {
                [SamplingDatasetIds.SystemMemoryUsage] = Observation(now, status)
            },
            WorkspaceIdentity = 7,
            ConfigurationGeneration = 8,
            CatalogGeneration = 9,
            CommittedGeneration = 10
        };

    private static HardwareMetricDatasetObservation Observation(
        DateTimeOffset now,
        SamplingObservationStatus status)
        => new(
            SamplingDatasetIds.SystemMemoryUsage,
            status,
            11,
            now.UtcTicks,
            7,
            8,
            9,
            10)
        {
            LastAttemptAtUtcTicks = now.UtcTicks,
            LastSuccessAtUtcTicks = now.UtcTicks,
            ReadyUntilUtcTicks = now.AddSeconds(7).UtcTicks,
            FailureCode = status == SamplingObservationStatus.Current
                ? null
                : "fixture-retained"
        };
}
