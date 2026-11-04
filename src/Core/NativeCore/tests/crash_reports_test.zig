const std = @import("std");
// build.zig supplies this named module; this root never imports outside its
// module directory, and no tests are placed in the product source tree.
const native = @import("crash_reports");
const testing = std.testing;

fn utf16(comptime text: []const u8) []const u16 {
    return std.unicode.utf8ToUtf16LeStringLiteral(text);
}

const Fixture = struct {
    count: i64 = 0,
    present: bool = false,
    scalar_failure: bool = false,
    text_failure: bool = false,
    bad_boolean: bool = false,
    date: native.DateParts = .{ .year = 2026, .month = 10, .day = 31, .hour = 8, .minute = 9, .second = 10, .fraction = 1234500, .offset_minutes = -330 },

    fn from(context: ?*anyopaque) *Fixture {
        return @ptrCast(@alignCast(context.?));
    }

    fn scalar(context: ?*anyopaque, index: usize, field: u32, output: *i64) callconv(.c) i32 {
        const self = from(context);
        if (self.scalar_failure or field > @backingInt(native.ScalarField.dump_removed) or index > 1) return 1;
        const selected: native.ScalarField = @fromBackingInt(@intCast(field));
        output.* = switch (selected) {
            .report_count => self.count,
            .dump_present, .fault_present, .package_present => if (self.bad_boolean) 2 else @intFromBool(self.present),
            .dump_size => 1234567890123,
            .package_size => 456,
            .acknowledged => 1,
            .result_package_size => 789,
            // The second report's dump was rotated away; the encoder passes the source's fact through.
            .dump_removed => @intFromBool(index == 1),
        };
        return 0;
    }

    fn text(context: ?*anyopaque, index: usize, field: u32, writer: *native.Writer) callconv(.c) i32 {
        const self = from(context);
        if (self.text_failure or field > @backingInt(native.TextField.result_package_created_at) or index > 1) return 1;
        const selected: native.TextField = @fromBackingInt(@intCast(field));
        const value: ?[]const u16 = switch (selected) {
            .occurred_at, .package_created_at, .result_package_created_at => return native.writeDateTime(writer, &self.date),
            .dump_folder => utf16("D:\\dumps"),
            .id => if (index == 0) utf16("r0") else utf16("r1"),
            .program => utf16("ResourceManager"),
            .role => utf16("backend"),
            .version, .fault_module, .fault_exception_code, .fault_description => null,
            .dump_status => if (index == 1) utf16("missing") else utf16("saved"),
            .dump_path => utf16("d.dmp"),
            .fault_offset => utf16("0x1"),
            .fault_managed_stack => utf16("line\nline"),
            .package_path => utf16("p.zip"),
            .result_package_path => utf16("result.zip"),
        };
        return native.writeString(writer, if (value) |s| s.ptr else null, if (value) |s| s.len else 0);
    }

    const callbacks: native.Callbacks = .{ .scalar = scalar, .text = text };
};

fn encodeAlloc(fixture: *Fixture, operation: native.Operation) ![]u8 {
    var measured: native.Response = undefined;
    try testing.expectEqual(native.Code.success, native.encode(@backingInt(operation), 1, fixture, &Fixture.callbacks, null, 0, &measured));
    const output = try testing.allocator.alloc(u8, @intCast(measured.length));
    errdefer testing.allocator.free(output);
    var written: native.Response = undefined;
    try testing.expectEqual(native.Code.success, native.encode(@backingInt(operation), 1, fixture, &Fixture.callbacks, output.ptr, output.len, &written));
    try testing.expectEqual(measured.length, written.length);
    try testing.expectEqual(@as(u32, 200), written.status);
    try testing.expectEqual(@as(u32, 1), written.content_type);
    return output;
}

