//! C42: reads the existing state authority, owns no runtime state.
const std = @import("std");
const Allocator = std.mem.Allocator;
const Value = std.json.Value;
const Writer = std.Io.Writer;

pub const Diagnostic = struct {
    entry: ?usize = null,
    field: []const u8 = "registry",
    reason: []const u8 = "invalid declaration",
    owned_field: ?[]u8 = null,

    /// Diagnostic text survives a failed read's JSON teardown. Use the same
    /// allocator as read; the diagnostic remains valid until deinit or reuse.
    pub fn deinit(self: *Diagnostic, allocator: Allocator) void {
        if (self.owned_field) |field| allocator.free(field);
        self.* = .{};
    }

    fn fail(self: *Diagnostic, field: []const u8, reason: []const u8) error{InvalidDeclaration} {
        self.field = field;
        self.reason = reason;
        return error.InvalidDeclaration;
    }
};

pub const Registry = struct {
    parsed: std.json.Parsed(Value),
    entries: []Value,
    version: [40]u8,

    pub fn deinit(self: *Registry) void {
        self.parsed.deinit();
    }
};

pub fn read(allocator: Allocator, bytes: []const u8, diagnostic: *Diagnostic) !Registry {
    diagnostic.deinit(allocator);
    if (std.mem.startsWith(u8, bytes, "\xef\xbb\xbf")) return diagnostic.fail("registry", "UTF-8 BOM is not allowed");
    if (std.mem.indexOfScalar(u8, bytes, '\r') != null) return diagnostic.fail("registry", "CR bytes are not allowed; use LF");
    if (!std.unicode.utf8ValidateSlice(bytes)) return diagnostic.fail("registry", "invalid UTF-8");
    const parsed = std.json.parseFromSlice(Value, allocator, bytes, .{ .duplicate_field_behavior = .@"error" }) catch |err| {
        diagnostic.reason = @errorName(err);
        return error.InvalidDeclaration;
    };
    errdefer parsed.deinit();
    if (parsed.value != .object or parsed.value.object.count() != 1) return diagnostic.fail("registry", "expected exactly an entries array");
    const entries = parsed.value.object.get("entries") orelse return diagnostic.fail("entries", "missing array");
    if (entries != .array) return diagnostic.fail("entries", "expected an array");
    validate(allocator, entries.array.items, diagnostic) catch |err| {
        // Unknown property names can live in parsed's arena. Preserve the
        // actual field before errdefer destroys that arena, including decoded
        // JSON escapes; do not substitute or truncate the reported property.
        const field = allocator.dupe(u8, diagnostic.field) catch |allocation_error| {
            diagnostic.field = "registry";
            diagnostic.reason = "diagnostic allocation failed";
            return allocation_error;
        };
        diagnostic.owned_field = field;
        diagnostic.field = field;
        return err;
    };
    var hash = std.crypto.hash.Sha1.init(.{});
    var header: ["blob ".len + 20 + 1]u8 = undefined;
    hash.update(try std.fmt.bufPrint(&header, "blob {d}\x00", .{bytes.len}));
    hash.update(bytes);
    return .{ .parsed = parsed, .entries = entries.array.items, .version = std.fmt.bytesToHex(hash.finalResult(), .lower) };
}

fn get(entry: Value, field: []const u8, d: *Diagnostic) !Value {
    return entry.object.get(field) orelse return d.fail(field, "missing field");
}
fn text(value: Value, field: []const u8, d: *Diagnostic) ![]const u8 {
    if (value != .string) return d.fail(field, "expected text");
    return value.string;
}
fn nonempty(value: Value, field: []const u8, d: *Diagnostic) ![]const u8 {
    const s = try text(value, field, d);
    var it = std.unicode.Utf8View.initUnchecked(s).iterator();
    while (it.nextCodepoint()) |cp| {
        // .NET String.IsNullOrWhiteSpace Unicode White_Space contract.
        if (!(cp == 0x20 or (cp >= 9 and cp <= 13) or cp == 0x85 or cp == 0xa0 or cp == 0x1680 or (cp >= 0x2000 and cp <= 0x200a) or cp == 0x2028 or cp == 0x2029 or cp == 0x202f or cp == 0x205f or cp == 0x3000)) return s;
    }
    return d.fail(field, "expected non-empty text");
}
fn uid(value: Value, field: []const u8, d: *Diagnostic) !u32 {
    if (value != .integer or value.integer < 1 or value.integer > std.math.maxInt(u32)) return d.fail(field, "uid must be an integer from 1 to 4294967295");
    return @intCast(value.integer);
}
fn boolean(value: Value, field: []const u8, d: *Diagnostic) !bool {
    if (value != .bool) return d.fail(field, "expected boolean");
    return value.bool;
}
fn array(value: Value, field: []const u8, d: *Diagnostic) ![]Value {
    if (value != .array) return d.fail(field, "expected array");
    return value.array.items;
}
fn oneOf(s: []const u8, choices: []const []const u8) bool {
    for (choices) |choice| if (std.mem.eql(u8, s, choice)) return true;
    return false;
}
fn string(entry: Value, field: []const u8) []const u8 {
    return entry.object.get(field).?.string;
}
fn is(entry: Value, field: []const u8, expected: []const u8) bool {
    return std.mem.eql(u8, string(entry, field), expected);
}
fn identifier(s: []const u8) bool {
    if (s.len == 0 or !(std.ascii.isAlphabetic(s[0]) or s[0] == '_')) return false;
    for (s[1..]) |c| if (!(std.ascii.isAlphanumeric(c) or c == '_')) return false;
    var keywords = std.mem.tokenizeScalar(u8, "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while __arglist __makeref __reftype __refvalue", ' ');
    while (keywords.next()) |word| if (std.mem.eql(u8, s, word)) return false;
    return true;
}

