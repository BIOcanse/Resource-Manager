//! Converts CLI syntax using the service's schema. Product defaults stay in the service.
const std = @import("std");
const Allocator = std.mem.Allocator;
const Value = std.json.Value;
const Parameter = @import("rm_cli_arguments").Parameter;
const List = std.array_list.Managed;

pub const Payload = struct {
    arena: std.heap.ArenaAllocator,
    value: Value,

    pub fn deinit(self: *Payload) void {
        self.arena.deinit();
        self.* = undefined;
    }
};

const Kind = enum { string, integer, number, boolean, object, array, null };
const Type = struct { kind: Kind, nullable: bool = false };
const Pair = struct { schema: *const Value, value: *const Value };
const parse_options: std.json.ParseOptions = .{
    .allocate = .alloc_always,
    .parse_numbers = false,
    .duplicate_field_behavior = .@"error",
};

pub fn fromParameters(allocator: Allocator, schema: Value, parameters: []const Parameter) !Payload {
    var result: Payload = .{ .arena = std.heap.ArenaAllocator.init(allocator), .value = .null };
    errdefer result.deinit();
    var scratch = std.heap.ArenaAllocator.init(allocator);
    defer scratch.deinit();
    const temporary = scratch.allocator();
    const schema_nodes = checkSchema(temporary, &schema) catch |err| {
        if (unsupported(err)) return error.InputFileRequired;
        return err;
    };
    if (schema == .null or (try schemaType(schema)).kind == .null) {
        if (parameters.len != 0) return error.UnexpectedParameters;
        try checkValue(temporary, &schema, &result.value, schema_nodes);
        return result;
    }
    if ((try schemaType(schema)).kind != .object) return error.InputFileRequired;

    const owned = result.arena.allocator();
    result.value = .{ .object = .empty };
    const properties = schema.object.get("properties");
    const additional = schema.object.get("additionalProperties");
    var index: usize = 0;
    while (index < parameters.len) : (index += 1) {
        const parameter = parameters[index];
        if (!std.unicode.utf8ValidateSlice(parameter.name)) return error.InvalidUtf8;
        if (result.value.object.contains(parameter.name)) return error.DuplicateParameter;
        const property_schema = if (properties) |p| p.object.get(parameter.name) else null;
        const field_schema = property_schema orelse blk: {
            if (additional) |a| {
                if (a == .object) break :blk a;
                if (!a.bool) return error.UnknownParameter;
            }
            // Unconstrained properties have no authoritative CLI text conversion.
            return error.InputFileRequired;
        };
        const value = try parameterValue(owned, field_schema, parameter.value);
        try result.value.object.put(owned, try owned.dupe(u8, parameter.name), value);
    }
    try checkValue(temporary, &schema, &result.value, schema_nodes);
    return result;
}

pub fn fromJson(allocator: Allocator, schema: Value, bytes: []const u8) !Payload {
    // JSON already has explicit types. The service alone interprets its schema;
    // files can express capabilities from any current or future schema dialect.
    _ = schema;
    if (!std.unicode.utf8ValidateSlice(bytes)) return error.InvalidUtf8;
    const parsed = try std.json.parseFromSlice(Value, allocator, bytes, parse_options);
    // Transfer the parser's arena to the public owner without copying its JSON tree.
    const arena = parsed.arena.*;
    allocator.destroy(parsed.arena);
    return .{ .arena = arena, .value = parsed.value };
}

fn parameterValue(allocator: Allocator, schema: Value, text: []const u8) !Value {
    const field_type = try schemaType(schema);
    if (field_type.nullable and std.mem.eql(u8, text, "null")) return .null;
    if (field_type.kind == .string) {
        if (!std.unicode.utf8ValidateSlice(text)) return error.InvalidUtf8;
        return .{ .string = try allocator.dupe(u8, text) };
    }
    // The leaky parser is intentional here: all parameter trees share Payload.arena.
    const value = try std.json.parseFromSliceLeaky(Value, allocator, text, parse_options);
    if (!try matchesType(allocator, value, field_type)) return error.TypeMismatch;
    return value;
}

fn kindName(name: []const u8) !Kind {
    return std.meta.stringToEnum(Kind, name) orelse error.UnsupportedSchemaType;
}

fn unsupported(err: anyerror) bool {
    return err == error.UnsupportedSchemaKeyword or err == error.UnsupportedSchemaType or
        err == error.UnsupportedSchemaUnion or err == error.UnsupportedSchemaForm or
        err == error.MissingSchemaType;
}

