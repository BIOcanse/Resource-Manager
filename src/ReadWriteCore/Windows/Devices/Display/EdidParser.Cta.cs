using ResourceManager.ReadWrite.Information;
using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static (byte Eotfs, IReadOnlyList<DeviceInformationHdrLuminanceDeclaration> Luminance)? ReadCtaDeclarations(ReadOnlySpan<byte> edid)
    {
        // The complete declared chain is bounded by the one-byte extension count.
        var blockCount = 1 + edid[126];
        if (edid[18] != 1 || edid[19] > 4 || edid.Length / 128 < blockCount)
            return null;
        for (var index = 0; index < blockCount; index++)
            if (!HasValidEdidBlockChecksum(edid.Slice(index * 128, 128)))
                return null;

        var result = new List<DeviceInformationHdrLuminanceDeclaration>();
        byte eotfs = 0;
        for (var index = 1; index < blockCount; index++)
        {
            var block = edid.Slice(index * 128, 128);
            if (block[0] != 0x02 || block[1] < 3)
                continue;
            var end = block[2];
            if (end == 0)
                continue;
            if (end < 4 || end > 127)
                return null;
            var offset = 4;
            while (offset < end)
            {
                var header = block[offset];
                var length = header & 0x1F;
                if (offset + 1 + length > end)
                    return null;
                var payload = block.Slice(offset + 1, length);
                if ((header >> 5) == 7)
                {
                    if (length == 0)
                        return null;
                    if (payload[0] == 0x06)
                    {
                        if (length < 3)
                            return null;
                        eotfs |= payload[1];
                        var maximum = length > 3 ? DesiredMaximumLuminance(payload[3]) : null;
                        var average = length > 4 ? DesiredMaximumLuminance(payload[4]) : null;
                        double? minimum = length > 5 && payload[5] != 0 && maximum is { } peak
                            ? peak * Math.Pow(payload[5] / 255d, 2) / 100d : null;
                        result.Add(new((uint)index, (uint)offset, maximum, average, minimum));
                    }
                }
                // Zero-length non-extended blocks still advance by their header.
                offset += 1 + length;
            }
        }
        return (eotfs, Array.AsReadOnly(result.ToArray()));
    }

    private static double? DesiredMaximumLuminance(byte code)
        => code == 0 ? null : 50d * Math.Pow(2d, code / 32d);

    private static bool HasValidEdidBlockChecksum(ReadOnlySpan<byte> block)
    {
        var checksum = 0;
        foreach (var value in block)
            checksum += value;
        return (checksum & 0xFF) == 0;
    }
}
