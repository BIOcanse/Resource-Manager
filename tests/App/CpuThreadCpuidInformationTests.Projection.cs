using System.Text.Json;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class CpuThreadCpuidInformationTests
{
    [Fact]
    public void TypedProjectionKeepsPerProcessorValuesSourcesAndVendorApplicability()
    {
        var snapshot = Snapshot();
        var fields = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var intel = Device(fields, "windows-cpu-thread|0:1");
        var amd = Device(fields, "windows-cpu-thread|2:7");
        Assert.Equal("GenuineIntel", Field(intel, "cpu_all_cpuid_vendor").Value!.Value.GetString());
        Assert.Equal("AuthenticAMD", Field(amd, "cpu_all_cpuid_vendor").Value!.Value.GetString());
        Assert.Equal("4294967295", Field(intel, "cpu_intel_x2apic_id").Value!.Value.GetString());
        Assert.Equal("2166572391", Field(amd, "cpu_amd_extended_apic_id").Value!.Value.GetString());
        Assert.Equal("254", Field(intel, "cpu_all_initial_apic_id").Value!.Value.GetString());
        Assert.Equal("135", Field(amd, "cpu_all_logical_processor_id").Value!.Value.GetString());
        Assert.Equal("25", Field(amd, "cpu_all_cpuid_family").Value!.Value.GetString());
        Assert.Equal("intel.cpuid.1a", Field(intel, "cpu_intel_hardware_core_type").Definition.SourceId);
        Assert.Equal("enum8", Field(intel, "cpu_intel_hardware_core_type").Definition.Unit);
        Assert.DoesNotContain(intel.Fields, field => field.Definition.Vendor == "amd");
        Assert.DoesNotContain(amd.Fields, field => field.Definition.Vendor == "intel");
        Assert.Equal("0", Field(intel, "cpu_all_processor_group").Value!.Value.GetString());
        var raw = Field(intel, "cpu_all_cpuid_identity_registers").Value!.Value;
        Assert.Equal(uint.MaxValue, raw.EnumerateArray().Single(row => row.GetProperty("leaf").GetUInt32() == 0x1F)
            .GetProperty("edx").GetUInt32());
        var json = JsonSerializer.Serialize(fields, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"value\":\"4294967295\"", json);
        var os = Device(fields, "windows-cpu-os-features");
        Assert.Equal("windows.processor-features", Field(os, "cpu_all_os_usable_feature").Definition.SourceId);
        Assert.Equal(new[] { "SSE2" }, Field(os, "cpu_all_os_usable_feature").Value!.Value.EnumerateArray().Select(static item => item.GetString()));
    }

    [Fact]
    public void ProcessorFailureClearsOnlyItsCpuidAndKeepsKnownVendorDefinitions()
    {
        var snapshot = Snapshot();
        var current = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var failed = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
            { ThreadCpuid = [new(1, null), snapshot.OsTopologyInformation.ThreadCpuid![1]] } };
        var fields = CpuTopologyInformationProjection.Create(failed, current);
        var intel = Device(fields, "windows-cpu-thread|0:1");
        Assert.All(intel.Fields.Where(static field => field.Definition.SourceId is not ("windows.processor-core" or "windows.processor-time")),
            field => Assert.Null(field.Value));
        Assert.NotNull(Field(intel, "cpu_all_logical_processor_id").Value);
        Assert.Equal(20d, Field(intel, "cpu_all_logical_busy_ratio").Value!.Value.GetDouble());
        Assert.Contains(intel.Fields, field => field.Definition.Name == "cpu_intel_hardware_core_type");
        Assert.NotNull(Field(Device(fields, "windows-cpu-thread|2:7"), "cpu_amd_extended_apic_id").Value);
        Assert.NotNull(Field(Device(fields, "windows-cpu-os-features"), "cpu_all_os_usable_feature").Value);
        var recovered = CpuTopologyInformationProjection.Create(snapshot, fields);
        Assert.NotNull(Field(Device(recovered, "windows-cpu-thread|0:1"), "cpu_intel_hardware_core_type").Value);
    }

    [Fact]
    public void InitialFailureDeclaresOnlyCommonFieldsAndUnknownBrandIsNull()
    {
        var snapshot = Snapshot();
        var unreported = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with { ThreadCpuid = [new(1, null), new(135, null)] } };
        var fields = CpuTopologyInformationProjection.Create(unreported, DeviceInformationSnapshot.Empty);
        var first = Device(fields, "windows-cpu-thread|0:1");
        Assert.Equal(17, first.Fields.Count);
        Assert.Equal(20d, Field(first, "cpu_all_logical_busy_ratio").Value!.Value.GetDouble());
        Assert.DoesNotContain(first.Fields, field => field.Definition.Vendor != "all");
        Assert.Null(Field(first, "cpu_all_cpuid_vendor").Value);
        var reader = Registers("GenuineIntel");
        reader.Values[(0x80000000, 0)] = (unchecked((int)0x80000000), 0, 0, 0);
        var emptyBrand = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
            { ThreadCpuid = [new(1, WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read))] } };
        Assert.Null(Field(Device(CpuTopologyInformationProjection.Create(emptyBrand, fields), "windows-cpu-thread|0:1"), "cpu_all_brand").Value);
    }

    [Fact]
    public void GroupFailureOsFailureAndSuccessfulProcessorRemovalHaveSeparateEffects()
    {
        var snapshot = Snapshot();
        var current = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var failed = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with { ThreadCpuid = null, OsUsableFeatures = null } };
        var empty = CpuTopologyInformationProjection.Create(failed, current);
        Assert.Null(Field(Device(empty, "windows-cpu-thread|0:1"), "cpu_all_cpuid_vendor").Value);
        Assert.NotNull(Field(Device(empty, "windows-cpu-thread|0:1"), "cpu_all_processor_group").Value);
        Assert.All(Device(empty, "windows-cpu-os-features").Fields, field => Assert.Null(field.Value));
        var removed = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
            { PhysicalCores = [snapshot.OsTopologyInformation.PhysicalCores![0]], ThreadCpuid = [snapshot.OsTopologyInformation.ThreadCpuid![0]] } };
        Assert.DoesNotContain(CpuTopologyInformationProjection.Create(removed, current).Devices, device => device.DeviceIdentity == "windows-cpu-thread|2:7");
        Assert.All(CpuTopologyInformationProjection.Create(null, current).Devices.SelectMany(static device => device.Fields), field => Assert.Null(field.Value));
    }

    private static CpuTopologySnapshot Snapshot()
    {
        var existing = CpuTopologyInformationFieldTests.Snapshot();
        return existing with { OsTopologyInformation = existing.OsTopologyInformation! with
        {
            PhysicalCores = [new("core:0", 0, false, [1]), new("core:1", 9, false, [135])],
            ThreadCpuid = [new(1, WindowsCpuThreadCpuidReader.ReadRegisters(Registers("GenuineIntel").Read)),
                new(135, WindowsCpuThreadCpuidReader.ReadRegisters(Registers("AuthenticAMD").Read))],
            OsUsableFeatures = ["SSE2"]
        } };
    }
    private static DeviceInformationObject Device(DeviceInformationSnapshot snapshot, string identity)
        => Assert.Single(snapshot.Devices, device => device.DeviceIdentity == identity);
    private static DeviceInformationField Field(DeviceInformationObject device, string name)
        => Assert.Single(device.Fields, field => field.Definition.Name == name);
}
