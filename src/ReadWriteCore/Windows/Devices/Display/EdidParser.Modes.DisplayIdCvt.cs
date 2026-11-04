namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadDisplayIdCvtModes(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte header, byte containerRevision, List<EdidModeDeclaration> modes)
        // This encoding deliberately stays outside the original CTA T7/T10 ordinal domain.
        => ReadTypeXCvtModes(payload, block, offset, header, containerRevision, "displayid-x", modes);
}
