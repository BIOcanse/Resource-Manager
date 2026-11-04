using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class JsonHostManagerRollbackStateStore
{
    public IReadOnlySet<string>? ReadExternalGpuAttemptReferences()
    {
        gate.Wait();
        try
        {
            ThrowIfDisposed();
            // Do not turn an unloaded or ambiguously committed authority into an empty set.
            if (persistedDocument is null) return null;
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var placement in persistedDocument.AppliedPlacements)
            foreach (var record in placement.Records)
            {
                if (record.Metadata?.TryGetValue("runtimeActionResult", out var json) != true) continue;
                if (json is null) return null;
                try
                {
                    var result = JsonSerializer.Deserialize<RunningGpuPlacementActionResult>(json);
                    if (result?.Records is null) return null;
                    foreach (var action in result.Records)
                    {
                        if (action?.Metadata is null) return null;
                        if (!action.Metadata.TryGetValue("evidenceDirectory", out var path)) continue;
                        if (!Path.IsPathFullyQualified(path)) return null;
                        references.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
                    }
                }
                catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
                { return null; }
            }
            return references;
        }
        finally { gate.Release(); }
    }
}
