using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private ProcessAttributionSnapshot CreateProcessAttributionSnapshot(
        IReadOnlyList<ProcessResourceSample> processSamples,
        RuntimeProcessAttributionCatalog catalog)
    {
        var processById = processSamples.ToDictionary(static process => process.ProcessId);
        return new ProcessAttributionSnapshot(
            processSamples,
            processById,
            BuildAttributionMap(processSamples, catalog),
            catalog);
    }

    private IReadOnlyDictionary<int, RuntimeSoftwareAttribution> BuildAttributionMap(
        IReadOnlyList<ProcessResourceSample> processSamples,
        RuntimeProcessAttributionCatalog catalog)
    {
        var resolved = new Dictionary<int, RuntimeSoftwareAttribution>();
        foreach (var process in processSamples)
        {
            var identity = CreateRuntimeIdentity(process);
            if (process.StartKey is not > 0)
            {
                resolved[process.ProcessId] =
                    catalog.Pipeline.Match(identity);
                continue;
            }

            var key =
                new ProcessInstanceKey(
                    process.ProcessId,
                    process.StartKey.Value);
            if (processAttributionCache.TryGet(key, out var cached)
                && cached is not null
                && cached.CatalogGeneration == catalog.Generation
                && cached.Identity == identity)
            {
                resolved[process.ProcessId] = cached.Attribution;
                continue;
            }

            var attribution = catalog.Pipeline.Match(identity);
            processAttributionCache.Set(
                key,
                new CachedProcessAttribution(
                    catalog.Generation,
                    identity,
                    attribution));
            resolved[process.ProcessId] = attribution;
        }

        return resolved;
    }

    private static RuntimeProcessIdentity CreateRuntimeIdentity(ProcessResourceSample process)
    {
        return new RuntimeProcessIdentity(
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
            process.StartKey);
    }

    private sealed record ProcessAttributionSnapshot(
        IReadOnlyList<ProcessResourceSample> Processes,
        IReadOnlyDictionary<int, ProcessResourceSample> ProcessById,
        IReadOnlyDictionary<int, RuntimeSoftwareAttribution> AttributionByProcessId,
        RuntimeProcessAttributionCatalog Catalog);

    private sealed record CachedProcessAttribution(
        long CatalogGeneration,
        RuntimeProcessIdentity Identity,
        RuntimeSoftwareAttribution Attribution);
}
