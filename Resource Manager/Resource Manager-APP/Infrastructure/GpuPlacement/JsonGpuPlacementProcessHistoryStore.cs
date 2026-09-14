using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed class JsonGpuPlacementProcessHistoryStore(IHostEnvironment environment) : IGpuPlacementProcessHistoryStore
{
    private const int MaxProcessesPerSoftware = 64;
    private static readonly TimeSpan MinimumLastSeenPersistInterval = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storagePath = Path.Combine(
        PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData",
        "SoftwareProfiles",
        "gpu-placement-process-history.local.json");

    public async Task<GpuPlacementSoftwareProcessHistory> GetSoftwareHistoryAsync(
        string softwareId,
        string softwareName,
        CancellationToken cancellationToken)
    {
        var normalizedId = RequireText(softwareId, "软件标识不能为空。");
        var normalizedName = CleanText(softwareName) ?? normalizedId;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCoreAsync(cancellationToken);
            return document.SoftwareHistories.FirstOrDefault(history =>
                    history.SoftwareId.Equals(normalizedId, StringComparison.OrdinalIgnoreCase))
                ?? GpuPlacementSoftwareProcessHistory.Empty(normalizedId, normalizedName);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GpuPlacementSoftwareProcessHistory> ObserveAsync(
        GpuPlacementProcessObservationRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedRequest = NormalizeRequest(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCoreAsync(cancellationToken);
            var now = DateTimeOffset.Now;
            var histories = document.SoftwareHistories.ToList();
            var index = histories.FindIndex(history =>
                history.SoftwareId.Equals(normalizedRequest.SoftwareId, StringComparison.OrdinalIgnoreCase));
            var current = index >= 0
                ? histories[index]
                : GpuPlacementSoftwareProcessHistory.Empty(normalizedRequest.SoftwareId, normalizedRequest.SoftwareName);

            var processMap = current.Processes.ToDictionary(
                static process => process.ProcessKey,
                StringComparer.OrdinalIgnoreCase);
            var shouldPersist = index < 0;

            foreach (var inputs in normalizedRequest.Processes.GroupBy(
                static input => BuildProcessKey(input.ProcessName, input.ExecutablePath), StringComparer.OrdinalIgnoreCase))
            {
                var input = inputs.First();
                var observed = CreateObservedProcess(input, now);
                processMap.TryGetValue(observed.ProcessKey, out var existing);
                if (existing is null)
                {
                    processMap[observed.ProcessKey] = observed;
                    shouldPersist = true;
                    continue;
                }

                var merged = MergeProcess(existing, observed, now);
                processMap[observed.ProcessKey] = merged;
                shouldPersist = shouldPersist || ShouldPersistExistingProcess(existing, merged, now);
            }

            var nextHistory = new GpuPlacementSoftwareProcessHistory(
                normalizedRequest.SoftwareId,
                normalizedRequest.SoftwareName,
                RetainIdentifiedProcesses(processMap.Values)
                    .OrderBy(static process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static process => process.ProcessKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                now);

            if (shouldPersist)
            {
                await SaveHistoryCoreAsync(document, nextHistory, cancellationToken);
            }

            return nextHistory;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GpuPlacementSoftwareProcessHistory> SaveFirstGraphicsApiAsync(
        string softwareId,
        string softwareName,
        GpuPlacementProcessInstance process,
        GpuGraphicsApi graphicsApi,
        CancellationToken cancellationToken)
    {
        var normalizedId = RequireText(softwareId, "软件标识不能为空。");
        var normalizedName = CleanText(softwareName) ?? normalizedId;
        ArgumentNullException.ThrowIfNull(process);
        if (process.ProcessId <= 4 || process.ProcessStartKey == 0
            || string.IsNullOrWhiteSpace(process.ExecutablePath)
            || !Path.IsPathFullyQualified(process.ExecutablePath)
            || string.IsNullOrEmpty(Path.GetFileName(process.ExecutablePath)))
            throw new ArgumentException("API 识别结果必须属于明确的进程实例和完整可执行路径。", nameof(process));
        if (!GpuGraphicsApiRoutes.IsIdentified(graphicsApi))
            throw new ArgumentOutOfRangeException(nameof(graphicsApi));

        var path = Path.GetFullPath(process.ExecutablePath);
        var key = BuildProcessKey(process.ProcessName, path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCoreAsync(cancellationToken);
            var current = document.SoftwareHistories.FirstOrDefault(history =>
                history.SoftwareId.Equals(normalizedId, StringComparison.OrdinalIgnoreCase))
                ?? GpuPlacementSoftwareProcessHistory.Empty(normalizedId, normalizedName);
            var existing = current.Processes.FirstOrDefault(item =>
                item.ProcessKey.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existing?.GraphicsApi is not null) return current;

            var now = DateTimeOffset.Now;
            var identified = (existing ?? CreateObservedProcess(new(
                process.ProcessName, path, null, process.ProcessId, ["actual-api-call"]), now)) with
            {
                GraphicsApi = graphicsApi,
                EvidenceSources = CleanEvidenceSources((existing?.EvidenceSources ?? []).Append("actual-api-call"))
            };
            var next = current with
            {
                Processes = RetainIdentifiedProcesses(current.Processes.Where(item =>
                        !item.ProcessKey.Equals(key, StringComparison.OrdinalIgnoreCase)).Append(identified))
                    .OrderBy(static item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.ProcessKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                UpdatedAt = now
            };
            await SaveHistoryCoreAsync(document, next, cancellationToken);
            return next;
        }
        finally
        {
            gate.Release();
        }
    }

    private Task SaveHistoryCoreAsync(
        GpuPlacementProcessHistoryDocument document,
        GpuPlacementSoftwareProcessHistory history,
        CancellationToken cancellationToken)
        => SaveCoreAsync(new(
            GpuPlacementPolicyDocumentVersions.Current,
            document.SoftwareHistories.Where(item =>
                    !item.SoftwareId.Equals(history.SoftwareId, StringComparison.OrdinalIgnoreCase))
                .Append(history)
                .OrderBy(static item => item.SoftwareName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.SoftwareId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            history.UpdatedAt), cancellationToken);

    private async Task<GpuPlacementProcessHistoryDocument> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(storagePath))
        {
            return GpuPlacementProcessHistoryDocument.Empty;
        }

        try
        {
            await using var stream = File.OpenRead(storagePath);
            var document = await JsonSerializer.DeserializeAsync<GpuPlacementProcessHistoryDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            return NormalizeDocument(document);
        }
        catch (FileNotFoundException) { return GpuPlacementProcessHistoryDocument.Empty; }
    }

    private async Task SaveCoreAsync(
        GpuPlacementProcessHistoryDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{storagePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, NormalizeDocument(document), JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        NativeCore.WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, storagePath);
    }

    private static GpuPlacementProcessHistoryDocument NormalizeDocument(
        GpuPlacementProcessHistoryDocument? document)
    {
        if (document is null)
        {
            return GpuPlacementProcessHistoryDocument.Empty;
        }

        var histories = (document.SoftwareHistories ?? [])
            .Select(static history => NormalizeHistory(history))
            .Where(static history => !string.IsNullOrWhiteSpace(history.SoftwareId))
            .GroupBy(static history => history.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static history => history.UpdatedAt).First())
            .OrderBy(static history => history.SoftwareName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static history => history.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new GpuPlacementProcessHistoryDocument(
            GpuPlacementPolicyDocumentVersions.Current,
            histories,
            document.UpdatedAt);
    }

    private static GpuPlacementSoftwareProcessHistory NormalizeHistory(
        GpuPlacementSoftwareProcessHistory history)
    {
        var softwareId = CleanText(history.SoftwareId) ?? "";
        var softwareName = CleanText(history.SoftwareName) ?? softwareId;
        var processes = (history.Processes ?? [])
            .Select(static process => NormalizeProcess(process))
            .Where(static process => !string.IsNullOrWhiteSpace(process.ProcessKey)
                && !string.IsNullOrWhiteSpace(process.ProcessName))
            .GroupBy(static process => process.ProcessKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static process => process.LastObservedAt).First())
            .OrderByDescending(static process => process.LastObservedAt)
            .ThenByDescending(static process => process.Confidence)
            .ToArray();
        processes = RetainIdentifiedProcesses(processes)
            .OrderBy(static process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static process => process.ProcessKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new GpuPlacementSoftwareProcessHistory(softwareId, softwareName, processes, history.UpdatedAt);
    }

    private static GpuPlacementObservedProcess NormalizeProcess(GpuPlacementObservedProcess process)
    {
        var processName = CleanText(process.ProcessName) ?? CleanText(process.ProcessKey) ?? "unknown";
        var processKey = CleanText(process.ProcessKey) ?? BuildProcessKey(processName, process.ExecutablePath);
        return new GpuPlacementObservedProcess(
            processKey,
            processName,
            CleanPath(process.ExecutablePath),
            CleanText(process.Architecture),
            process.FirstObservedAt == DateTimeOffset.MinValue ? process.LastObservedAt : process.FirstObservedAt,
            process.LastObservedAt,
            Math.Max(1, process.ObservationCount),
            process.LastProcessId,
            CleanEvidenceSources(process.EvidenceSources),
            (byte)Math.Clamp((int)process.Confidence, 0, 100),
            GpuGraphicsApiRoutes.IsIdentified(process.GraphicsApi) ? process.GraphicsApi : null);
    }

    private static GpuPlacementProcessObservationRequest NormalizeRequest(
        GpuPlacementProcessObservationRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("GPU 调度进程观察请求不能为空。");
        }

        var softwareId = RequireText(request.SoftwareId, "软件标识不能为空。");
        var softwareName = CleanText(request.SoftwareName) ?? softwareId;
        var processes = (request.Processes ?? [])
            .Select(static input => input with
            {
                ProcessName = CleanText(input.ProcessName) ?? "",
                ExecutablePath = CleanPath(input.ExecutablePath),
                Architecture = CleanText(input.Architecture),
                EvidenceSources = CleanEvidenceSources(input.EvidenceSources)
            })
            .Where(static input => !string.IsNullOrWhiteSpace(input.ProcessName)
                || !string.IsNullOrWhiteSpace(input.ExecutablePath))
            .ToArray();

        return new GpuPlacementProcessObservationRequest(softwareId, softwareName, processes);
    }

    private static GpuPlacementObservedProcess CreateObservedProcess(
        GpuPlacementObservedProcessInput input,
        DateTimeOffset now)
    {
        var processName = CleanText(input.ProcessName)
            ?? InferProcessName(input.ExecutablePath)
            ?? "unknown";
        var executablePath = CleanPath(input.ExecutablePath);
        var evidence = CleanEvidenceSources(input.EvidenceSources);
        var confidence = executablePath is null ? (byte)55 : (byte)85;
        if (evidence.Count > 1)
        {
            confidence = (byte)Math.Min(100, confidence + 10);
        }

        return new GpuPlacementObservedProcess(
            BuildProcessKey(processName, executablePath),
            processName,
            executablePath,
            CleanText(input.Architecture),
            now,
            now,
            1,
            input.ProcessId,
            evidence,
            confidence);
    }

    private static GpuPlacementObservedProcess MergeProcess(
        GpuPlacementObservedProcess existing,
        GpuPlacementObservedProcess observed,
        DateTimeOffset now)
    {
        return existing with
        {
            ProcessName = CleanText(observed.ProcessName) ?? existing.ProcessName,
            ExecutablePath = CleanPath(observed.ExecutablePath) ?? existing.ExecutablePath,
            Architecture = CleanText(observed.Architecture) ?? existing.Architecture,
            LastObservedAt = now,
            ObservationCount = Math.Min(int.MaxValue, existing.ObservationCount + 1),
            LastProcessId = observed.LastProcessId ?? existing.LastProcessId,
            EvidenceSources = CleanEvidenceSources(existing.EvidenceSources.Concat(observed.EvidenceSources)),
            Confidence = (byte)Math.Max(existing.Confidence, observed.Confidence),
            GraphicsApi = existing.GraphicsApi ?? observed.GraphicsApi
        };
    }

    private static bool ShouldPersistExistingProcess(
        GpuPlacementObservedProcess before,
        GpuPlacementObservedProcess after,
        DateTimeOffset now)
    {
        return !string.Equals(before.ProcessName, after.ProcessName, StringComparison.Ordinal)
            || !string.Equals(before.ExecutablePath, after.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(before.Architecture, after.Architecture, StringComparison.OrdinalIgnoreCase)
            || !before.EvidenceSources.SequenceEqual(after.EvidenceSources, StringComparer.OrdinalIgnoreCase)
            || after.Confidence != before.Confidence
            || after.GraphicsApi != before.GraphicsApi
            || now - before.LastObservedAt >= MinimumLastSeenPersistInterval;
    }

    internal static string BuildProcessKey(string? processName, string? executablePath)
    {
        var path = CleanPath(executablePath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return $"path:{ShortHash(path)}";
        }

        var name = CleanText(processName) ?? "unknown";
        return $"name:{ShortHash(name)}";
    }

    private static IEnumerable<GpuPlacementObservedProcess> RetainIdentifiedProcesses(
        IEnumerable<GpuPlacementObservedProcess> processes)
    {
        var ordered = processes.OrderByDescending(static process => process.LastObservedAt)
            .ThenByDescending(static process => process.Confidence).ToArray();
        return ordered.Where(static process => process.GraphicsApi is not null)
            .Concat(ordered.Where(static process => process.GraphicsApi is null).Take(MaxProcessesPerSoftware));
    }

    private static string? InferProcessName(string? executablePath)
    {
        var clean = CleanPath(executablePath);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return null;
        }

        return CleanText(Path.GetFileNameWithoutExtension(clean));
    }

    private static IReadOnlyList<string> CleanEvidenceSources(IEnumerable<string>? values)
    {
        var result = (values ?? [])
            .Select(static value => CleanText(value))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return result.Length > 0 ? result : ["resource-table"];
    }

    private static string RequireText(string? value, string message)
    {
        return CleanText(value) ?? throw new InvalidOperationException(message);
    }

    private static string? CleanText(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
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

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
