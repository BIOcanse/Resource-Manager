using ResourceManager.ReadWrite.Windows.Devices.Models;
using System.Text;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal static class WindowsDeviceInterfacePathDecoder
{
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);
    internal static string? Decode(byte[] details)
    {
        if (details.Length < 6 || details.Length > 16384 || details.Length % 2 != 0) return null;
        try
        {
            var end = -1;
            for (var offset = 4; offset < details.Length; offset += 2)
                if (details[offset] == 0 && details[offset + 1] == 0) { end = offset; break; }
            if (end <= 4) return null;
            var value = StrictUnicode.GetString(details.AsSpan(4, end - 4));
            return value.Any(char.IsControl) ? null : value;
        }
        catch (DecoderFallbackException) { return null; }
    }
}
