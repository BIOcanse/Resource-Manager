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
                ValueUnit(rule.FactKind),
                checked((int)Math.Min(output.ActiveSampleCount, int.MaxValue)),
                checked((int)Math.Min(output.SampleCount, int.MaxValue)),
                Math.Max(0, (observedAt - createdAt).TotalSeconds))
            {
                Process = IsStray(rule.FactKind) && targets.TryGetValue(output.TargetHandle, out var processTarget)
                    ? processTarget.Process : null
            },
            [
                new OptimizationReportAction(
                    "dismiss",
                    "dismiss",
                    true)
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
            target.Name,
            TrustScope(kind),
            ResourceKind(kind),
            trustedAt,
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
        if (IsStray(kind))
        {
            targets.TryGetValue(targetHandle, out var process);
            return new OptimizationReportTarget(OptimizationReportTargetTypes.ProcessInstance,
                process?.Process is { } evidence ? $"process:{evidence.ProcessId}:{evidence.ProcessStartKey}" : $"process_handle:{targetHandle:x16}",
                process?.Process?.Name ?? process?.SoftwareName ?? string.Empty,
                process?.SoftwareId, process?.SoftwareName, process?.SoftwareKind, process?.DisplayKind,
                process?.ProcessNames ?? [], process?.ProcessIds ?? [], null);
        }
        if (IsSoftwareMemory(kind))
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
                    string.Empty,
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
                    string.Empty,
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
                string.Empty,
                null,
                null,
                null,
                null,
                [],
                [],
                null)
        };
    }

    private static bool IsSoftwareMemory(HostManagerReportFactKind kind)
        => kind is HostManagerReportFactKind.SoftwareMemorySystemPercent
            or HostManagerReportFactKind.SoftwareMemoryBytes;

    private static bool IsStray(HostManagerReportFactKind kind) => HostManagerStrayProcessReportFactProjector.Supports(kind);

    internal static string ReportType(HostManagerReportFactKind kind)
        => IsStray(kind) ? OptimizationReportTypes.SuspectedStrayProcess : IsSoftwareMemory(kind)
            ? OptimizationReportTypes.BackgroundHighUsage
            : kind is HostManagerReportFactKind.CpuTemperatureCelsius
            or HostManagerReportFactKind.CpuUsagePercent
            or HostManagerReportFactKind.CpuFrequencyPercent
            ? OptimizationReportTypes.CpuSustainedThermalThrottling
            : OptimizationReportTypes.SystemInterruptPressure;

    private static string ResourceKind(HostManagerReportFactKind kind)
        => IsStray(kind) ? OptimizationResourceKinds.Memory : IsSoftwareMemory(kind)
            ? OptimizationResourceKinds.Memory
            : kind is HostManagerReportFactKind.CpuTemperatureCelsius
            or HostManagerReportFactKind.CpuUsagePercent
            or HostManagerReportFactKind.CpuFrequencyPercent
            ? OptimizationResourceKinds.CpuThermal
            : OptimizationResourceKinds.SystemInterrupt;

    private static string TrustScope(HostManagerReportFactKind kind)
        => IsStray(kind) ? "suspected_stray_process" : IsSoftwareMemory(kind)
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

    /// <summary>
    /// 这条事实的数值是什么单位。后端不换算也不拼字符串 —— 容量给原始字节，
    /// 前端按用户选的进制显示；百分比、温度、毫秒和次数同理由前端成句。
    /// </summary>
    private static string ValueUnit(HostManagerReportFactKind kind)
        => kind switch
        {
            HostManagerReportFactKind.CpuTemperatureCelsius
                => OptimizationValueUnits.Celsius,
            HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds
                => OptimizationValueUnits.Milliseconds,
            HostManagerReportFactKind.InterruptEventsAtOrAboveOneMillisecond
                => OptimizationValueUnits.Count,
            HostManagerReportFactKind.SoftwareMemoryBytes or HostManagerReportFactKind.ProcessMemoryBytes
                => OptimizationValueUnits.Bytes,
            HostManagerReportFactKind.ProcessIdleMilliseconds => OptimizationValueUnits.Milliseconds,
            _ => OptimizationValueUnits.Percent
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
