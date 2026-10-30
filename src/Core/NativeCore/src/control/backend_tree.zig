//! Backend production control trees for the supported host registrations.
//!
//! Full and Windows Service share their items, conditions, and common blocks.
//! The service host adds AutomaticProductUpdateService only when mutable
//! persistence is enabled, so it needs a separate exact compile-time tree.

const control = @import("rm_control");
const generated = @import("backend_declarations");
const d = control.declarations;
const tree = control.tree;
const BlockUids = generated.BlockUids;
const ItemUids = generated.ItemUids;
const ConditionUids = generated.ConditionUids;

const all_registry = generated.registry;
// Profile filters scan at most blocks x declarations, with a fixed allowance per entry.
const declaration_branch_quota = 16 * (all_registry.blocks.len + 1) * (all_registry.conditions.len + 1);

fn withoutAutomaticUpdate() [all_registry.blocks.len - 1]d.Block {
    var selected: [all_registry.blocks.len - 1]d.Block = undefined;
    var next: usize = 0;
    for (all_registry.blocks) |block| {
        if (block.uid == BlockUids.AutomaticProductUpdateService) continue;
        selected[next] = block;
        next += 1;
    }
    if (next != selected.len) @compileError("backend registry must contain AutomaticProductUpdateService exactly once");
    return selected;
}

const full_registry_blocks = withoutAutomaticUpdate();
fn normalReadOnlyBlockCount() usize {
    @setEvalBranchQuota(declaration_branch_quota);
    var count: usize = 0;
    for (all_registry.blocks) |block| {
        if (normalReadOnlyBlock(block.uid)) count += 1;
    }
    return count;
}
fn normalReadOnlyConditionCount() usize {
    @setEvalBranchQuota(declaration_branch_quota);
    var count: usize = 0;
    for (all_registry.conditions) |condition| {
        if (normalReadOnlyCondition(condition.uid)) count += 1;
    }
    return count;
}
fn fullConditions() [all_registry.conditions.len - 1]d.Condition {
    var result: [all_registry.conditions.len - 1]d.Condition = undefined;
    var next: usize = 0;
    for (all_registry.conditions) |condition| {
        if (condition.decides == BlockUids.AutomaticProductUpdateService) continue;
        result[next] = condition;
        next += 1;
    }
    if (next != result.len) @compileError("full profile must exclude exactly the automatic-update rule");
    return result;
}
const full_conditions = fullConditions();

/// The Full host composition has all backend declarations except the service-only update worker.
pub const registry: d.Registry = .{
    .version = all_registry.version,
    .process = all_registry.process,
    .blocks = &full_registry_blocks,
    .items = all_registry.items,
    .conditions = &full_conditions,
};

/// The Windows Service + MutablePersistence composition includes the update worker.
pub const service_registry: d.Registry = all_registry;
pub const declarations = registry;
pub const service_declarations = service_registry;

fn normalReadOnlyBlock(uid: d.BlockUid) bool {
    if (uid == BlockUids.Backend) return true;
    if (domainBlock(uid)) {
        // A profile retains a domain exactly when it retains a direct member.
        for (all_tree_blocks) |block| {
            if (block.parent == uid and normalReadOnlyMember(block.uid)) return true;
        }
        return false;
    }
    return normalReadOnlyMember(uid);
}

fn normalReadOnlyMember(uid: d.BlockUid) bool {
    return switch (uid) {
        BlockUids.Program,
        BlockUids.NativeEtwLifetimeService,
        BlockUids.TrustedAccessRequestOwner,
        BlockUids.TrustedAccessInvocationHostedService,
        BlockUids.RuntimeSpecializationCoordinator,
        BlockUids.HostManagerSamplingSubscriptionOwner,
        BlockUids.ServiceAutoStartSettings,
        BlockUids.ServiceRecoverySettings,
        BlockUids.FanControlCoreLifetimeService,
        BlockUids.HardwareBridgeProcessOwner,
        BlockUids.EtwPhysicalDiskIoAttributionReader,
        BlockUids.EtwNetworkTcpIpAttributionReader,
        BlockUids.PresentFrameEtwSource,
        BlockUids.GpuAllocationCollector,
        BlockUids.DxgkrnlVidMmEtwTelemetryZone,
        BlockUids.DeviceTopologySnapshotProvider,
        BlockUids.GpuTelemetryWorkerSupervisor,
        BlockUids.WindowsHardwareMetricSampler,
        BlockUids.CpuTopologySnapshotProvider,
        BlockUids.EtwCpuCoreResidencyReader,
        BlockUids.EtwSystemInterruptSnapshotSource,
        BlockUids.WindowsResourceBreakdownSampler,
        BlockUids.KernelLeakObservation,
        => true,
        else => false,
    };
}

fn normalReadOnlyRegistryBlocks() [normalReadOnlyBlockCount()]d.Block {
    var selected: [normalReadOnlyBlockCount()]d.Block = undefined;
    var next: usize = 0;
    for (all_registry.blocks) |block| {
        if (!normalReadOnlyBlock(block.uid)) continue;
        if (next >= selected.len) @compileError("normal-read-only backend registry has an unexpected block");
        selected[next] = block;
        next += 1;
    }
    if (next != selected.len) @compileError("normal-read-only backend registry must contain its exact fixed block set");
    return selected;
}

fn normalReadOnlyConditions() [normalReadOnlyConditionCount()]d.Condition {
    @setEvalBranchQuota(declaration_branch_quota);
    var selected: [normalReadOnlyConditionCount()]d.Condition = undefined;
    var next: usize = 0;
    for (all_registry.conditions) |condition| {
        if (!normalReadOnlyCondition(condition.uid)) continue;
        if (next >= selected.len) @compileError("normal-read-only registry has an unexpected condition");
        selected[next] = condition;
        next += 1;
    }
    if (next != selected.len) @compileError("normal-read-only registry must retain its exact registered conditions");
    return selected;
}

