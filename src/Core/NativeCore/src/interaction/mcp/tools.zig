//! MCP projections over the single shared Invoke.v1 client. P0 owns discovery,
//! execution, admission and domain validation; no service owner is linked here.
const std = @import("std");
const protocol = @import("protocol.zig");
pub const invocation = @import("rm_invocation_client");
pub const contract = invocation.contract;

pub const Error = error{
    InvalidParams,
    UnsupportedCursor,
    DuplicateToolName,
    InvalidInputSchema,
    InvalidOutputSchema,
    InvalidToolMetadata,
    InvalidDirectory,
    UnsupportedSchemaDialect,
    RelativeSchemaReference,
    OutputTooLarge,
    OutOfMemory,
};

/// All fields are borrowed. Names are service-owned strings; protocol naming
/// recommendations do not restrict them here. Schemas must be JSON Schema
/// objects. input_schema must declare type: object. output_schema describes the
/// project's exported shape: objects are unchanged; other service values are
/// exported as {"value": original}. This wrapper is project policy, not an MCP
/// requirement: MCP 2026-07-28 also permits nonobject structuredContent.
/// The live binding below derives this envelope from P0's success schema while
/// preserving its original reference scope.
pub const Tool = struct {
    name: []const u8,
    title: ?[]const u8 = null,
    description: ?[]const u8 = null,
    metadata: ?std.json.Value = null,
    input_schema: std.json.Value,
    output_schema: ?std.json.Value = null,

    pub fn jsonStringify(self: Tool, writer: *std.json.Stringify) std.Io.Writer.Error!void {
        try writer.beginObject();
        try writer.objectField("name");
        try writer.write(self.name);
        if (self.title) |title| {
            try writer.objectField("title");
            try writer.write(title);
        }
        if (self.description) |description| {
            try writer.objectField("description");
            try writer.write(description);
        }
        if (self.metadata) |metadata| {
            try writer.objectField("_meta");
            try writer.write(metadata);
        }
        try writer.objectField("inputSchema");
        try writer.write(self.input_schema);
        if (self.output_schema) |schema| {
            try writer.objectField("outputSchema");
            try writer.write(schema);
        }
        try writer.endObject();
    }
};

pub const CallParams = struct {
    name: []const u8,
    /// null means absent, not an empty object or a service default.
    arguments: ?std.json.Value,
};

fn schemaObject(schema: std.json.Value) bool {
    if (schema != .object) return false;
    if (schema.object.get("$schema")) |dialect| {
        if (dialect != .string) return false;
    }
    return true;
}

pub fn validateTool(tool: Tool) Error!void {
    if (tool.metadata) |metadata| if (metadata != .object) return error.InvalidToolMetadata;
    if (!schemaObject(tool.input_schema)) return error.InvalidInputSchema;
    const input_type = tool.input_schema.object.get("type") orelse return error.InvalidInputSchema;
    if (input_type != .string or !std.mem.eql(u8, input_type.string, "object")) return error.InvalidInputSchema;
    if (tool.output_schema) |schema| {
        if (!schemaObject(schema)) return error.InvalidOutputSchema;
    }
}

/// No pagination is advertised. Every supplied cursor, including an empty one,
/// is unsupported. Request metadata is handled by the protocol envelope owner.
pub fn parseListParams(params: ?std.json.Value) Error!void {
    const value = params orelse return;
    if (value != .object) return error.InvalidParams;
    if (value.object.contains("cursor")) return error.UnsupportedCursor;
}

/// Borrow the name and object arguments. Deliberately do not inspect domain
/// fields, apply defaults, resolve the operation, or validate its JSON Schema.
pub fn parseCallParams(params: ?std.json.Value) Error!CallParams {
    const value = params orelse return error.InvalidParams;
    if (value != .object) return error.InvalidParams;
    const name = value.object.get("name") orelse return error.InvalidParams;
    if (name != .string) return error.InvalidParams;
    const arguments = value.object.get("arguments");
    if (arguments) |argument_value| {
        if (argument_value != .object) return error.InvalidParams;
    }
    return .{ .name = name.string, .arguments = arguments };
}

