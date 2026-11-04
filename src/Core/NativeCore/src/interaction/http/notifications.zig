//! Native finite notification reply. The request keeps only a byte cursor;
//! replay visits immutable fields, never repeats read/restore or a command.
//! Existing scalar/string/date encoding preserves System.Text.Json wire bytes.
const std = @import("std");
const host = @import("host.zig");
const capability = @import("../capabilities/notifications.zig");
const contract = capability.contract;
const json = contract.json;
const State = struct { count: usize = 0, offset: u64 = 0, total: u64 = 0 };
fn state(call: *host.Call) *State {
    return @ptrCast(@alignCast(call.state.ptr));
}
const Window = struct {
    output: []u8,
    skip: u64,
    position: u64 = 0,
    used: usize = 0,
    scratch: [256]u8 = undefined, // Encoding staging, not a behavioral limit.
    full: bool = false,
    fn copy(self: *Window, count: usize) void {
        const end = self.position + count;
        if (end > self.skip and self.used < self.output.len) {
            const start: usize = @intCast(self.skip -| self.position);
            const copied = @min(count - start, self.output.len - self.used);
            @memcpy(self.output[self.used..][0..copied], self.scratch[start..][0..copied]);
            self.used += copied;
        }
        self.position = end;
    }
    fn reserve(context: ?*anyopaque, used: usize, pointer: *?[*]u8, capacity: *usize) callconv(.c) i32 {
        const self: *Window = @ptrCast(@alignCast(context.?));
        self.copy(used);
        if (self.used == self.output.len) {
            self.full = true;
            return 1;
        }
        pointer.* = &self.scratch;
        capacity.* = self.scratch.len;
        return 0;
    }
    fn commit(_: ?*anyopaque, _: usize) callconv(.c) i32 {
        return 1;
    }
};
const Tokens = struct {
    writer: *json.Writer,
    // System.Text.Json's original maximum serialization depth is 64.
    arrays: [64 - 3]bool = undefined,
    commas: [64 - 3]bool = undefined,
    depth: usize = 0,
    property: bool = false,
    fn before(self: *Tokens) void {
        if (self.depth != 0 and self.arrays[self.depth - 1]) {
            if (self.commas[self.depth - 1]) self.writer.raw(",");
            self.commas[self.depth - 1] = true;
        }
    }
    fn receive(context: ?*anyopaque, kind: u32, pointer: ?[*]const u16, length: usize) callconv(.c) i32 {
        const self: *Tokens = @ptrCast(@alignCast(context.?));
        const writer = self.writer;
        if (writer.failure != 0 or kind > 8) return 1;
        const token: contract.Token = @fromBackingInt(kind);
        switch (token) {
            .property => {
                if (self.depth == 0 or self.arrays[self.depth - 1]) return 1;
                if (self.commas[self.depth - 1]) writer.raw(",");
                self.commas[self.depth - 1] = true;
                _ = json.writeString(writer, pointer, length);
                writer.raw(":");
            },
            .end => {
                if (self.depth == 0) return 1;
                self.depth -= 1;
                writer.raw(if (self.arrays[self.depth]) "]" else "}");
            },
            else => {
                self.before();
                switch (token) {
                    .object, .array => {
                        if (self.depth == self.arrays.len) return 1;
                        self.arrays[self.depth] = token == .array;
                        self.commas[self.depth] = false;
                        self.depth += 1;
                        writer.raw(if (token == .array) "[" else "{");
                    },
                    .string => {
                        _ = json.writeString(writer, pointer, length);
                    },
                    .number => {
                        for ((pointer orelse return 1)[0..length]) |char| {
                            if (!(char >= '0' and char <= '9') and char != '-' and char != '+' and char != '.' and char != 'e' and char != 'E') return 1;
                            writer.raw(&.{@intCast(char)});
                        }
                    },
                    .true_value => writer.raw("true"),
                    .false_value => writer.raw("false"),
                    .null_value => writer.raw("null"),
                    else => unreachable,
                }
            },
        }
        return if (writer.failure == 0) 0 else 1;
    }
};
fn text(context: ?*anyopaque, pointer: ?[*]const u16, length: usize) callconv(.c) i32 {
    return json.writeString(@ptrCast(@alignCast(context.?)), pointer, length);
}
fn encode(source: contract.Source, count: usize, writer: *json.Writer) !void {
    writer.raw("{\"notifications\":[");
    var comma = false;
    for (0..count) |index| {
        if (writer.failure != 0) return error.SourceFailure;
        if (!try capability.visible(source, index)) continue;
        if (comma) writer.raw(",");
        comma = true;
        var meta: contract.Meta = undefined;
        if (source.meta(source.context, index, &meta) != 0) return error.SourceFailure;
        writer.raw("{\"sequence\":");
        writer.integer(meta.sequence);
        writer.raw(",\"kind\":");
        if (source.kind(source.context, index, writer, text) != 0) return error.SourceFailure;
        writer.raw(",\"occurredAt\":");
        if (json.writeDateTime(writer, &meta.date) != 0) return error.SourceFailure;
        writer.raw(",\"facts\":");
        var tokens: Tokens = .{ .writer = writer };
        if (source.facts(source.context, index, &tokens, Tokens.receive) != 0 or tokens.depth != 0) return error.SourceFailure;
        writer.raw("}");
    }
    writer.raw("]}");
    if (writer.failure != 0) return error.SourceFailure;
}
fn emit(source: contract.Source, call: *host.Call, output: []u8) !host.Progress {
    const cursor = state(call);
    var window: Window = .{ .output = output, .skip = cursor.offset };
    const callbacks: json.OutputCallbacks = .{ .reserve = Window.reserve, .commit = Window.commit };
    var writer: json.Writer = .{ .buffer = &window.scratch, .capacity = window.scratch.len, .output_callbacks = &callbacks, .context = &window };
    encode(source, cursor.count, &writer) catch |err| if (!window.full) return err;
    if (!window.full) window.copy(writer.used);
    const first = cursor.offset == 0;
    cursor.offset += window.used;
    return .{ .output = .{ .status = 200, .body = output[0..window.used], .more = cursor.offset < cursor.total, .total_length = if (first) cursor.total else null } };
}
fn begin(context: ?*anyopaque, _: host.Request, call: *host.Call, output: []u8) !host.Progress {
    return startRead(context, call, output) catch .{ .output = .{ .status = 500, .body = &.{}, .content_type = null } };
}
fn startRead(context: ?*anyopaque, call: *host.Call, output: []u8) !host.Progress {
    const source: *const contract.Source = @ptrCast(@alignCast(context orelse return error.SourceFailure));
    const cursor = state(call);
    cursor.* = .{ .count = try capability.read(source.*) };
    var count: json.Writer = .{ .buffer = null, .capacity = 0 };
    try encode(source.*, cursor.count, &count);
    cursor.total = count.length;
    return emit(source.*, call, output);
}
fn step(context: ?*anyopaque, call: *host.Call, event: host.Event, output: []u8) !host.Progress {
    if (event == .cancel or call.isCancelled()) return .done;
    const source: *const contract.Source = @ptrCast(@alignCast(context orelse return error.SourceFailure));
    return emit(source.*, call, output);
}
fn release(context: ?*anyopaque, _: *host.Call) void {
    if (context) |pointer| {
        const source: *const contract.Source = @ptrCast(@alignCast(pointer));
        source.end(source.context);
    }
}
pub fn route(source: ?*const contract.Source, window: usize) host.Route {
    return .{ .method = "GET", .template = "/api/notifications", .requirement = .normal_read, .handler = .{ .family = .{
        .context = @constCast(source),
        .can_suspend = true,
        .capacity = .{ .state_bytes = @sizeOf(State), .state_alignment = @alignOf(State), .output_bytes = window },
        .begin = begin,
        .step = step,
        .release = release,
    } } };
}
pub export fn rm_http_notifications_borrow(source: ?*const contract.Source, window: usize, context: ?*anyopaque, consume: *const fn (?*anyopaque, *const host.Route) callconv(.c) i32) callconv(.c) i32 {
    const descriptor = route(source, if (window == 0) @import("response.zig").Limits.defaults.final_output_bytes else window);
    return consume(context, &descriptor);
}
