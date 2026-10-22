using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    [Theory]
    [InlineData(0x50670)]
    [InlineData(0x80650)]
    public void XeonPhiModuleDtsIsNotPublishedAsCoreTemperatureEvenWhenCpuidDtsBitIsSet(uint signature)
    {
        var access = new Access { Signature = signature };
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(access.Read);
        var info = new CpuOsTopologyInformation([new("0", 0, false, [0])], [])
        { ThreadCpuid = [new(0, data)], Packages = [new(Package().ExactIdentityKey, [0])] };
        var result = IntelMsrTopologyProjection.Create(info);
        Assert.True(Assert.Single(result.Identities).IsPackage);
        var staleCore = Core() with { Signature = signature };
        var opens = 0;
        var read = Assert.Single(new IntelMsrReader(() => { opens++; throw new IOException(); }, () => access)
            .Read([staleCore], Request(staleCore, Codes(false))));
        Assert.Empty(read.Values); Assert.True(read.RequiresTopologyRefresh); Assert.Equal(0, opens);
    }

    [Fact]
    public void OriginalCpuInformationCreatesSeparateCoreAndPackageDomainsWithStaticApplicability()
    {
        var known = WindowsCpuThreadCpuidReader.ReadRegisters(new Access().Read);
        var unknown = WindowsCpuThreadCpuidReader.ReadRegisters(new Access { Signature = 0xF06E0 }.Read);
        var amd = known with { Identity = known.Identity with { Vendor = "AuthenticAMD" } };
        var info = new CpuOsTopologyInformation([new("0", 0, true, [1, 0]), new("1", 0, false, [2]), new("2", 0, false, [3])], [])
        { ThreadCpuid = [new(0, known), new(1, known), new(2, unknown), new(3, amd)], Packages = [new(Package().ExactIdentityKey, [0, 1])] };
        var result = IntelMsrTopologyProjection.Create(info);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, result.CoreStatus);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, result.PackageStatus);
        Assert.Equal(3, result.Identities.Count);
        Assert.Contains(result.Identities, target => target.ExactIdentityKey == Core().ExactIdentityKey && target.Processor == new CpuProcessorAddress(0, 0));
        var cpu = Assert.Single(result.Identities, target => target.Processor == new CpuProcessorAddress(0, 2));
        Assert.True(cpu.AppliesTo(MetricFieldCode.IntelCoreDtsDistance));
        Assert.False(cpu.AppliesTo(MetricFieldCode.IntelCoreDtsTemperature));
        Assert.False(cpu.AppliesTo(MetricFieldCode.IntelCoreTemperatureTarget));
        Assert.False(cpu.AppliesTo(MetricFieldCode.IntelPackageDtsDistance));
        Assert.True(Assert.Single(result.Identities, target => target.IsPackage).AppliesTo(MetricFieldCode.IntelPackageDtsTemperature));
    }

    [Fact]
    public void CoreAndPackageDirectoryFailureAndSuccessfulEmptyAreIndependent()
    {
        var known = WindowsCpuThreadCpuidReader.ReadRegisters(new Access().Read);
        var info = new CpuOsTopologyInformation([new("0", 0, false, [4])], [])
        { ThreadCpuid = [new(2, known)], Packages = [new("windows-cpu-package|0:2", [2])] };
        var partial = IntelMsrTopologyProjection.Create(info);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, partial.CoreStatus);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, partial.PackageStatus);
        Assert.True(Assert.Single(partial.Identities).IsPackage);
        var empty = IntelMsrTopologyProjection.Create(new([], []) { Packages = [] });
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, empty.CoreStatus);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, empty.PackageStatus); Assert.Empty(empty.Identities);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, IntelMsrTopologyProjection.Create(null).CoreStatus);
    }

    [Fact]
    public void CompilationUsesCanonicalFieldsExplicitScopesAndVerifiedPerInstanceCapabilities()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        Assert.True(compiled.IsPublished);
        var intelSource = Assert.Single(compiled.HotPublish.SourcePolicies,
            static source => source.SourceId == "intel.msr");
        Assert.Equal(18UL, intelSource.SourceHandle);
        var rules = compiled.HotPublish.RuleTemplates
            .Where(rule => rule.SourceHandle == intelSource.SourceHandle).ToArray();
        var effectiveFrequency = Assert.Single(rules,
            static rule => rule.TemplateId == "cpu_intel_effective_frequency_mhz.optional");
        var originalRules = rules.Where(rule => rule.TemplateId != effectiveFrequency.TemplateId).ToArray();
        Assert.Equal(61, originalRules.Length);
        Assert.All(originalRules, rule => Assert.True(rule.IsPublished));
        Assert.Equal(5506U, originalRules.Aggregate(0U,
            static (sum, rule) => sum + rule.MaximumInstanceCount));
        Assert.Equal(62, rules.Length);
        Assert.Equal(6018U, rules.Aggregate(0U,
            static (sum, rule) => sum + rule.MaximumInstanceCount));
        Assert.True(effectiveFrequency.IsPublished);
        Assert.Equal("intel.msr", effectiveFrequency.SourceId);
        Assert.Equal(intelSource.SourceHandle, effectiveFrequency.SourceHandle);
        Assert.Equal("thread.{thread}.intelEffectiveFrequencyMhz", effectiveFrequency.MetricIdTemplate);
        Assert.Equal(12U, effectiveFrequency.ScopeExpansion);
        Assert.Equal(2U, effectiveFrequency.ScopeKind);
        Assert.Equal((uint)NativeMetricSnapshotMetricKind.CounterFrequency, effectiveFrequency.MetricKind);
        Assert.Equal(512U, effectiveFrequency.MaximumInstanceCount);
        Assert.All(rules.Where(rule => rule.ScopeExpansion == 10), rule => { Assert.Equal(2U, rule.ScopeKind); Assert.Equal(256U, rule.MaximumInstanceCount); });
        Assert.All(rules.Where(rule => rule.ScopeExpansion == 11), rule => { Assert.Equal(2U, rule.ScopeKind); Assert.Equal(16U, rule.MaximumInstanceCount); });
        var known = Core(); var unknown = Core(1) with { SupportsTemperatureTarget = false };
        var unsupported = Core(2) with { SupportsDts = false };
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty,
            cpuVendor: MetricVendor.Amd, intelCpus: [known, unknown, unsupported, Package()]);
        var bindings = catalog.MetricBindings.Values.Where(binding => binding.SourceId == "intel.msr").ToArray();
        Assert.Equal(7, bindings.Length);
        Assert.DoesNotContain(bindings,
            static binding => binding.FieldCode == MetricFieldCode.IntelEffectiveFrequencyMhz);
        Assert.All(bindings, binding => { Assert.EndsWith('/' + binding.Field!.Name, binding.MetricId); Assert.Empty(binding.LegacyMetricId);
            Assert.Equal(MetricVendor.Intel, binding.Field.Vendor); Assert.Equal("°C", binding.Field.Unit); Assert.Equal("float64", binding.Field.ValueKind); });
        Assert.Equal(MetricFieldCode.IntelCoreDtsDistance, Assert.Single(bindings, binding => binding.DeviceIdentity == unknown.ExactIdentityKey).FieldCode);
        Assert.DoesNotContain(bindings, binding => binding.DeviceIdentity == unsupported.ExactIdentityKey);
        Assert.Equal(7, bindings.Select(static binding => binding.MetricHandle).Distinct().Count());
    }

    [Theory]
    [InlineData("address")]
    [InlineData("signature")]
    [InlineData("apic")]
    [InlineData("capability")]
    public void TargetBindingChangesRebuildOriginalCatalogEvenWhenDeviceIdentityStaysTheSame(string change)
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot; var device = Core();
        var first = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty, intelCpus: [device]);
        var changed = change switch { "address" => device with { Processor = new(0, 1) }, "signature" => device with { Signature = 0xA06E0 },
            "apic" => device with { X2ApicId = 9 }, _ => device with { SupportsTemperatureTarget = false } };
        var second = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], first.HandleMap, intelCpus: [changed]);
        Assert.False(HostManagerMetricSnapshotOwner.CanRetainCatalog(first, second));
        var reordered = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], first.HandleMap, intelCpus: [device]);
        Assert.True(HostManagerMetricSnapshotOwner.CanRetainCatalog(first, reordered));
    }

    [Fact]
    public void FailedDirectoryRetainsOnlyItsPriorDeclarationsWhileSuccessfulEmptyRemovesThem()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var prior = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty, intelCpus: [Core(), Package()]);
        var partial = HostManagerMetricSnapshotOwner.RetainUnavailableOptionalIdentities(Topology([]) with
        { IntelCpuCoreStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable }, prior);
        Assert.False(Assert.Single(partial.IntelCpuIdentities).IsPackage);
        var reverse = HostManagerMetricSnapshotOwner.RetainUnavailableOptionalIdentities(Topology([]) with
        { IntelCpuPackageStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable }, prior);
        Assert.True(Assert.Single(reverse.IntelCpuIdentities).IsPackage);
        Assert.Empty(HostManagerMetricSnapshotOwner.RetainUnavailableOptionalIdentities(Topology([]), prior).IntelCpuIdentities);
    }

    [Fact]
    public void DuplicateAndConfiguredExcessTargetsAreRejectedAtOriginalCatalogBoundary()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        Assert.Throws<InvalidOperationException>(() => NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty, intelCpus: [Core(), Core()]));
        var limited = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var templates = root["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray();
            templates.Single(template => template!["template_id"]!.GetValue<string>() == "cpu_intel_core_dts_temperature.optional")!["maximum_instance_count"] = 1;
        }).MetricSnapshot;
        Assert.Throws<InvalidOperationException>(() => NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(limited, [], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty, intelCpus: [Core(), Core(1)]));
    }

    private static NativeMetricSnapshotCatalogTopologySnapshot Topology(IReadOnlyList<IntelMsrCatalogIdentity> devices)
        => new(1, NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, [],
            NativeMetricSnapshotCatalogTopologyStatus.Complete, []) { IntelCpuCoreStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete,
            IntelCpuPackageStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete,
            IntelCpuLogicalProcessorStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete,
            IntelCpuDieStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete, IntelCpuIdentities = devices };
}
