using System.Collections.Concurrent;
using ResourceManager.App.Application.Indexing;
using ResourceManager.App.Domain.Indexing;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Persistence;

namespace ResourceManager.App.Infrastructure.Indexing;

public sealed partial class SqliteSoftwareFileIndex(
    ResourceManagerDatabase database,
    INativeFileQueryLeaseSource fileQuery,
    ILogger<SqliteSoftwareFileIndex> logger) : ISoftwareFileIndex, ISoftwareFileIndexRefresher
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> softwareGates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SoftwareFileIndexSnapshot> GetOrRefreshAsync(
        SoftwareFileIndexRequest request,
        TimeSpan maximumAge,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SoftwareId);
        var normalized = NormalizeRequest(request);
        var gate = softwareGates.GetOrAdd(normalized.SoftwareId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var cached = await GetSnapshotAsync(normalized.SoftwareId, cancellationToken);
            if (!forceRefresh && IsFreshAndComplete(cached, normalized.Roots, maximumAge))
            {
                return cached!;
            }

            var allSucceeded = true;
            foreach (var root in normalized.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await RefreshRootAsync(normalized, root, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    allSucceeded = false;
                    logger.LogWarning(
                        ex,
                        "Software file index refresh failed for {SoftwareId} root {RootPath}; retaining the last complete snapshot.",
                        normalized.SoftwareId,
                        root.Path);
                }
            }

            if (allSucceeded)
            {
                await RemoveStaleRootsAsync(normalized.SoftwareId, normalized.Roots, cancellationToken);
            }

            return await GetSnapshotAsync(normalized.SoftwareId, cancellationToken)
                ?? EmptySnapshot(normalized);
        }
        finally
        {
            gate.Release();
        }
    }

    private static SoftwareFileIndexRequest NormalizeRequest(SoftwareFileIndexRequest request)
    {
        var roots = request.Roots
            .Select(static root => new SoftwareFileIndexRoot(NormalizePath(root.Path), root.Kind.Trim()))
            .Where(static root => !string.IsNullOrWhiteSpace(root.Path))
            .GroupBy(static root => PathKey(root.Path), StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static root => root.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return request with
        {
            SoftwareId = request.SoftwareId.Trim(),
            SoftwareName = string.IsNullOrWhiteSpace(request.SoftwareName)
                ? request.SoftwareId.Trim()
                : request.SoftwareName.Trim(),
            Roots = roots
        };
    }

    private static bool IsFreshAndComplete(
        SoftwareFileIndexSnapshot? snapshot,
        IReadOnlyList<SoftwareFileIndexRoot> requestedRoots,
        TimeSpan maximumAge)
    {
        if (snapshot is null || snapshot.Roots.Count != requestedRoots.Count)
        {
            return false;
        }

        var requestedKeys = requestedRoots
            .Select(static root => PathKey(root.Path))
            .ToHashSet(StringComparer.Ordinal);
        if (snapshot.Roots.Any(root => !requestedKeys.Contains(PathKey(root.Path))))
        {
            return false;
        }

        return snapshot.Roots.All(root =>
            root.LastIndexedAt is { } indexedAt
            && DateTimeOffset.UtcNow - indexedAt <= maximumAge);
    }

    private static SoftwareFileIndexSnapshot EmptySnapshot(SoftwareFileIndexRequest request)
        => new(request.SoftwareId, request.SoftwareName, [], 0, 0, null);

    internal static string NormalizePath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrWhiteSpace(root)
                && fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Equals(
                        root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private static string PathKey(string path) => path.ToUpperInvariant();
}
