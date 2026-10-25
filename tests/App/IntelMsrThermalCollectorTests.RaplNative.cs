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
    public void RaplIntegersAndConvertedValuesUseOriginalAtomicCommitClearAndRecoverIndependently()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"intel-rapl-{Guid.NewGuid():N}.bin"));
        var device = RaplPackage();
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], workspace.CreateCatalogHandleMap(), intelCpus: [device]);
        var now = DateTimeOffset.UtcNow; workspace.ReplaceCatalog(catalog, now);
        var bindings = catalog.MetricBindings.Values.Where(binding => binding.SourceId == "intel.msr" && IntelRaplFieldCatalog.Find(binding.FieldCode) is not null).ToArray();
        Assert.Equal(16, bindings.Length);
        Assert.All(bindings, binding => { Assert.Equal(device.ExactIdentityKey, binding.DeviceIdentity); Assert.Empty(binding.LegacyMetricId); });
        var handles = bindings.Select(static binding => binding.MetricHandle).ToArray();
        var modes = catalog.Sources.Select(static source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle, 1,
            NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var first = Publish(1, new(device.ExactIdentityKey, new Dictionary<MetricFieldCode, double>
        { [MetricFieldCode.IntelPackageEnergyJoules] = 255.99999994039536 })
        { UnsignedValues = new Dictionary<MetricFieldCode, ulong> { [MetricFieldCode.IntelPackageEnergyCounter] = uint.MaxValue } });
        AssertRaplCounter(first, device.ExactIdentityKey, "4294967295");
        var json = JsonSerializer.Serialize(MetricDeviceFields.Read(first, device.ExactIdentityKey), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"integerValue\":\"4294967295\"", json);
        Assert.Equal(255.99999994039536, Assert.Single(Assert.Single(MetricDeviceFields.Read(first, device.ExactIdentityKey)).Fields,
            field => field.Name == "cpu_intel_package_energy_reported_joules").NumericValue);
        var failed = Publish(2, new(device.ExactIdentityKey, new Dictionary<MetricFieldCode, double>()));
        AssertRaplCounter(failed, device.ExactIdentityKey, null);
        Assert.All(Assert.Single(MetricDeviceFields.Read(failed, device.ExactIdentityKey)).Fields,
            field => { Assert.NotEqual(SamplingObservationStatus.Current, field.Status); Assert.Null(field.IntegerValue); Assert.Null(field.NumericValue); });
        var recovered = Publish(3, new(device.ExactIdentityKey, new Dictionary<MetricFieldCode, double>())
        { UnsignedValues = new Dictionary<MetricFieldCode, ulong> { [MetricFieldCode.IntelPackageEnergyCounter] = 0 } });
        AssertRaplCounter(recovered, device.ExactIdentityKey, "0");

        HardwareMetricSnapshot Publish(ulong sequence, IntelMsrMetricRead value)
        {
            var at = now.AddSeconds(sequence); var plan = workspace.PlanCollection(handles, modes, false, at);
            var source = Assert.Single(plan.Sources);
            workspace.CompleteSource(plan, WindowsHardwareMetricSampler.CreateIntelMsrCompletion(plan, source,
                new(source.SourceHandle, 1, sequence), at, [value]), at);
            return NativeMetricSnapshotCommittedProjection.Create(frame: workspace.ReadCommitted(), workspaceIdentity: 1);
        }
    }

    [Fact]
    public void FullIntelTemperatureAndFourDomainExpansionFitsExisting64MiBBudget()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"rapl-full-{Guid.NewGuid():N}.bin"));
        var cpus = Enumerable.Range(0, 256).Select(static index => Core(index)).Concat(Enumerable.Range(0, 16)
            .Select(static index => RaplPackage() with { DisplayIndex = index, ExactIdentityKey = $"windows-cpu-package|{index}:0", Processor = new((ushort)index, 0) })).ToArray();
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], workspace.CreateCatalogHandleMap(), intelCpus: cpus);
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var bindings = catalog.MetricBindings.Values.Where(static binding => binding.SourceId == "intel.msr").ToArray();
        Assert.Equal(1136, bindings.Length); Assert.Equal(1136, bindings.Select(static binding => binding.MetricHandle).Distinct().Count());
        Assert.Equal(272, bindings.Select(static binding => binding.DeviceIdentity).Distinct().Count());
        Assert.Equal(64UL * 1024 * 1024, compiled.Recreate.Capacity.ResidentByteBudget);
        Assert.Equal(13_488U, compiled.Recreate.Capacity.MaximumMetricCount);
        var server = RaplPackage(0x306F0);
        var restricted = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty, intelCpus: [server]);
        Assert.Equal(8, restricted.MetricBindings.Values.Count(binding => IntelRaplFieldCatalog.Find(binding.FieldCode) is not null));
        Assert.DoesNotContain(restricted.MetricBindings.Values, binding => binding.FieldCode is MetricFieldCode.IntelPp0EnergyCounter or MetricFieldCode.IntelPp1EnergyCounter);
        var changed = server with { RaplProfile = server.RaplProfile with { FixedDramUnitJoules = 61e-6 } };
        var next = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], restricted.HandleMap, intelCpus: [changed]);
        Assert.False(HostManagerMetricSnapshotOwner.CanRetainCatalog(restricted, next));
    }

    private static void AssertRaplCounter(HardwareMetricSnapshot snapshot, string identity, string? integer)
    {
        var field = Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot, identity)).Fields,
            field => field.Name == "cpu_intel_package_energy_counter_raw");
        Assert.Equal("count", field.Unit); Assert.Equal("unsigned64", field.ValueKind); Assert.Null(field.NumericValue);
        Assert.Equal(integer is null ? null : MetricInteger.Parse(integer), field.IntegerValue);
        Assert.Equal(integer is null ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current, field.Status);
    }
}
