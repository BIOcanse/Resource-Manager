const std = @import("std");
const decoder = @import("decoder.zig");
const io = @import("io_decode.zig");
const fileio = @import("fileio.zig");
const counters = @import("admission_counters.zig");

pub const IoObservation = struct {
    disk: ?counters.Observation = null,
    network: ?counters.Observation = null,
};

/// The 28 parser event names are diagnostic identities, not decoder variants.
/// In particular DCStart/DCEnd are grouped with process/thread start/end;
/// ImageDCEnd, FileIO name/rundown and FileIO read have no subscribed identity.
pub fn physicalKind(value: decoder.RawEvent) ?counters.PhysicalKind {
    if (value.provider.eql(decoder.process_provider)) return switch (value.opcode) {
        1, 3 => .process_start, 2, 4 => .process_end, else => null,
    };
    if (value.provider.eql(decoder.thread_provider)) return switch (value.opcode) {
        1, 3 => .thread_start, 2, 4 => .thread_end, 36 => .context_switch, else => null,
    };
    if (value.provider.eql(decoder.image_provider)) return switch (value.opcode) {
        3 => .image_rundown, 10 => .image_load, 2 => .image_unload, else => null,
    };
    if (value.provider.eql(decoder.perfinfo_provider)) return switch (value.opcode) {
        50, 67 => .isr, 68 => .dpc, 66 => .threaded_dpc, 69 => .timer_dpc, else => null,
    };
    if (value.provider.eql(io.disk_io_task_guid)) return switch (value.opcode) {
        10 => .disk_read, 11 => .disk_write, else => null,
    };
    if (value.provider.eql(fileio.file_io_task_guid)) return switch (value.opcode) {
        64 => .file_create, 32 => .file_name_create, 35 => .file_name_delete,
        68 => .file_write, 71 => .file_rename, 70 => .file_delete, else => null,
    };
    if (value.provider.eql(io.tcp_ip_task_guid)) return switch (value.opcode) {
        10 => .tcp_send, 11 => .tcp_receive, 26 => .tcp6_send, 27 => .tcp6_receive, else => null,
    };
    if (value.provider.eql(io.udp_ip_task_guid)) return switch (value.opcode) {
        10 => .udp_send, 11 => .udp_receive, 26 => .udp6_send, 27 => .udp6_receive, else => null,
    };
    return null;
}

fn physical(store: *counters.Store, active_outputs: u32, kind: counters.PhysicalKind) void {
    // A handler may reject its event, or have no registered diagnostic reader.
    // Neither case is a physical drop. Only absence of an output handler drops.
    store.recordPhysical(kind, active_outputs & counters.handlerOutputs(kind) != 0);
}

fn ended(lifecycle: decoder.Lifecycle) bool {
    return lifecycle == .end or lifecycle == .rundown_end;
}

fn signedPid(pid: u32) i32 {
    // TraceEvent exposes the native ULONG as an Int32, including invalid IDs.
    return @bitCast(pid);
}

fn observe(store: *counters.Store, active_outputs: u32, output: u32, kind: counters.Kind, facts: counters.Facts) !void {
    if (active_outputs & output != 0) _ = try store.observe(active_outputs & output, kind, facts);
}

