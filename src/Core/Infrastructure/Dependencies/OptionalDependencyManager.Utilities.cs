using System.Diagnostics;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Dependencies;

namespace ResourceManager.App.Infrastructure.Dependencies;

public sealed partial class OptionalDependencyManager
{
    private static void EnsureTerms(OptionalDependencyDefinition definition, bool acknowledgeExternalTerms)
    {
        OptionalDependencyCatalog.EnsureTerms(definition, acknowledgeExternalTerms);
    }

    private static DependencyDownloadProgress CreateProgress(
        string id,
        long bytesWritten,
        long? totalBytes,
        long startedAt)
    {
        var elapsedSeconds = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
        var speed = elapsedSeconds <= 0 ? (double?)null : bytesWritten / elapsedSeconds;
        var percent = totalBytes is > 0
            ? Math.Min(100, bytesWritten * 100d / totalBytes.Value)
            : (double?)null;
        return new DependencyDownloadProgress(id, bytesWritten, totalBytes, percent, speed);
    }
}
