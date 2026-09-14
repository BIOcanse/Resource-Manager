using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView
{
    private static void ApplyManualRecords(List<SoftwareRecord> records, IReadOnlyList<ManualSoftwareRecord> manualRecords)
    {
        foreach (var manualRecord in manualRecords)
        {
            var replacedRecords = records
                .Where(record => ShouldReplaceWithManual(record, manualRecord))
                .ToArray();
            records.RemoveAll(record => replacedRecords.Contains(record));
            records.Add(ToManualSoftwareRecord(
                manualRecord,
                ResolveMergedSoftwareIdentityId(replacedRecords)));
        }
    }

    private static bool ShouldReplaceWithManual(SoftwareRecord record, ManualSoftwareRecord manualRecord)
    {
        if (!string.IsNullOrWhiteSpace(manualRecord.SourceSoftwareId)
            && record.Id.Equals(manualRecord.SourceSoftwareId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (NormalizeSoftwareKey(record.Name) == NormalizeSoftwareKey(manualRecord.Name))
        {
            return true;
        }

        return RootOverlaps(record.RootPaths, manualRecord.RootPaths);
    }
}