/// Allocate no more than the caller's byte budget before writing. A fixed
/// writer rejects excess bytes as they are emitted. The caller owns the result.
fn encodeBounded(allocator: std.mem.Allocator, value: anytype, max_output_bytes: usize) Error![]u8 {
    const buffer = try allocator.alloc(u8, max_output_bytes);
    errdefer allocator.free(buffer);
    var writer = std.Io.Writer.fixed(buffer);
    std.json.Stringify.value(value, .{}, &writer) catch return error.OutputTooLarge;
    return try allocator.realloc(buffer, writer.buffered().len);
}

/// Result body only; the protocol owner writes the JSON-RPC envelope. Preserve
/// the supplied catalog's order and optional fields; never advertise a cursor.
pub fn encodeList(allocator: std.mem.Allocator, tools: []const Tool, max_output_bytes: usize) Error![]u8 {
    try validateCatalog(tools);
    return encodeBounded(allocator, .{ .resultType = protocol.complete_result_type, .tools = tools }, max_output_bytes);
}

fn validateCatalog(tools: []const Tool) Error!void {
    var index: usize = 0;
    while (index < tools.len) : (index += 1) {
        try validateTool(tools[index]);
        var previous: usize = 0;
        while (previous < index) : (previous += 1) {
            if (std.mem.eql(u8, tools[previous].name, tools[index].name)) return error.DuplicateToolName;
        }
    }
}

const ExportValue = struct {
    value: std.json.Value,

    pub fn jsonStringify(self: ExportValue, writer: *std.json.Stringify) std.Io.Writer.Error!void {
        if (self.value == .object) {
            try writer.write(self.value);
        } else {
            // The sole value-wrapping point. No stringification or field loss.
            try writer.write(.{ .value = self.value });
        }
    }
};

/// Format one already-completed service result. is_error comes explicitly from
/// its owner, never from HTTP status, error-looking text, or business fields.
/// Both views are serialized from the same borrowed typed value without a
/// second backend call. number_string retains arbitrary-precision JSON numbers.
/// Peak output storage is bounded by two caller-sized buffers.
pub fn encodeCallResult(allocator: std.mem.Allocator, value: std.json.Value, is_error: bool, max_output_bytes: usize) Error![]u8 {
    const exported = ExportValue{ .value = value };
    const text = try encodeBounded(allocator, exported, max_output_bytes);
    defer allocator.free(text);
    return encodeBounded(allocator, .{
        .resultType = protocol.complete_result_type,
        .content = .{.{ .type = "text", .text = text }},
        .structuredContent = exported,
        .isError = is_error,
    }, max_output_bytes);
}

/// Result JSON is owned by allocator. Faults have no request ID yet; the MCP
/// routing owner supplies the original ID when writing its JSON-RPC envelope.
pub const Execution = union(enum) {
    result: []u8,
    fault: struct { kind: protocol.FaultKind, data_json: ?[]u8 = null },

    pub fn deinit(self: *Execution, allocator: std.mem.Allocator) void {
        switch (self.*) {
            .result => |bytes| allocator.free(bytes),
            .fault => |fault| if (fault.data_json) |bytes| allocator.free(bytes),
        }
        self.* = undefined;
    }
};

/// Never mutate a shared client's allocator while concurrent requests run.
pub fn clientWithAllocator(client: invocation.Client, allocator: std.mem.Allocator) invocation.Client {
    var request_client = client;
    request_client.allocator = allocator;
    return request_client;
}

pub const ServiceDescriptor = struct {
    operation: []const u8,
    kind: contract.Kind,
    admission: contract.Admission,
    input_schema: std.json.Value,
    output_schema: std.json.Value,
};

fn requiredField(value: std.json.Value, name: []const u8) Error!std.json.Value {
    if (value != .object) return error.InvalidDirectory;
    return value.object.get(name) orelse error.InvalidDirectory;
}