const normal_readonly_registry_blocks = normalReadOnlyRegistryBlocks();
const normal_readonly_conditions = normalReadOnlyConditions();

/// Exact `normal-read-only` startup declaration subset. Items have component owners,
/// so all backend items remain available while capability-gated blocks and rules are removed.
pub const normal_readonly_registry: d.Registry = .{
    .version = all_registry.version,
    .process = all_registry.process,
    .blocks = &normal_readonly_registry_blocks,
    .items = all_registry.items,
    .conditions = &normal_readonly_conditions,
};
pub const normal_readonly_declarations = normal_readonly_registry;

const all_tree_blocks = [_]tree.Block{
    .{ .uid = BlockUids.Program, .parent = null },
    .{ .uid = BlockUids.Backend, .parent = BlockUids.Program },
    .{ .uid = BlockUids.ServiceHost, .parent = BlockUids.Backend },
    .{ .uid = BlockUids.Monitoring, .parent = BlockUids.Backend },
    .{ .uid = BlockUids.HardwareControl, .parent = BlockUids.Backend },
    .{ .uid = BlockUids.Optimization, .parent = BlockUids.Backend },
    .{ .uid = BlockUids.Reports, .parent = BlockUids.Backend },
    .{ .uid = BlockUids.SoftwareManagement, .parent = BlockUids.Backend },
    .{ .uid = BlockUids.TrustedAccess, .parent = BlockUids.Backend },
    .{ .uid = BlockUids.PublicServices, .parent = BlockUids.Backend },
    .{ .uid = BlockUids.NativeEtwLifetimeService, .parent = BlockUids.Monitoring },
    .{ .uid = BlockUids.TrustedAccessRequestOwner, .parent = BlockUids.TrustedAccess, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.TrustedAccessInvocationHostedService, .parent = BlockUids.TrustedAccess },
    .{ .uid = BlockUids.RuntimeSpecializationCoordinator, .parent = BlockUids.ServiceHost },
    .{ .uid = BlockUids.AutomaticProductUpdateService, .parent = BlockUids.ServiceHost, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.HostManagerSamplingSubscriptionOwner, .parent = BlockUids.Monitoring, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.HostManagerPublicServiceCoordinatorOwner, .parent = BlockUids.PublicServices, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.HostManagerOperationCoordinatorOwner, .parent = BlockUids.Backend, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.SqlitePortableSoftwareRegistry, .parent = BlockUids.SoftwareManagement, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.ServiceAutoStartSettings, .parent = BlockUids.ServiceHost, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.ServiceRecoverySettings, .parent = BlockUids.ServiceHost, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.ControlDesiredStateReassertion, .parent = BlockUids.HardwareControl, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.FanCurveTemperatureFeed, .parent = BlockUids.FanControlCoreLifetimeService, .inputs = &.{BlockUids.WindowsHardwareMetricSampler} },
    .{ .uid = BlockUids.ControlActualStateReader, .parent = BlockUids.HardwareControl },
    .{ .uid = BlockUids.HardwareBridgeProcessOwner, .parent = BlockUids.HardwareControl },
    .{ .uid = BlockUids.GpuTelemetryWorkerSupervisor, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{BlockUids.HostManagerSamplingSubscriptionOwner} },
    .{ .uid = BlockUids.WindowsHardwareMetricSampler, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{
        BlockUids.RuntimeSpecializationCoordinator,
        BlockUids.HostManagerSamplingSubscriptionOwner,
    } },
    .{ .uid = BlockUids.CpuTopologySnapshotProvider, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{BlockUids.HostManagerSamplingSubscriptionOwner} },
    .{ .uid = BlockUids.FanControlCoreLifetimeService, .parent = BlockUids.HardwareControl },
    .{ .uid = BlockUids.PortableSoftwareDiscoveryService, .parent = BlockUids.SoftwareManagement, .inputs = &.{BlockUids.SqlitePortableSoftwareRegistry} },
    .{ .uid = BlockUids.EtwCpuCoreResidencyReader, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{ BlockUids.RuntimeSpecializationCoordinator, BlockUids.HostManagerSamplingSubscriptionOwner } },
    .{ .uid = BlockUids.EtwSystemInterruptSnapshotSource, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{BlockUids.HostManagerSamplingSubscriptionOwner} },
    .{ .uid = BlockUids.EtwPhysicalDiskIoAttributionReader, .parent = BlockUids.Monitoring },
    .{ .uid = BlockUids.EtwNetworkTcpIpAttributionReader, .parent = BlockUids.Monitoring },
    .{ .uid = BlockUids.WindowsResourceBreakdownSampler, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{ BlockUids.RuntimeSpecializationCoordinator, BlockUids.WindowsHardwareMetricSampler, BlockUids.HostManagerSamplingSubscriptionOwner, BlockUids.EtwPhysicalDiskIoAttributionReader, BlockUids.EtwNetworkTcpIpAttributionReader, BlockUids.GpuAllocationCollector, BlockUids.DxgkrnlVidMmEtwTelemetryZone } },
    .{ .uid = BlockUids.GpuPlacementProcessHistoryObserver, .parent = BlockUids.Optimization, .inputs = &.{ BlockUids.RuntimeSpecializationCoordinator, BlockUids.WindowsResourceBreakdownSampler, BlockUids.PresentFrameEtwSource, BlockUids.SqlitePortableSoftwareRegistry } },
    .{ .uid = BlockUids.JsonDebugDiagnosticLogWriter, .parent = BlockUids.ServiceHost, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.HostManagerReportCoordinatorOwner, .parent = BlockUids.Reports, .inputs = &.{ BlockUids.RuntimeSpecializationCoordinator, BlockUids.WindowsResourceBreakdownSampler, BlockUids.WindowsHardwareMetricSampler, BlockUids.EtwSystemInterruptSnapshotSource, BlockUids.SqlitePortableSoftwareRegistry } },
    .{ .uid = BlockUids.KernelLeakObservation, .parent = BlockUids.Reports, .inputs = &.{BlockUids.WindowsResourceBreakdownSampler} },
    .{ .uid = BlockUids.HostManagerSmartCoordinator, .parent = BlockUids.Optimization, .inputs = &.{ BlockUids.RuntimeSpecializationCoordinator, BlockUids.WindowsResourceBreakdownSampler, BlockUids.WindowsHardwareMetricSampler, BlockUids.EtwCpuCoreResidencyReader } },
    .{ .uid = BlockUids.DeviceTopologySnapshotProvider, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{BlockUids.HostManagerSamplingSubscriptionOwner} },
    .{ .uid = BlockUids.PresentFrameEtwSource, .parent = BlockUids.NativeEtwLifetimeService },
    .{ .uid = BlockUids.GpuAllocationCollector, .parent = BlockUids.NativeEtwLifetimeService },
    .{ .uid = BlockUids.DxgkrnlVidMmEtwTelemetryZone, .parent = BlockUids.NativeEtwLifetimeService },
    .{ .uid = BlockUids.HostMemoryProtection, .parent = BlockUids.Backend, .inputs = &.{ BlockUids.WindowsHardwareMetricSampler, BlockUids.WindowsResourceBreakdownSampler } },
};

