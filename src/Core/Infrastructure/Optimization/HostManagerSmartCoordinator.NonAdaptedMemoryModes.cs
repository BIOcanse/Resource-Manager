using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private HostManagerNonAdaptedMemoryModeProjectionSnapshot? lastNonAdaptedMemoryModeProjection;

    internal HostManagerNonAdaptedMemoryModeProjectionSnapshot? NonAdaptedMemoryModeProjection
        => Volatile.Read(ref lastNonAdaptedMemoryModeProjection);

    private HostManagerNonAdaptedMemoryModeProjectionSnapshot ProjectNonAdaptedMemoryModes(
        HostManagerComputeScoringCycleResult compute,
        HostManagerMemoryModeDesiredSnapshot memoryModes,
        HostManagerSample sample,
        HostManagerMemoryModePolicy policy)
    {
        var members = compute.Cpu!.Scores
            .Where(static score => score.Kind == NativeComputeScoringOutputKind.ProcessCpu)
            .Select(static score => new HostManagerComputeProcessIdentity(score.ProcessId, score.ProcessStartKey))
            .ToHashSet();
        var capabilities = ImmutableArray.CreateBuilder<HostManagerNonAdaptedMemoryProcessCapability>(
            members.Count);
        foreach (var target in sample.Targets)
        {
            if (target.ProcessIds.Count != 1)
            {
                throw new InvalidDataException(
                    $"Memory-mode target {target.TargetId} does not represent exactly one process instance.");
            }

            var processId = target.ProcessIds[0];
            if (!target.ProcessStartKeys.TryGetValue(processId, out var processStartKey)
                || processStartKey == 0)
            {
                throw new InvalidDataException(
                    $"Memory-mode target {target.TargetId} has no exact process start identity.");
            }
            if (!members.Contains(new HostManagerComputeProcessIdentity(processId, processStartKey)))
            {
                continue;
            }

            capabilities.Add(new(
                processId,
                processStartKey,
                target.SoftwareId,
                target.CanApplyAutomaticPolicy,
                target.CanApplyAdaptedPolicy));
        }

        return HostManagerNonAdaptedMemoryModeProjection.Create(
            compute,
            memoryModes,
            sample.ProcessFacts,
            capabilities.ToImmutable(),
            policy.OptimizeMemoryPriority,
            policy.PagedFrozenMemoryPriority);
    }

    private void PublishNonAdaptedMemoryModeProjection(
        HostManagerNonAdaptedMemoryModeProjectionSnapshot projection)
        => Volatile.Write(ref lastNonAdaptedMemoryModeProjection, projection);

    private void ClearNonAdaptedMemoryModeProjection()
        => Volatile.Write(ref lastNonAdaptedMemoryModeProjection, null);
}
