//! MCP 2026-07-28 subscription formatting, not a source/holder/transport owner.
//! https://modelcontextprotocol.io/specification/2026-07-28/basic/patterns/subscriptions
//! https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio
//!
//! MCP1 keeps the parsed request ID and requested/accepted filter storage alive
//! until this listen ends and all callbacks return. Its per-request gate must
//! serialize EVERY method below with inbound cancellation and the sole writer's
//! actual output commitment. Output.write must consume/copy its borrowed bytes
//! before returning and must not reenter this Listen. A queued writer must tag
//! every frame with request-owned cancellable delivery state and suppress it at
//! commitment after cancel/EOF; enqueueing untagged bytes is insufficient. This
//! suppression is required even if finish() already accepted terminal frames
//! into the queue: its ended phase does not prove those frames were committed.
//! Do not condition queue cancellation on cancel() returning true. This module
//! adds no lock, queue, source client, sampling, or business state.
const std = @import("std");
pub const Value = std.json.Value;
pub const jsonrpc_version = "2.0";
pub const complete_result_type = "complete";

/// Small shared format helper for this slice. The caller supplies its scoped,
/// bounded allocator; no transport, request parser or protocol owner is linked.
pub fn stringifyBounded(allocator: std.mem.Allocator, value: anytype, max_output_bytes: usize) ![]u8 {
    const buffer = try allocator.alloc(u8, max_output_bytes);
    errdefer allocator.free(buffer);
    var writer = std.Io.Writer.fixed(buffer);
    std.json.Stringify.value(value, .{}, &writer) catch return error.OutputLimitExceeded;
    return try allocator.realloc(buffer, writer.buffered().len);
}

pub const listen_method = "subscriptions/listen";
pub const acknowledged_method = "notifications/subscriptions/acknowledged";
pub const cancelled_method = "notifications/cancelled";
pub const resources_updated_method = "notifications/resources/updated";
pub const resources_list_changed_method = "notifications/resources/list_changed";
pub const tools_list_changed_method = "notifications/tools/list_changed";
pub const prompts_list_changed_method = "notifications/prompts/list_changed";
pub const subscription_id_key = "io.modelcontextprotocol/subscriptionId";

pub const FilterError = error{ InvalidParams, AcceptedFilterNotRequested };

/// Borrowed protocol fields only. False/empty means the protocol's opt-out,
/// never a service default. Source authorization/support are decided upstream.
pub const Filter = struct {
    tools_list_changed: bool = false,
    prompts_list_changed: bool = false,
    resources_list_changed: bool = false,
    resource_subscriptions: []const Value = &.{},

    pub fn parse(value: Value) FilterError!Filter {
        if (value != .object) return error.InvalidParams;
        const tools = try optionalBool(value, "toolsListChanged");
        const prompts = try optionalBool(value, "promptsListChanged");
        const resources = try optionalBool(value, "resourcesListChanged");
        const uris: []const Value = if (value.object.get("resourceSubscriptions")) |field| blk: {
            if (field != .array) return error.InvalidParams;
            break :blk field.array.items;
        } else &.{};
        const result: Filter = .{
            .tools_list_changed = tools,
            .prompts_list_changed = prompts,
            .resources_list_changed = resources,
            .resource_subscriptions = uris,
        };
        try result.validate();
        return result;
    }

    pub fn validate(self: Filter) FilterError!void {
        var index: usize = 0;
        while (index < self.resource_subscriptions.len) : (index += 1) {
            if (self.resource_subscriptions[index] != .string) return error.InvalidParams;
        }
    }

    pub fn containsUri(self: Filter, uri: []const u8) bool {
        var index: usize = 0;
        while (index < self.resource_subscriptions.len) : (index += 1) {
            const entry = self.resource_subscriptions[index];
            if (entry == .string and std.mem.eql(u8, entry.string, uri)) return true;
        }
        return false;
    }

    pub fn validateSubset(self: Filter, requested: Filter) FilterError!void {
        try self.validate();
        try requested.validate();
        if ((self.tools_list_changed and !requested.tools_list_changed) or
            (self.prompts_list_changed and !requested.prompts_list_changed) or
            (self.resources_list_changed and !requested.resources_list_changed))
            return error.AcceptedFilterNotRequested;
        var index: usize = 0;
        while (index < self.resource_subscriptions.len) : (index += 1) {
            if (!requested.containsUri(self.resource_subscriptions[index].string)) return error.AcceptedFilterNotRequested;
        }
    }

    /// Emit only honored fields, omitting false/empty optional subscriptions.
    pub fn jsonStringify(self: Filter, writer: *std.json.Stringify) std.Io.Writer.Error!void {
        try writer.beginObject();
        if (self.tools_list_changed) {
            try writer.objectField("toolsListChanged");
            try writer.write(true);
        }
        if (self.prompts_list_changed) {
            try writer.objectField("promptsListChanged");
            try writer.write(true);
        }
        if (self.resources_list_changed) {
            try writer.objectField("resourcesListChanged");
            try writer.write(true);
        }
        if (self.resource_subscriptions.len != 0) {
            try writer.objectField("resourceSubscriptions");
            try writer.write(self.resource_subscriptions);
        }
        try writer.endObject();
    }
};

fn optionalBool(value: Value, key: []const u8) FilterError!bool {
    const field = value.object.get(key) orelse return false;
    if (field != .bool) return error.InvalidParams;
    return field.bool;
}

/// Envelope metadata belongs to MCP1; only the method-specific params are read.
pub fn parseListenParams(params: Value) FilterError!Filter {
    if (params != .object) return error.InvalidParams;
    return Filter.parse(params.object.get("notifications") orelse return error.InvalidParams);
}

