using System.Text.Json;

namespace ResourceManager.TestFixtures;

internal static class DisplayIdTypeVViFixture
{
    private const int TypeVBlockOffset = 128;
    private const int TypeVDescriptorOffset = TypeVBlockOffset + 8;
    private const int TypeViBlockOffset = 256;
    private const int TypeViDescriptorOffset = TypeViBlockOffset + 8;

    internal static byte[] Bytes()
    {
        var bytes = new byte[3 * 128];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[18] = 1;
        bytes[19] = 4;
        bytes[20] = 0xB5;
        bytes[24] = 2;
        for (var offset = 38; offset < 54; offset += 2)
        {
            bytes[offset] = 1;
            bytes[offset + 1] = 1;
        }
        bytes[126] = 2;

        WriteDisplayIdSection(bytes, TypeVBlockOffset, 2, 1, 0x11, TypeVDescriptor());
        var typeViDescriptors = TypeViDescriptor(includeAspectAndSize: false)
            .Concat(TypeViDescriptor(includeAspectAndSize: true)).ToArray();
        WriteDisplayIdSection(bytes, TypeViBlockOffset, 0, 0, 0x13, typeViDescriptors);
        RepairChecksums(bytes);
        return bytes;
    }

    internal static void SetUnsupportedTypeV(byte[] bytes, bool unsupported)
    {
        Require(bytes.Length == 384 && bytes[TypeVBlockOffset] == 0x70
            && bytes[TypeVBlockOffset + 5] == 0x11
            && bytes[TypeVDescriptorOffset] is 0xD1 or 0xD2,
            "expected the Type V/VI fixture before mutating Type V formula code");
        bytes[TypeVDescriptorOffset] = unsupported ? (byte)0xD2 : (byte)0xD1;
        RepairChecksums(bytes);
    }

    internal static void SetContradictoryTypeViClock(byte[] bytes, bool contradictory)
    {
        var clockFlagsOffset = TypeViDescriptorOffset + 2;
        var scanFlagsOffset = TypeViDescriptorOffset + 13;
        Require(bytes.Length == 384 && bytes[TypeViBlockOffset] == 0x70
            && bytes[TypeViBlockOffset + 5] == 0x13
            && (bytes[clockFlagsOffset] & 0x40) == 0,
            "expected the 14-byte Type VI record before mutating its clock boundary");
        bytes[clockFlagsOffset] = contradictory
            ? (byte)(bytes[clockFlagsOffset] | 0x20)
            : (byte)(bytes[clockFlagsOffset] & ~0x20);
        bytes[scanFlagsOffset] = contradictory
            ? (byte)(bytes[scanFlagsOffset] | 0x80)
            : (byte)(bytes[scanFlagsOffset] & ~0x80);
        RepairChecksums(bytes);
    }

    internal static void RepairChecksums(byte[] bytes)
    {
        Require(bytes.Length == 384 && bytes[126] == 2, "two-extension Type V/VI fixture size");
        for (var offset = 128; offset < bytes.Length; offset += 128)
        {
            Require(bytes[offset] == 0x70, "DisplayID extension tag");
            var checksumOffset = checked(offset + 5 + bytes[offset + 2]);
            bytes[checksumOffset] = 0;
            var sectionSum = 0;
            for (var index = offset + 1; index < checksumOffset; index++)
                sectionSum += bytes[index];
            bytes[checksumOffset] = unchecked((byte)-sectionSum);

            bytes[offset + 127] = 0;
            var blockSum = 0;
            for (var index = offset; index < offset + 127; index++)
                blockSum += bytes[index];
            bytes[offset + 127] = unchecked((byte)-blockSum);
        }

        bytes[127] = 0;
        var baseSum = 0;
        for (var index = 0; index < 127; index++)
            baseSum += bytes[index];
        bytes[127] = unchecked((byte)-baseSum);
    }

    private static byte[] TypeVDescriptor() => [0xD1, 0xA5, 0x7F, 0x07, 0x37, 0x04, 0x3B];

    private static byte[] TypeViDescriptor(bool includeAspectAndSize)
    {
        const uint pixelClockCode = 148_499;
        var descriptor = new byte[includeAspectAndSize ? 17 : 14];
        descriptor[0] = (byte)(pixelClockCode & 0xFF);
        descriptor[1] = (byte)((pixelClockCode >> 8) & 0xFF);
        descriptor[2] = (byte)(((pixelClockCode >> 16) & 0x3F) | 0x80
            | (includeAspectAndSize ? 0x40 : 0));
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
        descriptor[13] = 0x24;
        if (includeAspectAndSize)
        {
            descriptor[14] = 152;
            descriptor[15] = 0x2A;
            descriptor[16] = 0x11;
        }
        return descriptor;
    }