// The native projection needs only bool versus host. Host type text remains
// opaque; full C# grammar validation stays in the still-used managed generator.
// Reject an obviously broken type rather than silently turning it into bool.
fn hostType(s: []const u8) bool {
    if (std.mem.trim(u8, s, " \t\n").len == 0) return false;
    var depth: usize = 0;
    var name = false;
    for (s) |c| switch (c) {
        '<', '[', '(' => {
            depth += 1;
            name = false;
        },
        '>', ']', ')' => {
            if (depth == 0) return false;
            depth -= 1;
        },
        '.', ',', ':' => name = false,
        '?', '*', ' ', '\t', '\n', '@' => {},
        else => {
            if (!(std.ascii.isAlphanumeric(c) or c == '_' or c >= 128)) return false;
            name = true;
        },
    };
    return depth == 0 and name;
}

fn reference(entries: []Value, index: *std.AutoHashMap(u32, usize), value: Value, kind: []const u8, process: []const u8, field: []const u8, d: *Diagnostic) !Value {
    const target = entries[index.get(try uid(value, field, d)) orelse return d.fail(field, "must reference an existing entry in the same process")];
    if (!is(target, "kind", kind) or !is(target, "process", process)) return d.fail(field, "must reference the expected kind in the same process");
    return target;
}

