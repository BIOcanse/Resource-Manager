using ResourceManager.App.Domain.Metrics;
using System.Collections.Frozen;
using System.Text.Json.Serialization;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.ReadWrite.ProcessFields;

public sealed record ProcessMetricFieldValue(string Field, MetricInteger? IntegerValue, double? NumericValue)
{
    public string? StringValue { get; init; }
}

/// <summary>Two accepted IO query-return reference points for this exact process creation: the return of the
/// kernel process table query that carried its record, or of its GetProcessIoCounters call.</summary>
public sealed record ProcessIoInterval(
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long BeginCounter,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long EndCounter,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long CounterFrequency)
{
    [JsonIgnore]
    public ulong? WindowMilliseconds
    {
        get
        {
            if (BeginCounter < 0 || EndCounter <= BeginCounter || CounterFrequency <= 0) return null;
            var milliseconds = (UInt128)(ulong)(EndCounter - BeginCounter) * 1000 / (ulong)CounterFrequency;
            return milliseconds <= ulong.MaxValue ? (ulong)milliseconds : null;
        }
    }
}

public sealed record ProcessMetricFieldValues(int ProcessId, string? CreationWindowsFileTime100Ns,
    IReadOnlyList<ProcessMetricFieldValue> Values)
{
    public ProcessIoInterval? IoInterval { get; init; }
    public PhysicalDiskIoScope? PhysicalDisk { get; init; }
    public ProcessGpuEngineScope? GpuEngine { get; init; }
    public ProcessGpuEngineInterval? GpuInterval { get; init; }
    public CpuLogicalProcessorResidencyScope? LogicalProcessor { get; init; }
}

public sealed record ProcessMetricFieldSourceObservation(string SourceId, DateTimeOffset? CapturedAt, long? WindowMilliseconds);

public sealed record ProcessMetricFieldSnapshot(DateTimeOffset? CapturedAt, long? WindowMilliseconds,
    IReadOnlyList<ProcessMetricFieldDefinition> Fields, IReadOnlyList<ProcessMetricFieldValues> Processes)
{
    private readonly object indexGate = new();
    private IReadOnlyList<ProcessMetricFieldValues>? indexedProcesses;
    private FrozenDictionary<(int ProcessId, string? Creation, PhysicalDiskIoScope? Disk, ProcessGpuEngineScope? Gpu, CpuLogicalProcessorResidencyScope? Cpu), ProcessMetricFieldValues>? byProcess;

    public ProcessMetricFieldValues? FindProcess(int processId, string creationWindowsFileTime100Ns, PhysicalDiskIoScope? disk = null, ProcessGpuEngineScope? gpu = null, CpuLogicalProcessorResidencyScope? logicalProcessor = null)
    {
        lock (indexGate)
        {
            if (!ReferenceEquals(indexedProcesses, Processes))
            {
                byProcess = Processes.ToFrozenDictionary(static process => (process.ProcessId, process.CreationWindowsFileTime100Ns, process.PhysicalDisk, process.GpuEngine, process.LogicalProcessor));
                indexedProcesses = Processes;
            }
            return byProcess!.GetValueOrDefault((processId, creationWindowsFileTime100Ns, disk, gpu, logicalProcessor));
        }
    }

    public IReadOnlyList<ProcessMetricFieldSourceObservation> Sources { get; init; } = [];
    public static ProcessMetricFieldSnapshot Empty(IReadOnlySet<string> selected)
        => new(null, null, selected.Select(ProcessMetricFieldRegistry.Find).OfType<ProcessMetricFieldDefinition>().ToArray(), []);

    public ProcessMetricFieldSnapshot Select(IReadOnlySet<string> selected)
    {
        var fields = Fields.Where(field => selected.Contains(field.Name)).ToArray();
        var ids = fields.Select(static field => field.SourceId).ToHashSet(StringComparer.Ordinal);
        var sources = Sources.Where(source => ids.Contains(source.SourceId)).ToArray();
        return this with
        {
            Fields = fields,
            Sources = sources,
            CapturedAt = Sources.Count == 0 ? CapturedAt : Latest(sources),
            WindowMilliseconds = Sources.Count == 0 ? WindowMilliseconds : sources.Length == 1 ? sources[0].WindowMilliseconds : null,
            Processes = Processes.Select(process => process with
                { IoInterval = fields.Any(static field => field.SourceId == "windows.process.io-interval") ? process.IoInterval : null,
                    Values = process.Values.Where(value => selected.Contains(value.Field)).ToArray() })
                .Where(static process => process.Values.Count > 0).ToArray()
        };
    }

    public ProcessMetricFieldSnapshot Clear()
        => this with { CapturedAt = null, WindowMilliseconds = null,
            Sources = Sources.Select(source => source with { CapturedAt = null, WindowMilliseconds = null }).ToArray(),
            Processes = Processes.Select(process => process with
                { IoInterval = null, GpuInterval = null, Values = process.Values.Select(value => value with { IntegerValue = null, NumericValue = null, StringValue = null }).ToArray() }).ToArray() };

    public ProcessMetricFieldSnapshot ClearUnavailable(IReadOnlySet<string> currentDatasets)
        => Merge(Fields.GroupBy(static field => field.DatasetId).Select(group =>
        {
            var part = Select(group.Select(static field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
            return currentDatasets.Contains(group.Key) ? part : part.Clear();
        }));

    public static ProcessMetricFieldSnapshot Merge(IEnumerable<ProcessMetricFieldSnapshot> parts)
    {
        var snapshots = parts.ToArray();
        var sources = snapshots.SelectMany(static part => part.Sources).ToArray();
        return new(sources.Length == 0 ? snapshots.Select(static part => part.CapturedAt).DefaultIfEmpty(null).Max() : Latest(sources),
            snapshots.Length == 1 ? snapshots[0].WindowMilliseconds : null,
            snapshots.SelectMany(static part => part.Fields).ToArray(),
            snapshots.SelectMany(static part => part.Processes).GroupBy(static row => (row.ProcessId, row.CreationWindowsFileTime100Ns, row.PhysicalDisk, row.GpuEngine, row.LogicalProcessor))
                .OrderBy(static group => group.Key.ProcessId).ThenBy(static group => group.Key.CreationWindowsFileTime100Ns, StringComparer.Ordinal)
                .Select(group => new ProcessMetricFieldValues(group.Key.ProcessId, group.Key.CreationWindowsFileTime100Ns,
                    group.SelectMany(static row => row.Values).ToArray()) { PhysicalDisk = group.Key.PhysicalDisk, GpuEngine = group.Key.GpuEngine, GpuInterval = group.First().GpuInterval, LogicalProcessor = group.Key.LogicalProcessor,
                    IoInterval = group.Select(static row => row.IoInterval).FirstOrDefault(static interval => interval is not null) }).ToArray()) { Sources = sources };
    }

    private static DateTimeOffset? Latest(IEnumerable<ProcessMetricFieldSourceObservation> sources)
        => sources.Select(static source => source.CapturedAt).DefaultIfEmpty(null).Max();
}

