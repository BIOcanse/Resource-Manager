using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessGpuResourceMeasureTests
{
    private const ulong Luid = 23;
    private static readonly DateTimeOffset Captured = DateTimeOffset.UnixEpoch.AddSeconds(1);
    private static readonly WindowsResourceBreakdownSampler.ProcessResourceSample[] Samples = [Sample(41), Sample(42)];

    [Fact]
    public void CatalogNamesRoundTripAndSeparateAdaptersPhysicalIndicesAndEngines()
    {
        var snapshot = Snapshot((41, 0, 60), (41, 1, 80));
        var fields = ProcessGpuResourceFieldRegistry.Describe([(0, Luid), (1, 24)], snapshot);
        Assert.Equal(10, fields.Count); // Three residency amounts and one shared-system-memory amount per adapter plus two observed engines.
        Assert.Equal(fields.Count, fields.Select(static field => field.Name).Distinct().Count());
        foreach (var field in fields)
        {
            var parsed = Assert.IsType<ProcessMetricFieldDefinition>(ProcessGpuResourceFieldRegistry.Find(field.Name));
            Assert.Equal(field with { EngineClass = null }, parsed);
            if (field.GpuEngine is not null) Assert.Equal("Compute", field.EngineClass);
            Assert.True(parsed.SupportsResourceFacts);
            Assert.True(parsed.AdditiveAcrossProcesses);
        }
        var sameEngineOnAnotherPhysical = ProcessGpuResourceFieldRegistry.ForEngine(0, new(Luid, 1, 0));
        Assert.DoesNotContain(sameEngineOnAnotherPhysical.Name, fields.Select(static field => field.Name));
        Assert.Equal(sameEngineOnAnotherPhysical, ProcessGpuResourceFieldRegistry.Find(sameEngineOnAnotherPhysical.Name));
        Assert.Null(ProcessGpuResourceFieldRegistry.Find(WindowsPdhProcessGpuFields.FieldName));
        Assert.Null(ProcessGpuResourceFieldRegistry.Find("process_all_gpu_residency_attributed_gpu_0_luid_0000000000000000_physical_0_engine_0"));
        Assert.Null(ProcessGpuResourceFieldRegistry.Find(fields[0].Name + "_extra"));
    }

    [Fact]
    public void AllocationAliasesSurviveTheOriginalPublicationSlotAndExactSelection()
    {
        var field = ProcessGpuResourceFieldRegistry.ForResidentMemory(0, Luid, ProcessMetricFieldCode.GpuResidentBytes);
        var allocation = new GpuAllocationReading(new Dictionary<ulong, IReadOnlyDictionary<int, GpuAllocationAmount>>
        { [Luid] = new Dictionary<int, GpuAllocationAmount> { [41] = new(10, 2) } }) { ObservedAtUtcTicks = Captured.UtcTicks };
        var projection = Project([field], null, null, allocation, Hardware(99));
        var source = new ResourceManager.App.Domain.ResourceBreakdown.ResourceBreakdownSnapshot(Captured, [])
        { ProcessFields = projection.Fields, ProcessMemoryFacts = projection.Facts };
        var state = new ResourceBreakdownSnapshotState();
        var request = ResourceManager.App.Domain.ResourceBreakdown.ResourceBreakdownSampleRequest.ForResourceTable([field.Name], new Dictionary<string, string>())
            with { ProcessMemoryFactIds = [field.Name] };
        state.ApplyDirect(request, source, Captured, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        var published = state.ReadOrCreate(Captured, request);
        Assert.Equal(12, Assert.Single(published.ProcessFields!.Processes).Values.Single().NumericValue);
        Assert.Equal(12, Assert.Single(published.ProcessMemoryFacts).ProcessSumValue);
        Assert.Equal(field.Name, Assert.Single(published.ProcessFields.Fields).Name);
    }

    [Fact]
    public void ExactEngineTotalsIncludeUnvalidatedPidsAndNeverClampToCapacityOrCombineEngines()
    {
        var read = Read(Snapshot((41, 0, 125), (42, 0, 20), (999, 0, 5), (41, 1, 77)));
        var definitions = new[] { Engine(0), Engine(1) };
        var result = Project(definitions, read, Canonical(read), null, Hardware(99));
        var first = result.Facts.Single(fact => fact.GpuEngine!.Value.EngineIndex == 0);
        Assert.Equal(150D, first.TotalValue);
        Assert.Equal(100D, first.CapacityValue);
        Assert.Equal(145D, first.ProcessSumValue);
        Assert.Equal(new double[] { 125, 20 }, first.Processes.Select(static process => process.Value));
        Assert.Equal(100L, first.WindowMilliseconds);
        Assert.Equal(DateTimeOffset.FromFileTime(190), first.CapturedAt);
        Assert.Equal("Compute", first.EngineClass);
        Assert.Equal(77D, result.Facts.Single(fact => fact.GpuEngine!.Value.EngineIndex == 1).TotalValue);
        Assert.DoesNotContain(first.Processes, static process => process.ProcessId == 999);
        // The canonical PDH projection clamps percent. The bound alias retains the native number.
        Assert.Equal(100D, Canonical(read).Processes.Single(row => row.ProcessId == 41
            && row.GpuEngine!.Value.EngineIndex == 0).Values.Single().NumericValue);
        Assert.Equal(125D, result.Fields.Processes.Single(row => row.ProcessId == 41
            && row.GpuEngine!.Value.EngineIndex == 0).Values.Single().NumericValue);
    }

    [Fact]
    public void ReusedPidCannotClaimContributionButStillRemainsInMachineTotal()
    {
        var read = Read(Snapshot((41, 0, 30), (42, 0, 40)));
        var validated = Canonical(read, static pid => pid == 42 ? 200 : 100);
        var fact = Assert.Single(Project([Engine(0)], read, validated).Facts);
        Assert.Equal(70D, fact.TotalValue);
        Assert.Equal(30D, fact.ProcessSumValue);
        Assert.Equal(41, Assert.Single(fact.Processes).ProcessId);
    }

    [Fact]
    public void ConflictingNativeWindowsCannotBecomeAnAdditiveEngineFact()
    {
        var raw = Snapshot((41, 0, 30), (42, 0, 40));
        raw = raw with { EngineWindows = raw.EngineWindows.Select(static window => window.ProcessId == 42
            ? window with { BeginFileTime = 160 } : window).ToArray() };
        var read = Read(raw);
        var result = Project([Engine(0)], read, Canonical(read));
        var fact = Assert.Single(result.Facts);
        Assert.Null(fact.TotalValue);
        Assert.Null(fact.CapturedAt);
        Assert.Null(fact.WindowMilliseconds);
        Assert.Empty(fact.Processes);
        // Individually valid field readings still retain their own original intervals.
        Assert.Equal(new long[] { 150, 160 }, result.Fields.Processes.Select(static row => row.GpuInterval!.BeginWindowsFileTime100Ns));
        Assert.All(result.Fields.Processes, static row => Assert.NotNull(row.Values.Single().NumericValue));
    }

    [Fact]
    public void LastGoodOrMissingWindowNeverUsesVendorUtilizationAsAnEngineFallback()
    {
        var raw = Snapshot((41, 0, 30));
        var stale = new NativePdhSnapshot(raw.CapturedAt, raw.Sequence, raw.TopologyGeneration,
            raw.EngineRows, raw.MemoryRows, raw.SystemIo, NativePdhFrameFlags.None,
            NativePdhDatasetObservation.LastGood(raw.CapturedAt, 7), NativePdhDatasetObservation.Unavailable,
            NativePdhDatasetObservation.Unavailable, NativePdhDatasetObservation.Unavailable)
        { EngineWindows = raw.EngineWindows };
        foreach (var snapshot in new[] { stale, raw with { EngineWindows = [] } })
        {
            var read = Read(snapshot);
            var result = Project([Engine(0)], read, Canonical(read), null, Hardware(99));
            var fact = Assert.Single(result.Facts);
            Assert.Null(fact.TotalValue);
            Assert.Null(fact.CapturedAt);
            Assert.Empty(fact.Processes);
            Assert.All(result.Fields.Processes, static row => Assert.Null(row.Values.Single().NumericValue));
        }
    }

    [Fact]
    public void AllocationAliasesPreserveFractionalSharedChargesAndIndependentDeviceTotal()
    {
        var ledger = new GpuAllocationLedger();
        ledger.SetAdapter(1, Luid);
        ledger.SetSegment(1, 2, 0);
        ledger.SetAllocation(1, 11, 9);
        ledger.Open(1, 11, 21, 41, 200);
        ledger.Open(1, 11, 22, 42, 200);
        ledger.SetCommittedGlobal(11, 2);
        ledger.SetAllocation(1, 12, 2);
        ledger.Open(1, 12, 23, 41, 200);
        ledger.SetCommittedGlobal(12, 2);
        var allocations = ledger.Read(new Dictionary<int, long> { [41] = 100, [42] = 100 })
            with { ObservedAtUtcTicks = Captured.UtcTicks, Generation = 7 };
        var definitions = ProcessGpuResourceFieldRegistry.Describe([(0, Luid)], null)
            .Where(ProcessGpuResourceFieldRegistry.IsVram).ToArray();
        var result = Project(definitions, null, null, allocations, Hardware(10));
        Assert.Equal(11D, result.Facts.Single(fact => fact.MetricId.StartsWith(ProcessGpuResourceFieldRegistry.ResidentBytes + "_gpu_", StringComparison.Ordinal)).ProcessSumValue);
        Assert.Equal(2D, result.Facts.Single(fact => fact.MetricId.StartsWith(ProcessGpuResourceFieldRegistry.ResidentPrivateBytes + "_gpu_", StringComparison.Ordinal)).ProcessSumValue);
        var shared = result.Facts.Single(fact => fact.MetricId.StartsWith(ProcessGpuResourceFieldRegistry.ResidentSharedBytes + "_gpu_", StringComparison.Ordinal));
        Assert.Equal(9D, shared.ProcessSumValue);
        Assert.All(shared.Processes, static process => Assert.Equal(4.5, process.Value));
        Assert.All(result.Facts, fact =>
        {
            Assert.Equal(10D, fact.TotalValue);
            Assert.Equal(100D, fact.CapacityValue);
            Assert.Equal(Captured, fact.CapturedAt);
            Assert.Equal("B", fact.Unit);
            Assert.True(fact.AdditiveAcrossProcesses);
            Assert.Empty(fact.Residuals); // Main same-frame reconciliation owns residuals.
        });
        Assert.All(result.Fields.Fields, static field =>
            Assert.Equal("process.gpu.resident-fields", field.DatasetId));
    }

    [Fact]
    public void SharedSystemMemoryFieldIsBoundButNotVramAndNamesItsOwnDataset()
    {
        var field = ProcessGpuResourceFieldRegistry.ForSharedSystemMemory(0, Luid);
        Assert.Equal("process_all_gpu_shared_system_memory_bytes_actual_gpu_0_luid_0000000000000017", field.Name);
        Assert.Equal(field, ProcessGpuResourceFieldRegistry.Find(field.Name));
        Assert.True(ProcessGpuResourceFieldRegistry.IsBound(field));
        Assert.False(ProcessGpuResourceFieldRegistry.IsVram(field)); // never starts the ETW allocation collector
        Assert.Equal(ProcessGpuResourceFieldRegistry.SharedSystemMemoryDatasetId, field.DatasetId);
        Assert.Equal(ProcessGpuResourceFieldRegistry.SharedSystemMemorySourceId, field.SourceId);
        Assert.Equal("gpu.0.sharedMemoryUsed", field.ResourceFactTotalMetricId);
        Assert.NotEqual(ProcessGpuResourceFieldRegistry.ForResidentMemory(0, Luid,
            ProcessMetricFieldCode.GpuResidentSharedBytes).DatasetId, field.DatasetId);
    }

    [Fact]
    public void SharedSystemMemoryPublishesOneValuePerValidatedInstanceAndAdapter()
    {
        var field = ProcessGpuResourceFieldRegistry.ForSharedSystemMemory(0, Luid);
        var read = new ProcessGpuSharedSystemMemoryRead(NativePdhProviderAvailability.Available, NativePdhResultCode.Ok,
            Captured, new Dictionary<(ulong AdapterLuid, int ProcessId, ulong ProcessStartKey), double>
            {
                [(Luid, 41, 100)] = 4096,
                [(24, 41, 100)] = 777, // another adapter: not this field's value
                [(Luid, 42, 99)] = 555 // a different instance of PID 42
            });

        var result = WindowsResourceBreakdownSampler.CreateGpuResourceFacts([field], Samples, [Adapter(Luid)], null,
            null, null, null, read);

        var fact = Assert.Single(result.Facts);
        var process = Assert.Single(fact.Processes);
        Assert.Equal((41, 100L, 4096D), (process.ProcessId, process.ProcessStartKey, process.Value));
        Assert.Equal(4096D, fact.ProcessSumValue);
        Assert.Equal(Captured, fact.CapturedAt);
        Assert.Equal("B", fact.Unit);
        Assert.Null(fact.CapacityValue);
        var row = Assert.Single(result.Fields.Processes);
        Assert.Equal(41, row.ProcessId);
        Assert.Equal(4096D, Assert.Single(row.Values).NumericValue);
        Assert.Equal(ProcessGpuResourceFieldRegistry.SharedSystemMemoryDatasetId, Assert.Single(result.Fields.Fields).DatasetId);
    }

    [Fact]
    public void SharedSystemMemoryWithoutAReadOrForAMissingInstanceHasNoRowAndNoZero()
    {
        var field = ProcessGpuResourceFieldRegistry.ForSharedSystemMemory(0, Luid);
        var unavailable = new ProcessGpuSharedSystemMemoryRead(NativePdhProviderAvailability.Unavailable,
            NativePdhResultCode.PdhError, null, new Dictionary<(ulong AdapterLuid, int ProcessId, ulong ProcessStartKey), double>());

        foreach (var read in new ProcessGpuSharedSystemMemoryRead?[] { null, ProcessGpuSharedSystemMemoryRead.NotRequested, unavailable })
        {
            var result = WindowsResourceBreakdownSampler.CreateGpuResourceFacts([field], Samples, [Adapter(Luid)], null,
                null, null, null, read);
            var fact = Assert.Single(result.Facts);
            Assert.Empty(fact.Processes);
            Assert.Null(fact.CapturedAt);
            Assert.Empty(result.Fields.Processes);
        }
    }

    [Fact]
    public void SharedSystemMemoryOnAnAdapterThatNoLongerMatchesPublishesNothing()
    {
        var field = ProcessGpuResourceFieldRegistry.ForSharedSystemMemory(0, Luid);
        var read = new ProcessGpuSharedSystemMemoryRead(NativePdhProviderAvailability.Available, NativePdhResultCode.Ok,
            Captured, new Dictionary<(ulong AdapterLuid, int ProcessId, ulong ProcessStartKey), double> { [(Luid, 41, 100)] = 1 });

        var result = WindowsResourceBreakdownSampler.CreateGpuResourceFacts([field], Samples, [Adapter(24)], null,
            null, null, null, read);

        Assert.Empty(Assert.Single(result.Facts).Processes);
    }

    [Fact]
    public void MissingAllocationPublicationNeverInventsProcessZerosOrUsesPdhDedicatedMemory()
    {
        var field = ProcessGpuResourceFieldRegistry.ForResidentMemory(0, Luid, ProcessMetricFieldCode.GpuResidentBytes);
        var result = Project([field], Read(Snapshot((41, 0, 30))), null, null, Hardware(10));
        var fact = Assert.Single(result.Facts);
        Assert.Equal(10D, fact.TotalValue);
        Assert.Equal(100D, fact.CapacityValue);
        Assert.Null(fact.CapturedAt);
        Assert.Empty(fact.Processes);
        Assert.Empty(result.Fields.Processes);
    }

    [Fact]
    public void PublicIndexReusedForAnotherLuidDoesNotBindOldEngineOrAllocationValues()
    {
        var read = Read(Snapshot((41, 0, 30)));
        var field = Engine(0);
        var result = WindowsResourceBreakdownSampler.CreateGpuResourceFacts([field], Samples, [Adapter(24)],
            read, Canonical(read), null, Hardware(99));
        var fact = Assert.Single(result.Facts);
        Assert.Null(fact.TotalValue);
        Assert.Null(fact.CapturedAt);
        Assert.Empty(fact.Processes);
        Assert.All(result.Fields.Processes, static row => Assert.Null(row.Values.Single().NumericValue));
    }

    private static ProcessMetricFieldDefinition Engine(uint engine)
        => ProcessGpuResourceFieldRegistry.ForEngine(0, new(Luid, 0, engine));

    private static NativePdhReadResult Read(NativePdhSnapshot raw)
        => new(raw, NativePdhProviderAvailability.Available, NativePdhResultCode.Ok, 0);

    private static NativePdhSnapshot Snapshot(params (uint Pid, uint Engine, double Busy)[] values)
        => new(Captured, 7, 1, values.Select(static value => new NativePdhGpuEngineRow
        {
            AdapterLuid = Luid, ProcessId = value.Pid, EngineIndex = value.Engine,
            EngineClass = NativePdhEngineClass.Compute, UsagePercent = value.Busy
        }).ToArray(), [new() { AdapterLuid = Luid, ProcessId = 41, DedicatedBytes = 9999 }], default)
        {
            EngineWindows = values.Select(static value => new NativePdhGpuEngineWindow
            {
                AdapterLuid = Luid, ProcessId = value.Pid, EngineIndex = value.Engine,
                BeginFileTime = 150, EndFileTime = 190, BeginCounter = 10, EndCounter = 20, CounterFrequency = 100
            }).ToArray()
        };

    private static ProcessMetricFieldSnapshot Canonical(NativePdhReadResult read, Func<int, long?>? birth = null)
        => WindowsPdhProcessGpuFields.Project(read, new(null, null, [],
            [new(41, "100", []), new(42, "100", [])]), birth ?? (static _ => 100));

    private static GpuResourceFactProjection Project(IReadOnlyList<ProcessMetricFieldDefinition> definitions,
        NativePdhReadResult? read, ProcessMetricFieldSnapshot? canonical, GpuAllocationReading? allocations = null,
        HardwareMetricSnapshot? hardware = null)
        => WindowsResourceBreakdownSampler.CreateGpuResourceFacts(definitions, Samples, [Adapter(Luid)], read,
            canonical, allocations, hardware);

    private static WindowsGpuAdapter Adapter(ulong luid)
        => new(0, "recorded GPU", 0x10DE, 1, 0,
            new AdapterLuid { LowPart = (uint)luid, HighPart = (int)(luid >> 32) }, false, 100, WindowsGpuAdapterKind.Dedicated);

    private static WindowsResourceBreakdownSampler.ProcessResourceSample Sample(int pid)
        => new(pid, null, 100, $"p{pid}", null, 0, 0, TimeSpan.Zero,
            false, false, false, 0, false, false, null, null, null, null, null, null, null, null);

    private static HardwareMetricSnapshot Hardware(double deviceValue)
    {
        const string totalId = "gpu.0.vram";
        var dataset = SamplingDatasetIds.ForSystemMetric(totalId);
        return new(Captured, HardwareMetricTestData.EmptyCpu, HardwareMetricTestData.EmptyMemory,
            HardwareMetricTestData.EmptyVirtualMemory,
            [new GpuMetrics(0, "recorded GPU", 99, 0, 0, 0, 0, 10, 100, 10, null!) { AdapterKey = Luid }],
            HardwareMetricTestData.EmptyGpuInventory,
            new Dictionary<string, MetricReadingValue> { [totalId] = new(totalId, deviceValue, "B", null, 100) })
        {
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
            { [dataset] = new(dataset, SamplingObservationStatus.Current, 1, Captured.UtcTicks, 1, 1, 1, 1) }
        };
    }
}