fn normalReadOnlyTreeBlocks() [normalReadOnlyBlockCount()]tree.Block {
    var selected: [normalReadOnlyBlockCount()]tree.Block = undefined;
    var next: usize = 0;
    var root_child_count: usize = 0;
    for (all_tree_blocks) |block| {
        if (!normalReadOnlyBlock(block.uid)) continue;
        if (next >= selected.len) @compileError("normal-read-only topology has an unexpected block");
        for (block.inputs) |input| {
            if (!normalReadOnlyBlock(input)) @compileError("normal-read-only topology input is outside its exact fixed block set");
        }
        if (block.parent) |parent| if (parent == BlockUids.Program) {
            root_child_count += 1;
        };
        selected[next] = block;
        next += 1;
    }
    if (next != selected.len) @compileError("normal-read-only topology must contain its exact fixed block set");
    if (root_child_count != 1) @compileError("only Backend belongs directly under Program");
    return selected;
}

const normal_readonly_tree_blocks = normalReadOnlyTreeBlocks();

fn fullTreeBlocks() [all_tree_blocks.len - 1]tree.Block {
    var selected: [all_tree_blocks.len - 1]tree.Block = undefined;
    var next: usize = 0;
    for (all_tree_blocks) |block| {
        if (block.uid == BlockUids.AutomaticProductUpdateService) continue;
        selected[next] = block;
        next += 1;
    }
    if (next != selected.len) @compileError("backend topology must contain AutomaticProductUpdateService exactly once");
    return selected;
}

const full_tree_blocks = fullTreeBlocks();

fn hasFixedCondition(child: d.BlockUid) bool {
    @setEvalBranchQuota(declaration_branch_quota);
    for (all_registry.conditions) |condition| {
        if (condition.decides == child) return true;
    }
    return false;
}
fn normalReadOnlyCondition(uid: d.ConditionUid) bool {
    @setEvalBranchQuota(declaration_branch_quota);
    for (all_registry.conditions) |condition| {
        if (condition.uid != uid) continue;
        return if (condition.decides) |child| normalReadOnlyBlock(child) else true;
    }
    return false;
}

fn domainBlock(uid: d.BlockUid) bool {
    return switch (uid) {
        BlockUids.ServiceHost,
        BlockUids.Monitoring,
        BlockUids.HardwareControl,
        BlockUids.Optimization,
        BlockUids.Reports,
        BlockUids.SoftwareManagement,
        BlockUids.TrustedAccess,
        BlockUids.PublicServices,
        => true,
        else => false,
    };
}

fn perBlockUseCount(comptime blocks: []const tree.Block) usize {
    @setEvalBranchQuota(declaration_branch_quota);
    var count: usize = 0;
    for (blocks) |block| {
        if (block.parent == null or hasFixedCondition(block.uid)) continue;
        count += 1;
    }
    return count;
}

fn usesFor(comptime blocks: []const tree.Block) [perBlockUseCount(blocks)]tree.PerBlockUse {
    @setEvalBranchQuota(declaration_branch_quota);
    var uses: [perBlockUseCount(blocks)]tree.PerBlockUse = undefined;
    var next: usize = 0;
    for (blocks) |block| {
        const parent = block.parent orelse continue;
        if (hasFixedCondition(block.uid)) continue;
        uses[next] = .{
            .node = parent,
            .condition = ConditionUids.AlwaysWanted,
            .child = block.uid,
        };
        next += 1;
    }
    if (next != uses.len) @compileError("backend tree must route every child without a fixed condition");
    return uses;
}

