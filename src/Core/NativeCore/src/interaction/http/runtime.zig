//! Runtime HTTP binding and resumable codec. The one catalog/identity source
//! remains borrowed only within callbacks; no domain object or pin enters State.
const std = @import("std");
pub const host = @import("host.zig");
const json = @import("crash_reports.zig");
pub const Kind = enum(u32) { health, identity, browsers, public_browsers };
pub const Visitor = *const fn (?*anyopaque, ?[*]const u16, usize) callconv(.c) i32;
pub const Source = extern struct {
    context: ?*anyopaque,
    // 0 begin, 1 completion/readiness, 2 cancel and join, 3 release.
    // 0 pending, 1 ready, 2 cancelled, -1 terminal source failure.
    read: *const fn (?*anyopaque, u32, Kind, u32, ?*anyopaque, *const fn (?*anyopaque) callconv(.c) void) callconv(.c) i32,
    scalar: *const fn (?*anyopaque, u32, usize, u32, *u64) callconv(.c) i32,
    text: *const fn (?*anyopaque, u32, usize, u32, ?*anyopaque, Visitor) callconv(.c) i32,
    date: *const fn (?*anyopaque, *json.DateParts) callconv(.c) i32,
};
pub const paths = [_][]const u8{ "/api/health", "/api/runtime/identity", "/api/browser-runtimes", "/api/public/v1/browser-runtimes" };
const identity_names = [_][]const u8{ "productId", "protocolVersion", "buildVersion", "instanceId", "processId", "processStartUtcTicks", "executablePath", "challenge" };
const entry_names = [_][]const u8{ "id", "name", "kind", "version", "executablePath", "runtimeDirectory", "source", "nativeWebView2Compatible", "selected" };
// Format-derived window granularity, NOT a maximum legal response. Larger
// prepared windows may be supplied by composition; long fields resume in place.
pub const minimum_window = @max(host.errorOutputBound(), "{\"error\":\"invalid-runtime-identity-challenge\"}".len);
pub const State = struct {
    kind: Kind = .health,
    refresh: bool = false,
    accepted: bool = false,
    ready: bool = false,
    finished: bool = false,
    challenge: [64]u8 = undefined, // Existing wire protocol: 64 ASCII hex digits.
    token: usize = 0,
    entry_token: usize = 0,
    entry: bool = false,
    candidate: usize = 0,
    candidate_count: usize = 0,
    candidate_comma: bool = false,
    group: u32 = 0,
    raw_offset: usize = 0,
    text_offset: usize = 0,
    text_phase: u8 = 0,
};
pub fn route(kind: Kind, input_source: ?*const Source, window: usize) host.Route {
    return .{ .method = "GET", .template = paths[@backingInt(kind)], .requirement = if (kind == .health) .anonymous else .normal_read, .handler = .{ .family = .{ .context = @constCast(input_source), .capacity = .{ .state_bytes = @sizeOf(State), .state_alignment = @alignOf(State), .output_bytes = @max(window, minimum_window) }, .begin = begin, .step = step, .release = release } } };
}
fn value(call: *host.Call) *State {
    return @ptrCast(@alignCast(call.state.ptr));
}
fn source(pointer: ?*anyopaque) !*const Source {
    return @ptrCast(@alignCast(pointer orelse return error.SourceUnavailable));
}
fn wake(pointer: ?*anyopaque) callconv(.c) void {
    const call: *host.Call = @ptrCast(@alignCast(pointer.?));
    call.wake.notify();
}
fn poll(pointer: ?*anyopaque, call: *host.Call, event: u32) !i32 {
    const owner = try source(pointer);
    const state = value(call);
    return owner.read(owner.context, event, state.kind, @intFromBool(state.refresh), call, wake);
}
fn empty(status: u16) host.Progress {
    return .{ .output = .{ .status = status, .body = &.{}, .content_type = null } };
}
fn begin(pointer: ?*anyopaque, request: host.Request, call: *host.Call, output: []u8) !host.Progress {
    const state = value(call);
    state.* = .{};
    var found = false;
    for (paths, 0..) |path, index| if (std.mem.eql(u8, request.route_template, path)) {
        state.kind = @fromBackingInt(@as(u32, @intCast(index)));
        found = true;
        break;
    };
    if (!found) return error.InvalidRuntimeRoute;
    if (state.kind == .health) return empty(204);
    if (state.kind == .identity) {
        if (!bindChallenge(request.target, &state.challenge)) return .{ .output = try host.failure(output, 400, "invalid-runtime-identity-challenge") };
    } else if (state.kind == .browsers) {
        state.refresh = bindRefresh(request.target) catch return empty(400);
    }
    state.accepted = true;
    return complete(pointer, call, output, try poll(pointer, call, 0));
}
fn complete(pointer: ?*anyopaque, call: *host.Call, output: []u8, result: i32) !host.Progress {
    if (result == 0) return .pending;
    if (call.isCancelled() or result == 2) return error.Cancelled;
    if (result != 1) return empty(500); // Original unhandled source exception: empty 500.
    const state = value(call);
    state.ready = true;
    if (state.kind == .browsers or state.kind == .public_browsers) state.candidate_count = @intCast(try scalar(try source(pointer), 4, 0, 2));
    return emit(try source(pointer), state, output);
}
fn step(pointer: ?*anyopaque, call: *host.Call, event: host.Event, output: []u8) !host.Progress {
    const state = value(call);
    if (event == .cancel or call.isCancelled()) {
        if (!state.accepted) return .done;
        const result = try poll(pointer, call, if (event == .cancel) 2 else 1);
        return if (result == 0) .pending else .done;
    }
    if (!state.ready) return complete(pointer, call, output, try poll(pointer, call, 1));
    return emit(try source(pointer), state, output);
}
fn release(pointer: ?*anyopaque, call: *host.Call) void {
    if (value(call).accepted) _ = poll(pointer, call, 3) catch {};
}
fn scalar(owner: *const Source, group: u32, index: usize, field: u32) !u64 {
    var result: u64 = 0;
    if (owner.scalar(owner.context, group, index, field, &result) != 0) return error.RuntimeSource;
    return result;
}
const Text = struct { group: u32, index: usize, field: u32 };
const Token = union(enum) { raw: []const u8, text: Text, number: u64, boolean: bool, challenge, date, done };
fn next(owner: *const Source, state: *State, temporary: []u8) !Token {
    if (state.entry) {
        if (state.entry_token == 0) return .{ .raw = "{" };
        if (state.entry_token == 19) return .{ .raw = "}" };
        const field = (state.entry_token - 1) / 2;
        if (state.entry_token % 2 == 1) return .{ .raw = try std.fmt.bufPrint(temporary, "{s}\"{s}\":", .{ if (field == 0) "" else ",", entry_names[field] }) };
        const index = if (state.group == 2) state.candidate else 0;
        return if (field < 7) .{ .text = .{ .group = state.group, .index = index, .field = @intCast(field) } } else .{ .boolean = (try scalar(owner, state.group, index, @intCast(field - 6))) != 0 };
    }
    if (state.kind == .identity) {
        if (state.token == 0) return .{ .raw = "{" };
        if (state.token == 17) return .{ .raw = "}" };
        if (state.token == 18) return .done;
        const field = (state.token - 1) / 2;
        if (state.token % 2 == 1) return .{ .raw = try std.fmt.bufPrint(temporary, "{s}\"{s}\":", .{ if (field == 0) "" else ",", identity_names[field] }) };
        return switch (field) {
            0, 2, 6 => .{ .text = .{ .group = 3, .index = 0, .field = @intCast(field) } },
            7 => .challenge,
            else => .{ .number = try scalar(owner, 3, 0, @intCast(field)) },
        };
    }
    // Browser envelope traversal. Entry/candidate cursors advance only after
    // their current token has been entirely consumed by the current window.
    while (true) {
        switch (state.token) {
            0 => return .{ .raw = "{\"sharedRuntime\":" },
            1, 3 => {
                state.group = if (state.token == 1) 0 else 1;
                if (try scalar(owner, state.group, 0, 0) == 0) return .{ .raw = "null" };
                state.entry = true;
                state.entry_token = 0;
                return .{ .raw = "{" };
            },
            2 => return .{ .raw = ",\"browserFallback\":" },
            4 => return .{ .raw = ",\"candidates\":[" },
            5 => {
                if (state.candidate == state.candidate_count) {
                    state.token = 6;
                    continue;
                }
                if (state.candidate_comma) return .{ .raw = "," };
                state.group = 2;
                state.entry = true;
                state.entry_token = 0;
                return .{ .raw = "{" };
            },
            6 => return .{ .raw = "],\"installComponentId\":" },
            7 => return .{ .text = .{ .group = 4, .index = 0, .field = 0 } },
            8 => return .{ .raw = ",\"capturedAt\":" },
            9 => return .date,
            10 => return .{ .raw = "}" },
            else => return .done,
        }
    }
}
fn advance(state: *State) void {
    state.raw_offset = 0;
    state.text_offset = 0;
    state.text_phase = 0;
    if (state.entry) {
        state.entry_token += 1;
        if (state.entry_token == 20) {
            state.entry = false;
            if (state.group == 2) {
                state.candidate += 1;
                state.candidate_comma = true;
            } else state.token += 1;
        }
    } else if (state.token == 5 and state.candidate_comma) state.candidate_comma = false else state.token += 1;
}
fn raw(state: *State, output: []u8, used: *usize, bytes: []const u8) bool {
    const count = @min(output.len - used.*, bytes.len - state.raw_offset);
    @memcpy(output[used.*..][0..count], bytes[state.raw_offset..][0..count]);
    used.* += count;
    state.raw_offset += count;
    return state.raw_offset == bytes.len;
}
const TextOutput = struct {
    state: *State,
    output: []u8,
    used: *usize,
    done: bool = false,
    fn visit(pointer: ?*anyopaque, text: ?[*]const u16, length: usize) callconv(.c) i32 {
        const self: *TextOutput = @ptrCast(@alignCast(pointer.?));
        if (text == null) {
            self.done = raw(self.state, self.output, self.used, "null");
            return 0;
        }
        if (self.state.text_phase == 0) {
            if (self.used.* == self.output.len) return 0;
            self.output[self.used.*] = '"';
            self.used.* += 1;
            self.state.text_phase = 1;
        }
        while (self.state.text_offset < length) {
            const offset = self.state.text_offset;
            const count: usize = if (text.?[offset] >= 0xd800 and text.?[offset] <= 0xdbff and offset + 1 < length and text.?[offset + 1] >= 0xdc00 and text.?[offset + 1] <= 0xdfff) 2 else 1;
            var bytes: [14]u8 = undefined;
            var writer = json.Writer{ .buffer = &bytes, .capacity = bytes.len };
            if (json.writeString(&writer, text.? + offset, count) != 0) return 1;
            const encoded = bytes[1 .. @as(usize, @intCast(writer.length)) - 1];
            if (encoded.len > self.output.len - self.used.*) return 0;
            @memcpy(self.output[self.used.*..][0..encoded.len], encoded);
            self.used.* += encoded.len;
            self.state.text_offset += count;
        }
        if (self.used.* == self.output.len) return 0;
        self.output[self.used.*] = '"';
        self.used.* += 1;
        self.done = true;
        return 0;
    }
};
fn emit(owner: *const Source, state: *State, output: []u8) !host.Progress {
    if (state.finished) return .{ .output = .{ .status = 200, .body = output[0..0] } };
    var used: usize = 0;
    while (used < output.len) {
        var bytes: [96]u8 = undefined;
        const token = try next(owner, state, &bytes);
        var complete_token = false;
        switch (token) {
            .done => {
                state.finished = true;
                break;
            },
            .raw => |text| complete_token = raw(state, output, &used, text),
            .number => |number| complete_token = raw(state, output, &used, try std.fmt.bufPrint(&bytes, "{d}", .{number})),
            .boolean => |boolean| complete_token = raw(state, output, &used, if (boolean) "true" else "false"),
            .challenge => {
                bytes[0] = '"';
                @memcpy(bytes[1..65], &state.challenge);
                bytes[65] = '"';
                complete_token = raw(state, output, &used, bytes[0..66]);
            },
            .date => {
                var date: json.DateParts = undefined;
                if (owner.date(owner.context, &date) != 0) return error.RuntimeSource;
                var writer = json.Writer{ .buffer = &bytes, .capacity = bytes.len };
                if (json.writeDateTime(&writer, &date) != 0) return error.RuntimeSource;
                complete_token = raw(state, output, &used, bytes[0..@intCast(writer.length)]);
            },
            .text => |text| {
                var destination = TextOutput{ .state = state, .output = output, .used = &used };
                if (owner.text(owner.context, text.group, text.index, text.field, &destination, TextOutput.visit) != 0) return error.RuntimeSource;
                complete_token = destination.done;
            },
        }
        if (complete_token) advance(state) else break;
    }
    // Match Results.Json's unknown-length response. The final empty window ends
    // chunked framing after the last bytes, without retaining a source borrow.
    return .{ .output = .{ .status = 200, .body = output[0..used], .more = true } };
}
const Decoder = struct {
    bytes: []const u8,
    offset: usize = 0,
    fn byte(self: *Decoder) ?u8 {
        if (self.offset == self.bytes.len) return null;
        const result = self.bytes[self.offset];
        self.offset += 1;
        if (result == '+') return ' ';
        if (result == '%' and self.bytes.len - self.offset >= 2 and std.ascii.isHex(self.bytes[self.offset]) and std.ascii.isHex(self.bytes[self.offset + 1])) {
            const decoded = std.fmt.parseInt(u8, self.bytes[self.offset..][0..2], 16) catch return result;
            self.offset += 2;
            return decoded;
        }
        return result;
    }
    fn point(self: *Decoder) ?u21 {
        const first = self.byte() orelse return null;
        var bytes: [4]u8 = undefined;
        bytes[0] = first;
        const count = std.unicode.utf8ByteSequenceLength(first) catch return 0xfffd;
        var index: usize = 1;
        while (index < count) : (index += 1) bytes[index] = self.byte() orelse return 0xfffd;
        return std.unicode.utf8Decode(bytes[0..count]) catch 0xfffd;
    }
};
fn query(target: []const u8, name: []const u8) struct { value: ?[]const u8, duplicate: bool } {
    const start = std.mem.indexOfScalar(u8, target, '?') orelse return .{ .value = null, .duplicate = false };
    var fields = std.mem.splitScalar(u8, target[start + 1 ..], '&');
    var found: ?[]const u8 = null;
    while (fields.next()) |field| {
        const equal = std.mem.indexOfScalar(u8, field, '=') orelse field.len;
        var key = Decoder{ .bytes = field[0..equal] };
        var at: usize = 0;
        var match = true;
        while (key.byte()) |byte| {
            if (at >= name.len or std.ascii.toLower(byte) != name[at]) match = false;
            at += 1;
        }
        if (!match or at != name.len) continue;
        if (found != null) return .{ .value = found, .duplicate = true };
        found = if (equal == field.len) "" else field[equal + 1 ..];
    }
    return .{ .value = found, .duplicate = false };
}
pub fn bindChallenge(target: []const u8, output: *[64]u8) bool {
    const selected = query(target, "challenge");
    if (selected.duplicate) return false;
    var text = Decoder{ .bytes = selected.value orelse return false };
    var index: usize = 0;
    while (text.byte()) |byte| {
        if (index == output.len or !std.ascii.isHex(byte)) return false;
        output[index] = byte;
        index += 1;
    }
    return index == output.len;
}
fn white(point: u21) bool {
    return point == 0 or (point >= 9 and point <= 13) or point == 32 or point == 0x85 or point == 0xa0 or point == 0x1680 or (point >= 0x2000 and point <= 0x200a) or point == 0x2028 or point == 0x2029 or point == 0x202f or point == 0x205f or point == 0x3000;
}
pub fn bindRefresh(target: []const u8) !bool {
    const selected = query(target, "refresh");
    if (selected.duplicate) return error.InvalidBoolean;
    const encoded = selected.value orelse return false;
    // Optional means an absent query key. Minimal API still rejects a present
    // empty bool value, including `?refresh` and `?refresh=`.
    if (encoded.len == 0) return error.InvalidBoolean;
    var text = Decoder{ .bytes = encoded };
    var bytes: [5]u8 = undefined;
    var count: usize = 0;
    var trailing = false;
    while (text.point()) |point| {
        if (white(point)) {
            if (count != 0) trailing = true;
            continue;
        }
        if (trailing or point > 127 or count == bytes.len) return error.InvalidBoolean;
        bytes[count] = std.ascii.toLower(@intCast(point));
        count += 1;
    }
    if (std.mem.eql(u8, bytes[0..count], "true")) return true;
    if (std.mem.eql(u8, bytes[0..count], "false")) return false;
    return error.InvalidBoolean;
}
