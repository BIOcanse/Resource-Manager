using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    [Fact]
    public void OriginalNativeWorkspaceFitsExplicitBudgetCommitsAndClearsValuesWithoutASecondCache()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        Assert.Equal(13_504U, compiled.Recreate.Capacity.MaximumMetricCount);
        Assert.Equal(64UL * 1024 * 1024, compiled.Recreate.Capacity.ResidentByteBudget);
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"intel-msr-{Guid.NewGuid():N}.bin"));
        var core = Core(); var package = Package();
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], workspace.CreateCatalogHandleMap(), intelCpus: [core, package]);
        var now = DateTimeOffset.UtcNow; workspace.ReplaceCatalog(catalog, now);
        var bindings = catalog.MetricBindings.Values.Where(static binding => binding.SourceId == "intel.msr").ToArray();
        Assert.Equal(6, bindings.Length);
        var handles = bindings.Select(static binding => binding.MetricHandle).ToArray();
        var modes = catalog.Sources.Select(static source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle, 1,
            NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var first = Publish(1, [new(core.ExactIdentityKey, Values(false, 80)), new(package.ExactIdentityKey, Values(true, 90))]);
        AssertTemperature(first, core.ExactIdentityKey, false, 80); AssertTemperature(first, package.ExactIdentityKey, true, 90);
        var wire = JsonSerializer.Serialize(MetricDeviceFields.Read(first, core.ExactIdentityKey), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("cpu_intel_core_dts_temperature", wire); Assert.Contains("\"numericValue\":80", wire);
        var failed = Publish(2, [new(package.ExactIdentityKey, Values(true, 91))]);
        AssertTemperature(failed, core.ExactIdentityKey, false, null); AssertTemperature(failed, package.ExactIdentityKey, true, 91);
        var recovered = Publish(3, [new(core.ExactIdentityKey, Values(false, 81))]);
        AssertTemperature(recovered, core.ExactIdentityKey, false, 81); AssertTemperature(recovered, package.ExactIdentityKey, true, null);

        HardwareMetricSnapshot Publish(ulong sequence, IReadOnlyList<IntelMsrMetricRead> values)
        {
            var at = now.AddSeconds(sequence); var plan = workspace.PlanCollection(handles, modes, false, at);
            var source = Assert.Single(plan.Sources);
            var completion = WindowsHardwareMetricSampler.CreateIntelMsrCompletion(plan, source, new(source.SourceHandle, 1, sequence), at, values);
            workspace.CompleteSource(plan, completion, at);
            return NativeMetricSnapshotCommittedProjection.Create(frame: workspace.ReadCommitted(), workspaceIdentity: 1);
        }
    }

    [Fact]
    public void FullConfiguredIntelExpansionFitsOriginalWorkspaceAndDoesNotAliasCoreOrPackageTargets()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"intel-capacity-{Guid.NewGuid():N}.bin"));
        var cpus = Enumerable.Range(0, 256).Select(static index => Core(index)).Concat(Enumerable.Range(0, 16)
            .Select(static index => Package() with { DisplayIndex = index, ExactIdentityKey = $"windows-cpu-package|{index}:0", Processor = new((ushort)index, 0) })).ToArray();
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], workspace.CreateCatalogHandleMap(), intelCpus: cpus);
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var bindings = catalog.MetricBindings.Values.Where(static binding => binding.SourceId == "intel.msr").ToArray();
        Assert.Equal(816, bindings.Length); Assert.Equal(272, bindings.Select(static binding => binding.DeviceIdentity).Distinct().Count());
        Assert.Equal(816, bindings.Select(static binding => binding.MetricHandle).Distinct().Count());
    }

    private static Dictionary<MetricFieldCode, double> Values(bool package, double temperature)
    {
        var codes = Codes(package); return new() { [codes[0]] = 100, [codes[1]] = 100 - temperature, [codes[2]] = temperature };
    }
    private static void AssertTemperature(HardwareMetricSnapshot snapshot, string identity, bool package, double? temperature)
    {
        var fields = Assert.Single(MetricDeviceFields.Read(snapshot, identity)).Fields;
        var name = package ? "cpu_intel_package_dts_temperature" : "cpu_intel_core_dts_temperature";
        var field = Assert.Single(fields, field => field.Name == name);
        Assert.Equal("°C", field.Unit); Assert.Equal("float64", field.ValueKind); Assert.Equal(temperature, field.NumericValue);
        Assert.Equal(temperature.HasValue ? SamplingObservationStatus.Current : SamplingObservationStatus.Unavailable, field.Status);
    }
}
