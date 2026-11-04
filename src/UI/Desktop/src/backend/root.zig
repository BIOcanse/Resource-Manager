const std = @import("std");
pub const c = @import("c.zig").win;
pub const keys = @import("key_reader.zig");
pub const identity = @import("identity.zig");
pub const probe = @import("probe.zig");
pub const transport = @import("transport.zig");
pub const channel = @import("channel.zig");

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
const Workspace = struct { reader: keys.Reader, identity: identity.Scratch, transport: transport.Scratch };

/// Long-lived block entry. Owns the single in-flight HTTP probe and returns on stop.
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
    var session: Session = .{};
    while (c.WaitForSingleObject(stop, 0) == c.WAIT_TIMEOUT) {
        const expected = attempt(workspace, stop, config) catch |err| {
            const reason = @errorName(err);
            const was_ready = session.ready;
            session.ready = false;
            if (was_ready or !std.mem.eql(u8, last_reason, reason)) {
                publish(context, false, session.generation, reason);
                last_reason = reason;
            }
            if (c.WaitForSingleObject(stop, config.probe_interval_ms) != c.WAIT_TIMEOUT) break;
            continue;
        };
        if (c.WaitForSingleObject(stop, 0) != c.WAIT_TIMEOUT) break;
        if (session.ready and !std.meta.eql(session.process, expected)) {
            session.ready = false;
            publish(context, false, session.generation, "ServiceChanged");
        }
        const changed = session.accept(expected) catch |err| {
            publish(context, false, session.generation, @errorName(err));
            return;
        };
        if (changed) {
            const reason = "ready";
            publish(context, true, session.generation, reason);
            last_reason = reason;
        }
        if (c.WaitForSingleObject(stop, config.probe_interval_ms) != c.WAIT_TIMEOUT) break;
    }
    publish(context, false, session.generation, "stopped");
}

const Session = struct {
    ready: bool = false,
    generation: u64 = 0,
    process: identity.Identity = .{ .pid = 0, .created = 0 },
    fn accept(self: *Session, expected: identity.Identity) !bool {
        if (self.ready and std.meta.eql(self.process, expected)) return false;
        if (self.generation == std.math.maxInt(i64)) return error.GenerationExhausted;
        self.generation += 1;
        self.process = expected;
        self.ready = true;
        return true;
    }
};

fn attempt(workspace: *Workspace, stop: c.HANDLE, config: Config) !identity.Identity {
    const expected = try identity.capture(&workspace.identity, config.backend_path, config.service_name);
    var key = (try workspace.reader.read(config.package_root, stop)) orelse return error.DefaultKeyUnavailable;
    defer key.clear();
    try transport.authenticated(stop, config.port, config.timeout_ms, &workspace.transport, &workspace.identity, expected, config.backend_path, config.service_name, key.value());
    return expected;
}

/// The product channel opener. Every open captures the configured backend's
/// identity, reads the default key and attests the connected peer before the
/// first byte, exactly like the health probe. One Link per block thread: its
/// scratch is not shared. Callers own the memory (VirtualAlloc per run).
pub const Link = struct {
    package_root: []const u16,
    backend_path: []const u16,
    service_name: [:0]const u16,
    port: u16,
    timeout_ms: u32,
    reader: keys.Reader = .{},
    identity: identity.Scratch = undefined,

    pub fn create(config: Config) !*Link {
        const memory = c.VirtualAlloc(null, @sizeOf(Link), c.MEM_RESERVE | c.MEM_COMMIT, c.PAGE_READWRITE) orelse return error.OutOfMemory;
        const self: *Link = @ptrCast(@alignCast(memory));
        self.package_root = config.package_root;
        self.backend_path = config.backend_path;
        self.service_name = config.service_name;
        self.port = config.port;
        self.timeout_ms = config.timeout_ms;
        self.reader = .{};
        return self;
    }
    pub fn destroy(self: *Link) void {
        _ = c.VirtualFree(self, 0, c.MEM_RELEASE);
    }
    pub fn opener(self: *Link) channel.Opener {
        return .{ .context = self, .open_fn = open };
    }
    fn open(raw: *anyopaque, target: *channel.Channel, stop: c.HANDLE) anyerror!void {
        const self: *Link = @ptrCast(@alignCast(raw));
        const expected = try identity.capture(&self.identity, self.backend_path, self.service_name);
        var key = (try self.reader.read(self.package_root, stop)) orelse return error.DefaultKeyUnavailable;
        defer key.clear();
        try target.open(stop, self.port, self.timeout_ms, transport.Gate{ .scratch = &self.identity, .expected = expected, .path = self.backend_path, .service_name = self.service_name }, key.value());
    }
};

test "health generation remains stable until loss or process replacement" {
    var session: Session = .{};
    const first: identity.Identity = .{ .pid = 7, .created = 11 };
    try std.testing.expect(try session.accept(first));
    try std.testing.expectEqual(@as(u64, 1), session.generation);
    try std.testing.expect(!try session.accept(first));
    session.ready = false;
    try std.testing.expect(try session.accept(first));
    try std.testing.expectEqual(@as(u64, 2), session.generation);
    try std.testing.expect(try session.accept(.{ .pid = 7, .created = 12 }));
    try std.testing.expectEqual(@as(u64, 3), session.generation);
    session.ready = false;
    session.generation = std.math.maxInt(i64);
    try std.testing.expectError(error.GenerationExhausted, session.accept(first));
    try std.testing.expect(!session.ready);
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

test "health generation stays absent when the configured service is unavailable" {
    const Observer = struct {
        stop: c.HANDLE,
        count: usize = 0,
        fn publish(raw: *anyopaque, ready: bool, generation: u64, reason: []const u8) void {
            const self: *@This() = @ptrCast(@alignCast(raw));
            std.debug.assert(!ready and generation == 0);
            if (self.count == 0) {
                std.debug.assert(std.mem.eql(u8, reason, "ServiceUnavailable") or std.mem.eql(u8, reason, "ServiceManagerUnavailable"));
                _ = c.SetEvent(self.stop);
            } else std.debug.assert(std.mem.eql(u8, reason, "stopped"));
            self.count += 1;
        }
    };
    const stop = c.CreateEventW(null, 1, 0, null) orelse return error.EventUnavailable;
    defer _ = c.CloseHandle(stop);
    var observer: Observer = .{ .stop = stop };
    run(stop, &observer, Observer.publish, .{
        .package_root = std.unicode.utf8ToUtf16LeStringLiteral("C:\\"),
        .backend_path = std.unicode.utf8ToUtf16LeStringLiteral("C:\\nonexistent-backend-fixture.exe"),
        .service_name = std.unicode.utf8ToUtf16LeStringLiteral("ResourceManager.Desktop.NonexistentTestService"),
        .probe_interval_ms = 2000,
        .timeout_ms = 2000,
    });
    try std.testing.expectEqual(@as(usize, 2), observer.count);
}

test {
    std.testing.refAllDecls(@This());
}