/// Caller supplies every allocation/output budget. Bytes contain compact JSON,
/// no newline; MCP1's sole writer supplies framing. Errors are propagated, never
/// retried or converted into successful completion by this module.
pub const Output = struct {
    allocator: std.mem.Allocator,
    max_output_bytes: usize,
    context: ?*anyopaque,
    write: *const fn (?*anyopaque, []const u8) anyerror!void,

    fn send(self: Output, value: anytype) !void {
        const bytes = try stringifyBounded(self.allocator, value, self.max_output_bytes);
        defer self.allocator.free(bytes);
        try self.write(self.context, bytes);
    }
};

/// Only standard state/list notification mappings are available. Resource URI
/// routing is exact; any source-owned sub-resource mapping needs an explicit
/// authorized binding upstream. Business records are not state invalidations.
pub const Event = union(enum) {
    tools_list_changed,
    prompts_list_changed,
    resources_list_changed,
    resource_updated: []const u8,
    unmapped_business_event: Value,
};

pub const Phase = enum { awaiting_acknowledgment, active, cancelled, disconnected, ended, failed };

pub const Listen = struct {
    id: Value,
    requested: Filter,
    accepted: Filter = .{},
    phase: Phase = .awaiting_acknowledgment,

    /// MCP1 supplies its already validated request ID and preserves the original
    /// number lexeme. Numeric syntax and duplicate-ID routing belong to MCP1;
    /// this formatter does not implement another JSON-RPC ID parser/registry.
    pub fn init(id: Value, requested: Filter) !Listen {
        switch (id) {
            .string, .integer, .number_string => {},
            else => return error.InvalidId,
        }
        try requested.validate();
        return .{ .id = id, .requested = requested };
    }

    /// Returns false after a terminal transition. Acceptance is committed only
    /// after the synchronous writer callback accepts the acknowledgment.
    pub fn acknowledge(self: *Listen, accepted: Filter, output: Output) !bool {
        if (self.phase == .active) return error.AlreadyAcknowledged;
        if (self.phase != .awaiting_acknowledgment) return false;
        try accepted.validateSubset(self.requested);
        output.send(.{
            .jsonrpc = jsonrpc_version,
            .method = acknowledged_method,
            .params = .{ ._meta = self.metadata(), .notifications = accepted },
        }) catch |err| {
            self.phase = .failed;
            return err;
        };
        self.accepted = accepted;
        self.phase = .active;
        return true;
    }

    /// False means filtered or terminal. Pre-ack delivery is an error so a
    /// caller cannot silently lose the initial source event and claim success.
    pub fn emit(self: *Listen, event: Event, output: Output) !bool {
        if (self.phase == .awaiting_acknowledgment) return error.NotAcknowledged;
        if (self.phase != .active) return false;
        const method: []const u8 = switch (event) {
            .tools_list_changed => if (self.accepted.tools_list_changed) tools_list_changed_method else return false,
            .prompts_list_changed => if (self.accepted.prompts_list_changed) prompts_list_changed_method else return false,
            .resources_list_changed => if (self.accepted.resources_list_changed) resources_list_changed_method else return false,
            .resource_updated => |uri| if (self.accepted.containsUri(uri)) resources_updated_method else return false,
            .unmapped_business_event => return error.UnmappedBusinessEvent,
        };
        (switch (event) {
            .resource_updated => |uri| output.send(.{
                .jsonrpc = jsonrpc_version,
                .method = method,
                .params = .{ ._meta = self.metadata(), .uri = uri },
            }),
            else => output.send(.{
                .jsonrpc = jsonrpc_version,
                .method = method,
                .params = .{ ._meta = self.metadata() },
            }),
        }) catch |err| {
            self.phase = .failed;
            return err;
        };
        return true;
    }

    /// MCP1 has already matched requestId to this owning slot. This emits
    /// nothing; MCP1 also cancels queued deliveries under the same gate.
    pub fn cancel(self: *Listen) bool {
        if (self.phase != .awaiting_acknowledgment and self.phase != .active) return false;
        self.phase = .cancelled;
        return true;
    }

    /// EOF/abrupt source transport loss is not a successful listen completion.
    /// MCP1 releases its holders and suppresses its queued frames separately.
    pub fn disconnect(self: *Listen) void {
        if (self.phase == .awaiting_acknowledgment or self.phase == .active) self.phase = .disconnected;
    }

    /// Server-initiated orderly end, not inbound cancellation or source error.
    /// Subscriptions requires the standard completion (SHOULD); Cancellation
    /// requires a server notifications/cancelled for teardown (MUST). Send that
    /// notification while the request is still in progress, as Cancellation's
    /// Behavior Requirements require, then the graceful completion response.
    /// Both writes run in the caller's single per-request serialization gate.
    pub fn finish(self: *Listen, output: Output) !bool {
        if (self.phase == .awaiting_acknowledgment) return error.NotAcknowledged;
        if (self.phase != .active) return false;
        errdefer self.phase = .failed;
        try output.send(.{
            .jsonrpc = jsonrpc_version,
            .method = cancelled_method,
            .params = .{ ._meta = self.metadata(), .requestId = self.id },
        });
        try output.send(.{
            .jsonrpc = jsonrpc_version,
            .id = self.id,
            .result = .{ .resultType = complete_result_type, ._meta = self.metadata() },
        });
        self.phase = .ended;
        return true;
    }

    fn metadata(self: *const Listen) struct { @"io.modelcontextprotocol/subscriptionId": Value } {
        return .{ .@"io.modelcontextprotocol/subscriptionId" = self.id };
    }
};
