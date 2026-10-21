using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Windows;

namespace Resource_Manager_APP.Tests;

public sealed class InformationCollectorTests
{
    [Theory]
    [InlineData("GenuineIntel", 0x000906EA, 6, 158, 10)]
    [InlineData("AuthenticAMD", 0x00A20F12, 25, 33, 2)]
    public void CpuidIdentitySeparatesHardwareInstructionsFromOsUsability(string vendor, int signature, int family, int model, int stepping)
    {
        var vendorWords = Words(vendor);
        var brand = Words("Fixture CPU".PadRight(48));
        var result = CpuIdentityInformationReader.ReadRegisters((leaf, _) => leaf switch
        {
            0 => (7, vendorWords[0], vendorWords[2], vendorWords[1]),
            1 => (signature, 0, 1 << 28, 1 << 26),
            7 => (0, 1 << 5, 0, 0),
            unchecked((int)0x80000000) => (unchecked((int)0x80000004), 0, 0, 0),
            unchecked((int)0x80000001) => (0, 0, 0, 0),
            unchecked((int)0x80000002) => (brand[0], brand[1], brand[2], brand[3]),
            unchecked((int)0x80000003) => (brand[4], brand[5], brand[6], brand[7]),
            unchecked((int)0x80000004) => (brand[8], brand[9], brand[10], brand[11]),
            _ => throw new InvalidOperationException("Unsupported leaf must not be queried.")
        }, ["SSE2"]);
        Assert.Equal(vendor, result.Vendor);
        Assert.Equal(family, result.Family);
        Assert.Equal(model, result.Model);
        Assert.Equal(stepping, result.Stepping);
        Assert.Equal("Fixture CPU", result.Brand);
        Assert.Contains("AVX2", result.HardwareFeatures);
        Assert.DoesNotContain("AVX2", result.OsUsableFeatures);
    }

    [Fact]
    public void SmbiosModulesPreserveHandlesUnitsAndUnknownDeclarations()
    {
        var first = Module(0x1042, 0x7FFF);
        U32(first, 0x1C, 32768);
        U16(first, 0x15, 0xFFFF); U32(first, 0x54, 8000);
        U16(first, 0x20, 6400); U16(first, 0x26, 1100);
        var second = Module(0x7042, 0xFFFF);
        U16(second, 0x15, 0); U16(second, 0x20, 0xFFFF); U32(second, 0x58, 0x80000001);
        U16(second, 0x08, 0xFFFF);
        var modules = SmbiosMemoryReader.ParseModules(RawTable(first, second));
        Assert.Equal(2, modules.Count);
        Assert.Equal("smbios-memory|1042", modules[0].DeviceIdentity);
        Assert.Equal(32UL * 1024 * 1024 * 1024, modules[0].CapacityBytes);
        Assert.Equal("DIMM A", modules[0].Locator);
        Assert.Equal("FixtureSerial", modules[0].SerialNumber);
        Assert.Equal(8000U, modules[0].DeclaredDataRateMtps);
        Assert.Equal(6400U, modules[0].ConfiguredDataRateMtps);
        Assert.Equal(1.1, modules[0].ConfiguredVoltageVolts);
        Assert.Equal("DDR5", modules[0].MemoryType);
        Assert.Equal("smbios-memory|7042", modules[1].DeviceIdentity);
        Assert.Null(modules[1].CapacityBytes);
        Assert.Null(modules[1].DeclaredDataRateMtps);
        Assert.Null(modules[1].ConfiguredDataRateMtps);
        Assert.Null(modules[1].TotalWidthBits);
    }

    [Fact]
    public void SmbiosSmallCapacityAndTruncatedStructuresDoNotBecomeInventedModules()
    {
        Assert.Empty(SmbiosMemoryReader.ParseModules([]));
        Assert.Empty(SmbiosMemoryReader.ParseModules(RawTable(Module(1, 0))));
        var small = Assert.Single(SmbiosMemoryReader.ParseModules(RawTable(Module(3, 0x8400))));
        Assert.Equal(1024UL * 1024, small.CapacityBytes);
        var truncated = RawTable(Module(4, 1024))[..30];
        Assert.Empty(SmbiosMemoryReader.ParseModules(truncated));
    }