fn validate(allocator: Allocator, entries: []Value, d: *Diagnostic) !void {
    var index = std.AutoHashMap(u32, usize).init(allocator);
    defer index.deinit();
    var names = std.StringHashMap(void).init(allocator);
    defer {
        var it = names.keyIterator();
        while (it.next()) |key| allocator.free(key.*);
        names.deinit();
    }
    var previous: u32 = 0;
    for (entries, 0..) |entry, n| {
        d.entry = n;
        if (entry != .object) return d.fail("entry", "expected object");
        const id = try uid(try get(entry, "uid", d), "uid", d);
        if (index.contains(id)) return d.fail("uid", "duplicate uid");
        if (id <= previous) return d.fail("uid", "entries must be sorted by uid");
        previous = id;
        try index.put(id, n);
        const kind = try text(try get(entry, "kind", d), "kind", d);
        const process = try text(try get(entry, "process", d), "process", d);
        if (!oneOf(kind, &.{ "block", "item", "condition" })) return d.fail("kind", "expected block, item or condition");
        if (!oneOf(process, &.{ "backend", "ui", "desktop", "window" })) return d.fail("process", "unknown process");
        const name = try text(try get(entry, "name", d), "name", d);
        if (!identifier(name)) return d.fail("name", "expected ASCII C# identifier that is not a keyword");
        const key = try std.fmt.allocPrint(allocator, "{s}:{s}", .{ process, name });
        if (names.contains(key)) {
            allocator.free(key);
            return d.fail("name", "duplicate name within process");
        }
        try names.put(key, {});
        _ = try nonempty(try get(entry, "comment", d), "comment", d);
        const item = std.mem.eql(u8, kind, "item");
        const condition = std.mem.eql(u8, kind, "condition");
        if (item) {
            const storage = try text(try get(entry, "storage", d), "storage", d);
            if (!oneOf(storage, &.{ "own", "reference" })) return d.fail("storage", "expected own or reference");
            if (!hostType(try text(try get(entry, "type", d), "type", d))) return d.fail("type", "invalid host type label");
            _ = try boolean(try get(entry, "perBlock", d), "perBlock", d);
            const trigger = try text(try get(entry, "trigger", d), "trigger", d);
            if (!oneOf(trigger, &.{ "event", "poll" })) return d.fail("trigger", "expected event or poll");
            const field: []const u8 = if (std.mem.eql(u8, trigger, "event")) "changeEvent" else "pollInterval";
            _ = try nonempty(try get(entry, field, d), field, d);
            const owner = try get(entry, "owner", d);
            if (owner != .object or owner.object.count() != 1) return d.fail("owner", "expected exactly block or component");
            if (owner.object.get("block")) |block| {
                _ = try uid(block, "owner.block", d);
            } else if (owner.object.get("component")) |component| {
                _ = try nonempty(component, "owner.component", d);
            } else return d.fail("owner", "expected exactly block or component");
        }
        if (condition) {
            const per_block = try boolean(try get(entry, "perBlock", d), "perBlock", d);
            const items = try array(try get(entry, "items", d), "items", d);
            const conditions = try array(try get(entry, "conditions", d), "conditions", d);
            const children = try array(try get(entry, "children", d), "children", d);
            if (per_block) {
                if (entry.object.contains("node") or entry.object.contains("decides") or conditions.len != 0 or children.len != 0) return d.fail("perBlock", "per-block condition reads items only and omits node/decides");
            } else _ = try uid(try get(entry, "node", d), "node", d);
            if (entry.object.get("decides")) |v| {
                _ = try uid(v, "decides", d);
            }
            for (items) |r| {
                if (r != .object or !r.object.contains("item")) return d.fail("items", "expected item and optional block");
                var fields = r.object.iterator();
                while (fields.next()) |f| {
                    if (!oneOf(f.key_ptr.*, &.{ "item", "block" })) return d.fail("items", "unknown item read field");
                    _ = try uid(f.value_ptr.*, "items", d);
                }
            }
            for (conditions) |v| {
                _ = try uid(v, "conditions", d);
            }
            for (children) |v| {
                _ = try uid(v, "children", d);
            }
        }
        var fields = entry.object.iterator();
        while (fields.next()) |f| {
            const field = f.key_ptr.*;
            if (oneOf(field, &.{ "uid", "kind", "process", "name", "comment" })) continue;
            if (item and oneOf(field, &.{ "storage", "type", "owner", "perBlock", "trigger" })) continue;
            if (item and std.mem.eql(u8, field, if (is(entry, "trigger", "event")) "changeEvent" else "pollInterval")) continue;
            if (condition and oneOf(field, &.{ "node", "perBlock", "items", "conditions", "children", "decides" })) continue;
            return d.fail(field, "unknown field for this entry kind");
        }
    }
    for (entries, 0..) |entry, n| {
        d.entry = n;
        const process = string(entry, "process");
        if (is(entry, "kind", "item")) {
            if (entry.object.get("owner").?.object.get("block")) |v| {
                _ = try reference(entries, &index, v, "block", process, "owner.block", d);
            }
        } else if (is(entry, "kind", "condition")) {
            for ([_][]const u8{ "node", "decides" }) |field| if (entry.object.get(field)) |v| {
                _ = try reference(entries, &index, v, "block", process, field, d);
            };
            for (entry.object.get("items").?.array.items) |r| {
                const target = try reference(entries, &index, r.object.get("item").?, "item", process, "items.item", d);
                if (r.object.get("block")) |v| {
                    _ = try reference(entries, &index, v, "block", process, "items.block", d);
                    if (!target.object.get("perBlock").?.bool) return d.fail("items.block", "allowed only for per-block item");
                } else if (target.object.get("perBlock").?.bool and !entry.object.get("perBlock").?.bool) return d.fail("items.block", "per-block item read requires block");
            }
            for (entry.object.get("children").?.array.items) |v| {
                _ = try reference(entries, &index, v, "block", process, "children", d);
            }
            for (entry.object.get("conditions").?.array.items) |v| {
                const lower = try reference(entries, &index, v, "condition", process, "conditions", d);
                if (lower.object.get("perBlock").?.bool or lower.object.get("node").?.integer != entry.object.get("node").?.integer) return d.fail("conditions", "lower conditions must belong to the same node");
            }
        }
    }
    // Bounded iterative three-colour DFS; no recursion or per-edge allocation.
    const colours = try allocator.alloc(u2, entries.len);
    defer allocator.free(colours);
    @memset(colours, 0);
    const Frame = struct { index: usize, edge: usize = 0 };
    const stack = try allocator.alloc(Frame, entries.len);
    defer allocator.free(stack);
    for (entries, 0..) |entry, n| {
        if (!is(entry, "kind", "condition") or colours[n] != 0) continue;
        var count: usize = 1;
        stack[0] = .{ .index = n };
        colours[n] = 1;
        while (count != 0) {
            const frame = &stack[count - 1];
            const edges = entries[frame.index].object.get("conditions").?.array.items;
            if (frame.edge == edges.len) {
                colours[frame.index] = 2;
                count -= 1;
                continue;
            }
            const target = index.get(@intCast(edges[frame.edge].integer)).?;
            frame.edge += 1;
            if (colours[target] == 1) {
                d.entry = frame.index;
                return d.fail("conditions", "condition cycle");
            }
            if (colours[target] == 0) {
                colours[target] = 1;
                stack[count] = .{ .index = target };
                count += 1;
            }
        }
    }
    d.entry = null;
}