/// Called once for a successfully decoded core record, before CPU window,
/// processor-map and IRQ duration gates. This records admission only: it never
/// changes business state, holds, keywords, clocks, or the shared flow's result.
/// The caller supplies the same observed tick and IRQ duration used by intake.
pub fn decoded(store: *counters.Store, active_outputs: u32, value: decoder.Event, supplied: counters.Facts) !void {
    var facts = supplied;
    facts.qpc = value.qpc;
    switch (value.data) {
        .process => |v| {
            const is_end = ended(v.lifecycle);
            physical(store, active_outputs, if (is_end) .process_end else .process_start);
            facts.process_id = signedPid(v.process_id);
            try observe(store, active_outputs, 1, if (is_end) .process_end else .process_start, facts);
            // Only DiskIO's prior business callback invalidated its birth cache.
            try observe(store, active_outputs, 4, .process_identity, facts);
            try observe(store, active_outputs, 32, .renderer_invalidation, facts);
            if (!is_end) try observe(store, active_outputs, 64, .process_notification, facts);
        },
        .thread => |v| {
            const is_end = ended(v.lifecycle);
            physical(store, active_outputs, if (is_end) .thread_end else .thread_start);
            facts.process_id = signedPid(v.process_id);
            try observe(store, active_outputs, 1, if (is_end) .thread_end else .thread_start, facts);
        },
        .cswitch => {
            physical(store, active_outputs, .context_switch);
            try observe(store, active_outputs, 1, .context_switch, facts);
        },
        .image => |v| {
            if (v.lifecycle == .rundown_end) return;
            const is_end = ended(v.lifecycle);
            physical(store, active_outputs, if (is_end) .image_unload else if (v.lifecycle == .rundown_start) .image_rundown else .image_load);
            facts.process_id = signedPid(v.process_id);
            facts.kernel_image = @import("interrupts.zig").isKernelAddress(v.base);
            try observe(store, active_outputs, 2, if (is_end) .image_unload else .image_load_or_rundown, facts);
            try observe(store, active_outputs, 32, .renderer_invalidation, facts);
        },
        .interrupt => |v| {
            const kind: counters.Kind = switch (v.kind) {
                .isr => .isr, .dpc => .dpc, .threaded_dpc => .threaded_dpc, .timer_dpc => .timer_dpc,
            };
            const physical_kind: counters.PhysicalKind = switch (v.kind) {
                .isr => .isr, .dpc => .dpc, .threaded_dpc => .threaded_dpc, .timer_dpc => .timer_dpc,
            };
            physical(store, active_outputs, physical_kind);
            try observe(store, active_outputs, 2, kind, facts);
        },
        .lost => {},
    }
}

/// Called only for the raw/unhandled decoder branch. Core records must use
/// decoded(), so a record can never increment a physical counter twice.
/// Classification borrows no payload and cannot hide malformed/unsupported
/// decoder errors; the caller still propagates them through the existing flow.
pub fn raw(store: *counters.Store, active_outputs: u32, value: decoder.RawEvent) void {
    if (!value.provider.eql(io.disk_io_task_guid) and !value.provider.eql(io.tcp_ip_task_guid) and
        !value.provider.eql(io.udp_ip_task_guid) and !value.provider.eql(fileio.file_io_task_guid)) return;
    if (physicalKind(value)) |kind| physical(store, active_outputs, kind);
}

/// Called after the existing single IO decode and thread-to-process lookup,
/// before enabled/capacity/business checks. Returned epochs support an exact
/// later rejection (for example before-process-birth) without a second receive.
pub fn ioEvent(store: *counters.Store, active_outputs: u32, value: io.Event, supplied: counters.Facts) !IoObservation {
    var facts = supplied;
    return switch (value) {
        .disk => |v| blk: {
            if (active_outputs & 4 == 0) break :blk .{};
            facts.qpc = v.qpc;
            facts.process_id = signedPid(v.process_id orelse 0);
            facts.valid_payload = facts.valid_payload and v.bytes != 0;
            break :blk .{ .disk = try store.observe(4, .disk_transfer, facts) };
        },
        .network => |v| blk: {
            if (active_outputs & 8 == 0) break :blk .{};
            facts.qpc = v.qpc;
            facts.process_id = signedPid(v.process_id orelse 0);
            facts.valid_payload = facts.valid_payload and v.bytes != 0;
            break :blk .{ .network = try store.observe(8, .network_transfer, facts) };
        },
    };
}

