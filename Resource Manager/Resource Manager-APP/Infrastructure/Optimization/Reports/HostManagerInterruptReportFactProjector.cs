using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

internal static class HostManagerInterruptReportFactProjector
{
    internal static bool Supports(HostManagerReportFactKind kind)
        => kind is HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds
            or HostManagerReportFactKind.InterruptEventsAtOrAboveOneMillisecond
            or HostManagerReportFactKind.InterruptCpuCapacityPercent;

    internal static HostManagerReportSourceObservation Project(
        ulong sourceHandle,
        ulong coverageScopeHandle,
        IReadOnlyList<CompiledHostManagerReportCoordinatorRulePlan> rules,
        SystemInterruptSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            return Unavailable(
                sourceHandle,
                coverageScopeHandle,
                snapshot.SourceGeneration > 0
                    ? snapshot.SourceGeneration
                    : 0,
                snapshot.CapturedAt,
                snapshot.ProviderState.State.Equals(
                    "Warming",
                    StringComparison.OrdinalIgnoreCase)
                    ? NativeReportSourceStatus.Skipped
                    : NativeReportSourceStatus.Unavailable);
        }

        var durationMilliseconds = checked((long)Math.Round(
            snapshot.Window.TotalMilliseconds));
        var minimumSampleDurationMilliseconds = rules.Max(
            static rule => rule.MinimumSampleDurationMilliseconds);
        if (durationMilliseconds > 0
            && durationMilliseconds < minimumSampleDurationMilliseconds)
        {
            return Unavailable(
                sourceHandle,
                coverageScopeHandle,
                snapshot.SourceGeneration,
                snapshot.CapturedAt,
                NativeReportSourceStatus.Skipped);
        }
        var complete = snapshot.SourceGeneration > 0
            && durationMilliseconds > 0
            && snapshot.LogicalProcessorCount > 0
            && snapshot.ProviderState.EventsLost == 0
            && snapshot.ProviderState.State.Equals(
                "Running",
                StringComparison.OrdinalIgnoreCase)
            && double.IsFinite(snapshot.MaximumSingleDurationMilliseconds)
            && snapshot.MaximumSingleDurationMilliseconds >= 0
            && snapshot.EventsAtOrAboveOneMillisecond >= 0
            && double.IsFinite(snapshot.CpuCapacityPercent)
            && snapshot.CpuCapacityPercent >= 0;
        if (!complete)
        {
            return Unavailable(
                sourceHandle,
                coverageScopeHandle,
                snapshot.SourceGeneration,
                snapshot.CapturedAt,
                NativeReportSourceStatus.Unavailable);
        }

        return new HostManagerReportSourceObservation(
            sourceHandle,
            coverageScopeHandle,
            snapshot.SourceGeneration,
            snapshot.CapturedAt,
            NativeReportSourceStatus.Complete,
            durationMilliseconds,
            rules.Select(rule => new HostManagerReportFactValue(
                rule,
                coverageScopeHandle,
                rule.FactKind switch
                {
                    HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds
                        => snapshot.MaximumSingleDurationMilliseconds,
                    HostManagerReportFactKind.InterruptEventsAtOrAboveOneMillisecond
                        => snapshot.EventsAtOrAboveOneMillisecond,
                    HostManagerReportFactKind.InterruptCpuCapacityPercent
                        => snapshot.CpuCapacityPercent,
                    _ => throw new InvalidOperationException(
                        "The interrupt source contains a non-interrupt fact kind.")
                },
                rule.FactKind
                    == HostManagerReportFactKind.InterruptEventsAtOrAboveOneMillisecond
                    ? checked((ulong)snapshot.EventsAtOrAboveOneMillisecond)
                    : null)).ToArray());
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