const report0 = "{\"id\":\"r0\",\"occurredAt\":\"2026-10-31T08:09:10.12345-05:30\",\"program\":\"ResourceManager\",\"role\":\"backend\",\"version\":null,\"dumpStatus\":\"saved\",\"dump\":null,\"dumpRemoved\":false,\"fault\":null,\"package\":null,\"acknowledged\":true}";
const report1 = "{\"id\":\"r1\",\"occurredAt\":\"2026-10-31T08:09:10.12345-05:30\",\"program\":\"ResourceManager\",\"role\":\"backend\",\"version\":null,\"dumpStatus\":\"missing\",\"dump\":null,\"dumpRemoved\":true,\"fault\":null,\"package\":null,\"acknowledged\":true}";
const full_report = "{\"id\":\"r0\",\"occurredAt\":\"2026-10-31T08:09:10.12345-05:30\",\"program\":\"ResourceManager\",\"role\":\"backend\",\"version\":null,\"dumpStatus\":\"saved\",\"dump\":{\"path\":\"d.dmp\",\"sizeBytes\":1234567890123},\"dumpRemoved\":false,\"fault\":{\"module\":null,\"offset\":\"0x1\",\"exceptionCode\":null,\"managedStack\":\"line\\nline\",\"description\":null},\"package\":{\"path\":\"p.zip\",\"sizeBytes\":456,\"createdAt\":\"2026-10-31T08:09:10.12345-05:30\"},\"acknowledged\":true}";

test "empty list and indexed report order preserve complete wire fields" {
    var fixture: Fixture = .{};
    const empty = try encodeAlloc(&fixture, .list);
    defer testing.allocator.free(empty);
    try testing.expectEqualStrings("{\"reports\":[],\"dumpFolder\":\"D:\\\\dumps\"}", empty);
    fixture.count = 2;
    const two = try encodeAlloc(&fixture, .list);
    defer testing.allocator.free(two);
    try testing.expectEqualStrings("{\"reports\":[" ++ report0 ++ "," ++ report1 ++ "],\"dumpFolder\":\"D:\\\\dumps\"}", two);
}

test "unknown reports and successful deletion do not require source callbacks" {
    var operation: u32 = @backingInt(native.Operation.package);
    while (operation <= @backingInt(native.Operation.delete)) : (operation += 1) {
        var response: native.Response = undefined;
        var bytes: [native.unknown_report.len]u8 = undefined;
        try testing.expectEqual(native.Code.success, native.encode(operation, 0, null, null, &bytes, bytes.len, &response));
        try testing.expectEqualStrings(native.unknown_report, &bytes);
        try testing.expectEqual(@as(u32, 404), response.status);
        try testing.expectEqual(@as(u32, 2), response.content_type);
        try testing.expectEqual(@as(u64, bytes.len), response.length);
    }
    var deleted: native.Response = undefined;
    try testing.expectEqual(native.Code.success, native.encode(@backingInt(native.Operation.delete), 1, null, null, null, 0, &deleted));
    try testing.expectEqual(@as(u64, 0), deleted.length);
    try testing.expectEqual(@as(u32, 204), deleted.status);
    try testing.expectEqual(@as(u32, 0), deleted.content_type);
}

test "acknowledgement and package preserve record order and nullable fields" {
    var fixture: Fixture = .{ .present = true };
    const ack = try encodeAlloc(&fixture, .acknowledge);
    defer testing.allocator.free(ack);
    try testing.expectEqualStrings("{\"report\":" ++ full_report ++ "}", ack);
    const package = try encodeAlloc(&fixture, .package);
    defer testing.allocator.free(package);
    try testing.expectEqualStrings("{\"report\":" ++ full_report ++ ",\"package\":{\"path\":\"result.zip\",\"sizeBytes\":789,\"createdAt\":\"2026-10-31T08:09:10.12345-05:30\"}}", package);
}

