using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

internal static class HostManagerReportPresentation
{
    internal static OptimizationReportItem CreateReport(
        in NativeReportOutput output,
        CompiledHostManagerReportCoordinatorPlan plan,
        IReadOnlyDictionary<ulong, HostManagerReportTargetDescriptor> targets)
    {
        var ruleHandle = output.RuleHandle;
        var rule = plan.Recreate.Rules.Single(candidate =>
            candidate.RuleHandle == ruleHandle);
        var target = CreateTarget(rule.FactKind, output.TargetHandle, targets);
        var type = ReportType(rule.FactKind);
        var state = ((NativeReportOutputFlags)output.Flags)
            .HasFlag(NativeReportOutputFlags.TrustedSuppressed)
            ? OptimizationReportStates.TrustedSuppressed
            : OptimizationReportStates.Active;
        var createdAt = FromUnixMilliseconds(output.CreatedAtUtcMilliseconds);
        var updatedAt = FromUnixMilliseconds(output.UpdatedAtUtcMilliseconds);
        var observedAt = FromUnixMilliseconds(output.LastObservedAtUtcMilliseconds);

        return new OptimizationReportItem(
            ReportId(output.ReportHandle),
            type,
            state,
            Severity(output.Severity),
            OptimizationConfidence.High,
            createdAt,
            updatedAt,
            createdAt,
            observedAt,
            Title(rule.FactKind, target),
            Message(rule.FactKind, target, output.CurrentValue),
            new OptimizationActivityContext(
                OptimizationActivityContextKinds.Normal,
                null,
                null,
                null,
                null,
                null,
                OptimizationConfidence.High,
                []),
            target,
            new OptimizationReportEvidence(
                ResourceKind(rule.FactKind),
                output.AverageValue,
                output.PeakValue,
                output.CurrentValue,
                FormatValue(rule.FactKind, output.AverageValue),
                FormatValue(rule.FactKind, output.PeakValue),
                FormatValue(rule.FactKind, output.CurrentValue),
                checked((int)Math.Min(output.ActiveSampleCount, int.MaxValue)),
                checked((int)Math.Min(output.SampleCount, int.MaxValue)),
                Math.Max(0, (observedAt - createdAt).TotalSeconds),
                []),
            [
                new OptimizationReportAction(
                    "dismiss",
                    "忽略",
                    "dismiss",
                    true,
                    null)
            ]);
    }

    internal static TrustedOptimizationTarget CreateTrust(
        in NativeReportPersistenceOperation row,
        CompiledHostManagerReportCoordinatorPlan plan,
        IReadOnlyDictionary<ulong, HostManagerReportTargetDescriptor> targets)
    {
        var familyHandle = row.FamilyHandle;
        var familyRule = plan.Recreate.Rules.FirstOrDefault(rule =>
            rule.FamilyHandle == familyHandle);
        var kind = familyRule?.FactKind
            ?? throw new InvalidDataException(
                "A persisted trust row references an unknown report family.");
        var target = CreateTarget(kind, row.TargetHandle, targets);
        var trustedAt = FromUnixMilliseconds(row.FirstObservedAtUtcMilliseconds);
        return new TrustedOptimizationTarget(
            TrustId(row.TargetHandle, row.FamilyHandle),
            target.TargetType,
            target.TargetKey,
            target.DisplayName,
            TrustScope(kind),
            ResourceKind(kind),
            trustedAt,
            "用户永久忽略该报告。",
            row.ReportHandle == 0 ? string.Empty : ReportId(row.ReportHandle),
            trustedAt,
            OptimizationTrustStates.Active);
    }

    internal static string ReportId(ulong reportHandle)
        => $"hm-report-{reportHandle:x16}";

    internal static string TrustId(ulong targetHandle, ulong familyHandle)
        => $"hm-trust-{targetHandle:x16}-{familyHandle:x16}";

    private static OptimizationReportTarget CreateTarget(
        HostManagerReportFactKind kind,
        ulong targetHandle,
        IReadOnlyDictionary<ulong, HostManagerReportTargetDescriptor> targets)
    {
        if (kind == HostManagerReportFactKind.SoftwareMemorySystemPercent)
        {
            return targets.TryGetValue(targetHandle, out var software)
                ? new OptimizationReportTarget(
                    OptimizationReportTargetTypes.Software,
                    $"software:{software.SoftwareId}",
                    software.SoftwareName,
                    software.SoftwareId,
                    software.SoftwareName,
                    software.SoftwareKind,
                    software.DisplayKind,
                    software.ProcessNames,
                    software.ProcessIds,
                    null)
                : new OptimizationReportTarget(
                    OptimizationReportTargetTypes.Software,
                    $"software-handle:{targetHandle:x16}",
                    "软件",
                    null,
                    null,
                    null,
                    null,
                    [],
                    [],
                    null);
        }

        return kind switch
        {
            HostManagerReportFactKind.CpuTemperatureCelsius
                or HostManagerReportFactKind.CpuUsagePercent
                or HostManagerReportFactKind.CpuFrequencyPercent
                => new OptimizationReportTarget(
                    OptimizationReportTargetTypes.CpuPackage,
                    $"cpu-package:{targetHandle}",
                    "CPU",
                    null,
                    null,
                    null,
                    null,
                    [],
                    [],
                    null),
            _ => new OptimizationReportTarget(
                OptimizationReportTargetTypes.System,
                $"system-interrupts:{targetHandle}",
                "系统中断",
                null,
                null,
                null,
                null,
                [],
                [],
                null)
        };
    }

