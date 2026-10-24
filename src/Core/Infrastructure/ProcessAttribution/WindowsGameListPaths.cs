using System.Collections.Frozen;
using ResourceManager.App.Infrastructure.SoftwareIdentity;

namespace ResourceManager.App.Infrastructure.ProcessAttribution;

internal static class WindowsGameListPaths
{
    internal static IReadOnlySet<string> Normalize(IEnumerable<string> paths)
        => paths.Select(CanonicalPath)
            .Where(static path => path is not null)
            .Select(static path => path!)
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static string? CanonicalPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path.Trim().Trim('"'))
            ? NativeSoftwareIdentityCatalogProjector.CanonicalPath(path)
            : null;
}
