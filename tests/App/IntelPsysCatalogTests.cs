using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;

namespace Resource_Manager_APP.Tests;

public sealed class IntelPsysCatalogTests
{
    private static IntelMsrCatalogIdentity Platform()
        => new(0, "windows-intel-psys-platform|windows-cpu-package|1:5", new(1, 5),
            0x406E0, null, IntelMsrTargetKind.Platform, false, false);

    [Fact]
    public void OriginalCatalogPublishesOnlyTwoExactSinglePlatformRawEvidenceFields()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var templates = plan.HotPublish.RuleTemplates.Where(static template => template.ScopeExpansion == 15).ToArray();
        Assert.Equal(2, templates.Length);
        Assert.All(templates, template =>
        {
            Assert.True(template.IsPublished);
            Assert.Equal("intel.msr", template.SourceId);
            Assert.Equal(1U, template.ScopeKind);
            Assert.Equal(13U, template.MetricKind);
            Assert.Equal(1U, template.MaximumInstanceCount);
            Assert.Equal(2U, template.RetentionPolicy);
        });
        var platform = Platform();
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(plan, [], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty, intelCpus: [platform]);
        var fields = catalog.MetricBindings.Values.Where(binding => binding.DeviceIdentity == platform.ExactIdentityKey).ToArray();
        Assert.Equal(2, fields.Length);
        Assert.All(fields, binding =>
        {
            Assert.Equal(MetricResource.System, binding.Field!.Resource);
            Assert.Equal(MetricVendor.Intel, binding.Field.Vendor);
            Assert.Empty(binding.LegacyMetricId);
            Assert.EndsWith('/' + binding.Field.Name, binding.MetricId);
            Assert.Equal(0, binding.DisplayIndex);
        });
        var counter = Assert.Single(fields, binding => binding.FieldCode == MetricFieldCode.IntelPsysEnergyCounter).Field!;
        Assert.Equal("unsigned64", counter.ValueKind);
        Assert.Equal("count", counter.Unit);
        Assert.Contains("zero does not prove", counter.Meaning);
        var unit = Assert.Single(fields, binding => binding.FieldCode == MetricFieldCode.IntelPsysEnergyUnit).Field!;
        Assert.Equal("float64", unit.ValueKind);
        Assert.Equal("J/count", unit.Unit);
        Assert.DoesNotContain(fields, binding => binding.Field!.Name.EndsWith("watts", StringComparison.Ordinal));
        Assert.DoesNotContain(NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(plan, [], [], [],
            catalog.HandleMap).MetricBindings.Values, binding => binding.FieldCode is
                MetricFieldCode.IntelPsysEnergyCounter or MetricFieldCode.IntelPsysEnergyUnit);
    }

    [Fact]
    public void PlatformScopeCannotBeExpandedToMultipleTargetsOrANonzeroOrdinal()
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var templates = root["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray();
            templates.Single(template => template!["template_id"]!.GetValue<string>() ==
                "system_intel_psys_energy_counter_raw.optional")!["maximum_instance_count"] = 2;
        }));
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var platform = Platform();
        Assert.Throws<InvalidOperationException>(() => NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty,
            intelCpus: [platform, platform with { DisplayIndex = 1, ExactIdentityKey = platform.ExactIdentityKey + "-other" }]));
        Assert.Throws<InvalidOperationException>(() => NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            plan, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty,
            intelCpus: [platform with { DisplayIndex = 1 }]));
    }
}