fn literal(w: *Writer, s: []const u8) !void {
    try w.writeByte('"');
    for (s) |c| switch (c) {
        '"' => try w.writeAll("\\\""),
        '\\' => try w.writeAll("\\\\"),
        32...33, 35...91, 93...126 => try w.writeByte(c),
        else => try w.print("\\x{x:0>2}", .{c}),
    };
    try w.writeByte('"');
}
fn emitName(w: *Writer, entry: Value, names: bool) !void {
    if (names) try literal(w, string(entry, "name")) else try w.writeAll("null");
}
fn optionalUid(w: *Writer, entry: Value, field: []const u8) !void {
    if (entry.object.get(field)) |v| try w.print("{d}", .{v.integer}) else try w.writeAll("null");
}
fn uids(w: *Writer, values: []Value) !void {
    for (values, 0..) |v, i| {
        if (i != 0) try w.writeAll(", ");
        try w.print("{d}", .{v.integer});
    }
}

pub fn emit(registry: Registry, process: []const u8, names: bool, w: *Writer) !void {
    if (!oneOf(process, &.{ "backend", "ui", "desktop", "window" })) return error.InvalidProcess;
    // Preserve bytes (including the historical provenance line) for old/new oracle comparison.
    try w.writeAll("// Generated by build/scripts/build/Generate-StateItems.ps1. Do not edit.\nconst declarations = @import(\"rm_control\").declarations;\n\npub const registry: declarations.Registry = .{\n    .version = ");
    try literal(w, &registry.version);
    try w.writeAll(",\n    .process = ");
    try literal(w, process);
    try w.writeAll(",\n    .blocks = &.{\n");
    for (registry.entries) |entry| {
        if (!is(entry, "process", process) or !is(entry, "kind", "block")) continue;
        try w.print("        .{{ .uid = {d}, .name = ", .{entry.object.get("uid").?.integer});
        try emitName(w, entry, names);
        try w.writeAll(" },\n");
    }
    try w.writeAll("    },\n    .items = &.{\n");
    for (registry.entries) |entry| {
        if (!is(entry, "process", process) or !is(entry, "kind", "item")) continue;
        try w.print("        .{{ .uid = {d}, .storage = .{s}, .value_type = .{s}, .per_block = {s}, .trigger = .{s}, .owner_block = ", .{ entry.object.get("uid").?.integer, string(entry, "storage"), if (is(entry, "type", "bool")) @as([]const u8, "bool") else "host", if (entry.object.get("perBlock").?.bool) @as([]const u8, "true") else "false", string(entry, "trigger") });
        try optionalUid(w, entry.object.get("owner").?, "block");
        try w.writeAll(", .name = ");
        try emitName(w, entry, names);
        try w.writeAll(" },\n");
    }
    try w.writeAll("    },\n    .conditions = &.{\n");
    for (registry.entries) |entry| {
        if (!is(entry, "process", process) or !is(entry, "kind", "condition")) continue;
        try w.print("        .{{ .uid = {d}, .node = ", .{entry.object.get("uid").?.integer});
        try optionalUid(w, entry, "node");
        try w.print(", .per_block = {s},\n            .items = &.{{\n", .{if (entry.object.get("perBlock").?.bool) @as([]const u8, "true") else "false"});
        for (entry.object.get("items").?.array.items) |r| {
            try w.print("                .{{ .item = {d}, .block = ", .{r.object.get("item").?.integer});
            try optionalUid(w, r, "block");
            try w.writeAll(" },\n");
        }
        try w.writeAll("            },\n            .conditions = &.{ ");
        try uids(w, entry.object.get("conditions").?.array.items);
        try w.writeAll(" }, .children = &.{ ");
        try uids(w, entry.object.get("children").?.array.items);
        try w.writeAll(" }, .decides = ");
        try optionalUid(w, entry, "decides");
        try w.writeAll(", .name = ");
        try emitName(w, entry, names);
        try w.writeAll(" },\n");
    }
    try w.writeAll("    },\n};\n");
    for ([_][]const u8{ "block", "item", "condition" }, [_][]const u8{ "BlockUids", "ItemUids", "ConditionUids" }) |kind, class| {
        try w.print("\npub const {s} = struct {{\n", .{class});
        for (registry.entries) |entry| {
            if (!is(entry, "process", process) or !is(entry, "kind", kind)) continue;
            try w.print("    pub const @\"{s}\" = {d};\n", .{ string(entry, "name"), entry.object.get("uid").?.integer });
        }
        try w.writeAll("};\n");
    }
}
