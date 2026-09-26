namespace ResourceManager.Shared.BrowserRuntimes;

public static class BrowserRuntimeSelection
{
    public static BrowserRuntimeDiscoveryResult Select(
        IEnumerable<BrowserRuntimeCandidate> source)
    {
        var candidates = source
            .GroupBy(static item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        var sharedRuntime = candidates
            .Where(static item => item.Kind == BrowserRuntimeKinds.WebView2Runtime)
            .OrderBy(SourcePriority)
            .ThenByDescending(static item => ParseVersion(item.Version))
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        var browserFallback = candidates
            .Where(static item => item.Kind == BrowserRuntimeKinds.ChromiumBrowser)
            .OrderByDescending(static item => ParseVersion(item.Version))
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? candidates
                .Where(static item => item.Kind == BrowserRuntimeKinds.GeckoBrowser)
                .OrderByDescending(static item => ParseVersion(item.Version))
                .ThenBy(static item => item.Id, StringComparer.Ordinal)
                .FirstOrDefault();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal)
        {
            sharedRuntime?.Id ?? "",
            browserFallback?.Id ?? ""
        };
        selectedIds.Remove("");

        var selectedCandidates = candidates
            .Select(item => item with { Selected = selectedIds.Contains(item.Id) })
            .OrderBy(static item => KindPriority(item.Kind))
            .ThenByDescending(static item => ParseVersion(item.Version))
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BrowserRuntimeDiscoveryResult(
            FindSelected(selectedCandidates, sharedRuntime),
            FindSelected(selectedCandidates, browserFallback),
            selectedCandidates,
            DateTimeOffset.UtcNow);
    }

    private static BrowserRuntimeCandidate? FindSelected(
        IReadOnlyList<BrowserRuntimeCandidate> candidates,
        BrowserRuntimeCandidate? selected)
        => selected is null
            ? null
            : candidates.First(item => item.Id.Equals(selected.Id, StringComparison.Ordinal));

    private static int SourcePriority(BrowserRuntimeCandidate candidate)
        => candidate.Source switch
        {
            "SystemMachine" => 0,
            "SystemUser" => 1,
            "Managed" => 2,
            _ => 3
        };

    private static int KindPriority(string kind)
        => kind switch
        {
            BrowserRuntimeKinds.WebView2Runtime => 0,
            BrowserRuntimeKinds.ChromiumBrowser => 1,
            BrowserRuntimeKinds.GeckoBrowser => 2,
            _ => 3
        };

    private static Version ParseVersion(string value)
    {
        var token = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "0.0";
        return Version.TryParse(token, out var parsed) ? parsed : new Version(0, 0);
    }
}
