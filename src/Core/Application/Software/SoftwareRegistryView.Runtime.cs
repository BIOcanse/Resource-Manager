using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView
{
    public static IReadOnlyList<SoftwareRecord> IncludeRuntimeSoftware(
        IReadOnlyList<SoftwareRecord> software,
        IReadOnlyList<RuntimeSoftwareAttribution> runtimeSoftware,
        IReadOnlyList<ManualSoftwareRecord> manualRecords)
    {
        var records = software.ToList();
        foreach (var runtime in runtimeSoftware)
        {
            if (runtime.Kind is not (SoftwareKinds.RuntimePackage or SoftwareKinds.RuntimeProduct or SoftwareKinds.RuntimeRoot)
                || software.Any(record => record.Id.Equals(runtime.Id, StringComparison.OrdinalIgnoreCase)
                    || NormalizeSoftwareKey(record.Name) == NormalizeSoftwareKey(runtime.Name)
                    || RootOverlaps(runtime.RootPaths, record.RootPaths)))
            {
                continue;
            }

            records.Add(new SoftwareRecord(
                runtime.Id, runtime.Name, runtime.Kind, runtime.DisplayKind,
                "runtime", [runtime.Kind], runtime.RootPaths, string.Empty,
                NoUninstall(BackendMessageCodes.Software.NoUninstallUnknownRoot), null));
        }

        ApplyManualRecords(records, manualRecords);
        return records.GroupBy(static record => record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => MergeRecords(group.ToArray()))
            .OrderBy(static record => KindSort(record.Kind))
            .ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