test "all short buffer boundaries count the full response without overwriting guards" {
    var fixture: Fixture = .{ .count = 1, .present = true };
    const expected = try encodeAlloc(&fixture, .list);
    defer testing.allocator.free(expected);
    const guarded = try testing.allocator.alloc(u8, expected.len + 2);
    defer testing.allocator.free(guarded);
    var capacity: usize = 0;
    while (capacity <= expected.len) : (capacity += 1) {
        @memset(guarded, 0xA5);
        var response: native.Response = undefined;
        const code = native.encode(@backingInt(native.Operation.list), 1, &fixture, &Fixture.callbacks, guarded[1..].ptr, capacity, &response);
        try testing.expectEqual(if (capacity == expected.len) native.Code.success else native.Code.short_buffer, code);
        try testing.expectEqual(@as(u64, expected.len), response.length);
        try testing.expectEqualSlices(u8, expected[0..capacity], guarded[1..][0..capacity]);
        try testing.expectEqual(@as(u8, 0xA5), guarded[0]);
        try testing.expectEqual(@as(u8, 0xA5), guarded[capacity + 1]);
    }
}

test "invalid ABI and failed source callbacks are explicit" {
    var response: native.Response = undefined;
    try testing.expectEqual(native.Code.invalid, native.encode(99, 1, null, null, null, 0, &response));
    try testing.expectEqual(native.Code.invalid, native.encode(0, 2, null, null, null, 0, &response));
    try testing.expectEqual(native.Code.invalid, native.encode(0, 1, null, null, null, 1, &response));
    try testing.expectEqual(native.Code.invalid, native.encode(0, 1, null, null, null, 0, null));
    try testing.expectEqual(native.Code.invalid, native.encode(0, 1, null, null, null, 0, &response));
    var fixture: Fixture = .{ .scalar_failure = true };
    try testing.expectEqual(native.Code.callback, native.encode(0, 1, &fixture, &Fixture.callbacks, null, 0, &response));
    fixture.scalar_failure = false;
    fixture.text_failure = true;
    try testing.expectEqual(native.Code.callback, native.encode(0, 1, &fixture, &Fixture.callbacks, null, 0, &response));
    fixture.text_failure = false;
    fixture.count = -1;
    try testing.expectEqual(native.Code.invalid, native.encode(0, 1, &fixture, &Fixture.callbacks, null, 0, &response));
    fixture.count = 1;
    fixture.bad_boolean = true;
    try testing.expectEqual(native.Code.invalid, native.encode(0, 1, &fixture, &Fixture.callbacks, null, 0, &response));
}

test "UTF16 relaxed JSON matches short escapes forbidden BMP supplementary and malformed input" {
    const input = [_]u16{ '"', '\\', 8, 9, 10, 12, 13, 0, '<', '>', '&', '\'', '`', 0x85, 0xA0, 0x2028, 0x2029, 0xE000, 0x4E2D, 0xFFFD, 0xD83D, 0xDE00, 0xD840, 0xDC00, 0xD800, 'x', 0xDC00 };
    const expected = "\"\\\"\\\\\\b\\t\\n\\f\\r\\u0000<>&'`\\u0085\\u00A0\\u2028\\u2029\\uE000中�\\uD83D\\uDE00\\uD840\\uDC00\\uFFFDx\\uFFFD\"";
    var bytes: [expected.len]u8 = undefined;
    var writer: native.Writer = .{ .buffer = &bytes, .capacity = bytes.len };
    try testing.expectEqual(native.Code.success, native.writeString(&writer, &input, input.len));
    try testing.expectEqualStrings(expected, &bytes);
    try testing.expectEqual(@as(u64, bytes.len), writer.length);

    var null_bytes: [4]u8 = undefined;
    var null_writer: native.Writer = .{ .buffer = &null_bytes, .capacity = null_bytes.len };
    try testing.expectEqual(native.Code.success, native.writeString(&null_writer, null, 0));
    try testing.expectEqualStrings("null", &null_bytes);
    const empty = [_]u16{0};
    var empty_bytes: [2]u8 = undefined;
    var empty_writer: native.Writer = .{ .buffer = &empty_bytes, .capacity = empty_bytes.len };
    try testing.expectEqual(native.Code.success, native.writeString(&empty_writer, &empty, 0));
    try testing.expectEqualStrings("\"\"", &empty_bytes);
    try testing.expectEqual(native.Code.invalid, native.writeString(null, null, 0));
}

