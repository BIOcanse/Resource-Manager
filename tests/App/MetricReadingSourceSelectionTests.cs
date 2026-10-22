using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Fields;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;

namespace Resource_Manager_APP.Tests;

public sealed class MetricReadingSourceSelectionTests
{
    [Fact]
    public void PreferredSourceAndLegacySchedulingAddressUseTheSameDecisionWithoutRemovingRawFields()
    {
        var frame = Frame(Temperature(1, 10, "amd.smu"), Temperature(2, 20, "amd.adlx"));
        var tracker = new MetricReadingSourceTracker();
        tracker.Observe(frame, 1, [Complete(10, 1), Complete(20, 2)]);
        var selection = tracker.Select(frame, 1);
        Assert.Equal(2, selection.Fields.Count);
        Assert.Equal(selection.Fields[1].Id, selection.Fields[2].Id);
        Assert.True(selection.Fields[1].IsPreferredSource);
        Assert.False(selection.Fields[2].IsPreferredSource);
        Assert.Equal(1UL, selection.LegacyHandles["gpu.0.temperature"]);
        Assert.Equal("field-1", selection.Fields[2].PreferredFieldName);
    }

    [Fact]
    public void PreferenceDependsOnDiscoveredSupportAndDoesNotSwitchOnATransientMissingSample()
    {
        var frame = Frame(Temperature(1, 10, "amd.smu"), Temperature(2, 20, "amd.adlx"));
        var tracker = new MetricReadingSourceTracker();
        Assert.All(tracker.Select(frame, 1).Fields.Values, r =>
        {
            Assert.Equal("unverified", r.SourceAvailability);
            Assert.Null(r.PreferredFieldName);
        });
        tracker.Observe(frame, 1, [Complete(20, 2)]);
        Assert.True(tracker.Select(frame, 1).Fields[2].IsPreferredSource);
        tracker.Observe(frame, 1, [Complete(10, 1)]);
        tracker.Observe(frame, 1, [Complete(10) with { Status = NativeMetricSnapshotSourceStatus.Unavailable }]);
        var selected = tracker.Select(frame, 1);
        Assert.True(selected.Fields[1].IsPreferredSource);
        Assert.Equal("available", selected.Fields[1].SourceAvailability);
        Assert.Equal(1UL, selected.LegacyHandles["gpu.0.temperature"]);
    }

    [Fact]
    public void CapabilityChangeAndWorkspaceReplacementInvalidateOldSupport()
    {
        var frame = Frame(Temperature(1, 10, "amd.smu"), Temperature(2, 20, "amd.adlx"));
        var tracker = new MetricReadingSourceTracker();
        tracker.Observe(frame, 1, [Complete(10, 1), Complete(20, 2)]);
        var changed = frame with { Sources = frame.Sources.Select(s => s.SourceHandle == 10
            ? s with { CapabilityGeneration = 2 } : s).ToImmutableArray() };
        Assert.Equal("unverified", tracker.Select(changed, 1).Fields[1].SourceAvailability);
        Assert.True(tracker.Select(changed, 1).Fields[2].IsPreferredSource);
        Assert.All(tracker.Select(changed, 2).Fields.Values, r => Assert.Equal("unverified", r.SourceAvailability));
    }

    [Fact]
    public void CrossDeviceApuAliasNeedsTheStructuredRouteAndPrefersTheGpuPresentation()
    {
        var cpu = Binding(1, 10, "amd.smu", MetricFieldCode.CpuIgpuTemperature, "cpu-opaque");
        var gpu = Temperature(2, 10, "amd.smu");
        var frame = Frame(cpu, gpu);
        var tracker = new MetricReadingSourceTracker();
        Assert.NotEqual(tracker.Select(frame, 1).Fields[1].Id, tracker.Select(frame, 1).Fields[2].Id);
        tracker.Observe(frame, 1, [Complete(10, 1) with { IntegratedGpuDeviceIdentity = "gpu-opaque" }]);
        var selected = tracker.Select(frame, 1);
        Assert.Equal(selected.Fields[1].Id, selected.Fields[2].Id);
        Assert.True(selected.Fields[2].IsPreferredSource);
        Assert.Equal("gpu-opaque", selected.Fields[1].PreferredDeviceIdentity);
    }

