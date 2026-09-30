using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private void ResetCpuExclusivityState()
    {
        if (placementCoordinatorSession is not null)
            RequirePlacementStatus(placementCoordinatorSession.ResetCpuExclusivity(), NativePlacementCoordinatorStatus.Ok, "CPU exclusivity reset");
    }
    private static void ConfigureCpuExclusivity(NativePlacementCoordinatorSession session, PlacementCoordinatorRuntimePlan plan)
    {
        var policy = plan.HotPublish.CpuAutomaticExclusivity
            ?? throw new InvalidDataException("The compiled CPU exclusivity policy is missing.");
        if (!policy.IsValid) throw new InvalidDataException("The compiled CPU exclusivity policy is invalid.");
        var config = new NativeCpuExclusivityConfiguration
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativeCpuExclusivityConfiguration>(),
            Generation = plan.HotPublish.ConfigurationGeneration,
            CoreCapacity = checked((uint)plan.Recreate.CoreCapacity), CcdCapacity = checked((uint)plan.Recreate.CcdCapacity),
            SoftwareCapacity = checked((uint)plan.Recreate.TargetCapacity), ReservationCapacity = checked((uint)plan.Recreate.ReservationCapacity),
            CoreEnter = policy.CoreEnterPercent, CoreExit = policy.CoreExitPercent,
            CcdEnter = policy.CcdEnterPercent, CcdExit = policy.CcdExitPercent,
            QualificationRounds = checked((uint)policy.QualificationCompletedRounds), Enabled = policy.Enabled ? 1U : 0U
        };
        RequirePlacementStatus(session.ConfigureCpuExclusivity(in config), NativePlacementCoordinatorStatus.Ok, "CPU exclusivity configure");
    }

    private HostManagerCpuReservations? PlanCpuReservations(CpuTopologySnapshot topology, HostManagerSample sample,
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes)
    {
        // A missing authoritative process dataset is not evidence that an owner exited.
        if (!sample.ProcessFacts.IsCpuCurrentComplete()) return null;
        EnsurePlacementCoordinatorWorkspace();
        var plan = appliedPlacementCoordinatorPlan!;
        var projection = HostManagerCpuExclusivityProjection.Create(topology, sample.CpuResidency, processes,
            plan.HotPublish.ConfigurationGeneration, plan.Recreate);
        var frame = projection.Frame;
        var status = placementCoordinatorSession!.PlanCpuExclusivity(ref frame, projection.Cores,
            projection.Software, projection.Usage, projection.Manual, projection.Output);
        RequirePlacementStatus(status, NativePlacementCoordinatorStatus.Ok, "CPU exclusivity plan");
        return projection.Map(frame.OutputCount);
    }
}
