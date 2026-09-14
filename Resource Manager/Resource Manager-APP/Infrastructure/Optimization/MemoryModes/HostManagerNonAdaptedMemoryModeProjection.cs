using System.Collections.Immutable;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal enum HostManagerNonAdaptedMemoryProcessAction : byte
{
    RestoreOwnedConstraints = 1,
    Optimize = 2,
    PagedFrozen = 3
}

internal readonly record struct HostManagerNonAdaptedMemoryProcessCapability(
    int ProcessId,
    ulong ProcessStartKey,
    string SoftwareId,
    bool CanApplyAutomaticPolicy,
    bool CanApplyAdaptedPolicy);

internal sealed record HostManagerNonAdaptedMemoryProcessDirective(
    ulong SchedulingGeneration,
    ulong SoftwareKey,
    string? SoftwareId,
    int ProcessId,
    ulong ProcessStartKey,
    ulong MemoryPolicyTargetKey,
    uint SoftwareRank,
    NativeMemoryMode Mode,
    HostManagerNonAdaptedMemoryProcessAction Action,
    uint? TargetMemoryPriority);

internal sealed record HostManagerNonAdaptedMemoryModeProjectionSnapshot(
    ulong SchedulingGeneration,
    ulong InventoryGeneration,
    ulong CpuSourceGeneration,
    ImmutableArray<HostManagerNonAdaptedMemoryProcessDirective> Processes);