/// An optional decode-error diagnostic path. It does not manufacture a
/// substitute decoded event or permit malformed records into the owner.
pub fn malformedIO(store: *counters.Store, active_outputs: u32, value: decoder.RawEvent, supplied: counters.Facts) !IoObservation {
    const kind = physicalKind(value) orelse return .{};
    var facts = supplied;
    facts.qpc = value.qpc;
    facts.process_id = signedPid(value.header_process_id);
    facts.valid_payload = false;
    return switch (kind) {
        .disk_read, .disk_write => if (active_outputs & 4 != 0) .{ .disk = try store.observe(4, .disk_transfer, facts) } else .{},
        .tcp_send, .tcp_receive, .tcp6_send, .tcp6_receive, .udp_send, .udp_receive, .udp6_send, .udp6_receive =>
            if (active_outputs & 8 != 0) .{ .network = try store.observe(8, .network_transfer, facts) } else .{},
        else => .{},
    };
}

fn testRaw(provider: decoder.Guid, opcode: u8) decoder.RawEvent {
    return .{ .provider = provider, .opcode = opcode, .version = 0, .pointer_size = 8,
        .qpc = 100, .processor = 0, .header_process_id = 9, .header_thread_id = 10, .payload = &.{} };
}
fn config(token: u64, output: u32) counters.Config {
    return .{ .token = token, .generation = 7, .kind_mask = counters.all_kinds, .output_mask = output,
        .flags = counters.active | counters.current_binding | counters.window_active, .window_start = 1, .incomplete_through = -1 };
}

test "classic raw classification covers all 28 dispatcher identities and rundown aliases" {
    const Case = struct { provider: decoder.Guid, opcode: u8, expected: counters.PhysicalKind };
    const cases = [_]Case{
        .{ .provider = decoder.process_provider, .opcode = 1, .expected = .process_start },
        .{ .provider = decoder.process_provider, .opcode = 2, .expected = .process_end },
        .{ .provider = decoder.thread_provider, .opcode = 1, .expected = .thread_start },
        .{ .provider = decoder.thread_provider, .opcode = 2, .expected = .thread_end },
        .{ .provider = decoder.thread_provider, .opcode = 36, .expected = .context_switch },
        .{ .provider = io.disk_io_task_guid, .opcode = 10, .expected = .disk_read },
        .{ .provider = io.disk_io_task_guid, .opcode = 11, .expected = .disk_write },
        .{ .provider = fileio.file_io_task_guid, .opcode = 64, .expected = .file_create },
        .{ .provider = fileio.file_io_task_guid, .opcode = 32, .expected = .file_name_create },
        .{ .provider = fileio.file_io_task_guid, .opcode = 35, .expected = .file_name_delete },
        .{ .provider = fileio.file_io_task_guid, .opcode = 68, .expected = .file_write },
        .{ .provider = fileio.file_io_task_guid, .opcode = 71, .expected = .file_rename },
        .{ .provider = fileio.file_io_task_guid, .opcode = 70, .expected = .file_delete },
        .{ .provider = io.tcp_ip_task_guid, .opcode = 10, .expected = .tcp_send },
        .{ .provider = io.tcp_ip_task_guid, .opcode = 11, .expected = .tcp_receive },
        .{ .provider = io.tcp_ip_task_guid, .opcode = 26, .expected = .tcp6_send },
        .{ .provider = io.tcp_ip_task_guid, .opcode = 27, .expected = .tcp6_receive },
        .{ .provider = io.udp_ip_task_guid, .opcode = 10, .expected = .udp_send },
        .{ .provider = io.udp_ip_task_guid, .opcode = 11, .expected = .udp_receive },
        .{ .provider = io.udp_ip_task_guid, .opcode = 26, .expected = .udp6_send },
        .{ .provider = io.udp_ip_task_guid, .opcode = 27, .expected = .udp6_receive },
        .{ .provider = decoder.image_provider, .opcode = 3, .expected = .image_rundown },
        .{ .provider = decoder.image_provider, .opcode = 10, .expected = .image_load },
        .{ .provider = decoder.image_provider, .opcode = 2, .expected = .image_unload },
        .{ .provider = decoder.perfinfo_provider, .opcode = 67, .expected = .isr },
        .{ .provider = decoder.perfinfo_provider, .opcode = 68, .expected = .dpc },
        .{ .provider = decoder.perfinfo_provider, .opcode = 66, .expected = .threaded_dpc },
        .{ .provider = decoder.perfinfo_provider, .opcode = 69, .expected = .timer_dpc },
    };
    try std.testing.expectEqual(counters.physical_count, cases.len);
    for (cases, 0..) |v, i| {
        try std.testing.expectEqual(v.expected, physicalKind(testRaw(v.provider, v.opcode)).?);
        try std.testing.expectEqual(i, @intFromEnum(v.expected));
    }
    try std.testing.expectEqual(counters.PhysicalKind.isr, physicalKind(testRaw(decoder.perfinfo_provider, 50)).?);
    try std.testing.expectEqual(counters.PhysicalKind.process_start, physicalKind(testRaw(decoder.process_provider, 3)).?);
    try std.testing.expectEqual(counters.PhysicalKind.process_end, physicalKind(testRaw(decoder.process_provider, 4)).?);
    try std.testing.expectEqual(counters.PhysicalKind.thread_start, physicalKind(testRaw(decoder.thread_provider, 3)).?);
    try std.testing.expectEqual(counters.PhysicalKind.thread_end, physicalKind(testRaw(decoder.thread_provider, 4)).?);
    for ([_]u8{ 0, 4, 36 }) |opcode| try std.testing.expect(physicalKind(testRaw(decoder.image_provider, opcode)) == null);
    for ([_]u8{ 0, 36, 65, 66, 67, 69 }) |opcode| try std.testing.expect(physicalKind(testRaw(fileio.file_io_task_guid, opcode)) == null);
}

