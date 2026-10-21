namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadDisplayIdDynamicRange(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte flags, byte containerRevision, List<EdidDynamicTimingRangeDeclaration> ranges)
    {
        // DisplayID2.0 section4.4/table4-22, including adopted E9 revision1.
        if (payload.Length != 9 || flags > 1)
            return false;
        var reservedMask = flags == 0 ? 0x7F : 0x7C;
        if ((payload[8] & reservedMask) != 0)
            return false;
        var minimumClock = checked(((ulong)(payload[0] | payload[1] << 8 | payload[2] << 16) + 1) * 1000);
        var maximumClock = checked(((ulong)(payload[3] | payload[4] << 8 | payload[5] << 16) + 1) * 1000);
        var minimumRate = (uint)payload[6];
        var maximumRate = (uint)(payload[7] | (flags == 1 ? (payload[8] & 3) << 8 : 0));
        if (minimumClock > maximumClock || minimumRate > maximumRate)
            return false;
        ranges.Add(new(block, offset, flags, flags, containerRevision, ModeBytes(payload),
            minimumClock, maximumClock, minimumRate, maximumRate, (payload[8] & 128) != 0));
        return true;
    }
}
