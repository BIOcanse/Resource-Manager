const std = @import("std");

// Stable wire codes. Device results are separately returned by the platform adapter.
pub const Code = enum(u32) {
    completed = 0,
    confirmation_required = 1,
    report_not_found = 2,
    unsupported_report = 3,
    device_evidence_unavailable = 4,
    invalid_settings = 5,
    record_capacity_reached = 6,
    persistence_failed = 7,
    device_resolution_failed = 8,
    device_restart_failed = 9,
    notification_failed = 10,
};

// Fixed ABI transport bounds, not runtime policy. Windows device ids have MAX_DEVICE_ID_LEN=200.
pub const Text = extern struct {
    data: [512]u16 = @splat(0),

    pub fn slice(self: *const Text) []const u16 {
        return std.mem.sliceTo(&self.data, 0);
    }
};
pub const Target = extern struct {
    report_type: Text = .{},
    module: Text = .{},
    instance_id: Text = .{},
};
pub const Record = extern struct {
    sequence: i64 = 0,
    occurred_at: i64 = 0,
    report_id: Text = .{},
    instance_id: Text = .{},
    // A durable reservation is active until a completed result is committed. Never replay it.
    state: u32 = 0,
    result_code: u32 = 0,
    detail: u32 = 0,
};
pub const Output = extern struct {
    code: Code = .completed,
    device_result: u32 = 0,
    detail: u32 = 0,
    reserved: u32 = 0,
    sequence: i64 = 0,
};
pub const Callbacks = extern struct {
    context: ?*anyopaque,
    lookup: *const fn (?*anyopaque, *const Text, *Target) callconv(.c) u32,
    resolve: *const fn (?*anyopaque, *const Text, *Text, *u32) callconv(.c) u32,
    restart: *const fn (?*anyopaque, *const Text, *u32) callconv(.c) u32,
    // store receives a reservation with sequence=0, then a completion with its allocated sequence.
    store: *const fn (?*anyopaque, *Record) callconv(.c) u32,
    notify: *const fn (?*anyopaque, *const Record) callconv(.c) u32,
};

pub fn validCapacity(capacity: i32) bool {
    return capacity >= 1 and capacity <= 16384; // report-lifecycle contract bound
}

fn eql(text: *const Text, comptime ascii: []const u8) bool {
    const s = text.slice();
    if (s.len != ascii.len) return false;
    var i: usize = 0;
    while (i < s.len) : (i += 1) if (s[i] != ascii[i]) return false;
    return true;
}

pub export fn rm_device_action_report_supported(report_type: *const Text) callconv(.c) u32 {
    return if (eql(report_type, "system_interrupt_pressure") or eql(report_type, "display-driver-reset")) 1 else 0;
}

pub fn execute(callbacks: Callbacks, report_id: *const Text, confirmed: bool, capacity: i32, count: u32, now: i64) Output {
    if (!confirmed) return .{ .code = .confirmation_required };
    if (!validCapacity(capacity) or now < 0) return .{ .code = .invalid_settings };
    if (report_id.slice().len == 0 or report_id.slice().len == report_id.data.len) return .{ .code = .report_not_found };
    var target: Target = .{};
    if (callbacks.lookup(callbacks.context, report_id, &target) != 0) return .{ .code = .report_not_found };
    const interrupt = eql(&target.report_type, "system_interrupt_pressure");
    const display = eql(&target.report_type, "display-driver-reset");
    if (!interrupt and !display) return .{ .code = .unsupported_report };
    var detail: u32 = 0;
    if (target.instance_id.slice().len == 0) {
        if (target.module.slice().len == 0) return .{ .code = .device_evidence_unavailable };
        const resolved = callbacks.resolve(callbacks.context, &target.module, &target.instance_id, &detail);
        if (resolved != 0) return .{ .code = .device_resolution_failed, .device_result = resolved, .detail = detail };
    }
    if (target.instance_id.slice().len == 0 or target.instance_id.slice().len >= 200) return .{ .code = .device_evidence_unavailable };
    if (count >= @as(u32, @intCast(capacity))) return .{ .code = .record_capacity_reached };
    var record: Record = .{ .occurred_at = now, .report_id = report_id.*, .instance_id = target.instance_id };
    if (callbacks.store(callbacks.context, &record) != 0 or record.sequence <= 0) return .{ .code = .persistence_failed };
    const device_result = callbacks.restart(callbacks.context, &record.instance_id, &detail);
    record.state = 1;
    record.result_code = device_result;
    record.detail = detail;
    if (callbacks.store(callbacks.context, &record) != 0) return .{ .code = .persistence_failed, .device_result = device_result, .detail = detail, .sequence = record.sequence };
    if (callbacks.notify(callbacks.context, &record) != 0) return .{ .code = .notification_failed, .device_result = device_result, .detail = detail, .sequence = record.sequence };
    return .{ .code = if (device_result == 0) .completed else .device_restart_failed, .device_result = device_result, .detail = detail, .sequence = record.sequence };
}

pub export fn rm_device_action_execute(callbacks: *const Callbacks, report_id: *const Text, confirmed: u32, capacity: i32, count: u32, now: i64, output: *Output) callconv(.c) void {
    output.* = execute(callbacks.*, report_id, confirmed == 1, capacity, count, now);
}
pub export fn rm_device_action_validate_capacity(capacity: i32) callconv(.c) u32 {
    return if (validCapacity(capacity)) 0 else 1;
}

// Invoked only while the host owns the action gate and no finite operation runs.
// An old durable reservation is evidence of an unknown outcome, never permission to replay.
pub export fn rm_device_action_recover_record(record: *Record) callconv(.c) u32 {
    if (record.state != 0) return 0;
    record.state = 1;
    record.result_code = 16; // interrupted / outcome not durably recorded
    record.detail = 0;
    return 1;
}
