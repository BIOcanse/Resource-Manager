//! Z-hook-wait: one typed result -> one HTTP body. No hook state or send gate.
//! All callbacks and UTF-16 borrows are synchronous. Only the final output
//! segments survive this call, under the HTTP host's existing ownership.
const std = @import("std");
const json = @import("crash_reports.zig");
pub const Writer = json.Writer;
pub const OutputCallbacks = json.OutputCallbacks;
pub const DateParts = json.DateParts;
pub const Code = json.Code;

// ABI discriminators, not product defaults. Unknown managed types are ignored.
pub const Kind = enum(u32) { ignored, rejection, metric, event };
pub const Input = extern struct {
    kind: u32,
    rejection_status: u32,
    state: ?[*]const u16,
    state_length: usize,
};
pub const Response = extern struct { selected: u32, status: u32, rejection: u32 };
pub const Node = enum(u32) {
    root,
    hook,
    rule,
    metric,
    target,
    owner,
    event_value,
    event,
    action,
    selection,
    tree,
    status,
    reserve,
    entry,
    commit,
    exclusion,
    rejection,
    dump_path,
    protected_process,
};
pub const Query = enum(u32) {
    rule_present,
    interval_present,
    event_present,
    rule_software_present,
    rule_process_present,
    rule_start_present,
    rule_device_present,
    rejection_position_present,
    rejection_reason_present,
    owner_count,
    action_present,
    tree_present,
    dump_count,
    commit_present,
    entry_count,
    exclusion_count,
    protected_count,
};
pub const Callbacks = extern struct {
    field: *const fn (?*anyopaque, u32, usize, u32, *Writer) callconv(.c) i32,
    shape: *const fn (?*anyopaque, u32, *usize) callconv(.c) i32,
};

fn stateIs(input: Input, comptime text: []const u8) bool {
    if (input.state_length != text.len) return false;
    const state = input.state orelse return false;
    var i: usize = 0;
    while (i < text.len) : (i += 1) if (state[i] != text[i]) return false;
    return true;
}

pub fn select(input: ?*const Input, response: ?*Response) i32 {
    const source = input orelse return Code.invalid;
    const result = response orelse return Code.invalid;
    result.* = .{ .selected = 0, .status = 0, .rejection = 0 };
    if (source.state == null and source.state_length != 0) return Code.invalid;
    switch (source.kind) {
        @backingInt(Kind.ignored) => {},
        @backingInt(Kind.rejection) => result.* = .{ .selected = 1, .status = source.rejection_status, .rejection = 1 },
        @backingInt(Kind.metric) => {
            if (stateIs(source.*, "met")) result.* = .{ .selected = 1, .status = 200, .rejection = 0 } else if (stateIs(source.*, "objectEnded")) result.* = .{ .selected = 1, .status = 409, .rejection = 0 };
        },
        @backingInt(Kind.event) => result.* = .{ .selected = 1, .status = 200, .rejection = 0 },
        else => return Code.invalid,
    }
    return Code.success;
}

// Reuse Z1's writer layout and string/date encoder without changing its shared
// source or public registration. This append operates on the same final segment.
fn raw(w: *Writer, bytes: []const u8) void {
    if (w.failure != Code.success) return;
    const sum = @addWithOverflow(w.length, @as(u64, @intCast(bytes.len)));
    if (sum[1] != 0) {
        w.failure = Code.overflow;
        return;
    }
    const output = w.output_callbacks orelse {
        w.failure = Code.invalid;
        return;
    };
    var offset: usize = 0;
    while (offset < bytes.len) {
        if (w.used == w.capacity) {
            var next: ?[*]u8 = null;
            var capacity: usize = 0;
            if (output.reserve(w.context, w.used, &next, &capacity) != 0 or next == null or capacity == 0) {
                w.failure = Code.callback;
                return;
            }
            w.buffer = next;
            w.capacity = capacity;
            w.used = 0;
        }
        const count = @min(bytes.len - offset, w.capacity - w.used);
        @memcpy(w.buffer.?[w.used..][0..count], bytes[offset..][0..count]);
        w.used += count;
        offset += count;
        w.length += count;
    }
}

