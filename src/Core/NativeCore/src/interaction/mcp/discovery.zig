//! Official DiscoverResult schema (2026-07-28); no initialize handshake.
//! https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/schema/2026-07-28/schema.json
const std = @import("std");
const protocol = @import("protocol.zig");
pub const CacheScope = enum { private, public };
pub const ServerInfo = struct { name: []const u8, version: []const u8 };
pub const Options = struct { server_info: ServerInfo, cache_scope: CacheScope, ttl_ms: u64 };

/// Only implemented tools are advertised here. Additional capabilities are an
/// explicit caller declaration, e.g. from the owner of a future resource adapter.
pub fn result(allocator: std.mem.Allocator, options: Options, extra_capabilities: ?std.json.Value, max_output_bytes: usize) ![]u8 {
    var capabilities: std.json.ObjectMap = .empty;
    defer capabilities.deinit(allocator);
    try capabilities.put(allocator, "tools", .{ .object = .empty });
    if (extra_capabilities) |extra| {
        if (extra != .object) return error.InvalidCapabilities;
        var iterator = extra.object.iterator();
        while (iterator.next()) |entry| {
            if (capabilities.contains(entry.key_ptr.*)) return error.DuplicateCapability;
            if (entry.value_ptr.* != .object) return error.InvalidCapabilities;
            try capabilities.put(allocator, entry.key_ptr.*, entry.value_ptr.*);
        }
    }
    return protocol.stringifyBounded(allocator, .{
        .resultType = protocol.complete_result_type,
        .supportedVersions = &protocol.supported_versions,
        .capabilities = std.json.Value{ .object = capabilities },
        .cacheScope = @tagName(options.cache_scope),
        .ttlMs = options.ttl_ms,
        ._meta = .{ .@"io.modelcontextprotocol/serverInfo" = options.server_info },
    }, max_output_bytes);
}
