using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Windows.Pdh;

/// <summary>Reads adapter-level shared GPU memory from the selected Windows PDH source.</summary>
public sealed class PdhGpuAdapterSharedMemoryReader
{
    private const string SharedUsageWildcardPath = "\\GPU Adapter Memory(*)\\Shared Usage";
    private readonly IPdhFormattedCounterArrayApi api;
    private readonly TimeProvider clock;

    public PdhGpuAdapterSharedMemoryReader()
        : this(new WindowsPdhFormattedCounterArrayApi(), TimeProvider.System)
    {
    }

    internal PdhGpuAdapterSharedMemoryReader(IPdhFormattedCounterArrayApi api, TimeProvider? clock = null)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        this.clock = clock ?? TimeProvider.System;
    }

    [RuntimeReadSource(typeof(NativeMetricSnapshotSources), nameof(NativeMetricSnapshotSources.PdhGpuEngine))]
    public PdhGpuAdapterSharedMemoryRead ReadByAdapterIndex(IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var adapterIndex = CreateAdapterIndex(adapters);
        if (adapterIndex.ByLuid.Count == 0)
        {
            return adapterIndex.AmbiguousIndexes.Count == 0
                ? PdhGpuAdapterSharedMemoryRead.NotRequested
                : PdhGpuAdapterSharedMemoryRead.Unavailable(NativePdhResultCode.NoData, 0,
                    adapterIndex.AmbiguousIndexes.Count);
        }

        var openStatus = api.OpenQuery(out var query);
        if (openStatus != PdhCounterMethods.ErrorSuccess || query == IntPtr.Zero)
        {
            if (query != IntPtr.Zero) _ = api.CloseQuery(query);
            return PdhGpuAdapterSharedMemoryRead.Unavailable(NativePdhResultCode.PdhError, openStatus);
        }

        PdhGpuAdapterSharedMemoryRead result;
        int closeStatus;
        try
        {
            result = ReadQuery(query, adapterIndex);
        }
        finally
        {
            closeStatus = api.CloseQuery(query);
        }

        return closeStatus == PdhCounterMethods.ErrorSuccess
            ? result
            : PdhGpuAdapterSharedMemoryRead.Unavailable(NativePdhResultCode.PdhError, closeStatus);
    }

    private PdhGpuAdapterSharedMemoryRead ReadQuery(IntPtr query, AdapterIndexMap adapterIndex)
    {
        // PDH resolves English counter paths itself. Formatted-array names are the actual instance names,
        // so this works on localized Windows without expanding an English wildcard path.
        var addStatus = api.AddEnglishCounter(query, SharedUsageWildcardPath, out var counter);
        if (addStatus != PdhCounterMethods.ErrorSuccess || counter == IntPtr.Zero)
            return PdhGpuAdapterSharedMemoryRead.Unavailable(NativePdhResultCode.PdhError, addStatus);

        var collectStatus = api.CollectQueryData(query);
        if (collectStatus != PdhCounterMethods.ErrorSuccess)
            return PdhGpuAdapterSharedMemoryRead.Unavailable(NativePdhResultCode.PdhError, collectStatus,
                adapterIndex.AmbiguousIndexes.Count + adapterIndex.ByLuid.Count);

        var arrayStatus = api.GetFormattedCounterArray(counter,
            PdhCounterMethods.PdhFmtLarge | PdhCounterMethods.PdhFmtNoScale, out var values);
        if (arrayStatus != PdhCounterMethods.ErrorSuccess)
            return PdhGpuAdapterSharedMemoryRead.Unavailable(NativePdhResultCode.PdhError, arrayStatus,
                adapterIndex.AmbiguousIndexes.Count + adapterIndex.ByLuid.Count);

        var bytesByAdapterIndex = new Dictionary<int, ulong>();
        var failedAdapterIndexes = new HashSet<int>(adapterIndex.AmbiguousIndexes);
        var failedCounterCount = adapterIndex.AmbiguousIndexes.Count;
        var instanceNamesByAdapterIndex = new Dictionary<int, HashSet<string>>();
        var nativeStatus = 0;
        foreach (var item in values)
        {
            if (!PdhGpuCounterInstanceName.TryParseAdapter(item.InstanceName, out var luid)
                || !adapterIndex.ByLuid.TryGetValue(luid, out var displayIndex))
                continue;

            if (!instanceNamesByAdapterIndex.TryGetValue(displayIndex, out var instanceNames))
                instanceNamesByAdapterIndex.Add(displayIndex,
                    instanceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (!instanceNames.Add(item.InstanceName))
            {
                failedAdapterIndexes.Add(displayIndex);
                failedCounterCount++;
                continue;
            }

            if (item.Status is not (PdhCounterMethods.PdhCStatusValidData or PdhCounterMethods.PdhCStatusNewData)
                || item.LargeValue < 0)
            {
                failedAdapterIndexes.Add(displayIndex);
                failedCounterCount++;
                nativeStatus = unchecked((int)item.Status);
                continue;
            }

            try
            {
                var bytes = checked((ulong)item.LargeValue);
                bytesByAdapterIndex[displayIndex] = checked(bytesByAdapterIndex.GetValueOrDefault(displayIndex) + bytes);
            }
            catch (OverflowException)
            {
                failedAdapterIndexes.Add(displayIndex);
                failedCounterCount++;
            }
        }

        foreach (var displayIndex in failedAdapterIndexes)
            bytesByAdapterIndex.Remove(displayIndex);

        if (bytesByAdapterIndex.Count == 0)
            return PdhGpuAdapterSharedMemoryRead.Unavailable(
                failedCounterCount == 0 ? NativePdhResultCode.NoData : NativePdhResultCode.PdhError,
                nativeStatus, failedCounterCount);

        return new PdhGpuAdapterSharedMemoryRead(bytesByAdapterIndex,
            NativePdhProviderAvailability.Available, clock.GetUtcNow())
        {
            ResultCode = NativePdhResultCode.Ok,
            NativeStatus = nativeStatus,
            FailedCounterCount = failedCounterCount
        };
    }

    private static AdapterIndexMap CreateAdapterIndex(IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        var eligible = adapters.Where(static adapter => !adapter.IsSoftware && adapter.Index >= 0
            && !IsZeroLuid(adapter.Luid)).ToArray();
        var duplicateLuidGroups = eligible.GroupBy(static adapter => WindowsGpuAdapterIdentity.Pack(adapter.Luid))
            .Where(static group => group.Skip(1).Any()).ToArray();
        var uniqueLuid = eligible.GroupBy(static adapter => WindowsGpuAdapterIdentity.Pack(adapter.Luid))
            .Where(static group => group.Skip(1).Any() is false)
            .Select(static group => group.Single()).ToArray();
        var duplicateDisplayIndexes = uniqueLuid.GroupBy(static adapter => adapter.Index)
            .Where(static group => group.Skip(1).Any())
            .Select(static group => group.Key).ToHashSet();
        var ambiguousIndexes = duplicateLuidGroups.SelectMany(static group => group.Select(adapter => adapter.Index))
            .Concat(duplicateDisplayIndexes).ToHashSet();
        var byLuid = uniqueLuid.Where(adapter => !duplicateDisplayIndexes.Contains(adapter.Index))
            .ToDictionary(static adapter => WindowsGpuAdapterIdentity.Pack(adapter.Luid), static adapter => adapter.Index);
        return new(byLuid, ambiguousIndexes);
    }

    private static bool IsZeroLuid(AdapterLuid luid)
        => luid.LowPart == 0 && luid.HighPart == 0;

    private sealed record AdapterIndexMap(Dictionary<ulong, int> ByLuid, HashSet<int> AmbiguousIndexes);
}

public sealed record PdhGpuAdapterSharedMemoryRead(
    IReadOnlyDictionary<int, ulong> SharedUsageBytesByAdapterIndex,
    NativePdhProviderAvailability ProviderAvailability,
    DateTimeOffset? ObservedAt)
{
    public NativePdhResultCode ResultCode { get; init; } = NativePdhResultCode.Ok;
    public int NativeStatus { get; init; }
    public int FailedCounterCount { get; init; }

    public static PdhGpuAdapterSharedMemoryRead NotRequested { get; } = new(
        new Dictionary<int, ulong>(), NativePdhProviderAvailability.NotRequested, null)
    {
        ResultCode = NativePdhResultCode.NoData
    };

    internal static PdhGpuAdapterSharedMemoryRead Unavailable(
        NativePdhResultCode resultCode, int nativeStatus, int failedCounterCount = 0)
        => new(new Dictionary<int, ulong>(), NativePdhProviderAvailability.Unavailable, null)
        {
            ResultCode = resultCode,
            NativeStatus = nativeStatus,
            FailedCounterCount = failedCounterCount
        };
}
