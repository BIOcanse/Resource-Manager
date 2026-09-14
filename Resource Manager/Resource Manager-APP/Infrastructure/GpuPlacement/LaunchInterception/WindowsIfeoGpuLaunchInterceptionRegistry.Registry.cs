using Microsoft.Win32;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsIfeoGpuLaunchInterceptionRegistry
{
    private ViewInspection InspectView(
        RegistryView view,
        string imageName,
        string executablePath,
        string ruleName,
        string debuggerCommand)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var imageKey = baseKey.OpenSubKey($@"{IfeoRoot}\{imageName}", writable: false);
        if (imageKey is null)
        {
            return new ViewInspection(false, null);
        }

        var directDebugger = imageKey.GetValue(DebuggerValueName) as string;
        if (!string.IsNullOrWhiteSpace(directDebugger))
        {
            return new ViewInspection(
                false,
                $"{ViewName(view)} 中该映像已有直接 IFEO Debugger，Resource Manager 不会覆盖。");
        }

        var useFilterValue = imageKey.GetValue(UseFilterValueName);
        var ownsUseFilter = OwnerValue.Equals(
            imageKey.GetValue(ParentOwnerValueName) as string,
            StringComparison.Ordinal);
        if (useFilterValue is not null
            && (useFilterValue is not int configuredUseFilter || configuredUseFilter != 1)
            && !ownsUseFilter)
        {
            return new ViewInspection(
                false,
                $"{ViewName(view)} 中已有非 Resource Manager 管理的 UseFilter 状态，拒绝改写。");
        }

        foreach (var subKeyName in imageKey.GetSubKeyNames())
        {
            using var subKey = imageKey.OpenSubKey(subKeyName, writable: false);
            if (subKey is null)
            {
                continue;
            }

            var filterPath = NormalizePath(subKey.GetValue(FilterFullPathValueName) as string);
            var owner = subKey.GetValue(OwnerValueName) as string;
            var debugger = subKey.GetValue(DebuggerValueName) as string;
            var ownsRule = OwnerValue.Equals(owner, StringComparison.Ordinal);

            if (subKeyName.Equals(ruleName, StringComparison.OrdinalIgnoreCase) && !ownsRule)
            {
                return new ViewInspection(
                    false,
                    $"{ViewName(view)} 中目标 IFEO 子键已由其他软件占用。");
            }

            if (!string.Equals(filterPath, executablePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ownsRule)
            {
                return new ViewInspection(
                    false,
                    $"{ViewName(view)} 中该完整路径已有第三方 IFEO 规则，Resource Manager 不会覆盖或串联。");
            }

            var registered = subKeyName.Equals(ruleName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(debugger, debuggerCommand, StringComparison.OrdinalIgnoreCase)
                && imageKey.GetValue(UseFilterValueName) is int registeredUseFilter
                && registeredUseFilter == 1;
            return new ViewInspection(registered, null);
        }

        return new ViewInspection(false, null);
    }

    private static void WriteRule(
        RegistryView view,
        string imageName,
        string executablePath,
        string ruleName,
        string debuggerCommand)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var imageKey = baseKey.CreateSubKey($@"{IfeoRoot}\{imageName}", writable: true)
            ?? throw new IOException($"无法打开 {ViewName(view)} IFEO 映像键。");

        if (imageKey.GetValue(UseFilterValueName) is null)
        {
            imageKey.SetValue(ParentOwnerValueName, OwnerValue, RegistryValueKind.String);
        }
        imageKey.SetValue(UseFilterValueName, 1, RegistryValueKind.DWord);

        using var ruleKey = imageKey.CreateSubKey(ruleName, writable: true)
            ?? throw new IOException($"无法创建 {ViewName(view)} IFEO 完整路径规则。");
        ruleKey.SetValue(OwnerValueName, OwnerValue, RegistryValueKind.String);
        ruleKey.SetValue(RuleIdValueName, ruleName, RegistryValueKind.String);
        ruleKey.SetValue(FilterFullPathValueName, executablePath, RegistryValueKind.String);
        ruleKey.SetValue(DebuggerValueName, debuggerCommand, RegistryValueKind.String);
    }

    private void RemoveOwnedRulesForPath(string? path)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        foreach (var view in RegistryViews)
        {
            RemoveOwnedRulesForPath(view, normalizedPath);
        }
    }

    private static void RemoveOwnedRulesForPath(RegistryView view, string executablePath)
    {
        var imageName = Path.GetFileName(executablePath);
        var expectedRuleName = CreateRuleName(executablePath);
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var imageKey = baseKey.OpenSubKey($@"{IfeoRoot}\{imageName}", writable: true);
        if (imageKey is null)
        {
            return;
        }

        foreach (var subKeyName in imageKey.GetSubKeyNames())
        {
            using var subKey = imageKey.OpenSubKey(subKeyName, writable: false);
            if (subKey is null
                || !OwnerValue.Equals(subKey.GetValue(OwnerValueName) as string, StringComparison.Ordinal)
                || (!subKeyName.Equals(expectedRuleName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(
                        NormalizePath(subKey.GetValue(FilterFullPathValueName) as string),
                        executablePath,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            subKey.Close();
            imageKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
        }

        CleanupParentUseFilter(imageKey);
    }

    private static int RemoveAllOwnedRules(RegistryView view)
    {
        var removed = 0;
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var root = baseKey.OpenSubKey(IfeoRoot, writable: true);
        if (root is null)
        {
            return 0;
        }

        foreach (var imageName in root.GetSubKeyNames())
        {
            using var imageKey = root.OpenSubKey(imageName, writable: true);
            if (imageKey is null)
            {
                continue;
            }

            foreach (var subKeyName in imageKey.GetSubKeyNames())
            {
                using var subKey = imageKey.OpenSubKey(subKeyName, writable: false);
                if (!OwnerValue.Equals(subKey?.GetValue(OwnerValueName) as string, StringComparison.Ordinal))
                {
                    continue;
                }

                subKey?.Close();
                imageKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                removed++;
            }

            CleanupParentUseFilter(imageKey);
        }

        return removed;
    }

    private static void CleanupParentUseFilter(RegistryKey imageKey)
    {
        var hasFullPathRules = imageKey.GetSubKeyNames().Any(subKeyName =>
        {
            using var subKey = imageKey.OpenSubKey(subKeyName, writable: false);
            return !string.IsNullOrWhiteSpace(subKey?.GetValue(FilterFullPathValueName) as string);
        });

        var ownsUseFilter = OwnerValue.Equals(
            imageKey.GetValue(ParentOwnerValueName) as string,
            StringComparison.Ordinal);
        if (!hasFullPathRules && ownsUseFilter)
        {
            imageKey.DeleteValue(UseFilterValueName, throwOnMissingValue: false);
        }
        if (ownsUseFilter)
        {
            imageKey.DeleteValue(ParentOwnerValueName, throwOnMissingValue: false);
        }
    }

    private static void RemoveStaleOwnedRules(IReadOnlySet<string> desiredPaths)
    {
        foreach (var view in RegistryViews)
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = baseKey.OpenSubKey(IfeoRoot, writable: true);
            if (root is null)
            {
                continue;
            }

            foreach (var imageName in root.GetSubKeyNames())
            {
                using var imageKey = root.OpenSubKey(imageName, writable: true);
                if (imageKey is null)
                {
                    continue;
                }

                foreach (var subKeyName in imageKey.GetSubKeyNames())
                {
                    using var subKey = imageKey.OpenSubKey(subKeyName, writable: false);
                    if (!OwnerValue.Equals(subKey?.GetValue(OwnerValueName) as string, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var filterPath = NormalizePath(subKey?.GetValue(FilterFullPathValueName) as string);
                    if (!string.IsNullOrWhiteSpace(filterPath) && desiredPaths.Contains(filterPath))
                    {
                        continue;
                    }

                    subKey?.Close();
                    imageKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                }

                CleanupParentUseFilter(imageKey);
            }
        }
    }

    private static string ViewName(RegistryView view)
    {
        return view == RegistryView.Registry64 ? "Registry64" : "Registry32";
    }
}
