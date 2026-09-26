using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Software;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;

namespace Resource_Manager_APP.Tests;

public sealed class ResourceMonitorProjectionTests
{
    [Fact]
    public void WireSnapshot_EmptyValueHasNoSamplingStateOrFakeCaptureTime()
    {
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.UtcNow, [])
        {
            Sampling = new ResourceBreakdownSamplingState(
                ResourceBreakdownSamplingStatuses.Warming,
                7,
                null,
                null,
                null,
                null),
            Datasets =
            [
                new ResourceBreakdownDatasetSamplingState(
                    SamplingDatasetIds.ProcessCpuUsage,
                    ResourceBreakdownSamplingStatuses.Warming,
                    0,
                    7,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null)
            ]
        };

        var json = JsonSerializer.Serialize(
            ResourceBreakdownWireSnapshot.Create(
                snapshot,
                [ResourceBreakdownMetricIds.CpuUsage]));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(
            ResourceBreakdownWireSnapshot.CurrentVersion,
            root.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("capturedAt").ValueKind);
        Assert.Empty(root.GetProperty("bars").EnumerateArray());
        Assert.False(root.TryGetProperty("sampling", out _));
        Assert.False(root.TryGetProperty("datasets", out _));
        Assert.DoesNotContain("stateRevision", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("generation", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WireSnapshot_EmptyDatasetDoesNotPublishABarOrFailureState()
    {
        var snapshot = new ResourceBreakdownSnapshot(
            DateTimeOffset.UnixEpoch,
            [
                new ResourceBreakdownBar(
                    "gpu.0.usage",
                    "GPU0 占用率",
                    "%",
                    ResourceBreakdownScaleModes.Capacity,
                    null,
                    null,
                    null,
                    [],
                    SamplingObservationStatus.Unavailable,
                    SamplingObservationStatus.NotRequested)
            ])
        {
            Datasets =
            [
                new ResourceBreakdownDatasetSamplingState(
                    SamplingDatasetIds.ForProcessMetric("gpu.0.usage"),
                    ResourceBreakdownSamplingStatuses.Failed,
                    1,
                    2,
                    null,
                    DateTimeOffset.UnixEpoch,
                    null,
                    null,
                    "fixture-failure",
                    "fixture failure")
            ]
        };

        var json = JsonSerializer.Serialize(
            ResourceBreakdownWireSnapshot.Create(snapshot, ["gpu.0.usage"]));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Empty(root.GetProperty("bars").EnumerateArray());
        Assert.False(root.TryGetProperty("sampling", out _));
        Assert.False(root.TryGetProperty("datasets", out _));
        Assert.DoesNotContain("failure", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WireSnapshot_ScopesCurrentValuesToVisibleBars()
    {
        var cpuCapturedAt = DateTimeOffset.Parse("2026-08-29T10:00:00Z");
        var memoryCapturedAt = cpuCapturedAt.AddSeconds(-5);
        var memoryAttemptedAt = cpuCapturedAt.AddSeconds(1);
        var snapshot = new ResourceBreakdownSnapshot(
            cpuCapturedAt,
            [
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.CpuUsage,
                    "CPU 占用率",
                    "%",
                    ResourceBreakdownScaleModes.Capacity,
                    25,
                    100,
                    25,
                    []),
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.MemoryUsage,
                    "内存占用",
                    "B",
                    ResourceBreakdownScaleModes.Capacity,
                    64,
                    1024,
                    6.25,
                    [])
            ])
        {
            Sampling = new ResourceBreakdownSamplingState(
                ResourceBreakdownSamplingStatuses.Stale,
                12,
                memoryAttemptedAt,
                cpuCapturedAt,
                "process-memory-failed",
                "进程内存采样失败。"),
            Datasets =
            [
                new ResourceBreakdownDatasetSamplingState(
                    SamplingDatasetIds.ProcessCpuUsage,
                    ResourceBreakdownSamplingStatuses.Ready,
                    3,
                    4,
                    cpuCapturedAt,
                    cpuCapturedAt,
                    cpuCapturedAt,
                    cpuCapturedAt.AddSeconds(6),
                    null,
                    null),
                new ResourceBreakdownDatasetSamplingState(
                    SamplingDatasetIds.ProcessMemoryUsage,
                    ResourceBreakdownSamplingStatuses.Stale,
                    7,
                    9,
                    memoryCapturedAt,
                    memoryAttemptedAt,
                    memoryCapturedAt,
                    memoryCapturedAt.AddSeconds(6),
                    "process-memory-failed",
                    "进程内存采样失败。")
            ]
        };

        var json = JsonSerializer.Serialize(
            ResourceBreakdownWireSnapshot.Create(
                snapshot,
                [ResourceBreakdownMetricIds.CpuUsage]));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(cpuCapturedAt, root.GetProperty("capturedAt").GetDateTimeOffset());
        var bar = Assert.Single(root.GetProperty("bars").EnumerateArray());
        Assert.Equal(
            ResourceBreakdownMetricIds.CpuUsage,
            bar.GetProperty("metricId").GetString());
        Assert.False(root.TryGetProperty("sampling", out _));
        Assert.False(root.TryGetProperty("datasets", out _));
        Assert.DoesNotContain("stateRevision", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("generation", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WireSnapshot_DefaultProjectionRemovesAllProcessDetails()
    {
        var json = JsonSerializer.Serialize(
            ResourceBreakdownWireSnapshot.Create(
                Snapshot(),
                [ResourceBreakdownMetricIds.MemoryUsage],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);

        Assert.Single(document.RootElement.GetProperty("bars").EnumerateArray());
        Assert.DoesNotContain("worker-a.exe", json, StringComparison.Ordinal);
        Assert.DoesNotContain("worker-b.exe", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WireSnapshot_OnlyKeepsRequestedSoftwareProcessDetails()
    {
        var json = JsonSerializer.Serialize(
            ResourceBreakdownWireSnapshot.Create(
                Snapshot(),
                [ResourceBreakdownMetricIds.MemoryUsage],
                new HashSet<string>(["software:b"], StringComparer.OrdinalIgnoreCase)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("worker-a.exe", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var catalog = root.GetProperty("softwareCatalog");
        var softwareBIdentity = catalog[1];
        Assert.Equal(4, softwareBIdentity.GetArrayLength());
        Assert.Equal("software:b", softwareBIdentity[0].GetString());
        Assert.Equal("App B", softwareBIdentity[1].GetString());
        Assert.Equal("Other", softwareBIdentity[2].GetString());
        Assert.Equal(SoftwareDisplayKinds.General, softwareBIdentity[3].GetString());

        // 元组里不再有展示串：条形自带 unit，数值由前端按用户选的进制格式化。
        var softwareBValue = root.GetProperty("bars")[0].GetProperty("software")[1];
        Assert.Equal(6, softwareBValue.GetArrayLength());
        Assert.Equal(1, softwareBValue[0].GetInt32());
        Assert.Equal(1, softwareBValue[1].GetDouble());
        Assert.Equal(1, softwareBValue[2].GetDouble());
        Assert.Equal(1, softwareBValue[3].GetInt32());
        Assert.Equal(0, softwareBValue[4].GetDouble());

        var process = Assert.Single(softwareBValue[5].EnumerateArray());
        Assert.Equal(11, process.GetArrayLength());
        Assert.Equal(2002, process[0].GetInt32());
        Assert.Equal("worker-b.exe", process[1].GetString());
        Assert.Equal(@"C:\Apps\worker-b.exe", process[2].GetString());
        Assert.Equal(ResourceProcessAttributionKinds.Process, process[8].GetString());
        Assert.Equal(0, process[9].GetDouble());
        Assert.Equal("2002000", process[10].GetString());
    }

    [Fact]
    public void WireSnapshot_UsesOneIdentityCatalogAndPositionalValueRows()
    {
        var json = JsonSerializer.Serialize(
            ResourceBreakdownWireSnapshot.Create(
                Snapshot(),
                [ResourceBreakdownMetricIds.MemoryUsage],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(ResourceBreakdownWireSnapshot.CurrentVersion, root.GetProperty("version").GetInt32());
        Assert.Equal(2, root.GetProperty("softwareCatalog").GetArrayLength());
        var softwareRows = root.GetProperty("bars")[0].GetProperty("software");
        Assert.Equal(2, softwareRows.GetArrayLength());
        Assert.Equal(JsonValueKind.Array, softwareRows[0].ValueKind);
        Assert.DoesNotContain("worker-a.exe", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WireSnapshot_DefaultScaleStaysUnderPayloadBudget()
    {
        var software = Enumerable.Range(0, 160)
            .Select(index => new ResourceSoftwareSegment(
                $"software:{index:D3}",
                $"Software {index:D3}",
                "Other",
                SoftwareDisplayKinds.General,
                index + 1,
                (index + 1) / 10d,
                1,
                []))
            .ToArray();
        var bars = Enumerable.Range(0, 6)
            .Select(barIndex => new ResourceBreakdownBar(
                $"metric.{barIndex}",
                $"Metric {barIndex}",
                "B",
                ResourceBreakdownScaleModes.Capacity,
                100,
                1000,
                10,
                Enumerable.Range(0, 64)
                    .Select(index => software[(barIndex * 31 + index) % software.Length])
                    .ToArray()))
            .ToArray();
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.UnixEpoch, bars)
        {
            Datasets = bars
                .Select(static bar => CurrentDataset(
                    bar.MetricId,
                    DateTimeOffset.UnixEpoch))
                .ToArray()
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var domainBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(snapshot, options));
        var wireBytes = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(
                ResourceBreakdownWireSnapshot.Create(
                    snapshot,
                    bars.Select(static bar => bar.MetricId).ToArray(),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                options));

        Assert.True(wireBytes < 64 * 1024, $"Wire payload was {wireBytes} bytes.");
        Assert.True(wireBytes < domainBytes * 0.6, $"Wire payload {wireBytes} did not materially reduce {domainBytes}.");
    }

    private static ResourceBreakdownSnapshot Snapshot()
    {
        return new ResourceBreakdownSnapshot(
            DateTimeOffset.UnixEpoch,
            [
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.MemoryUsage,
                    "内存占用",
                    "B",
                    ResourceBreakdownScaleModes.Capacity,
                    3,
                    100,
                    3,
                    [
                        Software("software:a", "App A", 1001, "worker-a.exe"),
                        Software("software:b", "App B", 2002, "worker-b.exe")
                    ]),
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.CpuUsage,
                    "CPU 占用率",
                    "%",
                    ResourceBreakdownScaleModes.Capacity,
                    5,
                    100,
                    5,
                    [Software("software:a", "App A", 1001, "worker-a.exe")])
            ])
        {
            Datasets =
            [
                CurrentDataset(
                    ResourceBreakdownMetricIds.MemoryUsage,
                    DateTimeOffset.UnixEpoch),
                CurrentDataset(
                    ResourceBreakdownMetricIds.CpuUsage,
                    DateTimeOffset.UnixEpoch)
            ]
        };
    }

    private static ResourceSoftwareSegment Software(string id, string name, int processId, string processName)
    {
        var process = new ResourceProcessSegment(
            processId,
            processName,
            $@"C:\Apps\{processName}",
            1,
            1,
            100)
        {
            ProcessStartKey = checked(processId * 1000L)
        };
        return new ResourceSoftwareSegment(id, name, "Other", SoftwareDisplayKinds.General, 1, 1, 1, [process]);
    }

    private static ResourceBreakdownDatasetSamplingState CurrentDataset(
        string metricId,
        DateTimeOffset capturedAt)
        => new(
            SamplingDatasetIds.ForProcessMetric(metricId),
            ResourceBreakdownSamplingStatuses.Ready,
            1,
            1,
            capturedAt,
            capturedAt,
            capturedAt,
            null,
            null,
            null);
}
