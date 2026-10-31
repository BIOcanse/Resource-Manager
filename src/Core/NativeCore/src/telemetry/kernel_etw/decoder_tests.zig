const std = @import("std");
const d = @import("decoder.zig");
const c = @import("consumer.zig");

fn raw(provider: d.Guid, opcode: u8, version: u8, p: u8, bytes: []const u8) d.RawEvent {
    return .{ .provider = provider, .opcode = opcode, .version = version, .pointer_size = p, .qpc = 9_007_199_254_740_999, .processor = 513, .header_process_id = 999, .header_thread_id = 888, .payload = bytes };
}
fn put(comptime T: type, bytes: []u8, offset: usize, value: T) void {
    std.mem.writeInt(T, bytes[offset..][0..@sizeOf(T)], value, .little);
}
fn putPointer(bytes: []u8, offset: usize, p: u8, value: u64) void {
    if (p == 4) put(u32, bytes, offset, @intCast(value)) else put(u64, bytes, offset, value);
}
fn rejectPrefixes(record: d.RawEvent) !void {
    var short = record;
    var length: usize = 0;
    while (length < record.payload.len) : (length += 1) {
        short.payload = record.payload[0..length];
        if (d.decode(short)) |_| return error.AcceptedTruncatedRecord else |_| {}
    }
}

test "CSwitch uses payload IDs and retains processor index and raw QPC" {
    var bytes = @as([24]u8, @splat(0));
    put(u32, &bytes, 0, 42);
    put(u32, &bytes, 4, 57);
    for ([_]u8{ 4, 8 }) |p| {
        const record = raw(d.thread_provider, 36, 2, p, &bytes);
        const event = (try d.decode(record)).?;
        try std.testing.expectEqual(record.qpc, event.qpc);
        try std.testing.expectEqual(@as(u16, 513), event.processor);
        try std.testing.expectEqual(@as(u32, 42), event.data.cswitch.new_thread_id);
        try std.testing.expectEqual(@as(u32, 57), event.data.cswitch.old_thread_id);
        try rejectPrefixes(record);
    }
    try std.testing.expectError(error.UnsupportedVersion, d.decode(raw(d.thread_provider, 36, 3, 8, &bytes)));
    var too_long = @as([25]u8, @splat(0));
    try std.testing.expectError(error.InvalidPayload, d.decode(raw(d.thread_provider, 36, 2, 8, &too_long)));
    var current = @as([28]u8, @splat(0));
    put(u32, &current, 0, 420);
    put(u32, &current, 4, 570);
    for ([_]u8{ 4, 8 }) |p| {
        const record = raw(d.thread_provider, 36, 5, p, &current);
        const event = (try d.decode(record)).?;
        try std.testing.expectEqual(@as(u32, 420), event.data.cswitch.new_thread_id);
        try std.testing.expectEqual(@as(u32, 570), event.data.cswitch.old_thread_id);
        try rejectPrefixes(record);
    }
}

test "thread lifecycles decode x86 x64 v0 v2 v3 without fabricated header identity" {
    var bytes = @as([80]u8, @splat(0));
    put(u32, &bytes, 0, 123);
    put(u32, &bytes, 4, 456);
    const v0 = (try d.decode(raw(d.thread_provider, 1, 0, 4, bytes[0..8]))).?;
    try std.testing.expectEqual(@as(u32, 456), v0.data.thread.process_id);
    try std.testing.expectEqual(@as(u32, 123), v0.data.thread.thread_id);
    for ([_]u8{ 4, 8 }) |p| {
        const length = 40 + (@as(usize, p) - 4) * 7;
        for ([_]u8{ 1, 2, 3, 4 }) |op| {
            const record = raw(d.thread_provider, op, 2, p, bytes[0..length]);
            const event = (try d.decode(record)).?;
            try std.testing.expectEqual(@as(u32, 123), event.data.thread.process_id);
            try std.testing.expectEqual(@as(u32, 456), event.data.thread.thread_id);
            try std.testing.expectEqual(@as(d.Lifecycle, switch (op) {
                1 => .start,
                2 => .end,
                3 => .rundown_start,
                4 => .rundown_end,
                else => unreachable,
            }), event.data.thread.lifecycle);
            try rejectPrefixes(record);
        }
        _ = try d.decode(raw(d.thread_provider, 1, 3, p, bytes[0 .. length + 4]));
        _ = try d.decode(raw(d.thread_provider, 1, 3, p, bytes[0 .. length + 6]));
        try std.testing.expectError(error.TruncatedPayload, d.decode(raw(d.thread_provider, 1, 3, p, bytes[0 .. length + 1])));
    }
    try std.testing.expectError(error.UnsupportedVersion, d.decode(raw(d.thread_provider, 1, 4, 8, &bytes)));
}

