//! HTTP adapter over the crash-domain view. Z1 remains the only HTTP encoder.
const codec = @import("crash_reports.zig");
const contract = @import("../contracts/crash_reports.zig");
const view = contract.view;

const Adapter = struct {
    source: view.Source,

    fn from(context: ?*anyopaque) *Adapter {
        return @ptrCast(@alignCast(context.?));
    }
    fn scalar(context: ?*anyopaque, index: usize, field: u32, value: *i64) callconv(.c) i32 {
        if (field > @backingInt(view.Scalar.dump_removed)) return view.Code.invalid;
        return from(context).source.scalar(index, @fromBackingInt(@intCast(field)), value);
    }
    fn string(context: ?*anyopaque, pointer: ?[*]const u16, length: usize) callconv(.c) i32 {
        return codec.writeString(@ptrCast(@alignCast(context.?)), pointer, length);
    }
    fn text(context: ?*anyopaque, index: usize, field: u32, writer: *codec.Writer) callconv(.c) i32 {
        const source = from(context).source;
        if (field == 2 or field == 14 or field == 16) {
            var date: view.DateParts = undefined;
            const code = source.date(index, if (field == 2) .occurred_at else if (field == 14) .package_created_at else .result_package_created_at, &date);
            if (code != 0) return code;
            const parts: codec.DateParts = .{ .year = date.year, .month = date.month, .day = date.day, .hour = date.hour, .minute = date.minute, .second = date.second, .fraction = date.fraction, .offset_minutes = date.offset_minutes };
            return codec.writeDateTime(writer, &parts);
        }
        if (field > @backingInt(view.Text.result_package_path)) return view.Code.invalid;
        return source.text(index, @fromBackingInt(@intCast(field)), writer, string);
    }
};

const fields: codec.Callbacks = .{ .scalar = Adapter.scalar, .text = Adapter.text };
pub const StepError = error{ Invalid, Callback, Cancelled, Failed, Overflow };
pub const Step = struct { used: usize, done: bool };

