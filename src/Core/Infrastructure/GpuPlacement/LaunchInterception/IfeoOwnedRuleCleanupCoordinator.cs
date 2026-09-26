using Microsoft.Win32;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

internal sealed record IfeoRuleSnapshot(
    string RuleName,
    string? Owner,
    string? NormalizedFilterPath);

internal sealed record IfeoImageSnapshot(
    string ImageName,
    bool Complete,
    bool ParentUseFilterOwned,
    IReadOnlyList<IfeoRuleSnapshot> Rules);

internal sealed record IfeoOwnedRuleCandidate(
    string RuleName,
    string? NormalizedFilterPath);

internal enum IfeoParentCleanupAction
{
    None,
    RemoveOwner,
    RemoveOwnerAndUseFilter
}

internal interface IIfeoOwnedRuleStore
{
    IReadOnlyList<string> EnumerateImageNames(RegistryView view);

    IfeoImageSnapshot? ReadImage(RegistryView view, string imageName);

    int DeleteOwnedRules(
        RegistryView view,
        string imageName,
        IReadOnlyList<IfeoOwnedRuleCandidate> candidates,
        bool settleParentOwnership);
}

internal sealed class IfeoOwnedRuleCleanupCoordinator(
    IIfeoOwnedRuleStore store,
    string ownerValue)
{
    internal void RemoveForPath(
        IReadOnlyList<RegistryView> views,
        string executablePath)
    {
        var imageName = Path.GetFileName(executablePath);
        var expectedRuleName = WindowsIfeoGpuLaunchInterceptionRegistry.CreateRuleName(executablePath);
        foreach (var view in views)
        {
            var image = store.ReadImage(view, imageName);
            if (image is null)
            {
                continue;
            }

            var candidates = image.Rules
                .Where(rule => IsOwned(rule)
                    && (rule.RuleName.Equals(expectedRuleName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            rule.NormalizedFilterPath,
                            executablePath,
                            StringComparison.OrdinalIgnoreCase)))
                .Select(ToCandidate)
                .ToArray();
            DeleteIfNeeded(
                view,
                image,
                candidates,
                candidates.Length > 0 || HasOrphanedParentOwnership(image));
        }
    }

    internal int RemoveAll(IReadOnlyList<RegistryView> views)
    {
        var removed = 0;
        foreach (var view in views)
        {
            foreach (var imageName in store.EnumerateImageNames(view))
            {
                var image = store.ReadImage(view, imageName);
                if (image is null)
                {
                    continue;
                }

                var candidates = image.Rules
                    .Where(IsOwned)
                    .Select(ToCandidate)
                    .ToArray();
                removed += DeleteIfNeeded(
                    view,
                    image,
                    candidates,
                    candidates.Length > 0 || image.ParentUseFilterOwned);
            }
        }

        return removed;
    }

    internal void RemoveStale(
        IReadOnlyList<RegistryView> views,
        IReadOnlySet<string> desiredPaths)
    {
        foreach (var view in views)
        {
            foreach (var imageName in store.EnumerateImageNames(view))
            {
                var image = store.ReadImage(view, imageName);
                if (image is null)
                {
                    continue;
                }

                var candidates = image.Rules
                    .Where(rule => IsOwned(rule) && !IsCanonicalDesiredRule(imageName, rule, desiredPaths))
                    .Select(ToCandidate)
                    .ToArray();
                DeleteIfNeeded(
                    view,
                    image,
                    candidates,
                    candidates.Length > 0 || HasOrphanedParentOwnership(image));
            }
        }
    }

    internal static bool MatchesCandidate(
        IfeoRuleSnapshot current,
        IfeoOwnedRuleCandidate candidate,
        string ownerValue)
    {
        return current.RuleName.Equals(candidate.RuleName, StringComparison.OrdinalIgnoreCase)
            && ownerValue.Equals(current.Owner, StringComparison.Ordinal)
            && string.Equals(
                current.NormalizedFilterPath,
                candidate.NormalizedFilterPath,
                StringComparison.OrdinalIgnoreCase);
    }

    internal static IfeoParentCleanupAction DecideParentCleanup(
        IfeoImageSnapshot image,
        string ownerValue)
    {
        if (!image.Complete
            || !image.ParentUseFilterOwned
            || image.Rules.Any(rule => ownerValue.Equals(rule.Owner, StringComparison.Ordinal)))
        {
            return IfeoParentCleanupAction.None;
        }

        return image.Rules.Any(static rule => !string.IsNullOrWhiteSpace(rule.NormalizedFilterPath))
            ? IfeoParentCleanupAction.RemoveOwner
            : IfeoParentCleanupAction.RemoveOwnerAndUseFilter;
    }

    private bool IsOwned(IfeoRuleSnapshot rule)
    {
        return ownerValue.Equals(rule.Owner, StringComparison.Ordinal);
    }

    private static bool IsCanonicalDesiredRule(
        string imageName,
        IfeoRuleSnapshot rule,
        IReadOnlySet<string> desiredPaths)
    {
        var filterPath = rule.NormalizedFilterPath;
        return !string.IsNullOrWhiteSpace(filterPath)
            && desiredPaths.Contains(filterPath)
            && imageName.Equals(Path.GetFileName(filterPath), StringComparison.OrdinalIgnoreCase)
            && rule.RuleName.Equals(
                WindowsIfeoGpuLaunchInterceptionRegistry.CreateRuleName(filterPath),
                StringComparison.OrdinalIgnoreCase);
    }

    private static IfeoOwnedRuleCandidate ToCandidate(IfeoRuleSnapshot rule)
    {
        return new IfeoOwnedRuleCandidate(rule.RuleName, rule.NormalizedFilterPath);
    }

    private bool HasOrphanedParentOwnership(IfeoImageSnapshot image)
    {
        return image.Complete
            && image.ParentUseFilterOwned
            && !image.Rules.Any(IsOwned);
    }

    private int DeleteIfNeeded(
        RegistryView view,
        IfeoImageSnapshot image,
        IReadOnlyList<IfeoOwnedRuleCandidate> candidates,
        bool settleParentOwnership)
    {
        return candidates.Count == 0 && !settleParentOwnership
            ? 0
            : store.DeleteOwnedRules(
                view,
                image.ImageName,
                candidates,
                settleParentOwnership);
    }
}
