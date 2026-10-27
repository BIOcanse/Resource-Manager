const std = @import("std");
pub const c = @import("c.zig").win;
pub const keys = @import("key_reader.zig");
pub const identity = @import("identity.zig");
pub const probe = @import("probe.zig");

pub const Config = struct {
    /// Borrowed UTF-16 paths, not NUL terminated. Valid until run returns.
    package_root: []const u16,
    backend_path: []const u16,
    port: u16 = 9321,
    service_name: [:0]const u16 = identity.service_name,
    probe_interval_ms: u32,
    timeout_ms: u32,
};
pub const Publish = *const fn (context: *anyopaque, ready: bool, generation: u64, reason: []const u8) void;
pub const authenticated_probe_unavailable = "winhttp-pre-send-attestation-unavailable";
const Workspace = struct { reader: keys.Reader, identity: identity.Scratch };

/// The session remains unavailable until there is an approved transport with
/// a guaranteed, actual-connection attestation gate before credential writes.
pub fn authenticatedReady() bool {
    return false;
}

/// Long-lived block entry. Owns all HTTP handles/callbacks and returns on stop.
/// Publication is borrowed for the callback; the host queues/copies as needed.
/// Generation is zero while no authenticated session has ever been established.
pub fn run(stop: c.HANDLE, context: *anyopaque, publish: Publish, config: Config) void {
    if (config.package_root.len == 0 or config.backend_path.len == 0 or config.port == 0 or config.probe_interval_ms == 0 or config.probe_interval_ms >= c.INFINITE or config.timeout_ms == 0 or config.timeout_ms > std.math.maxInt(i32)) {
        publish(context, false, 0, "invalid-backend-config");
        return;
    }
    // One fixed-size allocation, with no growing allocator or hidden task pool.
    const memory = c.VirtualAlloc(null, @sizeOf(Workspace), c.MEM_RESERVE | c.MEM_COMMIT, c.PAGE_READWRITE) orelse {
        publish(context, false, 0, "backend-workspace-unavailable");
        return;
    };
    defer _ = c.VirtualFree(memory, 0, c.MEM_RELEASE);
    const workspace: *Workspace = @ptrCast(@alignCast(memory));
    workspace.reader = .{};
    var last_reason: []const u8 = "";
    while (c.WaitForSingleObject(stop, 0) == c.WAIT_TIMEOUT) {
        const reason = attempt(workspace, stop, config);
        if (!std.mem.eql(u8, last_reason, reason)) {
            publish(context, false, 0, reason);
            last_reason = reason;
        }
        if (c.WaitForSingleObject(stop, config.probe_interval_ms) != c.WAIT_TIMEOUT) break;
    }
    publish(context, false, 0, "stopped");
}

fn attempt(workspace: *Workspace, stop: c.HANDLE, config: Config) []const u8 {
    const expected = identity.capture(&workspace.identity, config.backend_path, config.service_name) catch |err| return @errorName(err);
    var key = (workspace.reader.read(config.package_root, stop) catch |err| return @errorName(err)) orelse return "default-key-unavailable";
    defer key.clear();
    if (key.len == 0) return "default-key-empty";
    // A diagnostic probe cannot make an authenticated session ready. Never pass
    // the key to WinHTTP while its pre-send gate remains unresolved.
    _ = probe.unauthenticated(stop, config.port, config.timeout_ms, &workspace.identity, expected, config.backend_path) catch |err| {
        if (err == error.Stopped) return "stopped";
        return authenticated_probe_unavailable;
    };
    return authenticated_probe_unavailable;
}

test "unavailable authentication cannot advertise a session" {
    try std.testing.expect(!authenticatedReady());
    try std.testing.expectEqualStrings("winhttp-pre-send-attestation-unavailable", authenticated_probe_unavailable);
}

test "already stopped diagnostic probe creates no request" {
    const stop = c.CreateEventW(null, 1, 1, null) orelse return error.EventUnavailable;
    defer _ = c.CloseHandle(stop);
    // Stop is checked before scratch is accessed, before WinHTTP is opened.
    var scratch: identity.Scratch = undefined;
    try std.testing.expectError(error.Stopped, probe.unauthenticated(stop, 1, 2000, &scratch, .{ .pid = 1, .created = 0 }, &.{}));
}

test "invalid configuration publishes no ready generation" {
    const Observer = struct {
        count: usize = 0,
        fn publish(raw: *anyopaque, ready: bool, generation: u64, reason: []const u8) void {
            const self: *@This() = @ptrCast(@alignCast(raw));
            std.debug.assert(!ready and generation == 0 and std.mem.eql(u8, reason, "invalid-backend-config"));
            self.count += 1;
        }
    };
    var observer: Observer = .{};
    run(null, &observer, Observer.publish, .{ .package_root = &.{}, .backend_path = &.{}, .probe_interval_ms = 2000, .timeout_ms = 2000 });
    try std.testing.expectEqual(@as(usize, 1), observer.count);
}

test {
    std.testing.refAllDecls(@This());
}
