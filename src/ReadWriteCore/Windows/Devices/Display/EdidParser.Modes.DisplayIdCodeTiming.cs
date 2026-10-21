// Factual layout attribution: ../../../ThirdPartyNotices/EdidModes.NOTICE.md
namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadDisplayIdCodeTimings(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte flags, byte containerRevision, List<EdidModeDeclaration> modes)
    {
        // DisplayID VIII revision0 original code domains; CTA's native/also420 flags do not apply.
        // Other headers and an empty list remain outside this verified support range.
        if (flags is not (0x00 or 0x08 or 0x40 or 0x48 or 0x80 or 0x88) || payload.IsEmpty)
            return false;
        var stride = (flags & 8) == 0 ? 1 : 2;
        if (payload.Length % stride != 0)
            return false;
        for (var index = 0; index < payload.Length; index = checked(index + stride))
        {
            uint code = payload[index];
            if (stride == 2)
                code |= (uint)payload[index + 1] << 8;
            if (!TryDisplayIdOriginalModeCode(code, flags >> 6, out var mode))
                return false;
            modes.Add(new(block, checked(offset + (uint)index), (flags >> 6) switch { 0 => "displayid-dmt-code", 1 => "displayid-cta-vic", _ => "displayid-hdmi-vic" }, 0,
                ModeBytes(payload.Slice(index, stride)))
            {
                Code = code, Width = mode.Width, Height = mode.Height,
                NominalVerticalRateHz = mode.Rate, Interlaced = mode.Interlaced, ReducedBlanking = mode.Reduced,
                ContainerFlags = flags, ContainerRevision = containerRevision
            });
        }
        return true;
    }
}
