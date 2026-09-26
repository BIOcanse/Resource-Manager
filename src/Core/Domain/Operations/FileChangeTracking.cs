namespace ResourceManager.App.Domain.Operations;

public sealed record FileChangeTrackingScope(
    string OwnerId,
    IReadOnlyList<string> RootPaths,
    int MaxReportedChanges = 2000);

public sealed record FileInventorySnapshot(
    string OwnerId,
    DateTimeOffset CapturedAt,
    IReadOnlyList<string> RootPaths,
    int MaxReportedChanges,
    IReadOnlyDictionary<string, FileInventoryEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record FileInventoryEntry(
    string Path,
    string RootPath,
    string EntryKind,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

public sealed record FileChangeReport(
    string OwnerId,
    DateTimeOffset BeforeCapturedAt,
    DateTimeOffset AfterCapturedAt,
    IReadOnlyList<string> RootPaths,
    int CreatedCount,
    int DeletedCount,
    int ModifiedCount,
    long NetBytes,
    long TotalChangedBytes,
    IReadOnlyList<FileChangeDriveDelta> DriveDeltas,
    IReadOnlyList<FileChangeRecord> Changes,
    IReadOnlyList<string> Warnings,
    bool Truncated);

public sealed record FileChangeRecord(
    string ChangeKind,
    string EntryKind,
    string Path,
    string RootPath,
    long BeforeLength,
    long AfterLength,
    long DeltaBytes,
    DateTimeOffset? BeforeLastWriteTimeUtc,
    DateTimeOffset? AfterLastWriteTimeUtc);

public sealed record FileChangeDriveDelta(
    string Drive,
    int ChangeCount,
    long NetBytes,
    long TotalChangedBytes);
