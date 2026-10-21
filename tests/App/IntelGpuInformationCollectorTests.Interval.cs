using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelGpuInformationCollectorTests
{
    private static readonly MetricFieldCode[] IntervalCodes = [MetricFieldCode.IgclGpuIntervalPower, MetricFieldCode.IgclMemoryIntervalPower,
        MetricFieldCode.IgclBoardIntervalPower, MetricFieldCode.IgclAnyEngineBusy, MetricFieldCode.IgclRenderComputeBusy, MetricFieldCode.IgclAnyMediaEngineBusy];
    private static readonly string[] IntervalNames = ["gpu_intel_gpu_interval_power_watt", "gpu_intel_memory_interval_power_watt", "gpu_intel_board_interval_power_watt",
        "gpu_intel_any_engine_busy_percent", "gpu_intel_render_compute_busy_percent", "gpu_intel_any_media_engine_busy_percent"];
    private static readonly double[] IntervalValues = [3, 5, 10, 50, 25, 12.5];

    [Theory]
    [InlineData(0.0, 0.0, true)] [InlineData(0.125, 0.375, true)]
    [InlineData(double.NaN, 0.0, false)] [InlineData(0.0, double.PositiveInfinity, false)]
    [InlineData(-0.125, 0.0, false)] [InlineData(0.0, -0.125, false)]
    public void FloatingProjectionPreservesExactFinitePairsOrEmitsCanonicalEmpty(double primary, double reference, bool current)
    {
        var metric = new NativeMetricSnapshotMetricPlanOutput { RuleHandle = 1, MetricHandle = 2, SourceHandle = 3, ScopeHandle = 4, ValueKind = 1 };
        var binding = new NativeMetricSnapshotRuleCatalogBinding(1, 2, 3, 4, "test.metric", "test.source", 16, 1, 1, 2, 0, BitConverter.DoubleToUInt64Bits(double.MaxValue));
        var observation = NativeMetricSnapshotObservationProjection.CreateFloatingCounterPairObservation(metric, binding, new(3, 1, 1), DateTimeOffset.UtcNow, new(primary, reference));
        Assert.Equal(4U, observation.Flags); Assert.Equal(current ? 1U : 2U, observation.Status);
        Assert.Equal(current ? BitConverter.DoubleToUInt64Bits(primary) : 0UL, observation.ValueBits);
        Assert.Equal(current ? BitConverter.DoubleToUInt64Bits(reference) : 0UL, observation.ValueHighBits);
        Assert.Equal(0UL, observation.CounterTicksPerSecond); Assert.Equal(0UL, observation.SampleDurationMilliseconds); Assert.Equal(0U, observation.ByteCount);
    }

    [Fact]
    public void AllSdkZeroCountersRemainSuccessfulZeroPairs()
    {
        var packet = Sample(); packet.Timestamp = packet.GlobalActivity = packet.RenderComputeActivity = packet.MediaActivity = Number(0, IntelGclUnit.Seconds);
        packet.GpuEnergy = packet.MemoryEnergy = packet.BoardEnergy = Number(0, IntelGclUnit.Joules);
        Assert.All(IntelGclReader.Decode(9, packet, IntervalCodes.ToHashSet()).FloatingCounters.Values,
            pair => Assert.Equal(new NativeMetricSnapshotFloatingCounterPair(0, 0), pair));
    }

    [Fact]
    public void DerivedFieldsUseOneSelectedSdkPacketAndKeepRawAndPciResults()
    {
        var sdk = new Sdk(); var selected = IntervalCodes.Concat([MetricFieldCode.IgclGpuEnergy, MetricFieldCode.IgclPciCurrentGeneration, MetricFieldCode.IgclPciMaximumWidth]).ToHashSet();
        var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(), new Dictionary<int, IReadOnlySet<MetricFieldCode>> { [9] = selected }, CancellationToken.None));
        Assert.Equal(6, row.FloatingCounters.Count); Assert.Equal(new NativeMetricSnapshotFloatingCounterPair(5, 13), row.FloatingCounters[IntervalCodes[0]]);
        Assert.Equal(new NativeMetricSnapshotFloatingCounterPair(7, 13), row.FloatingCounters[IntervalCodes[1]]);
        Assert.Equal(new NativeMetricSnapshotFloatingCounterPair(11, 13), row.FloatingCounters[IntervalCodes[2]]);
        Assert.Equal(new NativeMetricSnapshotFloatingCounterPair(17, 13), row.FloatingCounters[IntervalCodes[3]]);
        Assert.Equal(new NativeMetricSnapshotFloatingCounterPair(19, 13), row.FloatingCounters[IntervalCodes[4]]);
        Assert.Equal(new NativeMetricSnapshotFloatingCounterPair(23, 13), row.FloatingCounters[IntervalCodes[5]]);
        Assert.Equal(5, row.Values[MetricFieldCode.IgclGpuEnergy]); Assert.Equal(4, row.Values[MetricFieldCode.IgclPciCurrentGeneration]); Assert.Equal(16, row.Values[MetricFieldCode.IgclPciMaximumWidth]);
        Assert.Equal(1, sdk.TelemetryCalls); Assert.Equal(1, sdk.PciCalls); Assert.Equal(1, sdk.PciPropertiesCalls); Assert.Equal(1, sdk.Closes);
        Assert.Equal(new IntPtr(1), Assert.Single(sdk.TelemetryDevices));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void BadNumeratorOnlyEmptiesItsOwnPairAndBadReferenceEmptiesAll(int defect)
    {
        var telemetry = Sample();
        if (defect == 1) telemetry.GpuEnergy.Supported = 0;
        if (defect == 2) telemetry.GpuEnergy.Unit = IntelGclUnit.Seconds;
        if (defect == 3) telemetry.GpuEnergy.Double = -0.5;
        if (defect == 4) telemetry.GpuEnergy.Double = double.NaN;
        if (defect == 5) telemetry.Timestamp.Double = double.PositiveInfinity;
        var row = IntelGclReader.Decode(9, telemetry, IntervalCodes.ToHashSet()); Assert.Empty(row.Values);
        Assert.Null(row.FloatingCounters[IntervalCodes[0]]);
        foreach (var code in IntervalCodes.Skip(1)) Assert.Equal(defect != 5, row.FloatingCounters[code].HasValue);
    }

    [Theory]
    [InlineData(16U, 1U, 2U, true)] [InlineData(17U, 1U, 2U, true)]
    [InlineData(16U, 3U, 2U, false)] [InlineData(17U, 1U, 1U, false)]
    public void FloatingCounterKindsRequireTypedFloatAndUnavailableRetention(uint kind, uint valueKind, uint retention, bool accepted)
    {
        var rule = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot.HotPublish.RuleTemplates.Single(template => template.MetricKind == 14);
        Assert.Equal(accepted, (rule with { MetricKind = kind, ValueKind = valueKind, RetentionPolicy = retention }).IsPublished);
    }

    [Theory]
    [InlineData(false, 0.25)] [InlineData(true, 25.0)]
    public void NativeFloatingDifferencePreservesFractionalInputsAndUsesSdkDuration(bool percent, double expected)
    {
        using var h = new FloatingWorkspace(percent); Assert.Null(h.Publish(new(1000.125, 10.125)));
        Assert.Equal(expected, h.Publish(new(1000.625, 12.125)));
        Assert.Equal(2000UL, h.Read().SampleDurationMilliseconds); // Host calls are one second apart.
        Assert.Equal(0, h.Publish(new(1000.625, 14.125)));
    }

    [Theory]
    [InlineData(90.25, 102.25)] [InlineData(101.25, 90.25)] [InlineData(101.25, 100.25)] [InlineData(101.25, 100.2501)]
    public void InvalidFloatingIntervalsBecomeNextBaseline(double primary, double reference)
    {
        using var h = new FloatingWorkspace(); h.Publish(new(100.25, 100.25)); Assert.Null(h.Publish(new(primary, reference)));
        Assert.Equal(0.25, h.Publish(new(primary + 0.5, reference + 2)));
    }

    [Fact]
    public void ActivityOvershootIsNullAndPowerArithmeticOverflowIsNull()
    {
        using var activity = new FloatingWorkspace(true); activity.Publish(new(0, 0)); Assert.Null(activity.Publish(new(3, 2)));
        Assert.Equal(50, activity.Publish(new(4, 4))); // No clamp to 100.
        using var power = new FloatingWorkspace(); power.Publish(new(0, 0)); Assert.Null(power.Publish(new(double.MaxValue, 0.01)));
        Assert.Equal(0, power.Publish(new(double.MaxValue, 2.01)));
        Assert.Null(power.Publish(new(double.MaxValue, double.MaxValue))); // Duration cannot fit UInt64 milliseconds.
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void MalformedNativeFloatingInputRejectsWithoutAdvancingBaseline(int defect)
    {
        using var h = new FloatingWorkspace(); h.Publish(new(0, 0)); Assert.Equal(0.25, h.Publish(new(0.5, 2)));
        Assert.Throws<InvalidOperationException>(() => h.Publish(new(10, 3), defect: defect));
        Assert.Equal(0.25, h.Value()); Assert.Equal(0.25, h.Publish(new(1, 4)));
    }

    [Fact]
    public void FailureCapabilityIncarnationFreezeAndDemandExitResetOnlyTheExistingBaseline()
    {
        using var h = new FloatingWorkspace(); h.Publish(new(0, 0)); Assert.Equal(0.25, h.Publish(new(0.5, 2)));
        Assert.Null(h.Publish(null)); Assert.Null(h.Publish(new(1, 4))); Assert.Equal(0.25, h.Publish(new(1.5, 6)));
        Assert.Null(h.Publish(new(2, 8), capability: 2)); Assert.Equal(0.25, h.Publish(new(2.5, 10), capability: 2));
        Assert.Null(h.Publish(new(3, 12), capability: 2, incarnation: 2)); Assert.Equal(0.25, h.Publish(new(3.5, 14), capability: 2, incarnation: 2));
        var plan = h.Workspace.PlanCollection([h.Handle], h.Modes(2, NativeMetricSnapshotZoneMode.Freeze), false, h.NextTime());
        Assert.Empty(plan.Sources); Assert.Equal(0.25, h.Value()); Assert.Null(h.Publish(new(4, 16), capability: 2, incarnation: 2));
        Assert.Equal(0.25, h.Publish(new(4.5, 18), capability: 2, incarnation: 2)); h.Sync([]); Assert.Equal(0.25, h.Value());
        h.Sync([h.Handle]); Assert.Null(h.Publish(new(10, 30), capability: 2, incarnation: 2));
    }

    [Fact]
    public void SlowActiveCounterKeepsBaselineWhenOnlyAnotherMetricIsDue()
    {
        using var h = new FloatingWorkspace(); h.Publish(new(0, 0)); Assert.Equal(0.25, h.Publish(new(0.5, 2)));
        h.Sync([h.Handle]); h.Other(); h.Sync([h.Handle]); Assert.Equal(0.25, h.Publish(new(1.5, 6)));
    }

    [Fact]
    public void FloatingPersistenceRestoresDisplayButNeverCounterBaseline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"floating-recovery-{Guid.NewGuid():N}.bin");
        try
        {
            using (var old = new FloatingWorkspace(path: path)) { old.Publish(new(0, 0)); Assert.Equal(0.25, old.Publish(new(0.5, 2))); Assert.True(old.Workspace.PersistRecoveryCheckpointIfAdvanced(old.NextTime())); }
            using var next = new FloatingWorkspace(path: path, sequence: 100); Assert.Equal(0.25, next.Value());
            Assert.Null(next.Publish(new(100, 100))); Assert.Equal(0.25, next.Publish(new(100.5, 102)));
        }
        finally { File.Delete(path); }
    }

    // Exercises the shared native counter owner independently of the SDK and hardware identity bridge.
    private sealed class FloatingWorkspace : IDisposable
    {
        private readonly IntelMsrCatalogIdentity target = new(65, "windows-cpu-thread|1:1", new(1, 1), 0x906E0, null, IntelMsrTargetKind.LogicalProcessor, false, false) { SupportsPerformanceFeedback = true };
        private readonly NativeMetricSnapshotCatalogProjection catalog;
        private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
        private ulong sequence;
        internal NativeMetricSnapshotWorkspace Workspace { get; }
        internal ulong Handle { get; }
        internal FloatingWorkspace(bool percent = false, string? path = null, ulong sequence = 0)
        {
            this.sequence = sequence; var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            compiled = compiled with { HotPublish = compiled.HotPublish with { RuleTemplates = compiled.HotPublish.RuleTemplates.Select(rule => rule.MetricKind == 14
                ? rule with { MetricKind = percent ? 17U : 16U, MetricFlags = percent ? 3U : 2U, MaximumValueBits = BitConverter.DoubleToUInt64Bits(percent ? 100 : double.MaxValue) } : rule).ToImmutableArray() } };
            Workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, path ?? Path.Combine(Path.GetTempPath(), $"floating-{Guid.NewGuid():N}.bin"));
            catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], Workspace.CreateCatalogHandleMap(), intelCpus: [target]);
            Workspace.ReplaceCatalog(catalog, start); Handle = Find(MetricFieldCode.IntelPerformanceRatio);
        }
        private ulong Find(MetricFieldCode field) => catalog.MetricBindings.Values.Single(binding => binding.DeviceIdentity == target.ExactIdentityKey && binding.FieldCode == field).MetricHandle;
        internal DateTimeOffset NextTime() => start.AddSeconds(++sequence);
        internal NativeMetricSnapshotSourceRuntimeMode[] Modes(ulong capability = 1, NativeMetricSnapshotZoneMode mode = NativeMetricSnapshotZoneMode.Normal)
            => catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle, capability, mode, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        internal void Sync(ulong[] active) => Workspace.SynchronizeCounterDemand(active, NextTime());
        internal double? Publish(NativeMetricSnapshotFloatingCounterPair? pair, ulong capability = 1, ulong incarnation = 1, int? defect = null)
            => Capture(Handle, pair, capability, incarnation, defect);
        internal void Other() => Capture(Find(MetricFieldCode.IntelAperfCounter), null, 1, 1, null);
        private double? Capture(ulong handle, NativeMetricSnapshotFloatingCounterPair? pair, ulong capability, ulong incarnation, int? defect)
        {
            var at = NextTime(); var plan = Workspace.PlanCollection([handle], Modes(capability), false, at); var source = Assert.Single(plan.Sources);
            var metric = Assert.Single(plan.Metrics, metric => metric.SourceHandle == source.SourceHandle && (metric.Flags & 1) != 0); var binding = plan.Catalog.RuleBindings[metric.RuleHandle];
            var observation = handle == Handle ? NativeMetricSnapshotObservationProjection.CreateFloatingCounterPairObservation(metric, binding, new(source.SourceHandle, incarnation, sequence), at, pair)
                : NativeMetricSnapshotObservationProjection.CreateUnsignedObservation(metric, binding, new(source.SourceHandle, incarnation, sequence), at, 42);
            switch (defect)
            {
                case 0: observation.Flags = (uint)NativeMetricSnapshotObservationFlags.CounterPair; break;
                case 1: observation.ValueBits = BitConverter.DoubleToUInt64Bits(double.NaN); break;
                case 2: observation.ValueHighBits = BitConverter.DoubleToUInt64Bits(double.PositiveInfinity); break;
                case 3: observation.ValueBits = BitConverter.DoubleToUInt64Bits(-1); break;
                case 4: observation.CounterTicksPerSecond = 1; break;
                case 5: observation.SampleDurationMilliseconds = 1; break;
            }
            Workspace.CompleteSource(plan, new(source.SourceHandle, incarnation, sequence, at, NativeMetricSnapshotSourceStatus.Complete, [observation], null, [], 0, 0), at);
            return Value();
        }
        internal NativeMetricSnapshotMetricOutput Read() => Assert.Single(Workspace.ReadCommitted().Metrics, metric => metric.MetricHandle == Handle);
        internal double? Value() => Read().Status == (uint)NativeMetricSnapshotMetricStatus.Current ? BitConverter.UInt64BitsToDouble(Read().ValueBits) : null;
        public void Dispose() => Workspace.Dispose();
    }
}
