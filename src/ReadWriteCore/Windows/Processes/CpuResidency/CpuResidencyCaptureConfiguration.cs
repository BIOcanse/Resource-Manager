using System.Collections.Frozen;
using ResourceManager.ReadWrite.Windows.Cpu;
namespace ResourceManager.ReadWrite.Windows.Processes;

public sealed record CpuResidencyCoreAccounting(string PhysicalCoreId, string CcdId,
    int LogicalProcessorCount, double LogicalProcessorShare);
/// <summary>Explicit closed-window and real topology inputs. Unknown CCD is empty, not inferred.</summary>
public sealed record CpuResidencyCaptureConfiguration(int ObservationWindowMilliseconds,
    int MaximumActivePairs, FrozenDictionary<int, CpuResidencyCoreAccounting> PhysicalCoreByLogicalProcessor,
    string MappingProvenance)
{
    public TimeSpan ObservationWindow => TimeSpan.FromMilliseconds(ObservationWindowMilliseconds);
    internal CpuResidencyCaptureConfiguration RequirePublished()
    {
        if (ObservationWindowMilliseconds <= 0 || MaximumActivePairs <= 0 || PhysicalCoreByLogicalProcessor.Count == 0
            || PhysicalCoreByLogicalProcessor.Any(pair => pair.Key < 0 || pair.Value.LogicalProcessorCount <= 0
                || !double.IsFinite(pair.Value.LogicalProcessorShare) || pair.Value.LogicalProcessorShare <= 0))
            throw new InvalidOperationException("A positive residency window, capacity and real logical/core mapping are required.");
        return this;
    }
    /// <summary>Explicit OS relationship read; no affinity presets or CCD guessing. Call only for selected residency.</summary>
    public static CpuResidencyCaptureConfiguration ReadWindowsTopology(int observationWindowMilliseconds = 5000,
        int maximumActivePairs = 16384)
    {
        var cores = WindowsCpuOsTopologyReader.ReadPhysicalCoreInformation()
            ?? throw new InvalidOperationException("Windows physical-core relationships are unavailable.");
        return new(observationWindowMilliseconds, maximumActivePairs,
            cores.SelectMany(core => core.LogicalProcessorIds.Select(id => new KeyValuePair<int, CpuResidencyCoreAccounting>(id,
                new(core.PhysicalCoreId, "", core.LogicalProcessorIds.Count, 1d / core.LogicalProcessorIds.Count))))
                .ToFrozenDictionary(), "Windows GetLogicalProcessorInformationEx physical-core relationships; CCD unavailable");
    }
}
public sealed record CpuLogicalProcessorResidencyScope(int LogicalProcessorId, string PhysicalCoreId,
    string? CcdId, string MappingProvenance);