test "process v2 through v5 validates variable SID and unaligned Unicode tails" {
    for ([_]u8{ 4, 8 }) |p| {
        for ([_]u8{ 2, 3, 4, 5 }) |version| {
            var bytes = @as([160]u8, @splat(0));
            putPointer(&bytes, 0, p, 0x123456);
            put(u32, &bytes, p, 321);
            put(u32, &bytes, p + 4, 123);
            put(u32, &bytes, p + 8, 2);
            put(u32, &bytes, p + 12, 259);
            const delta: usize = p - 4;
            const sid_offset: usize = switch (version) {
                2 => 20 + delta,
                3 => 24 + delta * 2,
                4, 5 => 28 + delta * 2,
                else => unreachable,
            };
            // Non-null TOKEN_USER, SID revision 1 with two subauthorities.
            putPointer(&bytes, sid_offset, p, 1);
            const sid = sid_offset + @as(usize, p) * 2;
            bytes[sid] = 1;
            bytes[sid + 1] = 2;
            const name = sid + 16;
            @memcpy(bytes[name..][0..4], "abc\x00");
            put(u16, &bytes, name + 4, 'x');
            var end = name + 8;
            if (version >= 4) end += 4;
            if (version == 5) end += 8;
            const record = raw(d.process_provider, 3, version, p, bytes[0..end]);
            const event = (try d.decode(record)).?;
            try std.testing.expectEqual(d.Lifecycle.rundown_start, event.data.process.lifecycle);
            try std.testing.expectEqual(@as(u32, 321), event.data.process.process_id);
            try std.testing.expectEqual(@as(u32, 123), event.data.process.parent_id);
            try std.testing.expectEqual(@as(?u64, 0x123456), event.data.process.unique_key);
            try std.testing.expectEqualStrings("abc", event.data.process.image_name);
            try std.testing.expectEqualSlices(u8, &.{ 'x', 0 }, event.data.process.command_line_utf16le);
            try rejectPrefixes(record);
            bytes[sid + 1] = 255;
            try std.testing.expectError(error.InvalidPayload, d.decode(record));
        }
    }
}

test "process v0 v1 null SID and unsupported versions" {
    var bytes = @as([64]u8, @splat(0));
    put(u32, &bytes, 0, 51);
    put(u32, &bytes, 4, 52);
    const v0 = (try d.decode(raw(d.process_provider, 2, 0, 4, bytes[0..13]))).?;
    try std.testing.expectEqual(@as(u32, 51), v0.data.process.process_id);
    try std.testing.expectEqual(@as(?u64, null), v0.data.process.unique_key);
    const v1 = (try d.decode(raw(d.process_provider, 4, 1, 4, bytes[0..25]))).?;
    try std.testing.expectEqual(@as(u32, 52), v1.data.process.process_id);
    try std.testing.expectError(error.UnsupportedVersion, d.decode(raw(d.process_provider, 1, 6, 8, &bytes)));
}

test "ISR DPC threaded and timer preserve raw duration and pointer widths" {
    for ([_]u8{ 4, 8 }) |p| {
        for ([_]u8{ 50, 66, 67, 68, 69 }) |op| {
            var bytes = @as([24]u8, @splat(0));
            const end: i64 = 9_007_199_254_740_999;
            put(u64, &bytes, 0, @intCast(end - 3));
            putPointer(&bytes, 8, p, 0x12345678);
            const length: usize = switch (op) {
                50 => 16 + @as(usize, p),
                67 => 12 + @as(usize, p),
                else => 8 + @as(usize, p),
            };
            const record = raw(d.perfinfo_provider, op, 2, p, bytes[0..length]);
            const event = (try d.decode(record)).?;
            try std.testing.expectEqual(switch (op) {
                50, 67 => d.InterruptKind.isr,
                66 => .threaded_dpc,
                68 => .dpc,
                69 => .timer_dpc,
                else => unreachable,
            }, event.data.interrupt.kind);
            try std.testing.expectEqual(@as(i64, 3), event.qpc - event.data.interrupt.initial_qpc);
            try std.testing.expectEqual(@as(u64, 0x12345678), event.data.interrupt.routine);
            try rejectPrefixes(record);
            put(u64, &bytes, 0, @intCast(end + 1));
            try std.testing.expectError(error.InvalidTimestamp, d.decode(record));
        }
    }
}

