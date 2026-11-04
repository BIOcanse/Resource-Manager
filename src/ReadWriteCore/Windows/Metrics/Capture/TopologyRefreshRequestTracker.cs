namespace ResourceManager.ReadWrite.Windows.Metrics.Capture;

// The original capture gate owns these source checks. Each disagreement can
// request once for the catalog it rejected; another sample is not new evidence.
internal sealed class TopologyRefreshRequestTracker(Action request, Action<string>? log = null)
{
    private readonly Dictionary<(string Source, string Reason), ulong> disagreements = [];

    internal void Observe(ulong catalogIdentity, string source, string reason, bool disagrees)
    {
        var key = (source, reason);
        if (!disagrees)
        {
            if (disagreements.Remove(key))
                log?.Invoke($"Hardware sampling stopped requesting topology refresh from {source}: {reason}.");
            return;
        }
        var active = disagreements.TryGetValue(key, out var requestedCatalog);
        if (!active)
            log?.Invoke($"Hardware sampling started requesting topology refresh from {source}: {reason}.");
        if (active && requestedCatalog == catalogIdentity) return;
        disagreements[key] = catalogIdentity;
        request();
    }
}