    [Theory]
    [InlineData(186U, 14850d, 14.85d)]
    [InlineData(185U, 9000d, 9d)]
    [InlineData(82U, 63d, 63d)]
    public void NvmlFieldUsesOfficialAbiUnitsAndProviderTimestamp(uint fieldId, double raw, double expected)
    {
        Assert.Equal(40, Marshal.SizeOf<NvmlFieldValue>());
        Assert.Equal(32, Marshal.OffsetOf<NvmlFieldValue>(nameof(NvmlFieldValue.Value)).ToInt32());
        Assert.Equal(40, Marshal.SizeOf<NvmlMemoryV2>());
        Assert.Equal(16, Marshal.OffsetOf<NvmlMemoryV2>(nameof(NvmlMemoryV2.Reserved)).ToInt32());
        const long us = 1700000000123456;
        var field = new NvmlFieldValue { FieldId = fieldId, TimestampUs = us, ValueType = 0, Value = new() { Double = raw } };
        Assert.Equal(expected, NvidiaNvmlReader.DecodeField(field));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(us * 10), NvidiaNvmlReader.DecodeFieldTimestamp(us));
        field.ReturnCode = 3;
        Assert.Null(NvidiaNvmlReader.DecodeField(field));
        field.ReturnCode = 0; field.TimestampUs = long.MaxValue;
        Assert.Null(NvidiaNvmlReader.DecodeField(field));
        field.TimestampUs = us; field.Value.Double = double.NaN;
        Assert.Null(NvidiaNvmlReader.DecodeField(field));
        field.Value.Double = raw; field.FieldId = uint.MaxValue;
        Assert.Null(NvidiaNvmlReader.DecodeField(field));
    }

    [Fact]
    public void WindowsPageCountersPublishBytesAndIndependentObjectCounts()
    {
        var value = WindowsVirtualMemoryMonitoringZone.CreateCommitMetrics(new PerformanceInformation
        {
            CommitTotal=2, CommitLimit=100, PageSize=4096, CommitPeak=5,
            SystemCache=7, KernelPaged=3, KernelNonpaged=4, ProcessCount=10, ThreadCount=20, HandleCount=30
        }, "fixture");
        Assert.Equal(5UL * 4096, value.CommitPeakBytes);
        Assert.Equal(7UL * 4096, value.SystemCacheBytes);
        Assert.Equal(3UL * 4096, value.KernelPagedBytes);
        Assert.Equal(4UL * 4096, value.KernelNonpagedBytes);
        Assert.Equal(10U, value.ProcessCount); Assert.Equal(20U, value.ThreadCount); Assert.Equal(30U, value.HandleCount);
        var invalid = WindowsVirtualMemoryMonitoringZone.CreateCommitMetrics(new PerformanceInformation
            { CommitLimit=1, PageSize=4096, CommitPeak=nuint.MaxValue }, "fixture");
        Assert.Null(invalid.CommitPeakBytes);
    }

    [Fact]
    public void CacheTopologyPreservesMasksFromAllProcessorGroups()
    {
        var bytes = new byte[72];
        bytes[8]=3; bytes[9]=16; U16(bytes,10,64); U32(bytes,12,32*1024*1024); U32(bytes,16,0);
        U16(bytes,38,2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56), 2); U16(bytes,64,2);
        U32(bytes,0,2); U32(bytes,4,(uint)bytes.Length);
        var cache = Assert.Single(WindowsCpuOsTopologyReader.ParseCacheInformation(bytes));
        Assert.Equal([0,1,129], cache.LogicalProcessorIds);
        U32(bytes,4,56);
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseCacheInformation(bytes[..56]));
    }

    private static int[] Words(string value) => Encoding.ASCII.GetBytes(value).Chunk(4).Select(static bytes => BitConverter.ToInt32(bytes)).ToArray();
    private static void U16(byte[] bytes,int offset,ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset),value);
    private static void U32(byte[] bytes,int offset,uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset),value);
    private static byte[] Module(ushort handle,ushort size)
    {
        var bytes = new byte[0x5C]; bytes[0]=17; bytes[1]=(byte)bytes.Length;
        U16(bytes,2,handle); U16(bytes,0x0C,size); U16(bytes,0x08,64); U16(bytes,0x0A,64);
        bytes[0x10]=1; bytes[0x17]=2; bytes[0x18]=3; bytes[0x1A]=4; bytes[0x12]=0x22;
        return bytes.Concat(Encoding.ASCII.GetBytes("DIMM A\0FixtureVendor\0FixtureSerial\0FixturePart\0\0")).ToArray();
    }
    private static byte[] RawTable(params byte[][] modules)
    {
        var table = modules.SelectMany(static module => module).ToArray();
        var data = new byte[8+table.Length]; U32(data,4,(uint)table.Length); table.CopyTo(data,8); return data;
    }
}
