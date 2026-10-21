namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadDisplayIdCtaModes(ReadOnlySpan<byte> payload, uint block, uint payloadOffset,
        byte flags, byte containerRevision, List<EdidModeDeclaration> modes, ModeCtaReferences references)
    {
        if (flags != 0 || payload.IsEmpty)
            return false;
        // This wrapper carries no CTA extension header. Supported video and colour declarations keep
        // the real DisplayID header; other mode/reference formats remain unknown.
        for (var offset = 0; offset < payload.Length;)
        {
            var header = payload[offset++];
            var length = header & 31;
            if (length > payload.Length - offset)
                return false;
            var data = payload.Slice(offset, length);
            var tag = header >> 5;
            var only420 = tag == 7 && !data.IsEmpty && data[0] == 0x0E;
            if (tag == 2 || only420)
            {
                var first = modes.Count;
                var codes = only420 ? data[1..] : data;
                if (codes.IsEmpty || !ReadCtaVideoCodes(codes, block,
                    checked(payloadOffset + (uint)offset + (only420 ? 1u : 0u)), flags, only420, modes,
                    only420 ? "displayid-cta-y420-vic" : "displayid-cta-svd", (byte)(flags & 7), containerRevision))
                    return false;
                var entries = only420 ? references.Only420 : references.EmbeddedRegular;
                for (var index = first; index < modes.Count; index++)
                    entries.Add(index);
            }
            else if (tag == 3)
            {
                if (!ReadHdmiModeDeclarations(data, block, checked(payloadOffset + (uint)offset),
                    (byte)(flags & 7), flags, modes, "displayid-cta-hdmi-vic", containerRevision))
                    return false;
            }
            else if (tag == 7 && !data.IsEmpty && data[0] == 0x0F)
            {
                if (references.Map is not null)
                    return false;
                references.Map = data[1..].ToArray();
                references.MapLocation = new(block, checked(payloadOffset + (uint)offset - 1),
                    ModeBytes(payload.Slice(offset - 1, checked(length + 1))), null);
            }
            else if (!IsNonModeCtaDataBlock((byte)(header >> 5), data))
                return false;
            offset = checked(offset + length);
        }
        return true;
    }
}
