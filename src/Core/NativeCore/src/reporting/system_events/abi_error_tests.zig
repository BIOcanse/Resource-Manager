const std = @import("std");
const abi = @import("abi.zig");
const native = @import("owner.zig");
const host_module = @import("../../telemetry/kernel_etw/host_allocator.zig");
const files = @import("store.zig");
extern "kernel32" fn GetEnvironmentVariableW([*:0]const u16, ?[*]u16, u32) callconv(.winapi) u32;
extern "kernel32" fn GetCurrentProcessId() callconv(.winapi) u32;
extern "kernel32" fn GetTickCount64() callconv(.winapi) u64;
var sequence: std.atomic.Value(u32) = .init(0);

// RuntimeRead: EventDriven("A focused test requests its registered fixture directory")
fn fixture() ![]const u8 {
    const a = std.testing.allocator;
    const name = std.unicode.utf8ToUtf16LeStringLiteral("RM_FREEZE_TEST_ROOT");
    const required = GetEnvironmentVariableW(name, null, 0);
    if (required == 0) return error.MissingRegisteredFreezeTestRoot;
    const buffer = try a.alloc(u16, required);
    defer a.free(buffer);
    const length = GetEnvironmentVariableW(name, buffer.ptr, @intCast(buffer.len));
    if (length == 0 or length >= buffer.len) return error.InvalidRegisteredFreezeTestRoot;
    const root = try std.unicode.utf16LeToUtf8Alloc(a, buffer[0..length]);
    defer a.free(root);
    const path = try std.fmt.allocPrint(a, "{s}\\abi-errors-{d}-{d}-{d}", .{ root, GetCurrentProcessId(), GetTickCount64(), sequence.fetchAdd(1, .monotonic) });
    errdefer a.free(path);
    if (try files.exists(a, path)) return error.TestDirectoryCollision;
    try files.mkdir(a, path);
    return path;
}
fn changed(_: ?*anyopaque, _: u32) callconv(.c) void {}
const Capture = struct {
    success: std.ArrayList(u8) = .empty,
    failure: std.ArrayList(u8) = .empty,
    fail_success: bool = false,
    calls: usize = 0,
    fn write(context: ?*anyopaque, bytes: [*]const u8, length: usize) callconv(.c) i32 {
        const self: *Capture = @ptrCast(@alignCast(context.?));
        self.calls += 1;
        if (self.fail_success) return -1;
        self.success.appendSlice(std.testing.allocator, bytes[0..length]) catch return -1;
        return 0;
    }
    fn errorWrite(context: ?*anyopaque, bytes: [*]const u8, length: usize) callconv(.c) i32 {
        const self: *Capture = @ptrCast(@alignCast(context.?));
        self.failure.appendSlice(std.testing.allocator, bytes[0..length]) catch return -1;
        return 0;
    }
    fn request(self: *Capture, owner: *native.Owner, op: u32, input: []const u8) i32 {
        self.success.clearRetainingCapacity();
        self.failure.clearRetainingCapacity();
        self.calls = 0;
        return abi.rm_freeze_request_with_error(owner, op, input.ptr, input.len, write, errorWrite, self);
    }
    fn expect(self: *Capture, reason: []const u8, detail: []const u8) !void {
        const parsed = try std.json.parseFromSlice(struct { @"error": []const u8, reasonCode: []const u8, detail: []const u8 }, std.testing.allocator, self.failure.items, .{});
        defer parsed.deinit();
        try std.testing.expectEqualStrings(reason, parsed.value.@"error");
        try std.testing.expectEqualStrings(reason, parsed.value.reasonCode);
        try std.testing.expectEqualStrings(detail, parsed.value.detail);
    }
    fn deinit(self: *Capture) void {
        self.success.deinit(std.testing.allocator);
        self.failure.deinit(std.testing.allocator);
    }
};
fn unavailable(_: ?*anyopaque, _: usize, _: *const fn (?*anyopaque, ?[*]u8, usize) callconv(.c) i32, _: ?*anyopaque) callconv(.c) i32 {
    return -1;
}

