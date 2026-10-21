using System.Buffers.Binary;
using ResourceManager.ReadWrite.Windows.Devices.Usb;

namespace Resource_Manager_APP.Tests;

public sealed class UsbBosSuperSpeedCapabilityParserTests
{
    [Fact]
    public void CompleteBosDeclaringOrdinarySuperSpeedHasExactFiveGigabitMaximum()
    {
        Assert.Equal<double?>(5000d, UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(
            Bos(SuperSpeedCapability())));
        Assert.Equal<double?>(5000d, UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(
            Bos(Usb20ExtensionCapability(), ContainerIdCapability(), SuperSpeedCapability())));
    }

    [Fact]
    public void UnsupportedAmbiguousAndMalformedBosValuesRemainUnavailable()
    {
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond([]));
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(
            Bos(SuperSpeedCapability(supportedSpeeds: 0x0004))));
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(
            Bos(SuperSpeedCapability(), SuperSpeedCapability())));
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(
            Bos(SuperSpeedCapability(), SuperSpeedPlusCapability())));
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(
            Bos(SuperSpeedCapability(), UnknownCapability())));

        var shortSuperSpeed = SuperSpeedCapability();
        shortSuperSpeed[0] = 9;
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(Bos(shortSuperSpeed)));

        var reservedSpeed = SuperSpeedCapability(supportedSpeeds: 0x8008);
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(Bos(reservedSpeed)));

        var invalidFunctionality = SuperSpeedCapability(supportedSpeeds: 0x0008, functionalitySupport: 2);
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(Bos(invalidFunctionality)));

        var complete = Bos(SuperSpeedCapability());
        var truncated = complete[..^1];
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(truncated));
    }

    [Fact]
    public void WrongHeaderAndTruncatedCapabilityCountRemainUnavailable()
    {
        var wrongHeader = Bos(SuperSpeedCapability());
        wrongHeader[1] = 0x02;
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(wrongHeader));

        var missingCapability = Bos(SuperSpeedCapability());
        missingCapability[4] = 2;
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(missingCapability));

        var malformedType = Bos(SuperSpeedCapability());
        malformedType[6] = 0x02;
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(malformedType));
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 20)]
    [InlineData(31, 140)]
    public void SuperSpeedPlusAttributeCountIsZeroBasedAndDoesNotInventScalarMaximum(byte ssac, int expectedLength)
    {
        var capability = SuperSpeedPlusCapability(ssac);
        Assert.Equal(expectedLength, capability.Length);
        Assert.True(UsbBosSuperSpeedCapabilityParser.IsValidSuperSpeedPlusCapability(capability));
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(
            Bos(SuperSpeedCapability(), capability)));
    }

    [Fact]
    public void SuperSpeedPlusAttributeCountMustMatchDescriptorLength()
    {
        var zeroBasedCountWithTwoAttributes = SuperSpeedPlusCapability(0);
        Array.Resize(ref zeroBasedCountWithTwoAttributes, 20);
        zeroBasedCountWithTwoAttributes[0] = 20;
        Assert.False(UsbBosSuperSpeedCapabilityParser.IsValidSuperSpeedPlusCapability(zeroBasedCountWithTwoAttributes));

        var oneBasedCountMissingAttribute = SuperSpeedPlusCapability(1)[..^sizeof(uint)];
        oneBasedCountMissingAttribute[0] = (byte)oneBasedCountMissingAttribute.Length;
        Assert.False(UsbBosSuperSpeedCapabilityParser.IsValidSuperSpeedPlusCapability(oneBasedCountMissingAttribute));
    }

    [Fact]
    public void ParsesTypedPerSublinkRatesAndKeepsAggregateScalarUnavailable()
    {
        var capability = SuperSpeedPlusCapability(3);
        BinaryPrimitives.WriteUInt32LittleEndian(capability.AsSpan(4, sizeof(uint)), 0x00000023);
        BinaryPrimitives.WriteUInt16LittleEndian(capability.AsSpan(8, sizeof(ushort)), 0x1100);
        BinaryPrimitives.WriteUInt32LittleEndian(capability.AsSpan(12, sizeof(uint)), 0x000A4030);
        BinaryPrimitives.WriteUInt32LittleEndian(capability.AsSpan(16, sizeof(uint)), 0x000A40B0);
        BinaryPrimitives.WriteUInt32LittleEndian(capability.AsSpan(20, sizeof(uint)), 0x00054031);
        BinaryPrimitives.WriteUInt32LittleEndian(capability.AsSpan(24, sizeof(uint)), 0x000540B1);

        var bos = Bos(SuperSpeedCapability(), capability);
        var parsed = UsbBosSuperSpeedCapabilityParser.ParseSuperSpeedPlusCapability(bos)
            ?? throw new InvalidOperationException("Expected one structurally valid SSP declaration.");
        Assert.True(UsbBosSuperSpeedCapabilityParser.IsValidSuperSpeedPlusCapability(capability));
        Assert.Equal((byte)28, parsed.DescriptorLength);
        Assert.Equal((byte)0x10, parsed.DescriptorType);
        Assert.Equal((byte)0x0A, parsed.DeviceCapabilityType);
        Assert.Equal(0u, parsed.RawBmAttributes & 0xFFFFFE00u);
        Assert.Equal(4, parsed.SublinkSpeedAttributeCount);
        Assert.Equal(2, parsed.SublinkSpeedIdCount);
        Assert.Equal((byte)0, parsed.MinimumSublinkSpeedAttributeId);
        Assert.Equal((byte)1, parsed.MinimumReceiveLaneCountEncoded);
        Assert.Equal((byte)1, parsed.MinimumTransmitLaneCountEncoded);
        Assert.Equal((ushort)0, parsed.RawReservedWord);
        Assert.Equal(4, parsed.SublinkSpeedAttributes.Length);
        var receive = parsed.SublinkSpeedAttributes[0];
        Assert.Equal(0x000A4030u, receive.RawValue);
        Assert.Equal((byte)0, receive.SublinkSpeedAttributeId);
        Assert.Equal((byte)3, receive.LaneSpeedExponent);
        Assert.Equal((byte)0, receive.SublinkTypeMode);
        Assert.Equal((byte)0, receive.SublinkTypeDirection);
        Assert.Equal((byte)1, receive.LinkProtocol);
        Assert.Equal((ushort)10, receive.LaneSpeedMantissa);
        Assert.Equal(10_000_000_000m, receive.MaximumLaneBitRatePerSecond);
        Assert.Equal(10_000m, receive.MaximumLaneMegabitsPerSecond);
        var transmit = parsed.SublinkSpeedAttributes[1];
        Assert.Equal(0x000A40B0u, transmit.RawValue);
        Assert.Equal((byte)1, transmit.SublinkTypeDirection);
        Assert.Equal((byte)0, transmit.ReservedBits);
        var secondSpeedReceive = parsed.SublinkSpeedAttributes[2];
        Assert.Equal(0x00054031u, secondSpeedReceive.RawValue);
        Assert.Equal((byte)1, secondSpeedReceive.SublinkSpeedAttributeId);
        Assert.Equal(5_000m, secondSpeedReceive.MaximumLaneMegabitsPerSecond);
        Assert.Equal(0x000540B1u, parsed.SublinkSpeedAttributes[3].RawValue);
        Assert.Null(UsbBosSuperSpeedCapabilityParser.DeclaredMaximumMegabitsPerSecond(bos));
    }

    [Fact]
    public void StructurallyParsesSspAndRetainsReservedOrUnknownRawBits()
    {
        var capability = SuperSpeedPlusCapability(0);
        capability[3] = 0xA5;
        BinaryPrimitives.WriteUInt32LittleEndian(capability.AsSpan(4, sizeof(uint)), 0x80000000);
        BinaryPrimitives.WriteUInt16LittleEndian(capability.AsSpan(8, sizeof(ushort)), 0x00F0);
        BinaryPrimitives.WriteUInt16LittleEndian(capability.AsSpan(10, sizeof(ushort)), 0xBEEF);
        BinaryPrimitives.WriteUInt32LittleEndian(capability.AsSpan(12, sizeof(uint)), 0xD7ABCDEF);

        var parsed = UsbBosSuperSpeedCapabilityParser.ParseSuperSpeedPlusCapability(Bos(capability))
            ?? throw new InvalidOperationException("Expected the structurally bounded SSP fields to be retained.");
        Assert.False(UsbBosSuperSpeedCapabilityParser.IsValidSuperSpeedPlusCapability(capability));
        Assert.Equal((byte)0xA5, parsed.ReservedByte);
        Assert.Equal(0x80000000u, parsed.RawBmAttributes);
        Assert.Equal(0x80000000u, parsed.AttributesReservedBits);
        Assert.Equal((ushort)0x00F0, parsed.RawFunctionalitySupport);
        Assert.Equal((byte)0x0F, parsed.FunctionalityReservedBits);
        Assert.Equal((ushort)0xBEEF, parsed.RawReservedWord);
        Assert.Equal(0xD7ABCDEFu, Assert.Single(parsed.SublinkSpeedAttributes).RawValue);
        Assert.Equal((byte)((0xD7ABCDEFu >> 8) & 0x3F), parsed.SublinkSpeedAttributes[0].ReservedBits);
    }

    [Fact]
    public void TypedSspParserRejectsIncorrectAttributeArrayLength()
    {
        var capability = SuperSpeedPlusCapability(0);
        Array.Resize(ref capability, 20);
        capability[0] = 20;
        Assert.Null(UsbBosSuperSpeedCapabilityParser.ParseSuperSpeedPlusCapability(Bos(capability)));
    }

    private static byte[] Bos(params byte[][] capabilities)
    {
        var length = 5 + capabilities.Sum(static capability => capability.Length);
        var bos = new byte[length];
        bos[0] = 5;
        bos[1] = 0x0F;
        BinaryPrimitives.WriteUInt16LittleEndian(bos.AsSpan(2, 2), (ushort)length);
        bos[4] = (byte)capabilities.Length;
        var offset = 5;
        foreach (var capability in capabilities)
        {
            capability.CopyTo(bos, offset);
            offset += capability.Length;
        }
        return bos;
    }

    private static byte[] SuperSpeedCapability(
        ushort supportedSpeeds = 0x0008,
        byte functionalitySupport = 3)
        => [10, 0x10, 0x03, 0, (byte)supportedSpeeds, (byte)(supportedSpeeds >> 8),
            functionalitySupport, 0, 0, 0];

    private static byte[] SuperSpeedPlusCapability(byte ssac = 0)
    {
        var descriptor = new byte[12 + ((ssac + 1) * sizeof(uint))];
        descriptor[0] = (byte)descriptor.Length;
        descriptor[1] = 0x10;
        descriptor[2] = 0x0A;
        descriptor[4] = ssac;
        return descriptor;
    }

    private static byte[] Usb20ExtensionCapability()
        => [7, 0x10, 0x02, 0, 0, 0, 0];

    private static byte[] ContainerIdCapability()
        => [20, 0x10, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private static byte[] UnknownCapability()
        => [3, 0x10, 0x7F];
}
