namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadDisplayIdCompactTimings(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte flags, byte containerRevision, List<EdidModeDeclaration> modes)
    {
        // DisplayID1.3 tables4-19/4-20 and adopted editorial corrections:
        // fixed11B descriptors, block revision0, no additional header flags.
        if (flags != 0 || payload.Length == 0 || payload.Length % 11 != 0)
            return false;
        for (var relative = 0; relative < payload.Length; relative = checked(relative + 11))
        {
            var d = payload.Slice(relative, 11);
            if ((d[3] & 3) != 0 || (d[3] & 0x60) == 0x60 || (d[8] & 0xF0) != 0)
                return false;
            var clock = checked(((ulong)(d[0] | d[1] << 8 | d[2] << 16) + 1) * 10_000);
            var h = checked(((uint)(d[4] | (d[5] & 1) << 8) + 1) * 8);
            var hb = checked(((uint)(d[5] >> 1) + 1) * 8);
            var hf = checked(((uint)(d[6] >> 4) + 1) * 8);
            var hs = checked(((uint)(d[6] & 15) + 1) * 8);
            var v = checked((uint)(d[7] | d[8] << 8) + 1);
            var vb = checked((uint)d[9] + 1);
            var vf = checked((uint)(d[10] >> 4) + 1);
            var vs = checked((uint)(d[10] & 15) + 1);
            var interlaced = (d[3] & 16) != 0;
            var lines = checked((ulong)v + vb);
            if (checked(hf + hs) > hb || checked(vf + vs) > vb || interlaced && (lines & 1) == 0)
                return false;
            var denominator = checked(((ulong)h + hb) * lines);
            modes.Add(new(block, checked(offset + (uint)relative), "displayid-ii", 0, ModeBytes(d))
            {
                Width = h, Height = v, Interlaced = interlaced, Preferred = (d[3] & 128) != 0,
                ContainerFlags = flags, ContainerRevision = containerRevision,
                Timing = new(clock, h, hb, hf, hs, v, vb, vf, vs, d[3], (d[3] & 8) != 0, (d[3] & 4) != 0,
                    ModeRate(clock, denominator), interlaced ? ModeRate(checked(clock * 2), denominator) : null)
            });
        }
        return true;
    }
}
