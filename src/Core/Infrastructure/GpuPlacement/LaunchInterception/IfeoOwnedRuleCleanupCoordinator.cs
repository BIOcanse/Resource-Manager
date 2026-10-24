using Microsoft.Win32;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

internal sealed record IfeoRuleSnapshot(
    string RuleName,
    string? Owner,
    string? NormalizedFilterPath,
    string? Debugger = null,
    string? RuleId = null);

internal sealed record IfeoCleanupFailure(RegistryView View, string ImageName, Exception Error);

internal sealed record IfeoImageSnapshot(
    string ImageName,
    bool Complete,
    bool ParentUseFilterOwned,
    IReadOnlyList<IfeoRuleSnapshot> Rules,
    int? CurrentUseFilter = 1);

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
        var failures = new List<IfeoCleanupFailure>();
        var imageName = Path.GetFileName(executablePath);
        var expectedRuleName = WindowsIfeoGpuLaunchInterceptionRegistry.CreateRuleName(executablePath);
        foreach (var view in views)
        {
            try
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
            catch (Exception error) when (IsRegistryFailure(error)) { failures.Add(new(view, imageName, error)); }
        }
        ThrowFailures(failures);
    }

    internal int RemoveAll(IReadOnlyList<RegistryView> views)
    {
        var removed = 0;
        var failures = new List<IfeoCleanupFailure>();
        foreach (var view in views)
        {
            foreach (var imageName in EnumerateImages(view, failures))
            {
                try
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
                catch (Exception error) when (IsRegistryFailure(error)) { failures.Add(new(view, imageName, error)); }
            }
        }

        ThrowFailures(failures);
        return removed;
    }

    internal IReadOnlyList<IfeoCleanupFailure> RemoveStale(
        IReadOnlyList<RegistryView> views,
        IReadOnlySet<string> desiredPaths)
    {
        var failures = new List<IfeoCleanupFailure>();
        foreach (var view in views)
        {
            foreach (var imageName in EnumerateImages(view, failures))
            {
                try
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
                catch (Exception error) when (IsRegistryFailure(error)) { failures.Add(new(view, imageName, error)); }
            }
        }
        return failures;
    }

    private IReadOnlyList<string> EnumerateImages(RegistryView view, List<IfeoCleanupFailure> failures)
    {
        try { return store.EnumerateImageNames(view); }
        catch (Exception error) when (IsRegistryFailure(error)) { failures.Add(new(view, string.Empty, error)); return []; }
    }

    internal static bool IsRegistryFailure(Exception error) => error is UnauthorizedAccessException
        or System.Security.SecurityException or IOException or System.ComponentModel.Win32Exception or AggregateException;

    private static void ThrowFailures(IReadOnlyList<IfeoCleanupFailure> failures)
    {
        if (failures.Count > 0) throw new AggregateException(failures.Select(failure => failure.Error));
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

        return image.CurrentUseFilter != 1 || image.Rules.Any(static rule => !string.IsNullOrWhiteSpace(rule.NormalizedFilterPath))
            ? IfeoParentCleanupAction.RemoveOwner
            : IfeoParentCleanupAction.RemoveOwnerAndUseFilter;
    }

    internal static IReadOnlyList<string> OwnedValuesToRemove(IfeoRuleSnapshot rule, string debuggerCommand)
    {
        var values = new List<string> { "ResourceManagerOwner" };
        if (string.Equals(rule.RuleId, rule.RuleName, StringComparison.Ordinal)) values.Add("ResourceManagerRuleId");
        var ownedDebugger = string.Equals(rule.Debugger, debuggerCommand, StringComparison.Ordinal);
        if (ownedDebugger) values.Add("Debugger");
        // A replacement debugger still uses this filter. Relinquish it with that debugger.
        if ((ownedDebugger || rule.Debugger is null) && rule.NormalizedFilterPath is { } path
            && rule.RuleName.Equals(WindowsIfeoGpuLaunchInterceptionRegistry.CreateRuleName(path), StringComparison.Ordinal))
            values.Add("FilterFullPath");
        return values;
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
        var removed = candidates.Count == 0 && !settleParentOwnership
            ? 0
            : store.DeleteOwnedRules(
                view,
                image.ImageName,
                candidates,
                settleParentOwnership);
        if (!image.Complete) throw new IOException($"IFEO rules could not be fully read for {image.ImageName}.");
        return removed;
    }
}
