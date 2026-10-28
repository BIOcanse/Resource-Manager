using System.Collections.Immutable;
using ResourceManager.App.Application.SoftwareIssues;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

internal static class HostManagerSoftwareIssueSignalProjection
{
    internal static ImmutableArray<OptimizationSoftwareIssueSignal> Project(
        IReadOnlyList<OptimizationReportItem> reports,
        ReadOnlySpan<NativeReportOutput> outputs,
        CompiledHostManagerReportCoordinatorPlan plan,
        SystemInterruptSnapshot interrupts)
    {
        if (reports.Count != outputs.Length)
        {
            throw new InvalidDataException(
                "Report and native output counts differ while projecting software issues.");
        }

        var signals = ImmutableArray.CreateBuilder<OptimizationSoftwareIssueSignal>();
        for (var index = 0; index < reports.Count; index++)
        {
            var report = reports[index];
            var output = outputs[index];
            var rule = plan.Recreate.Rules.Single(candidate =>
                candidate.RuleHandle == output.RuleHandle);
            var signal = ProjectReport(report, rule.FactKind, interrupts, rule.ActivationThreshold);
            if (signal is not null)
            {
                signals.Add(signal);
            }
        }

        return signals.ToImmutable();
    }

    internal static OptimizationSoftwareIssueSignal? ProjectReport(
        OptimizationReportItem report,
        HostManagerReportFactKind factKind,
        SystemInterruptSnapshot interrupts,
        double activationThreshold)
    {
        if (report.State != OptimizationReportStates.Active || report.Trusted) return null;
        if (report.Target.TargetType == OptimizationReportTargetTypes.Software
            && !string.IsNullOrWhiteSpace(report.Target.SoftwareId)
            && report.Evidence.ResourceKind == OptimizationResourceKinds.Memory)
        {
            return new OptimizationSoftwareIssueSignal(
                report.Id,
                SoftwareIssueKinds.AbnormalMemoryUsage,
                report.Severity,
                report.Target.SoftwareId,
                null)
            {
                Evidence = [new SoftwareIssueEvidence(SoftwareIssueMetricKinds.Memory, report.Evidence.CurrentValue, report.Evidence.ValueUnit)]
            };
        }

        if (report.Type != OptimizationReportTypes.SystemInterruptPressure
            || !interrupts.Available
            || interrupts.Drivers.Count == 0)
        {
            return null;
        }

        var driver = SelectMaximumContributor(interrupts.Drivers, factKind, activationThreshold);
        return driver is null || string.IsNullOrWhiteSpace(driver.ModulePath)
            ? null
            : CreateInterruptSignal(report, factKind, driver);
    }

    private static SystemInterruptDriverSnapshot? SelectMaximumContributor(
        IReadOnlyList<SystemInterruptDriverSnapshot> drivers,
        HostManagerReportFactKind kind, double activationThreshold)
        => drivers
            .Select(driver => new
            {
                Driver = driver,
                Value = MetricValue(driver, kind)
            })
            .Where(candidate => double.IsFinite(candidate.Value)
                && candidate.Value >= activationThreshold
                && !string.IsNullOrWhiteSpace(candidate.Driver.ModulePath))
            .OrderByDescending(static candidate => candidate.Value)
            .ThenBy(static candidate => candidate.Driver.ModulePath, StringComparer.OrdinalIgnoreCase)
            .Select(static candidate => candidate.Driver)
            .FirstOrDefault();

    private static double MetricValue(
        SystemInterruptDriverSnapshot driver,
        HostManagerReportFactKind kind)
        => kind switch
        {
            HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds
                => driver.MaximumSingleDurationMilliseconds,
            HostManagerReportFactKind.InterruptEventsAtOrAboveOneMillisecond
                => driver.EventsAtOrAboveOneMillisecond,
            HostManagerReportFactKind.InterruptCpuCapacityPercent
                => driver.CpuCapacityPercent,
            _ => 0
        };

    private static OptimizationSoftwareIssueSignal CreateInterruptSignal(
        OptimizationReportItem report,
        HostManagerReportFactKind kind,
        SystemInterruptDriverSnapshot driver)
    {
        var issueKind = kind == HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds
            ? SoftwareIssueKinds.LongSystemInterrupts : SoftwareIssueKinds.ExcessiveSystemInterrupts;
        var unit = kind == HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds
            ? "ms" : kind == HostManagerReportFactKind.InterruptCpuCapacityPercent ? "%" : "count";
        return new OptimizationSoftwareIssueSignal(report.Id, issueKind, report.Severity,
            null, driver.ModulePath)
        {
            Evidence = [new SoftwareIssueEvidence(kind switch
            {
                HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds => SoftwareIssueMetricKinds.InterruptMaximumSingleDurationMilliseconds,
                HostManagerReportFactKind.InterruptEventsAtOrAboveOneMillisecond => SoftwareIssueMetricKinds.InterruptEventsAtOrAboveOneMillisecond,
                HostManagerReportFactKind.InterruptCpuCapacityPercent => SoftwareIssueMetricKinds.InterruptCpuCapacityPercent,
                _ => throw new InvalidDataException($"Unsupported software issue metric kind '{kind}'.")
            }, MetricValue(driver, kind), unit, driver.ModuleName)]
        };
    }
}