    [Fact]
    public void AdlxIntegerAndConvertedBytesShareOneReadingAndNeitherBecomesAnotherSourcesQuantity()
    {
        var bytes = Binding(1, 10, "amd.adlx", MetricFieldCode.GpuMemoryUsed, "gpu-opaque");
        var integer = Binding(2, 10, "amd.adlx", MetricFieldCode.AdlxDedicatedMemoryMb, "gpu-opaque");
        var reserved = Binding(3, 20, "nvidia.nvml", MetricFieldCode.NvmlFbReserved, "gpu-opaque");
        var frame = Frame(bytes, integer, reserved);
        var tracker = new MetricReadingSourceTracker();
        tracker.Observe(frame, 1, [Complete(10, 2)]);
        var selected = tracker.Select(frame, 1);
        Assert.Equal(selected.Fields[1].Id, selected.Fields[2].Id);
        Assert.True(selected.Fields[1].IsPreferredSource);
        Assert.NotEqual(selected.Fields[1].Id, selected.Fields[3].Id);
    }

    [Fact]
    public void SeparateDevicesAndHotspotStaySeparateEvenWhenTheyHaveTheSameNumericTemperature()
    {
        var frame = Frame(Temperature(1, 10, "amd.smu"), Temperature(2, 20, "amd.adlx") with { DeviceIdentity = "another-gpu" },
            Binding(3, 20, "amd.adlx", MetricFieldCode.GpuHotspotTemperature, "gpu-opaque"));
        Assert.Equal(3, new MetricReadingSourceTracker().Select(frame, 1).Fields.Values.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void UnsupportedLegacyTargetProducesAnEmptyRequestRatherThanAnImplicitAllRequest()
    {
        var frame = Frame(Temperature(1, 10, "amd.smu"));
        var tracker = new MetricReadingSourceTracker();
        tracker.Observe(frame, 1, [Complete(10) with { Status = NativeMetricSnapshotSourceStatus.Unsupported }]);
        var request = tracker.ExpandLegacyRequest(MetricSampleRequest.ForIds(["gpu.0.temperature"]), frame, 1);
        Assert.True(request.IsEmpty);
        Assert.False(request.IsAll);
    }

    [Fact]
    public void LegacyExpansionPreservesExplicitAllGpuCoreDemand()
    {
        var frame = Frame(Temperature(1, 10, "amd.smu"));
        var request = new MetricReadingSourceTracker().ExpandLegacyRequest(
            MetricSampleRequest.ForIdsAndAllGpuCoreMetrics([]), frame, 1);
        Assert.True(request.IncludesAllGpuCoreMetrics);
        Assert.False(request.IsAll);
    }

    [Fact]
    public void HttpSensorQuantityIsReverifiedAfterItsSourceCapabilityChanges()
    {
        var binding = Binding(1, 10, "hardware-monitor.core", MetricFieldCode.CpuTemperature, "cpu-opaque");
        var frame = Frame(binding);
        var tracker = new MetricReadingSourceTracker();
        tracker.Observe(frame, 1, [Complete(10, 1) with
        { ReadingQuantities = ImmutableDictionary<ulong, string>.Empty.Add(1, "cpu.control_temperature") }]);
        Assert.Equal("cpu.control_temperature", tracker.Select(frame, 1).Fields[1].Quantity);
        var changed = frame with { Sources = frame.Sources.Select(s => s with { CapabilityGeneration = 2 }).ToImmutableArray() };
        Assert.Equal("cpu.temperature_reported", tracker.Select(changed, 1).Fields[1].Quantity);
    }

    [Fact]
    public void MetadataMergePreservesCatalogShapeValuesAndOfflineControlBindings()
    {
        var field = new MetricFieldDefinition(MetricFieldCode.GpuTemperature, "test", MetricResource.Gpu, MetricVendor.Amd, "°C", "raw");
        var binding = new MetricDeviceFieldBinding("opaque", field, "raw", "amd.smu");
        var original = Snapshot(new Dictionary<string, MetricDeviceFieldBinding> { ["raw"] = binding, ["offline"] = binding with { RequestAddress = "offline" } });
        var reading = new MetricReadingSource("reading", "gpu.edge_temperature", 10, "available", true, "opaque", "test");
        var update = original with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            { ["raw"] = binding with { Field = field with { Reading = reading } } } };
        var merged = MetricReadingCatalog.Update(original, update);
        Assert.Equal(2, merged.FieldBindings.Count);
        Assert.Equal(reading, merged.FieldBindings["raw"].Field.Reading);
        Assert.Same(original.Items, merged.Items);
        Assert.Same(original, MetricReadingCatalog.Update(original, update with { WorkspaceIdentity = 9 }));
    }

    [Theory]
    [InlineData(MetricFieldCode.IgclGpuTemperature, "temperature")]
    [InlineData(MetricFieldCode.IgclGpuIntervalPower, "power")]
    [InlineData(MetricFieldCode.IgclGpuVoltage, "coreVoltage")]
    public void IntelGpuComponentDemandAndPublicPreferenceUseTheSameRawField(MetricFieldCode code, string suffix)
    {
        var raw = Binding(1, 10, "intel.igcl", code, "intel-gpu") with { DisplayIndex = 2 };
        var frame = Frame(raw);
        var tracker = new MetricReadingSourceTracker();
        var component = $"gpu.2.{suffix}";
        Assert.Contains(raw.MetricId, tracker.ExpandLegacyRequest(MetricSampleRequest.ForIds([component]), frame, 1).Ids);
        tracker.Observe(frame, 1, [Complete(10, 1)]);
        var selected = tracker.Select(frame, 1);
        Assert.True(selected.Fields[1].IsPreferredSource);
        Assert.Equal(1UL, selected.LegacyHandles[component]);
        Assert.Equal(new[] { raw.MetricId }, tracker.ExpandLegacyRequest(MetricSampleRequest.ForIds([component]), frame, 1).Ids);
        tracker.Observe(frame, 1, [Complete(10) with { Status = NativeMetricSnapshotSourceStatus.Unavailable }]);
        Assert.Equal(1UL, tracker.Select(frame, 1).LegacyHandles[component]);
    }

    [Fact]
    public void SingularCpuTemperatureUsesStructuredFirstPackageWithoutMergingOtherPackagesOrCores()
    {
        var first = Binding(1, 10, "intel.msr", MetricFieldCode.IntelPackageDtsTemperature, "package-a") with
        { Device = new MetricDeviceDescriptor("cpu_package", null, null, 0) };
        var second = Binding(2, 10, "intel.msr", MetricFieldCode.IntelPackageDtsTemperature, "package-b") with
        { Device = new MetricDeviceDescriptor("cpu_package", null, null, 1) };
        var core = Binding(3, 10, "intel.msr", MetricFieldCode.IntelCoreDtsTemperature, "core-a");
        var frame = Frame(first, second, core);
        var tracker = new MetricReadingSourceTracker();
        tracker.Observe(frame, 1, [Complete(10, 1, 2, 3)]);
        var selected = tracker.Select(frame, 1);
        Assert.Equal(1UL, selected.LegacyHandles["cpu.temperature"]);
        Assert.Equal(3, selected.Fields.Values.Select(r => r.Id).Distinct().Count());
        Assert.Equal(new[] { first.MetricId }, tracker.ExpandLegacyRequest(MetricSampleRequest.ForIds(["cpu.temperature"]), frame, 1).Ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdlxRawChipPowerDoesNotBecomeIntegratedGraphicsComponentPower(bool dedicated)
    {
        var projection = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            HostManagerTestPlanFactory.CreatePlan().MetricSnapshot,
            [new NativeMetricSnapshotGpuCatalogIdentity(0, "AMD", "exact-amd", 1, 1, 2, dedicated)],
            [], [], NativeMetricSnapshotCatalogHandleMap.Empty);
        var raw = Assert.Single(projection.MetricBindings.Values, row => row.SourceId == "amd.adlx"
            && row.FieldCode == MetricFieldCode.GpuPower);
        Assert.Equal(dedicated ? "gpu.0.power" : string.Empty, raw.LegacyMetricId);
        Assert.Equal(dedicated, projection.LegacyMetricHandles.ContainsKey("gpu.0.power"));
        Assert.NotNull(raw.Field);
    }

    [Fact]
    public void RealNativeCpuCounterCommitPublishesTheSamePreferredRawAndComponentReading()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"cpu-preferred-counter-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], workspace.CreateCatalogHandleMap());
        var binding = Assert.Single(catalog.MetricBindings.Values, row => row.FieldCode == MetricFieldCode.CpuUsage);
        var tracker = new MetricReadingSourceTracker();
        var at = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, at);
        var runtime = new NativeMetricSnapshotSourceRuntimeFacts(static _ => NativeMetricSnapshotZoneMode.Normal, TimeSpan.FromSeconds(5));
        for (ulong step = 1; step <= 2; step++)
        {
            var time = at.AddSeconds(step);
            var plan = workspace.PlanCollection([binding.MetricHandle], runtime.CaptureModes(catalog, time), false, time);
            var source = Assert.Single(plan.Sources);
            var identity = new NativeMetricSnapshotSourceObservationIdentity(source.SourceHandle, 1, step);
            var metric = Assert.Single(plan.Metrics);
            var counter = NativeMetricSnapshotObservationProjection.CreateCpuCounter(metric, binding, identity,
                new(true, step == 1 ? 50UL : 70UL, step == 1 ? 80UL : 130UL, step == 1 ? 20UL : 70UL,
                    step * 1000, 1000, time));
            var completion = new NativeMetricSnapshotSourceCompletion(source.SourceHandle, 1, step, time,
                NativeMetricSnapshotSourceStatus.Complete, [], counter, [], 0, 0);
            workspace.CompleteSource(plan, completion, time);
            tracker.Observe(workspace.ReadCommitted(), 1, [completion]);
        }
        var frame = workspace.ReadCommitted();
        var selected = tracker.Select(frame, 1);
        var snapshot = NativeMetricSnapshotCommittedProjection.Create(frame, "CPU", "RAM", 1, "", null, selected);
        Assert.True(snapshot.Cpu.IsUsageAvailable);
        Assert.Equal(80d, snapshot.Cpu.UsagePercent);
        Assert.True(snapshot.FieldBindings[binding.MetricId].Field.Reading!.IsPreferredSource);
        Assert.Equal("available", snapshot.FieldBindings[binding.MetricId].Field.Reading!.SourceAvailability);
        Assert.Equal(binding.MetricHandle, selected.LegacyHandles["cpu.usage"]);
    }

