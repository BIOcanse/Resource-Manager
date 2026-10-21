using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class CpuThreadCpuidInformationTests
{
    [Fact]
    public void IntelCoreTypeDoesNotRequireHybridBitAndTopologyPrefersValidLeaf1f()
    {
        var read = Registers("GenuineIntel", coreType: 0x77);
        read.Values[(7, 0)] = (0, 1 << 5, 0, 0);
        var result = WindowsCpuThreadCpuidReader.ReadRegisters(read.Read);
        Assert.Equal((byte)0x77, result.IntelHardwareCoreType);
        Assert.Equal(0x12345U, result.IntelNativeModelId);
        Assert.Equal(uint.MaxValue, result.IntelX2ApicId);
        Assert.Null(result.AmdExtendedApicId);
        Assert.Equal((byte)0xFE, result.InitialApicId);
        Assert.DoesNotContain((0x0BU, 0U), read.Calls.Keys);
        Assert.All(read.Calls.Values, count => Assert.Equal(1, count));
        Assert.Equal(uint.MaxValue, result.IdentityRegisters.Single(row => row.Leaf == 0x1F).Edx);
        Assert.Empty(result.Identity.OsUsableFeatures);
        Assert.Contains("AVX2", result.Identity.HardwareFeatures);
    }

    [Theory]
    [InlineData(0, 0x100)]
    [InlineData(2, 0)]
    public void InvalidIntelTopologyFallsBackOnlyToValidLegacyTopology(int processors, int type)
    {
        var read = Registers("GenuineIntel");
        read.Values[(0x1F, 0)] = (1, processors, type, -1);
        read.Values[(0x1A, 0)] = (0, 0, 0, 0);
        var result = WindowsCpuThreadCpuidReader.ReadRegisters(read.Read);
        Assert.Equal(0x1001U, result.IntelX2ApicId);
        Assert.Null(result.IntelHardwareCoreType); Assert.Null(result.IntelNativeModelId);
        read.Values[(0x0B, 0)] = (0, 0, 0, -1);
        Assert.Null(WindowsCpuThreadCpuidReader.ReadRegisters(read.Read).IntelX2ApicId);
    }

    [Theory]
    [InlineData(7U, 0x80000004U, true, false)]
    [InlineData(7U, 0x8000001EU, false, false)]
    [InlineData(7U, 0x8000001EU, true, true)]
    public void AmdExtendedApicRequiresBothLeafRangeAndTopologyExtension(uint maximum, uint extended, bool flag, bool available)
    {
        var read = Registers("AuthenticAMD", maximum, extended);
        read.Values[(0x80000001, 0)] = (0, 0, flag ? 1 << 22 : 0, 0);
        var result = WindowsCpuThreadCpuidReader.ReadRegisters(read.Read);
        Assert.Equal(available ? 0x81234567U : (uint?)null, result.AmdExtendedApicId);
        Assert.Equal(available, read.Calls.ContainsKey((0x8000001E, 0)));
        Assert.Null(result.IntelHardwareCoreType); Assert.Null(result.IntelX2ApicId);
        Assert.DoesNotContain(read.Calls.Keys, key => key.Item1 is 0x1A or 0x1F or 0x0B);
        Assert.Equal(25, result.Identity.Family); Assert.Equal(33, result.Identity.Model);
    }

    [Fact]
    public void UnsupportedIdentityLeafAndUnknownVendorDoNotAcquireVendorSpecificValues()
    {
        var read = Registers("CentaurHauls", 0, 0x80000000);
        var result = WindowsCpuThreadCpuidReader.ReadRegisters(read.Read);
        Assert.Null(result.Signature); Assert.Null(result.InitialApicId); Assert.Null(result.Identity.Brand);
        Assert.Null(result.IntelX2ApicId); Assert.Null(result.AmdExtendedApicId);
        Assert.Equal(2, read.Calls.Count);
        Assert.Empty(result.Identity.HardwareFeatures);
        var unknown = Registers("CentaurHauls");
        WindowsCpuThreadCpuidReader.ReadRegisters(unknown.Read);
        Assert.DoesNotContain(unknown.Calls.Keys, key => key.Item1 is 0x1A or 0x1F or 0x0B or 0x8000001E);
    }

    [Fact]
    public void WindowsFeatureMappingUsesOsFlagsInsteadOfRuntimeIntrinsicSwitches()
    {
        var calls = new List<uint>();
        var result = WindowsCpuOsFeatureReader.Read(flag => { calls.Add(flag); return flag is 39 or 40; });
        Assert.Equal(new[] { "AVX", "AVX2" }, result);
        Assert.Equal(new uint[] { 6, 10, 13, 36, 37, 38, 39, 40, 41 }, calls);
        Assert.Empty(WindowsCpuOsFeatureReader.Read(static _ => false));
    }

    private static RegisterReader Registers(string vendor, uint maximum = 0x1F, uint extendedMaximum = 0x8000001E, byte coreType = 0x40)
    {
        var vendorWords = Words(vendor);
        var brand = Words("Fixture processor".PadRight(48));
        return new(new()
        {
            [(0, 0)] = ((int)maximum, vendorWords[0], vendorWords[2], vendorWords[1]),
            [(1, 0)] = (vendor == "AuthenticAMD" ? 0x00A20F12 : 0x000906EA, unchecked((int)0xFE000000), 1 << 28, 1 << 26),
            [(6, 0)] = (0, 0, 0, 0),
            [(7, 0)] = (0, 1 << 5, 0, 1 << 15),
            [(0x0B, 0)] = (1, 2, 0x100, 0x1001),
            [(0x1F, 0)] = (1, 2, 0x100, -1),
            [(0x1A, 0)] = (unchecked((int)((uint)coreType << 24 | 0x12345)), 0, 0, 0),
            [(0x15, 0)] = (0, 0, 0, 0),
            [(0x16, 0)] = (0, 0, 0, 0),
            [(0x80000000, 0)] = (unchecked((int)extendedMaximum), 0, 0, 0),
            [(0x80000001, 0)] = (0, 0, 1 << 22, 0),
            [(0x80000002, 0)] = (brand[0], brand[1], brand[2], brand[3]),
            [(0x80000003, 0)] = (brand[4], brand[5], brand[6], brand[7]),
            [(0x80000004, 0)] = (brand[8], brand[9], brand[10], brand[11]),
            [(0x80000008, 0)] = (0, 0, 0, 0),
            [(0x8000001E, 0)] = (unchecked((int)0x81234567), 0, 0, 0)
        });
    }
    private static int[] Words(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        return Enumerable.Range(0, bytes.Length / 4).Select(index => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index * 4, 4))).ToArray();
    }
    private sealed class RegisterReader(Dictionary<(uint, uint), (int, int, int, int)> values)
    {
        internal Dictionary<(uint, uint), (int, int, int, int)> Values { get; } = values;
        internal Dictionary<(uint, uint), int> Calls { get; } = new();
        internal (int, int, int, int) Read(int leaf, int subleaf)
        {
            var key = (unchecked((uint)leaf), unchecked((uint)subleaf));
            Calls[key] = Calls.GetValueOrDefault(key) + 1;
            if (key.Item1 < 0x80000000 && key.Item1 > (uint)Values[(0, 0)].Item1
                || key.Item1 >= 0x80000001 && key.Item1 > unchecked((uint)Values[(0x80000000, 0)].Item1))
                throw new InvalidOperationException("Unsupported leaf was queried.");
            return Values[key];
        }
    }
}
