using System.Text.Json;
using ResourceManager.App.Domain.Migration;

namespace ResourceManager.App.Infrastructure.Migration;

public sealed partial class SoftwareDataMigrationManager
{
    public async Task<IReadOnlyList<SoftwareDataMigrationRecord>> GetRecordsAsync(CancellationToken cancellationToken)
    {
        await recordGate.WaitAsync(cancellationToken);
        try
        {
            return await LoadRecordsCoreAsync(cancellationToken);
        }
        finally
        {
            recordGate.Release();
        }
    }

    internal async Task AppendRecordsAsync(
        IReadOnlyList<SoftwareDataMigrationRecord> newRecords,
        CancellationToken cancellationToken)
    {
        await recordGate.WaitAsync(cancellationToken);
        try
        {
            var records = (await LoadRecordsCoreAsync(cancellationToken)).ToList();
            records.AddRange(newRecords);
            await SaveRecordsCoreAsync(records, cancellationToken);
        }
        finally
        {
            recordGate.Release();
        }
    }

    private async Task<IReadOnlyList<SoftwareDataMigrationRecord>> LoadRecordsCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(RecordPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<List<SoftwareDataMigrationRecord>>(
                stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("The migration records file contains no records document.");
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private async Task SaveRecordsCoreAsync(
        IReadOnlyList<SoftwareDataMigrationRecord> records,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(RecordPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{RecordPath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, records, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        NativeCore.WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, RecordPath);
    }
}