    [Fact]
    public void CommittedCpuCounterEstablishesSupportAndKeepsTheComponentAliasThroughMissingSamples()
    {
        var binding = Binding(1, 10, "windows.cpu", MetricFieldCode.CpuUsage, "static|cpu")
            with { LegacyMetricId = "cpu.usage" };
        var frame = Frame(binding);
        var tracker = new MetricReadingSourceTracker();
        // CPU native completion contains a counter, not ordinary observations.
        frame = frame with { Metrics = [new NativeMetricSnapshotMetricOutput
            { MetricHandle = 1, SourceHandle = 10, SourceGeneration = 7,
                Status = (uint)NativeMetricSnapshotMetricStatus.Current }],
            Sources = frame.Sources.Select(row => row with { SourceGeneration = 7 }).ToImmutableArray() };
        tracker.Observe(frame, 1, [Complete(10)]);
        var selected = tracker.Select(frame, 1);
        Assert.True(selected.Fields[1].IsPreferredSource);
        Assert.Equal("available", selected.Fields[1].SourceAvailability);
        Assert.Equal(1UL, selected.LegacyHandles["cpu.usage"]);
        var projected = NativeMetricSnapshotCommittedProjection.Create(frame, "CPU", "RAM", 1, "", null, selected);
        Assert.True(projected.FieldBindings[binding.MetricId].Field.Reading!.IsPreferredSource);
        tracker.Observe(frame with { Metrics = [] }, 1,
            [Complete(10) with { Status = NativeMetricSnapshotSourceStatus.Unavailable }]);
        Assert.Equal(1UL, tracker.Select(frame, 1).LegacyHandles["cpu.usage"]);
        var changed = frame with { Sources = frame.Sources.Select(row => row with { CapabilityGeneration = 2 }).ToImmutableArray() };
        Assert.Equal("unverified", tracker.Select(changed, 1).Fields[1].SourceAvailability);
    }

