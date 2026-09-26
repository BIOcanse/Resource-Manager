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
            var signal = ProjectReport(report, rule.FactKind, interrupts);
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
        SystemInterruptSnapshot interrupts)
    {
        if (report.Target.TargetType == OptimizationReportTargetTypes.Software
            && !string.IsNullOrWhiteSpace(report.Target.SoftwareId)
            && report.Evidence.ResourceKind == OptimizationResourceKinds.Memory)
        {
            return new OptimizationSoftwareIssueSignal(
                report.Id,
                SoftwareIssueKinds.AbnormalMemoryUsage,
                report.Severity,
                "内存占用异常",
                report.Message,
                report.Target.SoftwareId,
                null);
        }

        if (report.Type != OptimizationReportTypes.SystemInterruptPressure
            || !interrupts.Available
            || interrupts.Drivers.Count == 0)
        {
            return null;
        }

        var driver = SelectMaximumContributor(interrupts.Drivers, factKind);
        return driver is null || string.IsNullOrWhiteSpace(driver.ModulePath)
            ? null
            : CreateInterruptSignal(report, factKind, driver);
    }

    private static SystemInterruptDriverSnapshot? SelectMaximumContributor(
        IReadOnlyList<SystemInterruptDriverSnapshot> drivers,
        HostManagerReportFactKind kind)
        => drivers
            .Select(driver => new
            {
                Driver = driver,
                Value = MetricValue(driver, kind)
            })
            .Where(static candidate => candidate.Value > 0
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
        var (issueKind, label, evidence) = kind switch
        {
            HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds
                => (
                    SoftwareIssueKinds.LongSystemInterrupts,
                    "造成长系统中断",
                    $"最大单次中断 {driver.MaximumSingleDurationMilliseconds:0.###} ms"),
            HostManagerReportFactKind.InterruptEventsAtOrAboveOneMillisecond
                => (
                    SoftwareIssueKinds.ExcessiveSystemInterrupts,
                    "造成过多系统中断",
                    $"不短于 1 ms 的中断 {driver.EventsAtOrAboveOneMillisecond} 次"),
            HostManagerReportFactKind.InterruptCpuCapacityPercent
                => (
                    SoftwareIssueKinds.ExcessiveSystemInterrupts,
                    "造成过多系统中断",
                    $"中断占用 CPU 总容量 {driver.CpuCapacityPercent:0.####}%"),
            _ => throw new InvalidDataException(
                $"Fact kind '{kind}' is not an interrupt issue fact.")
        };

        return new OptimizationSoftwareIssueSignal(
            report.Id,
            issueKind,
            report.Severity,
            label,
            $"{driver.ModuleName} 是同一采样窗口中的主要归因驱动（{evidence}）。",
            null,
            driver.ModulePath);
    }
}
