using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native.Pdh;

namespace ResourceManager.ReadWrite.Windows.Pdh;

/// <summary>Reads Windows shared GPU memory per process: the system memory a process has placed in the
/// GPU's shared segments (<c>\GPU Process Memory(*)\Shared Usage</c>), summed over the physical-adapter
/// instances of each requested adapter LUID. This is not dedicated VRAM and not an apportioned share of it.
/// Each read opens one PDH query and closes it before returning; nothing is held between reads, so the
/// counter exists only while a holder's subscribed field is being sampled.</summary>
public sealed class PdhGpuProcessSharedMemoryReader
{
    internal const string SharedUsageWildcardPath = "\\GPU Process Memory(*)\\Shared Usage";
    private readonly IPdhFormattedCounterArrayApi api;
    private readonly TimeProvider clock;

    public PdhGpuProcessSharedMemoryReader()
        : this(new WindowsPdhFormattedCounterArrayApi(), TimeProvider.System)
    {
    }

    internal PdhGpuProcessSharedMemoryReader(IPdhFormattedCounterArrayApi api, TimeProvider? clock = null)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Reads only the given adapter LUIDs. An empty selection opens nothing and returns NotRequested.</summary>
    [RuntimeReadSource(typeof(NativeMetricSnapshotSources), nameof(NativeMetricSnapshotSources.PdhGpuEngine))]
    public PdhGpuProcessSharedMemoryRead Read(IReadOnlyCollection<ulong> adapterLuids)
    {
        ArgumentNullException.ThrowIfNull(adapterLuids);
        var requested = adapterLuids.Where(static luid => luid != 0).ToHashSet();
        if (requested.Count == 0)
            return PdhGpuProcessSharedMemoryRead.NotRequested;

        var openStatus = api.OpenQuery(out var query);
        if (openStatus != PdhCounterMethods.ErrorSuccess || query == IntPtr.Zero)
        {
            if (query != IntPtr.Zero) _ = api.CloseQuery(query);
            return PdhGpuProcessSharedMemoryRead.Unavailable(NativePdhResultCode.PdhError, openStatus);
        }

        PdhGpuProcessSharedMemoryRead result;
        int closeStatus;
        try
        {
            result = ReadQuery(query, requested);
        }
        finally
        {
            closeStatus = api.CloseQuery(query);
        }

        return closeStatus == PdhCounterMethods.ErrorSuccess
            ? result
            : PdhGpuProcessSharedMemoryRead.Unavailable(NativePdhResultCode.PdhError, closeStatus);
    }

    private PdhGpuProcessSharedMemoryRead ReadQuery(IntPtr query, HashSet<ulong> requested)
    {
        // English path, actual instance names: works on localized Windows like the adapter reader.
        var addStatus = api.AddEnglishCounter(query, SharedUsageWildcardPath, out var counter);
        if (addStatus != PdhCounterMethods.ErrorSuccess || counter == IntPtr.Zero)
            return PdhGpuProcessSharedMemoryRead.Unavailable(NativePdhResultCode.PdhError, addStatus);

        var collectStatus = api.CollectQueryData(query);
        if (collectStatus != PdhCounterMethods.ErrorSuccess)
            return PdhGpuProcessSharedMemoryRead.Unavailable(NativePdhResultCode.PdhError, collectStatus);

        var arrayStatus = api.GetFormattedCounterArray(counter,
            PdhCounterMethods.PdhFmtLarge | PdhCounterMethods.PdhFmtNoScale, out var values);
        if (arrayStatus != PdhCounterMethods.ErrorSuccess)
            return PdhGpuProcessSharedMemoryRead.Unavailable(NativePdhResultCode.PdhError, arrayStatus);

        var bytes = new Dictionary<(ulong AdapterLuid, int ProcessId), ulong>();
        var failed = new HashSet<(ulong AdapterLuid, int ProcessId)>();
        var instanceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedCounterCount = 0;
        var nativeStatus = 0;
        foreach (var item in values)
        {
            if (!PdhGpuCounterInstanceName.TryParseProcess(item.InstanceName, out var processId, out var luid)
                || !requested.Contains(luid))
                continue;

            var key = (luid, processId);
            // A repeated instance name cannot identify one original counter instance.
            if (!instanceNames.Add(item.InstanceName)
                || item.Status is not (PdhCounterMethods.PdhCStatusValidData or PdhCounterMethods.PdhCStatusNewData)
                || item.LargeValue < 0)
            {
                if (item.Status is not (PdhCounterMethods.PdhCStatusValidData or PdhCounterMethods.PdhCStatusNewData))
                    nativeStatus = unchecked((int)item.Status);
                failed.Add(key);
                failedCounterCount++;
                continue;
            }

            try
            {
                bytes[key] = checked(bytes.GetValueOrDefault(key) + (ulong)item.LargeValue);
            }
            catch (OverflowException)
            {
                failed.Add(key);
                failedCounterCount++;
            }
        }

        // One bad physical instance makes that process's adapter sum partial; publish nothing for it.
        foreach (var key in failed)
            bytes.Remove(key);

        if (bytes.Count == 0)
            return PdhGpuProcessSharedMemoryRead.Unavailable(
                failedCounterCount == 0 ? NativePdhResultCode.NoData : NativePdhResultCode.PdhError,
                nativeStatus, failedCounterCount);

        return new PdhGpuProcessSharedMemoryRead(bytes, NativePdhProviderAvailability.Available, clock.GetUtcNow())
        {
            ResultCode = NativePdhResultCode.Ok,
            NativeStatus = nativeStatus,
            FailedCounterCount = failedCounterCount
        };
    }
}

/// <summary>Shared GPU memory bytes keyed by adapter LUID and PID as Windows reported them.
/// PIDs are not yet bound to a process instance; the consumer validates creation identity after the read.</summary>
public sealed record PdhGpuProcessSharedMemoryRead(
    IReadOnlyDictionary<(ulong AdapterLuid, int ProcessId), ulong> SharedUsageBytes,
    NativePdhProviderAvailability ProviderAvailability,
    DateTimeOffset? ObservedAt)
{
    public NativePdhResultCode ResultCode { get; init; } = NativePdhResultCode.Ok;
    public int NativeStatus { get; init; }
    public int FailedCounterCount { get; init; }

    public static PdhGpuProcessSharedMemoryRead NotRequested { get; } = new(
        new Dictionary<(ulong AdapterLuid, int ProcessId), ulong>(), NativePdhProviderAvailability.NotRequested, null)
    {
        ResultCode = NativePdhResultCode.NoData
    };

    internal static PdhGpuProcessSharedMemoryRead Unavailable(
        NativePdhResultCode resultCode, int nativeStatus, int failedCounterCount = 0)
        => new(new Dictionary<(ulong AdapterLuid, int ProcessId), ulong>(), NativePdhProviderAvailability.Unavailable, null)
        {
            ResultCode = resultCode,
            NativeStatus = nativeStatus,
            FailedCounterCount = failedCounterCount
        };
}
