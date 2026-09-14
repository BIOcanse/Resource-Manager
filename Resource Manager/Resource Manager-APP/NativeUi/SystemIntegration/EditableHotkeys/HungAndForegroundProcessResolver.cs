using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;

internal sealed record ForceTerminateTargetSnapshot(
    IReadOnlyList<int> ProcessIds,
    int? ForegroundProcessId,
    int HungProcessCount);

internal static class HungAndForegroundProcessResolver
{
    private static readonly HashSet<string> CriticalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "winlogon", "services", "lsass", "smss", "fontdrvhost", "dwm"
    };

    public static int? ReadForegroundProcessId()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        return processId > 0 ? unchecked((int)processId) : null;
    }

    public static ForceTerminateTargetSnapshot Resolve(int? foregroundProcessId)
    {
        var hung = new HashSet<int>();
        _ = EnumWindows((window, ignoredParameter) =>
        {
            _ = ignoredParameter;
            if (IsWindowVisible(window) && IsHungAppWindow(window))
            {
                _ = GetWindowThreadProcessId(window, out var processId);
                if (processId > 0)
                {
                    hung.Add(unchecked((int)processId));
                }
            }

            return true;
        }, IntPtr.Zero);

        var targets = new HashSet<int>(hung);
        if (foregroundProcessId is > 0)
        {
            targets.Add(foregroundProcessId.Value);
        }

        targets.RemoveWhere(static processId => !IsAllowedTarget(processId));
        return new ForceTerminateTargetSnapshot(
            targets.Order().Take(64).ToArray(),
            foregroundProcessId,
            hung.Count(targets.Contains));
    }

    private static bool IsAllowedTarget(int processId)
    {
        if (processId <= 4 || processId == Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !CriticalProcessNames.Contains(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
