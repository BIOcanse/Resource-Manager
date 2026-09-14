using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledCpuScoringPlan CompileCpuScoring(FreedomPointCompilation freedom)
    {
        const string consumer = $"{nameof(HostManagerPlanCompiler)}.{nameof(CompileCpuScoring)}";
        var ratio = freedom.Consume<double>(BackendFreedomPointPaths.CpuBaselineRatio, consumer);
        CpuBaselineRatio.Validate(ratio);
        var weights = freedom.Consume<CompiledCpuCoreWeights>(BackendFreedomPointPaths.CpuCorePerformanceWeights, consumer);
        if (string.IsNullOrWhiteSpace(weights.Source) || weights.Cores.IsDefault
            || weights.Cores.Any(static core => string.IsNullOrWhiteSpace(core.PhysicalCoreId)
                || core.PhysicalCoreIndex < 0 || !double.IsFinite(core.ReferenceWeight) || core.ReferenceWeight <= 0)
            || weights.Cores.Select(static core => core.PhysicalCoreId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != weights.Cores.Length
            || weights.Cores.Select(static core => core.PhysicalCoreIndex).Distinct().Count() != weights.Cores.Length)
        {
            throw new InvalidDataException("The CPU scoring weight configuration is invalid.");
        }
        return new(ratio, weights);
    }

    private static CompiledCpuCoreResidencyPlan CompileCpuCoreResidency(
        FreedomPointCompilation freedom,
        CpuTopologySnapshot topology)
    {
        const string consumer = $"{nameof(HostManagerPlanCompiler)}.{nameof(CompileCpuCoreResidency)}";
        var observationWindowMilliseconds = freedom.Consume<int>(
            BackendFreedomPointPaths.CpuResidencyObservationWindow,
            consumer);
        var source = freedom.Consume<CpuExecutionTimeSourceDeclaration>(
            BackendFreedomPointPaths.CpuResidencyExecutionTimeSource,
            consumer);
        var smt = freedom.Consume<CpuSmtAccountingDeclaration>(
            BackendFreedomPointPaths.CpuSmtAccounting,
            consumer);
        if (observationWindowMilliseconds <= 0)
        {
            throw new InvalidDataException(
                "The CPU core-residency observation window must be a positive integer.");
        }

        if (source.Kind != CpuExecutionTimeSourceKinds.KernelEtwContextSwitchClosedIntervals
            || source.MaximumClosedSlices <= 0)
        {
            throw new InvalidDataException(
                "The CPU core-residency execution-time source is not supported.");
        }

        if (smt.Kind != "fixed_logical_processor_share")
        {
            throw new InvalidDataException("The CPU physical-core accounting rule is not supported.");
        }

        return new CompiledCpuCoreResidencyPlan(
            observationWindowMilliseconds,
            new CompiledCpuExecutionTimeSourcePlan(
                source.Kind,
                source.MaximumClosedSlices),
            CpuSmtAccounting.FixedLogicalProcessorShare,
            CompilePhysicalCoreAccounting(topology));
    }

    private static FrozenDictionary<int, CompiledCpuPhysicalCoreAccounting> CompilePhysicalCoreAccounting(
        CpuTopologySnapshot topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (topology.PhysicalCores.Count == 0 || topology.LogicalProcessors.Count == 0)
        {
            throw new InvalidDataException("CPU core accounting requires a nonempty startup topology.");
        }

        var logicalById = new Dictionary<int, CpuLogicalProcessorModel>();
        foreach (var logical in topology.LogicalProcessors)
        {
            if (logical.Id < 0 || !logicalById.TryAdd(logical.Id, logical))
            {
                throw new InvalidDataException("The startup CPU topology has an invalid or duplicate logical processor.");
            }
        }

        var physicalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var compiled = new Dictionary<int, CompiledCpuPhysicalCoreAccounting>();
        foreach (var core in topology.PhysicalCores)
        {
            var siblingCount = core.LogicalProcessorIds.Count;
            if (string.IsNullOrWhiteSpace(core.Id) || string.IsNullOrWhiteSpace(core.CcdId)
                || !physicalIds.Add(core.Id) || siblingCount == 0)
            {
                throw new InvalidDataException("The startup CPU topology needs distinct physical cores with logical siblings.");
            }

            var accounting = new CompiledCpuPhysicalCoreAccounting(
                core.Id, core.CcdId, siblingCount, 1d / siblingCount);
            foreach (var logicalId in core.LogicalProcessorIds)
            {
                if (!logicalById.TryGetValue(logicalId, out var logical)
                    || !core.Id.Equals(logical.PhysicalCoreId, StringComparison.OrdinalIgnoreCase)
                    || !core.CcdId.Equals(logical.CcdId, StringComparison.OrdinalIgnoreCase)
                    || !compiled.TryAdd(logicalId, accounting))
                {
                    throw new InvalidDataException("The startup CPU topology has inconsistent logical-to-physical core membership.");
                }
            }
        }

        if (compiled.Count != logicalById.Count)
        {
            throw new InvalidDataException("Every startup logical processor must belong to one physical core.");
        }

        return compiled.ToFrozenDictionary();
    }

    private static TimeSpan CompileSchedulerSamplingInterval(FreedomPointCompilation freedom)
    {
        var milliseconds = freedom.Consume<int>(
            BackendFreedomPointPaths.SchedulerSamplingInterval,
            $"{nameof(HostManagerPlanCompiler)}.{nameof(CompileSchedulerSamplingInterval)}");
        if (milliseconds <= 0)
        {
            throw new InvalidDataException(
                "The scheduler sampling interval freedom point must be a positive integer.");
        }
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private sealed record CpuExecutionTimeSourceDeclaration(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("maximum_closed_slices")] int MaximumClosedSlices);

    private sealed record CpuSmtAccountingDeclaration([property: JsonPropertyName("kind")] string Kind);
}
