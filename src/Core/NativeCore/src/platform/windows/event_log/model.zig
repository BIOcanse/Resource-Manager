const std = @import("std");
pub const Channel = enum { system, application };
pub const Category = enum { storage_retry_reset, gpu_reset, memory_exhausted, desktop_crash, hardware_error, unexpected_shutdown };
pub const TargetKind = enum { machine, physical_disk, storage_port, volume, gpu_device, desktop_process, hardware };
pub const Delivery = enum { catchup, live };
/// All slices are borrowed until commit returns. Retaining consumers must copy.
pub const Event = struct {
    category: Category,
    target_kind: TargetKind,
    target: []const u8,
    channel: Channel,
    provider: []const u8,
    event_id: u32,
    record_id: u64,
    timestamp_ms: i64,
    timestamp_utc: []const u8,
    raw_xml: []const u8,
    delivery: Delivery = .catchup,
    bugcheck_code: ?u32 = null,
    dump_path: ?[]const u8 = null,
    actual_shutdown_ms: ?i64 = null,
};
/// A single durable transaction stores the evidence and cursor together. Errors
/// stop delivery; restart uses the previous durable cursor and may replay evidence.
/// The allocator is borrowed from the same event scope as rendering/parsing;
/// commit must not retain it or acquire a second workspace lease.
pub const Sink = struct {
    context: *anyopaque,
    commit: *const fn (*anyopaque, std.mem.Allocator, Channel, ?Event, []const u8) anyerror!void,
    failure: ?*const fn (*anyopaque, anyerror) void = null,
};
pub const LiveKernelReport = struct { path: []const u8, timestamp_ms: i64 };
pub const LiveKernelSink = struct {
    context: *anyopaque,
    display: *const fn (*anyopaque, LiveKernelReport) anyerror!void,
};
pub const Bookmarks = struct { system: ?[]const u8 = null, application: ?[]const u8 = null };
