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
        ResourceBreakdownSnapshot resources)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(resources);
        return ProjectCore(
            plan,
            hardware,
            interrupts,
            resources,
            includeSoftware: true);
    }

    private static HostManagerReportObservationBatch ProjectCore(
        CompiledHostManagerReportCoordinatorPlan plan,
        HardwareMetricSnapshot? hardware,
        SystemInterruptSnapshot interrupts,
        ResourceBreakdownSnapshot? resources,
        bool includeSoftware)
    {
        var targets = new Dictionary<ulong, HostManagerReportTargetDescriptor>();
        var sources = plan.Recreate.Rules
            .GroupBy(static rule => (rule.SourceHandle, rule.CoverageScopeHandle))
            .OrderBy(static group => group.Key.SourceHandle)
            .Where(group => includeSoftware || group.All(rule =>
                !HostManagerSoftwareMemoryReportFactProjector.Supports(
                    rule.FactKind)))
            .Select(group => ProjectSource(
                group.Key.SourceHandle,
                group.Key.CoverageScopeHandle,
                [.. group],
                hardware,
                interrupts,
                resources,
                targets))
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
        IDictionary<ulong, HostManagerReportTargetDescriptor> targets)
    {
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
                    targets);
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
    IReadOnlyList<int> ProcessIds);

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
    double CurrentValue,
    ulong? EventCount);
