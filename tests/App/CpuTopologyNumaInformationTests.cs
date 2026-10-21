using System.Buffers.Binary;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed class CpuTopologyNumaInformationTests
{
    [Fact]
    public void FullNodeRelationshipIncludesAllGroupsAndUnsignedNodeNumber()
    {
        var data = Node(uint.MaxValue, 2, [(3, 4ul), (0x8001, 1ul << 63)]);
        var node = Assert.Single(WindowsCpuOsTopologyReader.ParseNumaInformation(data));
        Assert.Equal(uint.MaxValue, node.NodeNumber);
        Assert.Equal([194, 0x8001 * 64 + 63], node.LogicalProcessorIds);
        var fields = CpuTopologyInformationProjection.Create(WithNodes([node]), DeviceInformationSnapshot.Empty);
        var device = Assert.Single(fields.Devices, device => device.ObjectType == "numa_node");
        Assert.Equal("windows-numa-node|4294967295", device.DeviceIdentity);
        var number = CpuTopologyInformationFieldTests.Field(device, "cpu_all_numa_node_id");
        Assert.Equal("4294967295", number.Value!.Value.GetString());
        Assert.Equal("node32", number.Definition.Unit);
        Assert.Equal("windows.numa-topology", number.Definition.SourceId);
        var affinity = CpuTopologyInformationFieldTests.Field(device, "cpu_all_numa_node_cpu_affinity");
        Assert.Equal("processor-addresses", affinity.Definition.ValueKind);
        Assert.Equal("group-index[]", affinity.Definition.Unit);
        Assert.Equal(32769, affinity.Value!.Value[1].GetProperty("processorGroup").GetInt32());
        Assert.Equal(63, affinity.Value!.Value[1].GetProperty("groupRelativeIndex").GetInt32());
    }

    [Fact]
    public void LegacyReservedZeroGroupCountRetainsTheSingleActualGroup()
    {
        var node = Assert.Single(WindowsCpuOsTopologyReader.ParseNumaInformation(Node(7, 0, [(2, 128ul)])));
        Assert.Equal(7u, node.NodeNumber);
        Assert.Equal([135], node.LogicalProcessorIds);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void MemoryOnlyNodeIsPresentWithAnExplicitEmptyAffinity(int count, bool hasMask)
    {
        var data = Node(19, (ushort)count, hasMask ? [(0, 0ul)] : []);
        var node = Assert.Single(WindowsCpuOsTopologyReader.ParseNumaInformation(data));
        Assert.Empty(node.LogicalProcessorIds);
        var fields = CpuTopologyInformationProjection.Create(WithNodes([node]), DeviceInformationSnapshot.Empty);
        var device = Assert.Single(fields.Devices, device => device.DeviceIdentity == "windows-numa-node|19");
        Assert.Equal("19", CpuTopologyInformationFieldTests.Field(device, "cpu_all_numa_node_id").Value!.Value.GetString());
        Assert.Empty(CpuTopologyInformationFieldTests.Field(device, "cpu_all_numa_node_cpu_affinity").Value!.Value.EnumerateArray());
    }

    [Fact]
    public void NodeIdentityIsTheNodeNumberAndDoesNotDependOnMembershipOrder()
    {
        var first = Assert.Single(WindowsCpuOsTopologyReader.ParseNumaInformation(Node(7, 2, [(2, 4ul), (0, 1ul)])));
        var second = Assert.Single(WindowsCpuOsTopologyReader.ParseNumaInformation(Node(7, 2, [(0, 1ul), (2, 4ul)])));
        Assert.Equal(first.LogicalProcessorIds, second.LogicalProcessorIds);
        var fields = CpuTopologyInformationProjection.Create(WithNodes([second]), DeviceInformationSnapshot.Empty);
        Assert.Single(fields.Devices, device => device.DeviceIdentity == "windows-numa-node|7");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(31)]
    [InlineData(47)]
    public void TruncatedNodeNeverReadsNextRecordBytes(int size)
    {
        var data = Node(7, 1, [(0, 1ul)]); U32(data, 4, (uint)size);
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseNumaInformation(data));
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(2, 48)]
    [InlineData(65535, 48)]
    public void NodeMaskCountMustFitItsRecord(int count, int size)
    {
        var data = new byte[size]; U32(data, 0, 1); U32(data, 4, (uint)size); U16(data, 30, (ushort)count);
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseNumaInformation(data));
    }

    [Fact]
    public void DuplicateNodesGroupsAndCrossNodeThreadOwnershipRejectTheWholeGroup()
    {
        var first = Node(7, 1, [(0, 1ul)]);
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseNumaInformation([.. first, .. first]));
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseNumaInformation(
            Node(7, 2, [(0, 1ul), (0, 2ul)])));
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseNumaInformation(
            [.. first, .. Node(8, 1, [(0, 1ul)])]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    public void BadTailDoesNotCertifyAValidNumaPrefix(int size)
        => Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseNumaInformation(
            [.. Node(7, 1, [(0, 1ul)]), .. new byte[size]]));

    [Fact]
    public void ExtendedRequestTagIsNotMistakenForTheReturnedNumaRecordTag()
    {
        var data = Node(7, 1, [(0, 1ul)]); U32(data, 0, 6);
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.ParseNumaInformation(data));
    }

    [Fact]
    public void NodeFailureClearsOnlyNumaAndEmptySuccessRemovesOldNodes()
    {
        var current = CpuTopologyInformationProjection.Create(WithNodes([new(7, [135])]), DeviceInformationSnapshot.Empty);
        var failed = CpuTopologyInformationProjection.Create(CpuTopologyInformationFieldTests.Snapshot(), current);
        var emptyNode = Assert.Single(failed.Devices, device => device.ObjectType == "numa_node");
        Assert.All(emptyNode.Fields, field => Assert.Null(field.Value));
        Assert.All(failed.Devices.Where(static device => device.ObjectType != "numa_node").SelectMany(static device => device.Fields),
            field => Assert.NotNull(field.Value));
        var empty = CpuTopologyInformationProjection.Create(WithNodes([]), current);
        Assert.DoesNotContain(empty.Devices, device => device.ObjectType == "numa_node");
        Assert.Equal(7, empty.Devices.Count);
        Assert.Empty(WindowsCpuOsTopologyReader.ParseNumaInformation([]));
    }

    [Fact]
    public void OtherTopologyFailuresDoNotClearCurrentNodeRelations()
    {
        var snapshot = WithNodes([new(7, [135])]);
        snapshot = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with { PhysicalCores = null, Caches = null } };
        var fields = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var device = Assert.Single(fields.Devices);
        Assert.Equal("numa_node", device.ObjectType); Assert.All(device.Fields, field => Assert.NotNull(field.Value));
        Assert.DoesNotContain(device.Fields, field => field.Definition.Name == "memory_all_numa_node_capacity");
    }

    [Fact]
    public async Task ExistingCpuFieldSubscriptionPublishesNumaAndClearsItOnSamplingFailure()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        using var cancellation = new CancellationTokenSource();
        await using var subscription = fixture.Reader.SubscribeInformationAsync("numa", TimeSpan.FromMilliseconds(100),
            cancellation.Token).GetAsyncEnumerator();
        var next = subscription.MoveNextAsync().AsTask(); await fixture.Reader.StartAsync(CancellationToken.None);
        _ = await fixture.Sampler.WaitForCaptureAsync(); fixture.Sampler.Complete(WithNodes([new(7, [135])]));
        Assert.True(await next.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Equal(1u, fixture.ReadDemand().ItemCount);
        var node = Assert.Single(subscription.Current.Devices, device => device.ObjectType == "numa_node");
        Assert.All(node.Fields, field => Assert.NotNull(field.Value));
        Assert.Same(subscription.Current, fixture.Reader.GetInformationSnapshot());
        next = subscription.MoveNextAsync().AsTask(); _ = await fixture.Sampler.WaitForCaptureAsync();
        fixture.Sampler.Complete(new InvalidOperationException("Numa capture failed"));
        Assert.True(await next.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        node = Assert.Single(subscription.Current.Devices, device => device.ObjectType == "numa_node");
        Assert.All(node.Fields, field => Assert.Null(field.Value)); Assert.Null(subscription.Current.ObservedAt);
    }

    private static CpuTopologySnapshot WithNodes(IReadOnlyList<CpuOsNumaNode> nodes)
    {
        var snapshot = CpuTopologyInformationFieldTests.Snapshot();
        return snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with { NumaNodes = nodes } };
    }

    private static byte[] Node(uint number, ushort count, (ushort Group, ulong Mask)[] masks)
    {
        var data = new byte[32 + masks.Length * 16]; U32(data, 0, 1); U32(data, 4, (uint)data.Length);
        U32(data, 8, number); U16(data, 30, count);
        for (var index = 0; index < masks.Length; index++)
        { BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(32 + index * 16), masks[index].Mask); U16(data, 40 + index * 16, masks[index].Group); }
        return data;
    }
    private static void U16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
}