test "image load rundown unload strict terminated UTF16 and range overflow" {
    for ([_]u8{ 4, 8 }) |p| {
        for ([_]u8{ 0, 1, 2, 3 }) |version| {
            for ([_]u8{ 10, 2, 3, 4 }) |op| {
                var bytes = @as([100]u8, @splat(0));
                putPointer(&bytes, 0, p, 0x10000000);
                if (version == 0) put(u32, &bytes, p, 0x1234) else putPointer(&bytes, p, p, 0x1234);
                if (version > 0) put(u32, &bytes, p * 2, 5);
                const delta: usize = p - 4;
                const offset: usize = switch (version) {
                    0 => p + 4,
                    1 => 12 + delta * 2,
                    2, 3 => 44 + delta * 3,
                    else => unreachable,
                };
                put(u16, &bytes, offset, 'z');
                const record = raw(d.image_provider, op, version, p, bytes[0 .. offset + 4]);
                const event = (try d.decode(record)).?;
                try std.testing.expectEqual(@as(u64, 0x10000000), event.data.image.base);
                try std.testing.expectEqual(@as(u64, 0x1234), event.data.image.size);
                try std.testing.expectEqual(@as(u32, if (version == 0) 999 else 5), event.data.image.process_id);
                try std.testing.expectEqualSlices(u8, &.{ 'z', 0 }, event.data.image.path_utf16le);
                try rejectPrefixes(record);
                bytes[offset + 2] = 1;
                try std.testing.expectError(error.InvalidPayload, d.decode(record));
            }
        }
    }
    var bytes = @as([58]u8, @splat(0));
    put(u64, &bytes, 0, std.math.maxInt(u64) - 1);
    put(u64, &bytes, 8, 3);
    try std.testing.expectError(error.InvalidPayload, d.decode(raw(d.image_provider, 10, 2, 8, &bytes)));
}

test "foreign providers ignored and invalid pointer sizes explicit" {
    const unknown: d.Guid = std.mem.zeroes(d.Guid);
    try std.testing.expectEqual(@as(?d.Event, null), try d.decode(raw(unknown, 36, 2, 0, &.{})));
    try std.testing.expectError(error.InvalidPointerSize, d.decode(raw(d.thread_provider, 36, 2, 0, &.{})));
    try std.testing.expect((try d.decode(raw(d.lost_provider, 32, 0, 8, &.{}))).?.data == .lost);
}

const Capture = struct {
    seen: usize = 0,
    rejected: usize = 0,
    last: ?d.Event = null,
    fn onNotice(context: ?*anyopaque, notice: c.Notice) bool {
        const self: *Capture = @ptrCast(@alignCast(context.?));
        switch (notice) {
            .event => |event| {
                self.seen += 1;
                self.last = event;
            },
            .rejected, .invalid_record => self.rejected += 1,
            .loss => {},
        }
        return true;
    }
};

test "SDK callback conversion uses processor-index and pointer-header flags" {
    var capture: Capture = .{};
    var consumer = c.Consumer.init(.{ .context = &capture, .on_notice = Capture.onNotice });
    consumer.metadata.pointer_size = 8;
    var bytes = @as([24]u8, @splat(0));
    put(u32, &bytes, 0, 12);
    put(u32, &bytes, 4, 13);
    var record: c.EventRecord = std.mem.zeroes(c.EventRecord);
    // Compact kernel records retain their original size after ProcessTrace
    // expands the SDK header. A CSwitch record can therefore be below 80 bytes.
    record.header.size = 56;
    record.header.timestamp = 9_007_199_254_740_999;
    record.header.flags = 0x220;
    record.header.provider = d.thread_provider;
    record.header.descriptor = .{ .id = 0, .version = 2, .channel = 0, .level = 0, .opcode = 36, .task = 0, .keyword = 0 };
    record.processor_index = 513;
    record.payload = &bytes;
    record.payload_length = bytes.len;
    consumer.acceptRecord(&record);
    try std.testing.expectEqual(@as(usize, 1), capture.seen);
    try std.testing.expectEqual(@as(u16, 513), capture.last.?.processor);
    record.header.flags = 0x40;
    consumer.acceptRecord(&record);
    try std.testing.expectEqual(@as(u16, 1), capture.last.?.processor);
    record.header.flags = 0x60;
    consumer.acceptRecord(&record);
    record.header.flags = 0;
    record.payload = null;
    consumer.acceptRecord(&record);
    try std.testing.expectEqual(@as(usize, 2), capture.rejected);
}

test "exact integer QPC conversion requires an actual clock anchor" {
    var metadata: c.Metadata = .{ .qpc_frequency = 10_000_000, .clock_type = 1, .start_filetime_100ns = 123456789 };
    try std.testing.expectError(error.NoClockAnchor, metadata.qpcToFiletime(1));
    metadata.header_qpc = 9_007_199_254_740_999;
    try std.testing.expectEqual(@as(i64, 123456792), try metadata.qpcToFiletime(metadata.header_qpc.? + 3));
    try std.testing.expectEqual(@as(i64, 123456786), try metadata.qpcToFiletime(metadata.header_qpc.? - 3));
}

// Ensure the complete Windows API path is semantically checked even in unit
// tests that don't attach to the user's running kernel session.
test "Windows consumer entry points compile" {
    std.testing.refAllDecls(c.Consumer);
}

