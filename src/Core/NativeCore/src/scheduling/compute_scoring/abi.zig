const protocol = @import("protocol.zig");
const session_module = @import("session.zig");

pub export fn rm_compute_scoring_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_compute_scoring_create(
    config: ?*const protocol.Config,
    weights: ?[*]const f64,
    weight_count: u32,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    if (weight_count > 0 and weights == null) return code(.invalid_argument);
    const session = session_module.Session.create(config orelse return code(.invalid_argument),
        if (weights) |values| values[0..weight_count] else &.{}) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.abi_mismatch),
        else => return code(.out_of_memory),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_compute_scoring_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

pub export fn rm_compute_scoring_reconfigure(
    handle: ?*anyopaque,
    config: ?*const protocol.Config,
    weights: ?[*]const f64,
    weight_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    if (weight_count > 0 and weights == null) return code(.invalid_argument);
    return code(session.reconfigure(config orelse return code(.invalid_argument),
        if (weights) |values| values[0..weight_count] else &.{}));
}

pub export fn rm_compute_scoring_weighted_cpu_use(
    handle: ?*anyopaque,
    rows: ?[*]const protocol.CpuCoreInput,
    row_count: u32,
    output: ?[*]f64,
    process_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    if ((row_count > 0 and rows == null) or (process_count > 0 and output == null))
        return code(.invalid_argument);
    return code(session.weightedCpuUse(if (rows) |values| values[0..row_count] else &.{},
        if (output) |values| values[0..process_count] else &.{}));
}

pub export fn rm_compute_scoring_query_capacity(
    handle: ?*anyopaque,
    output: ?*protocol.Capacity,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(protocol.Capacity)) return code(.abi_mismatch);
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    const actual_output = output orelse return code(.invalid_argument);
    actual_output.* = session.capacity();
    return code(.ok);
}

pub export fn rm_compute_scoring_software_base_mean(
    handle: ?*anyopaque,
    values: ?[*]const f64,
    count: u32,
    output: ?*f64,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    if (count > 0 and values == null) return code(.invalid_argument);
    return code(session.softwareBaseMean(if (values) |rows| rows[0..count] else &.{},
        output orelse return code(.invalid_argument)));
}

pub export fn rm_compute_scoring_score(
    handle: ?*anyopaque,
    envelope: ?*const protocol.GenerationEnvelope,
    processes: ?[*]const protocol.ProcessInput,
    process_capacity: u32,
    gpus: ?[*]const protocol.GpuInput,
    gpu_capacity: u32,
    outputs: ?[*]protocol.ScoreOutput,
    output_capacity: u32,
    snapshot: ?*protocol.Snapshot,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.score(
        session,
        envelope orelse return code(.invalid_argument),
        processes,
        process_capacity,
        gpus,
        gpu_capacity,
        outputs,
        output_capacity,
        snapshot orelse return code(.invalid_argument),
    ));
}

fn code(status: protocol.Status) i32 {
    return @intFromEnum(status);
}

comptime {
    if (@sizeOf(protocol.Config) != 200) @compileError("compute scoring Config export size changed");
    if (@sizeOf(protocol.Capacity) != 72) @compileError("compute scoring Capacity export size changed");
    if (@sizeOf(protocol.GenerationEnvelope) != 168) @compileError("compute scoring GenerationEnvelope export size changed");
    if (@sizeOf(protocol.ProcessInput) != 96) @compileError("compute scoring ProcessInput export size changed");
    if (@sizeOf(protocol.GpuInput) != 96) @compileError("compute scoring GpuInput export size changed");
    if (@sizeOf(protocol.ScoreOutput) != 96) @compileError("compute scoring ScoreOutput export size changed");
    if (@sizeOf(protocol.Snapshot) != 224) @compileError("compute scoring Snapshot export size changed");
    if (@sizeOf(protocol.CpuCoreInput) != 16) @compileError("compute scoring CpuCoreInput export size changed");
}
