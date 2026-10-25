using System.Diagnostics;
using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Endpoints.Transport;

namespace Resource_Manager_APP.Tests;

public sealed class BackendFrontendWireAcceptanceTests
{
    private const long JavaScriptMaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset CapturedAt =
        DateTimeOffset.Parse("2026-08-29T12:34:56.789Z");

    [Fact]
    public void ResidentSharedBytesSurviveCompactSerialization()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(CreateBreakdownWire(), WebJson));
        var row = json.RootElement.GetProperty("bars")[0].GetProperty("software")[0];
        Assert.Equal(4, row[6].GetDouble());
        Assert.Equal(4, row[5][0][11].GetDouble());
        Assert.Equal(12, row[1].GetDouble());
    }

    [Fact]
    public async Task MetricV5SerializerFeedsProductionDecoderWithRawEmptyValue()
    {
        var attemptedAt = CapturedAt.AddSeconds(5);
        var snapshot = new HardwareMetricSnapshot(
            attemptedAt,
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
            new Dictionary<string, MetricReadingValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["cpu.usage"] = new(Id: "cpu.usage", NumericValue: 25, Unit: "%", Percent: 25)
            })
        {
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(
                StringComparer.OrdinalIgnoreCase)
            {
                [SamplingDatasetIds.SystemCpuUsage] = new(
                    SamplingDatasetIds.SystemCpuUsage,
                    SamplingObservationStatus.RetainedLastGood,
                    1,
                    CapturedAt.UtcTicks,
                    1,
                    1,
                    1,
                    1)
                {
                    LastAttemptAtUtcTicks = attemptedAt.UtcTicks,
                    LastSuccessAtUtcTicks = CapturedAt.UtcTicks,
                    ReadyUntilUtcTicks = CapturedAt.AddSeconds(2).UtcTicks,
                    FailureCode = "fixture-refresh-failed"
                }
            },
            WorkspaceIdentity = 1,
            ConfigurationGeneration = 1,
            CatalogGeneration = 1,
            CommittedGeneration = 1
        };
        var wire = MetricSnapshotWireSnapshot.From(
            snapshot,
            MetricSampleRequest.ForIds(["cpu.usage"]));

        var result = await DecodeAsync("metric", wire);

        Assert.Equal(MetricSnapshotWireSnapshot.CurrentVersion, result.GetProperty("metricVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("metricCapturedAt").ValueKind);
        Assert.False(result.GetProperty("hasMetricDisplayText").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("metricNumericValue").ValueKind);
        Assert.False(result.GetProperty("hasMetricSamplingMetadata").GetBoolean());
    }

    [Fact]
    public async Task NeutralFactsSerializerFeedsProductionDecoderWithLosslessProcessIdentity()
    {
        var facts = ResourceFactsSnapshot.Create(ResourceFactsPublicationTests.Snapshot(), ["cpu.usage", "gpu.1.vram"]);
        var result = await DecodeAsync("facts", facts);
        Assert.Equal(long.MaxValue.ToString(), result.GetProperty("factsProcessStartKey").GetString());
        Assert.Equal(4, result.GetProperty("factsProcessSharedValue").GetDouble());
    }

    private static ResourceBreakdownWireSnapshot CreateBreakdownWire()
    {
        var process = new ResourceProcessSegment(
            4242,
            "example.exe",
            @"C:\Apps\example.exe",
            12,
            12,
            100)
        {
            ProcessStartKey = long.MaxValue,
            SharedValue = 4
        };
        var software = new ResourceSoftwareSegment(
            "software:test",
            "Test App",
            "other",
            "other",
            12,
            12,
            1,
            [process]) { SharedValue = 4 };
        var snapshot = new ResourceBreakdownSnapshot(
            CapturedAt,
            [
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.CpuUsage,
                    "CPU",
                    "%",
                    ResourceBreakdownScaleModes.Capacity,
                    12,
                    100,
                    12,
                    [software],
                    SamplingObservationStatus.Current,
                    SamplingObservationStatus.Current)
            ])
        {
            Sampling = ResourceBreakdownSamplingState.Ready(CapturedAt),
            Datasets =
            [
                new ResourceBreakdownDatasetSamplingState(
                    "process.cpu.usage",
                    ResourceBreakdownSamplingStatuses.Ready,
                    ulong.MaxValue,
                    JavaScriptMaximumSafeInteger,
                    CapturedAt,
                    CapturedAt,
                    CapturedAt,
                    CapturedAt.AddSeconds(5),
                    null,
                    null)
            ]
        };
        return ResourceBreakdownWireSnapshot.Create(
            snapshot,
            [ResourceBreakdownMetricIds.CpuUsage],
            new HashSet<string>(["software:test"], StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<JsonElement> DecodeAsync(string kind, object payload)
    {
        var repositoryRoot = TestRepositoryPaths.FindRoot();
        var clientRoot = Path.Combine(
            repositoryRoot,
            "src",
            "UI",
            "Frontend");
        var decoderPath = Path.Combine(clientRoot, "tests", "backendWireAcceptanceDecoder.ts");
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = clientRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--experimental-strip-types");
        startInfo.ArgumentList.Add(decoderPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the production TypeScript decoder.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(
            new { kind, payload },
            WebJson));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert.True(
            process.ExitCode == 0,
            $"Production TypeScript decoder exited {process.ExitCode}: {stderr}{Environment.NewLine}{stdout}");
        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.Clone();
    }

}
