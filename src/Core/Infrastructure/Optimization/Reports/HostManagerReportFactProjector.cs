using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

internal static class HostManagerReportFactProjector
{
    internal static IReadOnlyList<HostManagerReportSourceObservation> Project(
        CompiledHostManagerReportCoordinatorPlan plan,
        HardwareMetricSnapshot hardware,
        SystemInterruptSnapshot interrupts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(interrupts);

        return ProjectCore(
            plan,
            hardware,
            interrupts,
            resources: null,
            includeSoftware: false).Sources;
    }

    internal static HostManagerReportObservationBatch Project(
        CompiledHostManagerReportCoordinatorPlan plan,
        HardwareMetricSnapshot? hardware,
        SystemInterruptSnapshot interrupts,
        ResourceBreakdownSnapshot resources,
        IReadOnlySet<string>? backgroundSoftwareIds = null,
        IReadOnlySet<string>? protectedSoftwareIds = null,
        HostManagerStrayProcessReportFactProjector? strayProjector = null,
        SchedulingProcessFactSnapshot? processes = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(resources);
        return ProjectCore(
            plan,
            hardware,
            interrupts,
            resources,
            includeSoftware: true, backgroundSoftwareIds, protectedSoftwareIds, strayProjector, processes);
    }

    private static HostManagerReportObservationBatch ProjectCore(
        CompiledHostManagerReportCoordinatorPlan plan,
        HardwareMetricSnapshot? hardware,
        SystemInterruptSnapshot interrupts,
        ResourceBreakdownSnapshot? resources,
        bool includeSoftware,
        IReadOnlySet<string>? backgroundSoftwareIds = null,
        IReadOnlySet<string>? protectedSoftwareIds = null,
        HostManagerStrayProcessReportFactProjector? strayProjector = null,
        SchedulingProcessFactSnapshot? processes = null)
    {
        var targets = new Dictionary<ulong, HostManagerReportTargetDescriptor>();
        var sources = plan.SourceRuleGroups
            .Where(group => includeSoftware || group.Rules.All(rule =>
                !HostManagerSoftwareMemoryReportFactProjector.Supports(
                    rule.FactKind)))
            .Select(group => ProjectSource(
                group.SourceHandle,
                group.CoverageScopeHandle,
                group.Rules,
                hardware,
                interrupts,
                resources,
                targets, backgroundSoftwareIds, protectedSoftwareIds, strayProjector, processes))
            .Where(static source => source is not null)
            .Select(static source => source!)
            .ToArray();
        return new HostManagerReportObservationBatch(sources, targets);
    }

    private static HostManagerReportSourceObservation? ProjectSource(
        ulong sourceHandle,
        ulong coverageScopeHandle,
        IReadOnlyList<CompiledHostManagerReportCoordinatorRulePlan> rules,
        HardwareMetricSnapshot? hardware,
        SystemInterruptSnapshot interrupts,
        ResourceBreakdownSnapshot? resources,
        IDictionary<ulong, HostManagerReportTargetDescriptor> targets,
        IReadOnlySet<string>? backgroundSoftwareIds,
        IReadOnlySet<string>? protectedSoftwareIds,
        HostManagerStrayProcessReportFactProjector? strayProjector,
        SchedulingProcessFactSnapshot? processes)
    {
        if (rules.All(static rule => HostManagerStrayProcessReportFactProjector.Supports(rule.FactKind)))
            return resources is null ? null : strayProjector?.Project(rules, resources, processes, targets, protectedSoftwareIds);
        if (rules.All(static rule =>
            HostManagerCpuReportFactProjector.Supports(rule.FactKind)))
        {
            return hardware is null
                ? null
                : HostManagerCpuReportFactProjector.Project(
                    sourceHandle,
                    coverageScopeHandle,
                    rules,
                    hardware);
        }
        if (rules.All(static rule =>
            HostManagerInterruptReportFactProjector.Supports(rule.FactKind)))
        {
            return HostManagerInterruptReportFactProjector.Project(
                sourceHandle,
                coverageScopeHandle,
                rules,
                interrupts);
        }
        if (rules.All(static rule =>
            HostManagerSoftwareMemoryReportFactProjector.Supports(rule.FactKind)))
        {
            return resources is null
                ? null
                : HostManagerSoftwareMemoryReportFactProjector.Project(
                    sourceHandle,
                    coverageScopeHandle,
                    rules,
                    resources,
                    targets, backgroundSoftwareIds, protectedSoftwareIds);
        }
        throw new InvalidOperationException(
            "A report source mixes unsupported Host Manager fact domains.");
    }
}

internal sealed record HostManagerReportObservationBatch(
    IReadOnlyList<HostManagerReportSourceObservation> Sources,
    IReadOnlyDictionary<ulong, HostManagerReportTargetDescriptor> Targets);

internal sealed record HostManagerReportTargetDescriptor(
    ulong TargetHandle,
    string SoftwareId,
    string SoftwareName,
    string SoftwareKind,
    string DisplayKind,
    IReadOnlyList<string> ProcessNames,
    IReadOnlyList<int> ProcessIds,
    ResourceManager.App.Domain.Optimization.OptimizationProcessEvidence? Process = null);

internal sealed record HostManagerReportSourceObservation(
    ulong SourceHandle,
    ulong CoverageScopeHandle,
    long ProviderGeneration,
    DateTimeOffset ObservedAt,
    NativeReportSourceStatus Status,
    long SampleDurationMilliseconds,
    IReadOnlyList<HostManagerReportFactValue> Facts);

internal sealed record HostManagerReportFactValue(
    CompiledHostManagerReportCoordinatorRulePlan Rule,
    ulong TargetHandle,
    double? CurrentValue,
    ulong? EventCount);