pub fn writeString(w: ?*Writer, text: ?[*]const u16, length: usize) i32 {
    return json.writeString(w, text, length);
}
pub fn writeDateTime(w: ?*Writer, date: ?*const DateParts) i32 {
    return json.writeDateTime(w, date);
}
pub fn writeNull(writer: ?*Writer) i32 {
    const w = writer orelse return Code.invalid;
    raw(w, "null");
    return w.failure;
}
pub fn writeBool(writer: ?*Writer, value: u32) i32 {
    const w = writer orelse return Code.invalid;
    if (value > 1) return Code.invalid;
    raw(w, if (value == 1) "true" else "false");
    return w.failure;
}
pub fn writeI64(writer: ?*Writer, value: i64) i32 {
    const w = writer orelse return Code.invalid;
    var buffer: [21]u8 = undefined;
    raw(w, std.fmt.bufPrint(&buffer, "{d}", .{value}) catch return Code.invalid);
    return w.failure;
}
pub fn writeU64(writer: ?*Writer, value: u64) i32 {
    const w = writer orelse return Code.invalid;
    var buffer: [20]u8 = undefined;
    raw(w, std.fmt.bufPrint(&buffer, "{d}", .{value}) catch return Code.invalid);
    return w.failure;
}
pub fn writeActionState(writer: ?*Writer, value: i32) i32 {
    const w = writer orelse return Code.invalid;
    // JsonStringEnumConverter<HostMemoryActionRecordState> names History=0;
    // its original allowIntegerValues behavior also covers unknown enum values.
    if (value == 0) raw(w, "\"history\"") else return writeI64(w, value);
    return w.failure;
}

// System.Text.Json's finite Double general format: shortest round trip,
// fixed notation for decimal exponents [-4,17), otherwise E with sign and at
// least two exponent digits. These are wire-format rules, not numeric defaults.
pub fn writeF64(writer: ?*Writer, value: f64) i32 {
    const w = writer orelse return Code.invalid;
    if (!std.math.isFinite(value)) return Code.invalid;
    var buffer: [std.fmt.float.min_buffer_size]u8 = undefined;
    const scientific = std.fmt.float.render(&buffer, value, .{ .mode = .scientific }) catch return Code.invalid;
    const separator = std.mem.indexOfScalar(u8, scientific, 'e') orelse return Code.invalid;
    const exponent = std.fmt.parseInt(i32, scientific[separator + 1 ..], 10) catch return Code.invalid;
    if (exponent >= -4 and exponent < 17) {
        raw(w, std.fmt.float.render(&buffer, value, .{ .mode = .decimal }) catch return Code.invalid);
    } else {
        raw(w, scientific[0..separator]);
        raw(w, if (exponent < 0) "E-" else "E+");
        const magnitude: u32 = @intCast(if (exponent < 0) -exponent else exponent);
        if (magnitude < 10) raw(w, "0");
        _ = writeU64(w, magnitude);
    }
    return w.failure;
}

// Decimal's original 96-bit integer and scale arrive directly from GetBits;
// no double conversion, JSON token, string buffer or object graph crosses ABI.
pub fn writeDecimal(writer: ?*Writer, low: u32, middle: u32, high: u32, flags: u32) i32 {
    const w = writer orelse return Code.invalid;
    const scale = (flags >> 16) & 0xff;
    if (scale > 28 or flags & 0x7f00ffff != 0) return Code.invalid;
    const magnitude: u128 = @as(u128, low) | (@as(u128, middle) << 32) | (@as(u128, high) << 64);
    var buffer: [29]u8 = undefined;
    const digits = std.fmt.bufPrint(&buffer, "{d}", .{magnitude}) catch return Code.invalid;
    if (flags >> 31 != 0 and magnitude != 0) raw(w, "-");
    if (scale == 0) raw(w, digits) else if (digits.len > scale) {
        raw(w, digits[0 .. digits.len - scale]);
        raw(w, ".");
        raw(w, digits[digits.len - scale ..]);
    } else {
        raw(w, "0.");
        var padding = scale - digits.len;
        while (padding > 0) : (padding -= 1) raw(w, "0");
        raw(w, digits);
    }
    return w.failure;
}

