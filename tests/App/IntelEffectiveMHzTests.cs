using System.Collections.Immutable;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    [Fact]
    public void EffectiveFrequencyTopologyUsesPerThreadCpuid16AndOmitsUnsupportedZero()
    {
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(new Access
        {
            MaximumLeaf = 0x16,
            BaseFrequencyMhz = 2400,
            FeedbackFlags = 1
        }.Read);
        var topology = IntelMsrTopologyProjection.Create(new CpuOsTopologyInformation([], [])
        {
            Packages = [],
            ThreadCpuid = [new(65, data)]
        });
        var identity = Assert.Single(topology.Identities);
        Assert.Equal((ushort)2400, identity.IntelProcessorBaseFrequencyDeclaredMhz);
        Assert.True(identity.AppliesTo(MetricFieldCode.IntelEffectiveFrequencyMhz));

        var zeroData = WindowsCpuThreadCpuidReader.ReadRegisters(new Access
        {
            MaximumLeaf = 0x16,
            BaseFrequencyMhz = 0,
            FeedbackFlags = 1
        }.Read);
        var zeroTopology = IntelMsrTopologyProjection.Create(new CpuOsTopologyInformation([], [])
        {
            Packages = [],
            ThreadCpuid = [new(65, zeroData)]
        });
        var zeroIdentity = Assert.Single(zeroTopology.Identities);
        Assert.Null(zeroIdentity.IntelProcessorBaseFrequencyDeclaredMhz);
        Assert.False(zeroIdentity.AppliesTo(MetricFieldCode.IntelEffectiveFrequencyMhz));
    }

    [Fact]
    public void EffectiveFrequencyReaderReusesFeedbackPairAndRevalidatesDeclaredBase()
    {
        var target = EffectiveThread(65);
        var access = new Access { MaximumLeaf = 0x16, BaseFrequencyMhz = 2400, FeedbackFlags = 1, ThermalFlags = 0 };
        var hardware = new Hardware(access);
        hardware.RaplRegisters[0xE7] = 1000;
        hardware.RaplRegisters[0xE8] = 1500;

        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access)
            .Read([target], Request(target, MetricFieldCode.IntelEffectiveFrequencyMhz)));
        Assert.Equal(new NativeMetricSnapshotCounterPair(1500, 1000, 2400),
            Assert.Single(result.CounterPairs).Value);
        Assert.Empty(result.Values);
        Assert.Empty(result.UnsignedValues);
        Assert.Equal(new uint[] { 0xE7, 0xE8 }, hardware.Reads.Select(static read => read.Register));
        Assert.Equal(0, hardware.Writes);

        var changedBase = new Access { MaximumLeaf = 0x16, BaseFrequencyMhz = 2800, FeedbackFlags = 1 };
        var opens = 0;
        var stale = Assert.Single(new IntelMsrReader(() => { opens++; throw new IOException(); }, () => changedBase)
            .Read([target], Request(target, MetricFieldCode.IntelEffectiveFrequencyMhz)));
        Assert.True(stale.RequiresTopologyRefresh);
        Assert.Empty(stale.CounterPairs);
        Assert.Equal(0, opens);
        Assert.False(FeedbackThread().AppliesTo(MetricFieldCode.IntelEffectiveFrequencyMhz));
    }

    [Fact]
    public void EffectiveFrequencyNativeOwnerUsesDeltasAndResetsForInvalidIntervalsAndCancellation()
    {
        using var h = new EffectiveFrequencyWorkspace();

        Assert.Null(h.Publish(new(1000, 100, 2400))); // First pair establishes a baseline.
        Assert.Equal(3600, h.Publish(new(1150, 200, 2400))); // 150 / 100 * 2400 MHz.
        Assert.Equal(0, h.Publish(new(1150, 300, 2400))); // A real zero APERF delta is a valid zero.
        Assert.Null(h.Publish(new(1250, 300, 2400))); // Zero MPERF delta is unavailable, never zero MHz.
        Assert.Equal(2400, h.Publish(new(1350, 400, 2400))); // The rejected pair became the new baseline.

        Assert.Null(h.Publish(new(1500, 500, 2800))); // A changed declared base resets the interval.
        Assert.Equal(2800, h.Publish(new(1600, 600, 2800)));
        h.Sync([h.FrequencyHandle]);
        h.Sync([]);
        Assert.Null(h.Publish(new(1700, 700, 2800))); // Cancellation discards the old baseline.
        Assert.Equal(2800, h.Publish(new(1800, 800, 2800)));
        Assert.Null(h.Publish(new(1900, 900, 0))); // A missing base cannot be turned into a numeric zero.
        Assert.Null(h.Publish(new(2000, 1000, 2800)));
        Assert.Equal(2800, h.Publish(new(2100, 1100, 2800)));

        var field = h.ReadField();
        Assert.Equal("MHz", field.Unit);
        Assert.Equal("float64", field.ValueKind);
        Assert.Equal(1000UL, Assert.Single(h.Workspace.ReadCommitted().Metrics,
            metric => metric.MetricHandle == h.FrequencyHandle).SampleDurationMilliseconds);
    }

    private static IntelMsrCatalogIdentity EffectiveThread(int id)
        => FeedbackThread(id) with { IntelProcessorBaseFrequencyDeclaredMhz = 2400 };

    private sealed class EffectiveFrequencyWorkspace : IDisposable
    {
        private readonly IntelMsrCatalogIdentity target = EffectiveThread(65);
        private readonly NativeMetricSnapshotCatalogProjection catalog;
        private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
        private ulong sequence;

        internal NativeMetricSnapshotWorkspace Workspace { get; }
        internal ulong FrequencyHandle { get; }

        internal EffectiveFrequencyWorkspace()
        {
            var original = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            var declaredFrequency = WindowsMetricConfiguration.Default.CatalogConfiguration.RuleTemplates
                .Single(static rule => rule.MetricKind == (uint)NativeMetricSnapshotMetricKind.CounterFrequency);
            var rule = new CompiledHostManagerMetricSnapshotRuleTemplate(
                declaredFrequency.TemplateId, declaredFrequency.MetricIdTemplate, declaredFrequency.SourceId,
                declaredFrequency.SourceHandle, declaredFrequency.SourcePriority, declaredFrequency.ScopeExpansion,
                declaredFrequency.MetricKind, declaredFrequency.ScopeKind, declaredFrequency.ValueKind,
                declaredFrequency.CapabilityMask, declaredFrequency.MetricFlags,
                declaredFrequency.MinimumValueBits, declaredFrequency.MaximumValueBits, declaredFrequency.GpuVendorMask,
                declaredFrequency.ApplicabilityMask, declaredFrequency.MaximumInstanceCount, declaredFrequency.SemanticFingerprint);
            var ruleTemplates = original.HotPublish.RuleTemplates.Any(
                value => value.TemplateId == rule.TemplateId)
                ? original.HotPublish.RuleTemplates
                : original.HotPublish.RuleTemplates.Add(rule);
            var hotPublish = original.HotPublish with
            {
                RuleTemplates = ruleTemplates
                    .OrderBy(static value => value.TemplateId, StringComparer.Ordinal).ToImmutableArray()
            };
            var plan = original with
            {
                Recreate = original.Recreate with { ObservationContractVersion = NativeMetricSnapshotAbi.ObservationContractVersion },
                HotPublish = hotPublish,
                RecreateSha256 = new string('a', 64),
                HotPublishSha256 = new string('b', 64)
            };

            Workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(
                plan, Path.Combine(Path.GetTempPath(), $"effective-mhz-{Guid.NewGuid():N}.bin"));
            catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
                plan, [], [], [], Workspace.CreateCatalogHandleMap(), intelCpus: [target]);
            Workspace.ReplaceCatalog(catalog, start);
            FrequencyHandle = catalog.MetricBindings.Values.Single(
                binding => binding.DeviceIdentity == target.ExactIdentityKey
                    && binding.FieldCode == MetricFieldCode.IntelEffectiveFrequencyMhz).MetricHandle;
        }

        internal DateTimeOffset NextTime() => start.AddSeconds(++sequence);
        internal NativeMetricSnapshotSourceRuntimeMode[] Modes()
            => catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeMode(
                source.SourceHandle, 1, NativeMetricSnapshotZoneMode.Normal,
                NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        internal void Sync(ulong[] active) => Workspace.SynchronizeCounterDemand(active, NextTime());

        internal double? Publish(NativeMetricSnapshotCounterPair? pair)
        {
            var at = NextTime();
            var plan = Workspace.PlanCollection([FrequencyHandle], Modes(), false, at);
            var source = Assert.Single(plan.Sources);
            var read = new IntelMsrMetricRead(target.ExactIdentityKey, new Dictionary<MetricFieldCode, double>())
            {
                CounterPairs = pair is { } raw
                    ? new Dictionary<MetricFieldCode, NativeMetricSnapshotCounterPair>
                    {
                        [MetricFieldCode.IntelEffectiveFrequencyMhz] = raw
                    }
                    : []
            };
            var completion = WindowsHardwareMetricSampler.CreateIntelMsrCompletion(
                plan, source, new(source.SourceHandle, 1, sequence), at, [read]);
            Workspace.CompleteSource(plan, completion, at);
            return ReadField().NumericValue;
        }

        private HardwareMetricSnapshot Snapshot()
            => NativeMetricSnapshotCommittedProjection.Create(Workspace.ReadCommitted(), "CPU", "RAM", 1);

        internal DeviceMetricFieldValue ReadField()
            => MetricDeviceFields.Read(Snapshot(), target.ExactIdentityKey).Single().Fields.Single(
                static field => field.Name == "cpu_intel_effective_frequency_mhz");

        public void Dispose() => Workspace.Dispose();
    }
}
