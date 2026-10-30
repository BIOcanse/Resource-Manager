using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class AdlxMetricsCollectorTests
{
    private static readonly MetricFieldCode[] AvailabilityCodes = [MetricFieldCode.GpuGraphicsClock,
        MetricFieldCode.GpuGraphicsClockPercent, MetricFieldCode.GpuMemoryClock, MetricFieldCode.GpuMemoryUsed,
        MetricFieldCode.GpuMemoryTotal, MetricFieldCode.GpuMemoryPercent];
    private static AmdAdlxReadRequest AvailabilityRequest() => Request(9, 3) with
        { IncludeGraphicsClocks = true, IncludeMemoryClock = true, IncludeMemory = true };
    private static NativeAdlxGpuMetrics AvailabilityRow(int id, string pnp) => Row(id, pnp, 2 | 4 | 64) with
        { GraphicsClockMhz = 1200, MaxGraphicsClockMhz = 2000, MemoryClockMhz = 1800, VramUsedMb = 128, TotalVramMb = 8192 };

    [Fact]
    public async Task IntegratedAdapterReadsSupportedAdlxSensorsThroughOriginalWorkspaceAndSelection()
    {
        const string apuPnp = @"PCI\VEN_1002&DEV_164E&SUBSYS_12345678&REV_D8\RECORDED_610M";
        var inventory = Inventory() with { Adapters = Inventory().Adapters.Where(adapter => adapter.Index == 9)
            .Select(adapter => adapter with { Kind = WindowsGpuAdapterKind.Integrated, DedicatedVideoMemoryBytes = 0,
                DeviceId = 0x164E, SubSystemId = 0x12345678,
                PhysicalIdentities = adapter.PhysicalIdentities.Select(identity => identity with
                    { PnpInstanceId = apuPnp, DeviceId = 0x164E, SubVendorId = 0x5678, SubsystemId = 0x1234,
                        RevisionId = 0xD8 }).ToArray() }).ToArray(),
            ObservedCount = 1 };
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"adlx-integrated-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(9, "AMD Radeon 610M", "gpu-apu", 0xA1, 1, 2, false)], [], [], workspace.CreateCatalogHandleMap());
        var codes = new[] { MetricFieldCode.GpuPower, MetricFieldCode.GpuBoardPower, MetricFieldCode.GpuTemperature,
            MetricFieldCode.GpuHotspotTemperature, MetricFieldCode.GpuIntakeTemperature, MetricFieldCode.GpuVoltage,
            MetricFieldCode.GpuGraphicsClock, MetricFieldCode.GpuGraphicsClockPercent, MetricFieldCode.GpuMemoryClock };
        var bindings = catalog.MetricBindings.Values.Where(binding => binding.SourceId == "amd.adlx" && codes.Contains(binding.FieldCode)).ToArray();
        Assert.Equal(codes.Length, bindings.Length);
        var now = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, now);
        var modes = catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeState(source.SourceHandle, 1,
            NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var plan = workspace.PlanCollection(bindings.Select(binding => binding.MetricHandle).ToArray(), modes, false, now);
        var source = Assert.Single(plan.Sources);
        var row = AvailabilityRow(10, apuPnp) with { AvailableFlags = 2 | 4 | 8 | 16 | 32 | 128 | 512 | 1024,
            SupportedFlags = 2 | 4 | 8 | 16 | 32 | 128 | 512 | 1024,
            PowerWatts = 5, BoardPowerWatts = 7, TemperatureCelsius = 45, HotspotTemperatureCelsius = 51,
            IntakeTemperatureCelsius = 30, VoltageMillivolts = 850 };
        var native = new Bridge { Metadata = [row], Values = [row] };
        var values = await new AmdAdlxBridgeReader(native).ReadAsync(
            WindowsHardwareMetricSampler.CreateAdlxReadRequest(bindings), inventory, default);
        Assert.Single(values);
        var completion = WindowsHardwareMetricSampler.CreateAdlxCompletion(plan, source,
            new(source.SourceHandle, 1, 1), now, values);
        workspace.CompleteSource(plan, completion, now);
        var tracker = new ResourceManager.ReadWrite.Windows.Metrics.Capture.MetricReadingSourceTracker();
        var frame = workspace.ReadCommitted();
        tracker.Observe(frame, 1, [completion]);
        var snapshot = NativeMetricSnapshotCommittedProjection.Create(frame, 1, tracker.Select(frame, 1));
        var definitions = MetricCatalog.FromSnapshot(snapshot);
        foreach (var binding in bindings)
        {
            var definition = definitions.Single(item => item.Id == binding.LegacyMetricId);
            Assert.True(definition.Selectable);
            var selected = Assert.Single(definition.CandidateSources, candidate => candidate.IsSelected);
            Assert.Equal("amd.adlx", selected.SourceId);
            Assert.Equal("available", selected.SourceAvailability);
        }
        Assert.Equal(5d, snapshot.Items["gpu.9.power"].NumericValue);
        Assert.Equal(1800d, snapshot.Items["gpu.9.memoryClock"].NumericValue);
        Assert.Equal(0.85d, snapshot.Items["gpu.9.coreVoltage"].NumericValue);
        var powerSource = Assert.Single(definitions.Single(item => item.Id == "gpu.9.power").CandidateSources,
            candidate => candidate.IsSelected);
        Assert.Equal("gpu.chip_power_reported", powerSource.Reading!.Quantity);
        Assert.Equal(10, powerSource.Reading.SourceRank);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(definitions.Single(item => item.Id == "gpu.9.power")));
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)] [InlineData(0, 3)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)] [InlineData(1, 3)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)] [InlineData(2, 3)]
    public void AdlxNullableReadingsDistinguishSuccessZeroFailureAndNegative(int field, int mode)
    {
        var row = AvailabilityRow(10, PnpA);
        var code = field switch { 0 => MetricFieldCode.GpuGraphicsClock, 1 => MetricFieldCode.GpuMemoryClock, _ => MetricFieldCode.GpuMemoryUsed };
        var flag = field switch { 0 => 2, 1 => 4, _ => 64 };
        if (mode == 1) row.AvailableFlags &= ~flag;
        else if (mode != 3)
        {
            var value = mode == 0 ? 0 : -1;
            if (field == 0) row.GraphicsClockMhz = value;
            else if (field == 1) row.MemoryClockMhz = value;
            else row.VramUsedMb = value;
        }
        var gpu = Assert.IsType<GpuMetrics>(AmdAdlxBridgeReader.ToGpuMetrics(row, AvailabilityRequest(), Inventory()));
        Assert.True(gpu.AdditionalReadings.TryGetValue(code, out var actual));
        double? expected = mode is 1 or 2 ? null : mode == 0 ? 0 : field switch { 0 => 1200, 1 => 1800, _ => 128d * 1024 * 1024 };
        Assert.Equal(expected, actual);
        if (field != 1)
            Assert.Equal(mode is 1 or 2 ? null : mode == 0 ? 0d : field == 0 ? 60d : 128d * 100 / 8192,
                gpu.AdditionalReadings[field == 0 ? MetricFieldCode.GpuGraphicsClockPercent : MetricFieldCode.GpuMemoryPercent]);
    }

    [Fact]
    public void NoMemoryOrClockDemandPublishesExplicitEmptyReadingsEvenIfBufferContainsValues()
    {
        var gpu = Assert.IsType<GpuMetrics>(AmdAdlxBridgeReader.ToGpuMetrics(AvailabilityRow(10, PnpA), Request(9), Inventory()));
        Assert.All(AvailabilityCodes, code => Assert.Null(gpu.AdditionalReadings[code]));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public void MissingClockReferenceDoesNotHideActualClockOrInventPercentage(int reference)
    {
        var row = AvailabilityRow(10, PnpA); row.MaxGraphicsClockMhz = reference;
        var gpu = Assert.IsType<GpuMetrics>(AmdAdlxBridgeReader.ToGpuMetrics(row, AvailabilityRequest(), Inventory()));
        Assert.Equal(1200d, gpu.AdditionalReadings[MetricFieldCode.GpuGraphicsClock]);
        Assert.Null(gpu.AdditionalReadings[MetricFieldCode.GpuGraphicsClockPercent]);
    }

    [Fact]
    public void OriginalWorkspacePreservesZeroClearsIndividualFailuresAndRecoversPerGpu()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"adlx-availability-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(9, "AMD A", "gpu-a", 0xA1, 1, 2, true), new(3, "AMD B", "gpu-b", 0xB1, 1, 2, true)], [], [], workspace.CreateCatalogHandleMap());
        var bindings = catalog.MetricBindings.Values.Where(binding => binding.SourceId == "amd.adlx" && AvailabilityCodes.Contains(binding.FieldCode)).ToArray();
        output.WriteLine("ADLX availability bindings: " + string.Join(", ", bindings.Select(binding => $"{binding.DisplayIndex}:{binding.FieldCode}")));
        Assert.Equal(12, bindings.Length);
        Assert.Equal(2, bindings.Count(binding => binding.FieldCode == MetricFieldCode.GpuMemoryUsed));
        var now = DateTimeOffset.UtcNow; workspace.ReplaceCatalog(catalog, now);
        var modes = catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeState(source.SourceHandle, 1,
            NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        for (var phase = 0; phase < 7; phase++)
        {
            var at = now.AddSeconds(phase + 1);
            var plan = workspace.PlanCollection(bindings.Select(binding => binding.MetricHandle).ToArray(), modes, false, at);
            var source = Assert.Single(plan.Sources);
            var row = AvailabilityRow(10, PnpA);
            if (phase == 1) row.AvailableFlags &= ~64;
            if (phase == 2) { row.GraphicsClockMhz = row.MemoryClockMhz = row.VramUsedMb = 0; }
            if (phase == 3) { row.GraphicsClockMhz = row.MemoryClockMhz = row.VramUsedMb = -1; }
            if (phase == 4) row.TotalVramMb = 0;
            workspace.CompleteSource(plan, WindowsHardwareMetricSampler.CreateAdlxCompletion(plan, source,
                new(source.SourceHandle, 1, (ulong)phase + 1), at, phase == 5 ? [] :
                [AmdAdlxBridgeReader.ToGpuMetrics(row, AvailabilityRequest(), Inventory())!,
                 AmdAdlxBridgeReader.ToGpuMetrics(AvailabilityRow(20, PnpB), AvailabilityRequest(), Inventory())!]), at);
            var snapshot = NativeMetricSnapshotCommittedProjection.Create(frame: workspace.ReadCommitted(), workspaceIdentity: 1);
            foreach (var binding in bindings)
            {
                var template = MetricFieldRegistry.Definitions.First(field => field.Code == binding.FieldCode).RequestTemplate;
                var name = MetricFieldRegistry.FindByTemplate(template, binding.SourceId)!.Name;
                var field = Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot, binding.DeviceIdentity, new HashSet<string> { name })).Fields);
                var changed = binding.DisplayIndex == 9;
                double? expected = phase == 5 ? null : changed && phase == 1 && binding.FieldCode is MetricFieldCode.GpuMemoryUsed or MetricFieldCode.GpuMemoryPercent ? null
                    : changed && phase == 3 && binding.FieldCode != MetricFieldCode.GpuMemoryTotal ? null
                    : changed && phase == 4 && binding.FieldCode is MetricFieldCode.GpuMemoryTotal or MetricFieldCode.GpuMemoryPercent ? null
                    : changed && phase == 2 && binding.FieldCode != MetricFieldCode.GpuMemoryTotal ? 0
                    : binding.FieldCode switch { MetricFieldCode.GpuGraphicsClock => 1200, MetricFieldCode.GpuGraphicsClockPercent => 60,
                        MetricFieldCode.GpuMemoryClock => 1800, MetricFieldCode.GpuMemoryUsed => 128d * 1024 * 1024,
                        MetricFieldCode.GpuMemoryTotal => 8192d * 1024 * 1024, _ => 128d * 100 / 8192 };
                Assert.Equal(expected, field.NumericValue);
                if (expected.HasValue) Assert.Equal(SamplingObservationStatus.Current, field.Status);
                else Assert.NotEqual(SamplingObservationStatus.Current, field.Status);
                using var wire = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(field,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
                var value = wire.RootElement.GetProperty("numericValue");
                if (expected.HasValue) Assert.Equal(expected.Value, value.GetDouble());
                else Assert.Equal(System.Text.Json.JsonValueKind.Null, value.ValueKind);
            }
        }
    }
}