    [Fact]
    public void OldCounterOrUnacceptedCompletionCannotConfirmSupport()
    {
        var frame = Frame(Binding(1, 10, "windows.cpu", MetricFieldCode.CpuUsage, "static|cpu"));
        frame = frame with { Metrics = [new NativeMetricSnapshotMetricOutput
            { MetricHandle = 1, SourceHandle = 10, SourceGeneration = 6,
                Status = (uint)NativeMetricSnapshotMetricStatus.Current }],
            Sources = frame.Sources.Select(row => row with { SourceGeneration = 7 }).ToImmutableArray() };
        var tracker = new MetricReadingSourceTracker();
        tracker.Observe(frame, 1, [Complete(10)]);
        Assert.Equal("unverified", tracker.Select(frame, 1).Fields[1].SourceAvailability);
        frame = frame with { Metrics = frame.Metrics.Select(row => row with { SourceGeneration = 7 }).ToImmutableArray() };
        tracker.Observe(frame, 1, [Complete(10) with { SourceObservationSequence = 99 }]);
        Assert.Equal("unverified", tracker.Select(frame, 1).Fields[1].SourceAvailability);
    }

    private static NativeMetricSnapshotRuleCatalogBinding Temperature(ulong handle, ulong source, string sourceId)
        => Binding(handle, source, sourceId, MetricFieldCode.GpuTemperature, "gpu-opaque") with { LegacyMetricId = "gpu.0.temperature" };

