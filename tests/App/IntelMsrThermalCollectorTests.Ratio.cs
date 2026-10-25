using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    [Theory]
    [InlineData(1U, true)]
    [InlineData(3U, false)]
    public void CounterRatioRuleRequiresFloatOutput(uint valueKind, bool accepted)
    {
        var rule = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot.HotPublish.RuleTemplates.Single(template => template.MetricKind == 14);
        Assert.Equal(accepted, (rule with { ValueKind = valueKind }).IsPublished);
    }

    [Fact]
    public void RatioDemandReadsPairOnceWithoutPublishingUnrequestedRawCounters()
    {
        var target = FeedbackThread(65); var access = new Access { FeedbackFlags = 1, ThermalFlags = 0 };
        var hardware = new Hardware(access); hardware.RaplRegisters[0xE7] = ulong.MaxValue;
        hardware.RaplRegisters[0xE8] = 9007199254740993UL;
        var reader = new IntelMsrReader(() => hardware, () => access);
        var onlyRatio = Assert.Single(reader.Read([Core(), target], Request(target, MetricFieldCode.IntelPerformanceRatio)));
        Assert.Equal(new NativeMetricSnapshotCounterPair(9007199254740993UL, ulong.MaxValue), Assert.Single(onlyRatio.CounterPairs).Value);
        Assert.Empty(onlyRatio.Values); Assert.Empty(onlyRatio.UnsignedValues);
        Assert.Equal(new uint[] { 0xE7, 0xE8 }, hardware.Reads.Select(static item => item.Register));
        hardware.Reads.Clear();
        var combined = Assert.Single(reader.Read([target], Request(target,
            MetricFieldCode.IntelPerformanceRatio, MetricFieldCode.IntelAperfCounter, MetricFieldCode.IntelMperfCounter)));
        Assert.Single(combined.CounterPairs); Assert.Equal(2, combined.UnsignedValues.Count);
        Assert.Equal(new uint[] { 0xE7, 0xE8 }, hardware.Reads.Select(static item => item.Register));
        Assert.All(hardware.Reads, item => Assert.Equal(target.Processor, item.Address)); Assert.Equal(0, hardware.Writes);
    }

    [Theory]
    [InlineData(0xE7)]
    [InlineData(0xE8)]
    public void EitherFailedCounterMakesPairEmptyWithoutRemovingSurvivingRawValue(uint failed)
    {
        var target = FeedbackThread(); var access = new Access { FeedbackFlags = 1 };
        var hardware = new Hardware(access) { FailingRegister = failed };
        hardware.RaplRegisters[0xE7] = 10; hardware.RaplRegisters[0xE8] = 20;
        var fields = Request(target, MetricFieldCode.IntelPerformanceRatio, MetricFieldCode.IntelAperfCounter, MetricFieldCode.IntelMperfCounter);
        var reader = new IntelMsrReader(() => hardware, () => access);
        var missing = Assert.Single(reader.Read([target], fields));
        Assert.Empty(missing.CounterPairs); Assert.Single(missing.UnsignedValues);
        hardware.FailingRegister = null;
        Assert.Single(Assert.Single(reader.Read([target], fields)).CounterPairs);
    }

    [Fact]
    public void NativeRatioUsesDeltasAndAllowsZeroAndAboveNominalResults()
    {
        using var h = new RatioWorkspace();
        Assert.Null(h.Publish(new(1000, 100))); // Lifetime ratio would incorrectly be 10.
        Assert.Equal(1.5, h.Publish(new(1150, 200)));
        Assert.Equal(0, h.Publish(new(1150, 300)));
        Assert.Equal(2, h.Publish(new(1350, 400)));
        var field = h.ReadField(); Assert.Equal("ratio", field.Unit); Assert.Equal("float64", field.ValueKind);
        Assert.Equal(1000UL, Assert.Single(h.Workspace.ReadCommitted().Metrics, metric => metric.MetricHandle == h.RatioHandle).SampleDurationMilliseconds);
    }

    [Theory]
    [InlineData(90UL, 200UL)] // Primary regression.
    [InlineData(200UL, 90UL)] // Reference regression.
    [InlineData(200UL, 100UL)] // No reference progress.
    public void InvalidIntervalPublishesEmptyAndUsesCurrentPairAsNextBaseline(ulong primary, ulong reference)
    {
        using var h = new RatioWorkspace(); Assert.Null(h.Publish(new(100, 100)));
        Assert.Null(h.Publish(new(primary, reference)));
        Assert.Equal(1.5, h.Publish(new(primary + 150, reference + 100)));
    }

    [Fact]
    public void NativeRatioKeepsFullUInt64DeltasAndHandlesResetWithoutModuloGuessing()
    {
        using var h = new RatioWorkspace();
        Assert.Null(h.Publish(new(ulong.MaxValue - 200, ulong.MaxValue - 100)));
        Assert.Equal(2, h.Publish(new(ulong.MaxValue - 100, ulong.MaxValue - 50)));
        Assert.Null(h.Publish(new(0, 0)));
        Assert.Equal(1.5, h.Publish(new(150, 100)));
    }

    [Fact]
    public void NativeRatioFailureCapabilityAndIncarnationEachStartNewInterval()
    {
        using var h = new RatioWorkspace(); h.Publish(new(0, 0)); Assert.Equal(1.5, h.Publish(new(150, 100)));
        Assert.Null(h.Publish(null)); Assert.Null(h.Publish(new(200, 200)));
        Assert.Equal(1.5, h.Publish(new(350, 300)));
        Assert.Null(h.Publish(new(500, 400), capability: 2));
        Assert.Equal(1.5, h.Publish(new(650, 500), capability: 2));
        Assert.Null(h.Publish(new(800, 600), capability: 2, incarnation: 2));
        Assert.Equal(1.5, h.Publish(new(950, 700), capability: 2, incarnation: 2));
    }

    [Fact]
    public void DueOnlyOtherMetricPreservesSlowActiveRatioAndCancellationPreservesDisplay()
    {
        using var h = new RatioWorkspace(); h.Publish(new(0, 0)); Assert.Equal(1.5, h.Publish(new(150, 100)));
        h.Sync([h.RatioHandle]); h.CaptureOtherMetric(); h.Sync([h.RatioHandle]);
        Assert.Equal(1.5, h.Publish(new(450, 300)));
        h.Sync([]); Assert.Equal(1.5, h.ReadField().NumericValue);
        h.Sync([h.RatioHandle]); Assert.Null(h.Publish(new(1000, 1000)));
        Assert.Equal(1.5, h.Publish(new(1150, 1100)));
    }

    [Fact]
    public void FrozenSourceStartsFreshIntervalWhenReenabled()
    {
        using var h = new RatioWorkspace(); h.Publish(new(0, 0)); Assert.Equal(1.5, h.Publish(new(150, 100)));
        var plan = h.Workspace.PlanCollection([h.RatioHandle], h.Modes(1, NativeMetricSnapshotZoneMode.Freeze), false, h.NextTime());
        Assert.Empty(plan.Sources); Assert.Equal(1.5, h.ReadField().NumericValue);
        Assert.Null(h.Publish(new(450, 300))); Assert.Equal(1.5, h.Publish(new(600, 400)));
    }

    [Fact]
    public void RejectedObservationAndDemandDoNotMutateCommittedCounterBaseline()
    {
        using var h = new RatioWorkspace(); h.Publish(new(0, 0)); Assert.Equal(1.5, h.Publish(new(150, 100)));
        Assert.Throws<InvalidOperationException>(() => h.Publish(new(900, 200), corrupt: true));
        Assert.Equal(1.5, h.ReadField().NumericValue);
        Assert.Throws<InvalidOperationException>(() => h.Sync([ulong.MaxValue]));
        Assert.Equal(1.5, h.Publish(new(450, 300)));
    }

    [Fact]
    public void LogicalProcessorsHaveIndependentCounterBaselines()
    {
        using var h = new RatioWorkspace();
        h.Publish(new(0, 0)); h.CaptureSecond(new(10000, 1000));
        Assert.Equal(1.5, h.Publish(new(150, 100)));
        Assert.Equal(0.5, h.CaptureSecond(new(10050, 1100)));
    }

    [Fact]
    public void RestartClearsCurrentRatioAndCounterBaseline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ratio-recovery-{Guid.NewGuid():N}.bin");
        try
        {
            using (var old = new RatioWorkspace(path))
            {
                old.Publish(new(0, 0)); Assert.Equal(1.5, old.Publish(new(150, 100)));
                Assert.True(old.Workspace.PersistRecoveryCheckpointIfAdvanced(old.NextTime()));
            }
            using var restored = new RatioWorkspace(path, sequence: 100);
            Assert.Null(restored.ReadField().NumericValue);
            Assert.Null(restored.Publish(new(10000, 1000)));
            Assert.Equal(0.5, restored.Publish(new(10050, 1100)));
        }
        finally { File.Delete(path); }
    }

    private sealed class RatioWorkspace : IDisposable
    {
        private readonly IntelMsrCatalogIdentity target = FeedbackThread(65), second = FeedbackThread(2);
        private readonly NativeMetricSnapshotCatalogProjection catalog;
        private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
        private ulong sequence;
        internal NativeMetricSnapshotWorkspace Workspace { get; }
        internal ulong RatioHandle { get; }
        internal RatioWorkspace(string? path = null, ulong sequence = 0)
        {
            this.sequence = sequence;
            var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            Workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, path ?? Path.Combine(Path.GetTempPath(), $"ratio-{Guid.NewGuid():N}.bin"));
            catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], Workspace.CreateCatalogHandleMap(), intelCpus: [target, second]);
            Workspace.ReplaceCatalog(catalog, start);
            RatioHandle = Handle(target, MetricFieldCode.IntelPerformanceRatio);
        }
        private ulong Handle(IntelMsrCatalogIdentity device, MetricFieldCode field) => catalog.MetricBindings.Values.Single(
            binding => binding.DeviceIdentity == device.ExactIdentityKey && binding.FieldCode == field).MetricHandle;
        internal DateTimeOffset NextTime() => start.AddSeconds(++sequence);
        internal NativeMetricSnapshotSourceRuntimeMode[] Modes(ulong capability = 1, NativeMetricSnapshotZoneMode mode = NativeMetricSnapshotZoneMode.Normal)
            => catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle, capability, mode, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        internal void Sync(ulong[] active) => Workspace.SynchronizeCounterDemand(active, NextTime());
        internal double? Publish(NativeMetricSnapshotCounterPair? pair, ulong capability = 1, ulong incarnation = 1, bool corrupt = false)
            => Capture(target, MetricFieldCode.IntelPerformanceRatio, pair, capability, incarnation, corrupt);
        internal double? CaptureSecond(NativeMetricSnapshotCounterPair pair) => Capture(second, MetricFieldCode.IntelPerformanceRatio, pair, 1, 1, false);
        internal void CaptureOtherMetric() => Capture(target, MetricFieldCode.IntelAperfCounter, null, 1, 1, false);
        private double? Capture(IntelMsrCatalogIdentity device, MetricFieldCode field, NativeMetricSnapshotCounterPair? pair, ulong capability, ulong incarnation, bool corrupt)
        {
            var at = NextTime(); var plan = Workspace.PlanCollection([Handle(device, field)], Modes(capability), false, at);
            var source = Assert.Single(plan.Sources);
            var read = new IntelMsrMetricRead(device.ExactIdentityKey, new Dictionary<MetricFieldCode, double>()) {
                CounterPairs = pair is { } raw ? new Dictionary<MetricFieldCode, NativeMetricSnapshotCounterPair> { [MetricFieldCode.IntelPerformanceRatio] = raw } : []
            };
            var completion = WindowsHardwareMetricSampler.CreateIntelMsrCompletion(plan, source, new(source.SourceHandle, incarnation, sequence), at, [read]);
            if (corrupt) { var observation = completion.Observations[0]; observation.Flags = 0; completion = completion with { Observations = completion.Observations.SetItem(0, observation) }; }
            Workspace.CompleteSource(plan, completion, at);
            return MetricDeviceFields.Read(Snapshot(), device.ExactIdentityKey).Single().Fields.Single(item => item.Name == "cpu_intel_aperf_mperf_interval_ratio").NumericValue;
        }
        private HardwareMetricSnapshot Snapshot() => NativeMetricSnapshotCommittedProjection.Create(frame: Workspace.ReadCommitted(), workspaceIdentity: 1);
        internal DeviceMetricFieldValue ReadField() => MetricDeviceFields.Read(Snapshot(), target.ExactIdentityKey).Single().Fields.Single(item => item.Name == "cpu_intel_aperf_mperf_interval_ratio");
        public void Dispose() => Workspace.Dispose();
    }
}
