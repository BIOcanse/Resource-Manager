namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadDisplayIdAdaptiveSyncRanges(ReadOnlySpan<byte> payload, uint block,
        uint offset, byte flags, byte containerRevision, List<EdidAdaptiveSyncRangeDeclaration> ranges)
    {
        // DisplayID2.1/2.1a section4.9, tables4-51/4-52. Both use structure0x20.
        // No mode-dependent actual-Hz calculation or CVT timing generation.
        if (flags != 0 || payload.Length % 6 != 0)
            return false;
        for (var relative = 0; relative < payload.Length; relative += 6)
        {
            var d = payload.Slice(relative, 6);
            var operation = (d[0] >> 2) & 3;
            if ((d[0] & 0xC0) != 0 || operation > 1 || (d[4] & 0xFC) != 0)
                return false;
            var minimum = (uint)d[2];
            var maximum = checked((uint)(d[3] | (d[4] & 3) << 8) + 1);
            if (minimum > maximum)
                return false;
            ranges.Add(new(block, checked(offset + (uint)relative), 0, flags, containerRevision,
                ModeBytes(d), minimum, maximum, (d[0] & 1) != 0, operation == 1, (d[0] & 16) == 0,
                new((d[0] & 2) != 0, d[1] == 0 ? null : checked((uint)d[1] * 250)),
                new((d[0] & 32) != 0, d[5] == 0 ? null : checked((uint)d[5] * 250))));
        }
        return true;
    }
}
