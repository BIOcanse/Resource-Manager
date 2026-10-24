using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView
{
    private static void IncludeRuntimeSoftware(
        List<SoftwareRecord> records,
        IReadOnlyList<RuntimeSoftwareAttribution> runtimeSoftware)
    {
        foreach (var runtime in runtimeSoftware)
        {
            var index = records.FindIndex(record => record.Id.Equals(runtime.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var record = records[index];
                if (runtime.CategorySource is not null && !record.HasExplicitKind)
                {
                    records[index] = record with
                    {
                        Kind = runtime.Kind, DisplayKind = runtime.DisplayKind,
                        CategorySource = runtime.CategorySource
                    };
                }
                continue;
            }

            if (runtime.Kind == SoftwareKinds.Unattributed) continue;

            records.Add(new SoftwareRecord(
                runtime.Id, runtime.Name, runtime.Kind, runtime.DisplayKind,
                "runtime", [runtime.Kind], runtime.RootPaths, string.Empty,
                NoUninstall(BackendMessageCodes.Software.NoUninstallUnknownRoot), null,
                CategorySource: runtime.CategorySource));
        }
    }
}
