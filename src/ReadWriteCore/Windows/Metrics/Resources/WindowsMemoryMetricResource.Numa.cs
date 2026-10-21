using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
namespace ResourceManager.ReadWrite.Windows.Metrics.Resources;

public sealed partial class WindowsMemoryMetricResource
{
    private readonly Func<MemoryMetrics>? readMemory;
    private readonly Func<ushort, ulong?> readNumaAvailable = ReadNumaAvailableNative;

    public WindowsMemoryMetricResource(Func<ushort, ulong?> readNumaAvailable, Func<MemoryMetrics>? readMemory = null)
        : this()
    {
        this.readNumaAvailable = readNumaAvailable;
        this.readMemory = readMemory;
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

