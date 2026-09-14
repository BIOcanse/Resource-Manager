using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed partial class ResourceManagerSelfSchedulingControl
{
    public ResourceManagerSelfSchedulingSnapshot GetSchedulingSnapshot()
    {
        lock (schedulingGate)
        {
            return CreateSchedulingSnapshotLocked();
        }
    }

    private SelfSchedulingApplyOutcome ApplySoftwareSchedulingCore(
        AdapterSoftwareSchedulingEnvelope envelope,
        ResourceManagerSelfCpuGrade? cpuGrade,
        ResourceManagerSelfGpuGrade? gpuGrade)
    {
        ResourceManagerSelfSchedulingSnapshot snapshot;
        bool cpuChanged;
        bool gpuChanged;
        lock (schedulingGate)
        {
            var targetId = string.IsNullOrWhiteSpace(envelope.TargetId)
                ? envelope.SoftwareId ?? "resource-manager:self"
                : envelope.TargetId.Trim();
            schedulingSources.TryGetValue(targetId, out var existingSource);
            var nextSourceCpuGrade = cpuGrade
                ?? existingSource?.CpuGrade
                ?? ResourceManagerSelfCpuGrade.Normal;
            var nextSourceGpuGrade = gpuGrade
                ?? existingSource?.GpuGrade
                ?? ResourceManagerSelfGpuGrade.Normal;
            if (nextSourceCpuGrade == ResourceManagerSelfCpuGrade.Normal
                && nextSourceGpuGrade == ResourceManagerSelfGpuGrade.Normal)
            {
                schedulingSources.Remove(targetId);
            }
            else
            {
                schedulingSources[targetId] = new ResourceManagerSelfSchedulingSource(
                    targetId,
                    string.IsNullOrWhiteSpace(envelope.DisplayName) ? targetId : envelope.DisplayName.Trim(),
                    envelope.PolicyId,
                    nextSourceCpuGrade,
                    nextSourceGpuGrade,
                    DateTimeOffset.Now,
                    TrimReason(envelope.Reason));
            }

            var (nextCpuGrade, nextGpuGrade) = AggregateSchedulingGrades(schedulingSources.Values);
            cpuChanged = nextCpuGrade != currentCpuGrade;
            gpuChanged = nextGpuGrade != currentGpuGrade;
            UpdateSchedulingGradesLocked(nextCpuGrade, nextGpuGrade, envelope.PolicyId, envelope.Reason);
            if (cpuGrade.HasValue)
            {
                cpuChanged |= ApplyInternalCpuGrade(nextCpuGrade);
            }
            snapshot = CreateSchedulingSnapshotLocked();
        }

        return new SelfSchedulingApplyOutcome(snapshot, cpuChanged, gpuChanged);
    }

    private static (ResourceManagerSelfCpuGrade Cpu, ResourceManagerSelfGpuGrade Gpu)
        AggregateSchedulingGrades(IEnumerable<ResourceManagerSelfSchedulingSource> sources)
    {
        var cpu = ResourceManagerSelfCpuGrade.Normal;
        var gpu = ResourceManagerSelfGpuGrade.Normal;
        foreach (var source in sources)
        {
            if (source.CpuGrade == ResourceManagerSelfCpuGrade.Optimize)
            {
                cpu = ResourceManagerSelfCpuGrade.Optimize;
            }
            if (source.GpuGrade == ResourceManagerSelfGpuGrade.Optimize)
            {
                gpu = ResourceManagerSelfGpuGrade.Optimize;
            }
        }
        return (cpu, gpu);
    }

    private void UpdateSchedulingGradesLocked(
        ResourceManagerSelfCpuGrade cpuGrade,
        ResourceManagerSelfGpuGrade gpuGrade,
        string policyId,
        string? reason)
    {
        if (cpuGrade != currentCpuGrade)
        {
            currentCpuGrade = cpuGrade;
            cpuGradeUpdatedAt = DateTimeOffset.Now;
            cpuGradePolicyId = policyId;
            cpuGradeReason = TrimReason(reason);
        }
        if (gpuGrade != currentGpuGrade)
        {
            currentGpuGrade = gpuGrade;
            gpuGradeUpdatedAt = DateTimeOffset.Now;
            gpuGradePolicyId = policyId;
            gpuGradeReason = TrimReason(reason);
        }
    }

    private ResourceManagerSelfSchedulingSnapshot CreateSchedulingSnapshotLocked()
    {
        return new ResourceManagerSelfSchedulingSnapshot(
            currentCpuGrade,
            currentGpuGrade,
            cpuGradeUpdatedAt,
            gpuGradeUpdatedAt,
            cpuGradePolicyId,
            gpuGradePolicyId,
            cpuGradeReason,
            gpuGradeReason,
            schedulingSources.Values
                .OrderBy(static source => source.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static source => source.TargetId, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private bool ApplyInternalCpuGrade(ResourceManagerSelfCpuGrade grade)
    {
        var computeMode = grade == ResourceManagerSelfCpuGrade.Optimize
            ? ResourceManagerComputeZoneMode.LowPower
            : ResourceManagerComputeZoneMode.Normal;
        var applied = new HashSet<ulong>();
        var changed = false;

        foreach (var zone in EnumerateInternalCpuConsumers())
        {
            if (applied.Add(zone.ZoneKey) && zone.CurrentMode != computeMode)
            {
                zone.ApplyMode(computeMode);
                if (zone.CurrentMode != computeMode)
                {
                    throw new InvalidOperationException($"Self CPU consumer {zone.DisplayName} did not apply {computeMode}.");
                }
                changed = true;
            }
        }
        return changed;
    }

    private bool InternalCpuConsumersMatch(ResourceManagerSelfCpuGrade grade)
    {
        var expected = grade == ResourceManagerSelfCpuGrade.Optimize
            ? ResourceManagerComputeZoneMode.LowPower
            : ResourceManagerComputeZoneMode.Normal;
        var checkedZones = new HashSet<ulong>();
        foreach (var zone in EnumerateInternalCpuConsumers())
        {
            if (checkedZones.Add(zone.ZoneKey) && zone.CurrentMode != expected)
            {
                return false;
            }
        }
        return true;
    }

    private IEnumerable<IResourceManagerSelfComputeZone> EnumerateInternalCpuConsumers()
    {
        if (monitoringSourceZones is not null)
        {
            foreach (var zone in monitoringSourceZones.GetZones())
            {
                yield return zone;
            }
        }

        if (hostManagerSmartControlZones is not null)
        {
            foreach (var zone in hostManagerSmartControlZones.GetZones())
            {
                yield return zone;
            }
        }

        foreach (var zone in standaloneComputeZones)
        {
            yield return zone;
        }
    }

    private static string TrimReason(string? reason)
    {
        var clean = reason?.Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            return "软件级调度器更新资源管理器自身档位。";
        }

        const int maxLength = 512;
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    private sealed record SelfSchedulingApplyOutcome(
        ResourceManagerSelfSchedulingSnapshot Snapshot,
        bool CpuChanged,
        bool GpuChanged);
}
