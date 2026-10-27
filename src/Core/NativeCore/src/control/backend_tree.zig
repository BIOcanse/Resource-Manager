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

/// The Full host composition has all backend declarations except the service-only update worker.
pub const registry: d.Registry = .{
    .version = all_registry.version,
    .process = all_registry.process,
    .blocks = &full_registry_blocks,
    .items = all_registry.items,
    .conditions = all_registry.conditions,
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
        BlockUids.HostManagerMetricSnapshotOwner,
        BlockUids.HostManagerSoftwareIdentityOwner,
        BlockUids.HostManagerFileQueryOwner,
        BlockUids.HostManagerDisplayCoordinatorOwner,
        BlockUids.ServiceAutoStartSettings,
        BlockUids.ServiceRecoverySettings,
        BlockUids.FanControlCoreLifetimeService,
        BlockUids.KernelEtwSessionBroker,
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
        => true,
        else => false,
    };
}

fn normalReadOnlyRegistryBlocks() [32]d.Block {
    var selected: [32]d.Block = undefined;
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

fn normalReadOnlyConditions() [5]d.Condition {
    var selected: [5]d.Condition = undefined;
    var next: usize = 0;
    for (all_registry.conditions) |condition| {
        if (!normalReadOnlyCondition(condition.uid)) continue;
        if (next >= selected.len) @compileError("normal-read-only registry has an unexpected condition");
        selected[next] = condition;
        next += 1;
    }
    if (next != selected.len) @compileError("normal-read-only registry must retain demand and native ETW conditions");
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
    .{ .uid = BlockUids.HostManagerMetricSnapshotOwner, .parent = BlockUids.Monitoring, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.HostManagerSoftwareIdentityOwner, .parent = BlockUids.SoftwareManagement, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.HostManagerFileQueryOwner, .parent = BlockUids.SoftwareManagement, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.HostManagerDisplayCoordinatorOwner, .parent = BlockUids.Monitoring, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.HostManagerPublicServiceCoordinatorOwner, .parent = BlockUids.PublicServices, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.HostManagerOperationCoordinatorOwner, .parent = BlockUids.Backend, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.SqlitePortableSoftwareRegistry, .parent = BlockUids.SoftwareManagement, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.ServiceAutoStartSettings, .parent = BlockUids.ServiceHost, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.ServiceRecoverySettings, .parent = BlockUids.ServiceHost, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.ControlDesiredStateReassertion, .parent = BlockUids.HardwareControl, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.FanCurveTemperatureFeed, .parent = BlockUids.FanControlCoreLifetimeService, .inputs = &.{BlockUids.WindowsHardwareMetricSampler} },
    .{ .uid = BlockUids.ControlActualStateReader, .parent = BlockUids.HardwareControl },
    .{ .uid = BlockUids.GpuTelemetryWorkerSupervisor, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{BlockUids.HostManagerSamplingSubscriptionOwner} },
    .{ .uid = BlockUids.WindowsHardwareMetricSampler, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{ BlockUids.RuntimeSpecializationCoordinator, BlockUids.HostManagerSamplingSubscriptionOwner, BlockUids.HostManagerMetricSnapshotOwner } },
    .{ .uid = BlockUids.CpuTopologySnapshotProvider, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{BlockUids.HostManagerSamplingSubscriptionOwner} },
    .{ .uid = BlockUids.FanControlCoreLifetimeService, .parent = BlockUids.HardwareControl },
    .{ .uid = BlockUids.KernelEtwSessionBroker, .parent = BlockUids.Monitoring },
    .{ .uid = BlockUids.PortableSoftwareDiscoveryService, .parent = BlockUids.SoftwareManagement, .inputs = &.{ BlockUids.KernelEtwSessionBroker, BlockUids.SqlitePortableSoftwareRegistry } },
    .{ .uid = BlockUids.EtwCpuCoreResidencyReader, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{ BlockUids.RuntimeSpecializationCoordinator, BlockUids.KernelEtwSessionBroker, BlockUids.HostManagerSamplingSubscriptionOwner } },
    .{ .uid = BlockUids.EtwSystemInterruptSnapshotSource, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{ BlockUids.KernelEtwSessionBroker, BlockUids.HostManagerSamplingSubscriptionOwner } },
    .{ .uid = BlockUids.EtwPhysicalDiskIoAttributionReader, .parent = BlockUids.Monitoring, .inputs = &.{BlockUids.KernelEtwSessionBroker} },
    .{ .uid = BlockUids.EtwNetworkTcpIpAttributionReader, .parent = BlockUids.Monitoring, .inputs = &.{BlockUids.KernelEtwSessionBroker} },
    .{ .uid = BlockUids.WindowsResourceBreakdownSampler, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{ BlockUids.RuntimeSpecializationCoordinator, BlockUids.WindowsHardwareMetricSampler, BlockUids.HostManagerSamplingSubscriptionOwner, BlockUids.EtwPhysicalDiskIoAttributionReader, BlockUids.EtwNetworkTcpIpAttributionReader, BlockUids.GpuAllocationCollector, BlockUids.DxgkrnlVidMmEtwTelemetryZone } },
    .{ .uid = BlockUids.GpuPlacementProcessHistoryObserver, .parent = BlockUids.Optimization, .inputs = &.{ BlockUids.RuntimeSpecializationCoordinator, BlockUids.WindowsResourceBreakdownSampler, BlockUids.PresentFrameEtwSource, BlockUids.SqlitePortableSoftwareRegistry } },
    .{ .uid = BlockUids.SharedResourceSubscriptionMaintenanceService, .parent = BlockUids.PublicServices },
    .{ .uid = BlockUids.JsonDebugDiagnosticLogWriter, .parent = BlockUids.ServiceHost, .inputs = &.{BlockUids.RuntimeSpecializationCoordinator} },
    .{ .uid = BlockUids.HostManagerReportCoordinatorOwner, .parent = BlockUids.Reports, .inputs = &.{ BlockUids.RuntimeSpecializationCoordinator, BlockUids.WindowsResourceBreakdownSampler, BlockUids.WindowsHardwareMetricSampler, BlockUids.EtwSystemInterruptSnapshotSource, BlockUids.SqlitePortableSoftwareRegistry } },
    .{ .uid = BlockUids.HostManagerSmartCoordinator, .parent = BlockUids.Optimization, .inputs = &.{ BlockUids.RuntimeSpecializationCoordinator, BlockUids.WindowsResourceBreakdownSampler, BlockUids.WindowsHardwareMetricSampler, BlockUids.EtwCpuCoreResidencyReader } },
    .{ .uid = BlockUids.DeviceTopologySnapshotProvider, .parent = BlockUids.HostManagerSamplingSubscriptionOwner, .inputs = &.{BlockUids.HostManagerSamplingSubscriptionOwner} },
    .{ .uid = BlockUids.PresentFrameEtwSource, .parent = BlockUids.NativeEtwLifetimeService },
    .{ .uid = BlockUids.GpuAllocationCollector, .parent = BlockUids.NativeEtwLifetimeService },
    .{ .uid = BlockUids.DxgkrnlVidMmEtwTelemetryZone, .parent = BlockUids.NativeEtwLifetimeService },
    .{ .uid = BlockUids.HostMemoryProtection, .parent = BlockUids.Backend, .inputs = &.{ BlockUids.WindowsHardwareMetricSampler, BlockUids.WindowsResourceBreakdownSampler } },
};

fn normalReadOnlyTreeBlocks() [32]tree.Block {
    var selected: [32]tree.Block = undefined;
    var next: usize = 0;
    var edge_count: usize = 0;
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
        edge_count += block.inputs.len;
        selected[next] = block;
        next += 1;
    }
    if (next != selected.len) @compileError("normal-read-only topology must contain its exact fixed block set");
    if (edge_count != 28 or root_child_count != 1)
        @compileError("normal-read-only topology must retain 28 inputs and only Backend under Program");
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

const FixedChildCondition = struct { child: d.BlockUid, condition: d.ConditionUid };

// These children keep their own fixed-node condition when their parent moves.
// KernelLeakObservation will join this list when its block is registered.
const fixed_child_conditions = [_]FixedChildCondition{
    .{ .child = BlockUids.HostMemoryProtection, .condition = ConditionUids.HostMemoryProtectionReady },
    .{ .child = BlockUids.PresentFrameEtwSource, .condition = ConditionUids.PresentEtwChildShouldRun },
    .{ .child = BlockUids.GpuAllocationCollector, .condition = ConditionUids.GpuAllocationEtwChildShouldRun },
    .{ .child = BlockUids.DxgkrnlVidMmEtwTelemetryZone, .condition = ConditionUids.VidMmEtwChildShouldRun },
};

fn hasFixedCondition(child: d.BlockUid) bool {
    for (fixed_child_conditions) |fixed| if (fixed.child == child) return true;
    return false;
}

fn normalReadOnlyCondition(condition: d.ConditionUid) bool {
    for (fixed_child_conditions) |fixed| {
        if (fixed.condition == condition) return normalReadOnlyBlock(fixed.child);
    }
    return true;
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
    var count: usize = 0;
    for (blocks) |block| {
        if (block.parent == null or hasFixedCondition(block.uid)) continue;
        count += 1;
    }
    return count;
}

fn usesFor(comptime blocks: []const tree.Block) [perBlockUseCount(blocks)]tree.PerBlockUse {
    var uses: [perBlockUseCount(blocks)]tree.PerBlockUse = undefined;
    var next: usize = 0;
    for (blocks) |block| {
        const parent = block.parent orelse continue;
        if (hasFixedCondition(block.uid)) continue;
        uses[next] = .{
            .node = parent,
            .condition = if (block.uid == BlockUids.Backend or domainBlock(block.uid)) ConditionUids.AlwaysWanted else ConditionUids.HasExternalDemand,
            .child = block.uid,
        };
        next += 1;
    }
    if (next != uses.len) @compileError("backend tree must route every child without a fixed condition");
    return uses;
}

fn itemValueOrFalse(read: control.Read) bool {
    return read.valueOr(false);
}

fn unfrozenOrNotPublished(read: control.Read) bool {
    return switch (read) {
        .value => |value| value,
        .not_published => true,
        .owner_stopped, .read_failed => false,
    };
}

fn memoryProtectionFactsAllow(enabled: control.Read, normal_point_exists: control.Read, reserve_built: control.Read) bool {
    return itemValueOrFalse(enabled) and itemValueOrFalse(normal_point_exists) and itemValueOrFalse(reserve_built);
}

fn nativeChildFactsAllow(demand: control.Read, available: control.Read, mode: ?control.Read) bool {
    if (!itemValueOrFalse(demand) or !itemValueOrFalse(available)) return false;
    if (mode) |value| return unfrozenOrNotPublished(value);
    return true;
}

fn readBool(input: *control.Input, item: d.ItemUid, block: ?d.BlockUid) anyerror!control.Read {
    return input.read(.{ .item = item, .block = block });
}

fn hasExternalDemand(input: *control.Input) anyerror!bool {
    // The declaration leaves the per-block key unbound; each compiled use
    // supplies its concrete block through input.block.
    return itemValueOrFalse(try readBool(input, ItemUids.BlockDemand, input.block));
}

fn alwaysWanted(_: *control.Input) anyerror!bool {
    return true;
}

fn hostMemoryProtectionReady(input: *control.Input) anyerror!bool {
    return memoryProtectionFactsAllow(
        try readBool(input, ItemUids.HostMemoryProtectionEnabled, null),
        try readBool(input, ItemUids.MemoryProtectionNormalRunningPointExists, null),
        try readBool(input, ItemUids.MemoryProtectionReserveBuilt, null),
    );
}

fn nativeChildShouldRun(input: *control.Input, child: d.BlockUid, availability_item: d.ItemUid, mode_item: ?d.ItemUid) anyerror!bool {
    const demand = try readBool(input, ItemUids.BlockDemand, child);
    const available = try readBool(input, availability_item, null);
    const mode: ?control.Read = if (mode_item) |item| try readBool(input, item, null) else null;
    return nativeChildFactsAllow(demand, available, mode);
}

fn presentEtwChildShouldRun(input: *control.Input) anyerror!bool {
    return nativeChildShouldRun(input, BlockUids.PresentFrameEtwSource, ItemUids.PresentNativeSessionAvailable, ItemUids.PresentUnfrozen);
}

fn gpuAllocationEtwChildShouldRun(input: *control.Input) anyerror!bool {
    return nativeChildShouldRun(input, BlockUids.GpuAllocationCollector, ItemUids.GpuAllocationNativeSessionAvailable, null);
}

fn vidMmEtwChildShouldRun(input: *control.Input) anyerror!bool {
    return nativeChildShouldRun(input, BlockUids.DxgkrnlVidMmEtwTelemetryZone, ItemUids.VidMmNativeSessionAvailable, ItemUids.VidMmUnfrozen);
}

const compute_functions = [_]control.types.Compute{
    .{ .condition = ConditionUids.HasExternalDemand, .function = hasExternalDemand },
    .{ .condition = ConditionUids.AlwaysWanted, .function = alwaysWanted },
    .{ .condition = ConditionUids.HostMemoryProtectionReady, .function = hostMemoryProtectionReady },
    .{ .condition = ConditionUids.PresentEtwChildShouldRun, .function = presentEtwChildShouldRun },
    .{ .condition = ConditionUids.GpuAllocationEtwChildShouldRun, .function = gpuAllocationEtwChildShouldRun },
    .{ .condition = ConditionUids.VidMmEtwChildShouldRun, .function = vidMmEtwChildShouldRun },
};

fn normalReadOnlyComputes() [5]control.types.Compute {
    var selected: [5]control.types.Compute = undefined;
    var next: usize = 0;
    for (compute_functions) |compute| {
        if (!normalReadOnlyCondition(compute.condition)) continue;
        if (next >= selected.len) @compileError("normal-read-only topology has an unexpected condition compute");
        selected[next] = compute;
        next += 1;
    }
    if (next != selected.len) @compileError("normal-read-only topology must compute its five registered conditions");
    return selected;
}

const normal_readonly_computes = normalReadOnlyComputes();

const full_uses = usesFor(&full_tree_blocks);
const service_uses = usesFor(&all_tree_blocks);
const normal_readonly_uses = usesFor(&normal_readonly_tree_blocks);

/// Full host topology (47 runnable blocks, 51 input edges, only Backend under Program).
pub const topology: tree.Tree = .{
    .program = BlockUids.Program,
    .blocks = &full_tree_blocks,
    .uses = &full_uses,
    .computes = &compute_functions,
};

/// Windows Service + MutablePersistence topology (adds the update worker).
pub const service_topology: tree.Tree = .{
    .program = BlockUids.Program,
    .blocks = &all_tree_blocks,
    .uses = &service_uses,
    .computes = &compute_functions,
};

/// Exact `normal-read-only` startup topology: 31 runnable blocks, 28 data inputs,
/// and only Backend directly under Program.
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

test "backend rule no-value behavior matches the C# composition" {
    const std = @import("std");
    const not_published: control.Read = .not_published;
    const owner_stopped: control.Read = .owner_stopped;
    const read_failed: control.Read = .read_failed;
    const yes: control.Read = .{ .value = true };
    const no: control.Read = .{ .value = false };

    try std.testing.expect(!itemValueOrFalse(not_published));
    try std.testing.expect(!itemValueOrFalse(owner_stopped));
    try std.testing.expect(!itemValueOrFalse(read_failed));
    try std.testing.expect(itemValueOrFalse(yes));
    try std.testing.expect(!memoryProtectionFactsAllow(yes, not_published, yes));
    try std.testing.expect(memoryProtectionFactsAllow(yes, yes, yes));

    try std.testing.expect(unfrozenOrNotPublished(not_published));
    try std.testing.expect(unfrozenOrNotPublished(yes));
    try std.testing.expect(!unfrozenOrNotPublished(no));
    try std.testing.expect(!unfrozenOrNotPublished(owner_stopped));
    try std.testing.expect(!unfrozenOrNotPublished(read_failed));
    try std.testing.expect(nativeChildFactsAllow(yes, yes, not_published));
    try std.testing.expect(nativeChildFactsAllow(yes, yes, null));
    try std.testing.expect(!nativeChildFactsAllow(not_published, yes, null));
    try std.testing.expect(!nativeChildFactsAllow(yes, not_published, null));
}
