using System.Text.Json;
using ResourceManager.App.Application.CpuMonopolyProtection;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.CpuMonopolyProtection;

// This host adapter only commits/projects records. Native decisions supply all
// action/lifecycle transitions; no age eviction and no priority restoration.
public sealed partial class CpuMonopolyProtectionService
{
    private sealed record StoredRecords(int Version, int UnrecordedRecordCount, IReadOnlyList<CpuMonopolyActionRecord> Records);
    private static readonly JsonSerializerOptions RecordJsonOptions = new(JsonSerializerDefaults.Web)
        { RespectRequiredConstructorParameters = true };
    private IReadOnlyList<CpuMonopolyActionRecord> records = [];
    private int unrecordedRecordCount;
    private bool restored;
    private string RecordsPath => Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData", "CpuMonopolyProtection", "action-records.json");

    public int ClearHistory()
    {
        lock (gate)
        {
            RestoreRecordsOnce();
            var next = records.Where(record => record.State == "active").ToArray();
            var removed = records.Count - next.Length;
            SaveRecords(next, 0);
            records = next;
            unrecordedRecordCount = 0;
            Publish([], true);
            return removed;
        }
    }
    private void AppendRecord(CpuMonopolyActionRecord record)
    {
        var full = records.Count >= settings.RecordCapacity;
        var next = full ? records : new[] { record }.Concat(records).ToArray();
        var omitted = full ? checked(unrecordedRecordCount + 1) : unrecordedRecordCount;
        try { SaveRecords(next, omitted); records = next; unrecordedRecordCount = omitted; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { logger.LogError(error, "CPU monopoly protection could not persist its action record."); }
    }
    private void EndRecord(int pid, string key, DateTimeOffset at, string reason, List<CpuMonopolyProtectionEvent> events)
    {
        var old = records.FirstOrDefault(record => record.ProcessId == pid && record.ProcessStartKey == key && record.State == "active");
        if (old is null) return;
        var ended = old with { State = "history", EndedAt = at, EndReason = reason };
        var next = records.Select(record => ReferenceEquals(record, old) ? ended : record).ToArray();
        try
        {
            SaveRecords(next, unrecordedRecordCount);
            records = next;
            events.Add(new("action-ended", reason, ended));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { logger.LogError(error, "CPU monopoly protection could not persist the action ending."); }
    }
    private void RestoreRecordsOnce()
    {
        if (restored) return;
        restored = true;
        if (!persistence.MutablePersistenceEnabled) return;
        try
        {
            using var stream = WindowsNativeAtomicFileCommitter.OpenCommittedRead(RecordsPath);
            var stored = JsonSerializer.Deserialize<StoredRecords>(stream, RecordJsonOptions);
            if (stored is null || stored.Version != 1 || stored.UnrecordedRecordCount < 0 || stored.Records is null
                || stored.Records.Count > Domain.Settings.CpuMonopolyProtectionSettings.MaximumRecordCapacity
                || stored.Records.Any(record => record is null || string.IsNullOrWhiteSpace(record.ActionId)
                    || record.ProcessId <= 0 || !ulong.TryParse(record.ProcessStartKey, out var key) || key == 0
                    || record.State is not ("active" or "history") || record.Outcome is not ("normalized" or "failed"))
                || stored.Records.Select(record => record.ActionId).Distinct().Count() != stored.Records.Count)
                throw new InvalidDataException("invalid-cpu-monopoly-action-records");
            records = stored.Records.ToArray();
            unrecordedRecordCount = stored.UnrecordedRecordCount;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { logger.LogError(error, "CPU monopoly protection could not restore records from {Path}.", RecordsPath); }
    }
    private void SaveRecords(IReadOnlyList<CpuMonopolyActionRecord> next, int omitted)
    {
        if (!persistence.MutablePersistenceEnabled) return;
        Directory.CreateDirectory(Path.GetDirectoryName(RecordsPath)!);
        var temporary = RecordsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new StoredRecords(1, omitted, next), RecordJsonOptions);
                stream.Flush(flushToDisk: true);
            }
            WindowsNativeAtomicFileCommitter.CommitReplace(temporary, RecordsPath);
        }
        finally { WindowsNativeAtomicFileCommitter.DeleteExact(temporary); }
    }
}
