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
        return ReadRuntimeCommandLine(processAttributionCache, identity, () => ReadHelperCommandLine(process));
    }

    internal static RuntimeProcessIdentity ReadRuntimeCommandLine(
        ProcessInstanceCache<CachedProcessAttribution> cache, RuntimeProcessIdentity identity, Func<string?> read)
    {
        if (identity.StartKey is not > 0) return identity;
        var key = new ProcessInstanceKey(identity.ProcessId, identity.StartKey.Value);
        cache.TryGet(key, out var cached);
        identity = identity with { CommandLine = cached is not null ? cached.Identity.CommandLine : read() };
        CacheRuntimeIdentity(cache, identity);
        return identity;
    }

    internal static void CacheRuntimeIdentity(
        ProcessInstanceCache<CachedProcessAttribution> cache,
        RuntimeProcessIdentity identity)
    {
        if (identity.StartKey is not > 0) return;
        var key = new ProcessInstanceKey(identity.ProcessId, identity.StartKey.Value);
        if (!cache.TryGet(key, out var cached) || cached?.Identity != identity)
            // A direct match belongs to its original input. Recovery must not
            // overwrite that input while carrying the old result and stamps.
            cache.Set(key, new CachedProcessAttribution(identity));
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
            && cached.CatalogSnapshot?.TryGetTarget(out var snapshot) == true
            && ReferenceEquals(snapshot, catalog)
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
            match,
            new WeakReference<RuntimeProcessAttributionCatalog>(catalog)));
    }

    private string? ReadHelperCommandLine(ProcessResourceSample process)
    {
        if (process.StartKey is not > 0) return null;
        return processBasicMetricReader.Read(
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
        RuntimeProcessAttributionDirectMatch? DirectMatch = null,
        WeakReference<RuntimeProcessAttributionCatalog>? CatalogSnapshot = null);
}
