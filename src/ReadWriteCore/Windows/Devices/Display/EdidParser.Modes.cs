using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static EdidModeCapability? ReadModeCapability(ReadOnlySpan<byte> edid)
    {
        // This field has its own complete-chain contract. Existing raw/HDR/chromaticity are independent.
        if (edid.Length < 128 || !edid[..8].SequenceEqual(Header) || edid[18] != 1 || edid[19] > 4)
            return null;
        if (!TryReadModeBlockCount(edid, out var count) || edid.Length != checked(count * 128))
            return null;
        for (var index = 0; index < count; index++)
            if (!HasValidEdidBlockChecksum(edid.Slice(checked(index * 128), 128)))
                return null;

        var modes = new List<EdidModeDeclaration>();
        var ranges = new List<EdidRangeDeclaration>();
        var dynamicRanges = new List<EdidDynamicTimingRangeDeclaration>();
        var adaptiveSyncRanges = new List<EdidAdaptiveSyncRangeDeclaration>();
        var adaptiveSyncSeen = false;
        var cta = new ModeCtaReferences();
        if (!ReadBaseModes(edid[..128], modes, ranges))
            return null;
        var legacyDisplayIdCount = 0;
        for (var index = 1; index < count; index++)
            if (edid[checked(index * 128)] == 0x70 && edid[checked(index * 128 + 1)] == 0x12)
                legacyDisplayIdCount++;
        var legacyDisplayIdSeen = 0;
        var modernDisplayIdSeen = 0;
        for (var index = 1; index < count; index++)
        {
            var block = edid.Slice(checked(index * 128), 128);
            switch (block[0])
            {
                case 0x02:
                    if (!ReadCtaModes(block, checked((uint)index), modes, cta))
                        return null;
                    break;
                case 0x70:
                    // Each structure has its own Base. A legacy chain does not count
                    // the independent structure2.0 EDID sections that follow it.
                    if (!ReadDisplayIdModes(block, checked((uint)index), legacyDisplayIdCount,
                        block[1] == 0x12 ? legacyDisplayIdSeen++ : modernDisplayIdSeen++, modes, dynamicRanges,
                        adaptiveSyncRanges, ref adaptiveSyncSeen, cta))
                        return null;
                    break;
                // Block-map entries are tags, never timing declarations. Validate their references too.
                case 0xF0:
                    if (!ReadModeBlockMap(edid, index, count))
                        return null;
                    break;
                default:
                    return null;
            }
        }
        // The reference iterator visits ordinary CTA before embedded DisplayID CTA;
        // output declarations below still retain their original physical order.
        cta.Regular.AddRange(cta.EmbeddedRegular);
        if (!ResolveCtaModeReferences(modes, cta) || !ResolveCtaModeSelections(modes, cta))
            return null;
        return new(modes.OrderBy(static value => value.BlockIndex).ThenBy(static value => value.ByteOffset).ToImmutableArray(),
            ranges.OrderBy(static value => value.BlockIndex).ThenBy(static value => value.ByteOffset).ToImmutableArray())
        { EdidRevision = edid[19], BaseFeatures = edid[24], CtaFlags = cta.Flags,
            SelectionDeclarations = cta.Selections.ToImmutableArray(),
            DynamicTimingRanges = dynamicRanges.ToImmutableArray(),
            AdaptiveSyncRanges = adaptiveSyncRanges.ToImmutableArray() };
    }

    private static bool TryReadModeBlockCount(ReadOnlySpan<byte> edid, out int count)
    {
        count = checked(1 + (int)edid[126]);
        if (edid[126] == 0 || edid.Length < 256)
            return true;
        var first = edid.Slice(128, 128);
        // HF-EEODB can override the base count only at bytes 4..6 of CTA block 1.
        // The complete CTA pass also rejects a malformed, duplicate or misplaced occurrence.
        if (first[0] != 0x02 || first[1] < 3 || first[2] < 7 || first[4] != 0xE2 || first[5] != 0x78)
            return true;
        if (edid[126] != 1 || first[6] <= 1)
            return false;
        count = checked(1 + (int)first[6]);
        return true;
    }

    private static bool ReadModeBlockMap(ReadOnlySpan<byte> edid, int index, int count)
    {
        if (index is not (1 or 128))
            return false;
        var map = edid.Slice(checked(index * 128), 128);
        for (var offset = 1; offset < 127; offset++)
        {
            var target = checked(index + offset);
            if (map[offset] != (target < count ? edid[checked(target * 128)] : 0))
                return false;
        }
        return true;
    }

    private static ImmutableArray<byte> ModeBytes(ReadOnlySpan<byte> bytes)
        => ImmutableArray.Create<byte>(bytes);

    private static EdidModeRate ModeRate(ulong numerator, ulong denominator)
    {
        var a = numerator;
        var b = denominator;
        while (b != 0)
            (a, b) = (b, a % b);
        return new(numerator / a, denominator / a);
    }

    private static bool ReadDetailedMode(ReadOnlySpan<byte> descriptor, uint block, uint offset, byte revision,
        bool? native, bool? preferred, List<EdidModeDeclaration> modes)
    {
        var clock = checked((ulong)(descriptor[0] | descriptor[1] << 8) * 10_000UL);
        var h = checked((uint)(descriptor[2] | (descriptor[4] & 0xF0) << 4));
        var hb = checked((uint)(descriptor[3] | (descriptor[4] & 0x0F) << 8));
        var v = checked((uint)(descriptor[5] | (descriptor[7] & 0xF0) << 4));
        var vb = checked((uint)(descriptor[6] | (descriptor[7] & 0x0F) << 8));
        var hf = checked((uint)(descriptor[8] | (descriptor[11] & 0xC0) << 2));
        var hs = checked((uint)(descriptor[9] | (descriptor[11] & 0x30) << 4));
        var vf = checked((uint)(descriptor[10] >> 4 | (descriptor[11] & 0x0C) << 2));
        var vs = checked((uint)(descriptor[10] & 0x0F | (descriptor[11] & 0x03) << 4));
        // Border interpretation differs by EDID revision and deployed encoders; do not infer it.
        if (clock == 0 || h == 0 || v == 0 || descriptor[15] != 0 || descriptor[16] != 0
            || checked(hf + hs) > hb || checked(vf + vs) > vb)
            return false;
        var interlaced = (descriptor[17] & 0x80) != 0;
        var htotal = checked((ulong)h + hb);
        var frameLines = interlaced ? checked(2UL * (v + (ulong)vb) + 1) : checked(v + (ulong)vb);
        var frame = ModeRate(clock, checked(htotal * frameLines));
        var separate = (descriptor[17] & 0x18) == 0x18;
        var timing = new EdidDetailedTiming(clock, h, hb, hf, hs, v, vb, vf, vs, descriptor[17],
            separate ? (descriptor[17] & 2) != 0 : null, separate ? (descriptor[17] & 4) != 0 : null,
            frame, interlaced ? ModeRate(checked(clock * 2), checked(htotal * frameLines)) : null);
        modes.Add(new(block, offset, "edid-detailed", revision, ModeBytes(descriptor))
        {
            Width = h, Height = interlaced ? checked(v * 2) : v, Interlaced = interlaced,
            Native = native, Preferred = preferred, Timing = timing
        });
        return true;
    }
}
