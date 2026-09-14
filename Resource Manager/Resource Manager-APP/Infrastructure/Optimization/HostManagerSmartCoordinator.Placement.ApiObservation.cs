using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private Task<(HostManagerRollbackStateDocument State, GpuRemoteCallSnapshot Result)> ExecuteOwnedGpuApiObservationCallAsync(
        HostManagerRollbackStateDocument state, HostManagerPlacementReceiptKey key,
        HostManagerAutomaticPlacementProcess process, GpuRemoteCallExecution execution, CancellationToken cancellationToken)
        => ExecuteOwnedGpuRemoteCallCoreAsync(state, key, execution, process, cancellationToken);

    private HostManagerRollbackStateDocument PrepareGpuApiObservationLedger(
        HostManagerRollbackStateDocument state, HostManagerPlacementReceiptKey key,
        HostManagerAutomaticPlacementProcess process, GpuRemoteCallRequest request)
    {
        var target = request.Process;
        if (request.Kind is not (GpuRemoteCallKind.LoadObservationProvider or GpuRemoteCallKind.StartApiObservation
                or GpuRemoteCallKind.ReadApiObservation or GpuRemoteCallKind.StopApiObservation)
            || string.IsNullOrWhiteSpace(process.TargetId) || string.IsNullOrWhiteSpace(process.SoftwareId)
            || string.IsNullOrWhiteSpace(process.ExecutablePath) || !Path.IsPathFullyQualified(process.ExecutablePath)
            || process.ProcessId <= 4 || process.ProcessStartKey == 0
            || target.ProcessId != process.ProcessId || target.ProcessStartKey != process.ProcessStartKey
            || !string.Equals(target.ExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("API observation requires its permitted, attributed exact process and an observation-only call.");

        var now = timeProvider.GetUtcNow();
        var placement = new HostManagerAppliedPlacementReceipt(process.TargetId, process.DisplayName, process.SoftwareId,
            OptimizationResourceKinds.Gpu, [], now, now);
        if (HostManagerPlacementReceiptKey.Create(placement) != key)
            throw new InvalidOperationException("API observation must remain in its original software's GPU ledger.");
        var existing = state.AppliedPlacements.SingleOrDefault(item => HostManagerPlacementReceiptKey.Create(item) == key);
        if (!IsGpuApiObservationPermitted(existing, process, request))
            throw new InvalidOperationException("API observation is not currently permitted and has no owned stop to perform.");
        if (existing is not null)
        {
            if (!string.Equals(existing.SoftwareId, process.SoftwareId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("API observation cannot adopt another software's ledger.");
            return state;
        }

        // SaveRemoteFactAsync persists this container together with the original call intent, before Start.
        return state with { AppliedPlacements = [.. state.AppliedPlacements, placement] };
    }

    private bool IsGpuApiObservationPermitted(HostManagerAppliedPlacementReceipt? existing,
        HostManagerAutomaticPlacementProcess process, GpuRemoteCallRequest request)
        => (runtimePlanProvider.Current.GpuPlacement.GlobalPreciseProviderEnabled
                && process.CanApplyPhysicalPlacement && process.Policy.AllowsStartupShimExecution())
            || (request.Kind == GpuRemoteCallKind.StopApiObservation
                && existing?.Records.Any(record => GpuRemoteCallRecord.TryRead(record, out var fact)
                    && fact.Request.Kind == GpuRemoteCallKind.StartApiObservation
                    && fact.Started?.ThreadId is not null
                    && fact.Request.Process.ProcessId == request.Process.ProcessId
                    && fact.Request.Process.ProcessStartKey == request.Process.ProcessStartKey
                    && string.Equals(fact.Request.Process.ExecutablePath, request.Process.ExecutablePath, StringComparison.OrdinalIgnoreCase)) == true);
}
