namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadCtaTypeVII(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte containerRevision, List<EdidModeDeclaration> modes)
    {
        // Only the verified revision-2 header and exact 20-byte timing are supported.
        // Other flags/strides/tails remain unknown, rather than publishing a prefix.
        if (payload.Length != 21 || payload[0] != 0x02)
            return false;
        if (!ReadDisplayIdDetailed(payload[1..], block, checked(offset + 1), 2,
            payload[0], containerRevision, true, modes))
            return false;
        // The timing body is shared; its surrounding CTA block is not DisplayID.
        modes[^1] = modes[^1] with { Encoding = "cta-vtdb-vii", DscPassThrough = null };
        return true;
    }
}
