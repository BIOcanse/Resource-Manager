using System.Reflection;
using System.Text.Json;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed class CpuTopologyInformationFieldTests
{
    internal static CpuTopologySnapshot Snapshot() => CpuTopologyProviderTestFixture.CreateSnapshot(12) with
    {
        // Deliberately inconsistent legacy counts: they must never become canonical OS facts.
        PhysicalCoreCount = 99, LogicalProcessorCount = 100, SimultaneousMultithreading = false,
        OsTopologyInformation = new([
            new("core:0", 0, true, [1, 3]), new("core:1", 9, false, [135])],
            [new("windows-cpu-cache|3|0|1,3,135", 3, uint.MaxValue, 64, 255, 0, [1, 3, 135])])
        {
            LogicalProcessorBusyPercent = BusyValues()
        }
    };

    internal static double?[] BusyValues()
    {
        var values = new double?[136];
        values[1] = 20; values[3] = 80; values[135] = 37.123456789;
        return values;
    }

    [Fact]
    public void CanonicalValuesComeOnlyFromOsRelationsWithExplicitTypesAndUnits()
    {
        var snapshot = Snapshot();
        var fields = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        Assert.Equal(snapshot.CapturedAt, fields.ObservedAt);
        var aggregate = Assert.Single(fields.Devices, device => device.ObjectType == "cpu_topology");
        Assert.Equal("2", Field(aggregate, "cpu_all_physical_core_count_reported").Value!.Value.GetString());
        Assert.Equal("3", Field(aggregate, "cpu_all_logical_processor_count_reported").Value!.Value.GetString());
        Assert.True(Field(aggregate, "cpu_all_smt_enabled").Value!.Value.GetBoolean());
        var core = Assert.Single(fields.Devices, device => device.DeviceIdentity == "windows-cpu-core|135");
        Assert.Equal("9", Field(core, "cpu_all_os_efficiency_class").Value!.Value.GetString());
        var address = Field(core, "cpu_all_core_logical_processor_addresses").Value!.Value[0];
        Assert.Equal(2, address.GetProperty("processorGroup").GetInt32());
        Assert.Equal(7, address.GetProperty("groupRelativeIndex").GetInt32());
        var thread = Assert.Single(fields.Devices, device => device.DeviceIdentity == "windows-cpu-thread|2:7");
        Assert.Equal("135", Field(thread, "cpu_all_logical_processor_id").Value!.Value.GetString());
        Assert.Equal("core:1", Field(thread, "cpu_all_physical_core_id").Value!.Value.GetString());
        Assert.All(fields.Devices.SelectMany(static device => device.Fields), field =>
        {
            Assert.Equal("cpu", field.Definition.Resource); Assert.Equal("all", field.Definition.Vendor);
            Assert.StartsWith("windows.processor-", field.Definition.SourceId);
            Assert.DoesNotContain("apic", field.Definition.Quantity);
        });
    }

    [Fact]
    public void CacheKeepsLosslessUnsignedBytesRawAssociativityAndArrayMembership()
    {
        var fields = CpuTopologyInformationProjection.Create(Snapshot(), DeviceInformationSnapshot.Empty);
        var cache = Assert.Single(fields.Devices, device => device.ObjectType == "cpu_cache");
        var capacity = Field(cache, "cpu_all_cache_capacity");
        Assert.Equal("B", capacity.Definition.Unit);
        Assert.Equal("unsigned-integer", capacity.Definition.ValueKind);
        Assert.Equal("4294967295", capacity.Value!.Value.GetString());
        Assert.Equal("255", Field(cache, "cpu_all_cache_associativity").Value!.Value.GetString());
        Assert.Equal("associativity8", Field(cache, "cpu_all_cache_associativity").Definition.Unit);
        Assert.Equal([1, 3, 135], Field(cache, "cpu_all_cache_shared_logical_processor_ids").Value!.Value
            .EnumerateArray().Select(static value => value.GetInt32()));
    }

    [Fact]
    public void LegacyFallbackCpuidAndCacheValuesCannotFillCanonicalFields()
    {
        var old = Snapshot();
        var current = CpuTopologyInformationProjection.Create(old, DeviceInformationSnapshot.Empty);
        var fallback = old with { OsTopologyInformation = null, Caches = old.OsTopologyInformation!.Caches! };
        var empty = CpuTopologyInformationProjection.Create(fallback, current);
        Assert.Equal(current.Devices.Select(static device => device.DeviceIdentity), empty.Devices.Select(static device => device.DeviceIdentity));
        Assert.All(empty.Devices.SelectMany(static device => device.Fields), field => Assert.Null(field.Value));
        Assert.Empty(CpuTopologyInformationProjection.Create(fallback, DeviceInformationSnapshot.Empty).Devices);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneUnavailableGroupClearsOnlyItsKnownValues(bool failCores)
    {
        var snapshot = Snapshot();
        var current = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var partial = snapshot with { OsTopologyInformation = failCores
            ? snapshot.OsTopologyInformation! with { PhysicalCores = null }
            : snapshot.OsTopologyInformation! with { Caches = null } };
        var projected = CpuTopologyInformationProjection.Create(partial, current);
        Assert.All(projected.Devices, device => Assert.All(device.Fields, field =>
        {
            if ((device.ObjectType != "cpu_cache") == failCores) Assert.Null(field.Value);
            else Assert.NotNull(field.Value);
        }));
    }

    [Fact]
    public void WholeFailureRetainsDefinitionsButNotCurrentValuesOrTime()
    {
        var current = CpuTopologyInformationProjection.Create(Snapshot(), DeviceInformationSnapshot.Empty);
        var empty = CpuTopologyInformationProjection.Create(null, current);
        Assert.Null(empty.ObservedAt);
        Assert.Equal(current.Devices.Count, empty.Devices.Count);
        Assert.All(empty.Devices.SelectMany(static device => device.Fields), field => Assert.Null(field.Value));
        Assert.NotNull(current.Devices[0].Fields[0].Value);
    }

    [Fact]
    public void SuccessfulEmptyGroupsRemoveOldObjectsAndReportZeroCounts()
    {
        var current = CpuTopologyInformationProjection.Create(Snapshot(), DeviceInformationSnapshot.Empty);
        var empty = CpuTopologyInformationProjection.Create(Snapshot() with { OsTopologyInformation = new([], []) }, current);
        var aggregate = Assert.Single(empty.Devices);
        Assert.Equal("0", Field(aggregate, "cpu_all_physical_core_count_reported").Value!.Value.GetString());
        Assert.False(Field(aggregate, "cpu_all_smt_enabled").Value!.Value.GetBoolean());
    }

    [Fact]
    public void CpuDefinitionsAreUniqueAndEfficiencyIsACommonWindowsAttribute()
    {
        var definitions = typeof(DeviceInformationFieldRegistry).GetFields(BindingFlags.Static | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(DeviceInformationDefinition))
            .Select(field => (DeviceInformationDefinition)field.GetValue(null)!).Where(definition => definition.Resource == "cpu").ToArray();
        Assert.Equal(62, definitions.Length);
        Assert.Equal(definitions.Length, definitions.Select(static definition => definition.Name).Distinct().Count());
        Assert.Equal(new[] { "cpu_intel_avx10_version", "cpu_intel_bus_reference_frequency_declared_mhz",
            "cpu_intel_hardware_core_type", "cpu_intel_native_model_id", "cpu_intel_nominal_core_crystal_frequency_declared_hz",
            "cpu_intel_processor_base_frequency_declared_mhz", "cpu_intel_processor_maximum_frequency_declared_mhz",
            "cpu_intel_smx_supported", "cpu_intel_supported_xss_state_mask", "cpu_intel_tsc_to_core_crystal_ratio_declared",
            "cpu_intel_vmx_supported", "cpu_intel_x2apic_id" },
            definitions.Where(static definition => definition.Vendor == "intel").Select(static definition => definition.Name).Order());
        Assert.Single(definitions, definition => definition.Vendor == "amd" && definition.Name == "cpu_amd_extended_apic_id");
        Assert.DoesNotContain(definitions, definition => definition.Name == "cpu_intel_os_efficiency_class");
        Assert.Equal("all", Assert.Single(definitions, definition => definition.Name == "cpu_all_os_efficiency_class").Vendor);
        var socket = Assert.Single(definitions, definition => definition.SourceId == "smbios.type4");
        Assert.Equal("cpu_all_socket_designation", socket.Name);
        Assert.Equal("all", socket.Vendor);
        Assert.Equal("cpu.socket_designation_declared", socket.Quantity);
    }

    internal static DeviceInformationField Field(DeviceInformationObject device, string name)
        => Assert.Single(device.Fields, field => field.Definition.Name == name);
}
