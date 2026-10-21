using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class CpuThreadCpuidInformationTests
{
    [Theory]
    [InlineData("GenuineIntel")] [InlineData("AuthenticAMD")]
    public void CommonInstructionsKeepHighBitsSubsetsAndHardwareIndependentOfOs(string vendor)
    {
        var reader = InstructionRegisters(vendor);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        foreach (var name in new[] { "CMPXCHG16B", "MOVBE", "POPCNT", "XSAVEC", "XGETBV1", "AVX512VL",
            "AVX512VBMI2", "VAES", "VPCLMULQDQ", "AVX512VNNI", "AVX512BF16", "RDTSCP", "LZCNT" })
            Assert.Contains(name, data.Identity.HardwareFeatures);
        Assert.Empty(data.Identity.OsUsableFeatures);
        Assert.DoesNotContain("OSXSAVE", data.Identity.HardwareFeatures);
        Assert.DoesNotContain("OSPKE", data.Identity.HardwareFeatures);
        Assert.Equal(data.Identity.HardwareFeatures.Count, data.Identity.HardwareFeatures.Distinct().Count());
        Assert.All(reader.Calls.Values, count => Assert.Equal(1, count));
        Assert.DoesNotContain(reader.Calls.Keys, key => key.Item1 == 7 && key.Item2 > 1);
    }

    [Fact]
    public void IntelDefinitionsDoNotLeakFromAmdReservedStructuredBits()
    {
        var intel = WindowsCpuThreadCpuidReader.ReadRegisters(InstructionRegisters("GenuineIntel").Read);
        var amd = WindowsCpuThreadCpuidReader.ReadRegisters(InstructionRegisters("AuthenticAMD").Read);
        foreach (var name in new[] { "HLE", "MPX", "AVX512PF", "WAITPKG", "SERIALIZE", "AMXTILE", "SHA512",
            "SM3", "SM4", "CMPCCXADD", "PBNDKB", "AVXVNNIINT8", "AVX10", "USER_MSR", "APX_F", "SYSCALL_SYSRET_64" })
        {
            Assert.Contains(name, intel.Identity.HardwareFeatures);
            Assert.DoesNotContain(name, amd.Identity.HardwareFeatures);
        }
        Assert.Null(amd.IntelAvx10Version);
        Assert.Equal(uint.MaxValue, amd.IdentityRegisters.Single(row => row.Leaf == 7 && row.Subleaf == 1).Edx);
        Assert.Contains("PREFETCHI", amd.Identity.HardwareFeatures); // AMD uses 80000021, not Intel's 7.1.
        var reader = InstructionRegisters("AuthenticAMD");
        reader.Values[(0x80000021, 0)] = (0, 0, 0, 0);
        Assert.DoesNotContain("PREFETCHI", WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read).Identity.HardwareFeatures);
    }

    [Fact]
    public void AmdExtensionsRemainDistinctFromIntelAndFromCommonInstructionFamilies()
    {
        var amd = WindowsCpuThreadCpuidReader.ReadRegisters(InstructionRegisters("AuthenticAMD").Read);
        var intelReader = InstructionRegisters("GenuineIntel");
        var intel = WindowsCpuThreadCpuidReader.ReadRegisters(intelReader.Read);
        foreach (var name in new[] { "SSE4A", "FMA4", "XOP", "SKINIT_STGI", "MONITORX", "MWAITX",
            "3DNOW", "3DNOWEXT", "MMXEXT", "CLZERO", "INVLPGB", "TLBSYNC", "RDPRU", "MCOMMIT", "SYSCALL_SYSRET" })
        {
            Assert.Contains(name, amd.Identity.HardwareFeatures);
            Assert.DoesNotContain(name, intel.Identity.HardwareFeatures);
        }
        Assert.Contains("SSE4.1", amd.Identity.HardwareFeatures); Assert.Contains("FMA", amd.Identity.HardwareFeatures);
        Assert.DoesNotContain(intelReader.Calls.Keys, key => key.Item1 == 0x80000021);
        Assert.Equal(1, intelReader.Calls[(0x80000008, 0)]);
        Assert.Contains("WBNOINVD", intel.Identity.HardwareFeatures); Assert.Contains("WBNOINVD", amd.Identity.HardwareFeatures);
    }

    [Theory]
    [InlineData("GenuineIntel")] [InlineData("AuthenticAMD")]
    public void VaesAndVectorCarrylessMultiplyRequireTheirDocumentedAvxDeclaration(string vendor)
    {
        var reader = InstructionRegisters(vendor);
        var identity = reader.Values[(1, 0)];
        reader.Values[(1, 0)] = (identity.Item1, identity.Item2, identity.Item3 & ~(1 << 28), identity.Item4);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.DoesNotContain("VAES", data.Identity.HardwareFeatures);
        Assert.DoesNotContain("VPCLMULQDQ", data.Identity.HardwareFeatures);
        Assert.Contains("AES", data.Identity.HardwareFeatures); Assert.Contains("PCLMULQDQ", data.Identity.HardwareFeatures);
    }

    [Theory]
    [InlineData("GenuineIntel")] [InlineData("AuthenticAMD")]
    public void StructuredSubleafZeroNeverReadsOrDecodesSubleafOne(string vendor)
    {
        var reader = InstructionRegisters(vendor);
        reader.Values[(7, 0)] = (0, 0, 0, 0);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.DoesNotContain((7U, 1U), reader.Calls.Keys);
        Assert.DoesNotContain("AVX512BF16", data.Identity.HardwareFeatures);
        Assert.DoesNotContain("SHA512", data.Identity.HardwareFeatures);
        Assert.DoesNotContain("USER_MSR", data.Identity.HardwareFeatures);
        Assert.DoesNotContain("APX_F", data.Identity.HardwareFeatures);
        Assert.Null(data.IntelAvx10Version);
    }

    [Theory]
    [InlineData(15, "USER_MSR", "APX_F")]
    [InlineData(21, "APX_F", "USER_MSR")]
    public void IntelAdditionalInstructionDeclarationsUseIndependentBitsWithoutOsEnablement(
        int bit, string present, string absent)
    {
        var reader = InstructionRegisters("GenuineIntel");
        reader.Values[(1, 0)] = reader.Values[(1, 0)] with { Item3 = 0 };
        reader.Values[(7, 1)] = (0, 0, 0, 1 << bit);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Contains(present, data.Identity.HardwareFeatures);
        Assert.DoesNotContain(absent, data.Identity.HardwareFeatures);
        Assert.Empty(data.Identity.OsUsableFeatures);
        Assert.Equal(1U << bit, data.IdentityRegisters.Single(row => row.Leaf == 7 && row.Subleaf == 1).Edx);
        Assert.All(reader.Calls.Values, count => Assert.Equal(1, count));
        Assert.DoesNotContain(reader.Calls.Keys, key => key.Item1 == 7 && key.Item2 > 1);

        reader.Values[(7, 1)] = (0, 0, 0, 0);
        var cleared = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.DoesNotContain(present, cleared.Identity.HardwareFeatures);
        Assert.DoesNotContain(absent, cleared.Identity.HardwareFeatures);
    }

    [Fact]
    public void UnsupportedBasicAndExtendedRangesDoNotQueryAliasedHigherLeaves()
    {
        var reader = Registers("AuthenticAMD", 1, 0x80000000);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Contains("SSE2", data.Identity.HardwareFeatures);
        Assert.Equal(new[] { (0U, 0U), (1U, 0U), (0x80000000U, 0U) }, reader.Calls.Keys.Order());
        reader = InstructionRegisters("GenuineIntel");
        reader.Values[(0, 0)] = reader.Values[(0, 0)] with { Item1 = unchecked((int)0x80000000) };
        reader.Values[(0x80000000, 0)] = (unchecked((int)0x80000000), 0, 0, 0);
        Assert.Empty(WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read).Identity.HardwareFeatures);
        Assert.DoesNotContain(reader.Calls.Keys, key => key.Item1 > 0 && key.Item1 < 0x80000000);
    }

    [Theory]
    [InlineData(0x80000007U, false, false)]
    [InlineData(0x80000008U, true, false)]
    [InlineData(0x80000020U, true, false)]
    [InlineData(0x80000021U, true, true)]
    public void EachAmdExtendedBankRequiresItsOwnSupportedRange(uint maximum, bool read8, bool read21)
    {
        var reader = InstructionRegisters("AuthenticAMD");
        reader.Values[(0x80000000, 0)] = (unchecked((int)maximum), 0, 0, 0);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Equal(read8, reader.Calls.ContainsKey((0x80000008, 0)));
        Assert.Equal(read21, reader.Calls.ContainsKey((0x80000021, 0)));
        Assert.Equal(read8, data.Identity.HardwareFeatures.Contains("CLZERO"));
        Assert.Equal(read21, data.Identity.HardwareFeatures.Contains("PREFETCHI"));
        Assert.Contains("SSE4A", data.Identity.HardwareFeatures);
    }

    [Fact]
    public void UnknownVendorRetainsRawIdentityWithoutUsingUnverifiedFeatureTables()
    {
        var reader = InstructionRegisters("CentaurHauls");
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Empty(data.Identity.HardwareFeatures);
        Assert.Equal(uint.MaxValue, data.IdentityRegisters.Single(row => row.Leaf == 1).Ecx);
        Assert.DoesNotContain(reader.Calls.Keys, key => key.Item1 is 7 or 0xD or 0x24 or 0x80000001 or 0x80000008 or 0x80000021);
    }

    [Fact]
    public void MwaitOnlyDeclarationDoesNotInventMonitorAndRepeatedDeclarationsAreUnique()
    {
        var reader = InstructionRegisters("GenuineIntel");
        reader.Values[(1, 0)] = reader.Values[(1, 0)] with { Item3 = 0 };
        var features = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read).Identity.HardwareFeatures;
        Assert.Contains("MWAIT", features); Assert.DoesNotContain("MONITOR", features);
        reader.Values[(1, 0)] = reader.Values[(1, 0)] with { Item3 = 1 << 3 };
        features = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read).Identity.HardwareFeatures;
        Assert.Contains("MONITOR", features); Assert.Single(features, feature => feature == "MWAIT");
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(255)]
    public void Avx10ReportsExactLowByteAndKeepsReservedBitsOnlyInRawRegisters(int version)
    {
        var reader = InstructionRegisters("GenuineIntel");
        reader.Values[(0x24, 0)] = (-1, unchecked((int)0xFFFF0000) | version, -1, -1);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Equal((byte)version, data.IntelAvx10Version);
        Assert.Contains("AVX10", data.Identity.HardwareFeatures);
        Assert.DoesNotContain(data.Identity.HardwareFeatures, feature => feature.StartsWith("AVX10_"));
        Assert.Equal(1, reader.Calls[(0x24, 0)]);
        Assert.Equal(unchecked((uint)(0xFFFF0000 | (uint)version)), data.IdentityRegisters.Single(row => row.Leaf == 0x24).Ebx);
        Assert.DoesNotContain(reader.Calls.Keys, key => key.Item1 == 0x24 && key.Item2 != 0);
    }

    [Theory]
    [InlineData("GenuineIntel", 0x23U, 1U, true)]
    [InlineData("GenuineIntel", 0x24U, 0U, true)]
    [InlineData("GenuineIntel", 0x24U, 1U, false)]
    [InlineData("AuthenticAMD", 0x24U, 1U, true)]
    [InlineData("CentaurHauls", 0x24U, 1U, true)]
    public void Avx10VersionRequiresAllDocumentedIntelSupportConditions(string vendor, uint maximum, uint subleaf, bool avx10)
    {
        var reader = InstructionRegisters(vendor);
        reader.Values[(0, 0)] = reader.Values[(0, 0)] with { Item1 = (int)maximum };
        reader.Values[(7, 0)] = reader.Values[(7, 0)] with { Item1 = (int)subleaf };
        if (!avx10) reader.Values[(7, 1)] = reader.Values[(7, 1)] with { Item4 = ~(1 << 19) };
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Null(data.IntelAvx10Version);
        Assert.DoesNotContain((0x24U, 0U), reader.Calls.Keys);
    }

    [Fact]
    public void Avx10TypedProjectionAndInstructionListClearAndRecoverThroughTheOriginalProjection()
    {
        var snapshot = InstructionSnapshot();
        var current = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var intel = Device(current, "windows-cpu-thread|0:1");
        var value = Field(intel, "cpu_intel_avx10_version");
        Assert.Equal("2", value.Value!.Value.GetString());
        Assert.Equal("version8", value.Definition.Unit); Assert.Equal("intel.cpuid.24.0", value.Definition.SourceId);
        Assert.Contains("AVX512VL", Field(intel, "cpu_all_instruction_feature").Value!.Value.EnumerateArray().Select(item => item.GetString()));
        foreach (var name in new[] { "USER_MSR", "APX_F" })
        {
            Assert.Contains(name, Field(intel, "cpu_all_instruction_feature").Value!.Value.EnumerateArray().Select(item => item.GetString()));
            Assert.DoesNotContain(name, Field(Device(current, "windows-cpu-thread|2:7"), "cpu_all_instruction_feature")
                .Value!.Value.EnumerateArray().Select(item => item.GetString()));
        }
        Assert.DoesNotContain(Device(current, "windows-cpu-thread|2:7").Fields, field => field.Definition.Name == "cpu_intel_avx10_version");
        var failed = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
            { ThreadCpuid = [new(1, null), snapshot.OsTopologyInformation.ThreadCpuid![1]] } };
        var cleared = CpuTopologyInformationProjection.Create(failed, current);
        Assert.Null(Field(Device(cleared, "windows-cpu-thread|0:1"), "cpu_intel_avx10_version").Value);
        Assert.Null(Field(Device(cleared, "windows-cpu-thread|0:1"), "cpu_all_instruction_feature").Value);
        Assert.NotNull(Field(Device(cleared, "windows-cpu-thread|2:7"), "cpu_all_instruction_feature").Value);
        Assert.Equal("2", Field(Device(CpuTopologyInformationProjection.Create(snapshot, cleared), "windows-cpu-thread|0:1"),
            "cpu_intel_avx10_version").Value!.Value.GetString());
        var recovered = CpuTopologyInformationProjection.Create(snapshot, cleared);
        foreach (var name in new[] { "USER_MSR", "APX_F" })
            Assert.Contains(name, Field(Device(recovered, "windows-cpu-thread|0:1"), "cpu_all_instruction_feature")
                .Value!.Value.EnumerateArray().Select(item => item.GetString()));
    }

    private static RegisterReader InstructionRegisters(string vendor)
    {
        var reader = XStateRegisters(vendor, 0x24);
        reader.Values[(0x80000000, 0)] = (unchecked((int)0x80000021), 0, 0, 0);
        reader.Values[(1, 0)] = reader.Values[(1, 0)] with { Item3 = -1, Item4 = -1 };
        reader.Values[(7, 0)] = (-1, -1, -1, -1);
        reader.Values[(7, 1)] = (-1, -1, -1, -1);
        reader.Values[(0xD, 1)] = (-1, -1, -1, -1);
        reader.Values[(0x80000001, 0)] = (-1, -1, -1, -1);
        reader.Values[(0x80000008, 0)] = (-1, -1, -1, -1);
        reader.Values[(0x8000000A, 0)] = (1, 128, 0, 0);
        reader.Values[(0x80000021, 0)] = (-1, -1, -1, -1);
        reader.Values[(0x24, 0)] = (0, 0x70002, -1, -1);
        return reader;
    }

    internal static CpuTopologySnapshot InstructionSnapshot()
    {
        var snapshot = XStateSnapshot();
        return snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
        {
            ThreadCpuid = [new(1, WindowsCpuThreadCpuidReader.ReadRegisters(InstructionRegisters("GenuineIntel").Read)),
                new(135, WindowsCpuThreadCpuidReader.ReadRegisters(InstructionRegisters("AuthenticAMD").Read))]
        } };
    }
}
