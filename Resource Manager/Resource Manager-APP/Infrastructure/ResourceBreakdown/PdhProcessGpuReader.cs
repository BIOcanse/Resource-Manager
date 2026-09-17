using System.Diagnostics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed class PdhProcessGpuReader
{
    private readonly INativePdhSnapshotSource snapshotSource;
    private readonly Func<IReadOnlyCollection<ProcessInstanceKey>, IReadOnlySet<ProcessInstanceKey>>
        validateCurrentProcessInstances;

    public PdhProcessGpuReader()
        : this(UnavailableNativePdhSnapshotSource.Instance, ReadCurrentProcessInstances)
    {
    }

    internal PdhProcessGpuReader(INativePdhSnapshotSource snapshotSource)
        : this(snapshotSource, ReadCurrentProcessInstances)
    {
    }

    internal PdhProcessGpuReader(
        INativePdhSnapshotSource snapshotSource,
        Func<IReadOnlyCollection<ProcessInstanceKey>, IReadOnlySet<ProcessInstanceKey>>
            validateCurrentProcessInstances)
    {
        this.snapshotSource = snapshotSource;
        this.validateCurrentProcessInstances = validateCurrentProcessInstances;
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
            ? snapshot.EngineRows
                .Where(row => row.ProcessId is > 0 and <= int.MaxValue
                    && stableProcesses.ContainsKey((int)row.ProcessId))
                .GroupBy(row => (
                    row.AdapterLuid,
                    ProcessId: (int)row.ProcessId,
                    ProcessStartKey: checked((ulong)stableProcesses[(int)row.ProcessId].StartKey)))
                .ToDictionary(
                    static group => group.Key,
                    static group => Math.Clamp(group.Sum(static row => Sanitize(row.UsagePercent)), 0, 100))
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
                using var process = Process.GetProcessById(expected.ProcessId);
                if (process.StartTime.ToFileTimeUtc() == expected.StartKey)
                {
                    result.Add(expected);
                }
            }
            catch (Exception ex) when (ex is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
            {
            }
        }
        return result;
    }

    internal ProcessGpuBreakdownRead ReadBreakdownSnapshot(
        IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        var adapterIndexByLuid = new Dictionary<ulong, int>();
        foreach (var adapter in adapters)
        {
            if (!adapter.IsSoftware
                && (adapter.Luid.HighPart != 0 || adapter.Luid.LowPart != 0))
            {
                adapterIndexByLuid[
                    NativePdhAdapterIdentity.Pack(adapter.Luid)] =
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
            NativePdhAdapterIdentity.ComputeTopologyFingerprint(adapters));
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
        // 同一次读、同一个求和口径下的整卡总和。
        //
        // **这是分解表的总和，必须从这里出**，不能拿设备自己报的那个占用率去当它。
        // 那个数（N 卡上是 NVML 的 SM utilization）问的是"卡上有没有核在跑"，
        // 而这里的分项问的是"这个进程在各引擎上忙了多久"，两个量定义不同、
        // 采样窗口也不同 —— 拿它当总和，结果就是分项可以比总和还大。
        var usageTotalByAdapter = new Dictionary<int, double>();
        if (HasPayload(usageStatus))
        {
            foreach (var row in snapshot.EngineRows)
            {
                if (!adapterIndexByLuid.TryGetValue(
                        row.AdapterLuid,
                        out var adapterIndex))
                {
                    continue;
                }

                // 总和把**所有**引擎行都算进去，包括归不到进程头上的那些 ——
                // 那部分正是分解表里的"未归属"，不该在这一步就被丢掉。
                usageTotalByAdapter[adapterIndex] =
                    usageTotalByAdapter.GetValueOrDefault(adapterIndex)
                        + Sanitize(row.UsagePercent);

                if (row.ProcessId is not (> 0 and <= int.MaxValue))
                {
                    continue;
                }

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
            NativePdhProviderAvailability.LastGood =>
                SamplingObservationStatus.RetainedLastGood,
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
        => status is SamplingObservationStatus.Current
            or SamplingObservationStatus.RetainedLastGood;

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
    /// 这块卡的占用总和，**和分项出自同一次读、同一个求和口径**。
    ///
    /// 分解表的总和要用它，不要用设备自己报的占用率：那个数在 N 卡上是 NVML 的
    /// SM utilization（"卡上有没有核在跑"），和这里的"各引擎忙了多久"不是一个量，
    /// 采样窗口也不同。两个量凑在一列里，结果就是单个软件能比总和还大。
    ///
    /// 读不到就是 null —— 那时这一列如实报不可用，而不是拿另一个量顶上。
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
