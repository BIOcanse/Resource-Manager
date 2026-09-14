using ResourceManager.Adapter.LocalResources;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal static class HostManagerSelfMemoryModeProjection
{
    private static readonly ulong SelfSoftwareKey =
        NativeStableIdentity.CreateCaseInsensitiveKey(
            RuntimeAttributionIds.ResourceManagerSelf);

    internal static LocalResourceSoftwareMemoryMode Resolve(
        bool selfMemoryActionsEnabled,
        HostManagerSchedulingPlanBinding currentPlanBinding,
        HostManagerSchedulingAuthoritySnapshot authority)
    {
        ArgumentNullException.ThrowIfNull(currentPlanBinding);
        ArgumentNullException.ThrowIfNull(authority);
        if (!currentPlanBinding.IsPublished)
        {
            throw new InvalidDataException(
                "Resource Manager self memory-mode projection requires a published Host plan binding.");
        }
        if (!selfMemoryActionsEnabled
            || authority.Availability != HostManagerSchedulingAuthorityAvailability.Ready
            || authority.PlanBinding != currentPlanBinding)
        {
            return LocalResourceSoftwareMemoryMode.Normal;
        }

        var compute = authority.Compute
            ?? throw new InvalidDataException(
                "A ready self memory-mode projection requires compute authority.");
        var cpu = compute.Cpu
            ?? throw new InvalidDataException(
                "A ready self memory-mode projection requires the CPU score domain.");
        var memoryModes = authority.MemoryModes
            ?? throw new InvalidDataException(
                "A ready self memory-mode projection requires desired memory modes.");
        var policyEvidence = authority.PolicyEvidence
            ?? throw new InvalidDataException(
                "A ready self memory-mode projection requires memory policy evidence.");
        if (authority.AttemptGeneration == 0
            || compute.SchedulingGeneration != authority.AttemptGeneration
            || cpu.SchedulingGeneration != authority.AttemptGeneration
             || memoryModes.SchedulingGeneration != authority.AttemptGeneration
             || memoryModes.SnapshotGeneration != authority.AttemptGeneration
             || !memoryModes.MemorySource.IsValid
             || memoryModes.ConfigurationGeneration
                != currentPlanBinding.MemoryModeConfigurationGeneration
            || !policyEvidence.IsBoundTo(
                currentPlanBinding,
                memoryModes.UnrestrictedAllowed))
        {
            throw new InvalidDataException(
                "Resource Manager self memory-mode authority is not one sealed generation.");
        }

        HostManagerComputeScore? selfScore = null;
        foreach (var score in cpu.Scores)
        {
            if (score.Kind != NativeComputeScoringOutputKind.SoftwareCpu
                || score.SoftwareKey != SelfSoftwareKey)
            {
                continue;
            }
            if (selfScore is not null)
            {
                throw new InvalidDataException(
                    "Resource Manager self has duplicate software CPU score rows.");
            }
            selfScore = score;
        }

        HostManagerDesiredMemoryMode? selfMode = null;
        foreach (var mode in memoryModes.Software)
        {
            if (mode.SoftwareKey != SelfSoftwareKey)
            {
                continue;
            }
            if (selfMode is not null)
            {
                throw new InvalidDataException(
                    "Resource Manager self has duplicate desired memory-mode rows.");
            }
            selfMode = mode;
        }

        if (selfScore is null && selfMode is null)
        {
            return LocalResourceSoftwareMemoryMode.Normal;
        }

        if (selfScore is null
            || selfMode is null
            || selfScore.SchedulingGeneration != authority.AttemptGeneration
            || selfScore.ProcessId != 0
            || selfScore.ProcessStartKey != 0
            || selfScore.AdapterKey != 0
            || selfScore.MemberCount == 0
            || selfMode.SchedulingGeneration != authority.AttemptGeneration
            || selfMode.SnapshotGeneration != authority.AttemptGeneration
            || BitConverter.DoubleToInt64Bits(selfMode.CpuScore)
                != BitConverter.DoubleToInt64Bits(selfScore.Score)
            || selfMode.Mode is < NativeMemoryMode.Unrestricted
                or > NativeMemoryMode.PagedFrozen)
        {
            throw new InvalidDataException(
                "Resource Manager self memory mode is not bound to its software CPU score.");
        }

        return selfMode.Mode switch
        {
            NativeMemoryMode.Unrestricted => LocalResourceSoftwareMemoryMode.Unrestricted,
            NativeMemoryMode.Normal => LocalResourceSoftwareMemoryMode.Normal,
            NativeMemoryMode.Optimize => LocalResourceSoftwareMemoryMode.Optimize,
            NativeMemoryMode.PagedFrozen => LocalResourceSoftwareMemoryMode.PagedFrozen,
            _ => throw new ArgumentOutOfRangeException(nameof(authority))
        };
    }
}