test "dates preserve seven tick digits trimming offset and calendar boundaries" {
    const Case = struct { date: native.DateParts, expected: []const u8 };
    const cases = [_]Case{
        .{ .date = .{ .year = 1, .month = 1, .day = 1, .hour = 0, .minute = 0, .second = 0, .fraction = 0, .offset_minutes = 0 }, .expected = "\"0001-01-01T00:00:00+00:00\"" },
        .{ .date = .{ .year = 9999, .month = 12, .day = 31, .hour = 23, .minute = 59, .second = 59, .fraction = 9999999, .offset_minutes = 0 }, .expected = "\"9999-12-31T23:59:59.9999999+00:00\"" },
        .{ .date = .{ .year = 2000, .month = 2, .day = 29, .hour = 12, .minute = 3, .second = 4, .fraction = 1, .offset_minutes = 840 }, .expected = "\"2000-02-29T12:03:04.0000001+14:00\"" },
        .{ .date = .{ .year = 2026, .month = 10, .day = 31, .hour = 8, .minute = 9, .second = 10, .fraction = 1200000, .offset_minutes = -840 }, .expected = "\"2026-10-31T08:09:10.12-14:00\"" },
    };
    var index: usize = 0;
    while (index < cases.len) : (index += 1) {
        var bytes: [35]u8 = undefined;
        var writer: native.Writer = .{ .buffer = &bytes, .capacity = bytes.len };
        try testing.expectEqual(native.Code.success, native.writeDateTime(&writer, &cases[index].date));
        try testing.expectEqualStrings(cases[index].expected, bytes[0..@intCast(writer.length)]);
    }
    var invalid = cases[2].date;
    invalid.year = 1900;
    var writer: native.Writer = .{ .buffer = null, .capacity = 0 };
    try testing.expectEqual(native.Code.invalid, native.writeDateTime(&writer, &invalid));
    try testing.expectEqual(native.Code.invalid, native.writeDateTime(null, &invalid));
    try testing.expectEqual(native.Code.invalid, native.writeDateTime(&writer, null));
}

const StreamFixture = struct {
    source: Fixture = .{ .count = 2, .present = true },
    storage: []u8,
    segment_size: usize,
    committed: usize = 0,
    current_capacity: usize = 0,
    reserve_calls: usize = 0,
    commit_calls: usize = 0,
    fail_reserve: bool = false,
    fail_commit: bool = false,
    empty_segment: bool = false,

    fn from(context: ?*anyopaque) *StreamFixture {
        return @ptrCast(@alignCast(context.?));
    }
    fn scalar(context: ?*anyopaque, index: usize, field: u32, value: *i64) callconv(.c) i32 {
        return Fixture.scalar(&from(context).source, index, field, value);
    }
    fn text(context: ?*anyopaque, index: usize, field: u32, writer: *native.Writer) callconv(.c) i32 {
        return Fixture.text(&from(context).source, index, field, writer);
    }
    fn reserve(context: ?*anyopaque, completed: usize, ptr: *?[*]u8, capacity: *usize) callconv(.c) i32 {
        const self = from(context);
        self.reserve_calls += 1;
        if (self.fail_reserve or completed != self.current_capacity) return 1;
        self.committed += completed;
        if (self.empty_segment) {
            ptr.* = null;
            capacity.* = 0;
            return 0;
        }
        self.current_capacity = @min(self.segment_size, self.storage.len - self.committed);
        ptr.* = self.storage[self.committed..].ptr;
        capacity.* = self.current_capacity;
        return 0;
    }
    fn commit(context: ?*anyopaque, completed: usize) callconv(.c) i32 {
        const self = from(context);
        self.commit_calls += 1;
        if (self.fail_commit or completed > self.current_capacity) return 1;
        self.committed += completed;
        return 0;
    }
    const callbacks: native.Callbacks = .{ .scalar = scalar, .text = text };
    const output_callbacks: native.OutputCallbacks = .{ .reserve = reserve, .commit = commit };
};

