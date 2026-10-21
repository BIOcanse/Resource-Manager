namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadDisplayIdShortTimings(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte flags, byte containerRevision, List<EdidModeDeclaration> modes)
    {
        // Legacy Type III: fixed three-byte declarations, block revision0/1 only.
        // No pixel height, CVT clock, or detailed timing is synthesized.
        if (flags > 1 || payload.Length == 0 || payload.Length % 3 != 0)
            return false;
        for (var relative = 0; relative < payload.Length; relative = checked(relative + 3))
        {
            var d = payload.Slice(relative, 3);
            var algorithm = (d[0] >> 4) & 7;
            var aspectCode = (byte)(d[0] & 15);
            if (algorithm > 1 || aspectCode > 8)
                return false;
            var aspect = aspectCode switch
            {
                0 => new EdidAspectRatioDeclaration(0, 1, 1),
                1 => new EdidAspectRatioDeclaration(1, 5, 4),
                2 => new EdidAspectRatioDeclaration(2, 4, 3),
                3 => new EdidAspectRatioDeclaration(3, 15, 9),
                4 => new EdidAspectRatioDeclaration(4, 16, 9),
                5 => new EdidAspectRatioDeclaration(5, 16, 10),
                6 => new EdidAspectRatioDeclaration(6, 64, 27),
                7 => new EdidAspectRatioDeclaration(7, 256, 135),
                _ => new EdidAspectRatioDeclaration(8, null, null)
            };
            modes.Add(new(block, checked(offset + (uint)relative), "displayid-iii", flags, ModeBytes(d))
            {
                ContainerFlags = flags, ContainerRevision = containerRevision,
                Width = checked(((uint)d[1] + 1) * 8), AspectRatioDeclared = aspect,
                Interlaced = (d[2] & 128) != 0, NominalVerticalRateHz = checked((uint)(d[2] & 127) + 1),
                Preferred = (d[0] & 128) != 0, Formula = "cvt", ReducedBlanking = algorithm == 1
            });
        }
        return true;
    }
}
