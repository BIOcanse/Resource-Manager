using System.Collections.Frozen;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public enum CpuSmtAccounting
{
    Unpublished,
    FixedLogicalProcessorShare
}

public static class CpuExecutionTimeSourceKinds
{
    public const string KernelEtwContextSwitchClosedIntervals =
        "kernel_etw_context_switch_qpc_closed_intervals_v1";
}

public sealed record CompiledCpuCoreResidencyPlan(
    int ObservationWindowMilliseconds,
    CompiledCpuExecutionTimeSourcePlan ExecutionTimeSource,
    CpuSmtAccounting SmtAccounting,
    FrozenDictionary<int, CompiledCpuPhysicalCoreAccounting> PhysicalCoreByLogicalProcessor)
{
    public TimeSpan ObservationWindow => TimeSpan.FromMilliseconds(ObservationWindowMilliseconds);

    public bool IsPublished => ObservationWindowMilliseconds > 0
        && ExecutionTimeSource.IsPublished
        && SmtAccounting == CpuSmtAccounting.FixedLogicalProcessorShare
        && PhysicalCoreByLogicalProcessor.Count > 0;

    public CompiledCpuCoreResidencyPlan RequirePublished()
    {
        if (!IsPublished)
        {
            throw new InvalidOperationException("The CPU core-residency plan has not been published.");
        }

        return this;
    }

    public static CompiledCpuCoreResidencyPlan Unpublished { get; } = new(
        0,
        CompiledCpuExecutionTimeSourcePlan.Unpublished,
        CpuSmtAccounting.Unpublished,
        FrozenDictionary<int, CompiledCpuPhysicalCoreAccounting>.Empty);
}

public sealed record CompiledCpuPhysicalCoreAccounting(
    string PhysicalCoreId,
    string CcdId,
    int LogicalProcessorCount,
    double LogicalProcessorShare);

public sealed record CompiledCpuExecutionTimeSourcePlan(
    string Kind,
    int MaximumClosedSlices)
{
    public bool IsPublished => Kind == CpuExecutionTimeSourceKinds.KernelEtwContextSwitchClosedIntervals
        && MaximumClosedSlices > 0;

    public static CompiledCpuExecutionTimeSourcePlan Unpublished { get; } = new(string.Empty, 0);
}
