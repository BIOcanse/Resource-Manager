//! Presentation of Invoke.v1 values. Routing stdout/stderr belongs to main.
const std = @import("std");
const contract = @import("rm_invocation_contract");
const Allocator = std.mem.Allocator;
const Format = @import("rm_cli_arguments").Format;

pub const Exit = enum(u8) {
    success = 0,
    backend = 1,
    syntax = 2,
    transport = 3,
    cancelled = 130,
};

/// The payload is opaque: null, empty results and domain outcomes do not
/// override the status supplied by the invocation contract.
pub fn responseExit(response: contract.Response) Exit {
    return switch (response.status) {
        .ok => .success,
        .cancelled => .cancelled,
        .not_found, .unauthorized, .forbidden, .unavailable, .invalid_request, .effect_unknown => .backend,
    };
}

pub fn failureExit(failure: contract.Failure) Exit {
    return switch (failure.kind) {
        .invalid_request => .syntax,
        .cancelled => .cancelled,
        .transport, .identity, .invalid_response => .transport,
    };
}

/// Caller owns the returned newline-terminated bytes. Values retain their
/// number_string lexemes; payload strings are never parsed as another JSON value.
pub fn renderResponse(allocator: Allocator, format: Format, response: contract.Response) ![]u8 {
    var output: std.Io.Writer.Allocating = .init(allocator);
    defer output.deinit();
    switch (format) {
        .json => try std.json.Stringify.value(.{
            .status = response.status.text(),
            .payload = response.payload,
        }, .{}, &output.writer),
        .text => {
            try output.writer.print("status: {s}\n", .{response.status.text()});
            try std.json.Stringify.value(response.payload, .{}, &output.writer);
        },
    }
    try output.writer.writeByte('\n');
    return output.toOwnedSlice();
}

/// Both failure fields are retained, including unknown effects after an
/// attempted write. This renderer does not imply rollback or suggest retrying.
pub fn renderFailure(allocator: Allocator, format: Format, failure: contract.Failure) ![]u8 {
    var output: std.Io.Writer.Allocating = .init(allocator);
    defer output.deinit();
    switch (format) {
        .json => try std.json.Stringify.value(.{
            .kind = @tagName(failure.kind),
            .effect = @tagName(failure.effect),
        }, .{}, &output.writer),
        .text => try output.writer.print("kind: {s}\neffect: {s}", .{
            @tagName(failure.kind),
            @tagName(failure.effect),
        }),
    }
    try output.writer.writeByte('\n');
    return output.toOwnedSlice();
}

/// Help is service metadata, not a local operation catalog. Complete schemas
/// remain visible even when a property cannot be expressed as a simple flag.
pub fn renderHelp(allocator: Allocator, format: Format, descriptor: std.json.Value) ![]u8 {
    var output: std.Io.Writer.Allocating = .init(allocator);
    defer output.deinit();
    switch (format) {
        .json => try std.json.Stringify.value(descriptor, .{}, &output.writer),
        .text => {
            const operation = try descriptorString(descriptor, "operation");
            const kind = try descriptorString(descriptor, "kind");
            const admission = try descriptorString(descriptor, "admission");
            const input_schema = descriptor.object.get("inputSchema") orelse return error.InvalidDescriptor;
            const output_schema = descriptor.object.get("outputSchema") orelse return error.InvalidDescriptor;
            try output.writer.print("operation: {s}\nkind: {s}\nadmission: {s}\n", .{ operation, kind, admission });
            if (descriptor.object.get("description")) |description| {
                if (description != .string) return error.InvalidDescriptor;
                try output.writer.print("description: {s}\n", .{description.string});
            }
            if (input_schema == .object) {
                if (input_schema.object.get("properties")) |properties| {
                    if (properties == .object) {
                        var index: usize = 0;
                        while (index < properties.object.count()) : (index += 1) {
                            const property = properties.object.values()[index];
                            if (property != .object) continue;
                            const property_type = property.object.get("type") orelse continue;
                            if (property_type != .string and property_type != .array) continue;
                            const name = properties.object.keys()[index];
                            const required = propertyRequired(input_schema, name);
                            try output.writer.print("--{s} <", .{name});
                            if (property_type == .string) {
                                try output.writer.writeAll(property_type.string);
                            } else try std.json.Stringify.value(property_type, .{}, &output.writer);
                            try output.writer.print("> required: {}\n", .{required});
                        }
                    }
                }
            }
            try output.writer.writeAll("inputSchema: ");
            try std.json.Stringify.value(input_schema, .{}, &output.writer);
            try output.writer.writeAll("\noutputSchema: ");
            try std.json.Stringify.value(output_schema, .{}, &output.writer);
        },
    }
    try output.writer.writeByte('\n');
    return output.toOwnedSlice();
}

fn descriptorString(descriptor: std.json.Value, key: []const u8) ![]const u8 {
    if (descriptor != .object) return error.InvalidDescriptor;
    const value = descriptor.object.get(key) orelse return error.InvalidDescriptor;
    if (value != .string) return error.InvalidDescriptor;
    return value.string;
}

fn propertyRequired(schema: std.json.Value, name: []const u8) bool {
    const required = schema.object.get("required") orelse return false;
    if (required != .array) return false;
    var index: usize = 0;
    while (index < required.array.items.len) : (index += 1) {
        const value = required.array.items[index];
        if (value == .string and std.mem.eql(u8, value.string, name)) return true;
    }
    return false;
}
