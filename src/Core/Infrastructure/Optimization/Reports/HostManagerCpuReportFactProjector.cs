using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
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

        var usageCurrent = snapshot.TryGetCurrentDataset(MetricDatasetIds.SystemCpuUsage, out var usage);
        var temperatureCurrent = snapshot.TryGetCurrentDataset(MetricDatasetIds.ForSystemMetric("cpu.temperature"), out var temperatureDataset);
        var frequencyCurrent = snapshot.TryGetCurrentDataset(MetricDatasetIds.ForSystemMetric("cpu.frequencyPercent"), out var frequency);
        var temperature = cpu.Sensors.TemperatureCelsius;
        var complete = cpu.ObservationStatus == CpuMetricObservationStatus.Complete
            && cpu.IsUsageAvailable
            && cpu.SourceGeneration > 0
            && cpu.SampleDurationMilliseconds > 0
            && double.IsFinite(cpu.UsagePercent)
            && temperature is not null
            && double.IsFinite(temperature.Value)
            && usageCurrent && temperatureCurrent && frequencyCurrent
            && double.IsFinite(cpu.FrequencyPercent);
        if (!complete)
        {
            return Unavailable(
                sourceHandle,
                coverageScopeHandle,
                checked((long)cpu.SourceGeneration),
                snapshot.CapturedAt,
                NativeReportSourceStatus.Unavailable);
        }

        var publicationGeneration = Math.Max(usage.CommittedGeneration,
            Math.Max(temperatureDataset.CommittedGeneration, frequency.CommittedGeneration));
        var observedAt = new DateTimeOffset(Math.Max(usage.ObservedAtUtcTicks,
            Math.Max(temperatureDataset.ObservedAtUtcTicks, frequency.ObservedAtUtcTicks)), TimeSpan.Zero);
        var temperatureValue = temperature.GetValueOrDefault();
        return new HostManagerReportSourceObservation(
            sourceHandle,
            coverageScopeHandle,
            checked((long)publicationGeneration),
            observedAt,
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

}
