using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.ProcessFields;

/// <summary>The canonical process dataset slots. Host prepared copies share the original publication gate.</summary>
public sealed class ProcessFieldCurrentState
{
    internal object Gate { get; }
    private Dictionary<string, ProcessMetricFieldSnapshot?> parts = new(StringComparer.OrdinalIgnoreCase);
    private DeviceInformationSnapshot processDetails = DeviceInformationSnapshot.Empty;
    private long revision;
    public ProcessFieldCurrentState() : this(new object()) { }
    internal ProcessFieldCurrentState(object gate) => Gate = gate;
    public long Revision { get { lock (Gate) return revision; } }
    public ProcessMetricFieldSnapshot[] ReadParts() { lock (Gate) return parts.Values.Where(static part => part is not null).Select(static part => part!).ToArray(); }
    public ProcessMetricFieldSnapshot? ReadPart(string dataset) { lock (Gate) return parts.GetValueOrDefault(dataset); }
    public DeviceInformationSnapshot ReadProcessDetails() { lock (Gate) return processDetails; }
    internal void SetProcessDetails(DeviceInformationSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (Gate) { processDetails = value; revision++; }
    }
    internal void SetPart(string dataset, ProcessMetricFieldSnapshot? value)
    {
        lock (Gate)
        {
            if (dataset != ProcessMetricDatasetIds.BasicMetrics && dataset != ProcessMetricDatasetIds.ObservedNetworkTraffic && dataset != ProcessMetricDatasetIds.ObservedPhysicalDiskIo && dataset != ProcessMetricDatasetIds.GpuResidency && dataset != ProcessMetricDatasetIds.ObservedCpuResidency
                && dataset != ProcessGpuResourceFieldRegistry.AllocationDatasetId) return;
            parts[dataset] = value; revision++;
        }
    }
    internal ProcessFieldCurrentState PrepareCopy()
    {
        lock (Gate) return new(Gate) { parts = new(parts, StringComparer.OrdinalIgnoreCase), processDetails = processDetails, revision = revision };
    }
    internal void CommitPrepared(ProcessFieldCurrentState prepared)
    {
        lock (Gate) { parts = prepared.parts; processDetails = prepared.processDetails; revision = prepared.revision; }
    }
    internal void Apply(IEnumerable<string> selected, ProcessMetricFieldSnapshot? sample)
    {
        lock (Gate)
        {
            foreach (var group in selected.Distinct(StringComparer.OrdinalIgnoreCase).GroupBy(name => ProcessMetricFieldRegistry.Find(name)!.DatasetId))
            {
                var names = group.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var fields = sample?.Select(names);
                if (fields is null || fields.Fields.Count != names.Count || fields.CapturedAt is null)
                    fields = (fields ?? parts.GetValueOrDefault(group.Key))?.Clear();
                SetPart(group.Key, fields);
            }
        }
    }
    internal void Fail(IEnumerable<string> selected)
    {
        lock (Gate) foreach (var group in selected.GroupBy(name => ProcessMetricFieldRegistry.Find(name)!.DatasetId))
            SetPart(group.Key, parts.GetValueOrDefault(group.Key)?.Clear());
    }
}

