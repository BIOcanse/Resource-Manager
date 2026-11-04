namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadCtaCvtModes(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte containerRevision, List<EdidModeDeclaration> modes)
    {
        if (payload.Length < 1)
            return false;
        return ReadTypeXCvtModes(payload[1..], block, checked(offset + 1), payload[0],
            containerRevision, "cta-vtdb-x", modes);
    }

    private static bool ReadTypeXCvtModes(ReadOnlySpan<byte> descriptors, uint block, uint offset,
        byte header, byte containerRevision, string encoding, List<EdidModeDeclaration> modes)
    {
        // Both containers pass real descriptor bytes and source metadata, never a synthetic CTA block.
        // The original CTA Type X body guards and value mapping are shared without changing them.
        if (header is not (0x00 or 0x10 or 0x20))
            return false;
        var stride = 6 + (header >> 4);
        if (descriptors.Length == 0 || descriptors.Length % stride != 0)
            return false;
        for (var relative = 0; relative < descriptors.Length; relative = checked(relative + stride))
        {
            var d = descriptors.Slice(relative, stride);
            var rb = (byte)(d[0] & 7);
            var stereo = (byte)((d[0] >> 5) & 3);
            if (rb is < 1 or > 3 || stereo == 3 || rb == 1 && (d[0] & 0x10) != 0
                || rb != 3 && (d[0] & 8) != 0 || stride > 6 && rb != 3 && (d[6] & 0xFC) != 0
                || stride == 8 && (d[7] & 0xFE) != 0)
                return false;
            var parameters = new EdidCvtTimingParameters(rb, stereo)
            {
                AlternativeRefresh1000Over1001Supported = rb == 2 ? (d[0] & 0x10) != 0 : null,
                HorizontalBlankingBase160Pixels = rb == 3 ? (d[0] & 0x10) != 0 : null,
                EarlyVsyncRequired = rb == 3 ? (d[0] & 8) != 0 : null,
                DeltaHorizontalBlankingCode = rb == 3 && stride > 6 ? (byte)((d[6] >> 2) & 7) : null,
                AdditionalVerticalBlankingCode = rb == 3 && stride > 6 ? (byte)((d[6] >> 5) & 7) : null,
                AlternativeMinimumVerticalBlanking = stride == 8 ? (d[7] & 1) != 0 : null
            };
            modes.Add(new(block, checked(offset + (uint)relative), encoding, 0, ModeBytes(d))
            {
                Width = checked(1u + (uint)(d[1] | d[2] << 8)),
                Height = checked(1u + (uint)(d[3] | d[4] << 8)),
                Interlaced = false,
                NominalVerticalRateHz = checked(1u + d[5] + (stride > 6 ? (uint)(d[6] & 3) << 8 : 0u)),
                ContainerFlags = header, ContainerRevision = containerRevision,
                YCbCr420Encoding = (d[0] & 0x80) != 0,
                YCbCr420Supported = (d[0] & 0x80) != 0 ? true : null,
                Formula = "cvt", ReducedBlanking = true, CvtParameters = parameters
            });
        }
        return true;
    }
}