fn readBool(input: *control.Input, item: d.ItemUid, block: ?d.BlockUid) anyerror!control.Read {
    return input.read(.{ .item = item, .block = block });
}
fn isTrue(input: *control.Input, item: d.ItemUid) anyerror!bool {
    return (try readBool(input, item, null)).valueOr(false);
}
fn hostPermitted(input: *control.Input, child: ?d.BlockUid) anyerror!bool {
    return (try readBool(input, ItemUids.HostStartPermitted, child)).valueOr(false);
}
fn anyFact(input: *control.Input, child: d.BlockUid, comptime facts: []const d.ItemUid) anyerror!bool {
    if (!try hostPermitted(input, child)) return false;
    if (legacyHostedWorker(child) and !try hostedRunAllowed(input, child)) return false;
    for (facts) |item| {
        if (try isTrue(input, item)) return true;
    }
    return false;
}
fn alwaysWanted(input: *control.Input) anyerror!bool {
    return try hostPermitted(input, input.block) and try hostedRunAllowed(input, input.block);
}
fn hostedRunAllowed(input: *control.Input, child: ?d.BlockUid) anyerror!bool {
    return switch (try readBool(input, ItemUids.HostedRunEnded, child)) {
        .value => |failed| !failed,
        // No failure has been published in an inert/entry-only fixture.
        .not_published => true,
        .owner_stopped, .read_failed => false,
    };
}
fn legacyHostedWorker(child: d.BlockUid) bool {
    return switch (child) {
        BlockUids.RuntimeSpecializationCoordinator,
        BlockUids.HostManagerPublicServiceCoordinatorOwner,
        BlockUids.HostManagerOperationCoordinatorOwner,
        BlockUids.TrustedAccessRequestOwner,
        BlockUids.FanCurveTemperatureFeed,
        BlockUids.AutomaticProductUpdateService,
        BlockUids.ControlDesiredStateReassertion,
        BlockUids.ControlActualStateReader,
        BlockUids.GpuPlacementProcessHistoryObserver,
        BlockUids.JsonDebugDiagnosticLogWriter,
        BlockUids.HostManagerReportCoordinatorOwner,
        BlockUids.HostManagerSmartCoordinator,
        BlockUids.PortableSoftwareDiscoveryService,
        BlockUids.EtwPhysicalDiskIoAttributionReader,
        BlockUids.EtwNetworkTcpIpAttributionReader,
        BlockUids.TrustedAccessInvocationHostedService,
        => true,
        else => false,
    };
}
fn nativeChild(input: *control.Input, child: d.BlockUid, field: d.ItemUid, available: d.ItemUid) anyerror!bool {
    if (!try hostPermitted(input, child) or !try isTrue(input, field) or !try isTrue(input, available)) return false;
    return true;
}
fn presentEtwChildShouldRun(input: *control.Input) anyerror!bool {
    return nativeChild(input, BlockUids.PresentFrameEtwSource, ItemUids.GraphicsApiObservationInUse, ItemUids.PresentNativeSessionAvailable);
}
fn gpuAllocationEtwChildShouldRun(input: *control.Input) anyerror!bool {
    return nativeChild(input, BlockUids.GpuAllocationCollector, ItemUids.ProcessDedicatedGpuMemoryInUse, ItemUids.GpuAllocationNativeSessionAvailable);
}
fn vidMmEtwChildShouldRun(input: *control.Input) anyerror!bool {
    return nativeChild(input, BlockUids.DxgkrnlVidMmEtwTelemetryZone, ItemUids.VramResidualInUse, ItemUids.VidMmNativeSessionAvailable);
}
fn nativeEtwGroupShouldRun(input: *control.Input) anyerror!bool {
    return try hostPermitted(input, BlockUids.NativeEtwLifetimeService) and
        (try presentEtwChildShouldRun(input) or try gpuAllocationEtwChildShouldRun(input) or try vidMmEtwChildShouldRun(input));
}
// HardwareControl decides its direct bridge child from resident owner facts.
fn hardwareBridgeShouldRun(input: *control.Input) anyerror!bool {
    if (!try isTrue(input, ItemUids.HardwareBridgeAvailable)) return false;
    return anyFact(input, BlockUids.HardwareBridgeProcessOwner, &.{
        ItemUids.HardwareCommandPending, ItemUids.HardwareBridgeReadingsInUse,
    });
}
fn hostMemoryProtectionReady(input: *control.Input) anyerror!bool {
    return try hostPermitted(input, BlockUids.HostMemoryProtection) and
        try isTrue(input, ItemUids.HostMemoryProtectionEnabled) and
        try isTrue(input, ItemUids.MemoryProtectionNormalRunningPointExists) and
        try isTrue(input, ItemUids.MemoryProtectionReserveBuilt);
}
fn kernelLeakObservationShouldRun(input: *control.Input) anyerror!bool {
    return isTrue(input, ItemUids.KernelLeakObservationEnabled);
}
fn debugWriterShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.JsonDebugDiagnosticLogWriter, &.{ ItemUids.DebugLoggingEnabled, ItemUids.DebugWriterPending });
}
fn automaticUpdateShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.AutomaticProductUpdateService, &.{ItemUids.AutomaticUpdatesEnabled});
}
fn samplingOwnerShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.HostManagerSamplingSubscriptionOwner, &.{ ItemUids.SamplingGpuTelemetryInUse, ItemUids.SamplingHardwareMetricsInUse, ItemUids.SamplingCpuTopologyInUse, ItemUids.SamplingCpuResidencyInUse, ItemUids.SamplingInterruptsInUse, ItemUids.SamplingResourceBreakdownInUse, ItemUids.SamplingDeviceTopologyInUse });
}
fn gpuTelemetryShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.GpuTelemetryWorkerSupervisor, &.{ItemUids.SamplingGpuTelemetryInUse});
}
fn hardwareMetricsShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.WindowsHardwareMetricSampler, &.{ItemUids.SamplingHardwareMetricsInUse});
}
fn cpuTopologyShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.CpuTopologySnapshotProvider, &.{ItemUids.SamplingCpuTopologyInUse});
}
fn cpuResidencyShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.EtwCpuCoreResidencyReader, &.{ItemUids.SamplingCpuResidencyInUse});
}
fn interruptsShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.EtwSystemInterruptSnapshotSource, &.{ItemUids.SamplingInterruptsInUse});
}
fn resourceBreakdownShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.WindowsResourceBreakdownSampler, &.{ItemUids.SamplingResourceBreakdownInUse});
}
fn deviceTopologyShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.DeviceTopologySnapshotProvider, &.{ItemUids.SamplingDeviceTopologyInUse});
}
fn diskAttributionShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.EtwPhysicalDiskIoAttributionReader, &.{ItemUids.DiskAttributionInUse});
}
fn networkAttributionShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.EtwNetworkTcpIpAttributionReader, &.{ItemUids.NetworkAttributionInUse});
}
fn fanCoreShouldRun(input: *control.Input) anyerror!bool {
    return try isTrue(input, ItemUids.FanCoreAvailable) and
        try anyFact(input, BlockUids.FanControlCoreLifetimeService, &.{ ItemUids.AppliedFanControlExists, ItemUids.FanCommandPending, ItemUids.FanReadingsInUse, ItemUids.SoftwareCurveRunNeeded });
}
fn fanTemperatureShouldRun(input: *control.Input) anyerror!bool {
    return try hostedRunAllowed(input, BlockUids.FanCurveTemperatureFeed) and try isTrue(input, ItemUids.SoftwareCurveRunNeeded);
}
fn controlReassertionShouldRun(input: *control.Input) anyerror!bool {
    return try hostPermitted(input, BlockUids.ControlDesiredStateReassertion) and
        try hostedRunAllowed(input, BlockUids.ControlDesiredStateReassertion) and
        try isTrue(input, ItemUids.ControlKeepAliveEnabled) and
        try isTrue(input, ItemUids.AppliedControlIntentsExist);
}
fn actualControlShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.ControlActualStateReader, &.{ItemUids.ActualControlValuesInUse});
}
fn smartShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.HostManagerSmartCoordinator, &.{ ItemUids.OptimizationEnabled, ItemUids.ActiveCpuLeasesExist, ItemUids.SmartOwnedStatePending });
}
fn gpuApiHistoryShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.GpuPlacementProcessHistoryObserver, &.{ItemUids.GpuApiHistoryEnabled});
}
fn backgroundReportsShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.HostManagerReportCoordinatorOwner, &.{ItemUids.BackgroundReportsEnabled});
}
fn portableDiscoveryShouldRun(input: *control.Input) anyerror!bool {
    return try isTrue(input, ItemUids.PortableAttributionCatalogAvailable) and
        try anyFact(input, BlockUids.PortableSoftwareDiscoveryService, &.{ItemUids.PortableDiscoveryEnabled});
}
fn trustedRequestsShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.TrustedAccessRequestOwner, &.{ItemUids.TrustedRequestsPending});
}
fn publicServiceShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.HostManagerPublicServiceCoordinatorOwner, &.{ ItemUids.PublicServiceEnabled, ItemUids.PublicServiceTasksPending });
}
fn operationsShouldRun(input: *control.Input) anyerror!bool {
    return anyFact(input, BlockUids.HostManagerOperationCoordinatorOwner, &.{ItemUids.OperationsPending});
}
const compute_functions = [_]control.types.Compute{
    .{ .condition = ConditionUids.HardwareBridgeShouldRun, .function = hardwareBridgeShouldRun },
    .{ .condition = ConditionUids.AlwaysWanted, .function = alwaysWanted },
    .{ .condition = ConditionUids.HostMemoryProtectionReady, .function = hostMemoryProtectionReady },
    .{ .condition = ConditionUids.KernelLeakObservationShouldRun, .function = kernelLeakObservationShouldRun },
    .{ .condition = ConditionUids.PresentEtwChildShouldRun, .function = presentEtwChildShouldRun },
    .{ .condition = ConditionUids.GpuAllocationEtwChildShouldRun, .function = gpuAllocationEtwChildShouldRun },
    .{ .condition = ConditionUids.VidMmEtwChildShouldRun, .function = vidMmEtwChildShouldRun },
    .{ .condition = ConditionUids.NativeEtwGroupShouldRun, .function = nativeEtwGroupShouldRun },
    .{ .condition = ConditionUids.DebugWriterShouldRun, .function = debugWriterShouldRun },
    .{ .condition = ConditionUids.AutomaticUpdateShouldRun, .function = automaticUpdateShouldRun },
    .{ .condition = ConditionUids.SamplingOwnerShouldRun, .function = samplingOwnerShouldRun },
    .{ .condition = ConditionUids.GpuTelemetryShouldRun, .function = gpuTelemetryShouldRun },
    .{ .condition = ConditionUids.HardwareMetricsShouldRun, .function = hardwareMetricsShouldRun },
    .{ .condition = ConditionUids.CpuTopologyShouldRun, .function = cpuTopologyShouldRun },
    .{ .condition = ConditionUids.CpuResidencyShouldRun, .function = cpuResidencyShouldRun },
    .{ .condition = ConditionUids.InterruptsShouldRun, .function = interruptsShouldRun },
    .{ .condition = ConditionUids.ResourceBreakdownShouldRun, .function = resourceBreakdownShouldRun },
    .{ .condition = ConditionUids.DeviceTopologyShouldRun, .function = deviceTopologyShouldRun },
    .{ .condition = ConditionUids.DiskAttributionShouldRun, .function = diskAttributionShouldRun },
    .{ .condition = ConditionUids.NetworkAttributionShouldRun, .function = networkAttributionShouldRun },
    .{ .condition = ConditionUids.FanCoreShouldRun, .function = fanCoreShouldRun },
    .{ .condition = ConditionUids.FanTemperatureShouldRun, .function = fanTemperatureShouldRun },
    .{ .condition = ConditionUids.ControlReassertionShouldRun, .function = controlReassertionShouldRun },
    .{ .condition = ConditionUids.ActualControlShouldRun, .function = actualControlShouldRun },
    .{ .condition = ConditionUids.SmartShouldRun, .function = smartShouldRun },
    .{ .condition = ConditionUids.GpuApiHistoryShouldRun, .function = gpuApiHistoryShouldRun },
    .{ .condition = ConditionUids.BackgroundReportsShouldRun, .function = backgroundReportsShouldRun },
    .{ .condition = ConditionUids.PortableDiscoveryShouldRun, .function = portableDiscoveryShouldRun },
    .{ .condition = ConditionUids.TrustedRequestsShouldRun, .function = trustedRequestsShouldRun },
    .{ .condition = ConditionUids.PublicServiceShouldRun, .function = publicServiceShouldRun },
    .{ .condition = ConditionUids.OperationsShouldRun, .function = operationsShouldRun },
};
fn fullComputes() [compute_functions.len - 1]control.types.Compute {
    var result: [compute_functions.len - 1]control.types.Compute = undefined;
    var next: usize = 0;
    for (compute_functions) |compute| {
        if (compute.condition == ConditionUids.AutomaticUpdateShouldRun) continue;
        result[next] = compute;
        next += 1;
    }
    if (next != result.len) @compileError("full profile must exclude exactly the automatic-update compute");
    return result;
}
const full_computes = fullComputes();

