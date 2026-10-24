using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ResourceManager.App.Hosting;

[SupportedOSPlatform("windows")]
internal static class WindowsNativeLaunchErrorPolicy
{
    internal const uint RequiredMode = 0x8003;
    private static readonly object Sync = new();

    internal static (uint Before, uint After) EstablishForLaunch()
    {
        lock (Sync)
        {
            var before = GetErrorMode();
            _ = SetErrorMode(before | RequiredMode);
            return (before, GetErrorMode());
        }
    }

    // Called by the process entry point before starting hosted or parallel work.
    internal static void InitializeForCurrentProcess()
    {
        var mode = EstablishForLaunch();
        var expected = mode.Before | RequiredMode;
        if ((mode.After & expected) != expected)
            throw new InvalidOperationException(
                $"Native launch error policy initialization failed: expected=0x{expected:X8}, actual=0x{mode.After:X8}.");
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
