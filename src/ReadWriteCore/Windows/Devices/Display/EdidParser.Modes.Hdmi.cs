// Factual layout/mapping attribution: ../../../ThirdPartyNotices/EdidModes.NOTICE.md
namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadHdmiModeDeclarations(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte ctaRevision, byte ctaFlags, List<EdidModeDeclaration> modes,
        string encoding = "hdmi-vic", byte? containerRevision = null)
    {
        // Payload excludes the CTA data-block header. This batch supports only OUI 000C03.
        if (payload.Length < 5 || payload[0] != 0x03 || payload[1] != 0x0C || payload[2] != 0x00)
            return false;
        // Known AI/deep-color/DVI metadata does not add or filter mode declarations.
        // Uninterpreted flag combinations mean unsupported here, not a claim of invalid HDMI.
        if (payload.Length >= 6 && (payload[5] & 0x06) != 0)
            return false;
        if (payload.Length < 8)
            return true;
        var flags = payload[7];
        if ((flags & 0x10) != 0 || (flags & 0xC0) == 0x40)
            return false;
        var cursor = 8;
        if ((flags & 0x80) != 0)
            cursor = checked(cursor + ((flags & 0x40) != 0 ? 4 : 2));
        if (payload.Length < cursor)
            return false;
        if ((flags & 0x20) == 0)
            return payload.Length == cursor;
        if (checked(cursor + 2) > payload.Length)
            return false;
        // Image_Size is known non-mode metadata. All 3D/multi and other flags await a later batch.
        if ((payload[cursor] & 0xE7) != 0)
            return false;
        var lengths = payload[checked(cursor + 1)];
        var vicLength = lengths >> 5;
        var threeDLength = lengths & 31;
        cursor = checked(cursor + 2);
        if (threeDLength != 0 || checked(cursor + vicLength) != payload.Length)
            return false;
        for (var index = 0; index < vicLength; index++)
        {
            var at = checked(cursor + index);
            var code = payload[at];
            if (!TryHdmiModeCode(code, out var mode))
                return false;
            // The source code and source position remain HDMI declarations, not synthetic CTA SVDs.
            // No code-table clock/porch or sole exact refresh is inferred from nominal code metadata.
            modes.Add(new(block, checked(offset + (uint)at), encoding, ctaRevision, ModeBytes(payload.Slice(at, 1)))
            {
                Code = code, Width = mode.Width, Height = mode.Height, Interlaced = mode.Interlaced,
                NominalVerticalRateHz = mode.Rate, ContainerRevision = containerRevision ?? ctaRevision, ContainerFlags = ctaFlags
            });
        }
        return true;
    }
}