fn normalReadOnlyComputes() [normalReadOnlyConditionCount()]control.types.Compute {
    @setEvalBranchQuota(declaration_branch_quota);
    var selected: [normalReadOnlyConditionCount()]control.types.Compute = undefined;
    var next: usize = 0;
    for (compute_functions) |compute| {
        if (!normalReadOnlyCondition(compute.condition)) continue;
        if (next >= selected.len) @compileError("normal-read-only topology has an unexpected condition compute");
        selected[next] = compute;
        next += 1;
    }
    if (next != selected.len) @compileError("normal-read-only topology must compute its exact registered conditions");
    return selected;
}

const normal_readonly_computes = normalReadOnlyComputes();

const full_uses = usesFor(&full_tree_blocks);
const service_uses = usesFor(&all_tree_blocks);
const normal_readonly_uses = usesFor(&normal_readonly_tree_blocks);

/// Full host topology with precise business conditions and no resource-only blocks.
pub const topology: tree.Tree = .{
    .program = BlockUids.Program,
    .blocks = &full_tree_blocks,
    .uses = &full_uses,
    .computes = &full_computes,
};

/// Windows Service + MutablePersistence topology (adds the update worker).
pub const service_topology: tree.Tree = .{
    .program = BlockUids.Program,
    .blocks = &all_tree_blocks,
    .uses = &service_uses,
    .computes = &compute_functions,
};

