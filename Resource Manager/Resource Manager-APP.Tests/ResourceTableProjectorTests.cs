using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Infrastructure.ResourceTable;

namespace Resource_Manager_APP.Tests;

public sealed class ResourceTableProjectorTests
{
    [Theory]
    [InlineData("disk.io", "disk.read", "disk.write", "disk")]
    [InlineData("network.traffic", "network.receive", "network.send", "network")]
    public void DirectionalBarsDoNotDoubleCountCombinedTableColumn(
        string total, string incoming, string outgoing, string column)
    {
        ResourceBreakdownBar Bar(string id, double value) => new(
            id, id, "B/s", ResourceBreakdownScaleModes.Capacity, value, 1000, value / 10,
            [new ResourceSoftwareSegment("app", "App", "Other", "Other", value, value / 10, 1,
                [new ResourceProcessSegment(42, "app", null, value, value / 10, 100)])]);
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.UtcNow,
            [Bar(total, 100), Bar(incoming, 30), Bar(outgoing, 70)]);
        var result = new ResourceTableProjector().Project(snapshot,
            new ResourceTableRequest([column], column, "desc", ResourceTableViewModes.Software,
                new HashSet<string>(), true));
        Assert.Equal(3, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal(100, row.Values[column].Value));
    }

    [Fact]
    public void Project_MixedDatasetGenerationsRemainVisibleAndTraceable()
    {
        var cpuCapturedAt = DateTimeOffset.UnixEpoch.AddSeconds(20);
        var memoryCapturedAt = DateTimeOffset.UnixEpoch.AddSeconds(15);
        var snapshot = new ResourceBreakdownSnapshot(
            cpuCapturedAt,
            [
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.CpuUsage,
                    "CPU 占用率",
                    "%",
                    ResourceBreakdownScaleModes.Capacity,
                    20,
                    100,
                    20,
                    []),
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.MemoryUsage,
                    "内存占用",
                    "B",
                    ResourceBreakdownScaleModes.Capacity,
                    400,
                    1000,
                    40,
                    [])
            ])
        {
            Datasets =
            [
                new ResourceBreakdownDatasetSamplingState(
                    SamplingDatasetIds.ProcessCpuUsage,
                    ResourceBreakdownSamplingStatuses.Ready,
                    3,
                    5,
                    cpuCapturedAt,
                    cpuCapturedAt,
                    cpuCapturedAt,
                    cpuCapturedAt.AddSeconds(5),
                    null,
                    null),
                new ResourceBreakdownDatasetSamplingState(
                    SamplingDatasetIds.ProcessMemoryUsage,
                    ResourceBreakdownSamplingStatuses.Stale,
                    7,
                    9,
                    memoryCapturedAt,
                    cpuCapturedAt,
                    memoryCapturedAt,
                    memoryCapturedAt.AddSeconds(5),
                    "memory-sample-failed",
                    "本轮内存采样失败，保留旧值。")
            ]
        };
        var request = new ResourceTableRequest(
            [ResourceTableColumnIds.Cpu, ResourceTableColumnIds.Memory],
            ResourceTableColumnIds.Cpu,
            "desc",
            ResourceTableViewModes.Software,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var result = new ResourceTableProjector().Project(snapshot, request);

        Assert.Equal(ResourceTableProjectionStatuses.Stale, result.Status);
        Assert.Equal(cpuCapturedAt, result.CapturedAt);
        Assert.Collection(
            result.InputDatasets.OrderBy(static input => input.DatasetId),
            input =>
            {
                Assert.Equal(SamplingDatasetIds.ProcessCpuUsage, input.DatasetId);
                Assert.Equal((ulong)3, input.Generation);
                Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, input.Status);
            },
            input =>
            {
                Assert.Equal(SamplingDatasetIds.ProcessMemoryUsage, input.DatasetId);
                Assert.Equal((ulong)7, input.Generation);
                Assert.Equal(ResourceBreakdownSamplingStatuses.Stale, input.Status);
            });
        // 汇总行的状态装的是采样状态标识，措辞由前端出。
        Assert.Equal(ResourceTableProjectionStatuses.Stale, result.Rows[0].Status);
    }

    [Fact]
    public void Project_WarmingInputsReturnARealTableWithoutInventingCaptureTime()
    {
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.UnixEpoch, []);
        var request = new ResourceTableRequest(
            [],
            ResourceTableSortIds.Impact,
            "desc",
            ResourceTableViewModes.Software,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var result = new ResourceTableProjector().Project(snapshot, request);

        Assert.Equal(ResourceTableProjectionStatuses.Warming, result.Status);
        Assert.Null(result.CapturedAt);
        Assert.Equal(4, result.InputDatasets.Count);
        Assert.All(
            result.InputDatasets,
            input => Assert.Equal(ResourceBreakdownSamplingStatuses.Warming, input.Status));
        Assert.Equal(ResourceTableProjectionStatuses.Warming, result.Rows[0].Status);
    }

    [Fact]
    public void Project_InProcessModeIncludesProcessIdentityColumnsAndKilobyteMemory()
    {
        var process = new ResourceProcessSegment(
            4242,
            "worker.exe",
            @"C:\Tools\worker.exe",
            2 * 1024 * 1024,
            25,
            100,
            @"TEST-PC\test-user",
            "x64");
        var software = new ResourceSoftwareSegment(
            "software:test-app",
            "Test App",
            "Other",
            "其他软件",
            2 * 1024 * 1024,
            25,
            1,
            [process]);
        var bar = new ResourceBreakdownBar(
            ResourceBreakdownMetricIds.MemoryUsage,
            "内存占用",
            "B",
            ResourceBreakdownScaleModes.Capacity,
            2 * 1024 * 1024,
            8d * 1024 * 1024 * 1024,
            25,
            [software]);
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.UnixEpoch, [bar]);
        var request = new ResourceTableRequest(
            [
                ResourceTableColumnIds.Name,
                ResourceTableColumnIds.ProcessId,
                ResourceTableColumnIds.Status,
                ResourceTableColumnIds.User,
                ResourceTableColumnIds.Architecture,
                ResourceTableColumnIds.Memory
            ],
            ResourceTableColumnIds.Memory,
            "desc",
            ResourceTableViewModes.Process,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var result = new ResourceTableProjector().Project(snapshot, request);

        var row = SingleDataRow(result);
        Assert.Equal(ResourceTableRowKinds.Process, row.Kind);
        Assert.Equal("worker.exe", row.Name);
        Assert.Equal(ResourceTableProcessStates.Running, row.Status);
        Assert.Equal("4242", row.Values[ResourceTableColumnIds.ProcessId].DisplayValue);
        Assert.Equal(@"TEST-PC\test-user", row.Values[ResourceTableColumnIds.User].DisplayValue);
        Assert.Equal("x64", row.Values[ResourceTableColumnIds.Architecture].DisplayValue);
        AssertBytes(row.Values[ResourceTableColumnIds.Memory], 2 * 1024 * 1024);
    }

    [Fact]
    public void Project_InSoftwareModeDefaultColumnsDoNotIncludeProcessIdentityColumns()
    {
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.UnixEpoch, []);
        var request = new ResourceTableRequest(
            [],
            ResourceTableSortIds.Impact,
            "desc",
            ResourceTableViewModes.Software,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var result = new ResourceTableProjector().Project(snapshot, request);

        Assert.DoesNotContain(result.Columns, column => column.Id == ResourceTableColumnIds.ProcessId);
        Assert.DoesNotContain(result.Columns, column => column.Id == ResourceTableColumnIds.User);
        Assert.DoesNotContain(result.Columns, column => column.Id == ResourceTableColumnIds.Architecture);
        Assert.Contains(result.Columns, column => column.Id == ResourceTableColumnIds.Name);
        Assert.Contains(result.Columns, column => column.Id == ResourceTableColumnIds.Status);
    }

    [Fact]
    public void Project_PreservesExactRequestedColumnOrder()
    {
        var snapshot = new ResourceBreakdownSnapshot(
            DateTimeOffset.UnixEpoch,
            [
                new ResourceBreakdownBar(
                    "gpu.0.usage",
                    "GPU0 占用率",
                    "%",
                    ResourceBreakdownScaleModes.Capacity,
                    25,
                    100,
                    25,
                    [])
            ]);
        string[] requested =
        [
            ResourceTableColumnIds.Network,
            "gpu.0.usage",
            ResourceTableColumnIds.Name,
            ResourceTableColumnIds.Disk,
            ResourceTableColumnIds.Cpu
        ];
        var request = new ResourceTableRequest(
            requested,
            ResourceTableColumnIds.Cpu,
            "desc",
            ResourceTableViewModes.Software,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var result = new ResourceTableProjector().Project(snapshot, request);

        Assert.Equal(requested, result.Columns.Select(static column => column.Id));
    }

    [Fact]
    public void Project_UnavailableBarProducesUnavailableSummaryInsteadOfZero()
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
            ]);
        var request = new ResourceTableRequest(
            [ResourceTableColumnIds.Name, "gpu.0.usage"],
            "gpu.0.usage",
            "desc",
            ResourceTableViewModes.Software,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var result = new ResourceTableProjector().Project(snapshot, request);
        var summary = Assert.Single(
            result.Rows,
            static row => row.Kind == ResourceTableRowKinds.Summary);
        var value = summary.Values["gpu.0.usage"];

        Assert.Null(value.Value);
        Assert.Null(value.Percent);
        Assert.Equal(string.Empty, value.DisplayValue);
        Assert.Equal("Unavailable", value.Availability);
        Assert.Equal("Unavailable", value.Availability);
    }

    [Fact]
    public void Project_InSoftwareModeUsesSmartByteUnits()
    {
        var software = new ResourceSoftwareSegment(
            "software:test-app",
            "Test App",
            "Other",
            "其他软件",
            2 * 1024 * 1024,
            25,
            1,
            []);
        var bar = new ResourceBreakdownBar(
            ResourceBreakdownMetricIds.MemoryUsage,
            "内存占用",
            "B",
            ResourceBreakdownScaleModes.Capacity,
            2 * 1024 * 1024,
            8d * 1024 * 1024 * 1024,
            25,
            [software]);
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.UnixEpoch, [bar]);
        var request = new ResourceTableRequest(
            [ResourceTableColumnIds.Name, ResourceTableColumnIds.Memory],
            ResourceTableColumnIds.Memory,
            "desc",
            ResourceTableViewModes.Software,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var result = new ResourceTableProjector().Project(snapshot, request);

        var row = SingleDataRow(result);
        Assert.Equal(ResourceTableRowKinds.Software, row.Kind);
        AssertBytes(row.Values[ResourceTableColumnIds.Memory], 2 * 1024 * 1024);
    }

    [Fact]
    public void Project_DoesNotCreateRowsForBarsWithoutVisibleTableColumn()
    {
        var software = new ResourceSoftwareSegment(
            "resource-residual:virtualMemory.usage",
            "系统/驱动保留 · 虚拟内存占用",
            "WindowsSystem",
            SoftwareDisplayKinds.SystemResidual,
            2 * 1024 * 1024,
            10,
            0,
            []);
        var bar = new ResourceBreakdownBar(
            ResourceBreakdownMetricIds.VirtualMemoryUsage,
            "虚拟内存占用",
            "B",
            ResourceBreakdownScaleModes.Capacity,
            2 * 1024 * 1024,
            20d * 1024 * 1024 * 1024,
            10,
            [software]);
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.UnixEpoch, [bar]);
        var request = new ResourceTableRequest(
            [ResourceTableColumnIds.Name, ResourceTableColumnIds.Memory],
            ResourceTableColumnIds.Memory,
            "desc",
            ResourceTableViewModes.Software,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var result = new ResourceTableProjector().Project(snapshot, request);

        Assert.Empty(DataRows(result));
    }

    [Fact]
    public void Project_SystemResidualProcessRowsAreExplanatoryNotRealPids()
    {
        var residualCategory = new ResourceProcessSegment(
            -1,
            "GPU 驱动 / WDDM / 桌面合成保留（未细分）",
            null,
            512 * 1024 * 1024,
            12.5,
            100,
            null,
            null,
            ResourceProcessAttributionKinds.SystemResidual);
        var software = new ResourceSoftwareSegment(
            "resource-residual:gpu.1.vram",
            "系统/驱动保留 · GPU1 显存占用",
            "WindowsSystem",
            SoftwareDisplayKinds.SystemResidual,
            512 * 1024 * 1024,
            12.5,
            0,
            [residualCategory]);
        var bar = new ResourceBreakdownBar(
            "gpu.1.vram",
            "GPU1 显存占用",
            "B",
            ResourceBreakdownScaleModes.Capacity,
            512 * 1024 * 1024,
            4d * 1024 * 1024 * 1024,
            12.5,
            [software]);
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.UnixEpoch, [bar]);
        var request = new ResourceTableRequest(
            [
                ResourceTableColumnIds.Name,
                ResourceTableColumnIds.ProcessId,
                ResourceTableColumnIds.Status,
                "gpu.1.vram"
            ],
            "gpu.1.vram",
            "desc",
            ResourceTableViewModes.Process,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var result = new ResourceTableProjector().Project(snapshot, request);

        var row = SingleDataRow(result);
        Assert.Equal(ResourceTableRowKinds.Process, row.Kind);
        Assert.Equal("GPU 驱动 / WDDM / 桌面合成保留（未细分）", row.Name);
        Assert.Equal(SoftwareDisplayKinds.SystemResidual, row.Status);
        Assert.Null(row.ProcessId);
        Assert.Equal("--", row.Values[ResourceTableColumnIds.ProcessId].DisplayValue);
        AssertBytes(row.Values["gpu.1.vram"], 512L * 1024 * 1024);
    }

    [Fact]
    public void Project_EtwResidualProcessRowsKeepPidAndEvidenceStatus()
    {
        var residualCategory = new ResourceProcessSegment(
            1528,
            "dwm（DXGKrnl/VidMm）",
            null,
            256 * 1024 * 1024,
            6.25,
            100,
            null,
            null,
            ResourceProcessAttributionKinds.EtwResidualProcess);
        var software = new ResourceSoftwareSegment(
            "resource-residual:gpu.1.vram",
            "系统/驱动保留 · GPU1 显存占用",
            "WindowsSystem",
            SoftwareDisplayKinds.SystemResidual,
            256 * 1024 * 1024,
            6.25,
            0,
            [residualCategory]);
        var bar = new ResourceBreakdownBar(
            "gpu.1.vram",
            "GPU1 显存占用",
            "B",
            ResourceBreakdownScaleModes.Capacity,
            256 * 1024 * 1024,
            4d * 1024 * 1024 * 1024,
            6.25,
            [software]);
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.UnixEpoch, [bar]);
        var request = new ResourceTableRequest(
            [
                ResourceTableColumnIds.Name,
                ResourceTableColumnIds.ProcessId,
                ResourceTableColumnIds.Status,
                "gpu.1.vram"
            ],
            "gpu.1.vram",
            "desc",
            ResourceTableViewModes.Process,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var result = new ResourceTableProjector().Project(snapshot, request);

        var row = SingleDataRow(result);
        Assert.Equal(ResourceTableRowKinds.Process, row.Kind);
        Assert.Equal("dwm（DXGKrnl/VidMm）", row.Name);
        Assert.Equal("DXGKrnl/VidMm", row.Status);
        Assert.Equal(1528, row.ProcessId);
        Assert.Equal("1528", row.Values[ResourceTableColumnIds.ProcessId].DisplayValue);
        AssertBytes(row.Values["gpu.1.vram"], 256L * 1024 * 1024);
    }

    [Fact]
    public void Project_IncludesExternalProviderStates()
    {
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.UnixEpoch, []);
        var request = new ResourceTableRequest(
            [ResourceTableColumnIds.Name, "gpu.1.vram"],
            ResourceTableSortIds.Impact,
            "desc",
            ResourceTableViewModes.Software,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var result = new ResourceTableProjector([new FakeProviderStateSource()]).Project(snapshot, request);

        var state = Assert.Single(result.ProviderStates);
        Assert.Equal("dxgkrnl-vidmm-etw", state.Id);
        Assert.Equal("Unavailable", state.State);
        Assert.Equal("DXGKrnl/VidMm ETW 需要管理员权限。", state.Message);
    }

    [Fact]
    public void Project_InSoftwareModeCanIncludeAllProcessRowsForFrontendProjection()
    {
        var process = new ResourceProcessSegment(
            4242,
            "worker.exe",
            @"C:\Tools\worker.exe",
            2 * 1024 * 1024,
            25,
            100,
            @"TEST-PC\test-user",
            "x64");
        var software = new ResourceSoftwareSegment(
            "software:test-app",
            "Test App",
            "Other",
            "其他软件",
            2 * 1024 * 1024,
            25,
            1,
            [process]);
        var bar = new ResourceBreakdownBar(
            ResourceBreakdownMetricIds.MemoryUsage,
            "内存占用",
            "B",
            ResourceBreakdownScaleModes.Capacity,
            2 * 1024 * 1024,
            8d * 1024 * 1024 * 1024,
            25,
            [software]);
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.UnixEpoch, [bar]);
        var request = new ResourceTableRequest(
            [ResourceTableColumnIds.Name, ResourceTableColumnIds.Memory],
            ResourceTableColumnIds.Memory,
            "desc",
            ResourceTableViewModes.Software,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            true);

        var result = new ResourceTableProjector().Project(snapshot, request);

        Assert.Collection(
            DataRows(result),
            row => Assert.Equal(ResourceTableRowKinds.Software, row.Kind),
            row =>
            {
                Assert.Equal(ResourceTableRowKinds.Process, row.Kind);
                Assert.Equal("worker.exe", row.Name);
                Assert.Equal(1, row.Depth);
            });
    }

    [Fact]
    public void Project_InProcessModeSortsProcessesGloballyAcrossSoftwareBySelectedResource()
    {
        var snapshot = CreateProcessMemorySnapshot(
            ("software:a", "App A", 1001, "a-high.exe", 400),
            ("software:a", "App A", 1002, "a-low.exe", 100),
            ("software:b", "App B", 2001, "b-high.exe", 300),
            ("software:b", "App B", 2002, "b-low.exe", 200));
        var request = new ResourceTableRequest(
            [ResourceTableColumnIds.Name, ResourceTableColumnIds.Memory],
            ResourceTableColumnIds.Memory,
            "desc",
            ResourceTableViewModes.Process,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var result = new ResourceTableProjector().Project(snapshot, request);

        Assert.Equal(
            [1001, 2001, 2002, 1002],
            DataRows(result).Select(static row => row.ProcessId));
    }

    [Fact]
    public void Project_ProcessImpactSortImmediatelyUsesCurrentScores()
    {
        var projector = new ResourceTableProjector();
        var request = new ResourceTableRequest(
            [ResourceTableColumnIds.Name, ResourceTableColumnIds.Memory],
            ResourceTableSortIds.Impact,
            "desc",
            ResourceTableViewModes.Process,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var first = projector.Project(
            CreateProcessMemorySnapshot(
                ("software:a", "App A", 1001, "first.exe", 100),
                ("software:b", "App B", 2001, "second.exe", 95)),
            request);
        Assert.Equal(
            [1001, 2001],
            DataRows(first).Select(static row => row.ProcessId));

        var next = projector.Project(
            CreateProcessMemorySnapshot(
                ("software:a", "App A", 1001, "first.exe", 95),
                ("software:b", "App B", 2001, "second.exe", 100)),
            request);

        Assert.Equal(
            [2001, 1001],
            DataRows(next).Select(static row => row.ProcessId));

        var ascending = projector.Project(
            CreateProcessMemorySnapshot(
                ("software:a", "App A", 1001, "first.exe", 95),
                ("software:b", "App B", 2001, "second.exe", 100)),
            request with { SortDirection = "asc" });

        Assert.Equal(
            [1001, 2001],
            DataRows(ascending).Select(static row => row.ProcessId));
    }

    [Fact]
    public void Project_DoesNotMergeDifferentProcessInstancesThatReusePid()
    {
        const int reusedProcessId = 4242;
        const long oldStartKey = 100;
        const long newStartKey = 200;
        var oldProcess = new ResourceProcessSegment(
            reusedProcessId,
            "old-worker.exe",
            @"C:\Tools\old-worker.exe",
            30,
            30,
            100)
        {
            ProcessStartKey = oldStartKey
        };
        var newProcess = new ResourceProcessSegment(
            reusedProcessId,
            "new-worker.exe",
            @"C:\Tools\new-worker.exe",
            400,
            40,
            100)
        {
            ProcessStartKey = newStartKey
        };
        var cpuSoftware = new ResourceSoftwareSegment(
            "software:test",
            "Test App",
            "Other",
            SoftwareDisplayKinds.General,
            30,
            30,
            1,
            [oldProcess]);
        var memorySoftware = new ResourceSoftwareSegment(
            "software:test",
            "Test App",
            "Other",
            SoftwareDisplayKinds.General,
            400,
            40,
            1,
            [newProcess]);
        var snapshot = new ResourceBreakdownSnapshot(
            DateTimeOffset.UnixEpoch,
            [
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.CpuUsage,
                    "CPU 占用率",
                    "%",
                    ResourceBreakdownScaleModes.Capacity,
                    30,
                    100,
                    30,
                    [cpuSoftware]),
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.MemoryUsage,
                    "内存占用",
                    "B",
                    ResourceBreakdownScaleModes.Capacity,
                    400,
                    1000,
                    40,
                    [memorySoftware])
            ]);
        var request = new ResourceTableRequest(
            [
                ResourceTableColumnIds.Name,
                ResourceTableColumnIds.ProcessId,
                ResourceTableColumnIds.Cpu,
                ResourceTableColumnIds.Memory
            ],
            ResourceTableSortIds.Impact,
            "desc",
            ResourceTableViewModes.Process,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        var rows = DataRows(new ResourceTableProjector().Project(snapshot, request));

        Assert.Equal(2, rows.Count);
        var oldRow = Assert.Single(rows, row => row.ProcessStartKey == oldStartKey.ToString());
        var newRow = Assert.Single(rows, row => row.ProcessStartKey == newStartKey.ToString());
        Assert.Equal("old-worker.exe", oldRow.Name);
        Assert.Equal("%", oldRow.Values[ResourceTableColumnIds.Cpu].Unit);
        Assert.Equal(30, oldRow.Values[ResourceTableColumnIds.Cpu].Value);
        Assert.Null(oldRow.Values[ResourceTableColumnIds.Memory].Value);
        Assert.Equal("new-worker.exe", newRow.Name);
        Assert.Null(newRow.Values[ResourceTableColumnIds.Cpu].Value);
        AssertBytes(newRow.Values[ResourceTableColumnIds.Memory], 400);
        Assert.NotEqual(oldRow.Id, newRow.Id);
    }

    private sealed class FakeProviderStateSource : IResourceTableProviderStateSource
    {
        public IReadOnlyList<ResourceTableProviderState> GetStates(IReadOnlyList<ResourceTableColumn> columns)
        {
            return columns.Any(column => column.Id.Equals("gpu.1.vram", StringComparison.OrdinalIgnoreCase))
                ? [new ResourceTableProviderState("dxgkrnl-vidmm-etw", "Unavailable", "DXGKrnl/VidMm ETW 需要管理员权限。")]
                : [];
        }
    }

    private static ResourceTableRow SingleDataRow(ResourceTableSnapshot snapshot)
    {
        return Assert.Single(DataRows(snapshot));
    }

    private static IReadOnlyList<ResourceTableRow> DataRows(ResourceTableSnapshot snapshot)
    {
        return snapshot.Rows
            .Where(static row => !row.Kind.Equals(ResourceTableRowKinds.Summary, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static ResourceBreakdownSnapshot CreateProcessMemorySnapshot(
        params (string SoftwareId, string SoftwareName, int ProcessId, string ProcessName, double Value)[] rows)
    {
        const double capacity = 1000;
        var software = rows
            .GroupBy(static row => (row.SoftwareId, row.SoftwareName))
            .Select(group =>
            {
                var processes = group
                    .Select(row => new ResourceProcessSegment(
                        row.ProcessId,
                        row.ProcessName,
                        $@"C:\Tools\{row.ProcessName}",
                        row.Value,
                        row.Value * 100 / capacity,
                        row.Value * 100 / group.Sum(static item => item.Value)))
                    .ToArray();
                var total = group.Sum(static row => row.Value);
                return new ResourceSoftwareSegment(
                    group.Key.SoftwareId,
                    group.Key.SoftwareName,
                    "Other",
                    SoftwareDisplayKinds.General,
                    total,
                    total * 100 / capacity,
                    processes.Length,
                    processes);
            })
            .ToArray();
        var totalValue = rows.Sum(static row => row.Value);
        return new ResourceBreakdownSnapshot(
            DateTimeOffset.UnixEpoch,
            [
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.MemoryUsage,
                    "内存占用",
                    "B",
                    ResourceBreakdownScaleModes.Capacity,
                    totalValue,
                    capacity,
                    totalValue * 100 / capacity,
                    software)
            ]);
    }
    // 容量单元只带原始字节和单位；显示成 GiB 还是 GB 由前端按用户选择决定，
    // 那条规则钉在 ClientApp 的 byteUnitsContract 里。
    private static void AssertBytes(ResourceTableValue value, double expectedBytes)
    {
        Assert.Equal("B", value.Unit);
        Assert.Equal(expectedBytes, value.Value);
        Assert.Equal(string.Empty, value.DisplayValue);
    }
}