internal static class HostManagerNonAdaptedMemoryModeProjection
{
    internal static HostManagerNonAdaptedMemoryModeProjectionSnapshot Create(
        HostManagerComputeScoringCycleResult compute,
        HostManagerMemoryModeDesiredSnapshot memoryModes,
        SchedulingProcessFactSnapshot processFacts,
        IReadOnlyList<HostManagerNonAdaptedMemoryProcessCapability> capabilities,
        uint optimizeMemoryPriority,
        uint pagedFrozenMemoryPriority)
    {
        ArgumentNullException.ThrowIfNull(compute);
        ArgumentNullException.ThrowIfNull(memoryModes);
        ArgumentNullException.ThrowIfNull(processFacts);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (optimizeMemoryPriority is < 1 or > 5
            || pagedFrozenMemoryPriority is < 1 or > 5
            || pagedFrozenMemoryPriority > optimizeMemoryPriority)
        {
            throw new InvalidDataException(
                "Non-adapted memory-mode projection requires canonical explicit memory-priority targets.");
        }

        var cpu = compute.Cpu
            ?? throw new InvalidDataException(
                "Non-adapted memory-mode projection requires a complete CPU score domain.");
        if (compute.SchedulingGeneration == 0
            || cpu.SchedulingGeneration != compute.SchedulingGeneration
             || memoryModes.SchedulingGeneration != compute.SchedulingGeneration
             || memoryModes.SnapshotGeneration != compute.SchedulingGeneration
             || !memoryModes.MemorySource.IsValid
             || cpu.SourceGeneration == 0
            || cpu.SourceIdentity.InventoryGeneration != processFacts.Generation
            || !processFacts.IsInventoryCurrentComplete())
        {
            throw new InvalidDataException(
                "Non-adapted memory-mode projection requires one complete scheduling and process-source generation.");
        }

        var processScores = cpu.Scores
            .Where(static score => score.Kind == NativeComputeScoringOutputKind.ProcessCpu)
            .ToArray();
        var processMembers = processScores
            .Select(static score => new ProcessIdentity(score.ProcessId, score.ProcessStartKey))
            .ToHashSet();
        var factsByIdentity = new Dictionary<ProcessIdentity, SchedulingProcessFact>(processMembers.Count);
        var softwareIdsByKey = new Dictionary<ulong, string>();
        foreach (var fact in processFacts.Processes)
        {
            var identity = new ProcessIdentity(fact.ProcessId, fact.ProcessStartKey);
            if (!processMembers.Contains(identity))
            {
                continue;
            }
            if (!factsByIdentity.TryAdd(identity, fact))
            {
                throw new InvalidDataException(
                    "Non-adapted memory-mode projection received duplicate process identities.");
            }

            var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(fact.SoftwareId);
            if (softwareKey == 0)
            {
                throw new InvalidDataException(
                    "Non-adapted memory-mode projection received an invalid software identity.");
            }
            if (softwareIdsByKey.TryGetValue(softwareKey, out var existingSoftwareId))
            {
                if (!existingSoftwareId.Equals(fact.SoftwareId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "A non-adapted memory-mode software key maps to multiple software identities.");
                }
            }
            else
            {
                softwareIdsByKey.Add(softwareKey, fact.SoftwareId);
            }
        }

        if (capabilities.Count != factsByIdentity.Count)
        {
            throw new InvalidDataException(
                "Non-adapted memory-mode capabilities do not cover the complete process generation.");
        }
        var capabilitiesByIdentity = new Dictionary<ProcessIdentity, HostManagerNonAdaptedMemoryProcessCapability>(
            capabilities.Count);
        foreach (var capability in capabilities)
        {
            var identity = new ProcessIdentity(capability.ProcessId, capability.ProcessStartKey);
            if (!factsByIdentity.TryGetValue(identity, out var fact)
                || !fact.SoftwareId.Equals(capability.SoftwareId, StringComparison.OrdinalIgnoreCase)
                || capability.CanApplyAutomaticPolicy && capability.CanApplyAdaptedPolicy
                || !capabilitiesByIdentity.TryAdd(identity, capability))
            {
                throw new InvalidDataException(
                    "Non-adapted memory-mode capabilities are inconsistent with the current process generation.");
            }
        }

        var softwareScores = cpu.Scores
            .Where(static score => score.Kind == NativeComputeScoringOutputKind.SoftwareCpu)
            .ToArray();
        if (processScores.Length != factsByIdentity.Count
            || softwareScores.Length != softwareIdsByKey.Count
            || memoryModes.Software.Length != softwareScores.Length)
        {
            throw new InvalidDataException(
                "Non-adapted memory-mode process, software, and desired-state membership is incomplete.");
        }

        var softwareScoresByKey = new Dictionary<ulong, HostManagerComputeScore>(softwareScores.Length);
        foreach (var score in softwareScores)
        {
            if (!IsCanonicalSoftwareScore(score, compute.SchedulingGeneration)
                || !softwareIdsByKey.ContainsKey(score.SoftwareKey)
                || !softwareScoresByKey.TryAdd(score.SoftwareKey, score))
            {
                throw new InvalidDataException(
                    "Non-adapted memory-mode software scores are not canonical for the current process generation.");
            }
        }

        var modesBySoftwareKey = new Dictionary<ulong, HostManagerDesiredMemoryMode>(
            memoryModes.Software.Length);
        foreach (var desired in memoryModes.Software)
        {
            if (desired.SchedulingGeneration != compute.SchedulingGeneration
                || desired.SnapshotGeneration != compute.SchedulingGeneration
                || desired.Mode is < NativeMemoryMode.Unrestricted or > NativeMemoryMode.PagedFrozen
                || !softwareScoresByKey.TryGetValue(desired.SoftwareKey, out var softwareScore)
                || BitConverter.DoubleToInt64Bits(desired.CpuScore)
                    != BitConverter.DoubleToInt64Bits(softwareScore.Score)
                || !modesBySoftwareKey.TryAdd(desired.SoftwareKey, desired))
            {
                throw new InvalidDataException(
                    "Non-adapted memory-mode desired rows are not bound to the current software scores.");
            }
        }

        var observedMembers = new Dictionary<ulong, uint>(softwareScoresByKey.Count);
        var directives = ImmutableArray.CreateBuilder<HostManagerNonAdaptedMemoryProcessDirective>(
            processScores.Length);
        var memoryPolicyTargetKeys = new HashSet<ulong>();
        foreach (var score in processScores)
        {
            var identity = new ProcessIdentity(score.ProcessId, score.ProcessStartKey);
            if (!IsCanonicalProcessScore(score, compute.SchedulingGeneration)
                || !factsByIdentity.TryGetValue(identity, out var fact)
                || !capabilitiesByIdentity.TryGetValue(identity, out var capability))
            {
                throw new InvalidDataException(
                    "Non-adapted memory-mode process scores are not bound to the current process generation.");
            }

            var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(fact.SoftwareId);
            if (score.SoftwareKey != softwareKey
                || !modesBySoftwareKey.TryGetValue(softwareKey, out var desired))
            {
                throw new InvalidDataException(
                    "A non-adapted memory-mode process is not bound to its current software desired state.");
            }

            observedMembers[softwareKey] = observedMembers.TryGetValue(softwareKey, out var count)
                ? checked(count + 1)
                : 1;
            if (!capability.CanApplyAutomaticPolicy || capability.CanApplyAdaptedPolicy)
            {
                continue;
            }

            var memoryPolicyTargetKey = NativeStableIdentity.CreateCaseInsensitiveKey(
                HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                    fact.ProcessId,
                    fact.ProcessStartKey));
            var cpuPolicyTargetKey = NativeStableIdentity.CreateCaseInsensitiveKey(
                HostManagerTargetIdentity.CreateProcessTargetId(
                    fact.ProcessId,
                    fact.ProcessStartKey));
            if (memoryPolicyTargetKey == 0
                || memoryPolicyTargetKey == cpuPolicyTargetKey
                || !memoryPolicyTargetKeys.Add(memoryPolicyTargetKey))
            {
                throw new InvalidDataException(
                    "A non-adapted memory-mode ownership target is invalid or collides with another policy target.");
            }

            directives.Add(new(
                compute.SchedulingGeneration,
                softwareKey,
                fact.SoftwareId,
                fact.ProcessId,
                fact.ProcessStartKey,
                memoryPolicyTargetKey,
                desired.Rank,
                desired.Mode,
                MapAction(desired.Mode),
                MapMemoryPriority(
                    desired.Mode,
                    optimizeMemoryPriority,
                    pagedFrozenMemoryPriority)));
        }