/// Exact normal-read-only profile; structural domains exist only with retained children.
pub const normal_readonly_topology: tree.Tree = .{
    .program = BlockUids.Program,
    .blocks = &normal_readonly_tree_blocks,
    .uses = &normal_readonly_uses,
    .computes = &normal_readonly_computes,
};

fn hostBlockUids(comptime value: d.Registry) [value.blocks.len - 1]d.BlockUid {
    var result: [value.blocks.len - 1]d.BlockUid = undefined;
    var next: usize = 0;
    for (value.blocks) |block| {
        if (block.uid == BlockUids.Program) continue;
        result[next] = block.uid;
        next += 1;
    }
    if (next != result.len) @compileError("backend declarations must contain the program root exactly once");
    return result;
}

/// Exact non-root host block sets passed to the controller for each topology.
pub const full_host_block_uids = hostBlockUids(registry);
pub const service_host_block_uids = hostBlockUids(service_registry);
pub const normal_readonly_host_block_uids = hostBlockUids(normal_readonly_registry);

pub const Engine = control.Controller(registry, topology);
pub const ServiceEngine = control.Controller(service_registry, service_topology);
pub const NormalReadOnlyEngine = control.Controller(normal_readonly_registry, normal_readonly_topology);

// Independent business-rule oracle: never calls a production predicate.
const RuleTestFacts = struct {
    reads: []const d.ItemRead,
    values: [16]control.Read = undefined,
    child: d.BlockUid,
    fn get(self: *const @This(), item: d.ItemUid, block: ?d.BlockUid) control.Read {
        for (self.reads, 0..) |declared, index| {
            const key_block = if (declared.item == ItemUids.HostStartPermitted or declared.item == ItemUids.HostedRunEnded) declared.block orelse self.child else declared.block;
            if (declared.item == item and key_block == block) return self.values[index];
        }
        return .not_published;
    }
    fn truth(self: *const @This(), item: d.ItemUid) bool {
        return self.get(item, null).valueOr(false);
    }
    fn permit(self: *const @This(), child: d.BlockUid) bool {
        return self.get(ItemUids.HostStartPermitted, child).valueOr(false);
    }
    fn runAllowed(self: *const @This()) bool {
        return switch (self.get(ItemUids.HostedRunEnded, self.child)) {
            .value => |ended| !ended,
            .not_published => true,
            .owner_stopped, .read_failed => false,
        };
    }
    fn present(self: *const @This()) bool {
        return self.permit(BlockUids.PresentFrameEtwSource) and self.truth(ItemUids.GraphicsApiObservationInUse) and
            self.truth(ItemUids.PresentNativeSessionAvailable);
    }
    fn allocation(self: *const @This()) bool {
        return self.permit(BlockUids.GpuAllocationCollector) and self.truth(ItemUids.ProcessDedicatedGpuMemoryInUse) and self.truth(ItemUids.GpuAllocationNativeSessionAvailable);
    }
    fn vidmm(self: *const @This()) bool {
        return self.permit(BlockUids.DxgkrnlVidMmEtwTelemetryZone) and self.truth(ItemUids.VramResidualInUse) and
            self.truth(ItemUids.VidMmNativeSessionAvailable);
    }
    fn read(context: *anyopaque, key: control.types.ItemKey) anyerror!control.Read {
        const self: *@This() = @ptrCast(@alignCast(context));
        for (self.reads) |declared| {
            const block = if (declared.item == ItemUids.HostStartPermitted or declared.item == ItemUids.HostedRunEnded) declared.block orelse self.child else declared.block;
            if (declared.item == key.item and block == key.block) return self.get(key.item, key.block);
        }
        return error.UndeclaredTestRead;
    }
    fn noCondition(_: *anyopaque, _: u32) anyerror!bool {
        return error.UnexpectedConditionRead;
    }
    fn noChild(_: *anyopaque, _: u32) anyerror!control.types.BlockState {
        return error.UnexpectedChildRead;
    }
    fn input(self: *@This(), per_block: bool) control.Input {
        return .{ .context = self, .block = if (per_block) self.child else null, .read_fn = read, .condition_fn = noCondition, .child_fn = noChild };
    }
    fn expected(self: *const @This()) bool {
        if (self.child == BlockUids.KernelLeakObservation) return self.truth(ItemUids.KernelLeakObservationEnabled);
        // Independent contract inventory; do not call the production classifier.
        const result_gated = switch (self.child) {
            BlockUids.RuntimeSpecializationCoordinator,
            BlockUids.HostManagerPublicServiceCoordinatorOwner,
            BlockUids.HostManagerOperationCoordinatorOwner,
            BlockUids.TrustedAccessRequestOwner,
            BlockUids.FanCurveTemperatureFeed,
            BlockUids.AutomaticProductUpdateService,
            BlockUids.ControlDesiredStateReassertion,
            BlockUids.ControlActualStateReader,
            BlockUids.GpuPlacementProcessHistoryObserver,
            BlockUids.JsonDebugDiagnosticLogWriter,
            BlockUids.HostManagerReportCoordinatorOwner,
            BlockUids.HostManagerSmartCoordinator,
            BlockUids.PortableSoftwareDiscoveryService,
            BlockUids.EtwPhysicalDiskIoAttributionReader,
            BlockUids.EtwNetworkTcpIpAttributionReader,
            BlockUids.TrustedAccessInvocationHostedService,
            => true,
            else => false,
        };
        if (result_gated and !self.runAllowed()) return false;
        if (self.child == BlockUids.FanCurveTemperatureFeed) return self.truth(ItemUids.SoftwareCurveRunNeeded);
        if (!self.permit(self.child)) return false;
        const I = ItemUids;
        return switch (self.child) {
            BlockUids.HardwareBridgeProcessOwner => self.truth(I.HardwareBridgeAvailable) and
            (self.truth(I.HardwareCommandPending) or self.truth(I.HardwareBridgeReadingsInUse)),
            BlockUids.PresentFrameEtwSource => self.present(),
            BlockUids.GpuAllocationCollector => self.allocation(),
            BlockUids.DxgkrnlVidMmEtwTelemetryZone => self.vidmm(),
            BlockUids.NativeEtwLifetimeService => self.present() or self.allocation() or self.vidmm(),
            BlockUids.HostMemoryProtection => self.truth(I.HostMemoryProtectionEnabled) and self.truth(I.MemoryProtectionNormalRunningPointExists) and self.truth(I.MemoryProtectionReserveBuilt),
            BlockUids.JsonDebugDiagnosticLogWriter => self.truth(I.DebugLoggingEnabled) or self.truth(I.DebugWriterPending),
            BlockUids.AutomaticProductUpdateService => self.truth(I.AutomaticUpdatesEnabled),
            BlockUids.HostManagerSamplingSubscriptionOwner => self.truth(I.SamplingGpuTelemetryInUse) or self.truth(I.SamplingHardwareMetricsInUse) or self.truth(I.SamplingCpuTopologyInUse) or self.truth(I.SamplingCpuResidencyInUse) or self.truth(I.SamplingInterruptsInUse) or self.truth(I.SamplingResourceBreakdownInUse) or self.truth(I.SamplingDeviceTopologyInUse),
            BlockUids.GpuTelemetryWorkerSupervisor => self.truth(I.SamplingGpuTelemetryInUse),
            BlockUids.WindowsHardwareMetricSampler => self.truth(I.SamplingHardwareMetricsInUse),
            BlockUids.CpuTopologySnapshotProvider => self.truth(I.SamplingCpuTopologyInUse),
            BlockUids.EtwCpuCoreResidencyReader => self.truth(I.SamplingCpuResidencyInUse),
            BlockUids.EtwSystemInterruptSnapshotSource => self.truth(I.SamplingInterruptsInUse),
            BlockUids.WindowsResourceBreakdownSampler => self.truth(I.SamplingResourceBreakdownInUse),
            BlockUids.DeviceTopologySnapshotProvider => self.truth(I.SamplingDeviceTopologyInUse),
            BlockUids.EtwPhysicalDiskIoAttributionReader => self.truth(I.DiskAttributionInUse),
            BlockUids.EtwNetworkTcpIpAttributionReader => self.truth(I.NetworkAttributionInUse),
            BlockUids.FanControlCoreLifetimeService => self.truth(I.FanCoreAvailable) and (self.truth(I.AppliedFanControlExists) or self.truth(I.FanCommandPending) or self.truth(I.FanReadingsInUse) or self.truth(I.SoftwareCurveRunNeeded)),
            BlockUids.FanCurveTemperatureFeed => self.truth(I.SoftwareCurveRunNeeded),
            BlockUids.ControlDesiredStateReassertion => self.truth(I.ControlKeepAliveEnabled) and self.truth(I.AppliedControlIntentsExist),
            BlockUids.ControlActualStateReader => self.truth(I.ActualControlValuesInUse),
            BlockUids.HostManagerSmartCoordinator => self.truth(I.OptimizationEnabled) or self.truth(I.ActiveCpuLeasesExist) or self.truth(I.SmartOwnedStatePending),
            BlockUids.GpuPlacementProcessHistoryObserver => self.truth(I.GpuApiHistoryEnabled),
            BlockUids.HostManagerReportCoordinatorOwner => self.truth(I.BackgroundReportsEnabled),
            BlockUids.PortableSoftwareDiscoveryService => self.truth(I.PortableDiscoveryEnabled) and self.truth(I.PortableAttributionCatalogAvailable),
            BlockUids.TrustedAccessRequestOwner => self.truth(I.TrustedRequestsPending),
            BlockUids.HostManagerPublicServiceCoordinatorOwner => self.truth(I.PublicServiceEnabled) or self.truth(I.PublicServiceTasksPending),
            BlockUids.HostManagerOperationCoordinatorOwner => self.truth(I.OperationsPending),
            else => self.runAllowed(),
        };
    }
};

