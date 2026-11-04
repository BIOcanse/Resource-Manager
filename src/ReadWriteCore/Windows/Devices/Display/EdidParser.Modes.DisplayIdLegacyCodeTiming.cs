// Factual layout attribution: ../../../ThirdPartyNotices/EdidModes.NOTICE.md
namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadDisplayIdLegacyCodeTimings(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte flags, byte containerRevision, List<EdidModeDeclaration> modes)
    {
        // Type IV revision1 uses byte IDs in each verified original code domain; no width bit.
        if (flags is not (0x01 or 0x41 or 0x81) || payload.IsEmpty)
            return false;
        for (var index = 0; index < payload.Length; index = checked(index + 1))
        {
            uint code = payload[index];
            if (!TryDisplayIdOriginalModeCode(code, flags >> 6, out var mode))
                return false;
            modes.Add(new(block, checked(offset + (uint)index), (flags >> 6) switch { 0 => "displayid-iv-dmt-code", 1 => "displayid-iv-cta-vic", _ => "displayid-iv-hdmi-vic" }, 1,
                ModeBytes(payload.Slice(index, 1)))
            {
                Code = code, Width = mode.Width, Height = mode.Height,
                NominalVerticalRateHz = mode.Rate, Interlaced = mode.Interlaced, ReducedBlanking = mode.Reduced,
                ContainerFlags = flags, ContainerRevision = containerRevision
            });
        }
        return true;
    }
}
