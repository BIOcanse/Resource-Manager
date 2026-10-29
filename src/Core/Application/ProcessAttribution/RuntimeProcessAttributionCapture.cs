using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Principal;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Application.ProcessAttribution;

internal static class RuntimeProcessAttributionCapture
{
    // Selecting SYSTEM explicitly also excludes the collector's HKCU when running interactively.
    internal const string MachineScope = "S-1-5-18";

    internal static string CatalogScope(string? userSid)
    {
        if (string.IsNullOrWhiteSpace(userSid)) return MachineScope;
        var sid = new SecurityIdentifier(userSid);
        return sid.IsAccountSid() || sid.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal)
            ? sid.Value : MachineScope;
    }

    internal static async Task<RuntimeProcessAttributionCaptureResult> CaptureAsync(
        IRuntimeProcessAttributionCatalogProvider provider,
        IReadOnlyList<RuntimeProcessIdentity> processes,
        CompiledBaseScorePlan baseScorePlan,
        CancellationToken cancellationToken,
        Func<RuntimeProcessIdentity, string, RuntimeProcessAttributionCatalog,
            RuntimeProcessAttributionDirectMatch?>? getCachedDirectMatch = null,
        Action<RuntimeProcessIdentity, string, RuntimeProcessAttributionCatalog,
            RuntimeProcessAttributionDirectMatch>? cacheDirectMatch = null,
        Func<RuntimeProcessIdentity, RuntimeProcessAttributionFixedInputs?>? getCachedFixedInputs = null,
        IDictionary<string, RuntimeProcessAttributionCatalogLease>? retainedCatalogs = null)
    {
        var catalogs = new Dictionary<string, RuntimeProcessAttributionCatalogLease>(StringComparer.Ordinal);
        var scopeByPid = new Dictionary<int, string>();
        try
        {
            foreach (var process in processes)
            {
                if (string.IsNullOrWhiteSpace(process.UserSid))
                    Trace.TraceWarning("Process attribution owner unavailable for PID {0}, creation {1}; using machine-wide software view.",
                        process.ProcessId, process.StartKey);
                var scope = CatalogScope(process.UserSid);
                scopeByPid.Add(process.ProcessId, scope);
                if (!catalogs.ContainsKey(scope))
                {
                    // Acquire keeps the existing source-revision check. A running
                    // owner keeps the catalog alive between rounds, so unchanged
                    // inputs reuse its native pipeline rather than rebuilding it.
                    var next = await provider.AcquireCatalogAsync(scope, cancellationToken);
                    if (retainedCatalogs is not null)
                    {
                        retainedCatalogs.TryGetValue(scope, out var previous);
                        try { retainedCatalogs[scope] = next; }
                        catch { next.Dispose(); throw; }
                        previous?.Dispose();
                    }
                    catalogs.Add(scope, next);
                }
            }
            Dictionary<int, RuntimeProcessAttributionDirectMatch>? cachedDirectMatches = null;
            if (getCachedDirectMatch is not null)
            {
                cachedDirectMatches = new Dictionary<int, RuntimeProcessAttributionDirectMatch>();
                foreach (var process in processes)
                {
                    if (process.StartKey is not > 0) continue;
                    var scope = scopeByPid[process.ProcessId];
                    var catalog = catalogs[scope].Catalog;
                    if (getCachedDirectMatch(process, scope, catalog) is { } match)
                        cachedDirectMatches[process.ProcessId] = match;
                }
            }
            var fixedInputs = getCachedFixedInputs is null ? null : processes
                .Select(process => (process.ProcessId, Facts: getCachedFixedInputs(process)))
                .Where(item => item.Facts is not null).ToDictionary(item => item.ProcessId, item => item.Facts!);
            var batch = RuntimeProcessAttributionPipeline.MatchBatch(processes,
                process => catalogs[scopeByPid[process.ProcessId]].Catalog.Pipeline,
                cancellationToken,
                cachedDirectMatches,
                captureDirectMatches: cacheDirectMatch is not null, cachedFixedInputs: fixedInputs);
            if (cacheDirectMatch is not null)
            {
                foreach (var process in processes)
                {
                    if (process.StartKey is not > 0
                        || !batch.DirectMatchesByProcessId.TryGetValue(process.ProcessId, out var match)
                        || cachedDirectMatches?.TryGetValue(process.ProcessId, out var previous) == true
                            && ReferenceEquals(previous.ExternalInputs, match.ExternalInputs))
                        continue;
                    var scope = scopeByPid[process.ProcessId];
                    cacheDirectMatch(process, scope, catalogs[scope].Catalog, match);
                }
            }
            // These are scoring inputs only. Matching always uses the owner's separate catalog.
            var scores = catalogs.Values.SelectMany(lease => lease.Catalog.GetSoftwareBaseScores(baseScorePlan))
                .DistinctBy(score => score.SoftwareId, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
            return new(batch, scores);
        }
        finally
        {
            if (retainedCatalogs is null)
                foreach (var catalog in catalogs.Values) catalog.Dispose();
        }
    }
}

internal sealed record RuntimeProcessAttributionCaptureResult(
    RuntimeProcessAttributionBatch Batch,
    ImmutableArray<SoftwareBaseScore> SoftwareBaseScores);