    private static void WriteDisplayIdSection(byte[] bytes, int offset, byte productType, byte extensionCount,
        byte tag, byte[] descriptor)
    {
        bytes[offset] = 0x70;
        bytes[offset + 1] = 0x12;
        bytes[offset + 2] = checked((byte)(3 + descriptor.Length));
        bytes[offset + 3] = productType;
        bytes[offset + 4] = extensionCount;
        bytes[offset + 5] = tag;
        bytes[offset + 6] = 0;
        bytes[offset + 7] = checked((byte)descriptor.Length);
        descriptor.CopyTo(bytes, offset + 8);
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidDataException("DisplayID Type V/VI package fixture: " + reason);
    }
}

internal static class DisplayIdTypeVViConsumerAssertions
{
    internal static void Verify(JsonElement value)
    {
        var modes = value.GetProperty("modes");
        Require(modes.GetArrayLength() == 3, "Type V and both Type VI stride forms are published");
        Require(value.GetProperty("ranges").GetArrayLength() == 0
            && value.GetProperty("dynamicTimingRanges").GetArrayLength() == 0
            && value.GetProperty("adaptiveSyncRanges").GetArrayLength() == 0,
            "no unrelated declaration is synthesized");

        var typeV = modes[0];
        Require(typeV.GetProperty("encoding").GetString() == "displayid-v"
            && typeV.GetProperty("blockIndex").GetUInt32() == 1
            && typeV.GetProperty("byteOffset").GetUInt32() == 8
            && typeV.GetProperty("revision").GetByte() == 0
            && typeV.GetProperty("containerFlags").GetByte() == 0
            && typeV.GetProperty("containerRevision").GetByte() == 0x12,
            "Type V original encoding and physical identity");
        Require(typeV.GetProperty("width").GetUInt32() == 1920
            && typeV.GetProperty("height").GetUInt32() == 1080
            && typeV.GetProperty("nominalVerticalRateHz").GetUInt32() == 60
            && typeV.GetProperty("preferred").GetBoolean()
            && typeV.GetProperty("formula").GetString() == "cvt"
            && typeV.GetProperty("reducedBlanking").GetBoolean(),
            "Type V dimensions, nominal rate, options, and formula");
        Require(typeV.GetProperty("timing").ValueKind == JsonValueKind.Null
            && typeV.GetProperty("cvtParameters").ValueKind == JsonValueKind.Null,
            "Type V does not synthesize unlisted timing parameters");
        Require(typeV.GetProperty("displayIdTypeVOptions").GetProperty("formulaCode").GetByte() == 1
            && typeV.GetProperty("displayIdTypeVOptions").GetProperty("stereoSupportCode").GetByte() == 2
            && typeV.GetProperty("displayIdTypeVOptions").GetProperty("fractionalRefreshSupported").GetBoolean(),
            "Type V option bits retain their declared meanings");
        Require(Raw(typeV).SequenceEqual(new byte[] { 0xD1, 0xA5, 0x7F, 0x07, 0x37, 0x04, 0x3B }),
            "Type V raw declaration including unlisted byte");

        var typeVi14 = modes[1];
        VerifyTypeViHeaderAndGeometry(typeVi14, 8);
        VerifyTypeViTiming(typeVi14);
        Require(Raw(typeVi14).Length == 14
            && !typeVi14.TryGetProperty("aspectRatioDeclared", out _)
            && !typeVi14.TryGetProperty("displayIdTypeViImageSize", out _),
            "14-byte Type VI record omits the optional tail");

        var typeVi17 = modes[2];
        VerifyTypeViHeaderAndGeometry(typeVi17, 22);
        VerifyTypeViTiming(typeVi17);
        var aspect = typeVi17.GetProperty("aspectRatioDeclared");
        Require(aspect.GetProperty("code").GetByte() == 152
            && aspect.GetProperty("horizontal").GetUInt32() == 456
            && aspect.GetProperty("vertical").GetUInt32() == 256,
            "Type VI declared aspect multiplier");
        var size = typeVi17.GetProperty("displayIdTypeViImageSize");
        Require(size.GetProperty("verticalImageBaseSizeMillimeters").GetUInt32() == 299
            && size.GetProperty("sizeMultiplier").GetByte() == 2
            && size.GetProperty("verticalImageSizeMillimeters").GetUInt32() == 598,
            "Type VI declared optional image size");
        Require(Raw(typeVi17).Length == 17, "Type VI optional tail is retained");
    }

