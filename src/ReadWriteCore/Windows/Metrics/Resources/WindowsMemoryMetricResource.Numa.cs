using System.Globalization;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Pdh;
namespace ResourceManager.ReadWrite.Windows.Metrics.Resources;

public sealed partial class WindowsMemoryMetricResource
{
    private readonly Func<MemoryMetrics>? readMemory;
    private readonly Func<ushort, ulong?> readNumaAvailable = ReadNumaAvailableNative;
    private readonly Func<IReadOnlyList<ushort>, IReadOnlyDictionary<ushort, ulong>> readNumaCapacityMiB = ReadNumaCapacityMiBNative;

    public WindowsMemoryMetricResource(Func<ushort, ulong?> readNumaAvailable, Func<MemoryMetrics>? readMemory = null,
        Func<IReadOnlyList<ushort>, IReadOnlyDictionary<ushort, ulong>>? readNumaCapacityMiB = null)
        : this()
    {
        this.readNumaAvailable = readNumaAvailable;
        this.readMemory = readMemory;
        if (readNumaCapacityMiB is not null) this.readNumaCapacityMiB = readNumaCapacityMiB;
    }

    public static NumaMemoryTopologyRead ReadNumaIdentities()
        => ProjectNumaIdentities(WindowsCpuOsTopologyReader.ReadNumaNodes());

    public static NumaMemoryTopologyRead ProjectNumaIdentities(IReadOnlyList<CpuOsNumaNode>? nodes)
    {
        if (nodes is null || nodes.Any(static node => node.NodeNumber > ushort.MaxValue)
            || nodes.Select(static node => node.NodeNumber).Distinct().Count() != nodes.Count)
            return new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
        return new(NativeMetricSnapshotCatalogTopologyStatus.Complete,
            nodes.OrderBy(static node => node.NodeNumber).Select(static node => new NumaMemoryCatalogIdentity((ushort)node.NodeNumber)).ToArray());
    }

    public IReadOnlyDictionary<string, ulong> ReadNumaAvailable(IReadOnlyList<NumaMemoryCatalogIdentity> nodes,
        IReadOnlySet<string> requestedDevices)
    {
        var values = new Dictionary<string, ulong>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            if (!requestedDevices.Contains(node.ExactIdentityKey)) continue;
            var value = readNumaAvailable(node.NodeNumber);
            if (value.HasValue) values.Add(node.ExactIdentityKey, value.Value);
        }
        return values;
    }

    public IReadOnlyDictionary<string, ulong> ReadNumaCapacityMiB(IReadOnlyList<NumaMemoryCatalogIdentity> nodes,
        IReadOnlySet<string> requestedDevices)
    {
        var selected = nodes.Where(node => requestedDevices.Contains(node.ExactIdentityKey))
            .Select(static node => node.NodeNumber).Distinct().ToArray();
        if (selected.Length == 0) return new Dictionary<string, ulong>(StringComparer.Ordinal);

        var byNode = readNumaCapacityMiB(selected);
        var values = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (requestedDevices.Contains(node.ExactIdentityKey)
                && byNode.TryGetValue(node.NodeNumber, out var value))
                values.Add(node.ExactIdentityKey, value);
        }
        return values;
    }

    internal static string CreateNumaCapacityCounterPath(ushort nodeNumber)
        => $@"\NUMA Node Memory({nodeNumber.ToString(CultureInfo.InvariantCulture)})\Total MBytes";

    internal static bool TryProjectNumaCapacityMiB(int functionStatus, uint counterType,
        PdhFmtCounterValue value, out ulong capacityMiB)
    {
        capacityMiB = 0;
        if (functionStatus != PdhCounterMethods.ErrorSuccess
            || counterType != PdhCounterMethods.PdhCounterTypeRawCount
            || value.CStatus is not (PdhCounterMethods.PdhCStatusValidData or PdhCounterMethods.PdhCStatusNewData)
            || value.LargeValue < 0 || value.LargeValue > uint.MaxValue)
            return false;
        capacityMiB = (ulong)value.LargeValue;
        return true;
    }

    private static IReadOnlyDictionary<ushort, ulong> ReadNumaCapacityMiBNative(IReadOnlyList<ushort> nodeNumbers)
    {
        var values = new Dictionary<ushort, ulong>();
        if (nodeNumbers.Count == 0) return values;

        var query = IntPtr.Zero;
        try
        {
            if (PdhCounterMethods.PdhOpenQuery(null, UIntPtr.Zero, out query) != PdhCounterMethods.ErrorSuccess)
                return values;
            var counters = new Dictionary<ushort, IntPtr>();
            foreach (var nodeNumber in nodeNumbers.Distinct())
            {
                if (PdhCounterMethods.PdhAddEnglishCounter(query, CreateNumaCapacityCounterPath(nodeNumber),
                        UIntPtr.Zero, out var counter) == PdhCounterMethods.ErrorSuccess)
                    counters.Add(nodeNumber, counter);
            }
            if (counters.Count == 0
                || PdhCounterMethods.PdhCollectQueryData(query) != PdhCounterMethods.ErrorSuccess)
                return values;

            foreach (var (nodeNumber, counter) in counters)
            {
                var status = PdhCounterMethods.PdhGetFormattedCounterValue(counter,
                    PdhCounterMethods.PdhFmtLarge | PdhCounterMethods.PdhFmtNoScale,
                    out var counterType, out var formatted);
                if (TryProjectNumaCapacityMiB(status, counterType, formatted, out var capacityMiB))
                    values.Add(nodeNumber, capacityMiB);
            }
            return values;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return values;
        }
        finally
        {
            if (query != IntPtr.Zero) PdhCounterMethods.PdhCloseQuery(query);
        }
    }

    private static ulong? ReadNumaAvailableNative(ushort nodeNumber)
    {
        try { return GetNumaAvailableMemoryNodeEx(nodeNumber, out var value) ? value : null; }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        { return null; }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNumaAvailableMemoryNodeEx(ushort nodeNumber, out ulong availableBytes);
}

