// Factual original code-domain mapping: ../../../ThirdPartyNotices/EdidModes.NOTICE.md
namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool TryHdmiModeCode(uint code, out (uint Width, uint Height, uint Rate, bool Interlaced) mode)
        => TryCtaModeCode(code switch { 1 => 95, 2 => 94, 3 => 93, 4 => 98, _ => 0 }, out mode);

    private static bool TryDisplayIdOriginalModeCode(uint code, int domain,
        out (uint Width, uint Height, uint? Rate, bool Interlaced, bool? Reduced) mode)
    {
        mode = default;
        if (domain == 0 && TryDmtModeCode(code, out var dmt))
        {
            mode = (dmt.Width, dmt.Height, dmt.Rate, dmt.Interlaced, dmt.Reduced);
            return true;
        }
        // Validate the full wire ID before narrowing; 0x0101 must never alias code1.
        (uint Width, uint Height, uint Rate, bool Interlaced) fact;
        if (domain == 1 && code <= int.MaxValue && TryCtaModeCode((int)code, out fact)
            || domain == 2 && TryHdmiModeCode(code, out fact))
        {
            mode = (fact.Width, fact.Height, fact.Rate, fact.Interlaced, null);
            return true;
        }
        return false;
    }
}
