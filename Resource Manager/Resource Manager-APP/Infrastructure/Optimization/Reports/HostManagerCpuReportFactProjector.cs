using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

internal static class HostManagerCpuReportFactProjector
{
    internal static bool Supports(HostManagerReportFactKind kind)
        => kind is HostManagerReportFactKind.CpuTemperatureCelsius
            or HostManagerReportFactKind.CpuUsagePercent
            or HostManagerReportFactKind.CpuFrequencyPercent;

    internal static HostManagerReportSourceObservation Project(
        ulong sourceHandle,
        ulong coverageScopeHandle,
        IReadOnlyList<CompiledHostManagerReportCoordinatorRulePlan> rules,
        HardwareMetricSnapshot snapshot)
    {
        var cpu = snapshot.Cpu;
        var minimumSampleDurationMilliseconds = rules.Max(
            static rule => rule.MinimumSampleDurationMilliseconds);
        if (cpu.ObservationStatus == CpuMetricObservationStatus.Warming)
        {
            return Unavailable(
                sourceHandle,
                coverageScopeHandle,
                checked((long)cpu.SourceGeneration),
                snapshot.CapturedAt,
                NativeReportSourceStatus.Skipped);
        }
        if (cpu.SampleDurationMilliseconds > 0
            && cpu.SampleDurationMilliseconds < minimumSampleDurationMilliseconds)
        {
            return Unavailable(
                sourceHandle,
                coverageScopeHandle,
                checked((long)cpu.SourceGeneration),
                snapshot.CapturedAt,
                NativeReportSourceStatus.Skipped);
        }

        var temperature = cpu.Sensors.TemperatureCelsius;
        var complete = cpu.ObservationStatus == CpuMetricObservationStatus.Complete
            && cpu.IsUsageAvailable
            && cpu.SourceGeneration > 0
            && cpu.SampleDurationMilliseconds > 0
            && double.IsFinite(cpu.UsagePercent)
            && temperature is not null
            && double.IsFinite(temperature.Value)
            && cpu.Sensors.ProviderState.State.Equals(
                "Active",
                StringComparison.OrdinalIgnoreCase)
            && double.IsFinite(cpu.FrequencyPercent)
            && IsFrequencySourceAvailable(cpu.FrequencySource);
        if (!complete)
        {
            return Unavailable(
                sourceHandle,
                coverageScopeHandle,
                checked((long)cpu.SourceGeneration),
                snapshot.CapturedAt,
                NativeReportSourceStatus.Unavailable);
        }

        var temperatureValue = temperature.GetValueOrDefault();
        return new HostManagerReportSourceObservation(
            sourceHandle,
            coverageScopeHandle,
            checked((long)cpu.SourceGeneration),
            snapshot.CapturedAt,
            NativeReportSourceStatus.Complete,
            cpu.SampleDurationMilliseconds,
            rules.Select(rule => new HostManagerReportFactValue(
                rule,
                coverageScopeHandle,
                rule.FactKind switch
                {
                    HostManagerReportFactKind.CpuTemperatureCelsius => temperatureValue,
                    HostManagerReportFactKind.CpuUsagePercent => cpu.UsagePercent,
                    HostManagerReportFactKind.CpuFrequencyPercent => cpu.FrequencyPercent,
                    _ => throw new InvalidOperationException(
                        "The CPU source contains a non-CPU fact kind.")
                },
                EventCount: null)).ToArray());
    }

    private static HostManagerReportSourceObservation Unavailable(
        ulong sourceHandle,
        ulong coverageScopeHandle,
        long providerGeneration,
        DateTimeOffset observedAt,
        NativeReportSourceStatus status)
        => new(
            sourceHandle,
            coverageScopeHandle,
            providerGeneration,
            observedAt,
            status,
            0,
            []);

    private static bool IsFrequencySourceAvailable(string source)
        => !string.IsNullOrWhiteSpace(source)
            && !source.Contains("Unavailable", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("Frozen", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("Not requested", StringComparison.OrdinalIgnoreCase);
}
