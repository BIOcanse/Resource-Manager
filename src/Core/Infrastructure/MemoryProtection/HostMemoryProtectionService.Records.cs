using System.Text.Json;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

// Action records are history report entries (docs/report_lifecycle_2026-10-28.md): persisted, never evicted and removed
// only by clearing history. A full report still acts and notifies, and counts the records it could not write.
public sealed partial class HostMemoryProtectionService
{
    private static readonly JsonSerializerOptions RecordJsonOptions = new(JsonSerializerDefaults.Web)
        { RespectRequiredConstructorParameters = true };
    private sealed record StoredRecords(int Version, int UnrecordedRecordCount, IReadOnlyList<HostMemoryActionRecord> Records);
    // Newest first. Every change replaces the whole list, so a published status shares it instead of copying it.
    private IReadOnlyList<HostMemoryActionRecord> records = [];
    private readonly List<HostMemoryActionRecord> appendedRecords = [];
    private int unrecordedRecordCount;
    private bool recordsRestored;
    private bool recordsReplaced;
    private string RecordsPath => Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData", "MemoryProtection", "action-records.json");

    private int RecordCapacity => plans.Current.HostManager.HostMemoryProtectionRecords?.RecordCapacity
        ?? throw new InvalidOperationException("The host memory protection record capacity is unavailable.");

    // Every record is history, so clearing history removes them all. Slice files stay; the user handles them.
    public int ClearHistory()
    {
        int removed;
        lock (gate)
        {
            RestoreRecordsOnce();
            removed = records.Count;
            if (removed > 0 || unrecordedRecordCount > 0)
            {
                SaveRecords([], 0);
                records = [];
                unrecordedRecordCount = 0;
                recordsReplaced = true;
            }
        }
        PublishStatus();
        return removed;
    }

    // Writes one action's records together, then posts its one notification. A full report writes nothing new
    // and counts it; a failed write leaves the published records unchanged. Neither stops the notification.
    private void CompleteAction(string actionId, HostMemoryTreeRoot? root, IReadOnlyList<HostMemoryActionRecord> written)
    {
        lock (gate)
        {
            RestoreRecordsOnce();
            var room = Math.Max(0, RecordCapacity - records.Count);
            var kept = written.Count <= room ? written : written.Take(room).ToArray();
            var next = kept.Count == 0 ? records : kept.Reverse().Concat(records).ToArray();
            var unrecorded = unrecordedRecordCount + written.Count - kept.Count;
            try
            {
                SaveRecords(next, unrecorded);
                records = next;
                unrecordedRecordCount = unrecorded;
                appendedRecords.AddRange(kept);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogError(error, "Host memory protection could not write action records to {Path}.", RecordsPath);
            }
        }
        PublishStatus();
        var bytes = 0UL;
        foreach (var record in written) bytes += record.BytesWritten;
        // A single process target is its one record; a tree names its root.
        notifications.Post(NotificationKinds.MemoryProtectionAction.Name, NotificationFacts.Of(new MemoryProtectionActionNotificationFacts(
            actionId, root is null ? "process" : "processTree", root is null ? written[0].Name : root.Name,
            root?.ProcessId ?? written[0].ProcessId, written.Count, bytes)));
    }

    // Caller holds gate.
    private void RestoreRecordsOnce()
    {
        if (recordsRestored) return;
        recordsRestored = true;
        if (!persistence.MutablePersistenceEnabled) return;
        try
        {
            using var stream = WindowsNativeAtomicFileCommitter.OpenCommittedRead(RecordsPath);
            var stored = JsonSerializer.Deserialize<StoredRecords>(stream, RecordJsonOptions);
            if (stored?.Version != 1 || stored.Records is null || stored.UnrecordedRecordCount < 0
                || stored.Records.Count > HostMemoryProtectionRecordOptions.MaximumRecordCapacity
                || stored.Records.Any(record => !ValidRecord(record))
                || stored.Records.Select(record => (record.ActionId, record.ProcessId, record.ProcessStartKey)).Distinct().Count()
                    != stored.Records.Count)
                throw new InvalidDataException("The stored host memory protection action records are incomplete or inconsistent.");
            // A smaller configured capacity never discards records already written; it only stops new ones.
            records = stored.Records.ToArray();
            unrecordedRecordCount = stored.UnrecordedRecordCount;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(error, "Host memory protection could not restore action records from {Path}.", RecordsPath);
        }
    }

    private static bool ValidRecord(HostMemoryActionRecord? record)
        => record is not null && !string.IsNullOrWhiteSpace(record.ActionId) && record.ProcessId >= 0
            && !string.IsNullOrEmpty(record.ProcessStartKey) && record.Name is not null && record.DumpPaths is not null
            && !string.IsNullOrWhiteSpace(record.Outcome) && record.SelectionBasis is not null && Enum.IsDefined(record.State)
            && record.TargetKind is "process" or "processTree" && (record.TargetKind == "processTree") == (record.TreeRoot is not null);

    private void SaveRecords(IReadOnlyList<HostMemoryActionRecord> value, int unrecorded)
    {
        if (!persistence.MutablePersistenceEnabled) return;
        var path = RecordsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new StoredRecords(1, unrecorded, value), RecordJsonOptions);
                stream.Flush(flushToDisk: true);
            }
            WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, path);
        }
        finally { WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath); }
    }
}
