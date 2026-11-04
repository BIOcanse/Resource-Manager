// Transitional ABI for existing NativeAccessPolicy.cs HTTP/pipe consumers.
// Original implementation: 86d71f4f, access.zig (unchanged below).
// The approved two-level policy stays in access.zig/access_abi.zig. These old
// exports are not its permission oracle. P-access/H-common/P-stream delete this
// file when the last legacy managed caller and credential routes are removed.
// H0 needs the actual old listener operational to capture its historical facts.
const std = @import("std");

// P-access owns policy, never credentials or caller evidence. The source borrows
// the original immutable scope lists synchronously; no payload is allocated here.
pub const List = enum(u32) { required, granted, administrator_holder, keyless_local };
pub const TextSink = struct {
    context: *anyopaque,
    consume: *const fn (*anyopaque, []const u16) anyerror!void,
};
pub const Source = extern struct {
    context: ?*anyopaque,
    read: *const fn (?*anyopaque, u32, usize, *TextSink) callconv(.c) c_int,
    counts: [4]usize,

    fn visit(self: Source, list: List, index: usize, sink: *TextSink) !void {
        if (self.read(self.context, @backingInt(list), index, sink) != 0) return error.SourceFailed;
    }
};

pub const Credential = enum(u32) { absent, malformed, pending, invalid, unavailable, valid };
pub const Fact = enum(u32) { unknown, no, yes };
pub const Keyless = enum(u32) { administrator_only, local_windows };
pub const GpuOperation = enum(u32) { invalid, resolve, report };

pub fn gpuOperation(operation: []const u16, payload_length: usize, maximum_payload: usize) GpuOperation {
    if (payload_length > maximum_payload) return .invalid;
    if (std.mem.eql(u16, operation, std.unicode.utf8ToUtf16LeStringLiteral("gpu.resolve"))) return .resolve;
    if (std.mem.eql(u16, operation, std.unicode.utf8ToUtf16LeStringLiteral("gpu.report"))) return .report;
    return .invalid;
}
pub const Outcome = enum(u32) {
    allow,
    authenticate,
    attest_administrator,
    attest_local_caller,
    keyless_required,
    invalid_key,
    service_unavailable,
    administrator_required,
    scope_required,
    local_caller_required,
    key_required,
};
// Purpose is supplied to the original key owner by the binding. It is never
// inferred from an operation (in particular, future pipe operations need not use Control).
pub const Input = extern struct {
    credential: Credential,
    administrator: Fact,
    local_caller: Fact,
    keyless: Keyless,
    require_keyless: u32,
};
pub const Decision = extern struct {
    outcome: Outcome,
    administrator: u32 = 0,
    local_caller: u32 = 0,
};

const Match = struct {
    expected: []const u16,
    equal: bool = false,
    fn consume(raw: *anyopaque, text: []const u16) !void {
        const self: *Match = @ptrCast(@alignCast(raw));
        self.equal = std.mem.eql(u16, self.expected, text);
    }
};

pub fn contains(source: Source, list: List, text: []const u16) !bool {
    var index: usize = 0;
    while (index < source.counts[@backingInt(list)]) : (index += 1) {
        var match: Match = .{ .expected = text };
        var sink: TextSink = .{ .context = &match, .consume = Match.consume };
        try source.visit(list, index, &sink);
        if (match.equal) return true;
    }
    return false;
}

const Membership = struct {
    source: Source,
    target: List,
    found: bool = false,
    fn consume(raw: *anyopaque, text: []const u16) !void {
        const self: *Membership = @ptrCast(@alignCast(raw));
        self.found = try contains(self.source, self.target, text);
    }
};

fn membership(source: Source, list: List, index: usize, target: List) !bool {
    var query: Membership = .{ .source = source, .target = target };
    var sink: TextSink = .{ .context = &query, .consume = Membership.consume };
    try source.visit(list, index, &sink);
    return query.found;
}

