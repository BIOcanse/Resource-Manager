using System.Buffers.Binary;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

internal static class DisplayEdidTypeVViTestData
{
    internal const string Name = DisplayEdidModeInformationCollectorTests.Name;

    internal static byte[] TypeVBytes(params byte[][] descriptors)
        => Build((0x11, Combine(descriptors)));

    internal static byte[] TypeViBytes(params byte[][] descriptors)
        => Build((0x13, Combine(descriptors)));

    internal static byte[] Bytes(bool unknown = false)
    {
        var typeV = TypeVDescriptor(options: unknown ? (byte)0xD2 : (byte)0xD1);
        return Build((0x11, typeV), (0x13, TypeViDescriptor(includeAspectAndSize: true)));
    }

    internal static byte[] TypeVDescriptor(byte options = 0xD1, byte unlistedByte1 = 0xA5,
        uint width = 1920, uint height = 1080, uint refreshHz = 60)
    {
        var descriptor = new byte[7];
        descriptor[0] = options;
        descriptor[1] = unlistedByte1;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(2, 2), checked((ushort)(width - 1)));
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(4, 2), checked((ushort)(height - 1)));
        descriptor[6] = checked((byte)(refreshHz - 1));
        return descriptor;
    }

    internal static byte[] TypeViDescriptor(uint pixelClockCode = 148_499,
        bool includeAspectAndSize = false, bool interlaced = false, byte stereoSupportCode = 1)
    {
        if (stereoSupportCode > 3)
            throw new ArgumentOutOfRangeException(nameof(stereoSupportCode));
        var descriptor = new byte[includeAspectAndSize ? 17 : 14];
        descriptor[0] = (byte)pixelClockCode;
        descriptor[1] = (byte)(pixelClockCode >> 8);
        descriptor[2] = (byte)(((pixelClockCode >> 16) & 0x3F)
            | (includeAspectAndSize ? 0x40 : 0) | 0x80);
        // 1920x1080, 280/88/44 horizontal and 45/4/5 vertical blank/porch/sync.
        descriptor[3] = 0x7F;
        descriptor[4] = 0x87;
        descriptor[5] = 0x37;
        descriptor[6] = 0x84;
        descriptor[7] = 0x17;
        descriptor[8] = 0x57;
        descriptor[9] = 0x10;
        descriptor[10] = 43;
        descriptor[11] = 44;
        descriptor[12] = 3;
        descriptor[13] = (byte)((interlaced ? 0x80 : 0) | (stereoSupportCode << 5) | 4);
        if (includeAspectAndSize)
        {
            descriptor[14] = 152;
            descriptor[15] = 0x2A; // Base size code 298 + 1 = 299 mm.
            descriptor[16] = 0x11; // Base high bits 1; size multiplier 1 + 1 = 2.
        }
        return descriptor;
    }

    internal static DeviceTopologySnapshot Snapshot(bool unknown = false)
        => DisplayEdidModeTestData.Snapshot(DisplayEdidModeTestData.Path(
            WindowsMonitorEdidReader.CreateReadResult(Bytes(unknown))) with { Active = false, TargetAvailable = false });

    internal static void RepairChecksums(byte[] bytes)
    {
        for (var offset = 128; offset < bytes.Length; offset += 128)
        {
            if (bytes[offset] == 0x70)
            {
                var checksumOffset = checked(offset + 5 + bytes[offset + 2]);
                bytes[checksumOffset] = 0;
                var sum = 0;
                for (var i = offset + 1; i < checksumOffset; i++) sum += bytes[i];
                bytes[checksumOffset] = unchecked((byte)-sum);
            }
            bytes[offset + 127] = 0;
            var outer = 0;
            for (var i = offset; i < offset + 127; i++) outer += bytes[i];
            bytes[offset + 127] = unchecked((byte)-outer);
        }
        bytes[127] = 0;
        var baseSum = 0;
        for (var i = 0; i < 127; i++) baseSum += bytes[i];
        bytes[127] = unchecked((byte)-baseSum);
    }

    internal static void AssertValue(JsonElement value)
    {
        var modes = value.GetProperty("modes");
        Assert.Equal(2, modes.GetArrayLength());
        var typeV = modes[0];
        Assert.Equal("displayid-v", typeV.GetProperty("encoding").GetString());
        Assert.Equal((1920u, 1080u, 60u),
            (typeV.GetProperty("width").GetUInt32(), typeV.GetProperty("height").GetUInt32(),
                typeV.GetProperty("nominalVerticalRateHz").GetUInt32()));
        Assert.True(typeV.GetProperty("preferred").GetBoolean());
        Assert.Equal("cvt", typeV.GetProperty("formula").GetString());
        Assert.True(typeV.GetProperty("reducedBlanking").GetBoolean());
        Assert.Equal(new byte[] { 0xD1, 0xA5, 0x7F, 0x07, 0x37, 0x04, 0x3B },
            typeV.GetProperty("rawDescriptor").EnumerateArray().Select(static item => item.GetByte()));
        var typeVOptions = typeV.GetProperty("displayIdTypeVOptions");
        Assert.Equal((byte)1, typeVOptions.GetProperty("formulaCode").GetByte());
        Assert.Equal((byte)2, typeVOptions.GetProperty("stereoSupportCode").GetByte());
        Assert.True(typeVOptions.GetProperty("fractionalRefreshSupported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, typeV.GetProperty("timing").ValueKind);
        Assert.Equal(JsonValueKind.Null, typeV.GetProperty("cvtParameters").ValueKind);

        var typeVi = modes[1];
        Assert.Equal("displayid-vi", typeVi.GetProperty("encoding").GetString());
        Assert.Equal((1920u, 1080u), (typeVi.GetProperty("width").GetUInt32(), typeVi.GetProperty("height").GetUInt32()));
        Assert.True(typeVi.GetProperty("preferred").GetBoolean());
        var timing = typeVi.GetProperty("timing");
        Assert.Equal(148_500_000UL, timing.GetProperty("pixelClockHz").GetUInt64());
        Assert.Equal(60UL, timing.GetProperty("frameRate").GetProperty("numerator").GetUInt64());
        Assert.Equal(1UL, timing.GetProperty("frameRate").GetProperty("denominator").GetUInt64());
        Assert.Equal(JsonValueKind.Null, timing.GetProperty("fieldRate").ValueKind);
        var viTiming = typeVi.GetProperty("displayIdTypeViTiming");
        Assert.Equal(148_500_000UL, viTiming.GetProperty("pixelClockHz").GetUInt64());
        Assert.Equal(280u, viTiming.GetProperty("horizontalBlankingPixels").GetUInt32());
        Assert.Equal(88u, viTiming.GetProperty("horizontalFrontPorchPixels").GetUInt32());
        Assert.Equal(44u, viTiming.GetProperty("horizontalSyncWidthPixels").GetUInt32());
        Assert.Equal(45u, viTiming.GetProperty("verticalBlankingLines").GetUInt32());
        Assert.Equal(4u, viTiming.GetProperty("verticalFrontPorchLines").GetUInt32());
        Assert.Equal(5u, viTiming.GetProperty("verticalSyncWidthLines").GetUInt32());
        Assert.Equal((byte)1, viTiming.GetProperty("stereoSupportCode").GetByte());
        var aspect = typeVi.GetProperty("aspectRatioDeclared");
        Assert.Equal((byte)152, aspect.GetProperty("code").GetByte());
        Assert.Equal(456u, aspect.GetProperty("horizontal").GetUInt32());
        Assert.Equal(256u, aspect.GetProperty("vertical").GetUInt32());
        var size = typeVi.GetProperty("displayIdTypeViImageSize");
        Assert.Equal(299u, size.GetProperty("verticalImageBaseSizeMillimeters").GetUInt32());
        Assert.Equal((byte)2, size.GetProperty("sizeMultiplier").GetByte());
        Assert.Equal(598u, size.GetProperty("verticalImageSizeMillimeters").GetUInt32());
        Assert.Equal(17, typeVi.GetProperty("rawDescriptor").GetArrayLength());
    }

    private static byte[] Combine(byte[][] descriptors) => descriptors.SelectMany(static bytes => bytes).ToArray();

    private static byte[] Build(params (byte Tag, byte[] Payload)[] blocks)
    {
        if (blocks.Length == 0 || blocks.Length > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(blocks));
        var bytes = new byte[checked((blocks.Length + 1) * 128)];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[18] = 1;
        bytes[19] = 4;
        bytes[20] = 0xB5;
        bytes[24] = 2;
        // EDID 1.4 marks every unused Standard Timing slot with 01 01.
        // Zero-filled slots are malformed and make the real base-block parser
        // reject the mode capability before it reaches the DisplayID sections.
        for (var standardTimingOffset = 38; standardTimingOffset < 54; standardTimingOffset += 2)
        {
            bytes[standardTimingOffset] = 1;
            bytes[standardTimingOffset + 1] = 1;
        }
        bytes[126] = checked((byte)blocks.Length);
        for (var index = 0; index < blocks.Length; index++)
        {
            var (tag, payload) = blocks[index];
            var dataLength = checked(3 + payload.Length);
            if (dataLength > 121)
                throw new ArgumentOutOfRangeException(nameof(blocks));
            var offset = checked((index + 1) * 128);
            bytes[offset] = 0x70;
            bytes[offset + 1] = 0x12;
            bytes[offset + 2] = checked((byte)dataLength);
            bytes[offset + 3] = index == 0 ? (byte)2 : (byte)0;
            bytes[offset + 4] = index == 0 ? checked((byte)(blocks.Length - 1)) : (byte)0;
            bytes[offset + 5] = tag;
            bytes[offset + 6] = 0;
            bytes[offset + 7] = checked((byte)payload.Length);
            payload.CopyTo(bytes, offset + 8);
            var innerChecksumOffset = checked(offset + 5 + dataLength);
            var innerSum = 0;
            for (var i = offset + 1; i < innerChecksumOffset; i++) innerSum += bytes[i];
            bytes[innerChecksumOffset] = unchecked((byte)-innerSum);
            var outerSum = 0;
            for (var i = offset; i < offset + 127; i++) outerSum += bytes[i];
            bytes[offset + 127] = unchecked((byte)-outerSum);
        }
        RepairChecksums(bytes);
        return bytes;
    }
}
