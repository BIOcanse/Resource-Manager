const std = @import("std");
const cpu = @import("cpu_exclusivity.zig");
const Session = @import("state.zig").Session;
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub export fn rm_placement_coordinator_cpu_configure(handle: ?*anyopaque, config: ?*const cpu.Config) callconv(.c) i32 {
    const s: *Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    const c = config orelse return code(.invalid_argument);
    while (!s.mutex.tryLock()) std.atomic.spinLoopHint();
    defer s.mutex.unlock();
    if (c.generation != s.config.generation) return code(.stale_frame);
    if (s.cpu_exclusivity) |state| return code(state.reconfigure(c.*));
    s.cpu_exclusivity = cpu.State.create(s.allocator, c.*) catch |err| return code(if (err == error.InvalidConfiguration) .invalid_argument else .out_of_memory);
    return code(.ok);
}

pub export fn rm_placement_coordinator_cpu_reset(handle: ?*anyopaque) callconv(.c) i32 {
    const s: *Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    while (!s.mutex.tryLock()) std.atomic.spinLoopHint();
    defer s.mutex.unlock();
    if (s.cpu_exclusivity) |state| state.reset();
    return code(.ok);
}

pub export fn rm_placement_coordinator_cpu_plan(handle: ?*anyopaque, frame: ?*cpu.Frame, cores: ?[*]const cpu.Core, software: ?[*]const cpu.Software, usage: ?[*]const cpu.Usage, manual: ?[*]const cpu.Reservation, output: ?[*]cpu.Reservation) callconv(.c) i32 {
    const s: *Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    const f = frame orelse return code(.invalid_argument);
    while (!s.mutex.tryLock()) std.atomic.spinLoopHint();
    defer s.mutex.unlock();
    if (f.generation != s.config.generation) return code(.stale_frame);
    const state = s.cpu_exclusivity orelse return code(.unavailable);
    if (f.core_count > state.config.core_capacity or f.software_count > state.config.software_capacity or
        f.usage_count > @as(u64, state.config.software_capacity) * state.config.core_capacity or f.manual_count > state.config.reservation_capacity or
        f.output_capacity > state.config.reservation_capacity) return code(.invalid_argument);
    return code(state.plan(f, if (cores) |p| p[0..f.core_count] else if (f.core_count == 0) &.{} else return code(.invalid_argument), if (software) |p| p[0..f.software_count] else if (f.software_count == 0) &.{} else return code(.invalid_argument), if (usage) |p| p[0..f.usage_count] else if (f.usage_count == 0) &.{} else return code(.invalid_argument), if (manual) |p| p[0..f.manual_count] else if (f.manual_count == 0) &.{} else return code(.invalid_argument), if (output) |p| p[0..f.output_capacity] else if (f.output_capacity == 0) &.{} else return code(.invalid_argument)));
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}
