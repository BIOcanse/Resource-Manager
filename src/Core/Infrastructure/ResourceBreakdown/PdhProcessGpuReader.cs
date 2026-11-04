using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed class PdhProcessGpuReader
{
    private static readonly WindowsProcessBasicMetricReader BasicMetricReader = new();
    internal ResourceManager.ReadWrite.ProcessFields.ProcessMetricFieldSnapshot ReadResidencyFields(
        ResourceManager.ReadWrite.ProcessFields.ProcessMetricFieldSnapshot inventory)
        => ResourceManager.ReadWrite.ProcessFields.WindowsPdhProcessGpuFields.Read(snapshotSource, inventory);
    private readonly INativePdhSnapshotSource snapshotSource;
    internal INativePdhSnapshotSource SnapshotSource => snapshotSource;
    internal static ResourceManager.ReadWrite.ProcessFields.ProcessMetricFieldSnapshot ProjectResidencyFields(
        NativePdhReadResult read, ResourceManager.ReadWrite.ProcessFields.ProcessMetricFieldSnapshot inventory)
        => ResourceManager.ReadWrite.ProcessFields.WindowsPdhProcessGpuFields.Project(read, inventory,
            static pid => BasicMetricReader.ReadCurrentCreationFileTime(pid));
    private readonly Func<IReadOnlyCollection<ProcessInstanceKey>, IReadOnlySet<ProcessInstanceKey>>
        validateCurrentProcessInstances;
    // Null only for the parameterless source-less reader, matching its unavailable native snapshot source.
    private readonly PdhGpuProcessSharedMemoryReader? sharedSystemMemoryReader;

    public PdhProcessGpuReader()
        : this(UnavailableNativePdhSnapshotSource.Instance, ReadCurrentProcessInstances, null)
    {
    }

    /// <summary>The production route: the native PDH collector plus the managed per-process
    /// shared-memory reader, which opens its PDH query only inside <see cref="ReadSharedSystemMemory"/>.</summary>
    internal PdhProcessGpuReader(INativePdhSnapshotSource snapshotSource)
        : this(snapshotSource, ReadCurrentProcessInstances,
            new PdhGpuProcessSharedMemoryReader())
    {
    }

    internal PdhProcessGpuReader(
        INativePdhSnapshotSource snapshotSource,
        Func<IReadOnlyCollection<ProcessInstanceKey>, IReadOnlySet<ProcessInstanceKey>>
            validateCurrentProcessInstances)
        : this(snapshotSource, validateCurrentProcessInstances, null)
    {
    }

    internal PdhProcessGpuReader(
        INativePdhSnapshotSource snapshotSource,
        Func<IReadOnlyCollection<ProcessInstanceKey>, IReadOnlySet<ProcessInstanceKey>>
            validateCurrentProcessInstances,
        PdhGpuProcessSharedMemoryReader? sharedSystemMemoryReader)
    {
        this.snapshotSource = snapshotSource;
        this.validateCurrentProcessInstances = validateCurrentProcessInstances;
        this.sharedSystemMemoryReader = sharedSystemMemoryReader;
    }

    /// <summary>Reads Windows shared GPU memory for the given adapters and binds each PID to the exact
    /// expected process instance. Identity is validated after the PDH read, so a PID reused during
    /// the read cannot claim the value. Called only when a subscribed field selects an adapter.</summary>
    internal ProcessGpuSharedSystemMemoryRead ReadSharedSystemMemory(
        IReadOnlyCollection<ulong> adapterLuids,
        IReadOnlyCollection<ProcessInstanceKey> expectedProcessInstances)
    {
        ArgumentNullException.ThrowIfNull(adapterLuids);
        ArgumentNullException.ThrowIfNull(expectedProcessInstances);
        if (adapterLuids.Count == 0) return ProcessGpuSharedSystemMemoryRead.NotRequested;
        if (sharedSystemMemoryReader is null) return ProcessGpuSharedSystemMemoryRead.NoReader;
        var read = sharedSystemMemoryReader.Read(adapterLuids);
        var bytes = new Dictionary<(ulong AdapterLuid, int ProcessId, ulong ProcessStartKey), double>();
        if (read.ProviderAvailability == NativePdhProviderAvailability.Available)
        {
            var stableProcesses = CreateStableProcessMap(expectedProcessInstances);
            foreach (var ((luid, processId), value) in read.SharedUsageBytes)
                if (stableProcesses.TryGetValue(processId, out var identity))
                    bytes[(luid, processId, checked((ulong)identity.StartKey))] = value;
        }
        return new(read.ProviderAvailability, read.ResultCode, read.ObservedAt, bytes);
    }

    internal NativePdhProviderAvailability ProviderAvailability { get; private set; } = NativePdhProviderAvailability.NotRequested;

    internal SchedulingProcessGpuRead ReadSchedulingSnapshot(
        SchedulingGpuInventorySnapshot inventory,
        IReadOnlyCollection<ProcessInstanceKey> expectedProcessInstances,
        bool includeDedicatedMemory = true)
    {
        ArgumentNullException.ThrowIfNull(expectedProcessInstances);
        if (!inventory.IsCurrentComplete())
        {
            ProviderAvailability = NativePdhProviderAvailability.NotRequested;
            return SchedulingProcessGpuRead.NotRequested;
        }

        var read = snapshotSource.Read(inventory.TopologyFingerprint);
        ProviderAvailability = read.Availability;
        var snapshot = read.Snapshot;
        var usageStatus = ObservationStatus(
            EffectiveDomainAvailability(
                snapshot?.GpuEngineObservation.Availability,
                read.Availability));
        var memoryStatus = ObservationStatus(
            EffectiveDomainAvailability(
                snapshot?.GpuMemoryObservation.Availability,
                read.Availability));
        if (snapshot is null)
        {
            return new SchedulingProcessGpuRead(
                usageStatus,
                memoryStatus,
                0,
                0,
                0,
                0,
                new Dictionary<(ulong AdapterKey, int ProcessId, ulong ProcessStartKey), double>(),
                new Dictionary<(ulong AdapterKey, int ProcessId, ulong ProcessStartKey), double>());
        }

        var stableProcesses = CreateStableProcessMap(expectedProcessInstances);
        var usage = HasPayload(usageStatus)
            ? NativePdhGpuUsage.ByProcess(snapshot.EngineRows.Where(row =>
                row.ProcessId is > 0 and <= int.MaxValue
                && stableProcesses.TryGetValue((int)row.ProcessId, out var identity)
                && snapshot.EngineWindows.Count(window => window.AdapterLuid == row.AdapterLuid
                    && window.ProcessId == row.ProcessId && window.PhysicalIndex == row.PhysicalIndex
                    && window.EngineIndex == row.EngineIndex && window.BeginFileTime >= identity.StartKey
                    && window.EndFileTime > window.BeginFileTime && window.BeginCounter > 0
                    && window.EndCounter > window.BeginCounter && window.CounterFrequency > 0) == 1))
                .Where(pair => pair.Key.ProcessId is > 0 and <= int.MaxValue
                    && stableProcesses.ContainsKey((int)pair.Key.ProcessId))
                .ToDictionary(
                    pair => (pair.Key.AdapterLuid, ProcessId: (int)pair.Key.ProcessId,
                        ProcessStartKey: checked((ulong)stableProcesses[(int)pair.Key.ProcessId].StartKey)),
                    pair => pair.Value)
            : new Dictionary<(ulong AdapterLuid, int ProcessId, ulong ProcessStartKey), double>();
        var dedicatedMemory = includeDedicatedMemory && HasPayload(memoryStatus)
            ? snapshot.MemoryRows
                .Where(row => row.ProcessId is > 0 and <= int.MaxValue
                    && stableProcesses.ContainsKey((int)row.ProcessId))
                .GroupBy(row => (
                    row.AdapterLuid,
                    ProcessId: (int)row.ProcessId,
                    ProcessStartKey: checked((ulong)stableProcesses[(int)row.ProcessId].StartKey)))
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Sum(static row => Sanitize(row.DedicatedBytes)))
            : new Dictionary<(ulong AdapterLuid, int ProcessId, ulong ProcessStartKey), double>();
        return new SchedulingProcessGpuRead(
            usageStatus,
            memoryStatus,
            snapshot.GpuEngineObservation.Generation,
            snapshot.GpuEngineObservation.ObservedAt?.UtcTicks ?? 0,
            snapshot.GpuMemoryObservation.Generation,
            snapshot.GpuMemoryObservation.ObservedAt?.UtcTicks ?? 0,
            usage,
            dedicatedMemory);
    }

    private IReadOnlyDictionary<int, ProcessInstanceKey> CreateStableProcessMap(
        IReadOnlyCollection<ProcessInstanceKey> expectedProcessInstances)
    {
        var expectedByProcessId = new Dictionary<int, ProcessInstanceKey>();
        var ambiguousProcessIds = new HashSet<int>();
        foreach (var process in expectedProcessInstances)
        {
            if (process.ProcessId <= 0 || process.StartKey <= 0)
            {
                continue;
            }

            if (!expectedByProcessId.TryAdd(process.ProcessId, process))
            {
                ambiguousProcessIds.Add(process.ProcessId);
            }
        }
        foreach (var processId in ambiguousProcessIds)
        {
            expectedByProcessId.Remove(processId);
        }

        var validated = validateCurrentProcessInstances(expectedByProcessId.Values.ToArray());
        var result = new Dictionary<int, ProcessInstanceKey>(validated.Count);
        foreach (var process in validated)
        {
            if (expectedByProcessId.TryGetValue(process.ProcessId, out var expected)
                && expected == process)
            {
                result.Add(process.ProcessId, process);
            }
        }
        return result;
    }

    private static IReadOnlySet<ProcessInstanceKey> ReadCurrentProcessInstances(
        IReadOnlyCollection<ProcessInstanceKey> expectedProcessInstances)
    {
        var result = new HashSet<ProcessInstanceKey>();
        foreach (var expected in expectedProcessInstances)
        {
            try
            {
                if (BasicMetricReader.ReadCurrentCreationFileTime(expected.ProcessId) == expected.StartKey)
                {
                    result.Add(expected);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
        return result;
    }

    internal ProcessGpuBreakdownRead ReadBreakdownSnapshot(
        IReadOnlyList<WindowsGpuAdapter> adapters,
        IReadOnlyCollection<ProcessInstanceKey>? expectedProcessInstances = null)
    {
        var adapterIndexByLuid = new Dictionary<ulong, int>();
        foreach (var adapter in adapters)
        {
            if (!adapter.IsSoftware
                && (adapter.Luid.HighPart != 0 || adapter.Luid.LowPart != 0))
            {
                adapterIndexByLuid[
                    WindowsGpuAdapterIdentity.Pack(adapter.Luid)] =
                    adapter.Index;
            }
        }

        if (adapterIndexByLuid.Count == 0)
        {
            ProviderAvailability =
                NativePdhProviderAvailability.NotRequested;
            return ProcessGpuBreakdownRead.NotRequested;
        }

        var read = snapshotSource.Read(
            WindowsGpuAdapterIdentity.ComputeTopologyFingerprint(adapters));
        ProviderAvailability = read.Availability;
        var snapshot = read.Snapshot;
        var usageStatus = ObservationStatus(
            EffectiveDomainAvailability(
                snapshot?.GpuEngineObservation.Availability,
                read.Availability));
        var memoryStatus = ObservationStatus(
            EffectiveDomainAvailability(
                snapshot?.GpuMemoryObservation.Availability,
                read.Availability));
        if (snapshot is null)
        {
            return new ProcessGpuBreakdownRead(
                usageStatus,
                memoryStatus,
                new Dictionary<int, IReadOnlyDictionary<int, double>>(),
                new Dictionary<int, IReadOnlyDictionary<int, double>>());
        }

        var usageByAdapter =
            new Dictionary<int, Dictionary<int, double>>();
        var stableProcesses = expectedProcessInstances is null ? null : CreateStableProcessMap(expectedProcessInstances);
        // 分解条只分解同一采样中最忙引擎的占用，分项和总量同源。
        // 调度侧独立使用每个进程自己的最忙引擎。
        var usageTotalByAdapter = new Dictionary<int, double>();
        if (HasPayload(usageStatus))
        {
            var busiest = NativePdhGpuUsage.BusiestEngines(snapshot.EngineRows);
            foreach (var row in snapshot.EngineRows)
            {
                if (!adapterIndexByLuid.TryGetValue(
                        row.AdapterLuid,
                        out var adapterIndex))
                {
                    continue;
                }
                if (!busiest.TryGetValue(row.AdapterLuid, out var engine)
                    || NativePdhGpuUsage.EngineOf(row) != engine.Key) continue;

                // 总和把**所有**引擎行都算进去，包括归不到进程头上的那些 ——
                // 那部分正是分解表里的"未归属"，不该在这一步就被丢掉。
                usageTotalByAdapter[adapterIndex] =
                    usageTotalByAdapter.GetValueOrDefault(adapterIndex)
                        + Sanitize(row.UsagePercent);

                if (row.ProcessId is not (> 0 and <= int.MaxValue))
                {
                    continue;
                }

                if (stableProcesses is not null && (!stableProcesses.TryGetValue((int)row.ProcessId, out var process)
                    || snapshot.EngineWindows.Count(window => window.AdapterLuid == row.AdapterLuid
                        && window.ProcessId == row.ProcessId && window.PhysicalIndex == row.PhysicalIndex
                        && window.EngineIndex == row.EngineIndex && window.BeginFileTime >= process.StartKey
                        && window.EndFileTime > window.BeginFileTime && window.BeginCounter > 0
                        && window.EndCounter > window.BeginCounter && window.CounterFrequency > 0) != 1)) continue;

                AddValue(
                    usageByAdapter,
                    adapterIndex,
                    (int)row.ProcessId,
                    Sanitize(row.UsagePercent),
                    maximum: 100);
            }
        }

        var memoryByAdapter =
            new Dictionary<int, Dictionary<int, double>>();
        if (HasPayload(memoryStatus))
        {
            foreach (var row in snapshot.MemoryRows)
            {
                if (row.ProcessId is not (> 0 and <= int.MaxValue)
                    || !adapterIndexByLuid.TryGetValue(
                        row.AdapterLuid,
                        out var adapterIndex))
                {
                    continue;
                }

                AddValue(
                    memoryByAdapter,
                    adapterIndex,
                    (int)row.ProcessId,
                    Sanitize(row.DedicatedBytes),
                    maximum: double.MaxValue);
            }
        }

        return new ProcessGpuBreakdownRead(
            usageStatus,
            memoryStatus,
            ToReadOnlyMap(usageByAdapter),
            ToReadOnlyMap(memoryByAdapter),
            // 总和和分项用同一个上限口径。夹是单调的，所以夹完之后
            // "任一分项 ≤ 总和"依然成立。
            usageTotalByAdapter.ToDictionary(
                static item => item.Key,
                static item => Math.Clamp(item.Value, 0, 100)));
    }

    private static SamplingObservationStatus ObservationStatus(
        NativePdhProviderAvailability availability)
    {
        return availability switch
        {
            NativePdhProviderAvailability.Available =>
                SamplingObservationStatus.Current,
            NativePdhProviderAvailability.NotRequested =>
                SamplingObservationStatus.NotRequested,
            _ => SamplingObservationStatus.Unavailable
        };
    }

    private static NativePdhProviderAvailability MissingDomainAvailability(
        NativePdhProviderAvailability availability)
        => availability == NativePdhProviderAvailability.NotRequested
            ? NativePdhProviderAvailability.NotRequested
            : NativePdhProviderAvailability.Unavailable;

    private static NativePdhProviderAvailability EffectiveDomainAvailability(
        NativePdhProviderAvailability? domain,
        NativePdhProviderAvailability aggregate)
        => aggregate == NativePdhProviderAvailability.LastGood
            && domain == NativePdhProviderAvailability.Available
                ? NativePdhProviderAvailability.LastGood
                : domain ?? MissingDomainAvailability(aggregate);

    private static bool HasPayload(SamplingObservationStatus status)
        => status is SamplingObservationStatus.Current;

    private static void AddValue(
        Dictionary<int, Dictionary<int, double>> valuesByAdapter,
        int adapterIndex,
        int processId,
        double value,
        double maximum)
    {
        if (value <= 0)
        {
            return;
        }

        if (!valuesByAdapter.TryGetValue(
                adapterIndex,
                out var valuesByProcess))
        {
            valuesByProcess = [];
            valuesByAdapter.Add(adapterIndex, valuesByProcess);
        }

        valuesByProcess.TryGetValue(processId, out var current);
        valuesByProcess[processId] =
            Math.Min(maximum, current + value);
    }

    private static Dictionary<int, IReadOnlyDictionary<int, double>>
        ToReadOnlyMap(
            Dictionary<int, Dictionary<int, double>> valuesByAdapter)
    {
        var result =
            new Dictionary<int, IReadOnlyDictionary<int, double>>(
                valuesByAdapter.Count);
        foreach (var pair in valuesByAdapter)
        {
            result.Add(pair.Key, pair.Value);
        }

        return result;
    }

    private static double Sanitize(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;
    }
}

internal sealed class ProcessGpuBreakdownRead(
    SamplingObservationStatus usageStatus,
    SamplingObservationStatus memoryStatus,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, double>>
        usageByAdapter,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, double>>
        memoryByAdapter,
    IReadOnlyDictionary<int, double>? usageTotalByAdapter = null)
{
    private static readonly IReadOnlyDictionary<int, double>
        EmptyValues = new Dictionary<int, double>();

    internal static ProcessGpuBreakdownRead NotRequested { get; } =
        new(
            SamplingObservationStatus.NotRequested,
            SamplingObservationStatus.NotRequested,
            new Dictionary<
                int,
                IReadOnlyDictionary<int, double>>(),
            new Dictionary<
                int,
                IReadOnlyDictionary<int, double>>());

    internal SamplingObservationStatus UsageStatus { get; } = usageStatus;

    internal SamplingObservationStatus MemoryStatus { get; } = memoryStatus;

    internal SamplingObservationStatus Status =>
        UsageStatus == MemoryStatus
            ? UsageStatus
            : SamplingObservationStatus.Partial;

    internal IReadOnlyDictionary<int, double>
        GetUsagePercentByProcess(int adapterIndex)
        => usageByAdapter.GetValueOrDefault(adapterIndex)
            ?? EmptyValues;

    /// <summary>
    /// 同一采样中最忙引擎的总占用。分项也是这个引擎的进程占用，供设备读数按份额分解。
    /// 没有引擎行时返回 null，不用另一种设备读数冒充归属数据。
    /// </summary>
    internal double? GetUsagePercentTotal(int adapterIndex)
        => usageTotalByAdapter is not null
            && usageTotalByAdapter.TryGetValue(adapterIndex, out var total)
                ? total
                : null;

    internal IReadOnlyDictionary<int, double>
        GetDedicatedMemoryBytesByProcess(int adapterIndex)
        => memoryByAdapter.GetValueOrDefault(adapterIndex)
            ?? EmptyValues;
}

/// <summary>Windows shared GPU memory bytes per adapter and validated process instance.</summary>
internal sealed record ProcessGpuSharedSystemMemoryRead(
    NativePdhProviderAvailability Availability,
    NativePdhResultCode ResultCode,
    DateTimeOffset? ObservedAt,
    IReadOnlyDictionary<(ulong AdapterLuid, int ProcessId, ulong ProcessStartKey), double> Bytes)
{
    public static ProcessGpuSharedSystemMemoryRead NotRequested { get; } = new(
        NativePdhProviderAvailability.NotRequested, NativePdhResultCode.NoData, null,
        new Dictionary<(ulong AdapterLuid, int ProcessId, ulong ProcessStartKey), double>());

    /// <summary>The source-less reader has no PDH route; the field stays unavailable.</summary>
    public static ProcessGpuSharedSystemMemoryRead NoReader { get; } = new(
        NativePdhProviderAvailability.Unavailable, NativePdhResultCode.NoData, null,
        new Dictionary<(ulong AdapterLuid, int ProcessId, ulong ProcessStartKey), double>());
}

internal sealed record SchedulingProcessGpuRead(
    SamplingObservationStatus UsageStatus,
    SamplingObservationStatus DedicatedMemoryStatus,
    ulong UsageGeneration,
    long UsageObservedAtUtcTicks,
    ulong DedicatedMemoryGeneration,
    long DedicatedMemoryObservedAtUtcTicks,
    IReadOnlyDictionary<(ulong AdapterKey, int ProcessId, ulong ProcessStartKey), double> UsagePercent,
    IReadOnlyDictionary<(ulong AdapterKey, int ProcessId, ulong ProcessStartKey), double> DedicatedMemoryBytes)
{
    public IReadOnlyDictionary<(ulong AdapterKey, int ProcessId, ulong ProcessStartKey), GpuAllocationAmount> AllocationAmounts { get; init; }
        = new Dictionary<(ulong, int, ulong), GpuAllocationAmount>();
    public ulong Generation => Math.Max(
        UsageGeneration,
        DedicatedMemoryGeneration);

    public long ObservedAtUtcTicks => Math.Max(
        UsageObservedAtUtcTicks,
        DedicatedMemoryObservedAtUtcTicks);

    public static SchedulingProcessGpuRead NotRequested { get; } = new(
        SamplingObservationStatus.NotRequested,
        SamplingObservationStatus.NotRequested,
        0,
        0,
        0,
        0,
        new Dictionary<(ulong AdapterKey, int ProcessId, ulong ProcessStartKey), double>(),
        new Dictionary<(ulong AdapterKey, int ProcessId, ulong ProcessStartKey), double>());
}