    internal static void VerifyContradictoryTypeViClock(JsonElement value)
    {
        var modes = value.GetProperty("modes");
        Require(modes.GetArrayLength() == 3, "clock ambiguity is local to one Type VI record");
        var typeVi14 = modes[1];
        VerifyTypeViHeaderAndGeometry(typeVi14, 8);
        Require(typeVi14.GetProperty("interlaced").GetBoolean()
            && typeVi14.GetProperty("timing").ValueKind == JsonValueKind.Null,
            "ambiguous interlaced Type VI record keeps geometry without a fabricated clock");
        var viTiming = typeVi14.GetProperty("displayIdTypeViTiming");
        Require(viTiming.GetProperty("pixelClockHz").ValueKind == JsonValueKind.Null
            && viTiming.GetProperty("horizontalBlankingPixels").GetUInt32() == 280
            && viTiming.GetProperty("horizontalFrontPorchPixels").GetUInt32() == 88
            && viTiming.GetProperty("horizontalSyncWidthPixels").GetUInt32() == 44
            && viTiming.GetProperty("verticalBlankingLines").GetUInt32() == 45
            && viTiming.GetProperty("verticalFrontPorchLines").GetUInt32() == 4
            && viTiming.GetProperty("verticalSyncWidthLines").GetUInt32() == 5
            && Raw(typeVi14)[2] == 0xA2 && Raw(typeVi14)[13] == 0xA4,
            "ambiguous clock withholds only clock/rates and retains declared sync plus raw bits");
        Require(modes[2].GetProperty("timing").GetProperty("pixelClockHz").GetUInt64() == 148_500_000,
            "the neighboring 17-byte Type VI clock remains available");
    }

    private static void VerifyTypeViHeaderAndGeometry(JsonElement typeVi, uint byteOffset)
    {
        Require(typeVi.GetProperty("encoding").GetString() == "displayid-vi"
            && typeVi.GetProperty("blockIndex").GetUInt32() == 2
            && typeVi.GetProperty("byteOffset").GetUInt32() == byteOffset
            && typeVi.GetProperty("revision").GetByte() == 0
            && typeVi.GetProperty("containerFlags").GetByte() == 0
            && typeVi.GetProperty("containerRevision").GetByte() == 0x12,
            "Type VI original encoding and physical identity");
        Require(typeVi.GetProperty("width").GetUInt32() == 1920
            && typeVi.GetProperty("height").GetUInt32() == 1080
            && typeVi.GetProperty("preferred").GetBoolean(),
            "Type VI active geometry and preferred flag");
    }

    private static void VerifyTypeViTiming(JsonElement typeVi)
    {
        Require(!typeVi.GetProperty("interlaced").GetBoolean(), "progressive Type VI declaration");
        var timing = typeVi.GetProperty("timing");
        Require(timing.GetProperty("pixelClockHz").GetUInt64() == 148_500_000
            && timing.GetProperty("frameRate").GetProperty("numerator").GetUInt64() == 60
            && timing.GetProperty("frameRate").GetProperty("denominator").GetUInt64() == 1
            && timing.GetProperty("fieldRate").ValueKind == JsonValueKind.Null,
            "Type VI clock, frame rate, and no invented field rate");
        var viTiming = typeVi.GetProperty("displayIdTypeViTiming");
        Require(viTiming.GetProperty("pixelClockHz").GetUInt64() == 148_500_000
            && viTiming.GetProperty("horizontalBlankingPixels").GetUInt32() == 280
            && viTiming.GetProperty("horizontalFrontPorchPixels").GetUInt32() == 88
            && viTiming.GetProperty("horizontalSyncWidthPixels").GetUInt32() == 44
            && viTiming.GetProperty("verticalBlankingLines").GetUInt32() == 45
            && viTiming.GetProperty("verticalFrontPorchLines").GetUInt32() == 4
            && viTiming.GetProperty("verticalSyncWidthLines").GetUInt32() == 5
            && viTiming.GetProperty("positiveHorizontalSync").GetBoolean()
            && viTiming.GetProperty("positiveVerticalSync").GetBoolean()
            && viTiming.GetProperty("stereoSupportCode").GetByte() == 1,
            "Type VI declared blanking, porch, sync, polarity, and stereo");
    }

    private static byte[] Raw(JsonElement mode)
        => mode.GetProperty("rawDescriptor").EnumerateArray().Select(static item => item.GetByte()).ToArray();

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidDataException("DisplayID Type V/VI package assertion: " + reason);
    }
}
