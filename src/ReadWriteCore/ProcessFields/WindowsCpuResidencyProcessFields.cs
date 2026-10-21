using System.Globalization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Windows.Processes;
namespace ResourceManager.ReadWrite.ProcessFields;

/// <summary>Original attributed share in a complete closed window. Continuity is not complete thread attribution.</summary>
public static class WindowsCpuResidencyProcessFields
{
    public static ProcessMetricFieldSnapshot Project(IReadOnlyList<string> selected, ProcessMetricFieldSnapshot inventory,
        IReadOnlyList<CpuLogicalProcessorResidencyScope> scopes, CpuCoreResidencySnapshot? window)
    {
        var fields = ProcessMetricFieldRegistry.Definitions.Where(field => selected.Contains(field.Name, StringComparer.OrdinalIgnoreCase)
            && field.DatasetId == ProcessMetricDatasetIds.ObservedCpuResidency).ToArray();
        var usable = IsUsable(window); var rows = new List<ProcessMetricFieldValues>();
        foreach (var process in inventory.Processes.Where(row => row.PhysicalDisk is null && row.LogicalProcessor is null)
            .DistinctBy(row => (row.ProcessId, row.CreationWindowsFileTime100Ns)))
        {
            if (process.ProcessId <= 0 || !long.TryParse(process.CreationWindowsFileTime100Ns, NumberStyles.None,
                CultureInfo.InvariantCulture, out var birth) || birth <= 0) continue;
            var attributed = window?.FindProcess(new(process.ProcessId, birth));
            foreach (var scope in scopes)
                rows.Add(new(process.ProcessId, process.CreationWindowsFileTime100Ns,
                    fields.Select(field => new ProcessMetricFieldValue(field.Name, null, Share(window, attributed, scope))).ToArray())
                    { LogicalProcessor = scope });
        }
        return new(usable ? window!.MeasuredThrough : null, usable ? Milliseconds(window!) : null, fields, rows)
        { Sources = [new("windows.etw.cpu-residency", usable ? window!.MeasuredThrough : null, usable ? Milliseconds(window!) : null)] };
    }
    /// <summary>Scalar pure view of the exact same raw snapshot; does not materialize another full values table.</summary>
    public static FieldReading ProjectReading(FieldReference field, ProcessInstanceKey identity,
        CpuLogicalProcessorResidencyScope scope, CpuCoreResidencySnapshot? window)
    {
        if (ProcessMetricFieldRegistry.Find(field.FieldName)?.Code != ProcessMetricFieldCode.CpuResidency)
            throw new ArgumentException("Select the canonical CPU residency field.", nameof(field));
        var share = Share(window, window?.FindProcess(identity), scope);
        return new(field, share is null ? FieldObservationStatus.Unavailable : FieldObservationStatus.Current,
            share is null ? null : new FieldValue(Number: share), share is null ? null : window!.MeasuredThrough,
            "windows.etw.cpu-residency", share is null ? null : checked((ulong)Milliseconds(window!)));
    }
    private static bool IsUsable(CpuCoreResidencySnapshot? window)
        => window is not null && window.Window > TimeSpan.Zero && window.MeasuredThrough > window.MeasuredFrom
            && window.MeasuredThrough - window.MeasuredFrom == window.Window;
    private static long Milliseconds(CpuCoreResidencySnapshot window) => checked((long)window.Window.TotalMilliseconds);
    private static double? Share(CpuCoreResidencySnapshot? window, CpuProcessCoreResidency? process, CpuLogicalProcessorResidencyScope scope)
    {
        if (!IsUsable(window) || process is null || !double.IsFinite(process.ExecutionTimeMilliseconds)
            || process.ExecutionTimeMilliseconds <= 0
            || (window!.LogicalProcessorScopes.Count != 0 && !window.LogicalProcessorScopes.Contains(scope))) return null;
        var leaves = process.LogicalProcessors.Where(leaf => leaf.LogicalProcessorId == scope.LogicalProcessorId).ToArray();
        if (leaves.Length == 0) return 0; // no attributed execution on this leaf, not proof of complete attribution
        var leaf = leaves[0];
        return leaves.Length == 1 && leaf.PhysicalCoreId == scope.PhysicalCoreId
            && (string.IsNullOrEmpty(leaf.CcdId) ? null : leaf.CcdId) == scope.CcdId
            && double.IsFinite(leaf.SharePercent) && leaf.SharePercent is >= 0 and <= 100 ? leaf.SharePercent : null;
    }
}