/// Decode precisely the directory shape written by Invoke.v1 writeDirectory.
/// complete:false means the service's incrementally bound directory; it does
/// not authorize the adapter to invent any additional product operations.
pub fn directoryOperations(payload: std.json.Value) Error![]const std.json.Value {
    const version = try requiredField(payload, "contract");
    if (version != .string or !std.mem.eql(u8, version.string, contract.version)) return error.InvalidDirectory;
    const complete = try requiredField(payload, "complete");
    if (complete != .bool) return error.InvalidDirectory;
    const operations = try requiredField(payload, "operations");
    if (operations != .array) return error.InvalidDirectory;
    return operations.array.items;
}

pub fn serviceDescriptor(value: std.json.Value) Error!ServiceDescriptor {
    const operation = try requiredField(value, "operation");
    const kind = try requiredField(value, "kind");
    const admission = try requiredField(value, "admission");
    const input = try requiredField(value, "inputSchema");
    const output = try requiredField(value, "outputSchema");
    if (operation != .string or kind != .string or admission != .string) return error.InvalidDirectory;
    if (input != .bool and !schemaObject(input)) return error.InvalidDirectory;
    if (output != .bool and !schemaObject(output)) return error.InvalidDirectory;
    const typed_kind = std.meta.stringToEnum(contract.Kind, kind.string) orelse return error.InvalidDirectory;
    const typed_admission = std.meta.stringToEnum(contract.Admission, admission.string) orelse return error.InvalidDirectory;
    return .{ .operation = operation.string, .kind = typed_kind, .admission = typed_admission, .input_schema = input, .output_schema = output };
}

const Field = struct { name: []const u8, value: std.json.Value };

fn objectValue(allocator: std.mem.Allocator, fields: []const Field) Error!std.json.Value {
    var value: std.json.ObjectMap = .empty;
    errdefer value.deinit(allocator);
    var index: usize = 0;
    while (index < fields.len) : (index += 1) try value.put(allocator, fields[index].name, fields[index].value);
    return .{ .object = value };
}

fn arrayValue(allocator: std.mem.Allocator, values: []const std.json.Value) Error!std.json.Value {
    var array = std.json.Array.init(allocator);
    errdefer array.deinit();
    try array.appendSlice(values);
    return .{ .array = array };
}

fn stringValue(value: []const u8) std.json.Value {
    return .{ .string = value };
}

pub fn statusIsError(status: contract.Status) bool {
    return status != .ok;
}

/// Preserve the status and the entire typed payload, including empty success.
/// The temporary object map belongs to the caller's request arena.
pub fn responseValue(allocator: std.mem.Allocator, response: contract.Response) Error!std.json.Value {
    return objectValue(allocator, &.{
        .{ .name = "status", .value = stringValue(response.status.text()) },
        .{ .name = "payload", .value = response.payload },
    });
}

/// These are the actual client failure fields. There is no synthetic status,
/// transport reason, HTTP inference, or guessed committed/effect flag.
pub fn failureValue(allocator: std.mem.Allocator, failure: contract.Failure) Error!std.json.Value {
    const detail = try objectValue(allocator, &.{
        .{ .name = "kind", .value = stringValue(@tagName(failure.kind)) },
        .{ .name = "effect", .value = stringValue(@tagName(failure.effect)) },
    });
    return objectValue(allocator, &.{.{ .name = "failure", .value = detail }});
}

pub fn encodeResponse(allocator: std.mem.Allocator, response: contract.Response, max_output_bytes: usize) Error!Execution {
    var arena = std.heap.ArenaAllocator.init(allocator);
    defer arena.deinit();
    return .{ .result = try encodeCallResult(allocator, try responseValue(arena.allocator(), response), statusIsError(response.status), max_output_bytes) };
}

pub fn encodeFailure(allocator: std.mem.Allocator, failure: contract.Failure, max_output_bytes: usize) Error!Execution {
    var arena = std.heap.ArenaAllocator.init(allocator);
    defer arena.deinit();
    return .{ .result = try encodeCallResult(allocator, try failureValue(arena.allocator(), failure), true, max_output_bytes) };
}