test "freeze report ABI actual requests return JSON failures for invalid missing conflicting unavailable and output failed" {
    const path = try fixture();
    defer std.testing.allocator.free(path);
    var host: host_module.TestHost = .{};
    var capture: Capture = .{};
    defer capture.deinit();
    {
        const owner = try native.Owner.create(path, host.callbacks(), changed, null);
        defer owner.destroy();
        try std.testing.expectEqual(@as(i32, 400), capture.request(owner, 8, "{"));
        try capture.expect("freeze-request-invalid-json", "UnexpectedEndOfInput");
        try std.testing.expectEqual(@as(i32, 404), capture.request(owner, 4, "missing"));
        try capture.expect("freeze-review-not-found", "NotFound");

        const recorder = try files.join(std.testing.allocator, path, "FreezeRecorder");
        defer std.testing.allocator.free(recorder);
        try files.mkdir(std.testing.allocator, recorder);
        const ring_path = try files.join(std.testing.allocator, recorder, "ring.bin");
        defer std.testing.allocator.free(ring_path);
        try files.atomic(std.testing.allocator, ring_path, "fixture");
        var settings = owner.settings;
        settings.freezeRecorder.slots += 1;
        const json = try std.json.Stringify.valueAlloc(std.testing.allocator, settings, .{});
        defer std.testing.allocator.free(json);
        try std.testing.expectEqual(@as(i32, 409), capture.request(owner, 8, json));
        try capture.expect("freeze-ring-migration-required", "RingMigrationRequired");

        owner.scratch = .{ .context = null, .run = unavailable };
        try std.testing.expectEqual(@as(i32, 503), capture.request(owner, 7, ""));
        try capture.expect("freeze-reports-workspace-unavailable", "WorkspaceUnavailable");
        owner.scratch = null;

        capture.fail_success = true;
        try std.testing.expectEqual(@as(i32, 500), capture.request(owner, 0, ""));
        try capture.expect("freeze-reports-failed", "OutputFailed");
        try std.testing.expectEqual(@as(usize, 1), capture.calls);
    }
    try std.testing.expect(host.empty());
}

test "freeze report ABI keeps partial success and original failure in separate streams" {
    const path = try fixture();
    defer std.testing.allocator.free(path);
    var host: host_module.TestHost = .{};
    var capture: Capture = .{};
    defer capture.deinit();
    {
        const owner = try native.Owner.create(path, host.callbacks(), changed, null);
        defer owner.destroy();
        const recorder = try files.join(std.testing.allocator, path, "FreezeRecorder");
        defer std.testing.allocator.free(recorder);
        try files.mkdir(std.testing.allocator, recorder);
        try files.mkdir(std.testing.allocator, owner.reviews_dir);
        const row_path = try files.join(std.testing.allocator, owner.reviews_dir, "broken.json");
        defer std.testing.allocator.free(row_path);
        try files.atomic(std.testing.allocator, row_path, "{");
        try std.testing.expectEqual(@as(i32, 400), capture.request(owner, 3, ""));
        try std.testing.expect(std.mem.startsWith(u8, capture.success.items, "{\"schemaVersion\":1,"));
        try std.testing.expect(std.mem.indexOf(u8, capture.success.items, "reasonCode") == null);
        try capture.expect("freeze-request-invalid-json", "UnexpectedEndOfInput");
        // A later request has no cached last failure and still yields valid JSON.
        try std.testing.expectEqual(@as(i32, 0), capture.request(owner, 7, ""));
        try std.testing.expectEqual(@as(usize, 0), capture.failure.items.len);
        const parsed = try std.json.parseFromSlice(std.json.Value, std.testing.allocator, capture.success.items, .{});
        defer parsed.deinit();
    }
    try std.testing.expect(host.empty());
}
