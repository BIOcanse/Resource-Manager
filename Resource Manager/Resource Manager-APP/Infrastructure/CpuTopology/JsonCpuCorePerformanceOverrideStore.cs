using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed class JsonCpuCorePerformanceOverrideStore : ICpuCorePerformanceOverrideStore
{
    private const int CurrentVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object gate = new();
    private readonly string storagePath;
    private CpuCorePerformanceOverrideDocument? cachedDocument;

    public JsonCpuCorePerformanceOverrideStore(IHostEnvironment environment)
    {
        storagePath = ResolveStoragePath(environment.ContentRootPath);
    }

    public IReadOnlyDictionary<int, double> LoadScores(string cpuName)
        => LoadConfiguration(cpuName).ScoresByCoreIndex;

    public CpuPerformanceOverrides LoadConfiguration(string cpuName)
    {
        lock (gate)
        {
            var document = LoadDocument();
            var key = NormalizeCpuKey(cpuName);
            var scores = document.Profiles.TryGetValue(key, out var profile)
                ? new Dictionary<int, double>(profile.ScoresByCoreIndex)
                : new Dictionary<int, double>();
            return new CpuPerformanceOverrides(scores, document.BaselineRatio);
        }
    }

    public void SaveBaselineRatio(double ratio)
    {
        CpuBaselineRatio.Validate(ratio);
        lock (gate)
        {
            SaveDocument(LoadDocument() with { BaselineRatio = ratio });
        }
    }

    public void ResetBaselineRatio()
    {
        lock (gate)
        {
            SaveDocument(LoadDocument() with { BaselineRatio = null });
        }
    }

    public CpuCorePerformanceOverrideResult Save(CpuCorePerformanceOverrideRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CpuName))
        {
            throw new InvalidOperationException("CPU 名称不能为空。");
        }

        lock (gate)
        {
            var document = CopyDocument(LoadDocument());
            var key = NormalizeCpuKey(request.CpuName);
            var normalizedScores = NormalizeScores(request.Scores);
            var updatedAt = DateTimeOffset.UtcNow;
            document.Profiles[key] = new CpuCorePerformanceOverrideProfile(
                request.CpuName.Trim(),
                normalizedScores,
                updatedAt);
            SaveDocument(document);
            return new CpuCorePerformanceOverrideResult(
                request.CpuName.Trim(),
                normalizedScores,
                updatedAt,
                storagePath);
        }
    }

    public CpuCorePerformanceOverrideResult Reset(string cpuName)
    {
        if (string.IsNullOrWhiteSpace(cpuName))
        {
            throw new InvalidOperationException("CPU 名称不能为空。");
        }

        lock (gate)
        {
            var document = CopyDocument(LoadDocument());
            var normalizedCpuName = cpuName.Trim();
            document.Profiles.Remove(NormalizeCpuKey(normalizedCpuName));
            var updatedAt = DateTimeOffset.UtcNow;
            SaveDocument(document);
            return new CpuCorePerformanceOverrideResult(
                normalizedCpuName,
                new Dictionary<int, double>(),
                updatedAt,
                storagePath);
        }
    }

    private CpuCorePerformanceOverrideDocument LoadDocument()
    {
        if (cachedDocument is not null)
        {
            return cachedDocument;
        }

        try
        {
            using var stream = File.OpenRead(storagePath);
            var document = JsonSerializer.Deserialize<CpuCorePerformanceOverrideDocument>(stream, JsonOptions);
            cachedDocument = NormalizeDocument(document);
        }
        catch (FileNotFoundException)
        {
            cachedDocument = EmptyDocument();
        }
        catch (DirectoryNotFoundException)
        {
            cachedDocument = EmptyDocument();
        }

        return cachedDocument;
    }

    private void SaveDocument(CpuCorePerformanceOverrideDocument document)
    {
        var normalized = NormalizeDocument(document);
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = storagePath + $".{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, normalized, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, storagePath);
        cachedDocument = normalized;
    }

    private static CpuCorePerformanceOverrideDocument EmptyDocument()
        => new(CurrentVersion, new Dictionary<string, CpuCorePerformanceOverrideProfile>(StringComparer.OrdinalIgnoreCase));

    private static CpuCorePerformanceOverrideDocument CopyDocument(CpuCorePerformanceOverrideDocument document)
        => document with
        {
            Profiles = new Dictionary<string, CpuCorePerformanceOverrideProfile>(document.Profiles, StringComparer.OrdinalIgnoreCase)
        };

    private static CpuCorePerformanceOverrideDocument NormalizeDocument(CpuCorePerformanceOverrideDocument? document)
    {
        if (document is null || document.Version is not (1 or CurrentVersion) || document.Profiles is null)
        {
            throw new InvalidDataException("Unsupported or invalid CPU override document.");
        }
        if (document.BaselineRatio is { } ratio && !CpuBaselineRatio.IsValid(ratio))
        {
            throw new InvalidDataException("The stored CPU baseline ratio is invalid.");
        }
        var profiles = new Dictionary<string, CpuCorePerformanceOverrideProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.Profiles)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Value is null
                || string.IsNullOrWhiteSpace(item.Value.CpuName) || item.Value.ScoresByCoreIndex is null)
            {
                throw new InvalidDataException("The CPU override document contains an invalid profile.");
            }
            var profile = item.Value with
            {
                CpuName = item.Value.CpuName.Trim(),
                ScoresByCoreIndex = NormalizeScores(item.Value.ScoresByCoreIndex
                    .Select(score => new CpuCorePerformanceOverrideItem(score.Key, score.Value))
                    .ToArray())
            };
            if (!profiles.TryAdd(NormalizeCpuKey(profile.CpuName), profile))
            {
                throw new InvalidDataException("The CPU override document contains duplicate profiles.");
            }
        }

        return new CpuCorePerformanceOverrideDocument(CurrentVersion, profiles, document.BaselineRatio);
    }

    private static Dictionary<int, double> NormalizeScores(IReadOnlyList<CpuCorePerformanceOverrideItem>? scores)
    {
        var result = new Dictionary<int, double>();
        if (scores is null)
        {
            return result;
        }

        foreach (var item in scores)
        {
            if (item.CoreIndex < 0 || item.PerformanceScore is null)
            {
                continue;
            }

            var score = item.PerformanceScore.Value;
            result[item.CoreIndex] = double.IsFinite(score) ? Math.Max(0, score) : 0;
        }

        return result
            .OrderBy(static item => item.Key)
            .ToDictionary(static item => item.Key, static item => item.Value);
    }

    private static string NormalizeCpuKey(string cpuName)
    {
        return string.Join(
            ' ',
            cpuName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();
    }

    private static string ResolveStoragePath(string contentRootPath)
    {
        var packageRoot = PackagePathResolver.ResolvePackageRoot(contentRootPath);
        return Path.Combine(packageRoot, "Config", "cpu-core-performance-overrides.json");
    }

    private sealed record CpuCorePerformanceOverrideDocument(
        int Version,
        Dictionary<string, CpuCorePerformanceOverrideProfile> Profiles,
        double? BaselineRatio = null);

    private sealed record CpuCorePerformanceOverrideProfile(
        string CpuName,
        Dictionary<int, double> ScoresByCoreIndex,
        DateTimeOffset UpdatedAt);
}