const EncodeError = error{Callback};
const Encoder = struct {
    w: *Writer,
    context: ?*anyopaque,
    callbacks: *const Callbacks,
    fn field(e: Encoder, node: Node, index: usize, field_id: u32) EncodeError!void {
        if (e.w.failure != Code.success or e.callbacks.field(e.context, @backingInt(node), index, field_id, e.w) != 0) return error.Callback;
    }
    fn query(e: Encoder, q: Query) EncodeError!usize {
        if (e.w.failure != Code.success) return error.Callback;
        var value: usize = 0;
        if (e.callbacks.shape(e.context, @backingInt(q), &value) != 0) return error.Callback;
        return value;
    }
    fn property(e: Encoder, node: Node, index: usize, field_id: u32, comptime name: []const u8) EncodeError!void {
        raw(e.w, "\"" ++ name ++ "\":");
        try e.field(node, index, field_id);
    }
    fn properties(e: Encoder, node: Node, index: usize, comptime names: []const []const u8) EncodeError!void {
        inline for (names, 0..) |name, id| {
            if (id != 0) raw(e.w, ",");
            try e.property(node, index, id, name);
        }
    }
    fn optional(e: Encoder, node: Node, id: u32, comptime name: []const u8, q: Query) EncodeError!void {
        if (try e.query(q) != 0) {
            raw(e.w, ",");
            try e.property(node, 0, id, name);
        }
    }
    fn hook(e: Encoder) EncodeError!void {
        raw(e.w, "{");
        try e.property(.hook, 0, 0, "id");
        try e.optional(.hook, 1, "intervalMs", .interval_present);
        if (try e.query(.rule_present) != 0) {
            raw(e.w, ",\"rule\":{");
            try e.properties(.rule, 0, &.{ "binding", "metricId", "quantity", "comparison", "threshold", "holdSeconds", "topOwners" });
            try e.optional(.rule, 7, "softwareId", .rule_software_present);
            try e.optional(.rule, 8, "processId", .rule_process_present);
            try e.optional(.rule, 9, "startKey", .rule_start_present);
            try e.optional(.rule, 10, "deviceIdentity", .rule_device_present);
            raw(e.w, "}");
        }
        try e.optional(.hook, 2, "event", .event_present);
        raw(e.w, "}");
    }
    fn metric(e: Encoder) EncodeError!void {
        raw(e.w, "{");
        try e.property(.metric, 0, 0, "state");
        raw(e.w, ",\"target\":{");
        try e.properties(.target, 0, &.{ "binding", "metricId", "softwareId", "processId", "startKey", "deviceIdentity" });
        raw(e.w, "}");
        inline for (.{ "quantity", "reading", "unit", "threshold", "since", "reason" }, 1..) |name, id| {
            raw(e.w, ",");
            try e.property(.metric, 0, id, name);
        }
        raw(e.w, ",\"owners\":[");
        const count = try e.query(.owner_count);
        var i: usize = 0;
        while (i < count) : (i += 1) {
            if (i != 0) raw(e.w, ",");
            raw(e.w, "{");
            try e.properties(.owner, i, &.{ "name", "processId", "reading" });
            raw(e.w, "}");
        }
        raw(e.w, "]}");
    }
    fn action(e: Encoder) EncodeError!void {
        if (try e.query(.action_present) == 0) {
            raw(e.w, "null");
            return;
        }
        raw(e.w, "{");
        try e.properties(.action, 0, &.{ "at", "processId", "processStartKey", "name", "baselineBytes", "currentBytes", "growthBytes" });
        raw(e.w, ",\"dumpPaths\":[");
        const count = try e.query(.dump_count);
        var i: usize = 0;
        while (i < count) : (i += 1) {
            if (i != 0) raw(e.w, ",");
            try e.field(.dump_path, i, 0);
        }
        raw(e.w, "]");
        inline for (.{ "bytesWritten", "outcome", "reason" }, 7..) |name, id| {
            raw(e.w, ",");
            try e.property(.action, 0, id, name);
        }
        raw(e.w, ",\"selectionBasis\":{");
        try e.properties(.selection, 0, &.{ "rule", "processGrowthBytes", "directChildrenMemoryGrowthBytes", "minimumProcessGrowthBytes" });
        raw(e.w, "},");
        try e.property(.action, 0, 10, "actionId");
        raw(e.w, ",");
        try e.property(.action, 0, 11, "targetKind");
        raw(e.w, ",\"treeRoot\":");
        if (try e.query(.tree_present) == 0) raw(e.w, "null") else {
            raw(e.w, "{");
            try e.properties(.tree, 0, &.{ "processId", "processStartKey", "name" });
            raw(e.w, "}");
        }
        raw(e.w, ",");
        try e.property(.action, 0, 12, "detail");
        raw(e.w, ",");
        try e.property(.action, 0, 13, "state");
        raw(e.w, "}");
    }
    fn status(e: Encoder) EncodeError!void {
        raw(e.w, "{");
        try e.properties(.status, 0, &.{ "enabled", "thresholdPercent", "checkIntervalSeconds" });
        raw(e.w, ",\"reserve\":{\"entries\":[");
        const entries = try e.query(.entry_count);
        var i: usize = 0;
        while (i < entries) : (i += 1) {
            if (i != 0) raw(e.w, ",");
            raw(e.w, "{");
            try e.properties(.entry, i, &.{ "location", "configuredSizeBytes", "built", "reason", "remainingBytes", "clusterBytes" });
            raw(e.w, "}");
        }
        raw(e.w, "],");
        try e.properties(.reserve, 0, &.{ "configuredSizeBytes", "built", "reason", "remainingBytes" });
        raw(e.w, "}");
        raw(e.w, ",");
        try e.property(.status, 0, 3, "triggerBytes");
        raw(e.w, ",");
        try e.property(.status, 0, 4, "remainingBytes");
        raw(e.w, ",\"currentCommit\":");
        if (try e.query(.commit_present) == 0) raw(e.w, "null") else {
            raw(e.w, "{");
            try e.properties(.commit, 0, &.{ "currentBytes", "limitBytes", "percent" });
            raw(e.w, "}");
        }
        raw(e.w, ",");
        try e.property(.status, 0, 5, "inactiveReason");
        raw(e.w, ",\"excludedCandidates\":[");
        const exclusions = try e.query(.exclusion_count);
        i = 0;
        while (i < exclusions) : (i += 1) {
            if (i != 0) raw(e.w, ",");
            raw(e.w, "{");
            try e.properties(.exclusion, i, &.{ "processId", "processStartKey", "name", "reason" });
            raw(e.w, "}");
        }
        raw(e.w, "]");
        inline for (.{ "actionMode", "active", "atOrAboveThreshold", "currentActionId", "minimumProcessGrowthBytes" }, 6..) |name, id| {
            raw(e.w, ",");
            try e.property(.status, 0, id, name);
        }
        raw(e.w, ",\"protectedProcesses\":[");
        const processes = try e.query(.protected_count);
        i = 0;
        while (i < processes) : (i += 1) {
            if (i != 0) raw(e.w, ",");
            try e.field(.protected_process, i, 0);
        }
        raw(e.w, "],");
        try e.property(.status, 0, 11, "decisionReason");
        raw(e.w, "}");
    }
    fn event(e: Encoder) EncodeError!void {
        raw(e.w, "{");
        try e.property(.event_value, 0, 0, "state");
        raw(e.w, ",\"event\":{");
        try e.properties(.event, 0, &.{ "kind", "reason" });
        raw(e.w, ",\"action\":");
        try e.action();
        raw(e.w, "},\"status\":");
        try e.status();
        raw(e.w, "}");
    }
    fn body(e: Encoder, kind: u32) EncodeError!void {
        if (kind == @backingInt(Kind.rejection)) {
            raw(e.w, "{");
            try e.properties(.rejection, 0, &.{ "statusCode", "error" });
            try e.optional(.rejection, 2, "position", .rejection_position_present);
            try e.optional(.rejection, 3, "reason", .rejection_reason_present);
            raw(e.w, "}");
            return;
        }
        raw(e.w, "{");
        try e.property(.root, 0, 0, "hookId");
        raw(e.w, ",\"hook\":");
        try e.hook();
        raw(e.w, ",\"value\":");
        if (kind == @backingInt(Kind.metric)) try e.metric() else try e.event();
        raw(e.w, "}");
    }
};

pub fn encodeStream(input: ?*const Input, context: ?*anyopaque, callbacks: ?*const Callbacks, output: ?*const OutputCallbacks, response: ?*Response) i32 {
    const code = select(input, response);
    if (code != Code.success or response.?.selected == 0) return code;
    const source = callbacks orelse return Code.invalid;
    const target = output orelse return Code.invalid;
    var writer: Writer = .{ .buffer = null, .capacity = 0, .output_callbacks = target, .context = context };
    const encoder: Encoder = .{ .w = &writer, .context = context, .callbacks = source };
    encoder.body(input.?.kind) catch {
        return if (writer.failure == Code.success) Code.callback else writer.failure;
    };
    if (writer.failure != Code.success) return writer.failure;
    return if (target.commit(context, writer.used) == 0) Code.success else Code.callback;
}
