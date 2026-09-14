using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Endpoints.Transport;

namespace ResourceManager.App.Tests;

public sealed class MetricSnapshotWireSnapshotTests
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void CurrentWireContainsOnlyCurrentValues()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-23T16:00:00Z");
        var wire = MetricSnapshotWireSnapshot.From(
            CreateSnapshot(observedAt, SamplingObservationStatus.Current),
            MetricSampleRequest.ForIds(["cpu.usage"]));
        var json = JsonSerializer.Serialize(wire, WebJson);

        Assert.Equal(MetricSnapshotWireSnapshot.CurrentVersion, wire.Version);
        Assert.Equal(observedAt, wire.CapturedAt);
        var value = Assert.Single(wire.Items).Value;
        Assert.Equal("25%", value.DisplayValue);
        Assert.Equal(25, value.NumericValue);
        Assert.DoesNotContain("status", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("datasets", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hasValue", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failure", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("generation", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyCurrentValueUsesTheSameItemShape()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-23T16:00:00Z");
        var attemptedAt = observedAt.AddSeconds(5);
        var wire = MetricSnapshotWireSnapshot.From(
            CreateSnapshot(
                attemptedAt,
                SamplingObservationStatus.RetainedLastGood,
                observedAt,
                attemptedAt,
                "fixture-failure"),
            MetricSampleRequest.ForIds(["cpu.usage"]));

        Assert.Null(wire.CapturedAt);
        var value = Assert.Single(wire.Items).Value;
        Assert.Equal("cpu.usage", value.Id);
        Assert.Equal("-", value.DisplayValue);
        Assert.Null(value.NumericValue);
        Assert.Null(value.Percent);
    }

    [Fact]
    public void ExplicitQueryReturnsOnlyRequestedValues()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-23T16:00:00Z");
        var wire = MetricSnapshotWireSnapshot.From(
            CreateSnapshot(
                observedAt,
                SamplingObservationStatus.Current,
                includeMemory: true),
            MetricSampleRequest.ForIds(["cpu.usage"]));

        Assert.Equal(["cpu.usage"], wire.Items.Keys);
    }

    [Fact]
    public void CaseInsensitiveQueryReturnsCanonicalMetricIdentity()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-23T16:00:00Z");
        var wire = MetricSnapshotWireSnapshot.From(
            CreateSnapshot(observedAt, SamplingObservationStatus.Current),
            MetricSampleRequest.ForIds(["CPU.usage"]));

        var item = Assert.Single(wire.Items);
        Assert.Equal("cpu.usage", item.Key);
        Assert.Equal("cpu.usage", item.Value.Id);
    }

    [Fact]
    public void SuccessfulEmptyCollectionStillReturnsDisplayablePlaceholder()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-23T16:00:00Z");
        var wire = MetricSnapshotWireSnapshot.From(
            CreateSnapshot(
                observedAt,
                SamplingObservationStatus.Current,
                includeCpuPayload: false),
            MetricSampleRequest.ForIds(["cpu.usage"]));

        Assert.Equal(observedAt, wire.CapturedAt);
        var value = Assert.Single(wire.Items).Value;
        Assert.Equal("-", value.DisplayValue);
        Assert.Null(value.NumericValue);
    }

    private static HardwareMetricSnapshot CreateSnapshot(
        DateTimeOffset capturedAt,
        SamplingObservationStatus status,
        DateTimeOffset? observedAt = null,
        DateTimeOffset? attemptedAt = null,
        string? failureCode = null,
        bool includeCpuPayload = true,
        bool includeMemory = false)
    {
        observedAt ??= capturedAt;
        attemptedAt ??= capturedAt;
        var items = new Dictionary<string, MetricValue>(
            StringComparer.OrdinalIgnoreCase);
        if (includeCpuPayload)
        {
            items["cpu.usage"] = new(
                "cpu.usage", "CPU", "CPU", "25%", 25, "%", 25, null);
        }
        var datasets = new Dictionary<string, HardwareMetricDatasetObservation>(
            StringComparer.OrdinalIgnoreCase)
        {
            [SamplingDatasetIds.SystemCpuUsage] = new(
                SamplingDatasetIds.SystemCpuUsage,
                status,
                1,
                observedAt.Value.UtcTicks,
                1,
                1,
                1,
                1)
            {
                LastAttemptAtUtcTicks = attemptedAt.Value.UtcTicks,
                LastSuccessAtUtcTicks = observedAt.Value.UtcTicks,
                ReadyUntilUtcTicks = observedAt.Value.AddSeconds(10).UtcTicks,
                FailureCode = failureCode
            }
        };
        if (includeMemory)
        {
            items["memory.usage"] = new(
                "memory.usage", "Memory", "Memory", "8 GB", 8, "GB", 25, null);
            datasets[SamplingDatasetIds.SystemMemoryUsage] = new(
                SamplingDatasetIds.SystemMemoryUsage,
                SamplingObservationStatus.Current,
                1,
                observedAt.Value.UtcTicks,
                1,
                1,
                1,
                1);
        }
        return new HardwareMetricSnapshot(
            capturedAt,
            new CpuMetrics(
                "CPU",
                25,
                true,
                CpuMetricObservationStatus.Complete,
                1,
                1,
                3_000,
                5_000,
                60,
                "fixture",
                new CpuSensorMetrics(
                    new HardwareSensorProviderState(
                        "fixture",
                        "Unavailable",
                        null),
                    null,
                    null,
                    null,
                    null)),
            new MemoryMetrics(8, 32, 25, true, "fixture"),
            new VirtualMemoryMetrics(0, 0, 0, "fixture", false),
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
            items)
        {
            Datasets = datasets,
            WorkspaceIdentity = 1,
            ConfigurationGeneration = 1,
            CatalogGeneration = 1,
            CommittedGeneration = 1
        };
    }
}
