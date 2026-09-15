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

        ownedRuleCleanup.RemoveForPath(RegistryViews, normalizedPath);
    }

    private void RemoveStaleOwnedRules(IReadOnlySet<string> desiredPaths)
    {
        ownedRuleCleanup.RemoveStale(RegistryViews, desiredPaths);
    }

    private sealed class WindowsIfeoOwnedRuleStore : IIfeoOwnedRuleStore
    {
        public IReadOnlyList<string> EnumerateImageNames(RegistryView view)
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = baseKey.OpenSubKey(IfeoRoot, writable: false);
            return root?.GetSubKeyNames() ?? [];
        }

        public IfeoImageSnapshot? ReadImage(RegistryView view, string imageName)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var imageKey = baseKey.OpenSubKey($@"{IfeoRoot}\{imageName}", writable: false);
                return imageKey is null ? null : SnapshotImage(imageKey, imageName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                or System.Security.SecurityException
                or IOException)
            {
                return null;
            }
        }

        public int DeleteOwnedRules(
            RegistryView view,
            string imageName,
            IReadOnlyList<IfeoOwnedRuleCandidate> candidates,
            bool settleParentOwnership)
        {
            if (candidates.Count == 0 && !settleParentOwnership)
            {
                return 0;
            }

            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var imageKey = baseKey.OpenSubKey($@"{IfeoRoot}\{imageName}", writable: true);
            if (imageKey is null)
            {
                return 0;
            }

            var removed = 0;
            foreach (var candidate in candidates)
            {
                IfeoRuleSnapshot? current;
                try
                {
                    using var ruleKey = imageKey.OpenSubKey(candidate.RuleName, writable: false);
                    current = ruleKey is null ? null : SnapshotRule(ruleKey, candidate.RuleName);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException
                    or System.Security.SecurityException
                    or IOException)
                {
                    continue;
                }

                if (current is null
                    || !IfeoOwnedRuleCleanupCoordinator.MatchesCandidate(current, candidate, OwnerValue))
                {
                    continue;
                }

                imageKey.DeleteSubKeyTree(candidate.RuleName, throwOnMissingSubKey: false);
                removed++;
            }

            if (settleParentOwnership)
            {
                CleanupParentUseFilter(imageKey, imageName);
            }

            return removed;
        }

        private static IfeoImageSnapshot SnapshotImage(RegistryKey imageKey, string imageName)
        {
            var complete = true;
            var rules = new List<IfeoRuleSnapshot>();
            foreach (var ruleName in imageKey.GetSubKeyNames())
            {
                try
                {
                    using var ruleKey = imageKey.OpenSubKey(ruleName, writable: false);
                    if (ruleKey is null)
                    {
                        complete = false;
                        continue;
                    }

                    rules.Add(SnapshotRule(ruleKey, ruleName));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException
                    or System.Security.SecurityException
                    or IOException)
                {
                    complete = false;
                }
            }

            return new IfeoImageSnapshot(
                imageName,
                complete,
                OwnerValue.Equals(
                    imageKey.GetValue(ParentOwnerValueName) as string,
                    StringComparison.Ordinal),
                rules);
        }

        private static IfeoRuleSnapshot SnapshotRule(RegistryKey ruleKey, string ruleName)
        {
            return new IfeoRuleSnapshot(
                ruleName,
                ruleKey.GetValue(OwnerValueName) as string,
                NormalizePath(ruleKey.GetValue(FilterFullPathValueName) as string));
        }

        private static void CleanupParentUseFilter(RegistryKey imageKey, string imageName)
        {
            IfeoImageSnapshot image;
            try
            {
                image = SnapshotImage(imageKey, imageName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                or System.Security.SecurityException
                or IOException)
            {
                return;
            }

            switch (IfeoOwnedRuleCleanupCoordinator.DecideParentCleanup(image, OwnerValue))
            {
                case IfeoParentCleanupAction.RemoveOwnerAndUseFilter:
                    imageKey.DeleteValue(UseFilterValueName, throwOnMissingValue: false);
                    imageKey.DeleteValue(ParentOwnerValueName, throwOnMissingValue: false);
                    break;
                case IfeoParentCleanupAction.RemoveOwner:
                    imageKey.DeleteValue(ParentOwnerValueName, throwOnMissingValue: false);
                    break;
            }
        }
    }

    private static string ViewName(RegistryView view)
    {
        return view == RegistryView.Registry64 ? "Registry64" : "Registry32";
    }
}