        foreach (var pair in softwareScoresByKey)
        {
            if (!observedMembers.TryGetValue(pair.Key, out var count)
                || count != pair.Value.MemberCount)
            {
                throw new InvalidDataException(
                    "Non-adapted memory-mode software membership does not match the CPU score aggregation.");
            }
        }

        directives.Sort(static (left, right) =>
        {
            var rank = left.SoftwareRank.CompareTo(right.SoftwareRank);
            if (rank != 0)
            {
                return rank;
            }
            var process = left.ProcessId.CompareTo(right.ProcessId);
            return process != 0
                ? process
                : left.ProcessStartKey.CompareTo(right.ProcessStartKey);
        });
        return new(
            compute.SchedulingGeneration,
            processFacts.Generation,
            cpu.SourceGeneration,
            directives.ToImmutable());
    }

    private static bool IsCanonicalProcessScore(
        HostManagerComputeScore score,
        ulong schedulingGeneration)
        => score.SchedulingGeneration == schedulingGeneration
            && score.ProcessId > 0
            && score.ProcessStartKey != 0
            && score.SoftwareKey != 0
            && score.MemberCount == 1;

    private static bool IsCanonicalSoftwareScore(
        HostManagerComputeScore score,
        ulong schedulingGeneration)
        => score.SchedulingGeneration == schedulingGeneration
            && score.ProcessId == 0
            && score.ProcessStartKey == 0
            && score.SoftwareKey != 0
            && score.MemberCount > 0;

    private static HostManagerNonAdaptedMemoryProcessAction MapAction(NativeMemoryMode mode)
        => mode switch
        {
            NativeMemoryMode.Unrestricted or NativeMemoryMode.Normal =>
                HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
            NativeMemoryMode.Optimize => HostManagerNonAdaptedMemoryProcessAction.Optimize,
            NativeMemoryMode.PagedFrozen => HostManagerNonAdaptedMemoryProcessAction.PagedFrozen,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    private static uint? MapMemoryPriority(
        NativeMemoryMode mode,
        uint optimizeMemoryPriority,
        uint pagedFrozenMemoryPriority)
        => mode switch
        {
            NativeMemoryMode.Unrestricted or NativeMemoryMode.Normal => null,
            NativeMemoryMode.Optimize => optimizeMemoryPriority,
            NativeMemoryMode.PagedFrozen => pagedFrozenMemoryPriority,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    private readonly record struct ProcessIdentity(int ProcessId, ulong ProcessStartKey);
}
