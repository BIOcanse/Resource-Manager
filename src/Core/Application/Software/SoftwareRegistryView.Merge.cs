using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView
{
    private static SoftwareRecord MergeRecords(IReadOnlyList<SoftwareRecord> records)
    {
        var first = records[0];
        if (records.Count == 1)
        {
            return first;
        }

        return first with
        {
            Sources = records.SelectMany(static record => record.Sources).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            RootPaths = records.SelectMany(static record => record.RootPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Kind = ResolveMergedKind(records),
            DisplayKind = ResolveMergedDisplayKind(records),
            Message = string.Join(" | ", records.Select(static record => record.Message).Where(static item => !string.IsNullOrWhiteSpace(item))),
            RequiresRootPathConfirmation = records.Any(static record => record.RequiresRootPathConfirmation),
            IdentityConfirmed = records.Any(static record => record.IdentityConfirmed),
            SuggestedRootPaths = records
                .SelectMany(static record => record.SuggestedRootPaths ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ExecutablePaths = records
                .SelectMany(static record => record.ExecutablePaths ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ManagementRole = ResolveMergedManagementRole(records),
            SoftwareIdentityId = ResolveMergedSoftwareIdentityId(records),
            Issues = records
                .SelectMany(static record => record.Issues ?? [])
                .GroupBy(static issue => issue.Id, StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToArray()
        };
    }

    private static string ResolveMergedKind(IReadOnlyList<SoftwareRecord> records)
    {
        return records
            .OrderBy(static record => KindSort(record.Kind))
            .First()
            .Kind;
    }

    private static string ResolveMergedDisplayKind(IReadOnlyList<SoftwareRecord> records)
    {
        return records
            .OrderBy(static record => KindSort(record.Kind))
            .First()
            .DisplayKind;
    }

    private static int KindSort(string kind)
    {
        return kind switch
        {
            SoftwareKinds.Adapted => 0,
            SoftwareKinds.Controlled => 1,
            SoftwareKinds.Managed => 1,
            SoftwareKinds.Game => 2,
            SoftwareKinds.HighPerformance => 3,
            SoftwareKinds.Other => 4,
            _ => 9
        };
    }

    private static string? ResolveMergedManagementRole(
        IReadOnlyList<SoftwareRecord> records)
    {
        if (records.Any(static record =>
            record.ManagementRole == SoftwareManagementRoles.Dependency))
        {
            return SoftwareManagementRoles.Dependency;
        }

        return records.Any(static record =>
            record.ManagementRole == SoftwareManagementRoles.Support)
            ? SoftwareManagementRoles.Support
            : null;
    }

    private static string? ResolveMergedSoftwareIdentityId(IReadOnlyList<SoftwareRecord> records)
    {
        var values = records
            .Select(static record => record.SoftwareIdentityId)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return values.Length == 1 ? values[0] : null;
    }
}
