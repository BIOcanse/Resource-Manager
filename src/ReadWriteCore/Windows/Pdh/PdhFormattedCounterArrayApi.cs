using System.Globalization;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Pdh;

/// <summary>One formatted wildcard-counter item: the actual instance name, its PDH status and large value.</summary>
internal readonly record struct PdhFormattedCounterValue(
    string InstanceName, uint Status, long LargeValue);

/// <summary>The PDH calls shared by the managed wildcard-counter readers
/// (adapter and per-process shared GPU memory). A reader opens one query per read and closes it before returning.</summary>
internal interface IPdhFormattedCounterArrayApi
{
    int OpenQuery(out IntPtr query);
    int AddEnglishCounter(IntPtr query, string counterPath, out IntPtr counter);
    int CollectQueryData(IntPtr query);
    int GetFormattedCounterArray(IntPtr counter, uint format,
        out IReadOnlyList<PdhFormattedCounterValue> values);
    int CloseQuery(IntPtr query);
}

internal static class PdhFormattedCounterArrayDecoder
{
    // Pre-existing bounds moved unchanged from the adapter shared-memory reader; they bound one formatted array.
    internal const uint MaximumFormattedBytes = 4 * 1024 * 1024;
    internal const uint MaximumFormattedItems = 4096;
    private const int MaximumInstanceNameCharacters = 128;

    internal static bool TryDecode(IntPtr buffer, uint allocatedBytes, uint returnedBytes, uint itemCount,
        out IReadOnlyList<PdhFormattedCounterValue> values)
    {
        values = [];
        if (buffer == IntPtr.Zero || allocatedBytes > MaximumFormattedBytes || returnedBytes > allocatedBytes
            || itemCount > MaximumFormattedItems)
            return false;

        var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
        var itemArrayBytes = checked(itemCount * (uint)itemSize);
        if (returnedBytes < itemArrayBytes)
            return false;

        var result = new List<PdhFormattedCounterValue>(checked((int)itemCount));
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

/// <summary>Parses the exact GPU counter instance names Windows publishes:
/// <c>luid_0x{high}_0x{low}_phys_{n}</c> and <c>pid_{pid}_luid_0x{high}_0x{low}_phys_{n}</c>.</summary>
internal static class PdhGpuCounterInstanceName
{
    internal static bool TryParseAdapter(ReadOnlySpan<char> instanceName, out ulong luid)
    {
        luid = 0;
        const string prefix = "luid_0x";
        if (!instanceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var highStart = prefix.Length;
        const int luidTextLength = 8 + 3 + 8;
        if (instanceName.Length < highStart + luidTextLength + 7
            || !uint.TryParse(instanceName.Slice(highStart, 8), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var high)
            || !instanceName.Slice(highStart + 8, 3).Equals("_0x", StringComparison.OrdinalIgnoreCase)
            || !uint.TryParse(instanceName.Slice(highStart + 11, 8), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var low))
            return false;

        var physicalMarker = highStart + luidTextLength;
        if (!instanceName[physicalMarker..].StartsWith("_phys_", StringComparison.OrdinalIgnoreCase))
            return false;

        var physicalIndex = instanceName[(physicalMarker + 6)..];
        if (physicalIndex.IsEmpty
            || !uint.TryParse(physicalIndex, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return false;

        luid = ((ulong)high << 32) | low;
        return luid != 0;
    }

    internal static bool TryParseProcess(ReadOnlySpan<char> instanceName, out int processId, out ulong luid)
    {
        processId = 0;
        luid = 0;
        const string prefix = "pid_";
        if (!instanceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = instanceName[prefix.Length..];
        var separator = rest.IndexOf('_');
        if (separator <= 0
            || !int.TryParse(rest[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || pid <= 0
            || !TryParseAdapter(rest[(separator + 1)..], out luid))
            return false;
        processId = pid;
        return true;
    }
}

internal sealed class WindowsPdhFormattedCounterArrayApi : IPdhFormattedCounterArrayApi
{
    private const int PdhMoreData = unchecked((int)0x8000_07D2);

    public int OpenQuery(out IntPtr query)
        => PdhCounterMethods.PdhOpenQuery(null, UIntPtr.Zero, out query);

    public int AddEnglishCounter(IntPtr query, string counterPath, out IntPtr counter)
        => PdhCounterMethods.PdhAddEnglishCounter(query, counterPath, UIntPtr.Zero, out counter);

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public int CollectQueryData(IntPtr query)
        => PdhCounterMethods.PdhCollectQueryData(query);

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public int GetFormattedCounterArray(IntPtr counter, uint format,
        out IReadOnlyList<PdhFormattedCounterValue> values)
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
        if (itemCount > PdhFormattedCounterArrayDecoder.MaximumFormattedItems || bufferSize == 0
            || bufferSize > PdhFormattedCounterArrayDecoder.MaximumFormattedBytes)
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
            if (!PdhFormattedCounterArrayDecoder.TryDecode(buffer, allocatedBytes,
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
