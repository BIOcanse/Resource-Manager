using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

/// <summary>Real ABI notifications and current-value decisions, with an independent business oracle.</summary>
public sealed class BackendNativeRuleEquivalenceTests
{
    private static readonly StateRead<bool>[] Values = [StateRead<bool>.FromValue(false), StateRead<bool>.FromValue(true),
        StateRead<bool>.NoValue(NoValueReason.NotPublished), StateRead<bool>.NoValue(NoValueReason.OwnerStopped),
        StateRead<bool>.NoValue(NoValueReason.ReadFailed)];
    private static readonly BusinessBlockReason Reason = BusinessBlockTestSupport.Reason();

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public async Task ControlReassertionRequiresEveryCombinationOfHostKeepAliveAndAppliedFacts(uint profile)
    {
        await using var native = new NativeControl(BackendExpectedProductionTree.Parents(profile).Keys,
            new NativeControlOptions { Profile = profile, ShutdownItemUid = BackendItems.ProgramShuttingDown },
            NullLogger<NativeControl>.Instance);
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var facts = new BackendPreciseRuleTestFacts(authority, native, nativeFacts: true);
        await facts.InitializeAsync();
        native.Activate(_ => new HoldingBlock());
        await BusinessBlockTestSupport.Until(() => native.ReadState(BlockUids.HardwareControl) == BusinessBlockState.Running);
        // Revisit true after every combination, so every false input must also stop an active block.
        foreach (var host in new[] { false, true })
        foreach (var keepAlive in new[] { false, true })
        foreach (var applied in new[] { false, true })
        {
            await Set(true, true, true);
            await Set(host, keepAlive, applied);
        }
        await native.ShutdownAsync(Reason);

        async Task Set(bool host, bool keepAlive, bool applied)
        {
            await facts[new(BackendItems.HostStartPermitted, BackendUids.ControlDesiredStateReassertion)].PublishAsync(host, Reason);
            await facts.PublishAsync(StateItemUids.ControlKeepAliveEnabled, keepAlive);
            await facts.PublishAsync(BackendItems.AppliedControlIntentsExist, applied);
            var expected = host && keepAlive && applied;
            await BusinessBlockTestSupport.Until(() => native.ReadState(BackendUids.ControlDesiredStateReassertion)
                == (expected ? BusinessBlockState.Running : BusinessBlockState.Stopped));
            AssertCondition(native, BlockUids.HardwareControl, ConditionUids.ControlReassertionShouldRun, null, expected);
        }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public async Task EveryDeclaredFactAndNoValueReachesItsPreciseProductionCondition(uint profile)
    {
        await using var native = new NativeControl(BackendExpectedProductionTree.Parents(profile).Keys,
            new NativeControlOptions { Profile = profile, ShutdownItemUid = BackendItems.ProgramShuttingDown, HistoryCapacity = 16 },
            NullLogger<NativeControl>.Instance);
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var facts = new BackendPreciseRuleTestFacts(authority, native, nativeFacts: true);
        await facts.InitializeAsync(true);
        native.Activate(_ => new HoldingBlock());
        using var registry = JsonDocument.Parse(File.ReadAllBytes(StateRegistryTests.RegistryPath()));
        var conditions = registry.RootElement.GetProperty("entries").EnumerateArray()
            .Where(entry => entry.GetProperty("process").GetString() == "backend" && entry.GetProperty("kind").GetString() == "condition")
            .ToArray();
        foreach (var declaration in conditions)
        {
            var perBlock = declaration.GetProperty("perBlock").GetBoolean();
            var child = perBlock ? BackendUids.Backend : declaration.GetProperty("decides").GetInt32();
            if (!native.Graph.BlockIds.Contains(child)) continue;
            var parent = native.Graph.Nodes.Single(node => node.Id == child).ParentId;
            await facts.InitializeAsync(true);
            var keys = declaration.GetProperty("items").EnumerateArray().Select(read => new StateItemKey(
                read.GetProperty("item").GetInt32(), read.TryGetProperty("block", out var block) ? block.GetInt32()
                    : read.GetProperty("item").GetInt32() == BackendItems.HostStartPermitted ? child : null)).ToArray();
            foreach (var baseline in new[] { false, true })
            {
                foreach (var key in keys)
                    await facts[key].PublishAsync(key.ItemUid == BackendItems.HostStartPermitted || baseline, Reason);
                foreach (var key in keys)
                {
                    foreach (var value in Values)
                    {
                        await PublishAsync(facts[key], value);
                        // Program is the decision root, not a hosted run entry exposed by ReadState.
                        if (parent != native.Graph.Program)
                            await BusinessBlockTestSupport.Until(() => native.ReadState(parent) == BusinessBlockState.Running);
                        AssertCondition(native, parent, declaration.GetProperty("uid").GetInt32(), perBlock ? child : null,
                            Expected(child, authority));
                    }
                    await facts[key].PublishAsync(key.ItemUid == BackendItems.HostStartPermitted || baseline, Reason);
                }
            }
        }
        await native.ShutdownAsync(Reason);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public async Task KernelLeakObservationConditionReadsOnlyEnablementAndRejectsEveryNoValue(uint profile)
    {
        await using var native = new NativeControl(BackendExpectedProductionTree.Parents(profile).Keys,
            new NativeControlOptions { Profile = profile, ShutdownItemUid = BackendItems.ProgramShuttingDown },
            NullLogger<NativeControl>.Instance);
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var facts = new BackendPreciseRuleTestFacts(authority, native, nativeFacts: true);
        await facts.InitializeAsync();
        var enabled = facts[ResourceManager.App.StateItems.StateItemUids.KernelLeakObservationEnabled];
        native.Activate(_ => new HoldingBlock());
        await BusinessBlockTestSupport.Until(() => native.ReadState(BackendUids.Reports) == BusinessBlockState.Running);
        foreach (var input in Values)
        foreach (var hostPermitted in new[] { false, true })
        {
            await facts[new(BackendItems.HostStartPermitted, BackendUids.KernelLeakObservation)].PublishAsync(hostPermitted, Reason);
            await PublishAsync(enabled, input);
            var expected = input.GetValueOrDefault(false);
            AssertCondition(native, BackendUids.Reports, ResourceManager.App.StateItems.ConditionUids.KernelLeakObservationShouldRun, null, expected);
            await BusinessBlockTestSupport.Until(() => native.ReadState(BackendUids.KernelLeakObservation)
                == (expected ? BusinessBlockState.Running : BusinessBlockState.Stopped));
        }
    }

    [Fact]
    public async Task TrayWithoutPageRunsSmartDefaultReportsAndProtectionButOnlyTheirProducerFields()
    {
        await using var native = new NativeControl(BackendExpectedProductionTree.Parents(1).Keys,
            new NativeControlOptions { Profile = 1, ShutdownItemUid = BackendItems.ProgramShuttingDown, HistoryCapacity = 128 },
            NullLogger<NativeControl>.Instance);
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var facts = new BackendPreciseRuleTestFacts(authority, native, nativeFacts: true);
        await facts.InitializeAsync();
        foreach (var item in new[] { BackendItems.SamplingHardwareMetricsInUse, BackendItems.SamplingCpuResidencyInUse,
            BackendItems.SamplingInterruptsInUse, BackendItems.SamplingResourceBreakdownInUse,
            BackendItems.ProcessDedicatedGpuMemoryInUse, BackendItems.OptimizationEnabled, BackendItems.GpuApiHistoryEnabled,
            BackendItems.BackgroundReportsEnabled, BackendItems.PortableDiscoveryEnabled,
            BackendItems.HostMemoryProtectionEnabled, BackendItems.MemoryProtectionNormalRunningPointExists,
            BackendItems.MemoryProtectionReserveBuilt, BackendItems.PresentNativeSessionAvailable,
            BackendItems.GpuAllocationNativeSessionAvailable, BackendItems.VidMmNativeSessionAvailable })
            await facts.PublishAsync(item, true);
        await facts[BackendItems.PresentUnfrozen].PublishNoValueAsync(NoValueReason.NotPublished, Reason);
        await facts[BackendItems.VidMmUnfrozen].PublishNoValueAsync(NoValueReason.NotPublished, Reason);
        native.Activate(_ => new HoldingBlock());
        var running = BackendExpectedProductionTree.Wrappers.Concat(new[] {
            BackendUids.RuntimeSpecializationCoordinator, BackendUids.ServiceAutoStartSettings, BackendUids.ServiceRecoverySettings,
            BackendUids.SqlitePortableSoftwareRegistry, BackendUids.TrustedAccessInvocationHostedService,
            BackendUids.NativeEtwLifetimeService, BackendUids.HostManagerSamplingSubscriptionOwner,
            BackendUids.WindowsHardwareMetricSampler, BackendUids.EtwCpuCoreResidencyReader,
            BackendUids.EtwSystemInterruptSnapshotSource, BackendUids.WindowsResourceBreakdownSampler,
            BackendUids.GpuAllocationCollector, BackendUids.HostManagerSmartCoordinator,
            BackendUids.GpuPlacementProcessHistoryObserver, BackendUids.HostManagerReportCoordinatorOwner,
            BackendUids.PortableSoftwareDiscoveryService, BackendUids.HostMemoryProtection }).ToHashSet();
        await BusinessBlockTestSupport.Until(() => running.SetEquals(native.Read().Blocks
            .Where(row => row.State == BusinessBlockState.Running).Select(row => row.Id)));
        Assert.DoesNotContain(native.Read().History, row => row.To == BusinessBlockState.Starting && !running.Contains(row.BlockId));
        await facts.PublishAsync(BackendItems.GraphicsApiObservationInUse, true);
        await BusinessBlockTestSupport.Until(() => native.ReadState(BackendUids.PresentFrameEtwSource) == BusinessBlockState.Running);
        await facts.PublishAsync(BackendItems.GraphicsApiObservationInUse, false);
        await BusinessBlockTestSupport.Until(() => native.ReadState(BackendUids.PresentFrameEtwSource) == BusinessBlockState.Stopped);
        Assert.Equal(BusinessBlockState.Running, native.ReadState(BackendUids.GpuAllocationCollector));
        await native.ShutdownAsync(Reason);
    }

    // These expressions follow the old local business gates, independently of native rule metadata.
    private static bool Expected(int child, StateAuthority authority)
    {
        bool T(int item) => authority.Read<bool>(item).GetValueOrDefault(false);
        bool P(int block) => authority.Read<bool>(new(BackendItems.HostStartPermitted, block)).GetValueOrDefault(false);
        bool M(int item) { var value = authority.Read<bool>(item); return value.HasValue ? value.Value : value.NoValueReason == NoValueReason.NotPublished; }
        bool Present() => P(BackendUids.PresentFrameEtwSource) && T(BackendItems.GraphicsApiObservationInUse)
            && T(BackendItems.PresentNativeSessionAvailable) && M(BackendItems.PresentUnfrozen);
        bool Allocation() => P(BackendUids.GpuAllocationCollector) && T(BackendItems.ProcessDedicatedGpuMemoryInUse) && T(BackendItems.GpuAllocationNativeSessionAvailable);
        bool VidMm() => P(BackendUids.DxgkrnlVidMmEtwTelemetryZone) && T(BackendItems.VramResidualInUse)
            && T(BackendItems.VidMmNativeSessionAvailable) && M(BackendItems.VidMmUnfrozen);
        if (child == BackendUids.KernelLeakObservation) return T(BackendItems.KernelLeakObservationEnabled);
        if (child == BackendUids.FanCurveTemperatureFeed) return T(BackendItems.SoftwareCurveRunNeeded);
        if (!P(child)) return false;
        return child switch
        {
            BlockUids.HardwareBridgeProcessOwner => T(StateItemUids.HardwareBridgeAvailable)
                && (T(StateItemUids.HardwareCommandPending) || T(StateItemUids.HardwareBridgeReadingsInUse)),
            BackendUids.PresentFrameEtwSource => Present(),
            BackendUids.GpuAllocationCollector => Allocation(),
            BackendUids.DxgkrnlVidMmEtwTelemetryZone => VidMm(),
            BackendUids.NativeEtwLifetimeService => Present() || Allocation() || VidMm(),
            BackendUids.HostMemoryProtection => T(BackendItems.HostMemoryProtectionEnabled) && T(BackendItems.MemoryProtectionNormalRunningPointExists) && T(BackendItems.MemoryProtectionReserveBuilt),
            BackendUids.JsonDebugDiagnosticLogWriter => T(BackendItems.DebugLoggingEnabled) || T(BackendItems.DebugWriterPending),
            BackendUids.AutomaticProductUpdateService => T(BackendItems.AutomaticUpdatesEnabled),
            BackendUids.HostManagerSamplingSubscriptionOwner => T(BackendItems.SamplingGpuTelemetryInUse) || T(BackendItems.SamplingHardwareMetricsInUse) || T(BackendItems.SamplingCpuTopologyInUse) || T(BackendItems.SamplingCpuResidencyInUse) || T(BackendItems.SamplingInterruptsInUse) || T(BackendItems.SamplingResourceBreakdownInUse) || T(BackendItems.SamplingDeviceTopologyInUse),
            BackendUids.GpuTelemetryWorkerSupervisor => T(BackendItems.SamplingGpuTelemetryInUse),
            BackendUids.WindowsHardwareMetricSampler => T(BackendItems.SamplingHardwareMetricsInUse),
            BackendUids.CpuTopologySnapshotProvider => T(BackendItems.SamplingCpuTopologyInUse),
            BackendUids.EtwCpuCoreResidencyReader => T(BackendItems.SamplingCpuResidencyInUse),
            BackendUids.EtwSystemInterruptSnapshotSource => T(BackendItems.SamplingInterruptsInUse),
            BackendUids.WindowsResourceBreakdownSampler => T(BackendItems.SamplingResourceBreakdownInUse),
            BackendUids.DeviceTopologySnapshotProvider => T(BackendItems.SamplingDeviceTopologyInUse),
            BackendUids.EtwPhysicalDiskIoAttributionReader => T(BackendItems.DiskAttributionInUse),
            BackendUids.EtwNetworkTcpIpAttributionReader => T(BackendItems.NetworkAttributionInUse),
            BackendUids.FanControlCoreLifetimeService => T(BackendItems.FanCoreAvailable) && (T(BackendItems.AppliedFanControlExists) || T(BackendItems.FanCommandPending) || T(BackendItems.FanReadingsInUse) || T(BackendItems.SoftwareCurveRunNeeded)),
            BackendUids.FanCurveTemperatureFeed => T(BackendItems.SoftwareCurveRunNeeded),
            BackendUids.ControlDesiredStateReassertion => T(StateItemUids.ControlKeepAliveEnabled) && T(BackendItems.AppliedControlIntentsExist),
            BackendUids.ControlActualStateReader => T(BackendItems.ActualControlValuesInUse),
            BackendUids.HostManagerSmartCoordinator => T(BackendItems.OptimizationEnabled) || T(BackendItems.ActiveCpuLeasesExist) || T(BackendItems.SmartOwnedStatePending),
            BackendUids.GpuPlacementProcessHistoryObserver => T(BackendItems.GpuApiHistoryEnabled),
            BackendUids.HostManagerReportCoordinatorOwner => T(BackendItems.BackgroundReportsEnabled),
            BackendUids.PortableSoftwareDiscoveryService => T(BackendItems.PortableDiscoveryEnabled),
            BackendUids.TrustedAccessRequestOwner => T(BackendItems.TrustedRequestsPending),
            BackendUids.HostManagerPublicServiceCoordinatorOwner => T(BackendItems.PublicServiceEnabled) || T(BackendItems.PublicServiceTasksPending),
            BackendUids.SharedResourceSubscriptionMaintenanceService => T(BackendItems.SharedResourceActivityExists),
            BackendUids.HostManagerOperationCoordinatorOwner => T(BackendItems.OperationsPending),
            _ => true
        };
    }
    private static Task PublishAsync(StateCell<bool> cell, StateRead<bool> value)
        => value.HasValue ? cell.PublishAsync(value.Value, Reason) : cell.PublishNoValueAsync(value.NoValueReason!.Value, Reason);
    private static void AssertCondition(NativeControl native, int parent, int uid, int? blockUid, bool expected)
    {
        var node = native.SnapshotJson().GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("uid").GetInt32() == parent);
        var condition = node.GetProperty("conditions").EnumerateArray().Single(value => value.GetProperty("uid").GetInt32() == uid
            && (blockUid is null ? !value.TryGetProperty("blockUid", out _)
                : value.TryGetProperty("blockUid", out var block) && block.GetInt32() == blockUid));
        Assert.Equal(JsonValueKind.Null, condition.GetProperty("lastError").ValueKind);
        Assert.Equal(expected, condition.GetProperty("value").GetBoolean());
    }
    private sealed class HoldingBlock : IBusinessBlock
    {
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            context.ReportRunning();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        }
    }
}
