using System.Text.Json;
using ResourceManager.App.Application.Optimization.Scheduling;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed class JsonGpuPerformanceScoreOverrideStore : IGpuPerformanceScoreOverrideStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly string storagePath;
    private GpuPerformanceScoreOverrideDocument? cachedDocument;

    public JsonGpuPerformanceScoreOverrideStore(IHostEnvironment environment)
    {
        storagePath = ResolveStoragePath(environment.ContentRootPath);
    }

    public IReadOnlyDictionary<string, double> LoadScores()
    {
        lock (gate)
        {
            var document = LoadDocument();
            return new Dictionary<string, double>(document.ScoresByGpuId, StringComparer.OrdinalIgnoreCase);
        }
    }

    public GpuPerformanceScoreOverrideResult Load()
    {
        lock (gate)
        {
            var document = LoadDocument();
            return new GpuPerformanceScoreOverrideResult(
                new Dictionary<string, double>(document.ScoresByGpuId, StringComparer.OrdinalIgnoreCase),
                document.UpdatedAt,
                storagePath);
        }
    }

    public GpuPerformanceScoreOverrideResult Save(GpuPerformanceScoreOverrideRequest request)
    {
        lock (gate)
        {
            var document = LoadDocument();
            var normalizedScores = NormalizeScores(request.Scores);
            var updatedAt = DateTimeOffset.UtcNow;
            var updated = document with
            {
                ScoresByGpuId = normalizedScores,
                UpdatedAt = updatedAt
            };
            SaveDocument(updated);
            return new GpuPerformanceScoreOverrideResult(
                normalizedScores,
                updatedAt,
                storagePath);
        }
    }

    public GpuPerformanceScoreOverrideResult Reset()
    {
        lock (gate)
        {
            _ = LoadDocument();
            var updatedAt = DateTimeOffset.UtcNow;
            var updated = new GpuPerformanceScoreOverrideDocument(
                CurrentVersion,
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                updatedAt);
            SaveDocument(updated);
            return new GpuPerformanceScoreOverrideResult(
                updated.ScoresByGpuId,
                updatedAt,
                storagePath);
        }
    }

    private GpuPerformanceScoreOverrideDocument LoadDocument()
    {
        if (cachedDocument is not null)
        {
            return cachedDocument;
        }

        try
        {
            using var stream = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<GpuPerformanceScoreOverrideDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("The GPU performance score file is empty.");
            cachedDocument = NormalizeDocument(document);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            cachedDocument = new GpuPerformanceScoreOverrideDocument(
                CurrentVersion,
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                DateTimeOffset.MinValue);
        }
        return cachedDocument;
    }

    private void SaveDocument(GpuPerformanceScoreOverrideDocument document)
    {
        var normalized = NormalizeDocument(document);
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{storagePath}.{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, normalized, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        NativeCore.WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, storagePath);
        cachedDocument = normalized;
    }

    private static GpuPerformanceScoreOverrideDocument NormalizeDocument(GpuPerformanceScoreOverrideDocument? document)
    {
        return new GpuPerformanceScoreOverrideDocument(
            CurrentVersion,
            NormalizeScores(document?.ScoresByGpuId
                .Select(item => new GpuPerformanceScoreOverrideItem(item.Key, item.Value))
                .ToArray()),
            document?.UpdatedAt ?? DateTimeOffset.MinValue);
    }

    private static Dictionary<string, double> NormalizeScores(IReadOnlyList<GpuPerformanceScoreOverrideItem>? scores)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (scores is null)
        {
            return result;
        }

        foreach (var item in scores)
        {
            var gpuId = NormalizeGpuId(item.GpuId);
            if (string.IsNullOrWhiteSpace(gpuId) || item.PerformanceScore is null)
            {
                continue;
            }

            var score = GpuPerformanceScorePresetResolver.NormalizeManualScore(item.PerformanceScore.Value);
            result[gpuId] = score;
        }

        return result
            .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static item => item.Key,
                static item => item.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeGpuId(string? gpuId)
    {
        return string.Join(
            ' ',
            (gpuId ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string ResolveStoragePath(string contentRootPath)
    {
        var packageRoot = PackagePathResolver.ResolvePackageRoot(contentRootPath);
        return Path.Combine(packageRoot, "Config", "gpu-performance-score-overrides.json");
    }

    private sealed record GpuPerformanceScoreOverrideDocument(
        int Version,
        Dictionary<string, double> ScoresByGpuId,
        DateTimeOffset UpdatedAt);
}
