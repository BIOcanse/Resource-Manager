using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.ReadWrite;

namespace ResourceManager.ReadWrite.ProcessFields;

/// <summary>Uses the original process dataset publications and demand owner.
/// Only confirmed PID/creation identities enter the public field directory.</summary>
public sealed class ProcessFieldReadWriteBackend : IReadWriteBackend
{
    private readonly Func<ProcessMetricFieldSnapshot[]> readParts;
    private readonly Func<string, ProcessMetricFieldSnapshot?> readPart;
    private readonly Func<long> readRevision;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ProcessMetricFieldSnapshot>> collect;
    internal ProcessFieldReadWriteBackend(Func<ProcessMetricFieldSnapshot[]> readParts,
        Func<string, ProcessMetricFieldSnapshot?> readPart, Func<long> revision,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessMetricFieldSnapshot>> collect)
    { this.readParts = readParts; this.readPart = readPart; readRevision = revision; this.collect = collect; }
    private readonly object gate = new();
    private long revision = -1;
    private ReadWriteCatalog catalog = new([], []);
    private FrozenDictionary<FieldReference, Selection> bindings = FrozenDictionary<FieldReference, Selection>.Empty;

    public ReadWriteCatalog Catalog { get { lock (gate) { Refresh(); return catalog; } } }

