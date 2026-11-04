//! MCP 2026-07-28 resource format boundary. The service directory supplies the
//! bindings, URI templates, schemas, permissions and cache policy. URI expansion
//! and current reads belong to the one P0/P-stream client/source, never here.
//! https://modelcontextprotocol.io/specification/2026-07-28/server/resources
const std = @import("std");
const subscriptions = @import("mcp_subscriptions");
const invocation = @import("rm_invocation_contract");
const client = @import("rm_invocation_client");
pub const Value = std.json.Value;
pub const resource_scheme = "rm://";
pub const list_method = "resources/list";
pub const read_method = "resources/read";
pub const templates_method = "resources/templates/list";

/// Required, explicitly supplied by the service/assembly. No frontend TTL or
/// authorization-dependent cache defaults. This module retains no cached value.
pub const CachePolicy = struct { ttl_ms: u64, scope: enum { public, private } };
pub const Page = struct { cache: CachePolicy, next_cursor: ?[]const u8 = null, meta: ?Value = null };
pub const ListParams = struct { cursor: ?[]const u8 };
pub const ReadParams = struct {
    uri: []const u8,
    input_responses: ?Value,
    request_state: ?[]const u8,

    /// URI/explicit continuation parameters only. Protocol _meta/client identity
    /// is not forwarded as service caller evidence. Omission stays omission.
    pub fn jsonStringify(self: ReadParams, writer: *std.json.Stringify) std.Io.Writer.Error!void {
        try writer.beginObject();
        try writer.objectField("uri");
        try writer.write(self.uri);
        try optional(writer, "inputResponses", self.input_responses);
        try optional(writer, "requestState", self.request_state);
        try writer.endObject();
    }
};

/// Protocol metadata projected from a directory binding, not a business registry.
/// kind must come from that same descriptor: a method named Read can be a scan.
pub const Resource = struct {
    kind: invocation.Kind,
    uri: []const u8,
    name: []const u8,
    title: ?[]const u8 = null,
    description: ?[]const u8 = null,
    mime_type: ?[]const u8 = null,
    size: ?u64 = null,
    annotations: ?Value = null,
    icons: ?Value = null,
    meta: ?Value = null,

    pub fn jsonStringify(self: Resource, writer: *std.json.Stringify) std.Io.Writer.Error!void {
        try writer.beginObject();
        try writer.objectField("uri");
        try writer.write(self.uri);
        try writer.objectField("name");
        try writer.write(self.name);
        try optional(writer, "title", self.title);
        try optional(writer, "description", self.description);
        try optional(writer, "mimeType", self.mime_type);
        try optional(writer, "size", self.size);
        try optional(writer, "annotations", self.annotations);
        try optional(writer, "icons", self.icons);
        try optional(writer, "_meta", self.meta);
        try writer.endObject();
    }
};

pub const Template = struct {
    kind: invocation.Kind,
    uri_template: []const u8,
    name: []const u8,
    title: ?[]const u8 = null,
    description: ?[]const u8 = null,
    mime_type: ?[]const u8 = null,
    annotations: ?Value = null,
    icons: ?Value = null,
    meta: ?Value = null,

    pub fn jsonStringify(self: Template, writer: *std.json.Stringify) std.Io.Writer.Error!void {
        try writer.beginObject();
        try writer.objectField("uriTemplate");
        try writer.write(self.uri_template);
        try writer.objectField("name");
        try writer.write(self.name);
        try optional(writer, "title", self.title);
        try optional(writer, "description", self.description);
        try optional(writer, "mimeType", self.mime_type);
        try optional(writer, "annotations", self.annotations);
        try optional(writer, "icons", self.icons);
        try optional(writer, "_meta", self.meta);
        try writer.endObject();
    }
};

pub const Content = struct {
    uri: []const u8,
    mime_type: ?[]const u8 = null,
    data: union(enum) { text: []const u8, binary: []const u8 },
    meta: ?Value = null,

    pub fn jsonStringify(self: Content, writer: *std.json.Stringify) std.Io.Writer.Error!void {
        try writer.beginObject();
        try writer.objectField("uri");
        try writer.write(self.uri);
        try optional(writer, "mimeType", self.mime_type);
        try optional(writer, "_meta", self.meta);
        switch (self.data) {
            .text => |text| {
                try writer.objectField("text");
                try writer.write(text);
            },
            .binary => |bytes| {
                try writer.objectField("blob");
                try writer.beginWriteRaw();
                try writer.writer.writeByte('"');
                try std.base64.standard.Encoder.encodeWriter(writer.writer, bytes);
                try writer.writer.writeByte('"');
                writer.endWriteRaw();
            },
        }
        try writer.endObject();
    }
};

