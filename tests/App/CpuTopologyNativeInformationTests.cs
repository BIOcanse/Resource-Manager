using System.Buffers.Binary;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed class CpuTopologyNativeInformationTests
{
    [Fact]
    public void CoreReadsUnsignedGroupAddressesEfficiencyAndRawSmtFlag()
    {
        var cores = WindowsCpuOsTopologyReader.ParsePhysicalCoreInformation(Core(0x8001, (1ul << 63) | 4, 9, true));
        var core = Assert.Single(cores);
        Assert.Equal("core:0", core.PhysicalCoreId);
        Assert.Equal(9, core.EfficiencyClass);
        Assert.True(core.SmtRelationshipReported);
        Assert.Equal([0x8001 * 64 + 2, 0x8001 * 64 + 63], core.LogicalProcessorIds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(65535)]
    public void CoreRequiresTheDeclaredSingleCompleteGroup(int groupCount)
    {
        var data = Core(0, 1);
        U16(data, 30, (ushort)groupCount);
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParsePhysicalCoreInformation(data));
    }

    [Fact]
    public void ExistingDieGroupingCanSpanProcessorGroups()
    {
        var data = new byte[64]; U32(data, 0, 5); U32(data, 4, 64); U16(data, 30, 2);
        U64(data, 32, 1); U16(data, 40, 0); U64(data, 48, 4); U16(data, 56, 1);
        var group = Assert.Single(WindowsCpuOsTopologyReader.ParseProcessorGroupingInformation(data, 5));
        Assert.Equal([0, 66], group);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(47)]
    public void TruncatedCoreDoesNotReadTheFollowingRecord(int size)
    {
        var data = Core(0, 1);
        U32(data, 4, (uint)size);
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParsePhysicalCoreInformation(data));
    }

    [Fact]
    public void CoreRejectsOverlappingThreadOwnershipAndWrongRelationship()
    {
        var core = Core(0, 1);
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParsePhysicalCoreInformation([.. core, .. core]));
        U32(core, 0, 2);
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParsePhysicalCoreInformation(core));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    public void AllWalkersRejectIncompleteTailsInsteadOfReturningAValidPrefix(int tailLength)
    {
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParsePhysicalCoreInformation(
            [.. Core(0, 1), .. new byte[tailLength]]));
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseCacheInformation(
            [.. Cache(0, [(0, 1ul)]), .. new byte[tailLength]]));
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseCpuSetInformation(
            [.. CpuSet(), .. new byte[tailLength]]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CachePreservesUnsignedCapacityAndFullyAssociativeCode(int count)
    {
        var data = Cache((ushort)count, [(0, 3ul)]);
        U32(data, 12, uint.MaxValue);
        data[9] = 255;
        var cache = Assert.Single(WindowsCpuOsTopologyReader.ParseCacheInformation(data));
        Assert.Equal(uint.MaxValue, cache.CapacityBytes);
        Assert.Equal(255, cache.Associativity);
        Assert.Equal(64, cache.LineSizeBytes);
        Assert.Equal([0, 1], cache.LogicalProcessorIds);
    }

    [Fact]
    public void CacheIdentityUsesSortedMembershipAcrossGroups()
    {
        var data = Cache(2, [(2, 128ul), (0, 1ul << 63)]);
        var cache = Assert.Single(WindowsCpuOsTopologyReader.ParseCacheInformation(data));
        Assert.Equal([63, 135], cache.LogicalProcessorIds);
        Assert.Equal("windows-cpu-cache|3|0|63,135", cache.DeviceIdentity);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(0, 72)]
    [InlineData(2, 56)]
    [InlineData(65535, 56)]
    public void CacheRequiresCompleteDeclaredMasksOrTheExactOldLayout(int count, int size)
    {
        var data = new byte[size];
        U32(data, 0, 2); U32(data, 4, (uint)size); U16(data, 38, (ushort)count);
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseCacheInformation(data));
    }

    [Fact]
    public void EmptyAndRepeatedGroupMasksAndCacheIdentitiesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParsePhysicalCoreInformation(Core(0, 0)));
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseCacheInformation(Cache(2, [(0, 1ul), (0, 2ul)])));
        var cache = Cache(1, [(0, 1ul)]);
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseCacheInformation([.. cache, .. cache]));
    }

    [Fact]
    public void CpuSetSchedulingClassIsOneByteAndNumaIndexRemainsGroupRelative()
    {
        var data = CpuSet();
        U16(data, 12, ushort.MaxValue);
        data[17] = 245; data[20] = 17;
        data[21] = data[22] = data[23] = 255;
        var cpuSet = Assert.Single(WindowsCpuOsTopologyReader.ParseCpuSetInformation(data));
        Assert.Equal(ushort.MaxValue, cpuSet.Group);
        Assert.Equal(17, cpuSet.SchedulingClass);
        Assert.Equal(245, cpuSet.NumaNodeIndex);
        Assert.Equal(ushort.MaxValue * 64 + 5, cpuSet.GlobalLogicalProcessorId);
    }

    [Fact]
    public void CpuSetSkipsUnknownTypesBySizeAndRejectsTruncatedKnownTypes()
    {
        var unknown = new byte[8]; U32(unknown, 0, 8); U32(unknown, 4, 31);
        Assert.Single(WindowsCpuOsTopologyReader.ParseCpuSetInformation([.. unknown, .. CpuSet()]));
        var truncated = CpuSet(); U32(truncated, 0, 24);
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseCpuSetInformation(truncated));
        var invalidIndex = CpuSet(); invalidIndex[14] = 64;
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseCpuSetInformation(invalidIndex));
    }

    [Theory]
    [InlineData(48, 49)]
    [InlineData(48, -1)]
    [InlineData(-1, 0)]
    [InlineData(4194305, 0)]
    public void NativeLengthMustFitTheActualBoundedAllocation(int allocated, int returned)
        => Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ValidateNativeBufferLength(allocated, returned));

    [Fact]
    public void SuccessfulEmptyBuffersRemainEmpty()
    {
        Assert.Empty(WindowsCpuOsTopologyReader.ParsePhysicalCoreInformation([]));
        Assert.Empty(WindowsCpuOsTopologyReader.ParseCacheInformation([]));
        Assert.Empty(WindowsCpuOsTopologyReader.ParseCpuSetInformation([]));
    }

    private static byte[] Core(ushort group, ulong mask, byte efficiency = 0, bool smt = false)
    {
        var data = new byte[48]; U32(data, 4, 48);
        data[8] = smt ? (byte)1 : (byte)0; data[9] = efficiency; U16(data, 30, 1);
        U64(data, 32, mask); U16(data, 40, group); return data;
    }

    private static byte[] Cache(ushort count, (ushort Group, ulong Mask)[] masks)
    {
        var data = new byte[40 + masks.Length * 16]; U32(data, 0, 2); U32(data, 4, (uint)data.Length);
        data[8] = 3; data[9] = 16; U16(data, 10, 64); U32(data, 12, 4 * 1024 * 1024); U16(data, 38, count);
        for (var index = 0; index < masks.Length; index++)
        { U64(data, 40 + index * 16, masks[index].Mask); U16(data, 48 + index * 16, masks[index].Group); }
        return data;
    }

    private static byte[] CpuSet()
    {
        var data = new byte[32]; U32(data, 0, 32); U32(data, 8, 17); data[14] = 5; return data;
    }
    private static void U16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void U64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
}
