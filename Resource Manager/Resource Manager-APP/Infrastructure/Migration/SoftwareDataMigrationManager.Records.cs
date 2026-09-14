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

    private async Task AppendRecordsAsync(
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
        if (!File.Exists(RecordPath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(RecordPath);
            return await JsonSerializer.DeserializeAsync<List<SoftwareDataMigrationRecord>>(stream, JsonOptions, cancellationToken)
                ?? [];
        }
        catch
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

        await using var stream = File.Create(RecordPath);
        await JsonSerializer.SerializeAsync(stream, records, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