fn optional(writer: *std.json.Stringify, key: []const u8, value: anytype) std.Io.Writer.Error!void {
    if (value) |present| {
        try writer.objectField(key);
        try writer.write(present);
    }
}

fn uriByte(byte: u8) bool {
    return std.ascii.isAlphanumeric(byte) or std.mem.indexOfScalar(u8, "-._~:/?#[]@!$&'()*+,;=", byte) != null;
}

/// Validate wire syntax only. Do not decode % escapes, resolve templates, split
/// parameters, normalize identities or infer a resource's domain operation.
pub fn validUri(uri: []const u8) bool {
    if (!std.mem.startsWith(u8, uri, resource_scheme) or uri.len == resource_scheme.len) return false;
    var i: usize = resource_scheme.len;
    while (i < uri.len) : (i += 1) {
        if (uri[i] == '%') {
            if (uri.len - i < 3 or !std.ascii.isHex(uri[i + 1]) or !std.ascii.isHex(uri[i + 2])) return false;
            i += 2;
        } else if (!uriByte(uri[i])) return false;
    }
    return true;
}

/// Templates are already validated RFC6570 definitions from the service
/// directory. Only reject an incorrect scheme or invalid text at this boundary;
/// never implement a second URI-template expansion/default algorithm here.
fn validTemplate(uri: []const u8) bool {
    if (!std.mem.startsWith(u8, uri, resource_scheme) or uri.len == resource_scheme.len) return false;
    return std.unicode.utf8ValidateSlice(uri) and std.mem.indexOfAny(u8, uri, " \t\r\n") == null;
}

pub fn parseListParams(params: Value) !ListParams {
    if (params != .object) return error.InvalidParams;
    const cursor = params.object.get("cursor") orelse return .{ .cursor = null };
    if (cursor != .string) return error.InvalidParams;
    // Empty and absent cursors remain distinct. The service owns pagination.
    return .{ .cursor = cursor.string };
}

pub fn parseReadParams(params: Value) !ReadParams {
    if (params != .object) return error.InvalidParams;
    const uri = params.object.get("uri") orelse return error.InvalidParams;
    if (uri != .string or !validUri(uri.string)) return error.InvalidParams;
    const input_responses = params.object.get("inputResponses");
    if (input_responses) |responses| {
        if (responses != .object) return error.InvalidParams;
    }
    const request_state = params.object.get("requestState");
    if (request_state) |state| {
        if (state != .string) return error.InvalidParams;
    }
    return .{ .uri = uri.string, .input_responses = input_responses, .request_state = if (request_state) |state| state.string else null };
}

const ListResult = struct {
    resources: []const Resource,
    page: Page,
    pub fn jsonStringify(self: ListResult, writer: *std.json.Stringify) std.Io.Writer.Error!void {
        try writer.beginObject();
        try resultFields(writer, self.page.cache, self.page.meta);
        try optional(writer, "nextCursor", self.page.next_cursor);
        try writer.objectField("resources");
        try writer.write(self.resources);
        try writer.endObject();
    }
};
const TemplatesResult = struct {
    templates: []const Template,
    page: Page,
    pub fn jsonStringify(self: TemplatesResult, writer: *std.json.Stringify) std.Io.Writer.Error!void {
        try writer.beginObject();
        try resultFields(writer, self.page.cache, self.page.meta);
        try optional(writer, "nextCursor", self.page.next_cursor);
        try writer.objectField("resourceTemplates");
        try writer.write(self.templates);
        try writer.endObject();
    }
};
const ReadResult = struct {
    contents: []const Content,
    cache: CachePolicy,
    meta: ?Value,
    pub fn jsonStringify(self: ReadResult, writer: *std.json.Stringify) std.Io.Writer.Error!void {
        try writer.beginObject();
        try resultFields(writer, self.cache, self.meta);
        try writer.objectField("contents");
        try writer.write(self.contents);
        try writer.endObject();
    }
};
fn resultFields(writer: *std.json.Stringify, cache: CachePolicy, meta: ?Value) std.Io.Writer.Error!void {
    try writer.objectField("resultType");
    try writer.write(subscriptions.complete_result_type);
    try writer.objectField("ttlMs");
    try writer.write(cache.ttl_ms);
    try writer.objectField("cacheScope");
    try writer.write(@tagName(cache.scope));
    try optional(writer, "_meta", meta);
}

