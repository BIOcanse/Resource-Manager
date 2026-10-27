using System.Globalization;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Windows.Pdh;

/// <summary>Reads adapter-level shared GPU memory from the selected Windows PDH source.</summary>
public sealed class PdhGpuAdapterSharedMemoryReader
{
    private const string SharedUsageWildcardPath = "\\GPU Adapter Memory(*)\\Shared Usage";
    private readonly IPdhGpuAdapterSharedMemoryApi api;
    private readonly TimeProvider clock;

    public PdhGpuAdapterSharedMemoryReader()
        : this(new WindowsPdhGpuAdapterSharedMemoryApi(), TimeProvider.System)
    {
    }

    internal PdhGpuAdapterSharedMemoryReader(IPdhGpuAdapterSharedMemoryApi api, TimeProvider? clock = null)
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
            if (!TryParseAdapterInstance(item.InstanceName, out var luid)
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

    private static bool TryParseAdapterInstance(string instanceName, out ulong luid)
    {
        luid = 0;
        const string prefix = "luid_0x";
        if (!instanceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var highStart = prefix.Length;
        const int luidTextLength = 8 + 3 + 8;
        if (instanceName.Length < highStart + luidTextLength + 7
            || !uint.TryParse(instanceName.AsSpan(highStart, 8), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var high)
            || !instanceName.AsSpan(highStart + 8, 3).Equals("_0x", StringComparison.OrdinalIgnoreCase)
            || !uint.TryParse(instanceName.AsSpan(highStart + 11, 8), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var low))
            return false;

        var physicalMarker = highStart + luidTextLength;
        if (!instanceName.AsSpan(physicalMarker).StartsWith("_phys_", StringComparison.OrdinalIgnoreCase))
            return false;

        var physicalIndex = instanceName.AsSpan(physicalMarker + 6);
        if (physicalIndex.IsEmpty
            || !uint.TryParse(physicalIndex, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return false;

        luid = ((ulong)high << 32) | low;
        return luid != 0;
    }

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

internal readonly record struct PdhGpuAdapterSharedMemoryCounterValue(
    string InstanceName, uint Status, long LargeValue);

internal static class PdhGpuAdapterSharedMemoryArrayDecoder
{
    private const uint MaximumFormattedBytes = 4 * 1024 * 1024;
    private const uint MaximumFormattedItems = 4096;
    private const int MaximumInstanceNameCharacters = 128;

    internal static bool TryDecode(IntPtr buffer, uint allocatedBytes, uint returnedBytes, uint itemCount,
        out IReadOnlyList<PdhGpuAdapterSharedMemoryCounterValue> values)
    {
        values = [];
        if (buffer == IntPtr.Zero || allocatedBytes > MaximumFormattedBytes || returnedBytes > allocatedBytes
            || itemCount > MaximumFormattedItems)
            return false;

        var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
        var itemArrayBytes = checked(itemCount * (uint)itemSize);
        if (returnedBytes < itemArrayBytes)
            return false;

        var result = new List<PdhGpuAdapterSharedMemoryCounterValue>(checked((int)itemCount));
        for (var index = 0U; index < itemCount; index++)
        {
            var itemPointer = IntPtr.Add(buffer, checked((int)(index * (uint)itemSize)));
            var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(itemPointer);
            if (!TryReadInstanceName(item.Name, buffer, itemArrayBytes, returnedBytes, out var instanceName))
                return false;
            result.Add(new(instanceName, item.Value.CStatus, item.Value.LargeValue));
        }

        values = result;
        return true;
    }

    private static bool TryReadInstanceName(IntPtr name, IntPtr buffer, uint itemArrayBytes,
        uint returnedBytes, out string instanceName)
    {
        instanceName = string.Empty;
        if (name == IntPtr.Zero)
            return false;

        var offset = name.ToInt64() - buffer.ToInt64();
        if (offset < itemArrayBytes || offset >= returnedBytes)
            return false;

        var availableCharacters = checked((int)Math.Min(
            (returnedBytes - (uint)offset) / sizeof(char), (uint)MaximumInstanceNameCharacters));
        var characters = new char[availableCharacters];
        for (var index = 0; index < availableCharacters; index++)
        {
            var character = (char)Marshal.ReadInt16(name, checked(index * sizeof(char)));
            if (character == '\0')
            {
                instanceName = new string(characters, 0, index);
                return instanceName.Length != 0;
            }
            characters[index] = character;
        }
        return false;
    }
}

internal interface IPdhGpuAdapterSharedMemoryApi
{
    int OpenQuery(out IntPtr query);
    int AddEnglishCounter(IntPtr query, string counterPath, out IntPtr counter);
    int CollectQueryData(IntPtr query);
    int GetFormattedCounterArray(IntPtr counter, uint format,
        out IReadOnlyList<PdhGpuAdapterSharedMemoryCounterValue> values);
    int CloseQuery(IntPtr query);
}

internal sealed class WindowsPdhGpuAdapterSharedMemoryApi : IPdhGpuAdapterSharedMemoryApi
{
    private const int PdhMoreData = unchecked((int)0x8000_07D2);
    private const uint MaximumFormattedBytes = 4 * 1024 * 1024;
    private const uint MaximumFormattedItems = 4096;

    public int OpenQuery(out IntPtr query)
        => PdhCounterMethods.PdhOpenQuery(null, UIntPtr.Zero, out query);

    public int AddEnglishCounter(IntPtr query, string counterPath, out IntPtr counter)
        => PdhCounterMethods.PdhAddEnglishCounter(query, counterPath, UIntPtr.Zero, out counter);

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public int CollectQueryData(IntPtr query)
        => PdhCounterMethods.PdhCollectQueryData(query);

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public int GetFormattedCounterArray(IntPtr counter, uint format,
        out IReadOnlyList<PdhGpuAdapterSharedMemoryCounterValue> values)
    {
        values = [];
        uint bufferSize = 0;
        uint itemCount = 0;
        var status = PdhCounterMethods.PdhGetFormattedCounterArray(counter, format,
            ref bufferSize, ref itemCount, IntPtr.Zero);
        if (status == PdhCounterMethods.ErrorSuccess && itemCount == 0)
            return status;
        if (status != PdhMoreData)
            return status;
        if (itemCount > MaximumFormattedItems || bufferSize == 0 || bufferSize > MaximumFormattedBytes)
            return PdhMoreData;

        var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
        if (bufferSize < checked(itemCount * (uint)itemSize))
            return PdhMoreData;

        var allocatedBytes = bufferSize;
        var buffer = Marshal.AllocHGlobal(checked((int)allocatedBytes));
        try
        {
            status = PdhCounterMethods.PdhGetFormattedCounterArray(counter, format,
                ref bufferSize, ref itemCount, buffer);
            if (status != PdhCounterMethods.ErrorSuccess)
                return status;
            if (!PdhGpuAdapterSharedMemoryArrayDecoder.TryDecode(buffer, allocatedBytes,
                    bufferSize, itemCount, out values))
                return PdhMoreData;
            return status;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public int CloseQuery(IntPtr query)
        => PdhCounterMethods.PdhCloseQuery(query);
}
