const protocol = @import("protocol.zig");
const state = @import("state.zig");
const session_module = @import("session.zig");
const persistence = @import("persistence.zig");
const wire_contract = @import("wire_contract.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub export fn rm_metric_snapshot_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_metric_snapshot_wire_contract_fingerprint() callconv(.c) u64 {
    return protocol.wire_contract_fingerprint;
}

pub export fn rm_metric_snapshot_create(
    config: ?*const protocol.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const actual_config = config orelse return code(.invalid_argument);
    const session = state.Session.create(actual_config) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.abi_mismatch),
        else => return code(.out_of_memory),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_metric_snapshot_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *state.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

pub export fn rm_metric_snapshot_reconfigure(
    handle: ?*anyopaque,
    config: ?*const protocol.Config,
) callconv(.c) i32 {
    return code(session_module.reconfigure(
        getSession(handle) orelse return code(.invalid_argument),
        config orelse return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_reset(
    handle: ?*anyopaque,
    input: ?*const protocol.ControlInput,
) callconv(.c) i32 {
    return code(session_module.reset(
        getSession(handle) orelse return code(.invalid_argument),
        input orelse return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_query_capacity(
    handle: ?*anyopaque,
    output: ?*protocol.Capacity,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(protocol.Capacity)) return code(.abi_mismatch);
    const session = getSession(handle) orelse return code(.invalid_argument);
    const actual_output = output orelse return code(.invalid_argument);
    actual_output.* = session.capacity();
    return code(.ok);
}

pub export fn rm_metric_snapshot_replace_catalog(
    handle: ?*anyopaque,
    input: ?*const protocol.CatalogReplaceInput,
    sources: ?[*]const protocol.SourcePolicyInput,
    source_count: u32,
    rules: ?[*]const protocol.MetricDefinitionInput,
    rule_count: u32,
) callconv(.c) i32 {
    return code(session_module.replaceCatalog(
        getSession(handle) orelse return code(.invalid_argument),
        input orelse return code(.invalid_argument),
        constSlice(protocol.SourcePolicyInput, sources, source_count) orelse
            return code(.invalid_argument),
        constSlice(protocol.MetricDefinitionInput, rules, rule_count) orelse
            return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_calculate_catalog_fingerprint(
    sources: ?[*]const protocol.SourcePolicyInput,
    source_count: u32,
    rules: ?[*]const protocol.MetricDefinitionInput,
    rule_count: u32,
    output: ?*u64,
) callconv(.c) i32 {
    const source_slice = constSlice(
        protocol.SourcePolicyInput,
        sources,
        source_count,
    ) orelse return code(.invalid_argument);
    const rule_slice = constSlice(
        protocol.MetricDefinitionInput,
        rules,
        rule_count,
    ) orelse return code(.invalid_argument);
    const actual_output = output orelse return code(.invalid_argument);
    const fingerprint = session_module.catalogFingerprint(
        source_slice,
        rule_slice,
    );
    actual_output.* = fingerprint;
    return code(.ok);
}

pub export fn rm_metric_snapshot_plan(
    handle: ?*anyopaque,
    input: ?*const protocol.PlanInput,
    requested_metrics: ?[*]const protocol.PlanMetricInput,
    requested_metric_count: u32,
    source_modes: ?[*]const protocol.SourceModeInput,
    source_mode_count: u32,
    output: ?*protocol.PlanOutput,
    source_plans: ?[*]protocol.SourcePlanOutput,
    source_plan_capacity: u32,
    metric_plans: ?[*]protocol.MetricPlanOutput,
    metric_plan_capacity: u32,
) callconv(.c) i32 {
    return code(session_module.plan(
        getSession(handle) orelse return code(.invalid_argument),
        input orelse return code(.invalid_argument),
        constSlice(protocol.PlanMetricInput, requested_metrics, requested_metric_count) orelse
            return code(.invalid_argument),
        constSlice(protocol.SourceModeInput, source_modes, source_mode_count) orelse
            return code(.invalid_argument),
        output orelse return code(.invalid_argument),
        mutableSlice(protocol.SourcePlanOutput, source_plans, source_plan_capacity) orelse
            return code(.invalid_argument),
        mutableSlice(protocol.MetricPlanOutput, metric_plans, metric_plan_capacity) orelse
            return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_begin_completion(
    handle: ?*anyopaque,
    input: ?*const protocol.CompletionHeader,
) callconv(.c) i32 {
    return code(session_module.beginCompletion(
        getSession(handle) orelse return code(.invalid_argument),
        input orelse return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_submit_requested(
    handle: ?*anyopaque,
    inputs: ?[*]const protocol.RequestedMetricInput,
    input_count: u32,
) callconv(.c) i32 {
    return code(session_module.submitRequested(
        getSession(handle) orelse return code(.invalid_argument),
        constSlice(protocol.RequestedMetricInput, inputs, input_count) orelse
            return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_submit_observations(
    handle: ?*anyopaque,
    inputs: ?[*]const protocol.ObservationInput,
    input_count: u32,
) callconv(.c) i32 {
    return code(session_module.submitObservations(
        getSession(handle) orelse return code(.invalid_argument),
        constSlice(protocol.ObservationInput, inputs, input_count) orelse
            return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_submit_cpu_counter(
    handle: ?*anyopaque,
    input: ?*const protocol.CpuCounterInput,
) callconv(.c) i32 {
    return code(session_module.submitCpuCounter(
        getSession(handle) orelse return code(.invalid_argument),
        input orelse return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_submit_gpu_inventory(
    handle: ?*anyopaque,
    inputs: ?[*]const protocol.GpuInventoryInput,
    input_count: u32,
) callconv(.c) i32 {
    return code(session_module.submitGpuInventory(
        getSession(handle) orelse return code(.invalid_argument),
        constSlice(protocol.GpuInventoryInput, inputs, input_count) orelse
            return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_finalize_completion(
    handle: ?*anyopaque,
    input: ?*const protocol.FinalizeInput,
) callconv(.c) i32 {
    return code(session_module.finalizeCompletion(
        getSession(handle) orelse return code(.invalid_argument),
        input orelse return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_abort_completion(
    handle: ?*anyopaque,
    input: ?*const protocol.AbortInput,
) callconv(.c) i32 {
    return code(session_module.abortCompletion(
        getSession(handle) orelse return code(.invalid_argument),
        input orelse return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_query_header(
    handle: ?*anyopaque,
    output: ?*protocol.SnapshotHeader,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(protocol.SnapshotHeader)) return code(.abi_mismatch);
    return code(session_module.querySnapshotHeader(
        getSession(handle) orelse return code(.invalid_argument),
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_read(
    handle: ?*anyopaque,
    input: ?*const protocol.ReadInput,
    sources: ?[*]protocol.SourceOutput,
    source_capacity: u32,
    metrics: ?[*]protocol.MetricOutput,
    metric_capacity: u32,
    gpu_inventory: ?[*]protocol.GpuInventoryOutput,
    gpu_capacity: u32,
    rules: ?[*]protocol.RuleStateOutput,
    rule_capacity: u32,
) callconv(.c) i32 {
    return code(session_module.readSnapshot(
        getSession(handle) orelse return code(.invalid_argument),
        input orelse return code(.invalid_argument),
        mutableSlice(protocol.SourceOutput, sources, source_capacity) orelse
            return code(.invalid_argument),
        mutableSlice(protocol.MetricOutput, metrics, metric_capacity) orelse
            return code(.invalid_argument),
        mutableSlice(protocol.GpuInventoryOutput, gpu_inventory, gpu_capacity) orelse
            return code(.invalid_argument),
        mutableSlice(protocol.RuleStateOutput, rules, rule_capacity) orelse
            return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_export_state(
    handle: ?*anyopaque,
    input: ?*const protocol.ReadInput,
    header: ?*protocol.PersistenceHeader,
    sources: ?[*]protocol.SourcePersistenceOutput,
    source_capacity: u32,
    rules: ?[*]protocol.RuleStateOutput,
    rule_capacity: u32,
    gpu_inventory: ?[*]protocol.GpuInventoryOutput,
    gpu_capacity: u32,
) callconv(.c) i32 {
    return code(persistence.exportState(
        getSession(handle) orelse return code(.invalid_argument),
        input orelse return code(.invalid_argument),
        header orelse return code(.invalid_argument),
        mutableSlice(protocol.SourcePersistenceOutput, sources, source_capacity) orelse
            return code(.invalid_argument),
        mutableSlice(protocol.RuleStateOutput, rules, rule_capacity) orelse
            return code(.invalid_argument),
        mutableSlice(protocol.GpuInventoryOutput, gpu_inventory, gpu_capacity) orelse
            return code(.invalid_argument),
    ));
}

pub export fn rm_metric_snapshot_import_state(
    handle: ?*anyopaque,
    input: ?*const protocol.PersistenceInput,
    header: ?*const protocol.PersistenceHeader,
    sources: ?[*]const protocol.SourcePersistenceOutput,
    source_count: u32,
    rules: ?[*]const protocol.RuleStateOutput,
    rule_count: u32,
    gpu_inventory: ?[*]const protocol.GpuInventoryOutput,
    gpu_count: u32,
) callconv(.c) i32 {
    return code(persistence.importState(
        getSession(handle) orelse return code(.invalid_argument),
        input orelse return code(.invalid_argument),
        header orelse return code(.invalid_argument),
        constSlice(protocol.SourcePersistenceOutput, sources, source_count) orelse
            return code(.invalid_argument),
        constSlice(protocol.RuleStateOutput, rules, rule_count) orelse
            return code(.invalid_argument),
        constSlice(protocol.GpuInventoryOutput, gpu_inventory, gpu_count) orelse
            return code(.invalid_argument),
    ));
}

fn getSession(handle: ?*anyopaque) ?*state.Session {
    return @ptrCast(@alignCast(handle orelse return null));
}

fn constSlice(comptime T: type, pointer: ?[*]const T, count: u32) ?[]const T {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}

fn mutableSlice(comptime T: type, pointer: ?[*]T, count: u32) ?[]T {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}
