using System.Buffers.Binary;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class GpuDeviceObservationTests
{
    [Fact]
    public void EmptyNativeStoreIsAValueNotAReadFailure()
    {
        var result = WindowsGpuPlacementInjector.DecodeDeviceObservations(Buffer());
        var empty = new GpuDeviceObservation(0, 0, GpuDeviceAdapterIdentityKind.NoDevice);
        Assert.True(result.Success);
        Assert.Equal("read", result.Status);
        Assert.Equal(new GpuDeviceObservationSnapshot(empty, empty, empty, empty, empty), result.Snapshot);
    }

    [Fact]
    public void SlotsPreserveUnsignedIdentityAndUnknownOrMultipleDevices()
    {
        var bytes = Buffer();
        WriteSlot(bytes, 0, ulong.MaxValue, 0x8123456789abcdefUL, 1);
        WriteSlot(bytes, 1, 4, 0, 2);
        WriteSlot(bytes, 2, 7, 0, 3);
        WriteSlot(bytes, 3, 5, 0x123456789abcdef0UL, 1);
        WriteSlot(bytes, 4, 9, 0xfedcba9876543210UL, 1);
        var result = WindowsGpuPlacementInjector.DecodeDeviceObservations(bytes);
        Assert.True(result.Success);
        Assert.Equal(new GpuDeviceObservationSnapshot(
            new(ulong.MaxValue, 0x8123456789abcdefUL, GpuDeviceAdapterIdentityKind.Adapter),
            new(4, 0, GpuDeviceAdapterIdentityKind.Unavailable),
            new(7, 0, GpuDeviceAdapterIdentityKind.MultipleAdapters),
            new(5, 0x123456789abcdef0UL, GpuDeviceAdapterIdentityKind.Adapter),
            new(9, 0xfedcba9876543210UL, GpuDeviceAdapterIdentityKind.Adapter)), result.Snapshot);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(80)]
    [InlineData(103)]
    [InlineData(104)]
    [InlineData(105)]
    [InlineData(127)]
    [InlineData(129)]
    public void WrongLengthDoesNotPartiallyRead(int length)
        => AssertRejected(new byte[length], "observation-layout-invalid");

    [Theory]
    [InlineData(0, 0U)]
    [InlineData(0, 80U)]
    [InlineData(0, 103U)]
    [InlineData(0, 104U)]
    [InlineData(0, 105U)]
    [InlineData(0, 127U)]
    [InlineData(0, 129U)]
    [InlineData(4, 0U)]
    [InlineData(4, 1U)]
    [InlineData(4, 2U)]
    [InlineData(4, 4U)]
    public void WrongHeaderIsNotAccepted(int offset, uint value)
    {
        var bytes = Buffer();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        AssertRejected(bytes, "observation-layout-invalid");
    }

    public static IEnumerable<object[]> InvalidSlots()
    {
        for (var index = 0; index < 5; index++)
        {
            yield return [index, 0UL, 0UL, 1U, 0U];
            yield return [index, 1UL, 0UL, 0U, 0U];
            yield return [index, 1UL, 1UL, 2U, 0U];
            yield return [index, 1UL, 1UL, 3U, 0U];
            yield return [index, 0UL, 1UL, 0U, 0U];
            yield return [index, 1UL, 0UL, 4U, 0U];
            yield return [index, 1UL, 0UL, uint.MaxValue, 0U];
            yield return [index, 1UL, 1UL, 1U, 1U];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidSlots))]
    public void InvalidSlotRejectsTheWholeSnapshot(int index, ulong count, ulong luid, uint kind, uint reserved)
    {
        var bytes = Buffer();
        WriteSlot(bytes, index, count, luid, kind);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + index * 24 + 20), reserved);
        AssertRejected(bytes, "observation-value-invalid");
    }

    private static byte[] Buffer()
    {
        var bytes = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 128);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 3);
        return bytes;
    }

    private static void WriteSlot(byte[] bytes, int index, ulong count, ulong luid, uint kind)
    {
        var slot = bytes.AsSpan(8 + index * 24);
        BinaryPrimitives.WriteUInt64LittleEndian(slot, count);
        BinaryPrimitives.WriteUInt64LittleEndian(slot[8..], luid);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[16..], kind);
    }

    private static void AssertRejected(byte[] bytes, string expected)
    {
        var result = WindowsGpuPlacementInjector.DecodeDeviceObservations(bytes);
        Assert.False(result.Success);
        Assert.Null(result.Snapshot);
        Assert.Equal(expected, result.Status);
    }
}