const InputShape = enum { object, null_value, wrapped };

fn inputShape(descriptor: ServiceDescriptor) Error!InputShape {
    if (descriptor.input_schema == .bool) return .wrapped;
    const schema_type = descriptor.input_schema.object.get("type") orelse return .wrapped;
    if (schema_type != .string) return .wrapped;
    if (std.mem.eql(u8, schema_type.string, "object")) return .object;
    if (std.mem.eql(u8, schema_type.string, "null")) return .null_value;
    return .wrapped;
}

/// Shape adaptation only; service validation owns all invalid arguments and
/// reasons. Preserve nonmatching/extra fields instead of rejecting or dropping
/// them locally. Object inputs keep omitted arguments as null for P0.
pub fn callPayload(descriptor: ServiceDescriptor, arguments: ?std.json.Value) Error!std.json.Value {
    if (arguments) |value| if (value != .object) return error.InvalidParams;
    switch (try inputShape(descriptor)) {
        .object => return arguments orelse .null,
        .null_value => {
            if (arguments) |value| if (value.object.count() != 0) return value;
            return .null;
        },
        .wrapped => {
            if (arguments) |value| {
                if (value.object.count() == 1) {
                    if (value.object.get("value")) |payload| return payload;
                }
                return value;
            }
            return .null;
        },
    }
}

/// Stable absolute schema resource identifiers preserve each original root's
/// local references when its schema is nested in a protocol envelope.
pub const schema_resource_prefix = "urn:resource-manager:mcp:schema:";

fn absoluteUri(text: []const u8) bool {
    if (text.len == 0 or !std.ascii.isAlphabetic(text[0])) return false;
    var index: usize = 1;
    while (index < text.len) : (index += 1) {
        const byte = text[index];
        if (byte == ':') return true;
        if (!std.ascii.isAlphanumeric(byte) and byte != '+' and byte != '-' and byte != '.') return false;
    }
    return false;
}

fn knownDialect(text: []const u8) bool {
    return std.mem.eql(u8, text, "https://json-schema.org/draft/2020-12/schema") or
        std.mem.eql(u8, text, "https://json-schema.org/draft/2020-12/schema#") or
        std.mem.eql(u8, text, "http://json-schema.org/draft-07/schema#") or
        std.mem.eql(u8, text, "http://json-schema.org/draft-07/schema");
}

const SchemaFrame = struct { schema: std.json.Value, explicit_base: bool };