test "one decoded process fans out only to held business consumers and end never notifies discovery" {
    var store = try counters.Store.init(std.testing.allocator, 4);
    defer store.deinit();
    try store.configure(config(1, 1));
    try store.configure(config(2, 4));
    try store.configure(config(3, 32));
    try store.configure(config(4, 64));
    var event: decoder.Event = .{ .qpc = 100, .processor = 0, .data = .{ .process = .{
        .lifecycle = .rundown_start, .process_id = 42, .parent_id = 1, .unique_key = null,
        .session_id = null, .exit_status = null, .image_name = "", .command_line_utf16le = "",
    } } };
    try decoded(&store, 1 | 4 | 32 | 64, event, .{ .generation = 7, .observed_tick = 100 });
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.queryPhysical(.process_start));
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.query(1, .process_start, .none).?);
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.query(2, .process_identity, .none).?);
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.query(3, .renderer_invalidation, .none).?);
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.query(4, .process_notification, .none).?);
    event.data.process.lifecycle = .rundown_end;
    try decoded(&store, 64, event, .{ .generation = 7, .observed_tick = 100 });
    try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.queryPhysical(.process_end));
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.query(4, .process_notification, .none).?);
    try std.testing.expectEqual(counters.Pair{}, store.query(1, .process_end, .none).?);
}

test "intake mapping preserves CPU rejection precedence and IRQ observed duration without filtering physical delivery" {
    var store = try counters.Store.init(std.testing.allocator, 2);
    defer store.deinit();
    var cpu = config(1, 1);
    cpu.flags &= ~counters.window_active;
    try store.configure(cpu);
    try store.configure(config(2, 2));
    try decoded(&store, 1, .{ .qpc = 100, .processor = 9, .data = .{ .cswitch = .{ .new_thread_id = 8, .old_thread_id = 9 } } },
        .{ .generation = 7, .mapped_processor = false });
    try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.query(1, .context_switch, .unmapped_processor).?);
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.queryPhysical(.context_switch));
    try decoded(&store, 2, .{ .qpc = 100, .processor = 0, .data = .{ .interrupt = .{ .kind = .timer_dpc, .initial_qpc = 99, .routine = 8 } } },
        .{ .generation = 7, .observed_tick = 100, .duration_ms = 0 });
    try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.query(2, .timer_dpc, .invalid_duration).?);
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.queryPhysical(.timer_dpc));
    try std.testing.expectEqual(counters.Pair{}, store.query(2, .dpc, .none).?);
}

