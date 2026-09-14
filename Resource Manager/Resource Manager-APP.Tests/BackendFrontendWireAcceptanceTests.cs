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
    public async Task ProductionSerializerFeedsProductionDecodersWithoutInternalSamplingMetadata()
    {
        var breakdown = CreateBreakdownWire();
        var table = ResourceTableWireSnapshot.Create(CreateTable());
        var monitor = new ResourceMonitorWireSnapshot(
            ResourceMonitorWireSnapshot.CurrentVersion,
            CapturedAt,
            breakdown,
            table);

        var monitorResult = await DecodeAsync("monitor", monitor);
        Assert.Equal(8, monitorResult.GetProperty("monitorVersion").GetInt32());
        Assert.Equal(long.MaxValue.ToString(), monitorResult.GetProperty("tableProcessStartKey").GetString());
        Assert.Equal(long.MaxValue.ToString(), monitorResult.GetProperty("breakdownProcessStartKey").GetString());
        Assert.False(monitorResult.GetProperty("hasTableSamplingMetadata").GetBoolean());
        Assert.False(monitorResult.GetProperty("hasBreakdownSamplingMetadata").GetBoolean());
        Assert.False(monitorResult.GetProperty("hasProviderStates").GetBoolean());

        var tableResult = await DecodeAsync("table", table);
        Assert.Equal(long.MaxValue.ToString(), tableResult.GetProperty("tableProcessStartKey").GetString());
        Assert.False(tableResult.GetProperty("hasTableSamplingMetadata").GetBoolean());
        Assert.False(tableResult.GetProperty("hasProviderStates").GetBoolean());
    }

    [Fact]
    public async Task MetricV4SerializerFeedsProductionDecoderWithEmptyValue()
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
            new Dictionary<string, MetricValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["cpu.usage"] = new(
                    "cpu.usage",
                    "CPU",
                    "CPU",
                    "25%",
                    25,
                    "%",
                    25,
                    null)
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

        Assert.Equal(4, result.GetProperty("metricVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("metricCapturedAt").ValueKind);
        Assert.Equal("-", result.GetProperty("metricDisplayValue").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("metricNumericValue").ValueKind);
        Assert.False(result.GetProperty("hasMetricSamplingMetadata").GetBoolean());
    }

    private static ResourceBreakdownWireSnapshot CreateBreakdownWire()
    {
        var process = new ResourceProcessSegment(
            4242,
            "example.exe",
            @"C:\Apps\example.exe",
            12,
            12,
            100,
            "12%")
        {
            ProcessStartKey = long.MaxValue
        };
        var software = new ResourceSoftwareSegment(
            "software:test",
            "Test App",
            "Other",
            "普通软件",
            12,
            12,
            "12%",
            1,
            [process]);
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
                    "12%",
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

    private static ResourceTableSnapshot CreateTable()
    {
        var row = new ResourceTableRow(
            "process:4242",
            null,
            0,
            ResourceTableRowKinds.Process,
            "example.exe",
            "running",
            "software:test",
            4242,
            1,
            12,
            new Dictionary<string, ResourceTableValue>
            {
                [ResourceTableColumnIds.Cpu] = new(12, 12, "12%", "%", "current", 12)
            },
            new Dictionary<string, double>())
        {
            SoftwareName = "Test App",
            ProcessIds = [4242],
            ProcessNames = ["example.exe"],
            ExecutablePaths = [@"C:\Apps\example.exe"],
            ProcessStartKey = long.MaxValue.ToString()
        };
        return new ResourceTableSnapshot(
            CapturedAt,
            ResourceTableProjectionStatuses.Ready,
            [
                new ResourceTableDatasetInput(
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
            ],
            [ResourceTableColumnCatalog.KnownColumns().Single(column => column.Id == ResourceTableColumnIds.Cpu)],
            [row],
            [],
            new ResourceTableSort(ResourceTableColumnIds.Cpu, "desc"),
            ResourceTableViewModes.Process);
    }

    private static async Task<JsonElement> DecodeAsync(string kind, object payload)
    {
        var repositoryRoot = FindRepositoryRoot();
        var clientRoot = Path.Combine(
            repositoryRoot,
            "Resource Manager",
            "Resource Manager-APP",
            "ClientApp");
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

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Resource Manager", "Resource Manager-APP")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Resource Manager repository root was not found.");
    }
}
