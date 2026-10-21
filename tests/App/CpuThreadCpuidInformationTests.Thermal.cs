using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class CpuThreadCpuidInformationTests
{
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, true, false)]
    [InlineData(64, false, true)]
    [InlineData(65, true, true)]
    public void IntelThermalCapabilitiesUseDistinctLeafSixBitsAndKeepRawRegisters(int eax, bool core, bool package)
    {
        var registers = Registers("GenuineIntel"); registers.Values[(6, 0)] = (eax, 12, 34, 56);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(registers.Read);
        Assert.Equal(core, data.IntelDtsSupported); Assert.Equal(package, data.IntelPackageThermalSupported);
        var raw = Assert.Single(data.IdentityRegisters, register => register.Leaf == 6);
        Assert.Equal((uint)eax, raw.Eax); Assert.Equal(12U, raw.Ebx); Assert.Equal(34U, raw.Ecx); Assert.Equal(56U, raw.Edx);
        Assert.Equal(1, registers.Calls[(6, 0)]);
    }

    [Theory]
    [InlineData("AuthenticAMD", 6)]
    [InlineData("GenuineIntel", 5)]
    [InlineData("GenuineIntel", 0)]
    public void UnsupportedVendorOrLeafDoesNotReadIntelThermalRegisters(string vendor, uint maximum)
    {
        var registers = Registers(vendor, maximum, 0x80000000);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(registers.Read);
        Assert.Null(data.IntelDtsSupported); Assert.Null(data.IntelPackageThermalSupported);
        Assert.Null(data.IntelPerformanceFeedbackSupported);
        Assert.DoesNotContain((6U, 0U), registers.Calls.Keys);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(-1, true)]
    public void PerformanceFeedbackUsesEcxBitZeroWithoutThermalCapability(int ecx, bool supported)
    {
        var registers = Registers("GenuineIntel"); registers.Values[(6, 0)] = (0, 0, ecx, 0);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(registers.Read);
        Assert.Equal(supported, data.IntelPerformanceFeedbackSupported); Assert.False(data.IntelDtsSupported);
        Assert.Equal(unchecked((uint)ecx), Assert.Single(data.IdentityRegisters, row => row.Leaf == 6).Ecx);
        Assert.Equal(1, registers.Calls[(6, 0)]);
    }
}
