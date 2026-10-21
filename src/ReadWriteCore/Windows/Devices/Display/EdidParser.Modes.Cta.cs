namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadCtaModes(ReadOnlySpan<byte> block, uint blockIndex, List<EdidModeDeclaration> modes,
        ModeCtaReferences references)
    {
        if (block[1] != 3 || references.Flags is { } flags && flags != block[3])
            return false;
        references.Flags = block[3];
        var end = block[2];
        if (end == 0)
            return block.Slice(4, 123).IndexOfAnyExcept((byte)0) < 0;
        if (end < 4 || end > 127)
            return false;
        var regular = new List<EdidModeDeclaration>();
        var only420 = new List<EdidModeDeclaration>();
        for (var offset = 4; offset < end;)
        {
            var header = block[offset];
            var length = header & 31;
            if (checked(offset + 1 + length) > end)
                return false;
            var payload = block.Slice(offset + 1, length);
            switch (header >> 5)
            {
                case 0:
                    if (header != 0 || block.Slice(offset, end - offset).IndexOfAnyExcept((byte)0) >= 0)
                        return false;
                    offset = end;
                    continue;
                case 2:
                    if (!ReadCtaVideoCodes(payload, blockIndex, checked((uint)(offset + 1)), block[3], false, regular,
                        "cta-vic", block[1], null))
                        return false;
                    break;
                case 3:
                    if (!ReadHdmiModeDeclarations(payload, blockIndex, checked((uint)(offset + 1)), block[1], block[3], modes))
                        return false;
                    break;
                case 7:
                    if (payload.Length == 0)
                        return false;
                    switch (payload[0])
                    {
                        case 0x08 or 0x0D:
                            if (!ReadCtaModeSelection(payload[1..], blockIndex, checked((uint)(offset + 2)),
                                block[1], payload[0] == 0x08, references.Selections))
                                return false;
                            break;
                        case 0x0E:
                            if (!ReadCtaVideoCodes(payload[1..], blockIndex, checked((uint)(offset + 2)), block[3], true, only420,
                                "cta-y420-vic", block[1], null))
                                return false;
                            break;
                        case 0x0F:
                            if (references.Map is not null)
                                return false;
                            references.Map = payload[1..].ToArray();
                            references.MapLocation = new(blockIndex, checked((uint)offset),
                                ModeBytes(block.Slice(offset, checked(length + 1))), null);
                            break;
                        case 0x22:
                            if (!ReadCtaTypeVII(payload[1..], blockIndex, checked((uint)(offset + 2)), block[1], modes))
                                return false;
                            break;
                        case 0x23:
                            if (!ReadCtaCodeTimings(payload[1..], blockIndex, checked((uint)(offset + 2)), block[1], modes))
                                return false;
                            break;
                        case 0x2A:
                            if (!ReadCtaCvtModes(payload[1..], blockIndex, checked((uint)(offset + 2)), block[1], modes))
                                return false;
                            break;
                        case 0x78:
                            // This one absolute location also guarantees there cannot be two occurrences.
                            // The prefix pass already used this byte to validate the entire EDID length.
                            if (blockIndex != 1 || offset != 4 || payload.Length != 2 || payload[1] <= 1)
                                return false;
                            break;
                        default:
                            if (!IsNonModeCtaDataBlock(7, payload))
                                return false;
                            break;
                    }
                    break;
                // Tag6 is VFDB, not harmless metadata; other unrecognized blocks remain unknown.
                default:
                    if (!IsNonModeCtaDataBlock((byte)(header >> 5), payload))
                        return false;
                    break;
            }
            offset = checked(offset + 1 + length);
        }
        for (var index = 0; index < regular.Count; index++)
            references.Regular.Add(checked(modes.Count + index));
        modes.AddRange(regular);
        for (var index = 0; index < only420.Count; index++)
            references.Only420.Add(checked(modes.Count + index));
        modes.AddRange(only420);
        var detailedOffset = (int)end;
        while (checked(detailedOffset + 18) <= 127)
        {
            var descriptor = block.Slice(detailedOffset, 18);
            if (descriptor.IndexOfAnyExcept((byte)0) < 0)
                break;
            if ((descriptor[0] | descriptor[1]) == 0 || !ReadDetailedMode(descriptor, blockIndex,
                checked((uint)detailedOffset), block[1], null, null, modes))
                return false;
            detailedOffset = checked(detailedOffset + 18);
        }
        return block.Slice(detailedOffset, 127 - detailedOffset).IndexOfAnyExcept((byte)0) < 0;
    }

    private static bool IsNonModeCtaDataBlock(byte tag, ReadOnlySpan<byte> payload)
        => tag is 1 or 4 or 5 || tag == 7 && !payload.IsEmpty
            && payload[0] is 0x00 or 0x05 or 0x06 or 0x07 or 0x11 or 0x12 or 0x13 or 0x14 or 0x20 or 0x21;

    private static bool ResolveCtaModeReferences(List<EdidModeDeclaration> modes, ModeCtaReferences references)
    {
        if (references.Flags is { } flags)
        {
            // CTA's DTD ordinal includes base-block DTDs and DTDs from all CTA extensions.
            var detailed = 0;
            for (var index = 0; index < modes.Count; index++)
                if (modes[index].Encoding == "edid-detailed")
                    modes[index] = modes[index] with { Native = detailed++ < (flags & 15) };
            if (detailed < (flags & 15))
                return false;
        }
        if (references.Only420.Count != 0)
        {
            var codes = references.Regular.Select(index => modes[index].Code).ToHashSet();
            if (references.Only420.Any(index => codes.Contains(modes[index].Code)))
                return false;
        }
        if (references.Map is not { } map)
            return true;
        // CMDB indexes all VDB SVDs across this EDID, not the block containing the map.
        for (var bit = references.Regular.Count; bit < checked(map.Length * 8); bit++)
            if ((map[bit / 8] & 1 << (bit % 8)) != 0)
                return false;
        for (var ordinal = 0; ordinal < references.Regular.Count; ordinal++)
        {
            var index = references.Regular[ordinal];
            modes[index] = modes[index] with
            {
                YCbCr420Supported = map.Length == 0 || ordinal / 8 < map.Length && (map[ordinal / 8] & 1 << (ordinal % 8)) != 0,
                YCbCr420CapabilityMap = references.MapLocation! with { BitIndex = map.Length == 0 ? null : checked(16 + ordinal) }
            };
        }
        return true;
    }

    // Only operation-local indexes into the pending result; no state survives this parse.
    private sealed class ModeCtaReferences
    {
        internal byte? Flags;
        internal byte[]? Map;
        internal EdidModeReference? MapLocation;
        internal List<int> Regular { get; } = [];
        internal List<int> EmbeddedRegular { get; } = [];
        internal List<int> Only420 { get; } = [];
        internal List<EdidModeSelectionDeclaration> Selections { get; } = [];
    }

    private static bool ReadCtaVideoCodes(ReadOnlySpan<byte> codes, uint block, uint offset, byte containerFlags,
        bool only420, List<EdidModeDeclaration> modes, string encoding, byte revision, byte? containerRevision)
    {
        for (var index = 0; index < codes.Length; index++)
        {
            var raw = codes[index];
            if (raw is 0 or 128)
                return false;
            var vic = only420 || ((raw - 1) & 0x40) != 0 ? raw : raw & 0x7F;
            if (!TryCtaModeCode(vic, out var mode))
                return false;
            modes.Add(new(block, checked(offset + (uint)index), encoding, revision,
                ModeBytes(codes.Slice(index, 1)))
            {
                Code = checked((uint)vic), Width = mode.Width, Height = mode.Height,
                HorizontalPixelRepetition = vic is 6 or 7 or 8 or 9 or 21 or 22 or 23 or 24 or 44 or 45
                    or 50 or 51 or 54 or 55 or 58 or 59 ? 2u : null,
                NominalVerticalRateHz = mode.Rate, Interlaced = mode.Interlaced,
                Native = only420 ? null : vic <= 64 && (raw & 0x80) != 0,
                YCbCr420Only = only420, YCbCr420Supported = only420 ? true : null, ContainerFlags = containerFlags,
                ContainerRevision = containerRevision
            });
        }
        return true;
    }
}