    private static NativeMetricSnapshotRuleCatalogBinding Binding(ulong handle, ulong source, string sourceId, MetricFieldCode code, string device)
        => new(handle, handle, source, 1, "raw-" + handle, sourceId, 1, 1, 0, 0, 0, 0)
        { DeviceIdentity = device, Field = new(code, "field-" + handle, MetricResource.Gpu, MetricVendor.Amd, "unit", "raw") };

    private static NativeMetricSnapshotCommittedFrame Frame(params NativeMetricSnapshotRuleCatalogBinding[] bindings)
    {
        var metrics = bindings.ToImmutableDictionary(b => b.MetricHandle, b => b.MetricId);
        var sources = bindings.GroupBy(b => b.SourceHandle).ToImmutableDictionary(g => g.Key, g => g.First().SourceId);
        var catalog = new NativeMetricSnapshotCatalogProjection([], [], metrics,
            metrics.ToImmutableDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase), sources,
            sources.ToImmutableDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal),
            bindings.ToImmutableDictionary(b => b.RuleHandle), ImmutableDictionary<ulong, NativeMetricSnapshotGpuScopeBinding>.Empty,
            NativeMetricSnapshotCatalogHandleMap.Empty, 1, "fixture")
        { MetricBindings = bindings.ToImmutableDictionary(b => b.MetricHandle),
            LegacyMetricHandles = bindings.Where(b => b.LegacyMetricId.Length != 0).GroupBy(b => b.LegacyMetricId)
                .ToImmutableDictionary(g => g.Key, g => g.First().MetricHandle, StringComparer.OrdinalIgnoreCase) };
        return new(new() { ConfigurationGeneration = 1, CatalogGeneration = 1, CommittedGeneration = 1 },
            sources.Keys.Select(s => new NativeMetricSnapshotSourceOutput { SourceHandle = s, SourceIncarnation = s,
                SourceObservationSequence = 1, CapabilityGeneration = 1, Priority = (uint)s }).ToImmutableArray(), [], [], [], catalog);
    }

    private static NativeMetricSnapshotSourceCompletion Complete(ulong source, params ulong[] available)
        => new(source, source, 1, DateTimeOffset.UtcNow, NativeMetricSnapshotSourceStatus.Complete, [], null, [], 0, 0)
        { SupportedMetricHandles = available.ToImmutableArray() };

    private static HardwareMetricSnapshot Snapshot(IReadOnlyDictionary<string, MetricDeviceFieldBinding> fields)
        => new(DateTimeOffset.UtcNow, null!, null!, null!, [], new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, []),
            new Dictionary<string, MetricValue>()) { WorkspaceIdentity = 1, ConfigurationGeneration = 1, CatalogGeneration = 1, FieldBindings = fields };
}
