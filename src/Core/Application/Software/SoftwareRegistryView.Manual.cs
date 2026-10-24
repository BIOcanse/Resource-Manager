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
            var classified = ToManualSoftwareRecord(
                manualRecord,
                ResolveMergedSoftwareIdentityId(replacedRecords)) with { HasExplicitKind = true };
            records.Add(replacedRecords.Length == 1
                ? replacedRecords[0] with
                {
                    Kind = classified.Kind, DisplayKind = classified.DisplayKind,
                    HasExplicitKind = true, SourceSoftwareId = manualRecord.SourceSoftwareId, CategorySource = null
                }
                : classified with { Id = manualRecord.SourceSoftwareId ?? manualRecord.Id });
        }
    }

    private static bool ShouldReplaceWithManual(SoftwareRecord record, ManualSoftwareRecord manualRecord)
    {
        if (!string.IsNullOrWhiteSpace(manualRecord.SourceSoftwareId))
        {
            return record.Id.Equals(manualRecord.SourceSoftwareId, StringComparison.OrdinalIgnoreCase);
        }

        // A published runtime identity already failed known-root matching.
        // Only the user's exact source identity may replace that observation.
        if (record.State == "runtime") return false;

        return record.Id.Equals(manualRecord.Id, StringComparison.OrdinalIgnoreCase);
    }
}
