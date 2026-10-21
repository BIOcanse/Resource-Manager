using System.Buffers.Binary;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class AtaExtendedCapacityInformationCollectorTests
{
    internal const ulong MaximumSectors = 0x0000FFFFFFFFFFFFUL;
    internal const string MaximumCapacity = "144115188075855360";

    [Theory]
    [InlineData(0UL)] [InlineData(123456UL)] [InlineData(0xFFFFFFFFUL)] [InlineData(ulong.MaxValue)]
    public void ValidExtendedCountWinsOverLegacyCountWithoutBorrowingOrTruncation(ulong legacy)
    {
        var raw = Raw(0x123456789ABC);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(200), legacy);
        var data = WindowsStorageIdentifyReader.ParseAta(raw)!;
        Assert.Equal(0x123456789ABCUL * 512, data.CapacityBytes);
        Assert.Equal("ATA Model", data.Model);
        Assert.Equal(raw, data.Raw);
    }

    [Theory]
    [InlineData(2, 8, 512000UL)]
    [InlineData(0, 8, 123456UL * 512)]
    [InlineData(ushort.MaxValue, 8, 123456UL * 512)]
    [InlineData(2, 0, 123456UL * 512)]
    [InlineData(0, ushort.MaxValue, 123456UL * 512)]
    [InlineData(2, ushort.MaxValue, null)]
    public void AdditionalGroupValidityDeterminesWhetherTheExtendedSupportBitCanBeUsed(ushort validity, ushort additional, ulong? expected)
    {
        var raw = Raw(1000);
        Word(raw, 53, validity); Word(raw, 69, additional);
        Assert.Equal(expected, WindowsStorageIdentifyReader.ParseAta(raw)!.CapacityBytes);
    }

    [Theory]
    [InlineData(0UL, false)] [InlineData(1UL, true)] [InlineData(MaximumSectors, true)]
    [InlineData(MaximumSectors + 1, false)] [InlineData(ulong.MaxValue, false)]
    public void ExtendedQwordCountStillHasTheProtocol48BitRangeAndNoFailureFallback(ulong sectors, bool valid)
    {
        var raw = Raw(sectors);
        var data = WindowsStorageIdentifyReader.ParseAta(raw)!;
        Assert.Equal(valid ? sectors * 512 : (ulong?)null, data.CapacityBytes);
        Assert.Equal(123456UL, BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(200)));
        Assert.NotNull(data.Model);
        Assert.Equal(raw, data.Raw);
    }

    [Fact]
    public void UnsupportedExtendedRegionIsIgnoredAndLba28KeepsItsOwnRange()
    {
        var raw = Raw(ulong.MaxValue);
        Word(raw, 69, 0); Word(raw, 83, 0x4000);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(120), 101);
        Assert.Equal(101UL * 512, WindowsStorageIdentifyReader.ParseAta(raw)!.CapacityBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(120), 0x10000000);
        Assert.Null(WindowsStorageIdentifyReader.ParseAta(raw)!.CapacityBytes);
        Word(raw, 69, 8);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(460), 1000);
        Assert.Equal(1000UL * 512, WindowsStorageIdentifyReader.ParseAta(raw)!.CapacityBytes);
    }

    [Fact]
    public void CapacityUsesLogicalSectorBytesAndInvalidUnitsOrOverflowLeaveOnlyThatValueEmpty()
    {
        var raw = Raw(MaximumSectors);
        Word(raw, 106, 0x7003);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(234), 2048);
        var data = WindowsStorageIdentifyReader.ParseAta(raw)!;
        Assert.Equal(MaximumSectors * 4096, data.CapacityBytes);
        Assert.Equal(32768UL, data.PhysicalSectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(234), uint.MaxValue);
        data = WindowsStorageIdentifyReader.ParseAta(raw)!;
        Assert.Null(data.CapacityBytes); Assert.NotNull(data.LogicalSectorBytes);
        Word(raw, 106, 0);
        data = WindowsStorageIdentifyReader.ParseAta(raw)!;
        Assert.Null(data.CapacityBytes); Assert.Null(data.LogicalSectorBytes);
        Assert.Equal("SERIAL-A", data.SerialNumber);
        Word(raw, 106, 0x4000); Word(raw, 49, 0);
        Assert.Null(WindowsStorageIdentifyReader.ParseAta(raw)!.CapacityBytes);
    }

    internal static byte[] Raw(ulong sectors = MaximumSectors)
    {
        var raw = StorageIdentifyInformationCollectorTests.AtaRaw();
        Word(raw, 53, 2); Word(raw, 69, 8);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(460), sectors);
        return raw;
    }

    internal static IReadOnlyList<DeviceTopologyStorageInformation> Declarations()
        => StorageIdentifyInformationCollectorTests.Declarations().Select(static device => device.Protocol == StorageIdentifyProtocol.Ata
            ? device with { Ata = WindowsStorageIdentifyReader.ParseAta(Raw()) } : device).ToArray();

    private static void Word(byte[] raw, int word, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(word * 2), value);
}