test "IO physical counts once and late rejection retains one receive while signed network IDs remain invalid" {
    var store = try counters.Store.init(std.testing.allocator, 2);
    defer store.deinit();
    try store.configure(config(1, 4));
    try store.configure(config(2, 8));
    raw(&store, 4, testRaw(io.disk_io_task_guid, 10));
    const disk = try ioEvent(&store, 4, .{ .disk = .{ .process_id = 42, .thread_id = 10, .bytes = 4096,
        .disk_number = 0, .is_write = false, .qpc = 100, .event_filetime_100ns = 100 } }, .{ .generation = 7, .observed_tick = 100 });
    store.rejectObservation(disk.disk.?, .disk_transfer, .before_process_birth);
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.queryPhysical(.disk_read));
    try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.query(1, .disk_transfer, .before_process_birth).?);
    _ = try ioEvent(&store, 8, .{ .network = .{ .process_id = 0xffffffff, .bytes = 1, .direction = .send,
        .protocol = .tcp, .remote_address = .{ .ipv4 = .{ 1, 2, 3, 4 } }, .qpc = 100, .event_filetime_100ns = 100 } },
        .{ .generation = 7, .observed_tick = 100 });
    try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.query(2, .network_transfer, .invalid_payload).?);
    raw(&store, 8, testRaw(io.disk_io_task_guid, 11));
    try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.queryPhysical(.disk_write));
}

test "renderer-only image intake records no IRQ consumers and image DCEnd has no subscribed delivery" {
    var store = try counters.Store.init(std.testing.allocator, 2);
    defer store.deinit();
    try store.configure(config(1, 2));
    try store.configure(config(2, 32));
    var event: decoder.Event = .{ .qpc = 100, .processor = 0, .data = .{ .image = .{
        .lifecycle = .rundown_start, .process_id = 42, .base = 0xFFFF800000001000, .size = 50, .path_utf16le = "",
    } } };
    try decoded(&store, 32, event, .{ .generation = 7, .observed_tick = 100 });
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.queryPhysical(.image_rundown));
    try std.testing.expectEqual(counters.Pair{}, store.query(1, .image_load_or_rundown, .none).?);
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.query(2, .renderer_invalidation, .none).?);
    event.data.image.lifecycle = .rundown_end;
    try decoded(&store, 2 | 32, event, .{ .generation = 7, .observed_tick = 100 });
    try std.testing.expectEqual(counters.Pair{}, store.queryPhysical(.image_unload));
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.query(2, .renderer_invalidation, .none).?);
    event.data.image.lifecycle = .end;
    event.data.image.process_id = 0xffffffff;
    try decoded(&store, 2 | 32, event, .{ .generation = 7, .observed_tick = 100 });
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.query(1, .image_unload, .none).?);
    try std.testing.expectEqual(counters.Pair{ .received = 2, .dropped = 1 }, store.query(2, .renderer_invalidation, .none).?);
    try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.query(2, .renderer_invalidation, .invalid_process).?);
}

