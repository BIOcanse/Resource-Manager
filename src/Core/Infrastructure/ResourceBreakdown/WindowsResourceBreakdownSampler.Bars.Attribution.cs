using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.RuntimeSpecialization;
using System.Collections.Immutable;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private async Task<ProcessAttributionSnapshot> CreateProcessAttributionSnapshotAsync(
        IReadOnlyList<ProcessResourceSample> processSamples,
        CancellationToken cancellationToken)
    {
        var processById = processSamples.ToDictionary(static process => process.ProcessId);
        var baseScorePlan = runtimePlanProvider.Current.BaseScore;
        var capture = await RuntimeProcessAttributionCapture.CaptureAsync(processAttributionCatalogProvider,
            processSamples.Select(CreateRuntimeIdentity).ToArray(), baseScorePlan, cancellationToken,
            TryGetCachedDirectMatch,
            CacheDirectMatch);
        var batch = capture.Batch;
        if (batch.Failures.Count != 0)
            logger?.LogWarning("Process attribution could not observe these processes: {FailedProcesses}.",
                string.Join(", ", batch.Failures.Select(pair => $"{pair.Key}[{pair.Value}]")));
        return new ProcessAttributionSnapshot(
            processSamples, processById, batch.SoftwareByProcessId,
            baseScorePlan, capture.SoftwareBaseScores, batch.HelperOwnerByProcessId);
    }

    private RuntimeProcessIdentity CreateRuntimeIdentity(ProcessResourceSample process)
    {
        var identity = new RuntimeProcessIdentity(
            process.ProcessId,
            process.ParentProcessId,
            process.Name,
            process.ExecutablePath,
            process.IsSelfDescendant,
            process.FileDescription,
            process.ProductName,
            process.CompanyName,
            process.ApplicationUserModelId,
            process.WindowApplicationUserModelId,
            process.WindowTitle,
            process.StartKey,
            UserSid: process.BasicMetrics.UserSid);
        if (process.StartKey is not > 0) return identity;
        var key = new ProcessInstanceKey(process.ProcessId, process.StartKey.Value);
        processAttributionCache.TryGet(key, out var cached);
        if (cached?.Identity.CommandLine is not null)
            return identity with { CommandLine = cached.Identity.CommandLine };
        identity = identity with { CommandLine = ReadHelperCommandLine(process) };
        if (identity.CommandLine is not null)
            processAttributionCache.Set(key, cached is null
                ? new CachedProcessAttribution(identity)
                : cached with { Identity = identity });
        return identity;
    }

    private RuntimeProcessAttributionDirectMatch? TryGetCachedDirectMatch(
        RuntimeProcessIdentity process,
        string catalogScope,
        RuntimeProcessAttributionCatalog catalog)
        => TryGetCachedDirectMatch(processAttributionCache, process, catalogScope, catalog);

    internal static RuntimeProcessAttributionDirectMatch? TryGetCachedDirectMatch(
        ProcessInstanceCache<CachedProcessAttribution> cache,
        RuntimeProcessIdentity process,
        string catalogScope,
        RuntimeProcessAttributionCatalog catalog)
    {
        if (process.StartKey is not > 0) return null;
        var key = new ProcessInstanceKey(process.ProcessId, process.StartKey.Value);
        return cache.TryGet(key, out var cached)
            && cached is not null
            && cached.DirectMatch is { } match
            && cached.Identity == process
            && StringComparer.Ordinal.Equals(cached.CatalogScope, catalogScope)
            && cached.CatalogGeneration == catalog.Generation
            && cached.CatalogVersion == catalog.Version
            && StringComparer.Ordinal.Equals(cached.BundledCatalogVersion, catalog.BundledCatalogVersion)
                ? match
                : null;
    }

    private void CacheDirectMatch(
        RuntimeProcessIdentity process,
        string catalogScope,
        RuntimeProcessAttributionCatalog catalog,
        RuntimeProcessAttributionDirectMatch match)
        => CacheDirectMatch(processAttributionCache, process, catalogScope, catalog, match);

    internal static void CacheDirectMatch(
        ProcessInstanceCache<CachedProcessAttribution> cache,
        RuntimeProcessIdentity process,
        string catalogScope,
        RuntimeProcessAttributionCatalog catalog,
        RuntimeProcessAttributionDirectMatch match)
    {
        if (process.StartKey is not > 0) return;
        var key = new ProcessInstanceKey(process.ProcessId, process.StartKey.Value);
        cache.Set(key, new CachedProcessAttribution(
            process,
            catalogScope,
            catalog.Generation,
            catalog.Version,
            catalog.BundledCatalogVersion,
            match));
    }

    private static string? ReadHelperCommandLine(ProcessResourceSample process)
    {
        if (process.StartKey is not > 0) return null;
        return new ResourceManager.ReadWrite.Windows.Processes.WindowsProcessBasicMetricReader().Read(
            process.ProcessId, process.StartKey.Value,
            ResourceManager.ReadWrite.ProcessFields.ProcessMetricReadMask.CommandLine).CommandLine;
    }

    private sealed record ProcessAttributionSnapshot(
        IReadOnlyList<ProcessResourceSample> Processes,
        IReadOnlyDictionary<int, ProcessResourceSample> ProcessById,
        IReadOnlyDictionary<int, RuntimeSoftwareAttribution> AttributionByProcessId,
        CompiledBaseScorePlan BaseScorePlan,
        ImmutableArray<SoftwareBaseScore> SoftwareBaseScores,
        IReadOnlyDictionary<int, int>? HelperOwnerByProcessId = null);

    internal sealed record CachedProcessAttribution(
        RuntimeProcessIdentity Identity,
        string? CatalogScope = null,
        long CatalogGeneration = 0,
        RuntimeProcessAttributionCatalogVersion? CatalogVersion = null,
        string? BundledCatalogVersion = null,
        RuntimeProcessAttributionDirectMatch? DirectMatch = null);
}