fn schemaType(schema: Value) !Type {
    if (schema == .bool) return error.UnsupportedSchemaForm;
    if (schema != .object) return error.InvalidSchema;
    const type_value = schema.object.get("type") orelse return error.MissingSchemaType;
    if (type_value == .string) return .{ .kind = try kindName(type_value.string) };
    if (type_value != .array or type_value.array.items.len == 0) return error.InvalidSchemaType;
    var shape_index: usize = 0;
    while (shape_index < type_value.array.items.len) : (shape_index += 1) {
        if (type_value.array.items[shape_index] != .string) return error.InvalidSchemaType;
    }
    var base: ?Kind = null;
    var nullable = false;
    var index: usize = 0;
    while (index < type_value.array.items.len) : (index += 1) {
        const item = type_value.array.items[index];
        if (item != .string) return error.InvalidSchemaType;
        const kind = try kindName(item.string);
        if (kind == .null) {
            if (nullable) return error.InvalidSchemaType;
            nullable = true;
        } else {
            if (base != null) return error.UnsupportedSchemaUnion;
            base = kind;
        }
    }
    return .{ .kind = base orelse .null, .nullable = nullable };
}

fn annotation(name: []const u8) bool {
    return std.mem.eql(u8, name, "$schema") or std.mem.eql(u8, name, "$id") or
        std.mem.eql(u8, name, "title") or std.mem.eql(u8, name, "description") or
        std.mem.eql(u8, name, "default") or std.mem.eql(u8, name, "examples") or
        std.mem.eql(u8, name, "deprecated") or std.mem.eql(u8, name, "readOnly") or
        std.mem.eql(u8, name, "writeOnly") or std.mem.eql(u8, name, "format");
}

fn checkAnnotation(name: []const u8, value: Value) !void {
    if (std.mem.eql(u8, name, "default")) return;
    if (std.mem.eql(u8, name, "examples")) {
        if (value != .array) return error.InvalidSchemaAnnotation;
    } else if (std.mem.eql(u8, name, "deprecated") or std.mem.eql(u8, name, "readOnly") or std.mem.eql(u8, name, "writeOnly")) {
        if (value != .bool) return error.InvalidSchemaAnnotation;
    } else if (value != .string) return error.InvalidSchemaAnnotation;
}

/// Counts the finite input graph once; pointer dedup also makes aliased nodes safe.
/// Subsequent traversals have hard work bounds derived from these actual nodes.
fn nodeCount(allocator: Allocator, root: *const Value) !usize {
    var seen = std.AutoHashMap(*const Value, void).init(allocator);
    defer seen.deinit();
    var pending = List(*const Value).init(allocator);
    defer pending.deinit();
    try seen.put(root, {});
    try pending.append(root);
    while (pending.pop()) |node| {
        const children: []const Value = switch (node.*) {
            .array => |a| a.items,
            .object => |o| o.values(),
            else => &.{},
        };
        var index: usize = 0;
        while (index < children.len) : (index += 1) {
            const child = &children[index];
            const entry = try seen.getOrPut(child);
            if (!entry.found_existing) try pending.append(child);
        }
    }
    return seen.count();
}

fn checkSchema(allocator: Allocator, root: *const Value) !usize {
    const bound = try nodeCount(allocator, root);
    if (root.* == .null) return bound; // Explicit service descriptor for no input.
    var pending = List(*const Value).init(allocator);
    defer pending.deinit();
    var seen = std.AutoHashMap(*const Value, void).init(allocator);
    defer seen.deinit();
    try pending.append(root);
    var visited: usize = 0;
    var unsupported_schema: ?anyerror = null;
    while (pending.pop()) |node| {
        const entry = try seen.getOrPut(node);
        if (entry.found_existing) continue;
        if (visited == bound) return error.TraversalLimitExceeded;
        visited += 1;
        const field_type: ?Type = schemaType(node.*) catch |err| blk: {
            if (!unsupported(err)) return err;
            unsupported_schema = err;
            break :blk null;
        };
        if (node.* != .object) continue;
        var fields = node.object.iterator();
        while (fields.next()) |field| {
            const name = field.key_ptr.*;
            const value = field.value_ptr;
            if (std.mem.eql(u8, name, "type")) continue;
            if (annotation(name)) {
                try checkAnnotation(name, value.*);
                continue;
            }
            if (std.mem.eql(u8, name, "properties")) {
                if ((if (field_type) |t| t.kind != .object else false) or value.* != .object) return error.InvalidSchemaProperties;
                var index: usize = 0;
                const children = value.object.values();
                while (index < children.len) : (index += 1) try pending.append(&children[index]);
            } else if (std.mem.eql(u8, name, "required")) {
                if ((if (field_type) |t| t.kind != .object else false) or value.* != .array) return error.InvalidSchemaRequired;
                var names = std.StringHashMap(void).init(allocator);
                defer names.deinit();
                var index: usize = 0;
                while (index < value.array.items.len) : (index += 1) {
                    const item = value.array.items[index];
                    if (item != .string) return error.InvalidSchemaRequired;
                    const required = try names.getOrPut(item.string);
                    if (required.found_existing) return error.InvalidSchemaRequired;
                }
            } else if (std.mem.eql(u8, name, "additionalProperties")) {
                if (field_type) |t| if (t.kind != .object) return error.InvalidSchemaAdditionalProperties;
                if (value.* == .object) {
                    try pending.append(value);
                } else if (value.* != .bool) return error.InvalidSchemaAdditionalProperties;
            } else if (std.mem.eql(u8, name, "items")) {
                if (field_type) |t| if (t.kind != .array) return error.InvalidSchemaItems;
                if (value.* != .object and value.* != .bool) return error.InvalidSchemaItems;
                try pending.append(value);
            } else if (std.mem.eql(u8, name, "enum")) {
                if (value.* != .array or value.array.items.len == 0) return error.InvalidSchemaEnum;
            } else unsupported_schema = error.UnsupportedSchemaKeyword;
        }
    }
    if (unsupported_schema) |err| return err;
    return bound;
}