    private static string ReportType(HostManagerReportFactKind kind)
        => kind == HostManagerReportFactKind.SoftwareMemorySystemPercent
            ? OptimizationReportTypes.BackgroundHighUsage
            : kind is HostManagerReportFactKind.CpuTemperatureCelsius
            or HostManagerReportFactKind.CpuUsagePercent
            or HostManagerReportFactKind.CpuFrequencyPercent
            ? OptimizationReportTypes.CpuSustainedThermalThrottling
            : OptimizationReportTypes.SystemInterruptPressure;

    private static string ResourceKind(HostManagerReportFactKind kind)
        => kind == HostManagerReportFactKind.SoftwareMemorySystemPercent
            ? OptimizationResourceKinds.Memory
            : kind is HostManagerReportFactKind.CpuTemperatureCelsius
            or HostManagerReportFactKind.CpuUsagePercent
            or HostManagerReportFactKind.CpuFrequencyPercent
            ? OptimizationResourceKinds.CpuThermal
            : OptimizationResourceKinds.SystemInterrupt;

    private static string TrustScope(HostManagerReportFactKind kind)
        => kind == HostManagerReportFactKind.SoftwareMemorySystemPercent
            ? "software-memory"
            : kind is HostManagerReportFactKind.CpuTemperatureCelsius
            or HostManagerReportFactKind.CpuUsagePercent
            or HostManagerReportFactKind.CpuFrequencyPercent
            ? "cpu-thermal"
            : "system-interrupt";

    private static string Severity(uint value)
        => value switch
        {
            >= 3 => OptimizationSeverity.Critical,
            2 => OptimizationSeverity.Warning,
            _ => OptimizationSeverity.Info
        };

    private static string Title(
        HostManagerReportFactKind kind,
        OptimizationReportTarget target)
        => kind switch
        {
            HostManagerReportFactKind.CpuTemperatureCelsius
                => "CPU 持续受到温度限制",
            HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds
                => "系统中断单次延迟偏高",
            HostManagerReportFactKind.InterruptEventsAtOrAboveOneMillisecond
                => "系统长中断出现频繁",
            HostManagerReportFactKind.InterruptCpuCapacityPercent
                => "系统中断占用偏高",
            HostManagerReportFactKind.SoftwareMemorySystemPercent
                => $"{target.DisplayName} 的内存占用异常",
            _ => "系统性能异常"
        };

    private static string Message(
        HostManagerReportFactKind kind,
        OptimizationReportTarget target,
        double current)
        => kind switch
        {
            HostManagerReportFactKind.CpuTemperatureCelsius
                => $"CPU 温度、负载和有效频率持续符合热限制特征，当前温度 {current:0.0} °C。",
            HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds
                => $"监测窗口内最大单次系统中断为 {current:0.###} ms。",
            HostManagerReportFactKind.InterruptEventsAtOrAboveOneMillisecond
                => $"监测窗口内出现 {current:0} 次不短于 1 ms 的系统中断。",
            HostManagerReportFactKind.InterruptCpuCapacityPercent
                => $"系统中断占用 CPU 总容量 {current:0.####}%。",
            HostManagerReportFactKind.SoftwareMemorySystemPercent
                => $"{target.DisplayName} 持续占用系统物理内存的 {current:0.##}%。",
            _ => $"当前值 {current:0.###}。"
        };

    private static string FormatValue(HostManagerReportFactKind kind, double value)
        => kind switch
        {
            HostManagerReportFactKind.CpuTemperatureCelsius => $"{value:0.0} °C",
            HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds
                => $"{value:0.###} ms",
            HostManagerReportFactKind.InterruptEventsAtOrAboveOneMillisecond
                => $"{value:0} 次",
            HostManagerReportFactKind.SoftwareMemorySystemPercent
                => $"{value:0.##}%",
            _ => $"{value:0.####}%"
        };

    private static DateTimeOffset FromUnixMilliseconds(long value)
    {
        if (value < 0)
        {
            throw new InvalidDataException(
                "A native report timestamp precedes the Unix epoch.");
        }
        return DateTimeOffset.FromUnixTimeMilliseconds(value);
    }
}