test "every precise rule matches business oracle for all booleans and each no-value input" {
    const std = @import("std");
    const absent = [_]control.Read{ .not_published, .owner_stopped, .read_failed };
    for (all_registry.conditions) |condition| {
        const child = condition.decides orelse BlockUids.Backend;
        var facts = RuleTestFacts{ .reads = condition.items, .child = child };
        try std.testing.expect(condition.items.len <= facts.values.len);
        var input = facts.input(condition.per_block);
        var compute: ?control.types.Compute = null;
        for (compute_functions) |candidate| {
            if (candidate.condition == condition.uid) {
                compute = candidate;
                break;
            }
        }
        try std.testing.expect(compute != null);
        const combinations = @as(usize, 1) << @intCast(condition.items.len);
        for (0..combinations) |bits| {
            for (0..condition.items.len) |index| facts.values[index] = .{ .value = bits & (@as(usize, 1) << @intCast(index)) != 0 };
            try std.testing.expectEqual(facts.expected(), try compute.?.function(&input));
            for (0..condition.items.len) |index| {
                const original = facts.values[index];
                for (absent) |missing| {
                    facts.values[index] = missing;
                    try std.testing.expectEqual(facts.expected(), try compute.?.function(&input));
                }
                facts.values[index] = original;
            }
        }
    }
}

