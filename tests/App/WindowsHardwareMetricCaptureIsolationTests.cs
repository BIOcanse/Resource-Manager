using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsHardwareMetricCaptureIsolationTests
{
    [Fact]
    public async Task InventoryOnlyPublishesUsableProcessGpuIdentityWithoutSamplingAnyNumericSource()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"inventory-only-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(0, "recorded NVIDIA", "gpu-recorded", 23, 9, 1, true)], [], [], workspace.CreateCatalogHandleMap());
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var inventoryReads = 0;
        var now = DateTimeOffset.UtcNow.UtcTicks;
        using var resources = RecordedResources(
            () => throw new InvalidOperationException("unselected CPU"),
            () => throw new InvalidOperationException("unselected memory"), () =>
            {
                inventoryReads++;
                return new(SamplingObservationStatus.Current, 9, now, 1, 0, 0, 17,
                    [new(0, "recorded NVIDIA", 0x10DE, 1, 1, new() { LowPart = 23 }, false,
                        8UL * 1024 * 1024 * 1024, WindowsGpuAdapterKind.Dedicated)]);
            });
        var failures = new List<string>();
        var capture = new WindowsHardwareMetricCapture(resources, _ => NativeMetricSnapshotZoneMode.Normal,
            TimeSpan.Zero, new AmdIntegratedGpuSensorRoute(), () => { }, (_, source) => failures.Add(source));
        try
        {
            var request = MetricSampleRequest.ForIds([MetricDatasetIds.SystemGpuInventory]);
            var queried = System.Collections.Immutable.ImmutableHashSet<string>.Empty;
            var result = Assert.IsType<HardwareMetricSnapshot>(await capture.CaptureUnderGateAsync(request, workspace, 1,
                null, commit => { commit(); return true; }, new("recorded"), default,
                sourceQueriesCompleted: addresses => queried = addresses));
            Assert.Equal(1, inventoryReads);
            Assert.Empty(failures); // Unselected numeric callbacks throw and are logged if invoked.
            Assert.Equal([MetricDatasetIds.SystemGpuInventory], queried);
            var current = new LastSuccessfulHardwareMetricSnapshot();
            current.Publish(result, request);
            var inventory = current.Read()!.GpuInventory;
            Assert.True(inventory.IsCurrentComplete(), System.Text.Json.JsonSerializer.Serialize(inventory));
            var adapter = Assert.Single(inventory.Adapters);
            Assert.Equal(23UL, adapter.AdapterKey);
            Assert.Equal(SamplingObservationStatus.NotRequested, adapter.UsageStatus);
            Assert.Equal(SchedulingGpuMetricMask.None, adapter.ValidMetricMask);
            var read = SchedulingProcessGpuRead.NotRequested with
            {
                UsageStatus = SamplingObservationStatus.Current, UsageGeneration = 7, UsageObservedAtUtcTicks = now,
                UsagePercent = new Dictionary<(ulong, int, ulong), double> { [(23, 41, 100)] = 35 }
            };
            var facts = WindowsResourceBreakdownSampler.CreateSchedulingGpuFacts(
                new(SchedulingProcessMetricMask.GpuUsage, inventory), read, 41, 100);
            var fact = Assert.Single(facts);
            Assert.Equal(35, fact.UsagePercent);
            Assert.Equal(inventory.Generation, fact.UsageTopologyGeneration);
            var process = new SchedulingProcessFact(41, 100, "recorded", "", "recorded", "recorded", "other", "other",
                35, SchedulingProcessMetricMask.GpuUsage, 0, 0, 1, facts);
            var processes = new SchedulingProcessFactSnapshot(SamplingObservationStatus.Current, 1, now, 1, 1, 0, 0,
                SchedulingProcessMetricMask.GpuUsage, SchedulingProcessMetricMask.GpuUsage, [process])
            {
                DatasetObservations = new Dictionary<SchedulingProcessMetricMask, SchedulingProcessDatasetObservation>
                {
                    [SchedulingProcessMetricMask.GpuUsage] = SchedulingProcessDatasetObservation.CreateCurrent(
                        SchedulingProcessMetricMask.GpuUsage, 7, now, 1, now,
                        topologyGeneration: inventory.Generation, topologyFingerprint: inventory.TopologyFingerprint)
                }
            };
            Assert.True(processes.IsGpuUsageCurrentComplete(inventory));
        }
        finally { capture.SampleGate.Dispose(); }
    }

    [Theory]
    [InlineData("collection")]
    [InlineData("provider-cancellation")]
    [InlineData("completion")]
    public async Task FailedRequestedCpuValueIsEmptyWhileMemoryPublishesAndNextCaptureRecovers(string failure)
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"capture-isolation-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [], [], [], workspace.CreateCatalogHandleMap());
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var fail = false;
        ulong counter = 0;
        ulong memoryUsed = 64;
        using var resources = RecordedResources(() =>
        {
            counter++;
            if (fail && failure == "collection") throw new InvalidOperationException("recorded collection failure");
            if (fail && failure == "provider-cancellation") throw new OperationCanceledException("recorded provider cancellation");
            return new(true, counter * 10, counter * 100, counter * 100, counter * 1000,
                fail && failure == "completion" ? 0UL : 1000UL, DateTimeOffset.UtcNow);
        }, () => new(memoryUsed, 256, memoryUsed * 100d / 256, true, "recorded"));
        var failures = new List<(Exception Error, string Source)>();
        var capture = new WindowsHardwareMetricCapture(resources, _ => NativeMetricSnapshotZoneMode.Normal,
            TimeSpan.Zero, new AmdIntegratedGpuSensorRoute(), () => { }, (error, source) => failures.Add((error, source)));
        try
        {
            var request = MetricSampleRequest.ForIds(["cpu.usage", "memory.usage"]);
            Task<HardwareMetricSnapshot?> Capture() => capture.CaptureUnderGateAsync(request, workspace, 1,
                null, commit => { commit(); return true; }, new("recorded"), default);
            Assert.NotNull(await Capture()); // CPU baseline.
            var prior = Assert.IsType<HardwareMetricSnapshot>(await Capture());
            Assert.Equal(SamplingObservationStatus.Current, prior.Datasets["cpu.usage"].Status);
            Assert.Contains("cpu.usage", prior.Items.Keys);
            Assert.Empty(failures);

            fail = true;
            memoryUsed = 128;
            var result = Assert.IsType<HardwareMetricSnapshot>(await Capture());

            Assert.Equal(128UL, result.Memory.UsedBytes);
            Assert.Equal(SamplingObservationStatus.Current, result.Datasets["memory.usage"].Status);
            Assert.Equal(SamplingObservationStatus.Unavailable, result.Datasets["cpu.usage"].Status);
            Assert.False(result.Cpu.IsUsageAvailable);
            Assert.Null(result.Items["cpu.usage"].NumericValue);
            Assert.Null(result.Items["cpu.usage"].IntegerValue);
            var error = Assert.Single(failures);
            Assert.Equal(NativeMetricSnapshotSources.WindowsCpu, error.Source);
            Assert.Contains(failure switch
            {
                "completion" => "submit CPU counter",
                "provider-cancellation" => "recorded provider cancellation",
                _ => "recorded collection failure"
            }, error.Error.Message);
            var cpu = Assert.Single(workspace.ReadCommitted().Sources,
                source => catalog.SourceIds[source.SourceHandle] == NativeMetricSnapshotSources.WindowsCpu);
            Assert.Equal((uint)NativeMetricSnapshotSourceStatus.Unavailable, cpu.Status);

            fail = false;
            Assert.NotNull(await Capture()); // Reestablish CPU baseline after the empty value.
            var recovered = Assert.IsType<HardwareMetricSnapshot>(await Capture());
            Assert.Equal(SamplingObservationStatus.Current, recovered.Datasets["cpu.usage"].Status);
            Assert.Equal(128UL, recovered.Memory.UsedBytes);
            Assert.Single(failures);
        }
        finally { capture.SampleGate.Dispose(); }
    }

    private static WindowsHardwareMetricResources RecordedResources(
        Func<NativeMetricSnapshotCpuCounterObservation> cpu, Func<MemoryMetrics> memory,
        Func<WindowsGpuAdapterInventoryRead>? gpu = null) => new()
    {
        CpuName = "recorded", MemoryDescription = "recorded", ReadCpuCounters = cpu, ReadMemory = memory,
        ReadVirtualMemory = () => throw new InvalidOperationException("unselected virtual memory"),
        ReadNumaAvailable = (_, _) => new Dictionary<string, ulong>(),
        ReadNumaIdentities = () => throw new InvalidOperationException("unselected NUMA directory"),
        ReadGpuInventory = gpu ?? (() => throw new InvalidOperationException("unselected GPU inventory")),
        ReadCpuFrequency = () => throw new InvalidOperationException("unselected frequency"),
        ReadGpuUsage = _ => throw new InvalidOperationException("unselected GPU usage"),
        ReadNvmlAsync = (_, _, _) => throw new InvalidOperationException("unselected NVML"),
        ReadNvapiAsync = (_, _, _) => throw new InvalidOperationException("unselected NVAPI"),
        ReadAdlxAsync = (_, _, _) => throw new InvalidOperationException("unselected ADLX"),
        ReadAmdSmu = _ => throw new InvalidOperationException("unselected SMU"),
        ReadIntelGpu = (_, _, _) => throw new InvalidOperationException("unselected IGCL"),
        ReadIntelMsr = (_, _) => throw new InvalidOperationException("unselected MSR"),
        ReadBatteryIdentities = () => throw new InvalidOperationException("unselected battery directory"),
        ReadBattery = (_, _) => throw new InvalidOperationException("unselected battery"),
        ReadNetworkIdentities = () => throw new InvalidOperationException("unselected network directory"),
        ReadNetworkInterfaces = (_, _) => throw new InvalidOperationException("unselected network"),
        ReadSystemIo = _ => throw new InvalidOperationException("unselected IO"),
        ReadStorageIdentities = _ => throw new InvalidOperationException("unselected storage directory"),
        ReadFanIdentities = _ => throw new InvalidOperationException("unselected fan directory"),
        ReadStorageSensors = () => throw new InvalidOperationException("unselected storage"),
        ReadNvmeSensors = _ => throw new InvalidOperationException("unselected NVMe"),
        ReadAtaSensors = _ => throw new InvalidOperationException("unselected ATA"),
        ReadHardwareMonitor = () => throw new InvalidOperationException("unselected WMI"),
        ReadNotebookFans = () => throw new InvalidOperationException("unselected OEM"),
        SynchronizeNotebookDemand = _ => { }
    };
}
