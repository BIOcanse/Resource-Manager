using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;

internal sealed record ForceTerminateTargetSnapshot(
    IReadOnlyList<ForceTerminateProcessTarget> Targets,
    int? ForegroundProcessId,
    int HungProcessCount);

internal sealed record ForceTerminateProcessTarget(int ProcessId, string ProcessStartKey);

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

        var identities = targets.Order().Take(64)
            .Select(ReadAllowedTarget).OfType<ForceTerminateProcessTarget>().ToArray();
        return new ForceTerminateTargetSnapshot(
            identities,
            foregroundProcessId,
            identities.Count(target => hung.Contains(target.ProcessId)));
    }

    internal static ForceTerminateProcessTarget? ReadAllowedTarget(int processId)
    {
        if (processId <= 4 || processId == Environment.ProcessId)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return CriticalProcessNames.Contains(process.ProcessName) ? null
                : new ForceTerminateProcessTarget(processId, process.StartTime.ToFileTimeUtc()
                    .ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch
        {
            return null;
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