pub fn subset(source: Source, list: List, target: List) !bool {
    var index: usize = 0;
    while (index < source.counts[@backingInt(list)]) : (index += 1) {
        if (!try membership(source, list, index, target)) return false;
    }
    return true;
}

fn requiresAdministrator(source: Source, list: List) !bool {
    var index: usize = 0;
    while (index < source.counts[@backingInt(list)]) : (index += 1) {
        if (try membership(source, list, index, .administrator_holder)) return true;
    }
    return false;
}

pub fn evaluate(input: Input, source: Source) !Decision {
    if (input.require_keyless != 0 and input.credential != .absent) return .{ .outcome = .keyless_required };
    switch (input.credential) {
        .malformed, .invalid => return .{ .outcome = .invalid_key },
        .pending => return .{ .outcome = .authenticate },
        .unavailable => return .{ .outcome = .service_unavailable },
        .valid => {
            const holder = try requiresAdministrator(source, .granted);
            if (holder and input.administrator == .unknown) return .{ .outcome = .attest_administrator };
            if (holder and input.administrator != .yes) return .{ .outcome = .administrator_required };
            if (!try subset(source, .required, .granted)) return .{ .outcome = .scope_required };
            return .{ .outcome = .allow, .administrator = @intFromBool(holder and try requiresAdministrator(source, .required)) };
        },
        .absent => {
            if (input.administrator == .unknown) return .{ .outcome = .attest_administrator };
            if (input.administrator == .yes) return .{ .outcome = .allow, .administrator = 1 };
            if (input.keyless == .administrator_only) return .{ .outcome = .key_required };
            if (input.local_caller == .unknown) return .{ .outcome = .attest_local_caller };
            if (input.local_caller == .no) return .{ .outcome = .local_caller_required };
            return .{ .outcome = .allow, .local_caller = 1 };
        },
    }
}

// HTTP can forbid local admission for this operation yet still distinguish an
// observed local caller (403) from a caller without evidence (401).
pub fn evaluateHttp(input: Input, source: Source, allow_local: bool) !Decision {
    const decision = try evaluate(input, source);
    if (!allow_local and decision.local_caller != 0) return .{ .outcome = .key_required };
    return decision;
}

pub export fn rm_access_text(sink: *TextSink, pointer: ?[*]const u16, length: usize) callconv(.c) c_int {
    if (pointer == null and length != 0) return 1;
    sink.consume(sink.context, if (pointer) |p| p[0..length] else &.{}) catch return 2;
    return 0;
}

pub export fn rm_access_evaluate(input: *const Input, source: *const Source, allow_local: u32, output: *Decision) callconv(.c) c_int {
    output.* = evaluateHttp(input.*, source.*, allow_local != 0) catch return 2;
    return 0;
}

// Also used for keyless selector eligibility, notification filtering and the
// missing-scope result. Only references and scalar answers cross this boundary.
pub export fn rm_access_subset(source: *const Source, list: u32, target: u32) callconv(.c) c_int {
    const a = std.enums.fromInt(List, list) orelse return -1;
    const b = std.enums.fromInt(List, target) orelse return -1;
    return @intFromBool(subset(source.*, a, b) catch return -1);
}

pub export fn rm_access_missing(source: *const Source, index: usize) callconv(.c) c_int {
    if (index >= source.counts[0]) return -1;
    return @intFromBool(!(membership(source.*, .required, index, .granted) catch return -1));
}

pub export fn rm_access_contains(source: *const Source, list: u32, pointer: ?[*]const u16, length: usize) callconv(.c) c_int {
    const selected = std.enums.fromInt(List, list) orelse return -1;
    if (pointer == null and length != 0) return -1;
    return @intFromBool(contains(source.*, selected, if (pointer) |p| p[0..length] else &.{}) catch return -1);
}

pub export fn rm_access_gpu_operation(pointer: ?[*]const u16, length: usize, payload_length: usize, maximum_payload: usize) callconv(.c) GpuOperation {
    if (pointer == null and length != 0) return .invalid;
    return gpuOperation(if (pointer) |p| p[0..length] else &.{}, payload_length, maximum_payload);
}
