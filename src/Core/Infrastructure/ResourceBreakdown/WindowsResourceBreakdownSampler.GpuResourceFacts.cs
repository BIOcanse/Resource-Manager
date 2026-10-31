using System.Globalization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal sealed record GpuResourceFactProjection(
    IReadOnlyList<ProcessMemoryFact> Facts, ProcessMetricFieldSnapshot Fields);

public sealed partial class WindowsResourceBreakdownSampler
{
    /// <summary>Projects one cached native read and the existing allocation publication.
    /// validatedEngineFields must come from WindowsPdhProcessGpuFields.Project of that same read,
    /// including its post-read process-creation check. This helper performs no collection.</summary>
    internal static GpuResourceFactProjection CreateGpuResourceFacts(
        IReadOnlyList<ProcessMetricFieldDefinition> definitions,
        IReadOnlyList<ProcessResourceSample> samples,
        IReadOnlyList<WindowsGpuAdapter> adapters,
        NativePdhReadResult? pdhRead,
        ProcessMetricFieldSnapshot? validatedEngineFields,
        GpuAllocationReading? allocations,
        HardwareMetricSnapshot? hardware,
        ProcessGpuSharedSystemMemoryRead? sharedSystemMemory = null)
    {
        var births = samples.Where(static sample => sample.ProcessId > 0 && sample.StartKey is > 0)
            .GroupBy(static sample => sample.ProcessId).Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single().StartKey!.Value);
        var exactAdapters = adapters.Where(static adapter => !adapter.IsSoftware)
            .Select(static adapter => (adapter.Index, Luid: WindowsGpuAdapterIdentity.Pack(adapter.Luid)))
            .Where(static adapter => adapter.Index >= 0 && adapter.Luid != 0).Distinct()
            .GroupBy(static adapter => adapter.Index).Where(static group => group.Count() == 1)
            .Select(static group => group.Single()).GroupBy(static adapter => adapter.Luid)
            .Where(static group => group.Count() == 1).Select(static group => group.Single()).ToHashSet();
        var facts = new List<ProcessMemoryFact>();
        var parts = new List<ProcessMetricFieldSnapshot>();
        foreach (var field in definitions.Where(ProcessGpuResourceFieldRegistry.IsBound)
            .DistinctBy(static field => field.Name, StringComparer.OrdinalIgnoreCase))
        {
            var adapterMatches = exactAdapters.Contains((field.GpuIndex!.Value, field.GpuAdapterLuid!.Value));
            var part = field.GpuEngine is not null
                ? CreateGpuEngineResourceFact(field, births, adapterMatches ? pdhRead : null, validatedEngineFields)
                : field.Code == ProcessMetricFieldCode.GpuSharedSystemMemoryBytes
                    ? CreateGpuSharedSystemMemoryFact(field, births, adapterMatches ? sharedSystemMemory : null,
                        adapterMatches ? hardware : null)
                    : CreateGpuResidentResourceFact(field, births, adapterMatches ? allocations : null,
                        adapterMatches ? hardware : null);
            facts.Add(part.Fact);
            parts.Add(part.Fields);
        }
        return new(facts, ProcessMetricFieldSnapshot.Merge(parts));
    }

    private static (ProcessMemoryFact Fact, ProcessMetricFieldSnapshot Fields) CreateGpuEngineResourceFact(
        ProcessMetricFieldDefinition field, IReadOnlyDictionary<int, long> births,
        NativePdhReadResult? read, ProcessMetricFieldSnapshot? validated)
    {
        var scope = field.GpuEngine!.Value;
        var raw = read?.Snapshot;
        var current = read?.Availability == NativePdhProviderAvailability.Available
            && raw?.GpuEngineObservation.Availability == NativePdhProviderAvailability.Available;
        var rawRows = raw?.EngineRows.Where(row => row.AdapterLuid == scope.AdapterLuid
            && row.PhysicalIndex == scope.PhysicalIndex && row.EngineIndex == scope.EngineIndex).ToArray() ?? [];
        var rawByProcess = rawRows.GroupBy(static row => row.ProcessId).Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single());
        var windows = raw?.EngineWindows.Where(window => window.AdapterLuid == scope.AdapterLuid
            && window.PhysicalIndex == scope.PhysicalIndex && window.EngineIndex == scope.EngineIndex)
            .GroupBy(static window => window.ProcessId).Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single()) ?? [];
        var classes = rawRows.Select(static row => row.EngineClass).Distinct().ToArray();
        GpuFactWindow? commonWindow = null;
        if (current && rawRows.Length > 0 && classes.Length == 1)
        {
            var distinctWindows = new HashSet<GpuFactWindow>();
            var complete = true;
            foreach (var row in rawRows)
            {
                if (!windows.TryGetValue(row.ProcessId, out var window) || !IsUsableGpuFactWindow(window))
                {
                    complete = false;
                    break;
                }
                distinctWindows.Add(GpuFactWindow.From(window));
            }
            if (complete && distinctWindows.Count == 1) commonWindow = distinctWindows.Single();
        }

        // Totals retain every native contribution, including an unknown or unreadable PID.
        // Both totals and values retain the reported number; capacity is not a clamp.
        double? total = commonWindow is not null && rawRows.All(static row => double.IsFinite(row.UsagePercent))
            ? rawRows.Sum(static row => row.UsagePercent) : null;
        if (total is { } reportedTotal && !double.IsFinite(reportedTotal)) total = null;
        var values = new List<ProcessMemoryValue>();
        var rows = new List<ProcessMetricFieldValues>();
        foreach (var original in (validated?.Processes ?? []).Where(row => row.GpuEngine == scope))
        {
            if (!births.TryGetValue(original.ProcessId, out var birth)
                || !long.TryParse(original.CreationWindowsFileTime100Ns, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var validatedBirth) || validatedBirth != birth) continue;
            var interval = original.GpuInterval;
            double? value = null;
            if (current && interval is { IsLastGood: false }
                && rawByProcess.TryGetValue((uint)original.ProcessId, out var native)
                && windows.TryGetValue((uint)original.ProcessId, out var window)
                && IsUsableGpuFactWindow(window) && GpuFactWindow.From(window) == GpuFactWindow.From(interval)
                && native.EngineClass == interval.EngineClass && double.IsFinite(native.UsagePercent)
                && original.Values.Any(candidate => candidate.Field == WindowsPdhProcessGpuFields.FieldName
                    && candidate.NumericValue is { } candidateNumber && double.IsFinite(candidateNumber)))
                value = native.UsagePercent;
            rows.Add(new(original.ProcessId, original.CreationWindowsFileTime100Ns, [new(field.Name, null, value)])
            { GpuEngine = scope, GpuInterval = value.HasValue ? interval : null });
            if (value is { } number && commonWindow is not null && commonWindow == GpuFactWindow.From(interval!))
                values.Add(new(original.ProcessId, birth, number));
        }
        var capturedAt = commonWindow is null ? (DateTimeOffset?)null
            : DateTimeOffset.FromFileTime(commonWindow.EndFileTime);
        var milliseconds = commonWindow?.Milliseconds;
        var fact = new ProcessMemoryFact(field.Name, field.ResourceFactTotalMetricId!, total, 100,
            values.Sum(static value => value.Value), values)
        {
            Unit = field.Unit,
            AdditiveAcrossProcesses = true,
            CapturedAt = capturedAt,
            WindowMilliseconds = milliseconds,
            GpuEngine = scope,
            EngineClass = classes.Length == 1 ? classes[0].ToString() : null
        };
        var fieldCapturedAt = rows.Where(static row => row.GpuInterval is not null)
            .Select(static row => (DateTimeOffset?)DateTimeOffset.FromFileTime(row.GpuInterval!.EndWindowsFileTime100Ns))
            .DefaultIfEmpty(null).Max();
        return (fact, new(fieldCapturedAt, milliseconds, [field], rows)
        { Sources = [new(field.SourceId, fieldCapturedAt, milliseconds)] });
    }

    /// <summary>One process value per validated instance that PDH reported on this adapter. A process with no
    /// PDH instance, or whose PID was reused during the read, gets no row and reads as unavailable.
    /// The total is the system shared-memory metric only when the holder's hardware snapshot already has it current.</summary>
    private static (ProcessMemoryFact Fact, ProcessMetricFieldSnapshot Fields) CreateGpuSharedSystemMemoryFact(
        ProcessMetricFieldDefinition field, IReadOnlyDictionary<int, long> births,
        ProcessGpuSharedSystemMemoryRead? read, HardwareMetricSnapshot? hardware)
    {
        var luid = field.GpuAdapterLuid!.Value;
        var current = read?.Availability == NativePdhProviderAvailability.Available;
        var capturedAt = current ? read!.ObservedAt : null;
        var values = new List<ProcessMemoryValue>();
        var rows = new List<ProcessMetricFieldValues>();
        if (current)
            foreach (var ((adapterLuid, pid, startKey), value) in read!.Bytes)
            {
                if (adapterLuid != luid || !births.TryGetValue(pid, out var birth)
                    || birth <= 0 || (ulong)birth != startKey || !double.IsFinite(value)) continue;
                values.Add(new(pid, birth, value));
                rows.Add(new(pid, birth.ToString(CultureInfo.InvariantCulture), [new(field.Name, null, value)]));
            }
        var totalId = field.ResourceFactTotalMetricId!;
        double? total = hardware is not null && TryGetCurrentHardwareMetric(hardware, totalId, out var reading)
            ? reading.NumericValue ?? (double?)(double)reading.IntegerValue!.Value.ToBigInteger() : null;
        return (new(field.Name, totalId, total, null, values.Sum(static value => value.Value), values)
        {
            Unit = field.Unit,
            AdditiveAcrossProcesses = true,
            CapturedAt = capturedAt
        }, new(capturedAt, null, [field], rows) { Sources = [new(field.SourceId, capturedAt, null)] });
    }

    private static (ProcessMemoryFact Fact, ProcessMetricFieldSnapshot Fields) CreateGpuResidentResourceFact(
        ProcessMetricFieldDefinition field, IReadOnlyDictionary<int, long> births,
        GpuAllocationReading? allocations, HardwareMetricSnapshot? hardware)
    {
        var luid = field.GpuAdapterLuid!.Value;
        var gpuIndex = field.GpuIndex!.Value;
        var capturedAt = allocations is { ObservedAtUtcTicks: > 0 }
            && allocations.ObservedAtUtcTicks <= DateTimeOffset.MaxValue.UtcTicks
                ? new DateTimeOffset(allocations.ObservedAtUtcTicks, TimeSpan.Zero) : (DateTimeOffset?)null;
        var values = new List<ProcessMemoryValue>();
        var rows = new List<ProcessMetricFieldValues>();
        if (allocations?.Adapters.TryGetValue(luid, out var amounts) == true)
            foreach (var (pid, amount) in amounts)
            {
                if (!births.TryGetValue(pid, out var birth)) continue;
                var value = field.Code switch
                {
                    ProcessMetricFieldCode.GpuResidentPrivateBytes => amount.PrivateBytes,
                    ProcessMetricFieldCode.GpuResidentSharedBytes => amount.SharedBytes,
                    ProcessMetricFieldCode.GpuResidentBytes => amount.TotalBytes,
                    _ => throw new InvalidOperationException("The bound field is not a GPU allocation quantity.")
                };
                if (!double.IsFinite(value)) continue;
                values.Add(new(pid, birth, value));
                rows.Add(new(pid, birth.ToString(CultureInfo.InvariantCulture), [new(field.Name, null, value)]));
            }
        var totalId = field.ResourceFactTotalMetricId!;
        var inventoryMatches = hardware?.GpuInventory.Adapters.Count(adapter => adapter.Index == gpuIndex
            && adapter.AdapterKey == luid) == 1;
        var matchingGpus = hardware?.Gpus.Where(candidate => candidate.Index == gpuIndex
            && (candidate.AdapterKey == luid || candidate.AdapterKey is null && inventoryMatches)).ToArray() ?? [];
        var gpu = matchingGpus.Length == 1 ? matchingGpus[0] : null;
        double? total = gpu?.HasDedicatedMemoryMetrics != false && hardware is not null && (gpu is not null || inventoryMatches)
            && TryGetCurrentHardwareMetric(hardware, totalId, out var reading) ? reading.NumericValue : null;
        // Capacity is a device quantity, independent of whether allocation attribution succeeded.
        double? capacity = gpu?.HasDedicatedMemoryMetrics != false && gpu?.TotalMemoryBytes is > 0 ? gpu.TotalMemoryBytes : null;
        return (new(field.Name, totalId, total, capacity, values.Sum(static value => value.Value), values)
        {
            Unit = field.Unit,
            AdditiveAcrossProcesses = true,
            CapturedAt = capturedAt
        }, new(capturedAt, null, [field], rows) { Sources = [new(field.SourceId, capturedAt, null)] });
    }

    private static bool IsUsableGpuFactWindow(NativePdhGpuEngineWindow window)
        => window.BeginFileTime > 0 && window.EndFileTime > window.BeginFileTime
            && window.EndFileTime <= 2650467743999999999L && window.BeginCounter > 0
            && window.EndCounter > window.BeginCounter && window.CounterFrequency > 0;

    private sealed record GpuFactWindow(long BeginFileTime, long EndFileTime,
        long BeginCounter, long EndCounter, long Frequency)
    {
        internal long? Milliseconds
        {
            get
            {
                var milliseconds = (UInt128)(ulong)(EndCounter - BeginCounter) * 1000 / (ulong)Frequency;
                return milliseconds <= long.MaxValue ? (long)milliseconds : null;
            }
        }
        internal static GpuFactWindow From(NativePdhGpuEngineWindow window)
            => new(window.BeginFileTime, window.EndFileTime, window.BeginCounter, window.EndCounter, window.CounterFrequency);
        internal static GpuFactWindow From(ProcessGpuEngineInterval interval)
            => new(interval.BeginWindowsFileTime100Ns, interval.EndWindowsFileTime100Ns,
                interval.BeginCounter, interval.EndCounter, interval.CounterFrequency);
    }
}
