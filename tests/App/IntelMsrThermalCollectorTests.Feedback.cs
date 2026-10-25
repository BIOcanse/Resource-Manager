using System.Text.Json;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Native;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    private static IntelMsrCatalogIdentity FeedbackThread(int id = 0) => new(id, $"windows-cpu-thread|{id / 64}:{id % 64}",
        new((ushort)(id / 64), (byte)(id % 64)), 0x906E0, null, IntelMsrTargetKind.LogicalProcessor, false, false)
        { SupportsPerformanceFeedback = true };

    [Theory]
    [InlineData(0UL)]
    [InlineData(9007199254740993UL)]
    [InlineData(18446744073709551615UL)]
    public void FeedbackCountersRetainAll64BitsAndReadOnlySelectedLogicalProcessor(ulong raw)
    {
        var device = FeedbackThread(65); var access = new Access { FeedbackFlags = 1, ThermalFlags = 0 };
        var hardware = new Hardware(access); hardware.RaplRegisters[0xE7] = raw; hardware.RaplRegisters[0xE8] = raw;
        var caller = Environment.CurrentManagedThreadId;
        var result = Assert.Single(new IntelMsrReader(() => { hardware.Opens++; return hardware; }, () => access)
            .Read([Core(), Package(), FeedbackThread(), device], Request(device, MetricFieldCode.IntelAperfCounter, MetricFieldCode.IntelMperfCounter)));
        Assert.Empty(result.Values); Assert.Equal(2, result.UnsignedValues.Count);
        Assert.All(result.UnsignedValues, pair => Assert.Equal(raw, pair.Value));
        Assert.Equal(new uint[] { 0xE7, 0xE8 }, hardware.Reads.Select(static read => read.Register));
        Assert.All(hardware.Reads, read => { Assert.Equal(new CpuProcessorAddress(1, 1), read.Address); Assert.NotEqual(caller, read.Thread); });
        Assert.Equal(1, hardware.Opens); Assert.Equal(1, hardware.Disposals); Assert.Equal(0, hardware.Writes);
    }

    [Theory]
    [InlineData(MetricFieldCode.IntelMperfCounter, 0xE7)]
    [InlineData(MetricFieldCode.IntelAperfCounter, 0xE8)]
    public void FeedbackSingleFieldDoesNotReadOtherCounterOrThermalRegisters(MetricFieldCode field, uint register)
    {
        var target = FeedbackThread(); var access = new Access { FeedbackFlags = 1 };
        var hardware = new Hardware(access); hardware.RaplRegisters[register] = ulong.MaxValue;
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([target],
            Request(target, field, MetricFieldCode.IntelCoreDtsTemperature, MetricFieldCode.IntelPackageEnergyCounter)));
        Assert.Equal(ulong.MaxValue, Assert.Single(result.UnsignedValues).Value);
        Assert.Equal(register, Assert.Single(hardware.Reads).Register); Assert.Empty(result.Values);
    }

    [Theory]
    [InlineData(0xE7, MetricFieldCode.IntelAperfCounter)]
    [InlineData(0xE8, MetricFieldCode.IntelMperfCounter)]
    public void FeedbackReadFailureClearsOnlyFailedFieldAndNextCaptureRecovers(uint register, MetricFieldCode survivor)
    {
        var target = FeedbackThread(); var access = new Access { FeedbackFlags = 1 };
        var hardware = new Hardware(access) { FailingRegister = register };
        hardware.RaplRegisters[0xE7] = 10; hardware.RaplRegisters[0xE8] = 20;
        var reader = new IntelMsrReader(() => hardware, () => access);
        var requested = Request(target, MetricFieldCode.IntelAperfCounter, MetricFieldCode.IntelMperfCounter);
        Assert.Equal(survivor, Assert.Single(Assert.Single(reader.Read([target], requested)).UnsignedValues).Key);
        hardware.FailingRegister = null;
        Assert.Equal(2, Assert.Single(reader.Read([target], requested)).UnsignedValues.Count);
        hardware.FailDispose = true;
        Assert.Empty(Assert.Single(reader.Read([target], requested)).UnsignedValues);
    }

    [Theory]
    [InlineData("support")]
    [InlineData("identity")]
    [InlineData("address")]
    public void FeedbackRevalidatesOriginalSupportIdentityAndAffinityBeforeHardwareAccess(string change)
    {
        var target = FeedbackThread(); var access = new Access { FeedbackFlags = 1 };
        if (change == "support") access.FeedbackFlags = 0;
        if (change == "identity") access.Signature++;
        if (change == "address") access.WrongCurrentFromCall = 2;
        var opens = 0;
        var result = Assert.Single(new IntelMsrReader(() => { opens++; throw new IOException(); }, () => access)
            .Read([target], Request(target, MetricFieldCode.IntelAperfCounter)));
        Assert.True(result.RequiresTopologyRefresh); Assert.Empty(result.UnsignedValues); Assert.Equal(0, opens);
    }

    [Fact]
    public void FeedbackFrozenNoDemandOrWrongObjectKindNeverCreatesHardwareAccess()
    {
        var reader = new IntelMsrReader(() => throw new InvalidOperationException(), () => throw new InvalidOperationException());
        var target = FeedbackThread(); var requested = Request(target, MetricFieldCode.IntelAperfCounter);
        Assert.Empty(reader.Read([target], new Dictionary<string, IReadOnlySet<MetricFieldCode>>()));
        Assert.Empty(reader.Read([target with { SupportsPerformanceFeedback = false }], requested));
        Assert.Empty(reader.Read([Core() with { SupportsPerformanceFeedback = true }], Request(Core(), MetricFieldCode.IntelAperfCounter)));
        Assert.Empty(reader.Read([Package() with { SupportsPerformanceFeedback = true }], Request(Package(), MetricFieldCode.IntelAperfCounter)));
        var zone = new IntelMsrMonitoringZone(reader); zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        Assert.Empty(zone.Read([target], requested));
    }

    [Fact]
    public void FeedbackAffinityChangeDuringReadDiscardsBothUnsignedCounters()
    {
        var target = FeedbackThread(); var access = new Access { FeedbackFlags = 1 };
        var hardware = new Hardware(access) { AfterRead = () => access.WrongCurrentFromCall = 1 };
        hardware.RaplRegisters[0xE7] = 10; hardware.RaplRegisters[0xE8] = 20;
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([target],
            Request(target, MetricFieldCode.IntelAperfCounter, MetricFieldCode.IntelMperfCounter)));
        Assert.True(result.RequiresTopologyRefresh); Assert.Empty(result.UnsignedValues); Assert.Equal(1, hardware.Disposals);
    }

    [Fact]
    public void FeedbackDirectoryUsesEachOsThreadAndDoesNotDependOnThermalOrKnownModel()
    {
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(new Access { FeedbackFlags = 1, ThermalFlags = 0, Signature = 0xF06E0 }.Read);
        var noFeedback = data with { IntelPerformanceFeedbackSupported = false };
        var amd = data with { Identity = data.Identity with { Vendor = "AuthenticAMD" } };
        var info = new CpuOsTopologyInformation([], []) { Packages = [],
            ThreadCpuid = [new(65, data), new(0, data), new(1, noFeedback), new(2, amd)] };
        var result = IntelMsrTopologyProjection.Create(info);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, result.LogicalProcessorStatus);
        Assert.Equal(new[] { "windows-cpu-thread|0:0", "windows-cpu-thread|1:1" }, result.Identities.Select(static cpu => cpu.ExactIdentityKey));
        Assert.All(result.Identities, cpu => { Assert.Equal(IntelMsrTargetKind.LogicalProcessor, cpu.Kind);
            Assert.True(cpu.AppliesTo(MetricFieldCode.IntelAperfCounter)); Assert.False(cpu.AppliesTo(MetricFieldCode.IntelCoreDtsDistance)); });
        var failed = IntelMsrTopologyProjection.Create(info with { ThreadCpuid = [new(0, data), new(65, null)] });
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, failed.LogicalProcessorStatus); Assert.Empty(failed.Identities);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete,
            IntelMsrTopologyProjection.Create(info with { ThreadCpuid = [] }).LogicalProcessorStatus);
    }

    [Fact]
    public void FeedbackDirectoryRetentionIsIndependentAndCapabilityChangeRebuildsCatalog()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot; var target = FeedbackThread();
        var prior = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty,
            intelCpus: [Core(), Package(), target]);
        var retained = HostManagerMetricSnapshotOwner.RetainUnavailableOptionalIdentities(Topology([]) with
            { IntelCpuLogicalProcessorStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable }, prior);
        Assert.Equal(target, Assert.Single(retained.IntelCpuIdentities));
        Assert.Empty(HostManagerMetricSnapshotOwner.RetainUnavailableOptionalIdentities(Topology([]), prior).IntelCpuIdentities);
        var changed = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], prior.HandleMap,
            intelCpus: [Core(), Package(), target with { SupportsPerformanceFeedback = false }]);
        Assert.False(HostManagerMetricSnapshotOwner.CanRetainCatalog(prior, changed));
        Assert.Throws<InvalidOperationException>(() => NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], prior.HandleMap,
            intelCpus: Enumerable.Range(0, 513).Select(static id => FeedbackThread(id)).ToArray()));
    }

    [Theory]
    [InlineData(0x306C0)]
    [InlineData(0x206D0)]
    public void FullFeedbackTemperatureAndRaplExpansionFitsOriginalResidentBudget(uint signature)
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"feedback-full-{Guid.NewGuid():N}.bin"));
        var cpus = Enumerable.Range(0, 256).Select(static index => Core(index))
            .Concat(Enumerable.Range(0, 16).Select(index => RaplPackage(signature) with
                { DisplayIndex = index, ExactIdentityKey = $"windows-cpu-package|{index}:0", Processor = new((ushort)index, 0) }))
            .Concat(Enumerable.Range(0, 512).Select(static index => FeedbackThread(index))).ToArray();
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], workspace.CreateCatalogHandleMap(),
            cpuVendor: MetricVendor.Amd, intelCpus: cpus);
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var bindings = catalog.MetricBindings.Values.Where(static binding => binding.SourceId == "intel.msr").ToArray();
        Assert.Equal(2672, bindings.Length); Assert.Equal(2672, bindings.Select(static binding => binding.MetricHandle).Distinct().Count());
        Assert.Equal(784, bindings.Select(static binding => binding.DeviceIdentity).Distinct().Count());
        Assert.InRange(workspace.Capacity.ResidentByteCount, 1UL, 64UL * 1024 * 1024);
        var threadRules = compiled.HotPublish.RuleTemplates.Where(static rule => rule.ScopeExpansion == 12).ToArray();
        var effectiveFrequency = Assert.Single(threadRules,
            static rule => rule.MetricKind == (uint)NativeMetricSnapshotMetricKind.CounterFrequency);
        Assert.Equal("cpu_intel_effective_frequency_mhz.optional", effectiveFrequency.TemplateId);
        Assert.Equal(18UL, effectiveFrequency.SourceHandle);
        Assert.Equal(512U, effectiveFrequency.MaximumInstanceCount);
        var originalThreadRules = threadRules
            .Where(rule => rule.TemplateId != effectiveFrequency.TemplateId).ToArray();
        Assert.Equal(
            [
                "cpu_intel_aperf_counter_raw.optional",
                "cpu_intel_aperf_mperf_interval_ratio.optional",
                "cpu_intel_mperf_counter_raw.optional"
            ],
            originalThreadRules.Select(static rule => rule.TemplateId)
                .OrderBy(static templateId => templateId, StringComparer.Ordinal));
        Assert.Equal(3, originalThreadRules.Length);
        Assert.Equal(1536U, originalThreadRules.Aggregate(0U,
            static (sum, rule) => sum + rule.MaximumInstanceCount));
        Assert.All(originalThreadRules, rule =>
        {
            Assert.True(rule.IsPublished);
            Assert.Equal(512U, rule.MaximumInstanceCount);
        });
        Assert.Equal(4, threadRules.Length);
        Assert.Equal(2048U, threadRules.Aggregate(0U,
            static (sum, rule) => sum + rule.MaximumInstanceCount));
        // These feedback fixtures omit the CPUID.16H base MHz, so the optional frequency field is not bound.
        Assert.DoesNotContain(bindings,
            static binding => binding.FieldCode == MetricFieldCode.IntelEffectiveFrequencyMhz);
    }

    [Fact]
    public void FeedbackOriginalNativeCommitAndJsonAreLosslessAndFailureClearsPriorValue()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"feedback-raw-{Guid.NewGuid():N}.bin"));
        var target = FeedbackThread(65);
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], workspace.CreateCatalogHandleMap(), intelCpus: [target]);
        var now = DateTimeOffset.UtcNow; workspace.ReplaceCatalog(catalog, now);
        var handles = catalog.MetricBindings.Values.Where(static binding => binding.SourceId == "intel.msr").Select(static binding => binding.MetricHandle).ToArray();
        var modes = catalog.Sources.Select(static source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle, 1,
            NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var initial = Publish(1, new() { [MetricFieldCode.IntelAperfCounter] = ulong.MaxValue, [MetricFieldCode.IntelMperfCounter] = 9007199254740993UL });
        AssertFeedbackCounter(initial, target.ExactIdentityKey, "18446744073709551615");
        var wire = JsonSerializer.Serialize(MetricDeviceFields.Read(initial, target.ExactIdentityKey), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"integerValue\":\"9007199254740993\"", wire);
        AssertFeedbackCounter(Publish(2, []), target.ExactIdentityKey, null);
        AssertFeedbackCounter(Publish(3, new() { [MetricFieldCode.IntelAperfCounter] = 0 }), target.ExactIdentityKey, "0");
        HardwareMetricSnapshot Publish(ulong sequence, Dictionary<MetricFieldCode, ulong> values)
        {
            var at = now.AddSeconds(sequence); var plan = workspace.PlanCollection(handles, modes, false, at); var source = Assert.Single(plan.Sources);
            workspace.CompleteSource(plan, WindowsHardwareMetricSampler.CreateIntelMsrCompletion(plan, source, new(source.SourceHandle, 1, sequence), at,
                [new(target.ExactIdentityKey, new Dictionary<MetricFieldCode, double>()) { UnsignedValues = values }]), at);
            return NativeMetricSnapshotCommittedProjection.Create(frame: workspace.ReadCommitted(), workspaceIdentity: 1);
        }
    }

    private static void AssertFeedbackCounter(HardwareMetricSnapshot snapshot, string identity, string? integer)
    {
        var field = Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot, identity)).Fields, field => field.Name == "cpu_intel_aperf_counter_raw");
        Assert.Equal("unsigned64", field.ValueKind); Assert.Equal("count", field.Unit); Assert.Null(field.NumericValue); Assert.Equal(integer is null ? null : MetricInteger.Parse(integer), field.IntegerValue);
        Assert.Equal(integer is null ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current, field.Status);
    }
}
