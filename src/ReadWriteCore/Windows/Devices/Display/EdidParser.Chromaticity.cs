using ResourceManager.ReadWrite.Information;
using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static DeviceInformationChromaticity? ReadChromaticity(ReadOnlySpan<byte> edid)
    {
        // Parse has already checked the header and complete base block.
        // Only the reviewed 1.3/1.4 declarations use this color layout.
        if (edid[18] != 1 || edid[19] is not (3 or 4))
            return null;
        if (!HasValidEdidBlockChecksum(edid[..128]))
            return null;

        // Only digital 1.4 interprets bits 4:3 as encodings that all include RGB.
        // Analog 1.4 and both 1.3 inputs require the explicit RGB type 01.
        var rgb = (edid[19] == 4 && (edid[20] & 0x80) != 0) || ((edid[24] >> 3) & 3) == 1;
        return new(
            rgb ? Point(edid[27], edid[28], edid[25], 6, 4) : null,
            rgb ? Point(edid[29], edid[30], edid[25], 2, 0) : null,
            rgb ? Point(edid[31], edid[32], edid[26], 6, 4) : null,
            Point(edid[33], edid[34], edid[26], 2, 0));
    }

    private static DeviceInformationChromaticityPoint Point(byte xHigh, byte yHigh, byte low, int xShift, int yShift)
        => new(((xHigh << 2) | ((low >> xShift) & 3)) / 1024d,
            ((yHigh << 2) | ((low >> yShift) & 3)) / 1024d);

}
