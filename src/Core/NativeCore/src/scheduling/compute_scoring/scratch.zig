const std = @import("std");
const protocol = @import("protocol.zig");

pub const ProcessWork = struct { row: protocol.ProcessInput, cpu_score: f64 };
pub const GpuWork = struct { row: protocol.GpuInput, gpu_score: f64 };
pub const alignment = @max(@alignOf(ProcessWork), @alignOf(GpuWork), @alignOf(u32));

pub const Layout = struct {
    processes: u64,
    gpus: u64,
    process_identity_order: u64,
    process_aggregate_order: u64,
    gpu_identity_order: u64,
    gpu_aggregate_order: u64,
    byte_count: u64,

    pub fn init(config: *const protocol.Config) Layout {
        return initRows(config.maximum_process_count, config.maximum_gpu_row_count);
    }

    pub fn initRows(process_count: u32, gpu_count: u32) Layout {
        // Counts are u32, so six columns of these fixed small types fit u64.
        var end: u64 = 0;
        const processes = column(&end, ProcessWork, process_count);
        const gpus = column(&end, GpuWork, gpu_count);
        const process_identity_order = column(&end, u32, process_count);
        const process_aggregate_order = column(&end, u32, process_count);
        const gpu_identity_order = column(&end, u32, gpu_count);
        const gpu_aggregate_order = column(&end, u32, gpu_count);
        return .{ .processes = processes, .gpus = gpus,
            .process_identity_order = process_identity_order, .process_aggregate_order = process_aggregate_order,
            .gpu_identity_order = gpu_identity_order, .gpu_aggregate_order = gpu_aggregate_order,
            .byte_count = alignForward(end, alignment) };
    }
};

fn alignForward(value: u64, boundary: u64) u64 {
    return (std.math.add(u64, value, boundary - 1) catch unreachable) & ~(boundary - 1);
}

fn column(end: *u64, comptime T: type, count: u32) u64 {
    const start = alignForward(end.*, @alignOf(T));
    const bytes = std.math.mul(u64, count, @sizeOf(T)) catch unreachable;
    end.* = std.math.add(u64, start, bytes) catch unreachable;
    return start;
}

// Only the synchronous score stack owns this view. No address enters Session.
pub const View = struct {
    processes: []ProcessWork,
    gpus: []GpuWork,
    process_identity_order: []u32,
    process_aggregate_order: []u32,
    gpu_identity_order: []u32,
    gpu_aggregate_order: []u32,

    pub fn init(process_count: u32, gpu_count: u32, pointer: ?*anyopaque, length: u64) ?View {
        const layout = Layout.initRows(process_count, gpu_count);
        const address = @intFromPtr(pointer orelse return null);
        if (address % alignment != 0 or length < layout.byte_count or length > std.math.maxInt(usize)) return null;
        _ = std.math.add(usize, address, @intCast(length)) catch return null;
        return .{
            .processes = slice(ProcessWork, address, layout.processes, process_count),
            .gpus = slice(GpuWork, address, layout.gpus, gpu_count),
            .process_identity_order = slice(u32, address, layout.process_identity_order, process_count),
            .process_aggregate_order = slice(u32, address, layout.process_aggregate_order, process_count),
            .gpu_identity_order = slice(u32, address, layout.gpu_identity_order, gpu_count),
            .gpu_aggregate_order = slice(u32, address, layout.gpu_aggregate_order, gpu_count),
        };
    }
};

fn slice(comptime T: type, address: usize, offset: u64, count: u32) []T {
    const start = std.math.add(usize, address, std.math.cast(usize, offset) orelse unreachable) catch unreachable;
    const pointer: [*]T = @ptrFromInt(start);
    return pointer[0..count];
}

pub fn disjoint(pointer: *anyopaque, length: u64, other: anytype, count: u64, comptime T: type) bool {
    if (count == 0) return true;
    const other_pointer = other orelse return false;
    const bytes = std.math.mul(u64, count, @sizeOf(T)) catch return false;
    const a = @intFromPtr(pointer);
    const b = @intFromPtr(other_pointer);
    const a_end = std.math.add(usize, a, std.math.cast(usize, length) orelse return false) catch return false;
    const b_end = std.math.add(usize, b, std.math.cast(usize, bytes) orelse return false) catch return false;
    return a_end <= b or b_end <= a;
}
