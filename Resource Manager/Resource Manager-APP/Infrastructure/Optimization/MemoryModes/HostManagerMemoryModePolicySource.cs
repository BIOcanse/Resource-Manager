using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed record HostManagerMemoryModePolicy(
    NativeMemoryModeConfiguration Configuration,
    string SourceKind,
    bool AllowUnrestricted,
    uint OptimizeMemoryPriority,
    uint PagedFrozenMemoryPriority,
    string ForeignMemoryPriorityDisposition,
    uint OwnedStateVerificationIntervalCycles,
    HostManagerSchedulingPlanBinding PlanBinding,
    HostManagerMemoryModePolicyEvidence PolicyEvidence);

internal sealed record HostManagerMemoryModePolicyCapture(
    HostManagerMemoryModePolicy? Policy,
    string? UnavailableReason)
{
    internal static HostManagerMemoryModePolicyCapture Unavailable(string reason)
        => new(null, reason);

    internal static HostManagerMemoryModePolicyCapture Available(
        HostManagerMemoryModePolicy policy)
        => new(policy ?? throw new ArgumentNullException(nameof(policy)), null);
}

internal interface IHostManagerMemoryModePolicySource
{
    HostManagerMemoryModePolicyCapture Capture(
        HostManagerSchedulingPlanBinding planBinding,
        CompiledHostManagerSmartCoordinatorPlan smartCoordinatorPlan);
}

public sealed class HostManagerMemoryModePolicyAuthority
{
    private readonly IHostManagerMemoryModePolicySource source;

    internal HostManagerMemoryModePolicyAuthority(
        IHostManagerMemoryModePolicySource source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
    }

    internal HostManagerMemoryModePolicyCapture Capture(
        HostManagerSchedulingPlanBinding planBinding,
        CompiledHostManagerSmartCoordinatorPlan smartCoordinatorPlan)
        => source.Capture(planBinding, smartCoordinatorPlan);
}