fn matchesType(allocator: Allocator, value: Value, expected: Type) !bool {
    if (value == .null) return expected.nullable or expected.kind == .null;
    return switch (expected.kind) {
        .string => value == .string,
        .boolean => value == .bool,
        .object => value == .object,
        .array => value == .array,
        .null => false,
        .number => isNumber(value),
        .integer => if (isNumber(value)) blk: {
            const normalized = try decimal(allocator, value);
            break :blk normalized.exponent.isPositive() or normalized.exponent.eqlZero();
        } else false,
    };
}

fn pushPair(allocator: Allocator, pending: *List(Pair), seen: *std.AutoHashMap(Pair, void), pair: Pair, bound: usize) !void {
    if (seen.contains(pair)) return;
    if (seen.count() == bound) return error.TraversalLimitExceeded;
    try seen.put(pair, {});
    _ = allocator;
    try pending.append(pair);
}

fn checkValue(allocator: Allocator, schema: *const Value, value: *const Value, schema_nodes: usize) !void {
    if (schema.* == .null) {
        if (value.* != .null) return error.TypeMismatch;
        return;
    }
    const value_nodes = try nodeCount(allocator, value);
    const bound = std.math.mul(usize, schema_nodes, value_nodes) catch return error.TraversalLimitExceeded;
    var pending = List(Pair).init(allocator);
    defer pending.deinit();
    var seen = std.AutoHashMap(Pair, void).init(allocator);
    defer seen.deinit();
    try pushPair(allocator, &pending, &seen, .{ .schema = schema, .value = value }, bound);
    while (pending.pop()) |pair| {
        if (!try matchesType(allocator, pair.value.*, try schemaType(pair.schema.*))) return error.TypeMismatch;
        if (pair.schema.object.get("enum")) |allowed| {
            var matched = false;
            var index: usize = 0;
            while (index < allowed.array.items.len) : (index += 1) {
                if (try equivalent(allocator, pair.value, &allowed.array.items[index])) {
                    matched = true;
                    break;
                }
            }
            if (!matched) return error.EnumMismatch;
        }
        if (pair.value.* == .object) {
            if (pair.schema.object.get("required")) |required| {
                var index: usize = 0;
                while (index < required.array.items.len) : (index += 1) {
                    if (!pair.value.object.contains(required.array.items[index].string)) return error.MissingRequiredParameter;
                }
            }
            const properties = pair.schema.object.getPtr("properties");
            const additional = pair.schema.object.getPtr("additionalProperties");
            var fields = pair.value.object.iterator();
            while (fields.next()) |field| {
                const child_schema = if (properties) |p| p.object.getPtr(field.key_ptr.*) else null;
                if (child_schema) |child| {
                    try pushPair(allocator, &pending, &seen, .{ .schema = child, .value = field.value_ptr }, bound);
                } else if (additional) |extra| {
                    if (extra.* == .bool) {
                        if (!extra.bool) return error.UnknownProperty;
                    } else try pushPair(allocator, &pending, &seen, .{ .schema = extra, .value = field.value_ptr }, bound);
                }
            }
        } else if (pair.value.* == .array) {
            if (pair.schema.object.getPtr("items")) |items| {
                var index: usize = 0;
                while (index < pair.value.array.items.len) : (index += 1) {
                    try pushPair(allocator, &pending, &seen, .{ .schema = items, .value = &pair.value.array.items[index] }, bound);
                }
            }
        }
    }
}