/// Inspect only schema-bearing keyword positions, never object literals under
/// const/default/enum/examples. Unknown dialects or unresolved relative external
/// references need a service contract decision, not a silently invented base.
fn checkSchemaReferences(allocator: std.mem.Allocator, schema: std.json.Value) Error!void {
    var stack: std.ArrayList(SchemaFrame) = .empty;
    defer stack.deinit(allocator);
    try stack.append(allocator, .{ .schema = schema, .explicit_base = false });
    while (stack.items.len != 0) {
        const frame = stack.items[stack.items.len - 1];
        stack.items.len -= 1;
        if (frame.schema == .bool) continue;
        if (frame.schema != .object) return error.InvalidOutputSchema;
        var has_base = frame.explicit_base;
        const members = frame.schema.object;
        if (members.get("$schema")) |dialect| {
            if (dialect != .string) return error.InvalidOutputSchema;
            if (!knownDialect(dialect.string)) return error.UnsupportedSchemaDialect;
        }
        if (members.get("$id")) |id| {
            if (id != .string) return error.InvalidOutputSchema;
            if (absoluteUri(id.string)) has_base = true else if (!has_base) return error.RelativeSchemaReference;
        }
        const references = [_][]const u8{ "$ref", "$dynamicRef" };
        var index: usize = 0;
        while (index < references.len) : (index += 1) {
            if (members.get(references[index])) |reference| {
                if (reference != .string) return error.InvalidOutputSchema;
                const local = reference.string.len == 0 or reference.string[0] == '#';
                if (!local and !absoluteUri(reference.string) and !has_base) return error.RelativeSchemaReference;
            }
        }
        const child_keywords = [_][]const u8{ "items", "additionalItems", "contains", "additionalProperties", "unevaluatedProperties", "unevaluatedItems", "propertyNames", "not", "if", "then", "else", "contentSchema" };
        index = 0;
        while (index < child_keywords.len) : (index += 1) {
            if (members.get(child_keywords[index])) |child| {
                if (child == .array) {
                    var child_index: usize = 0;
                    while (child_index < child.array.items.len) : (child_index += 1) try stack.append(allocator, .{ .schema = child.array.items[child_index], .explicit_base = has_base });
                } else try stack.append(allocator, .{ .schema = child, .explicit_base = has_base });
            }
        }
        const array_keywords = [_][]const u8{ "allOf", "anyOf", "oneOf", "prefixItems" };
        index = 0;
        while (index < array_keywords.len) : (index += 1) {
            if (members.get(array_keywords[index])) |children| {
                if (children != .array) return error.InvalidOutputSchema;
                var child_index: usize = 0;
                while (child_index < children.array.items.len) : (child_index += 1) try stack.append(allocator, .{ .schema = children.array.items[child_index], .explicit_base = has_base });
            }
        }
        const map_keywords = [_][]const u8{ "$defs", "definitions", "properties", "patternProperties", "dependentSchemas" };
        index = 0;
        while (index < map_keywords.len) : (index += 1) {
            if (members.get(map_keywords[index])) |children| {
                if (children != .object) return error.InvalidOutputSchema;
                var iterator = children.object.iterator();
                while (iterator.next()) |entry| try stack.append(allocator, .{ .schema = entry.value_ptr.*, .explicit_base = has_base });
            }
        }
        // Draft-07 dependencies contains either schemas or string arrays.
        if (members.get("dependencies")) |children| {
            if (children != .object) return error.InvalidOutputSchema;
            var iterator = children.object.iterator();
            while (iterator.next()) |entry| {
                if (entry.value_ptr.* != .array) try stack.append(allocator, .{ .schema = entry.value_ptr.*, .explicit_base = has_base });
            }
        }
    }
}

fn schemaId(allocator: std.mem.Allocator, operation: []const u8, suffix: []const u8) Error![]const u8 {
    const encoded_length = std.math.mul(usize, operation.len, 3) catch return error.OutOfMemory;
    const prefix_length = std.math.add(usize, schema_resource_prefix.len, encoded_length) catch return error.OutOfMemory;
    const total_length = std.math.add(usize, prefix_length, suffix.len) catch return error.OutOfMemory;
    const bytes = try allocator.alloc(u8, total_length);
    @memcpy(bytes[0..schema_resource_prefix.len], schema_resource_prefix);
    const hex = "0123456789ABCDEF";
    var index: usize = 0;
    while (index < operation.len) : (index += 1) {
        const start = schema_resource_prefix.len + index * 3;
        bytes[start] = '%';
        bytes[start + 1] = hex[operation[index] >> 4];
        bytes[start + 2] = hex[operation[index] & 15];
    }
    @memcpy(bytes[prefix_length..], suffix);
    return bytes;
}

fn scopedSchema(allocator: std.mem.Allocator, schema: std.json.Value, operation: []const u8, suffix: []const u8) Error!std.json.Value {
    try checkSchemaReferences(allocator, schema);
    if (schema == .bool) return schema;
    if (schema.object.contains("$id")) return schema;
    var members: std.json.ObjectMap = .empty;
    var iterator = schema.object.iterator();
    while (iterator.next()) |entry| try members.put(allocator, entry.key_ptr.*, entry.value_ptr.*);
    try members.put(allocator, "$id", stringValue(try schemaId(allocator, operation, suffix)));
    return .{ .object = members };
}

