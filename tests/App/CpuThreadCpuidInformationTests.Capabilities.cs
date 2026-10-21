using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class CpuThreadCpuidInformationTests
{
    [Theory]
    [InlineData("GenuineIntel", 0x00302F30U, 48, 47)]
    [InlineData("AuthenticAMD", 0x00302F30U, 48, 47)]
    [InlineData("GenuineIntel", 0xFFFFFFFFU, 255, 255)]
    [InlineData("AuthenticAMD", 0U, 0, 0)]
    public void DeclaredAddressWidthsPreserveExactBytesWithoutUsingModeOrMemorySize(string vendor, uint raw, byte physical, byte linear)
    {
        var reader = InstructionRegisters(vendor);
        reader.Values[(0x80000008, 0)] = (unchecked((int)raw), -1, -1, -1);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Equal(physical, data.PhysicalAddressWidthDeclaredBits);
        Assert.Equal(linear, data.LinearAddressWidthDeclaredBits);
        Assert.Equal(raw, data.IdentityRegisters.Single(value => value.Leaf == 0x80000008).Eax);
        if (vendor == "GenuineIntel") Assert.Null(data.AmdNestedGuestPhysicalAddressWidthBits);
        Assert.Equal(1, reader.Calls[(0x80000008, 0)]);
    }

    [Theory]
    [InlineData(0x00003034U, 52)]
    [InlineData(0x00303034U, 48)]
    [InlineData(0xFFFF3034U, 255)]
    public void AmdNestedGuestZeroUsesDocumentedPhysicalWidthAndRetainsOriginalRegister(uint raw, byte expected)
    {
        var reader = InstructionRegisters("AuthenticAMD");
        reader.Values[(0x80000008, 0)] = (unchecked((int)raw), 0, 0, 0);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Equal(expected, data.AmdNestedGuestPhysicalAddressWidthBits);
        Assert.Equal(raw, data.IdentityRegisters.Single(value => value.Leaf == 0x80000008).Eax);
    }

    [Theory]
    [InlineData("GenuineIntel", 0x80000007U)]
    [InlineData("AuthenticAMD", 0x80000007U)]
    [InlineData("CentaurHauls", 0x80000021U)]
    public void MissingAddressLeafOrUnknownVendorDoesNotInventWidths(string vendor, uint maximum)
    {
        var reader = InstructionRegisters(vendor);
        reader.Values[(0x80000000, 0)] = (unchecked((int)maximum), 0, 0, 0);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Null(data.PhysicalAddressWidthDeclaredBits); Assert.Null(data.LinearAddressWidthDeclaredBits);
        Assert.Null(data.AmdNestedGuestPhysicalAddressWidthBits);
        Assert.DoesNotContain((0x80000008U, 0U), reader.Calls.Keys);
    }

    [Theory]
    [InlineData(0, false, false)] [InlineData(1 << 5, true, false)]
    [InlineData(1 << 6, false, true)] [InlineData(-1, true, true)]
    public void IntelVmxAndSmxAreIndependentHardwareDeclarations(int bits, bool vmx, bool smx)
    {
        var reader = Registers("GenuineIntel");
        reader.Values[(1, 0)] = reader.Values[(1, 0)] with { Item3 = bits & ~(1 << 26) };
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Equal(vmx, data.IntelVmxSupported); Assert.Equal(smx, data.IntelSmxSupported);
        Assert.Equal(vmx, data.Identity.HardwareFeatures.Contains("VMX"));
        Assert.Equal(smx, data.Identity.HardwareFeatures.Contains("SMX"));
        Assert.Empty(data.Identity.OsUsableFeatures);
        Assert.Null(data.AmdSvmSupported);
    }

    [Theory]
    [InlineData("AuthenticAMD", 1U)]
    [InlineData("CentaurHauls", 1U)]
    [InlineData("GenuineIntel", 0U)]
    [InlineData("GenuineIntel", 0x80000000U)]
    public void UnsupportedIntelIdentityOrWrongVendorDoesNotApplyIntelVirtualizationBits(string vendor, uint maximum)
    {
        var reader = Registers(vendor, maximum, 0x80000000);
        reader.Values[(1, 0)] = reader.Values[(1, 0)] with { Item3 = 0x60 };
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Null(data.IntelVmxSupported); Assert.Null(data.IntelSmxSupported);
        Assert.DoesNotContain("VMX", data.Identity.HardwareFeatures); Assert.DoesNotContain("SMX", data.Identity.HardwareFeatures);
    }

    [Theory]
    [InlineData(0x80000000U, false, null)]
    [InlineData(0x80000001U, false, false)]
    [InlineData(0x80000009U, true, true)]
    [InlineData(0x8000000AU, false, false)]
    public void SvmDetailsRequireBothAmdSupportAndTheirOwnLeaf(uint maximum, bool supported, bool? expectedSupport)
    {
        var reader = InstructionRegisters("AuthenticAMD");
        reader.Values[(0x80000000, 0)] = (unchecked((int)maximum), 0, 0, 0);
        reader.Values[(0x80000001, 0)] = (0, 0, supported ? 4 : 0, 0);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Equal(expectedSupport, data.AmdSvmSupported);
        Assert.Null(data.AmdSvmRevision); Assert.Null(data.AmdSvmAsidCountDeclared); Assert.Null(data.AmdSvmFeatures);
        Assert.DoesNotContain((0x8000000AU, 0U), reader.Calls.Keys);
    }

    [Fact]
    public void IntelDoesNotQueryAmdSvmLeafEvenWithMatchingRawExtendedBits()
    {
        var reader = InstructionRegisters("GenuineIntel");
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Null(data.AmdSvmSupported); Assert.Null(data.AmdSvmRevision);
        Assert.Null(data.AmdSvmAsidCountDeclared); Assert.Null(data.AmdSvmFeatures);
        Assert.DoesNotContain((0x8000000AU, 0U), reader.Calls.Keys);
        Assert.DoesNotContain("SVM", data.Identity.HardwareFeatures);
    }

    [Fact]
    public void SvmFullDeclarationsKeepRevisionAsidPrecisionAndAllDocumentedFeatureRegisters()
    {
        var reader = CapabilityRegisters("AuthenticAMD");
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.True(data.AmdSvmSupported); Assert.Equal(byte.MaxValue, data.AmdSvmRevision);
        Assert.Equal(uint.MaxValue, data.AmdSvmAsidCountDeclared);
        Assert.Equal(28, data.AmdSvmFeatures!.Count);
        foreach (var name in new[] { "x2AVIC_EXT", "NP", "SVML", "NRIPS", "AVIC", "x2AVIC", "VNMI", "IdleHltIntercept" })
            Assert.Contains(name, data.AmdSvmFeatures);
        Assert.All(reader.Calls.Values, count => Assert.Equal(1, count));
        Assert.Contains("SVM", data.Identity.HardwareFeatures);
        var raw = data.IdentityRegisters.Single(value => value.Leaf == 0x8000000A);
        Assert.Equal(uint.MaxValue, raw.Eax); Assert.Equal(uint.MaxValue, raw.Ecx); Assert.Equal(uint.MaxValue, raw.Edx);
        Assert.Empty(data.Identity.OsUsableFeatures);
    }

    [Fact]
    public void SupportedSvmZeroAndReservedBitsProduceKnownEmptyListWithoutGuessingFeatures()
    {
        var reader = CapabilityRegisters("AuthenticAMD");
        // EDX 31/22/14/11/9 and ECX except bit 6 are reserved in the verified AMD publication.
        reader.Values[(0x8000000A, 0)] = (unchecked((int)0xFFFFFF00), 0, ~(1 << 6),
            unchecked((int)((1U << 31) | (1U << 22) | (1U << 14) | (1U << 11) | (1U << 9))));
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.True(data.AmdSvmSupported); Assert.Equal((byte)0, data.AmdSvmRevision); Assert.Equal(0U, data.AmdSvmAsidCountDeclared);
        Assert.Empty(data.AmdSvmFeatures!);
        Assert.NotEqual(0U, data.IdentityRegisters.Single(value => value.Leaf == 0x8000000A).Edx);
    }

    [Theory]
    [InlineData("GenuineIntel")] [InlineData("AuthenticAMD")]
    public void WbnoinvdUsesTheVerifiedCommonExtendedBitAndLeavesOtherIntelReservedBitsUndecoded(string vendor)
    {
        var reader = Registers(vendor);
        reader.Values[(0x80000008, 0)] = (0, 1 << 9, 0, 0);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Contains("WBNOINVD", data.Identity.HardwareFeatures);
        Assert.DoesNotContain("CLZERO", data.Identity.HardwareFeatures);
        reader.Values[(0x80000008, 0)] = (0, ~(1 << 9), 0, 0);
        data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.DoesNotContain("WBNOINVD", data.Identity.HardwareFeatures);
        if (vendor == "GenuineIntel")
        {
            Assert.DoesNotContain("CLZERO", data.Identity.HardwareFeatures);
            Assert.DoesNotContain("MCOMMIT", data.Identity.HardwareFeatures);
        }
    }

    [Fact]
    public void TypedAddressAndSvmProjectionKeepsVendorApplicabilityFalseZeroAndLosslessIntegers()
    {
        var snapshot = CapabilitiesSnapshot();
        var fields = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var intel = Device(fields, "windows-cpu-thread|0:1");
        var amd = Device(fields, "windows-cpu-thread|2:7");
        Assert.Equal("52", Field(intel, "cpu_all_physical_address_width_declared_bits").Value!.Value.GetString());
        Assert.Equal("48", Field(intel, "cpu_all_linear_address_width_declared_bits").Value!.Value.GetString());
        Assert.Equal("52", Field(amd, "cpu_amd_nested_guest_physical_address_width_bits").Value!.Value.GetString());
        Assert.Equal("4294967295", Field(amd, "cpu_amd_svm_asid_count_declared").Value!.Value.GetString());
        Assert.Equal("255", Field(amd, "cpu_amd_svm_revision").Value!.Value.GetString());
        Assert.True(Field(amd, "cpu_amd_svm_supported").Value!.Value.GetBoolean());
        Assert.Equal(28, Field(amd, "cpu_amd_svm_feature").Value!.Value.GetArrayLength());
        Assert.Equal("bit", Field(intel, "cpu_all_physical_address_width_declared_bits").Definition.Unit);
        Assert.Equal("amd.cpuid.8000000a", Field(amd, "cpu_amd_svm_revision").Definition.SourceId);
        Assert.DoesNotContain(intel.Fields, field => field.Definition.Vendor == "amd");
        Assert.DoesNotContain(amd.Fields, field => field.Definition.Vendor == "intel");
        var zeroReader = Registers("GenuineIntel");
        var zero = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
            { ThreadCpuid = [new(1, WindowsCpuThreadCpuidReader.ReadRegisters(zeroReader.Read))] } };
        var zeros = Device(CpuTopologyInformationProjection.Create(zero, fields), "windows-cpu-thread|0:1");
        Assert.False(Field(zeros, "cpu_intel_vmx_supported").Value!.Value.GetBoolean());
        Assert.Equal("0", Field(zeros, "cpu_all_physical_address_width_declared_bits").Value!.Value.GetString());
    }

    [Fact]
    public void CapabilitiesFailureClearsOnlyFailedProcessorAndRecoveryRestoresExactValues()
    {
        var snapshot = CapabilitiesSnapshot();
        var current = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var failed = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
            { ThreadCpuid = [snapshot.OsTopologyInformation.ThreadCpuid![0], new(135, null)] } };
        var empty = CpuTopologyInformationProjection.Create(failed, current);
        foreach (var name in new[] { "cpu_all_physical_address_width_declared_bits", "cpu_amd_nested_guest_physical_address_width_bits",
            "cpu_amd_svm_supported", "cpu_amd_svm_revision", "cpu_amd_svm_asid_count_declared", "cpu_amd_svm_feature" })
            Assert.Null(Field(Device(empty, "windows-cpu-thread|2:7"), name).Value);
        Assert.NotNull(Field(Device(empty, "windows-cpu-thread|0:1"), "cpu_intel_vmx_supported").Value);
        Assert.NotNull(Field(Device(empty, "windows-cpu-os-features"), "cpu_all_os_usable_feature").Value);
        var recovered = CpuTopologyInformationProjection.Create(snapshot, empty);
        Assert.Equal("4294967295", Field(Device(recovered, "windows-cpu-thread|2:7"), "cpu_amd_svm_asid_count_declared").Value!.Value.GetString());
        Assert.All(CpuTopologyInformationProjection.Create(null, recovered).Devices.SelectMany(value => value.Fields), field => Assert.Null(field.Value));
    }

    [Fact]
    public void CpuWorkerKeepsDistinctCapabilitiesForHeterogeneousProcessorAddresses()
    {
        var access = new CapabilityAccess();
        var values = WindowsCpuThreadCpuidReader.Read([1, 135], access, new List<string>());
        Assert.True(values[0].Data!.IntelVmxSupported); Assert.False(values[1].Data!.IntelVmxSupported);
        Assert.Equal((byte)52, values[0].Data!.PhysicalAddressWidthDeclaredBits);
        Assert.Equal((byte)48, values[1].Data!.PhysicalAddressWidthDeclaredBits);
        Assert.All(access.Readers.SelectMany(value => value.Calls.Values), count => Assert.Equal(1, count));
    }

    private static RegisterReader CapabilityRegisters(string vendor)
    {
        var reader = InstructionRegisters(vendor);
        reader.Values[(0x80000008, 0)] = (0x3034, -1, -1, -1);
        reader.Values[(0x8000000A, 0)] = (-1, -1, -1, -1);
        return reader;
    }

    internal static CpuTopologySnapshot CapabilitiesSnapshot()
    {
        var snapshot = InstructionSnapshot();
        return snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
        {
            ThreadCpuid = [new(1, WindowsCpuThreadCpuidReader.ReadRegisters(CapabilityRegisters("GenuineIntel").Read)),
                new(135, WindowsCpuThreadCpuidReader.ReadRegisters(CapabilityRegisters("AuthenticAMD").Read))]
        } };
    }

    private sealed class CapabilityAccess : ICpuThreadCpuidAccess
    {
        private CpuProcessorAddress current = new(0, 0);
        internal RegisterReader[] Readers { get; } = [CapabilityRegisters("GenuineIntel"), CapabilityRegisters("GenuineIntel")];
        public bool Select(CpuProcessorAddress address) { current = address; return true; }
        public CpuProcessorAddress Current() => current;
        public (int, int, int, int) Read(int leaf, int subleaf)
        {
            var reader = Readers[current.ProcessorGroup == 0 ? 0 : 1];
            if (current.ProcessorGroup != 0)
            {
                reader.Values[(1, 0)] = reader.Values[(1, 0)] with { Item3 = -1 & ~(1 << 5) };
                reader.Values[(0x80000008, 0)] = (0x3030, 0, 0, 0);
            }
            return reader.Read(leaf, subleaf);
        }
    }
}
