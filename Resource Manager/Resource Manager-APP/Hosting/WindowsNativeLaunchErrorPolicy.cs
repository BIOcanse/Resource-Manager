using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ResourceManager.App.Hosting;

[SupportedOSPlatform("windows")]
internal static class WindowsNativeLaunchErrorPolicy
{
    internal const uint RequiredMode = 0x8003;

    // Called by the process entry point before starting hosted or parallel work.
    internal static void InitializeForCurrentProcess()
    {
        var expected = GetErrorMode() | RequiredMode;
        _ = SetErrorMode(expected);
        var actual = GetErrorMode();
        if ((actual & expected) != expected)
            throw new InvalidOperationException(
                $"Native launch error policy initialization failed: expected=0x{expected:X8}, actual=0x{actual:X8}.");
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
