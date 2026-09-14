using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.ProcessIdentity;

public sealed class WindowsSystemProcessClassifier : IRuntimeSystemProcessClassifier
{
    private static readonly string WindowsRoot = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\Windows";

    private static readonly RuntimeSoftwareAttribution WindowsSystem = new(
        RuntimeAttributionIds.WindowsSystem,
        "Windows 系统",
        SoftwareKinds.WindowsSystem,
        "Windows 系统",
        [WindowsRoot]);

    private static readonly RuntimeSoftwareAttribution WindowsComponent = new(
        RuntimeAttributionIds.WindowsComponent,
        "Windows 应用/组件",
        SoftwareKinds.WindowsComponent,
        "Windows 应用/组件",
        [Path.Combine(WindowsRoot, "SystemApps")]);

    private static readonly HashSet<string> CoreSystemProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle",
        "System",
        "Registry",
        "Secure System",
        "smss",
        "csrss",
        "wininit",
        "winlogon",
        "services",
        "lsass",
        "svchost",
        "fontdrvhost",
        "dwm",
        "Memory Compression",
        "WmiPrvSE",
        "audiodg",
        "dasHost",
        "GameInputSvc",
        "LsaIso",
        "NgcIso",
        "unsecapp",
        "wlanext",
        "WUDFHost"
    };

    private static readonly HashSet<string> WindowsComponentProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost",
        "RuntimeBroker",
        "SearchHost",
        "SearchIndexer",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "TextInputHost",
        "ctfmon",
        "SecurityHealthSystray",
        "WidgetService",
        "Widgets",
        "PhoneExperienceHost"
    };

    public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
    {
        if (process.ProcessId <= 4 || CoreSystemProcessNames.Contains(process.Name))
        {
            return RuntimeAttributionObservation.Matched(WindowsSystem);
        }

        var processPath = NormalizePath(process.ExecutablePath);
        if (processPath is null)
        {
            return WindowsComponentProcessNames.Contains(process.Name)
                ? RuntimeAttributionObservation.Matched(WindowsComponent)
                : RuntimeAttributionObservation.NoMatch;
        }

        if (IsSameOrUnder(processPath, Path.Combine(WindowsRoot, "SystemApps"))
            || WindowsComponentProcessNames.Contains(process.Name))
        {
            return RuntimeAttributionObservation.Matched(WindowsComponent);
        }

        if (IsSameOrUnder(processPath, WindowsRoot))
        {
            return RuntimeAttributionObservation.Matched(WindowsSystem);
        }

        return RuntimeAttributionObservation.NoMatch;
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsSameOrUnder(string candidate, string root)
    {
        var normalizedRoot = NormalizePath(root);
        return normalizedRoot is not null
            && (candidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}
