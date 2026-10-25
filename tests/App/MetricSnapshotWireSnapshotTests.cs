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
        Assert.Equal("%", value.Unit);
        Assert.Equal(25, value.NumericValue);
        using var payload = JsonDocument.Parse(json);
        Assert.Equal((int)SamplingObservationStatus.Current,
            payload.RootElement.GetProperty("items").GetProperty("cpu.usage").GetProperty("status").GetInt32());
        Assert.DoesNotContain("displayValue", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("label", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("datasets", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hasValue", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failure", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("generation", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SamplingObservationStatus.Current)]
    [InlineData(SamplingObservationStatus.Unavailable)]
    public void CatalogRevisionIsAnOpaqueStringIndependentOfCurrentValue(SamplingObservationStatus status)
    {
        const string revision = "opaque-catalog-token";
        var wire = MetricSnapshotWireSnapshot.From(
            CreateSnapshot(DateTimeOffset.Parse("2026-08-23T16:00:00Z"), status),
            MetricSampleRequest.ForIds(["cpu.usage"]), revision);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(wire, WebJson));
        Assert.Equal(MetricSnapshotWireSnapshot.CurrentVersion, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(revision, json.RootElement.GetProperty("catalogRevision").GetString());
        Assert.Equal(status == SamplingObservationStatus.Current ? 25d : (double?)null,
            wire.Items["cpu.usage"].NumericValue);
    }

    [Fact]
    public void EmptyCurrentValueUsesTheSameItemShape()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-23T16:00:00Z");
        var attemptedAt = observedAt.AddSeconds(5);
        var wire = MetricSnapshotWireSnapshot.From(
            CreateSnapshot(
                attemptedAt,
                SamplingObservationStatus.Unavailable,
                observedAt,
                attemptedAt,
                "fixture-failure"),
            MetricSampleRequest.ForIds(["cpu.usage"]));

        Assert.Null(wire.CapturedAt);
        var value = Assert.Single(wire.Items).Value;
        Assert.Equal("cpu.usage", value.Id);
        Assert.Equal("%", value.Unit);
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
    public void SuccessfulEmptyCollectionReturnsTypedEmptyValue()
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
        Assert.Equal("%", value.Unit);
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
        var items = new Dictionary<string, MetricReadingValue>(
            StringComparer.OrdinalIgnoreCase);
        if (includeCpuPayload)
        {
            items["cpu.usage"] = new(Id: "cpu.usage", NumericValue: 25, Unit: "%", Percent: 25);
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
            items["memory.usage"] = new(Id: "memory.usage", NumericValue: 8, Unit: "GB", Percent: 25);
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
                        HardwareSensorProviderStates.Unavailable,
                        null),
                    null,
                    null,
                    null,
                    null)),
            new MemoryMetrics(UsedBytes: 8, TotalBytes: 32, UsagePercent: 25, IsUsageAvailable: true),
            new VirtualMemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false),
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
