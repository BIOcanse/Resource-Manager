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
        CancellationToken cancellationToken)
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
                    catalogs.Add(scope, await provider.AcquireCatalogAsync(scope, cancellationToken));
            }
            var batch = RuntimeProcessAttributionPipeline.MatchBatch(processes,
                process => catalogs[scopeByPid[process.ProcessId]].Catalog.Pipeline, cancellationToken);
            // These are scoring inputs only. Matching always uses the owner's separate catalog.
            var scores = catalogs.Values.SelectMany(lease => lease.Catalog.GetSoftwareBaseScores(baseScorePlan))
                .DistinctBy(score => score.SoftwareId, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
            return new(batch, scores);
        }
        finally
        {
            foreach (var catalog in catalogs.Values) catalog.Dispose();
        }
    }
}

internal sealed record RuntimeProcessAttributionCaptureResult(
    RuntimeProcessAttributionBatch Batch,
    ImmutableArray<SoftwareBaseScore> SoftwareBaseScores);
