namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadDisplayIdFormulaModes(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte header, byte containerRevision, List<EdidModeDeclaration> modes)
    {
        const int stride = 6;
        if (header != 0 || payload.Length == 0 || payload.Length % stride != 0)
            return false;
        for (var relative = 0; relative < payload.Length; relative = checked(relative + stride))
        {
            var d = payload.Slice(relative, stride);
            var formula = (byte)(d[0] & 7);
            var stereo = (byte)((d[0] >> 5) & 3);
            // Only explicit formula codes and verified options are supported; no default CVT fallback.
            if (formula > 2 || stereo == 3 || (d[0] & 0x88) != 0)
                return false;
            modes.Add(new(block, checked(offset + (uint)relative), "displayid-ix", 0, ModeBytes(d))
            {
                Width = checked(1u + (uint)(d[1] | d[2] << 8)),
                Height = checked(1u + (uint)(d[3] | d[4] << 8)),
                Interlaced = false,
                NominalVerticalRateHz = checked(1u + d[5]),
                ContainerFlags = header,
                ContainerRevision = containerRevision,
                Formula = "cvt",
                ReducedBlanking = formula != 0,
                CvtParameters = new(formula, stereo)
                {
                    AlternativeRefresh1000Over1001Supported = (d[0] & 0x10) != 0
                }
            });
        }
        return true;
    }
}
