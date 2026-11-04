// Factual mapping attribution: ../../../ThirdPartyNotices/EdidModes.NOTICE.md
namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadBaseModes(ReadOnlySpan<byte> block, List<EdidModeDeclaration> modes,
        List<EdidRangeDeclaration> ranges)
    {
        // Legacy declarations give nominal dimensions/rates, not trustworthy complete pixel timings.
        ReadOnlySpan<(uint Width, uint Height, uint Rate, bool Interlaced)> established =
        [
            (720, 400, 70, false), (720, 400, 88, false), (640, 480, 60, false), (640, 480, 67, false),
            (640, 480, 72, false), (640, 480, 75, false), (800, 600, 56, false), (800, 600, 60, false),
            (800, 600, 72, false), (800, 600, 75, false), (832, 624, 75, false), (1024, 768, 87, true),
            (1024, 768, 60, false), (1024, 768, 70, false), (1024, 768, 75, false), (1280, 1024, 75, false),
            (1152, 870, 75, false)
        ];
        if ((block[37] & 0x7F) != 0) // Manufacturer-defined timing bits have no verified layout here.
            return false;
        for (var index = 0; index < established.Length; index++)
        {
            var offset = 35 + index / 8;
            var bit = 7 - index % 8;
            if ((block[offset] & 1 << bit) == 0)
                continue;
            var mode = established[index];
            modes.Add(new(0, checked((uint)offset), "edid-established", block[19], ModeBytes(block.Slice(offset, 1)))
            {
                Code = checked((uint)index), BitIndex = bit, Width = mode.Width, Height = mode.Height,
                Interlaced = mode.Interlaced, NominalVerticalRateHz = mode.Rate
            });
        }
        for (var offset = 38; offset < 54; offset += 2)
            if (!ReadStandardMode(block.Slice(offset, 2), checked((uint)offset), block[19], "edid-standard", modes))
                return false;
        for (var offset = 54; offset <= 108; offset += 18)
        {
            var descriptor = block.Slice(offset, 18);
            if ((descriptor[0] | descriptor[1]) != 0)
            {
                if (!ReadDetailedMode(descriptor, 0, checked((uint)offset), block[19], null,
                    offset == 54 && (block[24] & 2) != 0 ? true : null, modes))
                    return false;
                continue;
            }
            if (descriptor.IndexOfAnyExcept((byte)0) < 0)
                continue;
            if (descriptor[2] != 0)
                return false;
            if (descriptor[3] != 0xFD && descriptor[4] != 0)
                return false;
            switch (descriptor[3])
            {
                case 0xFA:
                    if (descriptor[17] != 0x0A)
                        return false;
                    for (var pair = 5; pair < 17; pair += 2)
                        if (!ReadStandardMode(descriptor.Slice(pair, 2), checked((uint)(offset + pair)),
                            block[19], "edid-additional-standard", modes))
                            return false;
                    break;
                case 0xF7:
                    if (block[19] < 4 || !ReadEstablishedThree(descriptor, checked((uint)offset), modes))
                        return false;
                    break;
                case 0xF8:
                    if (block[19] < 4 || descriptor[5] != 1)
                        return false;
                    if (descriptor.Slice(6, 3).IndexOfAnyExcept((byte)0) < 0)
                        return false;
                    for (var cvt = 6; cvt < 18; cvt += 3)
                        if (!ReadCvtCode(descriptor.Slice(cvt, 3), checked((uint)(offset + cvt)), modes))
                            return false;
                    break;
                case 0xFD:
                    if (!ReadRangeMode(descriptor, checked((uint)offset), block[19], ranges))
                        return false;
                    break;
                // Explicit non-timing monitor descriptors: dummy, color management/point, text, name, serial.
                case 0x10 or 0xF9 or 0xFB or 0xFC or 0xFE or 0xFF:
                    if (descriptor[4] != 0)
                        return false;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private static bool ReadStandardMode(ReadOnlySpan<byte> pair, uint offset, byte revision, string encoding,
        List<EdidModeDeclaration> modes)
    {
        if (pair[0] == 1 && pair[1] == 1)
            return true;
        if (pair[0] == 0)
            return false;
        var width = checked(((uint)pair[0] + 31) * 8);
        var (horizontal, vertical) = (pair[1] >> 6) switch
        {
            0 => revision < 3 ? (1u, 1u) : (16u, 10u),
            1 => (4u, 3u),
            2 => (5u, 4u),
            _ => (16u, 9u)
        };
        modes.Add(new(0, offset, encoding, revision, ModeBytes(pair))
        {
            Code = checked((uint)(pair[0] << 8 | pair[1])), Width = width,
            Height = checked(width * vertical / horizontal), NominalVerticalRateHz = checked((uint)(pair[1] & 63) + 60),
            Interlaced = false
        });
        return true;
    }

    private static bool ReadEstablishedThree(ReadOnlySpan<byte> descriptor, uint offset, List<EdidModeDeclaration> modes)
    {
        ReadOnlySpan<(uint Code, uint Width, uint Height, uint Rate, bool Reduced)> table =
        [
            (1,640,350,85,false),(2,640,400,85,false),(3,720,400,85,false),(7,640,480,85,false),
            (14,848,480,60,false),(12,800,600,85,false),(19,1024,768,85,false),(21,1152,864,75,false),
            (22,1280,768,60,true),(23,1280,768,60,false),(24,1280,768,75,false),(25,1280,768,85,false),
            (32,1280,960,60,false),(33,1280,960,85,false),(35,1280,1024,60,false),(37,1280,1024,85,false),
            (39,1360,768,60,false),(46,1440,900,60,true),(47,1440,900,60,false),(48,1440,900,75,false),
            (49,1440,900,85,false),(41,1400,1050,60,true),(42,1400,1050,60,false),(43,1400,1050,75,false),
            (44,1400,1050,85,false),(57,1680,1050,60,true),(58,1680,1050,60,false),(59,1680,1050,75,false),
            (60,1680,1050,85,false),(51,1600,1200,60,false),(52,1600,1200,65,false),(53,1600,1200,70,false),
            (54,1600,1200,75,false),(55,1600,1200,85,false),(62,1792,1344,60,false),(63,1792,1344,75,false),
            (65,1856,1392,60,false),(66,1856,1392,75,false),(68,1920,1200,60,true),(69,1920,1200,60,false),
            (70,1920,1200,75,false),(71,1920,1200,85,false),(73,1920,1440,60,false),(74,1920,1440,75,false)
        ];
        if (descriptor[5] != 0x0A || (descriptor[11] & 15) != 0
            || descriptor.Slice(12, 6).IndexOfAnyExcept((byte)0) >= 0)
            return false;
        for (var index = 0; index < table.Length; index++)
        {
            var relative = 6 + index / 8;
            var bit = 7 - index % 8;
            if ((descriptor[relative] & 1 << bit) == 0)
                continue;
            var t = table[index];
            modes.Add(new(0, offset, "edid-established-iii", descriptor[5], ModeBytes(descriptor))
            {
                Code = t.Code, BitIndex = relative * 8 + bit, Width = t.Width, Height = t.Height, Interlaced = false,
                NominalVerticalRateHz = t.Rate, ReducedBlanking = t.Reduced
            });
        }
        return true;
    }

    private static bool ReadCvtCode(ReadOnlySpan<byte> code, uint offset, List<EdidModeDeclaration> modes)
    {
        if (code.IndexOfAnyExcept((byte)0) < 0)
            return true;
        if (code[0] == 0 || (code[1] & 3) != 0 || (code[2] & 0x80) != 0 || (code[2] & 31) == 0)
            return false;
        var height = checked(2u * ((uint)(code[0] | (code[1] & 0xF0) << 4) + 1));
        var (h, v) = ((code[1] >> 2) & 3) switch
        {
            0 => (4u, 3u), 1 => (16u, 9u), 2 => (16u, 10u), _ => (15u, 9u)
        };
        var width = checked(height * h / v / 8 * 8);
        var preferred = (code[2] >> 5) & 3;
        if (preferred == 1 && (code[2] & 1) != 0)
            preferred = 4;
        if ((code[2] & 1 << (4 - preferred)) == 0)
            return false;
        ReadOnlySpan<uint> rates = [50, 60, 75, 85, 60];
        for (var rate = 0; rate < rates.Length; rate++)
            if ((code[2] & 1 << (4 - rate)) != 0)
                modes.Add(new(0, offset, "edid-cvt-three-byte", 1, ModeBytes(code))
                {
                    Code = checked((uint)(code[0] << 16 | code[1] << 8 | code[2])), BitIndex = 16 + 4 - rate,
                    Width = width, Height = height, Interlaced = false, NominalVerticalRateHz = rates[rate],
                    Formula = "cvt", ReducedBlanking = rate == 4, Preferred = rate == preferred
                });
        return true;
    }

    private static bool ReadRangeMode(ReadOnlySpan<byte> d, uint offset, byte revision, List<EdidRangeDeclaration> ranges)
    {
        if (revision < 4 && d[4] != 0 || revision >= 4 && ((d[4] & 0xF0) != 0
            || (d[4] & 1) != 0 && (d[4] & 2) == 0 || (d[4] & 4) != 0 && (d[4] & 8) == 0))
            return false;
        var minV = checked((uint)d[5] + ((d[4] & 1) != 0 ? 255u : 0));
        var maxV = checked((uint)d[6] + ((d[4] & 2) != 0 ? 255u : 0));
        var minH = checked(((uint)d[7] + ((d[4] & 4) != 0 ? 255u : 0)) * 1000);
        var maxH = checked(((uint)d[8] + ((d[4] & 8) != 0 ? 255u : 0)) * 1000);
        if (minV == 0 || minH == 0 || minV > maxV || minH > maxH || d[10] is not (0 or 1 or 2 or 4)
            || revision < 4 && d[10] is 1 or 4)
            return false;
        ulong? clock = d[9] == 0 ? null : checked((ulong)d[9] * 10_000_000);
        var formula = d[10] switch { 0 => "gtf-default", 1 => "range-only", 2 => "gtf-secondary", _ => "cvt" };
        EdidSecondaryGtfCurve? gtf = d[10] == 2
            ? new(checked((uint)d[12] * 2000), d[13], checked((uint)(d[14] | d[15] << 8)), d[16], d[17]) : null;
        EdidCvtRange? cvt = null;
        if (d[10] == 4)
        {
            if (d[11] != 0x11 || (d[14] & 7) != 0 || (d[15] & 7) != 0 || (d[16] & 15) != 0
                || (d[15] >> 5) > 4)
                return false;
            var correction = checked((ulong)(d[12] >> 2) * 250_000);
            if (clock is { } maximum)
            {
                if (correction >= maximum)
                    return false;
                clock = maximum - correction;
            }
            else if (correction != 0)
                return false;
            var pixels = checked((uint)((d[12] & 3) << 8 | d[13]) * 8);
            cvt = new(d[11], pixels == 0 ? null : pixels, (byte)(d[14] & 0xF8), (byte)(d[15] >> 5),
                (d[15] & 8) != 0, (d[15] & 16) != 0, (byte)(d[16] & 0xF0), d[17] == 0 ? null : d[17]);
        }
        ranges.Add(new(0, offset, "edid-range", revision, ModeBytes(d), formula, minV, maxV, minH, maxH, clock)
        { SecondaryGtf = gtf, Cvt = cvt });
        return true;
    }
}
