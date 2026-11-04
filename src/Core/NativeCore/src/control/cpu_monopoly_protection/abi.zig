const core = @import("core.zig");
pub export fn rm_cpu_monopoly_migrate(source: [*]core.Row, source_count: u32, destination: [*]core.Row, destination_count: u32) callconv(.c) u32 { return @intFromBool(core.migrate(source[0..source_count], destination[0..destination_count])); }
pub export fn rm_cpu_monopoly_row_size() callconv(.c) u32 { return @sizeOf(core.Row); }
pub export fn rm_cpu_monopoly_can_automatic_write(rows: [*]core.Row, count: u32, pid: u32, key: u64) callconv(.c) u32 { return @intFromBool(core.canAutomaticWrite(rows[0..count], pid, key)); }
pub export fn rm_cpu_monopoly_end_reason(rows: [*]core.Row, count: u32, pid: u32, key: u64) callconv(.c) u32 { return core.endReason(rows[0..count], pid, key); }
pub export fn rm_cpu_monopoly_begin(rows: [*]core.Row, count: u32) callconv(.c) void { core.begin(rows[0..count]); }
pub export fn rm_cpu_monopoly_unavailable(rows: [*]core.Row, count: u32) callconv(.c) void { core.unavailable(rows[0..count]); }
pub export fn rm_cpu_monopoly_observe(rows: [*]core.Row, count: u32, config: *const core.Config, input: *const core.Input, result: *core.Result) callconv(.c) void { result.* = core.observe(rows[0..count], config.*, input.*); }
pub export fn rm_cpu_monopoly_select(rows: [*]core.Row, count: u32, result: *core.Result) callconv(.c) void { result.* = core.select(rows[0..count]); }
pub export fn rm_cpu_monopoly_finish(rows: [*]core.Row, count: u32, pid: u32, key: u64, success: u32) callconv(.c) void { core.finish(rows[0..count], pid, key, success != 0); }
pub export fn rm_cpu_monopoly_explicit(rows: [*]core.Row, count: u32, pid: u32, key: u64) callconv(.c) u32 { return @intFromBool(core.explicit(rows[0..count], pid, key)); }
pub export fn rm_cpu_monopoly_eligible(rows: [*]core.Row, count: u32, pid: u32, key: u64) callconv(.c) u32 { return @intFromBool(core.eligible(rows[0..count], pid, key)); }
pub export fn rm_cpu_monopoly_restore(rows: [*]core.Row, count: u32, pid: u32, key: u64) callconv(.c) u32 { return @intFromBool(core.restore(rows[0..count], pid, key)); }
pub export fn rm_cpu_monopoly_sweep(rows: [*]core.Row, count: u32, result: *core.Result) callconv(.c) void { result.* = core.sweep(rows[0..count]); }
