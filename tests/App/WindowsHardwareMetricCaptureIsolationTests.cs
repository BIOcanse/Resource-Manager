using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using System.Runtime.InteropServices;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsHardwareMetricCaptureIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogPreEnrichmentUsesFullFrameAndRejectedOrCanceledPublicationDoesNotCommit(bool cancel)
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"capture-preparation-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [], [], [], workspace.CreateCatalogHandleMap());
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var before = workspace.ReadHeader();
        var fieldCatalog = new HardwareMetricSnapshot(DateTimeOffset.UtcNow, null!, null!, null!, [], null!,
            new Dictionary<string, MetricValue>(StringComparer.OrdinalIgnoreCase))
        {
            WorkspaceIdentity = 1,
            ConfigurationGeneration = before.ConfigurationGeneration,
            CatalogGeneration = before.CatalogGeneration,
            CommittedGeneration = before.CommittedGeneration
        };
        using var resources = RecordedResources(
            () => new(true, 100, 1000, 1000, 10000, 1000, DateTimeOffset.UtcNow),
            () => throw new InvalidOperationException("unselected memory"));
        var capture = new WindowsHardwareMetricCapture(resources, _ => NativeMetricSnapshotZoneMode.Normal,
            TimeSpan.Zero, new AmdIntegratedGpuSensorRoute(), () => { });
        using var cancellation = new CancellationTokenSource();
        var enrichmentCalls = 0;
        var publicationCalls = 0;
        try
        {
            var request = MetricSampleRequest.ForIds(["cpu.usage"]).WithCatalog();
            async ValueTask<HardwareMetricSnapshot> Enrich(HardwareMetricSnapshot snapshot,
                MetricSampleRequest _, CancellationToken token, Action<string>? __)
            {
                enrichmentCalls++;
                Assert.NotEmpty(snapshot.FieldBindings); // Catalog work keeps the full native pre-projection.
                if (cancel)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                await ValueTask.CompletedTask;
                return snapshot;
            }

            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    _ = await capture.CaptureUnderGateAsync(request, workspace, 1, null,
                        _ => { publicationCalls++; return false; }, new("recorded"), cancellation.Token,
                        enrichBeforeCommit: Enrich, fieldCatalog: fieldCatalog);
                });
                Assert.Equal(0, publicationCalls);
            }
            else
            {
                var rejected = await capture.CaptureUnderGateAsync(request, workspace, 1, null,
                    _ => { publicationCalls++; return false; }, new("recorded"), default,
                    enrichBeforeCommit: Enrich, fieldCatalog: fieldCatalog);
                Assert.Null(rejected);
                Assert.Equal(1, publicationCalls);
            }

            Assert.Equal(1, enrichmentCalls);
            Assert.Equal(before.CommittedGeneration, workspace.ReadHeader().CommittedGeneration);
        }
        finally { capture.SampleGate.Dispose(); }
    }

    [Fact]
    public void StableFieldCatalogSkipsOneFullPreEnrichmentFrameAtActualNativeRowCounts()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"capture-preallocation-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [], [], [], workspace.CreateCatalogHandleMap());
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var header = workspace.ReadHeader();
        var fieldCatalog = new HardwareMetricSnapshot(DateTimeOffset.UtcNow, null!, null!, null!, [], null!,
            new Dictionary<string, MetricValue>(StringComparer.OrdinalIgnoreCase))
        {
            WorkspaceIdentity = 1,
            ConfigurationGeneration = header.ConfigurationGeneration,
            CatalogGeneration = header.CatalogGeneration,
            CommittedGeneration = header.CommittedGeneration
        };
        using var resources = RecordedResources(
            () => throw new InvalidOperationException("empty request sampled CPU"),
            () => throw new InvalidOperationException("empty request sampled memory"));
        var capture = new WindowsHardwareMetricCapture(resources, _ => NativeMetricSnapshotZoneMode.Normal,
            TimeSpan.Zero, new AmdIntegratedGpuSensorRoute(), () => { });
        var request = MetricSampleRequest.ForIds([]);
        HardwareMetricSnapshot? Capture(HardwareMetricSnapshot? currentCatalog)
            => capture.CaptureUnderGateAsync(request, workspace, 1, null,
                commit => { commit(); return true; }, new("recorded"), default,
                enrichBeforeCommit: static (snapshot, _, _, _) => ValueTask.FromResult(snapshot),
                fieldCatalog: currentCatalog).GetAwaiter().GetResult();

        try
        {
            for (var index = 0; index < 8; index++)
            {
                GC.KeepAlive(Capture(fieldCatalog));
                GC.KeepAlive(Capture(null));
            }

            long Measure(HardwareMetricSnapshot? currentCatalog)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                HardwareMetricSnapshot? result = null;
                for (var index = 0; index < 32; index++) result = Capture(currentCatalog);
                GC.KeepAlive(result);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }

            var minimalPrePath = Measure(fieldCatalog);
            var fullPrePath = Measure(null);
            var largeFrameBytes = (long)header.MetricCount * Marshal.SizeOf<NativeMetricSnapshotMetricOutput>()
                + (long)header.RuleCount * Marshal.SizeOf<NativeMetricSnapshotRuleStateOutput>();

            Assert.True(header.MetricCount > 0 && header.RuleCount > 0);
            Assert.True(fullPrePath - minimalPrePath >= largeFrameBytes * 32 * 3 / 4,
                $"Expected the full fallback to allocate one actual-count metric/rule frame per call. "
                + $"minimal={minimalPrePath:N0}, full={fullPrePath:N0}, expected-frame-buffers={largeFrameBytes:N0}, "
                + $"rows={header.MetricCount} metrics/{header.RuleCount} rules.");
        }
        finally { capture.SampleGate.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GpuInventoryFailureCannotDiscardReadableSmuCpuValues(bool requestGpu)
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"smu-cpu-isolation-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(0, "AMD integrated", "gpu-amd-integrated", 0xA1, 1, 2, false)], [], [], workspace.CreateCatalogHandleMap());
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var cpu = Assert.Single(catalog.MetricBindings.Values, binding => binding.Field?.Name == "cpu_amd_actual_power_watts");
        var gpu = Assert.Single(catalog.MetricBindings.Values, binding => binding.Field?.Name == "gpu_amd_apu_temperature_reported_celsius");
        var inventoryReads = 0;
        using var resources = RecordedResources(() => throw new Exception("unselected CPU counter"),
            () => throw new Exception("unselected memory"),
            () => { inventoryReads++; throw new IOException("GPU enumeration failed"); },
            _ => new(new("recorded SMU", HardwareSensorProviderStates.Active, "recorded"), null, null, null, null,
                ActualPowerWatts: 15, ApuTemperatureCelsius: 45));
        var failures = new List<string>();
        var capture = new WindowsHardwareMetricCapture(resources, _ => NativeMetricSnapshotZoneMode.Normal,
            TimeSpan.Zero, new AmdIntegratedGpuSensorRoute(), () => { }, (_, source) => failures.Add(source));
        try
        {
            var request = MetricSampleRequest.ForIds(requestGpu ? [cpu.MetricId, gpu.MetricId] : [cpu.MetricId]);
            var snapshot = Assert.IsType<HardwareMetricSnapshot>(await capture.CaptureUnderGateAsync(request, workspace,
                1, null, commit => { commit(); return true; }, new("recorded"), default));
            Assert.Equal(15d, snapshot.Items[cpu.MetricId].NumericValue);
            Assert.Equal(SamplingObservationStatus.Current, snapshot.Datasets[cpu.MetricId].Status);
            // The inventory dataset and SMU adapter routing are independently selected reads.
            Assert.Equal(requestGpu ? 2 : 0, inventoryReads);
            if (requestGpu)
            {
                Assert.Equal(SamplingObservationStatus.Unavailable, snapshot.Datasets[gpu.MetricId].Status);
                Assert.Equal(2, failures.Count);
                Assert.All(failures, source => Assert.Equal(NativeMetricSnapshotSources.WindowsGpuAdapterOrder, source));
            }
            else Assert.Empty(failures);
        }
        finally { capture.SampleGate.Dispose(); }
    }

    [Fact]
    public async Task InventoryOnlyPublishesUsableProcessGpuIdentityWithoutSamplingAnyNumericSource()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"inventory-only-{Guid.NewGuid():N}.bin"));
        var physical = new WindowsGpuPhysicalIdentity(0, @"PCI\VEN_10DE&DEV_0001\A",
            WindowsGpuPnpKind.Pci, true, true, 0, 1, 0, 0, 0x10DE, 1, 1, 1, 1,
            WindowsGpuIdentityEvidence.KmtLuidOpen, WindowsGpuPhysicalIdentityStatus.Bound, 0);
        var recorded = new WindowsGpuAdapter(0, "recorded NVIDIA", 0x10DE, 1, 1, new() { LowPart = 23 }, false,
            8UL * 1024 * 1024 * 1024, WindowsGpuAdapterKind.Dedicated)
            { PhysicalIdentityStatus = SamplingObservationStatus.Current, PhysicalIdentities = [physical] };
        var exact = ResourceManager.ReadWrite.Windows.Metrics.Topology.WindowsHardwareMetricTopologySource.CreateExactGpuKey(recorded);
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(0, "recorded NVIDIA", exact, 23, 9, 1, true)], [], [], workspace.CreateCatalogHandleMap());
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var inventoryReads = 0;
        var now = DateTimeOffset.UtcNow.UtcTicks;
        using var resources = RecordedResources(
            () => throw new InvalidOperationException("unselected CPU"),
            () => throw new InvalidOperationException("unselected memory"), () =>
            {
                inventoryReads++;
                return new(SamplingObservationStatus.Current, 9, now, 1, 0, 0, 17,
                    [recorded]);
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
        Func<WindowsGpuAdapterInventoryRead>? gpu = null, Func<AmdSmuCpuSensorReadRequest, CpuSensorMetrics>? smu = null) => new()
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
        ReadAmdSmu = smu ?? (_ => throw new InvalidOperationException("unselected SMU")),
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