fn objectSchema(allocator: std.mem.Allocator, properties: std.json.Value, required: []const std.json.Value) Error!std.json.Value {
    return objectValue(allocator, &.{
        .{ .name = "type", .value = stringValue("object") },
        .{ .name = "properties", .value = properties },
        .{ .name = "required", .value = try arrayValue(allocator, required) },
        .{ .name = "additionalProperties", .value = .{ .bool = false } },
    });
}

fn tagEnumSchema(allocator: std.mem.Allocator, comptime T: type) Error!std.json.Value {
    const tags = std.meta.tags(T);
    const values = try allocator.alloc(std.json.Value, tags.len);
    var index: usize = 0;
    while (index < tags.len) : (index += 1) values[index] = stringValue(@tagName(tags[index]));
    return objectValue(allocator, &.{.{ .name = "enum", .value = try arrayValue(allocator, values) }});
}

fn responseSchema(allocator: std.mem.Allocator, descriptor: ServiceDescriptor) Error!std.json.Value {
    const ok_status = try objectValue(allocator, &.{.{ .name = "const", .value = stringValue(contract.Status.ok.text()) }});
    const ok_properties = try objectValue(allocator, &.{
        .{ .name = "status", .value = ok_status },
        .{ .name = "payload", .value = try scopedSchema(allocator, descriptor.output_schema, descriptor.operation, ":output") },
    });
    const success = try objectSchema(allocator, ok_properties, &.{ stringValue("status"), stringValue("payload") });
    const statuses = std.meta.tags(contract.Status);
    var error_statuses = std.json.Array.init(allocator);
    var index: usize = 0;
    while (index < statuses.len) : (index += 1) {
        if (statuses[index] != .ok) try error_statuses.append(stringValue(statuses[index].text()));
    }
    const non_ok_status = try objectValue(allocator, &.{.{ .name = "enum", .value = .{ .array = error_statuses } }});
    const non_ok_properties = try objectValue(allocator, &.{
        .{ .name = "status", .value = non_ok_status },
        // P0 declares the success payload schema only. Error payloads remain
        // complete and unconstrained; an empty object schema accepts any JSON.
        .{ .name = "payload", .value = .{ .object = .empty } },
    });
    const non_ok = try objectSchema(allocator, non_ok_properties, &.{ stringValue("status"), stringValue("payload") });
    const detail_properties = try objectValue(allocator, &.{
        .{ .name = "kind", .value = try tagEnumSchema(allocator, contract.FailureKind) },
        .{ .name = "effect", .value = try tagEnumSchema(allocator, contract.Effect) },
    });
    const detail = try objectSchema(allocator, detail_properties, &.{ stringValue("kind"), stringValue("effect") });
    const failure_properties = try objectValue(allocator, &.{.{ .name = "failure", .value = detail }});
    const failure = try objectSchema(allocator, failure_properties, &.{stringValue("failure")});
    return objectValue(allocator, &.{
        .{ .name = "type", .value = stringValue("object") },
        .{ .name = "oneOf", .value = try arrayValue(allocator, &.{ success, non_ok, failure }) },
    });
}

/// Adapt every service descriptor by schema shape, never by operation name,
/// kind, admission, or a second capability whitelist. Returned maps belong to
/// allocator; production callers use a request-scoped temporary arena.
pub fn projectDescriptor(allocator: std.mem.Allocator, descriptor: ServiceDescriptor) Error!Tool {
    try checkSchemaReferences(allocator, descriptor.input_schema);
    const input = switch (try inputShape(descriptor)) {
        .object => descriptor.input_schema,
        .null_value => try objectSchema(allocator, .{ .object = .empty }, &.{}),
        .wrapped => try objectSchema(allocator, try objectValue(allocator, &.{.{
            .name = "value",
            .value = try scopedSchema(allocator, descriptor.input_schema, descriptor.operation, ":input"),
        }}), &.{stringValue("value")}),
    };
    const metadata = try objectValue(allocator, &.{
        .{ .name = "io.resource-manager/operationKind", .value = stringValue(@tagName(descriptor.kind)) },
        .{ .name = "io.resource-manager/admission", .value = stringValue(@tagName(descriptor.admission)) },
    });
    return .{ .name = descriptor.operation, .metadata = metadata, .input_schema = input, .output_schema = try responseSchema(allocator, descriptor) };
}

