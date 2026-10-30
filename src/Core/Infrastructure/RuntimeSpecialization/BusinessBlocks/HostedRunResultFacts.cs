using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>A process-lifetime record of ended worker runs, not a claim about resource availability.</summary>
internal sealed class HostedRunResultFacts
{
    internal static readonly int[] SupervisedBlocks =
    [
        BlockUids.RuntimeSpecializationCoordinator, BlockUids.HostManagerPublicServiceCoordinatorOwner,
        BlockUids.HostManagerOperationCoordinatorOwner, BlockUids.TrustedAccessRequestOwner,
        BlockUids.FanCurveTemperatureFeed, BlockUids.AutomaticProductUpdateService,
        BlockUids.ControlDesiredStateReassertion, BlockUids.ControlActualStateReader,
        BlockUids.GpuPlacementProcessHistoryObserver,
        BlockUids.JsonDebugDiagnosticLogWriter, BlockUids.HostManagerReportCoordinatorOwner,
        BlockUids.HostManagerSmartCoordinator, BlockUids.PortableSoftwareDiscoveryService,
        BlockUids.EtwPhysicalDiskIoAttributionReader, BlockUids.EtwNetworkTcpIpAttributionReader,
        BlockUids.TrustedAccessInvocationHostedService
    ];
    private readonly Dictionary<int, Entry> entries = [];

    public HostedRunResultFacts(StateAuthority authority, NativeControl native)
    {
        foreach (var id in native.Graph.BlockIds)
        {
            var cell = authority.ClaimCell<bool>(new(StateItemUids.HostedRunEnded, id));
            entries.Add(id, new(cell));
            _ = cell.PublishAsync(false, Reason("initial"));
        }
    }

    internal long CaptureInputs(int id)
    {
        var entry = entries[id];
        lock (entry) return entry.InputVersion;
    }

    internal void RecordEnd(int id, long runInputs)
    {
        if (!SupervisedBlocks.Contains(id)) return;
        var entry = entries[id];
        lock (entry)
            // A new plan, readmission or resource recovery may arrive while
            // this run drains. Its result must not overwrite that newer input.
            if (entry.InputVersion == runInputs)
                _ = entry.Cell.PublishAsync(true, Reason("run-ended"));
    }

    // Only actual host readmission, a new compiled plan, or a resource
    // holder's successful recovery check clears the ended result's decision gate.
    internal void Reset(int id)
    {
        if (!entries.TryGetValue(id, out var entry)) return;
        lock (entry)
        {
            entry.InputVersion++;
            _ = entry.Cell.PublishAsync(false, Reason("inputs-changed"));
        }
    }

    internal void PlanChanged()
    {
        foreach (var id in SupervisedBlocks) Reset(id);
    }

    private static BusinessBlockReason Reason(string cause)
        => new(BusinessBlockRequestKind.Dependency, nameof(HostedRunResultFacts), cause, 0);

    private sealed class Entry(StateCell<bool> cell)
    {
        internal readonly StateCell<bool> Cell = cell;
        internal long InputVersion;
    }
}