fn isNumber(value: Value) bool {
    return value == .number_string or value == .integer or value == .float;
}

const Decimal = struct { negative: bool, digits: []const u8, exponent: std.math.big.int.Managed };

/// Canonical decimal coefficient/exponent, with no floating-point conversion or exponent limit.
/// This is only for schema integer/enum checks; the returned payload keeps its original lexeme.
fn decimal(allocator: Allocator, value: Value) !Decimal {
    const bytes = switch (value) {
        .number_string => |text| text,
        .integer => |number| try std.fmt.allocPrint(allocator, "{d}", .{number}),
        .float => |number| blk: {
            if (!std.math.isFinite(number)) return error.InvalidSchemaEnum;
            break :blk try std.fmt.allocPrint(allocator, "{d}", .{number});
        },
        else => return error.TypeMismatch,
    };
    const exponent_start = std.mem.indexOfAny(u8, bytes, "eE") orelse bytes.len;
    const mantissa = bytes[0..exponent_start];
    const fraction = if (std.mem.indexOfScalar(u8, mantissa, '.')) |dot| mantissa.len - dot - 1 else 0;
    var exponent = try std.math.big.int.Managed.initSet(allocator, 0);
    if (exponent_start < bytes.len) {
        var text = bytes[exponent_start + 1 ..];
        if (text.len != 0 and text[0] == '+') text = text[1..];
        try exponent.setString(10, text);
    }
    try exponent.addScalar(&exponent, -@as(i128, @intCast(fraction)));
    const digits = try allocator.alloc(u8, mantissa.len);
    var length: usize = 0;
    var index: usize = 0;
    while (index < mantissa.len) : (index += 1) {
        const char = mantissa[index];
        if (char >= '0' and char <= '9') {
            digits[length] = char;
            length += 1;
        }
    }
    var start: usize = 0;
    while (start < length and digits[start] == '0') : (start += 1) {}
    if (start == length) {
        try exponent.set(0);
        return .{ .negative = false, .digits = "0", .exponent = exponent };
    }
    var end = length;
    while (end > start and digits[end - 1] == '0') : (end -= 1) {}
    try exponent.addScalar(&exponent, length - end);
    return .{ .negative = bytes[0] == '-', .digits = digits[start..end], .exponent = exponent };
}

/// JSON enum equality is structural and numbers compare exactly, including 1 == 1.0.
fn equivalent(allocator: Allocator, left: *const Value, right: *const Value) !bool {
    const bound = std.math.mul(usize, try nodeCount(allocator, left), try nodeCount(allocator, right)) catch
        return error.TraversalLimitExceeded;
    var pending = List(Pair).init(allocator);
    defer pending.deinit();
    var seen = std.AutoHashMap(Pair, void).init(allocator);
    defer seen.deinit();
    try pushPair(allocator, &pending, &seen, .{ .schema = left, .value = right }, bound);
    while (pending.pop()) |pair| {
        const a = pair.schema.*;
        const b = pair.value.*;
        if (isNumber(a) and isNumber(b)) {
            const x = try decimal(allocator, a);
            const y = try decimal(allocator, b);
            if (x.negative != y.negative or !std.mem.eql(u8, x.digits, y.digits) or !x.exponent.eql(y.exponent)) return false;
            continue;
        }
        if (std.meta.activeTag(a) != std.meta.activeTag(b)) return false;
        switch (a) {
            .null => {},
            .bool => |boolean| if (boolean != b.bool) return false,
            .string => |string| if (!std.mem.eql(u8, string, b.string)) return false,
            .array => |array| {
                if (array.items.len != b.array.items.len) return false;
                var index: usize = 0;
                while (index < array.items.len) : (index += 1) {
                    try pushPair(allocator, &pending, &seen, .{ .schema = &array.items[index], .value = &b.array.items[index] }, bound);
                }
            },
            .object => |object| {
                if (object.count() != b.object.count()) return false;
                var fields = object.iterator();
                while (fields.next()) |field| {
                    const other = b.object.getPtr(field.key_ptr.*) orelse return false;
                    try pushPair(allocator, &pending, &seen, .{ .schema = field.value_ptr, .value = other }, bound);
                }
            },
            .number_string, .integer, .float => unreachable,
        }
    }
    return true;
}