pub fn executeList(allocator: std.mem.Allocator, client: invocation.Client, cancel: contract.Cancel, max_output_bytes: usize) Error!Execution {
    const request_client = clientWithAllocator(client, allocator);
    var listed = request_client.list(cancel);
    defer listed.deinit();
    switch (listed) {
        .failure => |failure| {
            var arena = std.heap.ArenaAllocator.init(allocator);
            defer arena.deinit();
            return .{ .fault = .{ .kind = .internal_error, .data_json = try encodeBounded(allocator, try failureValue(arena.allocator(), failure), max_output_bytes) } };
        },
        .response => |response| {
            if (response.value.status != .ok) {
                var arena = std.heap.ArenaAllocator.init(allocator);
                defer arena.deinit();
                return .{ .fault = .{ .kind = .internal_error, .data_json = try encodeBounded(allocator, try responseValue(arena.allocator(), response.value), max_output_bytes) } };
            }
            const operations = try directoryOperations(response.value.payload);
            var arena = std.heap.ArenaAllocator.init(allocator);
            defer arena.deinit();
            const temporary = arena.allocator();
            const catalog = try temporary.alloc(Tool, operations.len);
            var tool_count: usize = 0;
            var index: usize = 0;
            while (index < operations.len) : (index += 1) {
                const descriptor = try serviceDescriptor(operations[index]);
                // Streaming declarations belong to P-stream/MCP subscription
                // routes, never to this synchronous Client.invoke path.
                if (descriptor.kind == .subscription) continue;
                catalog[tool_count] = try projectDescriptor(temporary, descriptor);
                tool_count += 1;
            }
            const visible = catalog[0..tool_count];
            try validateCatalog(visible);
            const metadata = try objectValue(temporary, &.{
                .{ .name = "io.resource-manager/invocationContract", .value = stringValue(contract.version) },
                .{ .name = "io.resource-manager/directoryComplete", .value = try requiredField(response.value.payload, "complete") },
            });
            return .{ .result = try encodeBounded(allocator, .{ .resultType = protocol.complete_result_type, .tools = visible, ._meta = metadata }, max_output_bytes) };
        },
    }
}

pub fn executeCall(allocator: std.mem.Allocator, client: invocation.Client, cancel: contract.Cancel, params: CallParams, max_output_bytes: usize) Error!Execution {
    const request_client = clientWithAllocator(client, allocator);
    var listed = request_client.list(cancel);
    defer listed.deinit();
    switch (listed) {
        .failure => |failure| return encodeFailure(allocator, failure, max_output_bytes),
        .response => |response| {
            if (response.value.status != .ok) return encodeResponse(allocator, response.value, max_output_bytes);
            const operations = try directoryOperations(response.value.payload);
            var selected: ?ServiceDescriptor = null;
            var index: usize = 0;
            while (index < operations.len) : (index += 1) {
                const descriptor = try serviceDescriptor(operations[index]);
                if (std.mem.eql(u8, descriptor.operation, params.name)) {
                    if (selected != null) return error.DuplicateToolName;
                    selected = descriptor;
                }
            }
            const descriptor = selected orelse return .{ .fault = .{ .kind = .invalid_params } };
            if (descriptor.kind == .subscription) return .{ .fault = .{ .kind = .invalid_params } };
            const payload = try callPayload(descriptor, params.arguments);
            var invoked = request_client.invoke(.{ .operation = descriptor.operation, .payload = payload }, cancel);
            defer invoked.deinit();
            return switch (invoked) {
                .response => |result| encodeResponse(allocator, result.value, max_output_bytes),
                .failure => |failure| encodeFailure(allocator, failure, max_output_bytes),
            };
        },
    }
}