test "control keepalive and hosted result match oracle for every value and no-value combination" {
    const std = @import("std");
    const values = [_]control.Read{ .{ .value = false }, .{ .value = true }, .not_published, .owner_stopped, .read_failed };
    var found = false;
    for (all_registry.conditions) |condition| {
        if (condition.uid != ConditionUids.ControlReassertionShouldRun) continue;
        found = true;
        var facts = RuleTestFacts{ .reads = condition.items, .child = BlockUids.ControlDesiredStateReassertion };
        try std.testing.expectEqual(@as(usize, 4), condition.items.len);
        var combinations: usize = 1;
        for (condition.items) |_| combinations *= values.len;
        var input = facts.input(false);
        for (0..combinations) |combination| {
            var remaining = combination;
            for (condition.items, 0..) |_, index| {
                facts.values[index] = values[remaining % values.len];
                remaining /= values.len;
            }
            try std.testing.expectEqual(facts.expected(), try controlReassertionShouldRun(&input));
        }
    }
    try std.testing.expect(found);
}

test "tray Smart default reports and memory protection run only their subscribed producers" {
    const std = @import("std");
    const enabled = [_]d.ItemUid{
        ItemUids.SamplingHardwareMetricsInUse,             ItemUids.SamplingCpuResidencyInUse,
        ItemUids.SamplingInterruptsInUse,                  ItemUids.SamplingResourceBreakdownInUse,
        ItemUids.ProcessDedicatedGpuMemoryInUse,           ItemUids.OptimizationEnabled,
        ItemUids.GpuApiHistoryEnabled,                     ItemUids.BackgroundReportsEnabled,
        ItemUids.PortableDiscoveryEnabled,                 ItemUids.HostMemoryProtectionEnabled,
        ItemUids.MemoryProtectionNormalRunningPointExists, ItemUids.MemoryProtectionReserveBuilt,
        ItemUids.PresentNativeSessionAvailable,            ItemUids.GpuAllocationNativeSessionAvailable,
        ItemUids.VidMmNativeSessionAvailable,
        ItemUids.PortableAttributionCatalogAvailable,
    };
    const running = [_]d.BlockUid{
        BlockUids.NativeEtwLifetimeService,           BlockUids.HostManagerSamplingSubscriptionOwner,
        BlockUids.WindowsHardwareMetricSampler,       BlockUids.EtwCpuCoreResidencyReader,
        BlockUids.EtwSystemInterruptSnapshotSource,   BlockUids.WindowsResourceBreakdownSampler,
        BlockUids.GpuAllocationCollector,             BlockUids.HostManagerSmartCoordinator,
        BlockUids.GpuPlacementProcessHistoryObserver, BlockUids.HostManagerReportCoordinatorOwner,
        BlockUids.PortableSoftwareDiscoveryService,   BlockUids.HostMemoryProtection,
    };
    for (all_registry.conditions) |condition| {
        const child = condition.decides orelse BlockUids.Backend;
        var facts = RuleTestFacts{ .reads = condition.items, .child = child };
        for (condition.items, 0..) |read, index| {
            var value = read.item == ItemUids.HostStartPermitted;
            for (enabled) |item| {
                if (read.item == item) value = true;
            }
            facts.values[index] = .{ .value = value };
        }
        var input = facts.input(condition.per_block);
        var expected = condition.per_block;
        for (running) |block| {
            if (child == block) expected = true;
        }
        for (compute_functions) |compute| {
            if (compute.condition != condition.uid) continue;
            try std.testing.expectEqual(expected, try compute.function(&input));
        }
    }
}
