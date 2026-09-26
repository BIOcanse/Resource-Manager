namespace ResourceManager.App.Infrastructure.Optimization;

internal enum HostManagerSchedulingAuthorityAvailability : byte
{
    Unavailable = 0,
    ComputeOnly = 1,
    Ready = 2
}

internal sealed record HostManagerSchedulingAuthoritySnapshot(
    ulong AttemptGeneration,
    HostManagerSchedulingAuthorityAvailability Availability,
    DateTimeOffset ObservedAt,
    HostManagerSchedulingPlanBinding? PlanBinding,
    HostManagerComputeScoringCycleResult? Compute,
    HostManagerMemoryModeDesiredSnapshot? MemoryModes,
    HostManagerMemoryModePolicyEvidence? PolicyEvidence,
    string? UnavailableReason)
{
    internal static HostManagerSchedulingAuthoritySnapshot Empty { get; } = new(
        0,
        HostManagerSchedulingAuthorityAvailability.Unavailable,
        default,
        null,
        null,
        null,
        null,
        "not-attempted");
}

internal interface IHostManagerSchedulingAuthoritySource
{
    HostManagerSchedulingAuthoritySnapshot Capture();
}

public sealed class HostManagerSchedulingAuthority : IHostManagerSchedulingAuthoritySource
{
    private readonly object sync = new();
    private HostManagerSchedulingAuthoritySnapshot current =
        HostManagerSchedulingAuthoritySnapshot.Empty;

    internal HostManagerSchedulingAuthoritySnapshot Capture()
        => Volatile.Read(ref current);

    HostManagerSchedulingAuthoritySnapshot IHostManagerSchedulingAuthoritySource.Capture()
        => Capture();

    internal void PublishUnavailable(
        ulong attemptGeneration,
        HostManagerSchedulingPlanBinding planBinding,
        DateTimeOffset observedAt,
        string reason)
    {
        ValidatePlanBinding(planBinding);
        ValidateReason(reason);
        Publish(new(
            attemptGeneration,
            HostManagerSchedulingAuthorityAvailability.Unavailable,
            observedAt,
            planBinding,
            null,
            null,
            null,
            reason));
    }

    internal void PublishComputeOnly(
        HostManagerComputeScoringCycleResult compute,
        HostManagerSchedulingPlanBinding planBinding,
        DateTimeOffset observedAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(compute);
        ValidatePlanBinding(planBinding);
        ValidateCompute(compute);
        ValidateReason(reason);
        Publish(new(
            compute.SchedulingGeneration,
            HostManagerSchedulingAuthorityAvailability.ComputeOnly,
            observedAt,
            planBinding,
            compute,
            null,
            null,
            reason));
    }

    internal void PublishReady(
        HostManagerComputeScoringCycleResult compute,
        HostManagerMemoryModeDesiredSnapshot memoryModes,
        DateTimeOffset observedAt,
        HostManagerSchedulingPlanBinding planBinding,
        HostManagerMemoryModePolicyEvidence policyEvidence)
    {
        ArgumentNullException.ThrowIfNull(compute);
        ArgumentNullException.ThrowIfNull(memoryModes);
        ArgumentNullException.ThrowIfNull(policyEvidence);
        ValidatePlanBinding(planBinding);
        ValidateCompute(compute);
        if (compute.Cpu is null
            || memoryModes.SchedulingGeneration != compute.SchedulingGeneration
            || memoryModes.SnapshotGeneration != compute.SchedulingGeneration
            || !memoryModes.MemorySource.IsValid
            || memoryModes.ConfigurationGeneration
                != planBinding.MemoryModeConfigurationGeneration)
        {
            throw new InvalidDataException(
                "A ready scheduling authority publication must bind one complete compute and memory-mode generation.");
        }
        if (!policyEvidence.IsBoundTo(
                planBinding,
                memoryModes.UnrestrictedAllowed))
        {
            throw new InvalidDataException(
                "A ready scheduling authority publication requires the current memory-mode policy and its exact Host binding.");
        }

        Publish(new(
            compute.SchedulingGeneration,
            HostManagerSchedulingAuthorityAvailability.Ready,
            observedAt,
            planBinding,
            compute,
            memoryModes,
            policyEvidence,
            null));
    }

    private void Publish(HostManagerSchedulingAuthoritySnapshot next)
    {
        if (next.AttemptGeneration == 0 || next.ObservedAt == default)
        {
            throw new InvalidDataException(
                "A scheduling authority publication requires nonzero attempt and observation identities.");
        }

        lock (sync)
        {
            if (current.PlanBinding is not null
                && (next.PlanBinding!.HostPublicationSequence
                        < current.PlanBinding.HostPublicationSequence
                    || next.PlanBinding.HostPublicationSequence
                        == current.PlanBinding.HostPublicationSequence
                        && next.PlanBinding != current.PlanBinding))
            {
                throw new InvalidOperationException(
                    "A stale or inconsistent Host publication cannot replace the scheduling authority.");
            }
            if (next.AttemptGeneration <= current.AttemptGeneration)
            {
                throw new InvalidOperationException(
                    "Scheduling authority attempt generations must increase strictly.");
            }
            Volatile.Write(ref current, next);
        }
    }

    private static void ValidateCompute(HostManagerComputeScoringCycleResult compute)
    {
        if (compute.SchedulingGeneration == 0 || compute.Cpu is null && compute.Gpu is null
            || compute.Cpu is not null
                && compute.Cpu.SchedulingGeneration != compute.SchedulingGeneration
            || compute.Gpu is not null
                && compute.Gpu.SchedulingGeneration != compute.SchedulingGeneration)
        {
            throw new InvalidDataException(
                "The compute authority result does not form one current scheduling generation.");
        }
    }

    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
        {
            throw new ArgumentException(
                "The scheduling authority reason must be a bounded nonempty value.",
                nameof(reason));
        }
    }

    private static void ValidatePlanBinding(HostManagerSchedulingPlanBinding planBinding)
    {
        ArgumentNullException.ThrowIfNull(planBinding);
        if (!planBinding.IsPublished)
        {
            throw new InvalidDataException(
                "A scheduling authority publication requires a complete Host plan binding.");
        }
    }
}
