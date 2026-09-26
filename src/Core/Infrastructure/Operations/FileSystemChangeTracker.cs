using ResourceManager.App.Application.Operations;
using ResourceManager.App.Domain.Operations;

namespace ResourceManager.App.Infrastructure.Operations;

public sealed class FileSystemChangeTracker : IFileChangeTracker
{
    private const string FileEntryKind = "File";
    private const string DirectoryEntryKind = "Directory";

    public Task<FileInventorySnapshot> CaptureAsync(
        FileChangeTrackingScope scope,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var entries = new Dictionary<string, FileInventoryEntry>(StringComparer.OrdinalIgnoreCase);
        var roots = NormalizeRoots(scope.RootPaths, warnings);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureRoot(root, entries, warnings, options, cancellationToken);
        }

        return Task.FromResult(new FileInventorySnapshot(
            scope.OwnerId,
            DateTimeOffset.UtcNow,
            roots,
            Math.Max(scope.MaxReportedChanges, 0),
            entries,
            warnings));
    }

    public FileChangeReport Compare(
        FileInventorySnapshot before,
        FileInventorySnapshot after)
    {
        if (!before.OwnerId.Equals(after.OwnerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("文件变更追踪的前后快照不属于同一个操作。");
        }

        var changes = new List<FileChangeRecord>();
        var allPaths = before.Entries.Keys
            .Concat(after.Entries.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var path in allPaths)
        {
            before.Entries.TryGetValue(path, out var beforeEntry);
            after.Entries.TryGetValue(path, out var afterEntry);

            if (beforeEntry is null && afterEntry is not null)
            {
                changes.Add(CreateChange("Created", null, afterEntry));
            }
            else if (beforeEntry is not null && afterEntry is null)
            {
                changes.Add(CreateChange("Deleted", beforeEntry, null));
            }
            else if (beforeEntry is not null
                && afterEntry is not null
                && HasChanged(beforeEntry, afterEntry))
            {
                changes.Add(CreateChange("Modified", beforeEntry, afterEntry));
            }
        }

        var fileChanges = changes.Where(static change => change.EntryKind == FileEntryKind).ToArray();
        var driveDeltas = fileChanges
            .GroupBy(static change => ResolveDrive(change.Path), StringComparer.OrdinalIgnoreCase)
            .Select(static group => new FileChangeDriveDelta(
                group.Key,
                group.Count(),
                group.Sum(static change => change.DeltaBytes),
                group.Sum(static change => Math.Abs(change.DeltaBytes))))
            .OrderBy(static item => item.Drive, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var maxReportedChanges = after.MaxReportedChanges > 0
            ? after.MaxReportedChanges
            : before.MaxReportedChanges;
        var truncated = maxReportedChanges > 0 && changes.Count > maxReportedChanges;
        var reportedChanges = truncated
            ? changes.Take(maxReportedChanges).ToArray()
            : changes.ToArray();

        return new FileChangeReport(
            before.OwnerId,
            before.CapturedAt,
            after.CapturedAt,
            before.RootPaths.Concat(after.RootPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            changes.Count(static change => change.ChangeKind == "Created"),
            changes.Count(static change => change.ChangeKind == "Deleted"),
            changes.Count(static change => change.ChangeKind == "Modified"),
            fileChanges.Sum(static change => change.DeltaBytes),
            fileChanges.Sum(static change => Math.Abs(change.DeltaBytes)),
            driveDeltas,
            reportedChanges,
            before.Warnings.Concat(after.Warnings).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            truncated);
    }

    private static IReadOnlyList<string> NormalizeRoots(
        IReadOnlyList<string> rootPaths,
        List<string> warnings)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in rootPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim());
                var fullPath = Path.GetFullPath(expanded);
                if (seen.Add(fullPath))
                {
                    roots.Add(fullPath);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                warnings.Add($"无法规范化追踪路径：{rawPath}");
            }
        }

        return roots;
    }

    private static void CaptureRoot(
        string root,
        Dictionary<string, FileInventoryEntry> entries,
        List<string> warnings,
        EnumerationOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(root))
            {
                AddFile(root, root, entries, warnings);
                return;
            }

            if (!Directory.Exists(root))
            {
                warnings.Add($"追踪路径不存在：{root}");
                return;
            }

            AddDirectory(root, root, entries, warnings);

            foreach (var directory in Directory.EnumerateDirectories(root, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddDirectory(directory, root, entries, warnings);
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddFile(file, root, entries, warnings);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
        {
            warnings.Add($"无法扫描追踪路径：{root}");
        }
    }

    private static void AddFile(
        string path,
        string root,
        Dictionary<string, FileInventoryEntry> entries,
        List<string> warnings)
    {
        try
        {
            var info = new FileInfo(path);
            entries[path] = new FileInventoryEntry(
                info.FullName,
                root,
                FileEntryKind,
                info.Length,
                info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
        {
            warnings.Add($"无法读取文件信息：{path}");
        }
    }

    private static void AddDirectory(
        string path,
        string root,
        Dictionary<string, FileInventoryEntry> entries,
        List<string> warnings)
    {
        try
        {
            var info = new DirectoryInfo(path);
            entries[path] = new FileInventoryEntry(
                info.FullName,
                root,
                DirectoryEntryKind,
                0,
                info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
        {
            warnings.Add($"无法读取目录信息：{path}");
        }
    }

    private static bool HasChanged(FileInventoryEntry before, FileInventoryEntry after)
    {
        return !before.EntryKind.Equals(after.EntryKind, StringComparison.Ordinal)
            || before.Length != after.Length
            || before.LastWriteTimeUtc != after.LastWriteTimeUtc;
    }

    private static FileChangeRecord CreateChange(
        string changeKind,
        FileInventoryEntry? before,
        FileInventoryEntry? after)
    {
        var current = after ?? before ?? throw new InvalidOperationException("Missing file change entry.");
        var beforeLength = before?.Length ?? 0;
        var afterLength = after?.Length ?? 0;

        return new FileChangeRecord(
            changeKind,
            current.EntryKind,
            current.Path,
            current.RootPath,
            beforeLength,
            afterLength,
            current.EntryKind == FileEntryKind ? afterLength - beforeLength : 0,
            before?.LastWriteTimeUtc,
            after?.LastWriteTimeUtc);
    }

    private static string ResolveDrive(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.IsNullOrWhiteSpace(root)
            ? "unknown"
            : root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