    public async ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken)
    {
        var basic = ProcessMetricFieldRegistry.Definitions.Where(static field => field.DatasetId == ProcessMetricDatasetIds.BasicMetrics);
        await collect(basic.Select(static field => field.Name).ToArray(), cancellationToken);
        return Catalog;
    }

    public FieldReading ReadCached(FieldReference field)
    {
        Selection item;
        lock (gate) { Refresh(); item = bindings.GetValueOrDefault(field)
            ?? throw new ArgumentException("Unknown process creation field.", nameof(field)); }
        return Project(item, readPart(item.Definition.DatasetId));
    }

    public async ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken cancellationToken)
    {
        var selected = Select(fields);
        var result = await collect(selected.Select(static item => item.Definition.Name).ToArray(), cancellationToken);
        return [.. selected.Select(item => Project(item, result))];
    }

    public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken cancellationToken)
        => ValueTask.FromResult(new FieldWriteResult(field, "unsupported", "field-write-not-bound"));

    internal ImmutableArray<Selection> Select(ImmutableArray<FieldReference> fields)
    {
        lock (gate)
        {
            Refresh();
            return [.. fields.Select(field => bindings.TryGetValue(field, out var selected) ? selected
                : throw new ArgumentException("Unknown process creation field.", nameof(fields)))];
        }
    }

    private void Refresh()
    {
        var nextRevision = readRevision();
        if (revision == nextRevision) return;
        var next = new Dictionary<FieldReference, Selection>();
        var devices = new List<DeviceDescriptor>();
        foreach (var process in readParts().SelectMany(static part => part.Processes)
            .DistinctBy(static process => (process.ProcessId, process.CreationWindowsFileTime100Ns, process.PhysicalDisk, process.GpuEngine, process.LogicalProcessor)))
        {
            if (process.ProcessId <= 0 || !long.TryParse(process.CreationWindowsFileTime100Ns, NumberStyles.None, CultureInfo.InvariantCulture, out var creation) || creation <= 0) continue;
            var identity = $"windows-process|{process.ProcessId}:{creation}";
            if (process.PhysicalDisk is { } disk)
                identity += $"|etw-disk:{disk.SessionGeneration}:{disk.DiskNumber}";
            if (process.GpuEngine is { } engine) identity += $"|pdh-gpu:{engine.AdapterLuid:x16}:{engine.PhysicalIndex}:{engine.EngineIndex}";
            if (process.LogicalProcessor is { } cpu) identity += $"|logical-cpu:{cpu.LogicalProcessorId}";
            devices.Add(new(identity, "process", null));
            foreach (var definition in ProcessMetricFieldRegistry.Definitions.Where(definition =>
                (definition.DatasetId == ProcessMetricDatasetIds.ObservedPhysicalDiskIo) == (process.PhysicalDisk is not null)
                && (definition.DatasetId == ProcessMetricDatasetIds.GpuResidency) == (process.GpuEngine is not null)
                && (definition.DatasetId == ProcessMetricDatasetIds.ObservedCpuResidency) == (process.LogicalProcessor is not null)))
            {
                var reference = new FieldReference(identity, definition.Name);
                next.Add(reference, new(reference, process.ProcessId, creation.ToString(CultureInfo.InvariantCulture), definition, process.PhysicalDisk, process.GpuEngine, process.LogicalProcessor));
            }
        }
        if (next.Count != bindings.Count || next.Any(pair => !bindings.TryGetValue(pair.Key, out var old) || old != pair.Value))
        {
            bindings = next.ToFrozenDictionary();
            catalog = new(devices, next.Values.Select(item => new FieldCapability(item.Field,
                new(item.Definition.Name, item.Definition.Unit, item.Definition.ValueKind, item.Definition.Meaning),
                Supported(item.Definition) ? new(true, null, item.Definition.DatasetId == ProcessMetricDatasetIds.GpuResidency ? ["native.core", "windows.pdh.gpu-engine", "native.pdh.engine-window"] : []) : OperationSupport.Unsupported("process-private-working-set-unsupported"),
                OperationSupport.Unsupported("field-write-not-bound"), item.Definition.SourceId)));
        }
        revision = nextRevision;
    }

    private static bool Supported(ProcessMetricFieldDefinition field)
        => field.Code != ProcessMetricFieldCode.PrivateWorkingSet || WindowsProcessResidentMemory.SupportsPrivateWorkingSet;

    internal static FieldReading Project(Selection selected, ProcessMetricFieldSnapshot? snapshot)
    {
        if (!Supported(selected.Definition)) return new(selected.Field, FieldObservationStatus.Unsupported, null, null, selected.Definition.SourceId);
        var row = snapshot?.FindProcess(selected.ProcessId, selected.Creation, selected.PhysicalDisk, selected.GpuEngine, selected.LogicalProcessor);
        var value = row?.Values.FirstOrDefault(value => value.Field == selected.Definition.Name);
        var observed = snapshot?.Sources.FirstOrDefault(source => source.SourceId == selected.Definition.SourceId);
        var payload = selected.Definition.ValueKind switch
        {
            "string" => value?.StringValue is { } text ? new FieldValue(Text: text) : null,
            "unsigned64" or "unsigned128" => value?.IntegerValue is { } integer ? new FieldValue(Integer: integer) : null,
            "float64" => value?.NumericValue is { } number && double.IsFinite(number) ? new FieldValue(Number: number) : null,
            _ => null
        };
        if (selected.GpuEngine is not null)
            return new(selected.Field, payload is null || row?.GpuInterval is null ? FieldObservationStatus.Unavailable :
                row.GpuInterval.IsLastGood ? FieldObservationStatus.Partial : FieldObservationStatus.Current,
                row?.GpuInterval is null ? null : payload,
                payload is null || row?.GpuInterval is null ? null : DateTimeOffset.FromFileTime(row.GpuInterval.EndWindowsFileTime100Ns),
                selected.Definition.SourceId, payload is null || row?.GpuInterval is null ? null :
                (ulong)((row.GpuInterval.EndCounter - row.GpuInterval.BeginCounter) * 1000d / row.GpuInterval.CounterFrequency));
        if (selected.Definition.SourceId == "windows.process.io-interval")
            return new(selected.Field, payload is null || row?.IoInterval is null ? FieldObservationStatus.Unavailable : FieldObservationStatus.Current,
                row?.IoInterval is null ? null : payload, payload is null ? null : observed?.CapturedAt ?? snapshot?.CapturedAt,
                selected.Definition.SourceId, payload is null ? null : row?.IoInterval?.WindowMilliseconds);
        return new(selected.Field, payload is null ? FieldObservationStatus.Unavailable : FieldObservationStatus.Current, payload,
            payload is null ? null : observed?.CapturedAt ?? snapshot?.CapturedAt, selected.Definition.SourceId,
            payload is not null && observed?.WindowMilliseconds is >= 0 ? (ulong)observed.WindowMilliseconds.Value : null);
    }

    internal sealed record Selection(FieldReference Field, int ProcessId, string Creation, ProcessMetricFieldDefinition Definition, PhysicalDiskIoScope? PhysicalDisk = null, ProcessGpuEngineScope? GpuEngine = null, CpuLogicalProcessorResidencyScope? LogicalProcessor = null);
}

