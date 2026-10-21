using System.ComponentModel;
using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Cpu;

/// <summary>Explicit original Windows CPU relationship and interval counter reads. Construction does not query Windows.</summary>
public sealed partial class WindowsCpuOsTopologyReader
{
    private readonly object usageGate = new();
    private IReadOnlyDictionary<int, ProcessorTimes>? previousTimes;
    private readonly Func<IReadOnlyDictionary<int, ProcessorTimes>> readProcessorTimes;

    public WindowsCpuOsTopologyReader() => readProcessorTimes = QueryProcessorTimes;

    internal WindowsCpuOsTopologyReader(Func<IReadOnlyDictionary<int, ProcessorTimes>> readProcessorTimes)
        => this.readProcessorTimes = readProcessorTimes;

    /// <summary>Reads OS information without product scores, CCD heuristics or fallback cores. Static captures do not advance the busy baseline.</summary>
    public CpuOsTopologyInformation ReadInformation(bool includeUsage = false, ICollection<string>? notes = null)
    {
        notes ??= new List<string>();
        var cores = ReadPhysicalCoreInformation(notes);
        var caches = ReadCacheInformation(notes);
        var packages = ReadPackageInformation(cores, notes);
        var dies = ReadDieInformation(cores, packages, notes);
        var numa = ReadNumaNodes(notes);
        var ids = cores?.SelectMany(static core => core.LogicalProcessorIds).Distinct().Order().ToArray();
        return new(cores, caches)
        {
            Packages = packages,
            Dies = dies,
            NumaNodes = numa,
            OsUsableFeatures = WindowsCpuOsFeatureReader.Read(),
            OsEnabledXStateMask = WindowsCpuOsFeatureReader.ReadEnabledStateMask(),
            ThreadCpuid = ids is null ? null : WindowsCpuThreadCpuidReader.Read(ids, notes),
            LogicalProcessorBusyPercent = includeUsage
                ? ReadLogicalProcessorUsage(Math.Max(Environment.ProcessorCount, ids?.DefaultIfEmpty(-1).Max() + 1 ?? 0), notes)
                : null
        };
    }

    public DeviceInformationSnapshot ReadFields(bool includeUsage = false, ICollection<string>? notes = null)
    {
        var information = ReadInformation(includeUsage, notes);
        return CpuInformationFieldProjection.Create(information, DateTimeOffset.UtcNow, DeviceInformationSnapshot.Empty);
    }

    public static IReadOnlyList<CpuOsPhysicalCore>? ReadPhysicalCoreInformation(ICollection<string>? notes = null)
    {
        try
        {
            return QueryLogicalProcessorRecords(WindowsCpuTopologyNativeMethods.RelationProcessorCore)
                .Select((record, index) => new CpuOsPhysicalCore($"core:{index}", (byte)record.EfficiencyClass,
                    record.SmtRelationshipReported, record.LogicalProcessorIds)).ToArray();
        }
        catch (Exception exception) when (IsExpectedNativeException(exception))
        {
            notes?.Add($"读取物理核心拓扑失败：{exception.Message}");
            return null;
        }
    }

    public static IReadOnlyList<CpuCacheInformation>? ReadCacheInformation(ICollection<string>? notes = null)
    {
        try { return QueryCacheRecords().Select(ToCacheInformation).ToArray(); }
        catch (Exception exception) when (IsExpectedNativeException(exception))
        {
            notes?.Add($"读取 CPU cache 关系失败：{exception.Message}");
            return null;
        }
    }

    public static IReadOnlyList<CpuSetRecord>? ReadCpuSetInformation(ICollection<string>? notes = null)
    {
        try { return QueryCpuSetRecords(); }
        catch (Exception exception) when (IsExpectedNativeException(exception))
        {
            notes?.Add($"读取 Windows CPU Sets 失败：{exception.Message}");
            return null;
        }
    }

    internal static bool IsExpectedNativeException(Exception exception)
        => exception is Win32Exception or InvalidOperationException or NotSupportedException or ExternalException;
}
