namespace ResourceManager.App.Infrastructure.Optimization;

internal enum HostManagerSchedulingAuthorityAvailability : byte
{
    Unavailable = 0,
    ComputeOnly = 1,
    Ready = 2
}

internal sealed record HostManagerSchedulingAuthoritySnapshot(
    ulong RoundNumber,
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
    private HostManagerSchedulingAuthoritySnapshot current =
        HostManagerSchedulingAuthoritySnapshot.Empty;

    internal HostManagerSchedulingAuthoritySnapshot Capture()
        => Volatile.Read(ref current);

    HostManagerSchedulingAuthoritySnapshot IHostManagerSchedulingAuthoritySource.Capture()
        => Capture();

    internal void PublishUnavailable(
        ulong roundNumber,
        HostManagerSchedulingPlanBinding planBinding,
        DateTimeOffset observedAt,
        string reason)
    {
        ValidatePlanBinding(planBinding);
        ValidateReason(reason);
        Publish(new(
            roundNumber,
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
            compute.RoundNumber,
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
            compute.RoundNumber,
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
        if (next.ObservedAt == default)
        {
            throw new InvalidDataException(
                "A scheduling authority publication requires an observation time.");
        }

        Volatile.Write(ref current, next);
    }

    private static void ValidateCompute(HostManagerComputeScoringCycleResult compute)
    {
        if (compute.RoundNumber == 0 || compute.Cpu is null && compute.Gpu is null)
        {
            throw new InvalidDataException(
                "The compute authority result does not form one current scheduling round.");
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
