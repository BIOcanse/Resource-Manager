// Factual bitmap layout attribution: ../../../ThirdPartyNotices/EdidModes.NOTICE.md
namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadDisplayIdBitmap(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte tag, byte flags, byte containerRevision, List<EdidModeDeclaration> modes)
    {
        var maximumLength = tag switch { 0x07 => 10, 0x08 => 8, _ => 0 };
        // Consume every supplied byte. A shorter positive bitmap needs no padding;
        // zero or excessive lengths remain outside this verified support range.
        if (flags != 0 || payload.IsEmpty || payload.Length > maximumLength)
            return false;
        var domain = tag == 0x07 ? 0 : 1;
        var bitCount = checked(payload.Length * 8);
        for (var bit = 0; bit < bitCount; bit = checked(bit + 1))
        {
            if ((payload[bit / 8] & (1 << (bit % 8))) == 0)
                continue;
            var code = checked((uint)(bit + 1));
            if (!TryDisplayIdOriginalModeCode(code, domain, out var mode))
                return false;
            modes.Add(new(block, offset, tag == 0x07 ? "displayid-dmt-bitmap" : "displayid-cta-bitmap",
                0, ModeBytes(payload))
            {
                Code = code, BitIndex = bit, Width = mode.Width, Height = mode.Height,
                NominalVerticalRateHz = mode.Rate, Interlaced = mode.Interlaced, ReducedBlanking = mode.Reduced,
                ContainerFlags = flags, ContainerRevision = containerRevision
            });
        }
        return true;
    }
}