test "streaming writes directly into final segments including one-byte segments" {
    var source: Fixture = .{ .count = 2, .present = true };
    const expected = try encodeAlloc(&source, .list);
    defer testing.allocator.free(expected);
    const storage = try testing.allocator.alloc(u8, expected.len + 2);
    defer testing.allocator.free(storage);
    const sizes = [_]usize{ 1, 2, 7, 64, expected.len, expected.len + 1 };
    var index: usize = 0;
    while (index < sizes.len) : (index += 1) {
        @memset(storage, 0xA5);
        var stream: StreamFixture = .{ .storage = storage[1..][0..expected.len], .segment_size = sizes[index] };
        var response: native.Response = undefined;
        try testing.expectEqual(native.Code.success, native.encodeStream(0, 1, &stream, &StreamFixture.callbacks, &StreamFixture.output_callbacks, &response));
        try testing.expectEqualSlices(u8, expected, storage[1..][0..expected.len]);
        try testing.expectEqual(expected.len, stream.committed);
        try testing.expectEqual(@as(u64, expected.len), response.length);
        try testing.expectEqual(@as(usize, 1), stream.commit_calls);
        try testing.expectEqual(@as(u8, 0xA5), storage[0]);
        try testing.expectEqual(@as(u8, 0xA5), storage[storage.len - 1]);
    }
}

test "stream metadata and callback failures never commit an incomplete final segment" {
    var bytes: [1024]u8 = undefined;
    var response: native.Response = undefined;
    try testing.expectEqual(native.Code.success, native.describe(1, 0, &response));
    try testing.expectEqual(@as(u32, 404), response.status);
    try testing.expectEqual(@as(u64, 0), response.length);
    try testing.expectEqual(native.Code.invalid, native.describe(99, 0, &response));
    try testing.expectEqual(native.Code.invalid, native.describe(0, 0, null));
    var stream: StreamFixture = .{ .storage = &bytes, .segment_size = 7, .fail_reserve = true };
    try testing.expectEqual(native.Code.callback, native.encodeStream(0, 1, &stream, &StreamFixture.callbacks, &StreamFixture.output_callbacks, &response));
    try testing.expectEqual(@as(usize, 0), stream.commit_calls);
    stream = .{ .storage = &bytes, .segment_size = 7, .empty_segment = true };
    try testing.expectEqual(native.Code.callback, native.encodeStream(0, 1, &stream, &StreamFixture.callbacks, &StreamFixture.output_callbacks, &response));
    try testing.expectEqual(@as(usize, 0), stream.commit_calls);
    stream = .{ .storage = &bytes, .segment_size = 7, .fail_commit = true };
    try testing.expectEqual(native.Code.callback, native.encodeStream(1, 0, &stream, null, &StreamFixture.output_callbacks, &response));
    try testing.expectEqual(@as(usize, 1), stream.commit_calls);
    stream = .{ .storage = &bytes, .segment_size = 7 };
    stream.source.text_failure = true;
    try testing.expectEqual(native.Code.callback, native.encodeStream(0, 1, &stream, &StreamFixture.callbacks, &StreamFixture.output_callbacks, &response));
    try testing.expectEqual(@as(usize, 0), stream.commit_calls);
    stream = .{ .storage = &bytes, .segment_size = 7 };
    try testing.expectEqual(native.Code.success, native.encodeStream(@backingInt(native.Operation.delete), 1, &stream, null, &StreamFixture.output_callbacks, &response));
    try testing.expectEqual(@as(usize, 0), stream.reserve_calls);
    try testing.expectEqual(@as(usize, 1), stream.commit_calls);
    try testing.expectEqual(native.Code.invalid, native.encodeStream(@backingInt(native.Operation.delete), 1, &stream, null, null, &response));
}
