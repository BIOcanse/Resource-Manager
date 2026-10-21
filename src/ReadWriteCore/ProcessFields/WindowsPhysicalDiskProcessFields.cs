using System.Globalization;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.ReadWrite.ProcessFields;

/// <summary>Pure projection of the one shared DiskIO window and a verified process inventory.
/// DiskNumber remains an ETW report scope; no physical-device identity is inferred.</summary>
public static class WindowsPhysicalDiskProcessFields
{
    public static ProcessMetricFieldSnapshot Project(IReadOnlyList<string> selected,
        ProcessMetricFieldSnapshot processInventory, PhysicalDiskProcessIoWindow? window)
    {
        var names = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var definitions = ProcessMetricFieldRegistry.Definitions.Where(field => names.Contains(field.Name)
            && field.DatasetId == ProcessMetricDatasetIds.ObservedPhysicalDiskIo).ToArray();
        var complete = window is { Complete: true, WindowMilliseconds: 2000 };
        var scopes = window?.Disks.Concat(window.Processes.Select(static row => row.Disk)).Distinct().ToArray() ?? [];
        var source = window?.Processes.GroupBy(static row => (row.Disk, row.ProcessId))
            .ToDictionary(static group => group.Key, static group => group.Count() == 1 ? group.Single() : null);
        var rows = new List<ProcessMetricFieldValues>();
        foreach (var process in processInventory.Processes.Where(static row => row.PhysicalDisk is null)
            .DistinctBy(static row => (row.ProcessId, row.CreationWindowsFileTime100Ns)))
        {
            if (process.ProcessId <= 0 || !ulong.TryParse(process.CreationWindowsFileTime100Ns,
                NumberStyles.None, CultureInfo.InvariantCulture, out var birth) || birth == 0) continue;
            foreach (var disk in scopes)
            {
                string? read = null, write = null;
                if (complete)
                {
                    if (!source!.TryGetValue((disk, process.ProcessId), out var raw)) { read = "0"; write = "0"; }
                    else if (raw?.CreationWindowsFileTime100Ns == process.CreationWindowsFileTime100Ns)
                    { read = Exact(raw.ReadBytes); write = Exact(raw.WriteBytes); }
                }
                rows.Add(new(process.ProcessId, process.CreationWindowsFileTime100Ns,
                    definitions.Select(field => new ProcessMetricFieldValue(field.Name,
                        field.Code == ProcessMetricFieldCode.PhysicalDiskReadBytes ? read : write, null)).ToArray()) { PhysicalDisk = disk });
            }
        }
        return new(complete ? window!.CapturedAt : null, complete ? 2000 : null, definitions, rows)
        { Sources = [new("windows.etw.physical-disk", complete ? window!.CapturedAt : null, complete ? 2000 : null)] };
    }
    private static string? Exact(string? value)
        => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture) : null;
}
