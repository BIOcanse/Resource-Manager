//! Temporary foreign-language boundary in the existing NativeCore library.
//! Test fixtures import real product exports; product registration belongs to
//! the integrating writer, never a second product library.
pub const api = @import("foundation.zig");
pub const ClockSample = extern struct {
    utc_ticks: u64 = 0,
    monotonic_ticks: u64 = 0,
    frequency: u64 = 0,
};

pub export fn rm_foundation_clock(result: *ClockSample) callconv(.c) u32 {
    result.* = .{};
    const frequency = api.clock.frequency() catch return 31;
    const now = api.clock.monotonicNow() catch return 31;
    const utc = api.clock.utcNow() catch return 534;
    result.* = .{ .utc_ticks = utc.ticks, .monotonic_ticks = now.ticks, .frequency = frequency.ticks_per_second };
    return 0;
}

pub export fn rm_foundation_identity(result: *api.identities.Identity) callconv(.c) u32 {
    result.* = .{};
    result.* = api.identities.capture() catch return 31;
    return 0;
}

fn sink(path: [*:0]const u16, path_length: u32, archive: [*:0]const u16, archive_length: u32, rotate_bytes: u64) !api.diagnostics.Sink {
    // Lengths exclude the NUL. Managed callers pin terminated strings;
    // all paths and bytes are borrowed only until this call returns.
    if (path_length > 32767 or archive_length > 32767) return error.InvalidInput;
    return api.diagnostics.Sink.init(path[0..path_length :0], archive[0..archive_length :0], rotate_bytes);
}

pub export fn rm_foundation_log_rotate(path: [*:0]const u16, path_length: u32, archive: [*:0]const u16, archive_length: u32, rotate_bytes: u64) callconv(.c) u32 {
    const target = sink(path, path_length, archive, archive_length, rotate_bytes) catch return api.diagnostics.invalid_parameter;
    return target.rotate();
}

pub export fn rm_foundation_log_append(path: [*:0]const u16, path_length: u32, archive: [*:0]const u16, archive_length: u32, rotate_bytes: u64, bytes: ?[*]const u8, byte_length: usize) callconv(.c) u32 {
    const target = sink(path, path_length, archive, archive_length, rotate_bytes) catch return api.diagnostics.invalid_parameter;
    if (byte_length != 0 and bytes == null) return api.diagnostics.invalid_parameter;
    return target.append(if (bytes) |data| data[0..byte_length] else &.{});
}