/// A non-current binding is an assembly error, not a resource hidden by a local
/// whitelist. The directory decides the actual available set on each request.
pub fn encodeList(allocator: std.mem.Allocator, resources: []const Resource, page: Page, max_output_bytes: usize) ![]u8 {
    for (resources, 0..) |resource, i| {
        if (resource.kind != .current_read) return error.NotCurrentRead;
        if (!validUri(resource.uri)) return error.InvalidResourceUri;
        for (resources[0..i]) |previous| {
            if (std.mem.eql(u8, previous.uri, resource.uri)) return error.DuplicateResourceUri;
        }
    }
    return subscriptions.stringifyBounded(allocator, ListResult{ .resources = resources, .page = page }, max_output_bytes);
}

pub fn encodeTemplates(allocator: std.mem.Allocator, templates: []const Template, page: Page, max_output_bytes: usize) ![]u8 {
    for (templates, 0..) |template, i| {
        if (template.kind != .current_read) return error.NotCurrentRead;
        if (!validTemplate(template.uri_template)) return error.InvalidResourceUri;
        for (templates[0..i]) |previous| {
            if (std.mem.eql(u8, previous.uri_template, template.uri_template)) return error.DuplicateResourceUri;
        }
    }
    return subscriptions.stringifyBounded(allocator, TemplatesResult{ .templates = templates, .page = page }, max_output_bytes);
}

/// Encode a successful existing resource, including legitimate empty contents.
/// A service not-found must instead become MCP Invalid Params (-32602), never
/// encodeRead(empty). Service failures/unavailable values keep their typed form.
/// Borrow content only inside the current read/region lease, encode synchronously
/// then return the lease before queuing owned bytes with MCP1's sole writer.
pub fn encodeRead(allocator: std.mem.Allocator, contents: []const Content, cache: CachePolicy, meta: ?Value, max_output_bytes: usize) ![]u8 {
    for (contents) |content| {
        if (!validUri(content.uri)) return error.InvalidResourceUri;
        switch (content.data) {
            .text => |text| if (!std.unicode.utf8ValidateSlice(text)) return error.InvalidText,
            .binary => {},
        }
    }
    return subscriptions.stringifyBounded(allocator, ReadResult{ .contents = contents, .cache = cache, .meta = meta }, max_output_bytes);
}

/// Preserve the complete typed current result as JSON text, including unavailable
/// reasons, nulls and exact integer lexemes. No cached last-good or sampling call.
/// Two temporary buffers are bounded by the caller's output budget; no payload
/// allocation is retained by this module. The caller supplies its scoped owner
/// allocator/workspace, never a private page_allocator allocation.
pub fn encodeTypedCurrent(allocator: std.mem.Allocator, uri: []const u8, mime_type: []const u8, value: Value, cache: CachePolicy, meta: ?Value, max_output_bytes: usize) ![]u8 {
    if (!validUri(uri)) return error.InvalidResourceUri;
    const text = try subscriptions.stringifyBounded(allocator, value, max_output_bytes);
    defer allocator.free(text);
    return encodeRead(allocator, &.{.{ .uri = uri, .mime_type = mime_type, .data = .{ .text = text } }}, cache, meta, max_output_bytes);
}

/// Consume the real P0 client delivery view while the caller's source Loan is
/// local and active. P-stream owns permission, stable current and write_json;
/// this neither opens a connection nor releases/retains the caller's Loan.
/// Events/terminal deliveries cannot be collapsed into current resource text.
pub const SourceResult = union(enum) {
    encoded: []u8,
    /// Still borrowed from the caller's local Loan. Status is independent of
    /// write_json: never assume the payload repeats status, or fabricate a
    /// successful resource. MCP1 maps the original status AND payload together
    /// before releasing the Loan (not_found -> Invalid Params, for example).
    failure: client.Delivery,
};

pub fn encodeSourceCurrent(allocator: std.mem.Allocator, uri: []const u8, mime_type: []const u8, delivery: client.Delivery, cache: CachePolicy, meta: ?Value, max_output_bytes: usize) !SourceResult {
    if (!validUri(uri)) return error.InvalidResourceUri;
    switch (delivery.kind) {
        .initial, .current => {},
        .event, .terminal => return error.NotCurrentRead,
    }
    if (delivery.status != .ok) return .{ .failure = delivery };
    const buffer = try allocator.alloc(u8, max_output_bytes);
    defer allocator.free(buffer);
    var writer = std.Io.Writer.fixed(buffer);
    delivery.writeJson(&writer) catch |err| {
        if (err == error.WriteFailed) return error.OutputLimitExceeded;
        return err;
    };
    return .{ .encoded = try encodeRead(allocator, &.{.{ .uri = uri, .mime_type = mime_type, .data = .{ .text = writer.buffered() } }}, cache, meta, max_output_bytes) };
}
