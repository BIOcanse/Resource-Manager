using ResourceManager.Adapter;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

internal static class HostManagerSoftwareMemoryReportFactProjector
{
    internal static bool Supports(HostManagerReportFactKind kind)
        => kind == HostManagerReportFactKind.SoftwareMemorySystemPercent;

    internal static HostManagerReportSourceObservation Project(
        ulong sourceHandle,
        ulong coverageScopeHandle,
        IReadOnlyList<CompiledHostManagerReportCoordinatorRulePlan> rules,
        ResourceBreakdownSnapshot snapshot,
        IDictionary<ulong, HostManagerReportTargetDescriptor> targets)
    {
        var observedAt = snapshot.Sampling.LastSuccessAt ?? snapshot.CapturedAt;
        var providerGeneration = snapshot.Sampling.StateRevision > 0
            ? snapshot.Sampling.StateRevision
            : Math.Max(1, observedAt.ToUnixTimeMilliseconds());
        var memory = snapshot.Bars.FirstOrDefault(static bar =>
            bar.MetricId.Equals(
                ResourceBreakdownMetricIds.MemoryUsage,
                StringComparison.OrdinalIgnoreCase));
        if (memory is null
            || snapshot.Sampling.Status != ResourceBreakdownSamplingStatuses.Ready
            || memory.ObservationStatus != SamplingObservationStatus.Current
            || memory.AttributionStatus != SamplingObservationStatus.Current)
        {
            return new HostManagerReportSourceObservation(
                sourceHandle,
                coverageScopeHandle,
                providerGeneration,
                snapshot.Sampling.LastAttemptAt ?? snapshot.CapturedAt,
                snapshot.Sampling.Status == ResourceBreakdownSamplingStatuses.Warming
                    ? NativeReportSourceStatus.Skipped
                    : NativeReportSourceStatus.Unavailable,
                0,
                []);
        }

        var facts = new List<HostManagerReportFactValue>();
        foreach (var software in memory.Software
            .Where(IsEligibleSoftware)
            .OrderBy(static item => item.SoftwareId, StringComparer.OrdinalIgnoreCase))
        {
            var targetHandle = AdapterResourceKey.FromString(
                $"report:software:{software.SoftwareId}");
            var descriptor = new HostManagerReportTargetDescriptor(
                targetHandle,
                software.SoftwareId,
                software.Name,
                software.Kind,
                software.DisplayKind,
                software.Processes
                    .Select(static process => process.Name)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                software.Processes
                    .Select(static process => process.ProcessId)
                    .Where(static processId => processId > 0)
                    .Distinct()
                    .Order()
                    .ToArray());
            if (targets.TryGetValue(targetHandle, out var existing)
                && !existing.SoftwareId.Equals(
                    descriptor.SoftwareId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Two software identities produced the same report target handle.");
            }
            targets[targetHandle] = descriptor;

            foreach (var rule in rules)
            {
                facts.Add(new HostManagerReportFactValue(
                    rule,
                    targetHandle,
                    software.SystemPercent,
                    EventCount: null));
            }
        }

        return new HostManagerReportSourceObservation(
            sourceHandle,
            coverageScopeHandle,
            providerGeneration,
            observedAt,
            NativeReportSourceStatus.Complete,
            10_000,
            facts);
    }

    private static bool IsEligibleSoftware(ResourceSoftwareSegment software)
        => !string.IsNullOrWhiteSpace(software.SoftwareId)
            && software.ProcessCount > 0
            && double.IsFinite(software.Value)
            && software.Value >= 0
            && double.IsFinite(software.SystemPercent)
            && software.SystemPercent >= 0
            && software.Kind is not SoftwareKinds.Game
                and not SoftwareKinds.WindowsSystem
                and not SoftwareKinds.WindowsComponent
                and not SoftwareKinds.WindowsService
                and not SoftwareKinds.Unattributed;
}
