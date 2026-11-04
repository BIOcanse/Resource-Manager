namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadDisplayIdModes(ReadOnlySpan<byte> block, uint index, int sectionCount, int sectionIndex,
        List<EdidModeDeclaration> modes,
        List<EdidDynamicTimingRangeDeclaration> dynamicRanges,
        List<EdidAdaptiveSyncRangeDeclaration> adaptiveSyncRanges, ref bool adaptiveSyncSeen,
        ModeCtaReferences references)
    {
        // Document1.3 uses structure1.2; documents2.0/2.1/2.1a use structure2.0.
        // The document revision is not an encoded structure revision.
        if (block[1] is not (0x12 or 0x20) || block[2] > 121)
            return false;
        if (sectionIndex == 0)
        {
            if (block[3] == 0 || block[3] > (block[1] < 0x20 ? 6 : 8)
                || block[4] != (block[1] == 0x12 ? sectionCount - 1 : 0))
                return false;
        }
        else if (block[3] != 0 || block[4] != 0)
            return false;
        var end = checked(5 + (int)block[2]);
        var checksum = 0;
        for (var offset = 1; offset <= end; offset++)
            checksum = checked(checksum + block[offset]);
        if ((checksum & 255) != 0 || block.Slice(end + 1, 126 - end).IndexOfAnyExcept((byte)0) >= 0)
            return false;
        for (var offset = 5; offset < end;)
        {
            if (block.Slice(offset, end - offset).IndexOfAnyExcept((byte)0) < 0)
                break;
            if (end - offset < 3)
                return false;
            var tag = block[offset];
            var flags = block[offset + 1];
            var length = block[offset + 2];
            var revision = (byte)(flags & 7);
            if (checked(offset + 3 + length) > end)
                return false;
            var payload = block.Slice(offset + 3, length);
            var typeI = tag == 3 && block[1] < 0x20;
            var typeVII = tag == 0x22 && block[1] >= 0x20;
            if (tag == 0x11 && block[1] == 0x12)
            {
                if (!ReadDisplayIdTypeV(payload, index, checked((uint)(offset + 3)), flags, block[1], modes))
                    return false;
            }
            else if (tag == 0x13 && block[1] == 0x12)
            {
                if (!ReadDisplayIdTypeVi(payload, index, checked((uint)(offset + 3)), flags, block[1], modes))
                    return false;
            }
            else if (typeI || typeVII)
            {
                var stride = typeI ? 20 : checked(20 + ((flags & 0x70) >> 4));
                // DisplayID2.0 E9 and2.1/2.1a table4-17 define only20-byte
                // descriptors; nonzero extension sizes remain reserved.
                if (stride != 20 || length == 0 || length % stride != 0
                    || typeI && (revision > 1 || (flags & 0xF8) != 0)
                    || typeVII && (revision > 2 || (flags & 0x80) != 0 || revision == 0 && (flags & 8) != 0))
                    return false;
                for (var relative = 0; relative < length; relative += stride)
                    if (!ReadDisplayIdDetailed(payload.Slice(relative, stride), index,
                        checked((uint)(offset + 3 + relative)), revision, flags, block[1], typeVII, modes))
                        return false;
            }
            else if (tag == 0x04 && block[1] == 0x12)
            {
                if (!ReadDisplayIdCompactTimings(payload, index, checked((uint)(offset + 3)), flags, block[1], modes))
                    return false;
            }
            else if (tag == 0x05 && block[1] < 0x20)
            {
                if (!ReadDisplayIdShortTimings(payload, index, checked((uint)(offset + 3)),
                    flags, block[1], modes))
                    return false;
            }
            else if (tag == 0x06 && block[1] < 0x20)
            {
                if (!ReadDisplayIdLegacyCodeTimings(payload, index, checked((uint)(offset + 3)),
                    flags, block[1], modes))
                    return false;
            }
            else if (tag == 0x23 && block[1] >= 0x20)
            {
                if (!ReadDisplayIdCodeTimings(payload, index, checked((uint)(offset + 3)),
                    flags, block[1], modes))
                    return false;
            }
            else if (tag == 0x24 && block[1] >= 0x20)
            {
                if (!ReadDisplayIdFormulaModes(payload, index, checked((uint)(offset + 3)),
                    flags, block[1], modes))
                    return false;
            }
            else if (tag == 0x2A && block[1] >= 0x20)
            {
                if (!ReadDisplayIdCvtModes(payload, index, checked((uint)(offset + 3)),
                    flags, block[1], modes))
                    return false;
            }
            else if (tag == 0x2B && block[1] == 0x20)
            {
                // One block per complete modern structure, including an empty block.
                if (adaptiveSyncSeen || !ReadDisplayIdAdaptiveSyncRanges(payload, index,
                    checked((uint)(offset + 3)), flags, block[1], adaptiveSyncRanges))
                    return false;
                adaptiveSyncSeen = true;
            }
            else if (tag == 0x25 && block[1] == 0x20)
            {
                if (sectionIndex != 0 || !ReadDisplayIdDynamicRange(payload, index,
                    checked((uint)(offset + 3)), flags, block[1], dynamicRanges))
                    return false;
            }
            else if ((tag is 0x07 or 0x08) && block[1] < 0x20)
            {
                if (!ReadDisplayIdBitmap(payload, index, checked((uint)(offset + 3)),
                    tag, flags, block[1], modes))
                    return false;
            }
            else if (tag == 0x81 && block[1] >= 0x20)
            {
                if (!ReadDisplayIdCtaModes(payload, index, checked((uint)(offset + 3)),
                    flags, block[1], modes, references))
                    return false;
            }
            else if (!IsNonModeDisplayIdBlock(block[1], tag, flags, length))
                return false;
            offset = checked(offset + 3 + length);
        }
        return true;
    }

    private static bool IsNonModeDisplayIdBlock(byte version, byte tag, byte flags, int length)
    {
        // Conservative, explicit whitelist. Stereo/tile and timing-range blocks are not silently skipped.
        if (version < 0x20)
        {
            if (tag == 0x02) // Color characteristics, with revision/coordinate-format flags.
                return (flags & 7) <= 1 && length >= 1;
            return flags == 0 && (tag switch
            {
                0x00 => length >= 12, // Product identification and serial text.
                0x01 => length == 12, // Display parameters, not a timing descriptor.
                0x0A or 0x0B => true, // Product serial / ASCII text.
                0x0C => length == 13, // Display technology and physical pixels.
                0x0D => length == 6, // Interface power sequencing.
                0x0E => length >= 1, // Transfer characteristics.
                0x0F => length == 10, // Interface color/depth capability, no timing entries.
                _ => false
            });
        }
        if (tag == 0x21) // Display parameters v2, revision0/1 and image-size unit flag.
            return length == 29 && (flags & 7) <= 1 && (flags & 0x7E) == 0;
        return flags == 0 && (tag switch
        {
            0x20 => length >= 12,
            0x26 => length >= 9, // Display interface color/depth features.
            0x29 => length == 16, // Container ID.
            0x2E => length == 6, // Brightness/luminance ranges, not video timing ranges.
            _ => false
        });
    }

    private static bool ReadDisplayIdDetailed(ReadOnlySpan<byte> d, uint block, uint offset, byte revision,
        byte containerFlags, byte containerRevision, bool typeVII, List<EdidModeDeclaration> modes)
    {
        var clock = checked(((ulong)(d[0] | d[1] << 8 | d[2] << 16) + 1) * (typeVII ? 1000UL : 10_000UL));
        var h = checked((uint)(d[4] | d[5] << 8) + 1);
        var hb = checked((uint)(d[6] | d[7] << 8) + 1);
        var hf = checked((uint)(d[8] | (d[9] & 127) << 8) + 1);
        var hs = checked((uint)(d[10] | d[11] << 8) + 1);
        var v = checked((uint)(d[12] | d[13] << 8) + 1);
        var vb = checked((uint)(d[14] | d[15] << 8) + 1);
        var vf = checked((uint)(d[16] | (d[17] & 127) << 8) + 1);
        var vs = checked((uint)(d[18] | d[19] << 8) + 1);
        var interlaced = (d[3] & 16) != 0;
        if (checked(hf + hs) > hb || checked(vf + vs) > vb || (d[3] & 15) > 8 || (d[3] & 0x60) == 0x60)
            return false;
        var lines = checked(v + (ulong)vb);
        // Document1.3 defines structure1.2 full-frame timings. TypeVII reuses
        // those fields except pixel-clock precision, including CTA encapsulation.
        if (interlaced && (lines & 1) == 0)
            return false;
        var denominator = checked(((ulong)h + hb) * lines);
        modes.Add(new(block, offset, typeVII ? "displayid-vii" : "displayid-i", revision, ModeBytes(d))
        {
            Width = h, Height = v, Interlaced = interlaced, ContainerFlags = containerFlags, ContainerRevision = containerRevision,
            DscPassThrough = typeVII && revision >= 1 ? (containerFlags & 8) != 0 : null,
            Preferred = typeVII && revision >= 2 ? null : (d[3] & 128) != 0,
            YCbCr420Encoding = typeVII && revision >= 2 ? (d[3] & 128) != 0 : null,
            YCbCr420Supported = typeVII && revision >= 2 && (d[3] & 128) != 0 ? true : null,
            Timing = new(clock, h, hb, hf, hs, v, vb, vf, vs, d[3], (d[9] & 128) != 0, (d[17] & 128) != 0,
                ModeRate(clock, denominator), interlaced ? ModeRate(checked(clock * 2), denominator) : null)
        });
        return true;
    }
}
