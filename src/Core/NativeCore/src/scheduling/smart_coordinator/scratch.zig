const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");

// Counts and widths describe the actual native types, never managed estimates.
pub const Requirements = extern struct {
    abi_version: u32,
    struct_size: u32,
    byte_count: u64,
    alignment: u32,
    process_size: u32,
    software_size: u32,
    gpu_size: u32,
    software_gpu_size: u32,
    process_count: u32,
    software_count: u32,
    gpu_count: u32,
    process_index_count: u64,
    software_index_count: u64,
    gpu_index_count: u64,
    reserved: u32,
    reserved1: u32,
};

pub const alignment = @max(@alignOf(state.StagedProcess), @alignOf(state.StagedSoftware), @alignOf(state.StagedGpu), @alignOf(state.StagedSoftwareGpu), @alignOf(u32));

pub const Layout = struct {
    processes: u64,
    softwares: u64,
    gpus: u64,
    software_gpus: u64,
    process_index: u64,
    software_index: u64,
    gpu_index: u64,
    software_gpu_index: u64,
    requirements: Requirements,

    pub fn init(config: *const protocol.Config) Layout {
        const pi: u64 = @intCast(state.hashIndexCapacity(config.max_processes) catch unreachable);
        const si: u64 = @intCast(state.hashIndexCapacity(config.max_software_groups) catch unreachable);
        const gi: u64 = @intCast(state.hashIndexCapacity(config.max_gpu_states) catch unreachable);
        var end: u64 = 0;
        const processes = column(&end, state.StagedProcess, config.max_processes);
        const softwares = column(&end, state.StagedSoftware, config.max_software_groups);
        const gpus = column(&end, state.StagedGpu, config.max_gpu_states);
        const software_gpus = column(&end, state.StagedSoftwareGpu, config.max_gpu_states);
        const process_index = column(&end, u32, pi);
        const software_index = column(&end, u32, si);
        const gpu_index = column(&end, u32, gi);
        const software_gpu_index = column(&end, u32, gi);
        return .{ .processes = processes, .softwares = softwares, .gpus = gpus, .software_gpus = software_gpus, .process_index = process_index, .software_index = software_index, .gpu_index = gpu_index, .software_gpu_index = software_gpu_index, .requirements = .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(Requirements),
            .byte_count = std.mem.alignForward(u64, end, alignment),
            .alignment = alignment,
            .process_size = @sizeOf(state.StagedProcess),
            .software_size = @sizeOf(state.StagedSoftware),
            .gpu_size = @sizeOf(state.StagedGpu),
            .software_gpu_size = @sizeOf(state.StagedSoftwareGpu),
            .process_count = config.max_processes,
            .software_count = config.max_software_groups,
            .gpu_count = config.max_gpu_states,
            .process_index_count = pi,
            .software_index_count = si,
            .gpu_index_count = gi,
            .reserved = 0,
            .reserved1 = 0,
        } };
    }

    pub fn bind(self: Layout, session: *state.Session, pointer: ?*anyopaque, length: u64) bool {
        const address = @intFromPtr(pointer orelse return false);
        if (address % alignment != 0 or length < self.requirements.byte_count or length > std.math.maxInt(usize)) return false;
        _ = std.math.add(usize, address, @intCast(length)) catch return false;
        const r = self.requirements;
        session.staged_processes = slice(state.StagedProcess, address, self.processes, r.process_count);
        session.staged_softwares = slice(state.StagedSoftware, address, self.softwares, r.software_count);
        session.staged_gpus = slice(state.StagedGpu, address, self.gpus, r.gpu_count);
        session.staged_software_gpus = slice(state.StagedSoftwareGpu, address, self.software_gpus, r.gpu_count);
        session.staged_process_index = slice(u32, address, self.process_index, r.process_index_count);
        session.staged_software_index = slice(u32, address, self.software_index, r.software_index_count);
        session.staged_gpu_index = slice(u32, address, self.gpu_index, r.gpu_index_count);
        session.staged_software_gpu_index = slice(u32, address, self.software_gpu_index, r.gpu_index_count);
        return true;
    }
};

pub fn unbind(session: *state.Session) void {
    session.staged_processes = &.{};
    session.staged_softwares = &.{};
    session.staged_gpus = &.{};
    session.staged_software_gpus = &.{};
    session.staged_process_index = &.{};
    session.staged_software_index = &.{};
    session.staged_gpu_index = &.{};
    session.staged_software_gpu_index = &.{};
    session.staged_process_count = 0;
    session.staged_software_count = 0;
    session.staged_gpu_count = 0;
    session.staged_software_gpu_count = 0;
}

fn column(end: *u64, comptime T: type, count: u64) u64 {
    const start = std.mem.alignForward(u64, end.*, @alignOf(T));
    end.* = start + @as(u64, count) * @sizeOf(T);
    return start;
}

fn slice(comptime T: type, address: usize, offset: u64, count: u64) []T {
    const pointer: [*]T = @ptrFromInt(address + @as(usize, @intCast(offset)));
    return pointer[0..@intCast(count)];
}
