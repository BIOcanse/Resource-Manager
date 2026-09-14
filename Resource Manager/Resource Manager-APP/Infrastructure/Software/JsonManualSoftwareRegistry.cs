using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Software;

public sealed class JsonManualSoftwareRegistry(IHostEnvironment environment) : IManualSoftwareRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storagePath = Path.Combine(
        PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "Config",
        "manual-software.json");

    public async Task<IReadOnlyList<ManualSoftwareRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ManualSoftwareRecord> AddOrUpdateAsync(
        ManualSoftwareRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeRequest(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var records = (await LoadCoreAsync(cancellationToken)).ToList();
            var id = BuildId(normalized);
            var now = DateTimeOffset.Now;
            var existing = records.FirstOrDefault(record =>
                record.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                || HasSameSourceSoftware(record, normalized));
            var existingCreatedAt = existing?.CreatedAt ?? now;
            var record = new ManualSoftwareRecord(
                id,
                normalized.Name,
                normalized.Kind,
                DisplayKind(normalized.Kind),
                "manual",
                string.IsNullOrWhiteSpace(normalized.SourceSoftwareId)
                    ? ["手动补录"]
                    : ["手动分类", "来自软件列表"],
                normalized.RootPaths,
                BuildMessage(normalized.Kind),
                normalized.SourceSoftwareId,
                existingCreatedAt,
                now);
            records.RemoveAll(existingRecord =>
                existingRecord.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                || HasSameSourceSoftware(existingRecord, normalized));
            records.Add(record);

            await SaveCoreAsync(records, cancellationToken);
            return record;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var records = (await LoadCoreAsync(cancellationToken)).ToList();
            var removed = records.RemoveAll(record => record.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                await SaveCoreAsync(records, cancellationToken);
            }

            return removed;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<ManualSoftwareRecord>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(storagePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(storagePath);
            var records = await JsonSerializer.DeserializeAsync<List<ManualSoftwareRecord>>(
                stream,
                JsonOptions,
                cancellationToken);
            return NormalizeRecords(records ?? []);
        }
        catch
        {
            return [];
        }
    }

    private async Task SaveCoreAsync(
        IReadOnlyList<ManualSoftwareRecord> records,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(storagePath);
        await JsonSerializer.SerializeAsync(stream, NormalizeRecords(records), JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static IReadOnlyList<ManualSoftwareRecord> NormalizeRecords(IEnumerable<ManualSoftwareRecord> records)
    {
        return records
            .Where(static record => !string.IsNullOrWhiteSpace(record.Id) && !string.IsNullOrWhiteSpace(record.Name))
            .Select(static record => record with
            {
                Kind = NormalizeKind(record.Kind),
                DisplayKind = DisplayKind(NormalizeKind(record.Kind)),
                RootPaths = NormalizeRootPaths(record.RootPaths),
                Sources = record.Sources is { Count: > 0 } ? record.Sources : ["手动分类"]
            })
            .Where(static record => IsAllowedKind(record.Kind))
            .GroupBy(static record => record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static record => record.UpdatedAt).First())
            .OrderBy(static record => KindSort(record.Kind))
            .ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ManualSoftwareRequest NormalizeRequest(ManualSoftwareRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("手动软件分类请求不能为空。");
        }

        var kind = NormalizeKind(request.Kind);
        if (!IsAllowedKind(kind))
        {
            throw new InvalidOperationException($"手动软件分类只支持{SoftwareText.Adapted}、{SoftwareText.Game}、{SoftwareText.HighPerformance}和{SoftwareText.General}。");
        }

        var roots = NormalizeRootPaths(request.RootPaths);
        var name = CleanText(request.Name)
            ?? (kind == SoftwareKinds.Adapted ? InferNameFromRootPath(roots.FirstOrDefault()) : null);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("软件名称不能为空。");
        }

        if ((kind == SoftwareKinds.Adapted || kind == SoftwareKinds.Other) && roots.Count == 0)
        {
            throw new InvalidOperationException($"手动补录{SoftwareText.Adapted}或{SoftwareText.General}至少需要一个根目录。");
        }

        return new ManualSoftwareRequest(name, kind, roots, CleanText(request.SourceSoftwareId));
    }

    private static string BuildId(ManualSoftwareRequest request)
    {
        var source = string.IsNullOrWhiteSpace(request.SourceSoftwareId)
            ? $"{request.Kind}|{request.Name}|{string.Join("|", request.RootPaths)}"
            : $"{request.Kind}|{request.SourceSoftwareId}";
        return $"manual:{request.Kind.ToLowerInvariant()}:{ShortHash(source)}";
    }

    private static IReadOnlyList<string> NormalizeRootPaths(IEnumerable<string>? paths)
    {
        return (paths ?? [])
            .Select(CleanPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? CleanPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value.Trim();
        }
    }

    private static string? CleanText(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    private static string? InferNameFromRootPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return CleanText(leaf) ?? CleanText(path);
    }

    private static string NormalizeKind(string? value)
    {
        return CleanText(value) switch
        {
            SoftwareKinds.Adapted => SoftwareKinds.Adapted,
            SoftwareKinds.Game => SoftwareKinds.Game,
            SoftwareKinds.HighPerformance => SoftwareKinds.HighPerformance,
            SoftwareKinds.Other => SoftwareKinds.Other,
            "适配软件" => SoftwareKinds.Adapted,
            "游戏" => SoftwareKinds.Game,
            "高性能软件" => SoftwareKinds.HighPerformance,
            _ => SoftwareKinds.Other
        };
    }

    private static bool HasSameSourceSoftware(ManualSoftwareRecord record, ManualSoftwareRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.SourceSoftwareId)
            && !string.IsNullOrWhiteSpace(record.SourceSoftwareId)
            && record.SourceSoftwareId.Equals(request.SourceSoftwareId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedKind(string kind)
    {
        return kind is SoftwareKinds.Adapted or SoftwareKinds.Game or SoftwareKinds.HighPerformance or SoftwareKinds.Other;
    }

    private static string DisplayKind(string kind)
    {
        return kind switch
        {
            SoftwareKinds.Adapted => SoftwareText.Adapted,
            SoftwareKinds.Game => SoftwareText.Game,
            SoftwareKinds.HighPerformance => SoftwareText.HighPerformance,
            _ => SoftwareText.General
        };
    }

    private static string BuildMessage(string kind)
    {
        return kind switch
        {
            SoftwareKinds.Adapted => "用户手动补录为适配软件；按根目录识别运行进程，并尝试读取其适配控制端点。",
            SoftwareKinds.Game => "用户手动分类为游戏；游戏模式可优先使用更强的后台让路策略。",
            SoftwareKinds.HighPerformance => "用户手动分类为高性能软件；需要资源倾斜，但不默认使用游戏同等级策略。",
            _ => "用户手动补录的软件；刷新 Windows 软件列表不会删除。"
        };
    }

    private static int KindSort(string kind)
    {
        return kind switch
        {
            SoftwareKinds.Adapted => 0,
            SoftwareKinds.Game => 1,
            SoftwareKinds.HighPerformance => 2,
            _ => 3
        };
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