test "explicit real-time Windows clock sample maps without an ETL header" {
    const sample = try c.ClockSample.read();
    try std.testing.expect(sample.qpc_before > 0);
    try std.testing.expect(sample.qpc_after >= sample.qpc_before);
    const metadata: c.Metadata = .{ .qpc_frequency = 10_000_000, .clock_type = 1, .realtime_clock = sample };
    try std.testing.expectEqual(@as(?i64, null), metadata.header_qpc);
    try std.testing.expectEqual(sample.filetime_100ns, try metadata.qpcToFiletime(sample.midpoint()));
    std.debug.print("Real-time clock bracket QPC ticks={d}\n", .{sample.qpc_after - sample.qpc_before});
}

test "OpenTrace reports the original Windows failure and leaves no handle" {
    var capture: Capture = .{};
    var consumer = c.Consumer.init(.{ .context = &capture, .on_notice = Capture.onNotice });
    const missing = std.unicode.utf8ToUtf16LeStringLiteral("F:\\__resource_manager_etw_decoder_nonexistent_fixture_8a03b0a1.etl");
    try std.testing.expectError(error.OpenFailed, consumer.openEtl(missing));
    try std.testing.expect(consumer.last_windows_error != 0);
    try std.testing.expectEqual(c.invalid_handle, consumer.handle.load(.acquire));
    try std.testing.expectEqual(@as(u32, 0), try consumer.close());
    try std.testing.expectError(error.NotOpen, consumer.process());
}

const ReplayCapture = struct {
    decoded: u64 = 0,
    records: u64 = 0,
    rejected: u64 = 0,
    kinds: [6]u64 = @splat(0),
    fn onNotice(context: ?*anyopaque, notice: c.Notice) bool {
        const self: *ReplayCapture = @ptrCast(@alignCast(context.?));
        self.records += 1;
        switch (notice) {
            .event => |event| {
                self.decoded += 1;
                const index: usize = switch (event.data) {
                    .process => 0,
                    .thread => 1,
                    .cswitch => 2,
                    .interrupt => 3,
                    .image => 4,
                    .lost => 5,
                };
                self.kinds[index] += 1;
            },
            .rejected => |rejected| {
                if (self.rejected < 8) std.debug.print("ETL rejected provider={x} op={d} v={d} p={d} bytes={d} reason={s}\n", .{ rejected.raw.provider.data1, rejected.raw.opcode, rejected.raw.version, rejected.raw.pointer_size, rejected.raw.payload.len, @errorName(rejected.reason) });
                self.rejected += 1;
            },
            .invalid_record => |invalid| {
                if (self.rejected < 8) std.debug.print("ETL invalid SDK record header_size={d} flags={x} payload={d} missing={any}\n", .{ invalid.original_header_size, invalid.flags, invalid.payload_length, invalid.missing_payload });
                self.rejected += 1;
            },
            .loss => |loss| std.debug.print("ETL loss source={s} events={d} buffers={d}\n", .{ @tagName(loss.source), loss.events_lost, loss.buffers_lost }),
        }
        return self.decoded < 100_000 and self.records < 1_000_000;
    }
    fn onUnhandled(context: ?*anyopaque, _: d.RawEvent) bool {
        const self: *ReplayCapture = @ptrCast(@alignCast(context.?));
        self.records += 1;
        return self.decoded < 100_000 and self.records < 1_000_000;
    }
};

test "optional bounded real ETL OpenTrace ProcessTrace replay" {
    const key = std.unicode.utf8ToUtf16LeStringLiteral("RESOURCE_MANAGER_ETL_REPLAY_PATH");
    const path = std.testing.environ.getWindows(key) orelse return error.SkipZigTest;
    var capture: ReplayCapture = .{};
    var consumer = c.Consumer.init(.{ .context = &capture, .on_notice = ReplayCapture.onNotice, .on_unhandled = ReplayCapture.onUnhandled });
    try consumer.openEtl(path);
    defer _ = consumer.close() catch {};
    const status = try consumer.process();
    try std.testing.expect(status == 0 or status == 1223);
    try std.testing.expect(consumer.counters.records > 0);
    try std.testing.expect(capture.decoded > 0);
    std.debug.print("ETL replay status={d} records={d} decoded={d} rejected={d} kinds={any} freq={d} start={d} end={d} anchor={any}\n", .{ status, consumer.counters.records, capture.decoded, capture.rejected, capture.kinds, consumer.metadata.qpc_frequency, consumer.metadata.start_filetime_100ns, consumer.metadata.end_filetime_100ns, consumer.metadata.header_qpc });
    try std.testing.expectEqual(@as(u64, 0), capture.rejected);
    try std.testing.expectEqual(@as(u32, 0), try consumer.close());
    try std.testing.expectEqual(c.invalid_handle, consumer.handle.load(.acquire));
}
