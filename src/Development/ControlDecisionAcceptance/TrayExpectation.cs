using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.ControlDecisionAcceptance;

// Independent tray expectation from the accepted table and ABI fixture; availability cannot shrink it.
internal static class TrayExpectation
{
    private static readonly int[] FixedRunning = [
        BlockUids.Backend, BlockUids.ServiceHost, BlockUids.Monitoring, BlockUids.HardwareControl,
        BlockUids.Optimization, BlockUids.Reports, BlockUids.SoftwareManagement,
        BlockUids.TrustedAccess, BlockUids.PublicServices,
        BlockUids.RuntimeSpecializationCoordinator, BlockUids.ServiceAutoStartSettings, BlockUids.ServiceRecoverySettings,
        BlockUids.SqlitePortableSoftwareRegistry, BlockUids.TrustedAccessInvocationHostedService,
        BlockUids.NativeEtwLifetimeService, BlockUids.HostManagerSamplingSubscriptionOwner,
        BlockUids.WindowsHardwareMetricSampler, BlockUids.EtwCpuCoreResidencyReader,
        BlockUids.EtwSystemInterruptSnapshotSource, BlockUids.WindowsResourceBreakdownSampler,
        BlockUids.GpuAllocationCollector, BlockUids.HostManagerSmartCoordinator,
        BlockUids.GpuPlacementProcessHistoryObserver, BlockUids.HostManagerReportCoordinatorOwner,
        BlockUids.PortableSoftwareDiscoveryService, BlockUids.HostMemoryProtection
    ];
    private static readonly int[] RequiredTrueFacts = [
        StateItemUids.SamplingHardwareMetricsInUse, StateItemUids.SamplingCpuResidencyInUse,
        StateItemUids.SamplingInterruptsInUse, StateItemUids.SamplingResourceBreakdownInUse,
        StateItemUids.ProcessDedicatedGpuMemoryInUse, StateItemUids.OptimizationEnabled,
        StateItemUids.GpuApiHistoryEnabled, StateItemUids.BackgroundReportsEnabled,
        StateItemUids.PortableDiscoveryEnabled, StateItemUids.HostMemoryProtectionEnabled,
        StateItemUids.MemoryProtectionNormalRunningPointExists, StateItemUids.MemoryProtectionReserveBuilt
    ];
    private static readonly int[] RequiredFalseFacts = [
        StateItemUids.DebugLoggingEnabled, StateItemUids.AutomaticUpdatesEnabled, StateItemUids.PublicServiceEnabled,
        StateItemUids.AppliedFanControlExists,
        StateItemUids.SamplingCpuTopologyInUse, StateItemUids.SamplingDeviceTopologyInUse,
        StateItemUids.SamplingGpuTelemetryInUse
    ];

    internal static Comparison Compare(BusinessBlocksSnapshot snapshot, StateAuthority authority)
    {
        var expected = FixedRunning.ToHashSet();
        if (authority.Read<bool>(StateItemUids.GraphicsApiObservationInUse).GetValueOrDefault(false))
            expected.Add(BlockUids.PresentFrameEtwSource);
        var actual = snapshot.Blocks.Where(block => block.State == BusinessBlockState.Running).Select(block => block.Id).ToHashSet();
        var badFacts = new List<string>();
        foreach (var uid in RequiredTrueFacts)
        {
            var value = authority.Read<bool>(uid);
            if (!value.HasValue || !value.Value) badFacts.Add($"{uid}:expected-true:{value.NoValueReason}");
        }
        foreach (var uid in RequiredFalseFacts)
        {
            var value = authority.Read<bool>(uid);
            if (!value.HasValue || value.Value) badFacts.Add($"{uid}:expected-false:{value.NoValueReason}");
        }
        return new(expected.Order().ToArray(), actual.Order().ToArray(), expected.Except(actual).Order().ToArray(),
            actual.Except(expected).Order().ToArray(), snapshot.Blocks.Where(block => block.State is BusinessBlockState.Starting or BusinessBlockState.Stopping)
                .Select(block => block.Id).Order().ToArray(), badFacts.ToArray());
    }
    internal sealed record Comparison(int[] ExpectedRunning, int[] ActualRunning, int[] MissingRunning,
        int[] UnexpectedRunning, int[] Transitional, string[] WrongFacts)
    {
        public bool Matches => MissingRunning.Length == 0 && UnexpectedRunning.Length == 0 && Transitional.Length == 0 && WrongFacts.Length == 0;
    }
}
