// Factual layout attribution: ../../../ThirdPartyNotices/EdidModes.NOTICE.md
namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadCtaCodeTimings(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte containerRevision, List<EdidModeDeclaration> modes)
    {
        if (payload.Length < 2 || payload[0] is not (0x01 or 0x09 or 0x21 or 0x29))
            return false;
        var flags = payload[0];
        var stride = (flags & 8) == 0 ? 1 : 2;
        var codes = payload[1..];
        if (codes.Length % stride != 0)
            return false;
        for (var index = 0; index < codes.Length; index = checked(index + stride))
        {
            uint code = codes[index];
            if (stride == 2)
                code |= (uint)codes[index + 1] << 8;
            // The complete wire ID is the lookup key; 0x0104 must not alias DMT 0x04.
            if (!TryDmtModeCode(code, out var mode))
                return false;
            modes.Add(new(block, checked(offset + 1 + (uint)index), "cta-dmt-code", 1,
                ModeBytes(codes.Slice(index, stride)))
            {
                Code = code, Width = mode.Width, Height = mode.Height,
                NominalVerticalRateHz = mode.Rate, Interlaced = mode.Interlaced, ReducedBlanking = mode.Reduced,
                ContainerFlags = flags, ContainerRevision = containerRevision,
                YCbCr420Supported = (flags & 0x20) != 0 ? true : null
            });
        }
        return true;
    }
}
