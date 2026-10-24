using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Native;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMetricSnapshotObservationProjectionTests
{
    [Fact]
    public void WarmProjectionAllocatesFreshRequiredResultsWithoutBitmaskDuplicateValues()
    {
        const int iterations = 32;
        const ulong workspaceIdentity = 42;
        var bitmaskFrame = ProjectionFixture(bitmask: true);
        var formattedFrame = ProjectionFixture(bitmask: false);

        _ = NativeMetricSnapshotCommittedProjection.Create(bitmaskFrame, "CPU", "RAM", workspaceIdentity);
        _ = NativeMetricSnapshotCommittedProjection.Create(formattedFrame, "CPU", "RAM", workspaceIdentity);
        var bitmaskSnapshots = new HardwareMetricSnapshot[iterations];
        var beforeBitmask = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < bitmaskSnapshots.Length; i++)
            bitmaskSnapshots[i] = NativeMetricSnapshotCommittedProjection.Create(
                bitmaskFrame, "CPU", "RAM", workspaceIdentity);
        var bitmaskAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeBitmask;

        var formattedSnapshots = new HardwareMetricSnapshot[iterations];
        var beforeFormatted = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < formattedSnapshots.Length; i++)
            formattedSnapshots[i] = NativeMetricSnapshotCommittedProjection.Create(
                formattedFrame, "CPU", "RAM", workspaceIdentity);
        var formattedAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeFormatted;

        var first = bitmaskSnapshots[0];
        var last = bitmaskSnapshots[^1];
        var bitmask = first.Items["cpu.core.status"];
        Assert.Equal(string.Empty, bitmask.DisplayValue);
        Assert.Null(bitmask.NumericValue);
        Assert.Equal("165", bitmask.IntegerValue);
        Assert.Equal(SamplingObservationStatus.Current, first.Datasets["metric.used"].Status);
        Assert.Equal(SamplingObservationStatus.Current, first.Datasets["gpu.0.vram"].Status);
        Assert.Equal(first.Datasets["metric.used"].ObservedAtUtcTicks,
            first.Datasets["gpu.0.vram"].ObservedAtUtcTicks);
        Assert.Equal("hardware-source-unavailable", first.Datasets["offline.metric"].FailureCode);
        var gpu = Assert.Single(first.Gpus);
        Assert.Equal(1_024UL, gpu.UsedMemoryBytes);
        Assert.Equal(4_096UL, gpu.TotalMemoryBytes);
        Assert.Equal(25d, gpu.MemoryUsagePercent);
        Assert.Equal("1024", first.Items["metric.used"].IntegerValue);
        Assert.Equal(first.Items["metric.used"].IntegerValue,
            first.Items["gpu.0.vram"].IntegerValue);
        Assert.Equal(SamplingObservationStatus.Current,
            first.Datasets["gpu.0.vramTotal"].Status);
        Assert.Equal(first.Datasets["metric.used"].ObservedAtUtcTicks,
            first.Datasets["gpu.0.vramTotal"].ObservedAtUtcTicks);
        Assert.NotSame(bitmask, last.Items["cpu.core.status"]);
        Assert.NotSame(first.Datasets["offline.metric"], last.Datasets["offline.metric"]);
        Assert.Equal(bitmask, last.Items["cpu.core.status"]);

        // An unsigned count item formats and discards a temporary display string.
        // A bitmask keeps only its one final MetricValue, so its warmed result
        // allocation must stay below the same projection with that formatted item.
        Assert.True(bitmaskAllocated < formattedAllocated,
            $"Expected one final bitmask MetricValue per projection; bitmask={bitmaskAllocated}, formatted={formattedAllocated}.");
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(-9007199254740993L)]
    public void CommittedSignedAbiValuesPreserveRawIntegerPrecision(long value)
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"signed-frame-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(0, "NVIDIA fixture", "gpu-fixture", 0xA1, 1, 1, true)], [], [], workspace.CreateCatalogHandleMap());
        var binding = catalog.MetricBindings.Values.Single(item => item.FieldCode == ResourceManager.App.Domain.Metrics.MetricFieldCode.NvmlClocksEvents);
        var now = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, now);
        var modes = catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle,
            1, NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var plan = workspace.PlanCollection([binding.MetricHandle], modes, false, now);
        var source = Assert.Single(plan.Sources);
        var metric = plan.Metrics.Single(item => item.MetricHandle == binding.MetricHandle);
        var observation = NativeMetricSnapshotObservationProjection.CreateUnsignedObservation(metric, binding,
            new(source.SourceHandle, 1, 1), now, 1);
        workspace.CompleteSource(plan, new(source.SourceHandle, 1, 1, now,
            NativeMetricSnapshotSourceStatus.Complete, [observation], null, [], 0, 0), now);
        var frame = workspace.ReadCommitted();
        // A committed ABI frame can carry signed values. Keep the original
        // identity/status and exercise the production projection with exact raw bits.
        var output = frame.Metrics.Single(item => item.MetricHandle == binding.MetricHandle);
        output.ValueKind = (uint)NativeMetricSnapshotValueKind.Signed64;
        output.ValueBits = unchecked((ulong)value);
        var signed = binding with { ValueKind = output.ValueKind, Field = binding.Field! with { ValueKind = "signed64", Unit = "count" } };
        var signedFrame = frame with
        {
            Metrics = [.. frame.Metrics.Select(item => item.MetricHandle == binding.MetricHandle ? output : item)],
            Catalog = frame.Catalog with
            {
                RuleBindings = frame.Catalog.RuleBindings.SetItem(binding.RuleHandle, signed),
                MetricBindings = frame.Catalog.MetricBindings.SetItem(binding.MetricHandle, signed)
            }
        };
        var snapshot = NativeMetricSnapshotCommittedProjection.Create(signedFrame, "CPU", "RAM", 1);
        Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), snapshot.Items[binding.MetricId].IntegerValue);
    }

    [Fact]
    public void CurrentValueMustSatisfyTheExactCatalogRange()
    {
        var observation = CreateObservation(
            NativeMetricSnapshotValueKind.Float64,
            NativeMetricSnapshotMetricFlags.Percentage
                | NativeMetricSnapshotMetricFlags.Nonnegative,
            DoubleBits(0),
            DoubleBits(100),
            101);

        Assert.Equal(
            (uint)NativeMetricSnapshotObservationStatus.Unavailable,
            observation.Status);
        Assert.Equal(
            (ulong)NativeMetricSnapshotObservationValidity.Required,
            observation.ValidMask);
        Assert.Equal(0UL, observation.ValueBits);
    }

    [Fact]
    public void ValidCurrentValuePreservesItsExactBits()
    {
        var observation = CreateObservation(
            NativeMetricSnapshotValueKind.Float64,
            NativeMetricSnapshotMetricFlags.Percentage
                | NativeMetricSnapshotMetricFlags.Nonnegative,
            DoubleBits(0),
            DoubleBits(100),
            73.25);

        Assert.Equal(
            (uint)NativeMetricSnapshotObservationStatus.Current,
            observation.Status);
        Assert.Equal(
            (ulong)(
                NativeMetricSnapshotObservationValidity.Required
                | NativeMetricSnapshotObservationValidity.Value),
            observation.ValidMask);
        Assert.Equal(
            BitConverter.DoubleToUInt64Bits(73.25),
            observation.ValueBits);
    }

    [Fact]
    public void IntegerEncodingOverflowIsUnavailable()
    {
        var observation = CreateObservation(
            NativeMetricSnapshotValueKind.Signed64,
            NativeMetricSnapshotMetricFlags.None,
            unchecked((ulong)long.MinValue),
            unchecked((ulong)long.MaxValue),
            double.MaxValue);

        Assert.Equal(
            (uint)NativeMetricSnapshotObservationStatus.Unavailable,
            observation.Status);
        Assert.Equal(0UL, observation.ValueBits);
    }

    private static NativeMetricSnapshotObservationInput CreateObservation(
        NativeMetricSnapshotValueKind valueKind,
        NativeMetricSnapshotMetricFlags metricFlags,
        ulong minimumValueBits,
        ulong maximumValueBits,
        double value)
    {
        const ulong ruleHandle = 11;
        const ulong metricHandle = 12;
        const ulong sourceHandle = 13;
        const ulong scopeHandle = 14;
        var metric = new NativeMetricSnapshotMetricPlanOutput
        {
            RuleHandle = ruleHandle,
            MetricHandle = metricHandle,
            SourceHandle = sourceHandle,
            ScopeHandle = scopeHandle,
            ValueKind = (uint)valueKind
        };
        var binding = new NativeMetricSnapshotRuleCatalogBinding(
            ruleHandle,
            metricHandle,
            sourceHandle,
            scopeHandle,
            "test.metric",
            "test.source",
            (uint)NativeMetricSnapshotMetricKind.CustomNumeric,
            (uint)valueKind,
            1,
            (uint)metricFlags,
            minimumValueBits,
            maximumValueBits);
        return NativeMetricSnapshotObservationProjection.CreateObservation(
            metric,
            binding,
            new NativeMetricSnapshotSourceObservationIdentity(
                sourceHandle,
                1,
                1),
            DateTimeOffset.UtcNow,
            value);
    }

    private static ulong DoubleBits(double value)
        => BitConverter.DoubleToUInt64Bits(value);

    private static NativeMetricSnapshotCommittedFrame ProjectionFixture(bool bitmask)
    {
        const ulong gpuSource = 10;
        const ulong cpuSource = 20;
        const ulong usedHandle = 101;
        const ulong totalHandle = 102;
        const ulong bitmaskHandle = 103;
        const ulong offlineHandle = 104;
        const ulong gpuScope = 1;
        const ulong capturedAt = 1_000;
        var used = Binding(usedHandle, gpuSource, gpuScope, "metric.used", "amd.adlx",
            MetricFieldCode.GpuMemoryUsed, MetricResource.Gpu, MetricVendor.Amd, "B", "gpu.used") with
        { DisplayIndex = 0, LegacyMetricId = "gpu.0.vram", DeviceIdentity = "gpu-a" };
        var total = Binding(totalHandle, gpuSource, gpuScope, "gpu.0.vramTotal", "amd.adlx",
            MetricFieldCode.GpuMemoryTotal, MetricResource.Gpu, MetricVendor.Amd, "B", "gpu.total") with
        { DisplayIndex = 0, LegacyMetricId = "gpu.0.vramTotal", DeviceIdentity = "gpu-a" };
        var bitmaskField = new MetricFieldDefinition(
            MetricFieldCode.IntelCoreThermalStatus,
            "cpu_intel_core_thermal_status_raw",
            MetricResource.Cpu,
            MetricVendor.Intel,
            bitmask ? "bitmask64" : "count",
            "core.{core}.intelThermalStatus")
        { ValueKind = "unsigned64" };
        var bitmaskBinding = new NativeMetricSnapshotRuleCatalogBinding(
            bitmaskHandle, bitmaskHandle, cpuSource, 0, "cpu.core.status", "intel.msr",
            (uint)NativeMetricSnapshotMetricKind.CustomNumeric,
            (uint)NativeMetricSnapshotValueKind.Unsigned64, 0, 0, 0, ulong.MaxValue)
        { Field = bitmaskField, DeviceIdentity = "cpu-core-0" };
        var offline = Binding(offlineHandle, cpuSource, 0, "offline.metric", "intel.msr",
            MetricFieldCode.CpuTemperature, MetricResource.Cpu, MetricVendor.Intel, "°C", "cpu.temperature")
            with { DeviceIdentity = "cpu" };
        var bindings = new[] { used, total, bitmaskBinding, offline };
        var metricIds = bindings.ToImmutableDictionary(static row => row.MetricHandle, static row => row.MetricId);
        var sourceIds = ImmutableDictionary<ulong, string>.Empty
            .Add(gpuSource, "amd.adlx")
            .Add(cpuSource, "intel.msr");
        var catalog = new NativeMetricSnapshotCatalogProjection(
            [], [],
            metricIds,
            metricIds.ToImmutableDictionary(static pair => pair.Value, static pair => pair.Key, StringComparer.OrdinalIgnoreCase),
            sourceIds,
            sourceIds.ToImmutableDictionary(static pair => pair.Value, static pair => pair.Key, StringComparer.Ordinal),
            bindings.ToImmutableDictionary(static row => row.RuleHandle),
            ImmutableDictionary<ulong, NativeMetricSnapshotGpuScopeBinding>.Empty.Add(
                gpuScope, new(0, "GPU A", gpuScope, 0xA1, 1, "gpu-a", true)),
            NativeMetricSnapshotCatalogHandleMap.Empty,
            1,
            "projection-allocation-fixture")
        {
            MetricBindings = bindings.ToImmutableDictionary(static row => row.MetricHandle),
            LegacyMetricHandles = ImmutableDictionary<string, ulong>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("gpu.0.vram", usedHandle)
        };

        NativeMetricSnapshotMetricOutput Output(
            ulong handle, ulong source, NativeMetricSnapshotMetricStatus status, ulong valueBits)
            => new()
            {
                MetricHandle = handle,
                SourceHandle = source,
                SourceGeneration = 1,
                Status = (uint)status,
                ValueKind = (uint)NativeMetricSnapshotValueKind.Unsigned64,
                ValueBits = valueBits,
                ObservedAtMilliseconds = capturedAt
            };

        return new(
            new NativeMetricSnapshotSnapshotHeader
            {
                CapturedAtMilliseconds = capturedAt,
                ConfigurationGeneration = 1,
                CatalogGeneration = 1,
                CommittedGeneration = 1
            },
            [
                new NativeMetricSnapshotSourceOutput
                {
                    SourceHandle = gpuSource,
                    SourceGeneration = 1,
                    Status = (uint)NativeMetricSnapshotSourceStatus.Complete,
                    LastCurrentAtMilliseconds = capturedAt
                },
                new NativeMetricSnapshotSourceOutput
                {
                    SourceHandle = cpuSource,
                    SourceGeneration = 1,
                    Status = (uint)NativeMetricSnapshotSourceStatus.Unavailable
                }
            ],
            [
                Output(usedHandle, gpuSource, NativeMetricSnapshotMetricStatus.Current, 1_024),
                Output(totalHandle, gpuSource, NativeMetricSnapshotMetricStatus.Current, 4_096),
                Output(bitmaskHandle, cpuSource, NativeMetricSnapshotMetricStatus.Current, 165),
                Output(offlineHandle, cpuSource, NativeMetricSnapshotMetricStatus.Unavailable, 0)
            ],
            [], [], catalog);

        static NativeMetricSnapshotRuleCatalogBinding Binding(
            ulong handle, ulong source, ulong scope, string metricId, string sourceId,
            MetricFieldCode code, MetricResource resource, MetricVendor vendor, string unit, string fieldName)
            => new(handle, handle, source, scope, metricId, sourceId,
                (uint)NativeMetricSnapshotMetricKind.CustomNumeric,
                (uint)NativeMetricSnapshotValueKind.Unsigned64, 0, 0, 0, ulong.MaxValue)
            {
                Field = new MetricFieldDefinition(code, fieldName, resource, vendor, unit, metricId)
                { ValueKind = "unsigned64" },
                DeviceIdentity = metricId,
                DisplayIndex = scope == 0 ? -1 : 0
            };
    }
}