test "user images reject only the IRQ holder before binding gates and preserve renderer delivery" {
    var store = try counters.Store.init(std.testing.allocator, 2);
    defer store.deinit();
    try store.configure(config(1, 2));
    try store.configure(config(2, 32));
    var event: decoder.Event = .{ .qpc = 100, .processor = 0, .data = .{ .image = .{
        .lifecycle = .start, .process_id = 42, .base = 0x7FF600000000, .size = 50, .path_utf16le = "",
    } } };
    try decoded(&store, 2 | 32, event, .{ .generation = 7, .observed_tick = 100 });
    event.data.image.lifecycle = .rundown_start;
    try decoded(&store, 2 | 32, event, .{ .generation = 7, .observed_tick = 100 });
    event.data.image.lifecycle = .end;
    try decoded(&store, 2 | 32, event, .{ .generation = 7, .observed_tick = 100 });
    try std.testing.expectEqual(counters.Pair{ .received = 2, .dropped = 2 }, store.query(1, .image_load_or_rundown, .user_mode_image).?);
    try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.query(1, .image_unload, .user_mode_image).?);
    try std.testing.expectEqual(counters.Pair{ .received = 3 }, store.query(2, .renderer_invalidation, .none).?);
    inline for (.{ counters.PhysicalKind.image_load, counters.PhysicalKind.image_rundown, counters.PhysicalKind.image_unload }) |kind|
        try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.queryPhysical(kind));
    event.data.image.base = 0xFFFF800000000000;
    try decoded(&store, 2 | 32, event, .{ .generation = 7, .observed_tick = 100 });
    try std.testing.expectEqual(counters.Pair{ .received = 2, .dropped = 1 }, store.query(1, .image_unload, .none).?);
    // The user-mode check precedes the binding gates: a stale generation before the window still counts as user_mode_image.
    event.data.image.base = 0xFFFF7FFFFFFFFFFF;
    try decoded(&store, 2, event, .{ .generation = 99, .observed_tick = 0 });
    try std.testing.expectEqual(counters.Pair{ .received = 2, .dropped = 2 }, store.query(1, .image_unload, .user_mode_image).?);
    try std.testing.expectEqual(counters.Pair{}, store.query(1, .image_unload, .generation_mismatch).?);
    try std.testing.expectEqual(counters.Pair{}, store.query(1, .image_unload, .before_window).?);
    // Normal intake skips an inactive descriptor, so it records nothing at all.
    var inactive = config(1, 2);
    inactive.flags &= ~counters.active;
    try store.configure(inactive);
    try decoded(&store, 2, event, .{ .generation = 7, .observed_tick = 100 });
    try std.testing.expectEqual(counters.Pair{ .received = 3, .dropped = 2 }, store.query(1, .image_unload, .none).?);
    try std.testing.expectEqual(counters.Pair{}, store.query(1, .image_unload, .inactive_binding).?);
    try store.retireToSnapshot(1);
    const retired = store.takeRetired(1).?;
    try std.testing.expectEqual(@as(u64, 2), retired.rejected[@intFromEnum(counters.Kind.image_unload)][@intFromEnum(counters.Reason.user_mode_image)]);
}

test "core records sent to the raw hook are not counted twice and malformed IO stays explicitly invalid" {
    var store = try counters.Store.init(std.testing.allocator, 1);
    defer store.deinit();
    try store.configure(config(1, 8));
    raw(&store, counters.all_outputs, testRaw(decoder.thread_provider, 36));
    try std.testing.expectEqual(counters.Pair{}, store.queryPhysical(.context_switch));
    const malformed = testRaw(io.tcp_ip_task_guid, 26);
    raw(&store, 8, malformed);
    _ = try malformedIO(&store, 8, malformed, .{ .generation = 7, .observed_tick = 100 });
    try std.testing.expectEqual(counters.Pair{ .received = 1 }, store.queryPhysical(.tcp6_send));
    try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.query(1, .network_transfer, .invalid_payload).?);
    // The classifier deliberately cannot mistake another provider's opcode.
    var unsupported = malformed;
    unsupported.provider.data1 = 0;
    try std.testing.expect(physicalKind(unsupported) == null);
    _ = try malformedIO(&store, 8, unsupported, .{ .generation = 7 });
    try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.query(1, .network_transfer, .none).?);
}