/// A cursor contains only schema/member/string/escape progress and small scalar
/// bytes. It owns NO Source, field pointer, lease or result. For EVERY step the
/// caller must synchronously borrow the SAME retained, immutable accepted result.
/// The current managed Borrow API is call-scoped; it is not that retention API.
/// End that borrow before handing the output window to an asynchronous sender.
pub const Cursor = struct {
    const State = enum { running, done, failed, cancelled };
    const StringState = struct {
        field: view.Text,
        index: usize,
        phase: enum { open, units, close, done } = .open,
        length: ?usize = null,
        is_null: bool = false,
        offset: usize = 0,
    };
    traversal: codec.Traversal,
    response: codec.Response,
    state: State = .running,
    literal: []const u8 = &.{},
    // A DateTimeOffset token is at most 35 bytes; UTF-16 units need at most 12.
    small: [40]u8 = undefined,
    small_start: usize = 0,
    small_end: usize = 0,
    string_state: ?StringState = null,

    pub fn init(operation: u32, found: u32) StepError!Cursor {
        var metadata: codec.Response = undefined;
        if (codec.describe(operation, found, &metadata) != 0) return error.Invalid;
        return .{ .traversal = codec.Traversal.init(@fromBackingInt(@intCast(operation)), metadata.status), .response = metadata };
    }
    pub fn cancel(self: *Cursor) void {
        if (self.state == .running) self.state = .cancelled;
    }
    fn stage(self: *Cursor, bytes: []const u8) void {
        @memcpy(self.small[0..bytes.len], bytes);
        self.small_start = 0;
        self.small_end = bytes.len;
    }
    fn drain(self: *Cursor, output: []u8, used: *usize) void {
        const count = @min(output.len - used.*, self.small_end - self.small_start);
        @memcpy(output[used.*..][0..count], self.small[self.small_start..][0..count]);
        used.* += count;
        self.small_start += count;
    }
    const TextBorrow = struct {
        cursor: *Cursor,
        output: []u8,
        used: *usize,
        calls: usize = 0,
        fn receive(context: ?*anyopaque, pointer: ?[*]const u16, length: usize) callconv(.c) i32 {
            const self: *TextBorrow = @ptrCast(@alignCast(context.?));
            self.calls += 1;
            if (self.calls != 1 or (pointer == null and length != 0)) return codec.Code.invalid;
            const cursor = self.cursor;
            const progress = &cursor.string_state.?;
            if (progress.length) |known| {
                if (known != length or progress.is_null != (pointer == null)) return codec.Code.callback;
            } else {
                progress.length = length;
                progress.is_null = pointer == null;
            }
            while (self.used.* < self.output.len) {
                if (cursor.small_start != cursor.small_end) {
                    cursor.drain(self.output, self.used);
                    continue;
                }
                switch (progress.phase) {
                    .open => {
                        cursor.stage(if (pointer == null) "null" else "\"");
                        progress.phase = if (pointer == null) .done else .units;
                    },
                    .units => {
                        if (progress.offset == length) {
                            progress.phase = .close;
                            continue;
                        }
                        var writer: codec.Writer = .{ .buffer = &cursor.small, .capacity = cursor.small.len };
                        progress.offset += codec.writeStringUnit(&writer, pointer.?[0..length], progress.offset);
                        cursor.small_start = 0;
                        cursor.small_end = @intCast(writer.length);
                    },
                    .close => {
                        cursor.stage("\"");
                        progress.phase = .done;
                    },
                    .done => break,
                }
            }
            return 0;
        }
    };
    pub fn step(self: *Cursor, source: view.Source, output: []u8) StepError!Step {
        switch (self.state) {
            .done => return .{ .used = 0, .done = true },
            .failed => return error.Failed,
            .cancelled => return error.Cancelled,
            .running => {},
        }
        if (output.len == 0) return error.Invalid;
        errdefer self.state = .failed;
        var adapter: Adapter = .{ .source = source };
        var used: usize = 0;
        while (used < output.len) {
            if (self.small_start != self.small_end) {
                self.drain(output, &used);
                continue;
            }
            if (self.literal.len != 0) {
                const count = @min(output.len - used, self.literal.len);
                @memcpy(output[used..][0..count], self.literal[0..count]);
                self.literal = self.literal[count..];
                used += count;
                continue;
            }
            if (self.string_state) |progress| {
                if (progress.phase == .done) {
                    self.string_state = null;
                    continue;
                }
                var borrowed: TextBorrow = .{ .cursor = self, .output = output, .used = &used };
                if (source.text(progress.index, progress.field, &borrowed, TextBorrow.receive) != 0 or borrowed.calls != 1) return error.Callback;
                continue;
            }
            const piece = self.traversal.next(&adapter, &fields) catch |err| return switch (err) {
                error.Invalid => error.Invalid,
                error.Callback => error.Callback,
            };
            const item = piece orelse {
                self.state = .done;
                break;
            };
            switch (item.value) {
                .raw => |bytes| self.literal = bytes,
                .number => |value| {
                    var writer: codec.Writer = .{ .buffer = &self.small, .capacity = self.small.len };
                    writer.integer(value);
                    self.small_start = 0;
                    self.small_end = @intCast(writer.length);
                },
                .text => |field| {
                    if (field == .occurred_at or field == .package_created_at or field == .result_package_created_at) {
                        var writer: codec.Writer = .{ .buffer = &self.small, .capacity = self.small.len };
                        const code = Adapter.text(&adapter, item.index, @backingInt(field), &writer);
                        if (code != 0) return if (writer.failure == codec.Code.invalid) error.Invalid else error.Callback;
                        self.small_start = 0;
                        self.small_end = @intCast(writer.length);
                    } else self.string_state = .{ .field = @fromBackingInt(@intCast(@backingInt(field))), .index = item.index };
                },
            }
        }
        const total = @addWithOverflow(self.response.length, @as(u64, @intCast(used)));
        if (total[1] != 0) return error.Overflow;
        self.response.length = total[0];
        return .{ .used = used, .done = self.state == .done };
    }
};

/// Existing product ABI consumer now uses the SAME resumable schema/cursor.
/// This wrapper is deliberately synchronous: OutputCallbacks cannot await and
/// the original ViewResult is only borrowed for this call. It is not evidence
/// of a retained result or native listener connection across network waits.
pub fn encodeStream(result: ?*const contract.ViewResult, context: ?*anyopaque, output: ?*const codec.OutputCallbacks, response: ?*codec.Response) i32 {
    const input = result orelse return codec.Code.invalid;
    const metadata = response orelse return codec.Code.invalid;
    if (@backingInt(input.outcome) > @backingInt(contract.Outcome.not_found)) return codec.Code.invalid;
    const segments = output orelse return codec.Code.invalid;
    var cursor = Cursor.init(@backingInt(input.operation), @intFromBool(input.outcome == .success)) catch return codec.Code.invalid;
    metadata.* = cursor.response;
    if (cursor.response.status == 204) return if (segments.commit(context, 0) == 0) codec.Code.success else codec.Code.callback;
    var completed: usize = 0;
    while (true) {
        var pointer: ?[*]u8 = null;
        var capacity: usize = 0;
        if (segments.reserve(context, completed, &pointer, &capacity) != 0 or pointer == null or capacity == 0) return codec.Code.callback;
        const step = cursor.step(input.source, pointer.?[0..capacity]) catch |err| {
            metadata.* = cursor.response;
            return switch (err) {
                error.Invalid => codec.Code.invalid,
                error.Overflow => codec.Code.overflow,
                else => codec.Code.callback,
            };
        };
        metadata.* = cursor.response;
        if (step.done) return if (segments.commit(context, step.used) == 0) codec.Code.success else codec.Code.callback;
        completed = step.used;
    }
}
