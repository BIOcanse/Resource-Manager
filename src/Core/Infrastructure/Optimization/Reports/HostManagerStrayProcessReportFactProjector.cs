using System.Globalization;
using ResourceManager.Adapter;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

// Owns only the start of continuously observed inactivity, never report/trust state.
// Called under the report owner's execution gate; instance removal drops its history.
internal sealed class HostManagerStrayProcessReportFactProjector
{
    private readonly Dictionary<(int Pid, ulong Start), DateTimeOffset> idleSince = [];
    private ulong idleRuleGeneration;

    internal static bool Supports(HostManagerReportFactKind kind)
        => kind is HostManagerReportFactKind.ProcessMemoryBytes or HostManagerReportFactKind.ProcessIdleMilliseconds;

    internal HostManagerReportSourceObservation Project(
        IReadOnlyList<CompiledHostManagerReportCoordinatorRulePlan> rules,
        ResourceBreakdownSnapshot resources,
        SchedulingProcessFactSnapshot? processes,
        IDictionary<ulong, HostManagerReportTargetDescriptor> targets,
        IReadOnlySet<string>? protectedSoftwareIds)
    {
        var idleRule = rules.Single(rule => rule.FactKind == HostManagerReportFactKind.ProcessIdleMilliseconds);
        if (idleRuleGeneration != idleRule.RuleGeneration)
        {
            idleSince.Clear();
            idleRuleGeneration = idleRule.RuleGeneration;
        }
        var memoryDataset = resources.Datasets.SingleOrDefault(item => item.DatasetId == SamplingDatasetIds.ProcessMemoryUsage);
        var memory = resources.Bars.SingleOrDefault(item => item.MetricId == ResourceBreakdownMetricIds.MemoryUsage);
        var memoryCurrent = memoryDataset is { Status: ResourceBreakdownSamplingStatuses.Ready, Generation: > 0, LastSuccessAt: not null }
            && memory?.ObservationStatus == SamplingObservationStatus.Current
            && memory.AttributionStatus == SamplingObservationStatus.Current;
        var hasCpu = processes?.TryGetCurrentDataset(SchedulingProcessMetricMask.CpuUsage, out _) == true;
        var hasGpu = processes?.TryGetCurrentDataset(SchedulingProcessMetricMask.GpuUsage, out _) == true;
        var cpu = processes?.DatasetObservations.GetValueOrDefault(SchedulingProcessMetricMask.CpuUsage);
        var gpu = processes?.DatasetObservations.GetValueOrDefault(SchedulingProcessMetricMask.GpuUsage);
        var inventory = processes?.FoundationDatasetObservations.GetValueOrDefault(SamplingDatasetIds.ProcessInventory);
        var attribution = processes?.FoundationDatasetObservations.GetValueOrDefault(SamplingDatasetIds.ProcessAttribution);
        // Bind only this report's dependencies, including unsuccessful releases.
        var protectionStamp = protectedSoftwareIds is null ? string.Empty : string.Join(',', protectedSoftwareIds.Order(StringComparer.OrdinalIgnoreCase));
        var stamp = FormattableString.Invariant($"""
            {inventory?.SourceGeneration}:{inventory?.LastSuccessAtUtcTicks}:{inventory?.LastAttemptAtUtcTicks}:{inventory?.Status}
            {attribution?.SourceGeneration}:{attribution?.LastSuccessAtUtcTicks}:{attribution?.LastAttemptAtUtcTicks}:{attribution?.Status}
            {cpu?.SourceGeneration}:{cpu?.LastSuccessAtUtcTicks}:{cpu?.LastAttemptAtUtcTicks}:{cpu?.Status}
            {gpu?.SourceGeneration}:{gpu?.LastSuccessAtUtcTicks}:{gpu?.LastAttemptAtUtcTicks}:{gpu?.Status}
            {memoryDataset?.Generation}:{memoryDataset?.LastSuccessAt?.UtcTicks}:{memoryDataset?.LastAttemptAt?.UtcTicks}:{memoryDataset?.Status}:{memory?.ObservationStatus}:{memory?.AttributionStatus}
            {protectionStamp}
            """);
        var generation = (long)(AdapterResourceKey.FromString(stamp) & long.MaxValue);
        var ticks = new[] { inventory?.LastAttemptAtUtcTicks ?? 0, attribution?.LastAttemptAtUtcTicks ?? 0,
            cpu?.LastAttemptAtUtcTicks ?? 0, gpu?.LastAttemptAtUtcTicks ?? 0, memoryDataset?.LastAttemptAt?.UtcTicks ?? 0 }.Max();
        var observedAt = ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : DateTimeOffset.UnixEpoch;
        if (processes?.IsInventoryCurrentComplete() != true || protectedSoftwareIds is null
            || processes.HasSeparateFoundationPayloads && (!processes.TryGetCurrentFoundationDataset(SamplingDatasetIds.ProcessInventory, out _)
                || !processes.TryGetCurrentFoundationDataset(SamplingDatasetIds.ProcessAttribution, out _)))
        {
            idleSince.Clear();
            return Observation(NativeReportSourceStatus.Unavailable, []);
        }

        var live = processes.Processes.Select(process => (process.ProcessId, process.ProcessStartKey)).ToHashSet();
        foreach (var key in idleSince.Keys.Where(key => !live.Contains(key)).ToArray()) idleSince.Remove(key);
        var memoryByInstance = memoryCurrent ? memory!.Software.SelectMany(software => software.Processes)
            .Where(process => process.ProcessStartKey is > 0)
            .ToDictionary(process => (process.ProcessId, (ulong)process.ProcessStartKey!.Value)) : [];
        var facts = new List<HostManagerReportFactValue>();
        foreach (var process in processes.Processes)
        {
            var key = (process.ProcessId, process.ProcessStartKey);
            if (process.ProcessId == Environment.ProcessId
                || process.SoftwareId.Equals(RuntimeAttributionIds.ResourceManagerSelf, StringComparison.OrdinalIgnoreCase)
                || protectedSoftwareIds.Contains(process.SoftwareId)
                || process.ExecutablePath is { Length: > 0 } path
                    && !idleRule.ExecutableNames.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                idleSince.Remove(key);
                continue;
            }
            var target = AdapterResourceKey.FromString(FormattableString.Invariant($"report:process:{process.ProcessId}:{process.ProcessStartKey}"));
            var complete = hasCpu && hasGpu && !string.IsNullOrWhiteSpace(process.ExecutablePath)
                && process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.GpuUsage)
                && double.IsFinite(process.CpuUsagePercent) && process.CpuUsagePercent >= 0
                && process.Gpus.Count > 0
                && process.Gpus.All(item => item.HasMetric(SchedulingProcessMetricMask.GpuUsage)
                    && item.UsageSourceGeneration == gpu!.SourceGeneration
                    && item.UsageTopologyGeneration == gpu.TopologyGeneration
                    && double.IsFinite(item.UsagePercent) && item.UsagePercent >= 0);
            double? idleMilliseconds = null;
            if (!complete) idleSince.Remove(key);
            else
            {
                var through = new DateTimeOffset(Math.Min(cpu!.ObservedAtUtcTicks, gpu!.ObservedAtUtcTicks), TimeSpan.Zero);
                var latest = new DateTimeOffset(Math.Max(cpu.ObservedAtUtcTicks, gpu.ObservedAtUtcTicks), TimeSpan.Zero);
                var active = process.CpuUsagePercent > idleRule.ZeroActivityThreshold!.Value
                    || process.Gpus.Any(item => item.UsagePercent > idleRule.ZeroActivityThreshold.Value);
                if (active)
                {
                    idleSince.Remove(key);
                    idleMilliseconds = 0;
                }
                else
                {
                    // Start when both zero observations exist; advance only as far
                    // as both streams have observed. Unequal cadences cannot backdate idle.
                    if (!idleSince.TryGetValue(key, out var since) || latest < since) idleSince[key] = since = latest;
                    idleMilliseconds = Math.Max(0, (through - since).TotalMilliseconds);
                }
            }
            double? bytes = memoryByInstance.TryGetValue(key, out var row) && double.IsFinite(row.Value) && row.Value >= 0
                ? row.Value : null;
            var commandLine = resources.ProcessFields?.Processes.FirstOrDefault(row => row.ProcessId == process.ProcessId
                && row.CreationWindowsFileTime100Ns == process.ProcessStartKey.ToString(CultureInfo.InvariantCulture))
                ?.Values.FirstOrDefault(value => value.Field == "process_all_command_line_actual")?.StringValue;
            var evidence = new OptimizationProcessEvidence(process.ProcessId, process.ProcessStartKey.ToString(CultureInfo.InvariantCulture),
                process.ProcessName, bytes, idleMilliseconds / 1000, commandLine);
            targets[target] = new HostManagerReportTargetDescriptor(target, process.SoftwareId, process.SoftwareName,
                process.SoftwareKind, process.SoftwareDisplayKind, [process.ProcessName], [process.ProcessId], evidence);
            // A missing required value makes the entire process decision unavailable,
            // rather than letting a different predicate clear it as if it were healthy.
            foreach (var rule in rules)
                facts.Add(new HostManagerReportFactValue(rule, target, bytes is null || idleMilliseconds is null ? null
                    : rule.FactKind == HostManagerReportFactKind.ProcessMemoryBytes ? bytes : idleMilliseconds, null));
        }
        return Observation(NativeReportSourceStatus.Complete, facts);

        HostManagerReportSourceObservation Observation(NativeReportSourceStatus status, IReadOnlyList<HostManagerReportFactValue> values)
            => new(rules[0].SourceHandle, rules[0].CoverageScopeHandle, generation, observedAt, status,
                rules.Max(rule => rule.MinimumSampleDurationMilliseconds), values);
    }
}
