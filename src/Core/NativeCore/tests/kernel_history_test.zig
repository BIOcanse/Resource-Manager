const std = @import("std");
const root = @import("kernel");
const command = root.command;
const host = root.host;
const equal = std.testing.expectEqual;

const Owner = struct {
    sequence: u32 = 0,
    fail: u32 = 0,
    fn restore(context: ?*anyopaque) callconv(.c) i32 {
        const self: *Owner = @ptrCast(@alignCast(context.?));
        self.sequence = self.sequence * 10 + 1;
        return @intFromBool(self.fail == 1);
    }
    fn mutate(context: ?*anyopaque, count: *i32) callconv(.c) i32 {
        const self: *Owner = @ptrCast(@alignCast(context.?));
        self.sequence = self.sequence * 10 + 2;
        count.* = 7;
        return @intFromBool(self.fail == 2);
    }
    fn publish(context: ?*anyopaque) callconv(.c) i32 {
        const self: *Owner = @ptrCast(@alignCast(context.?));
        self.sequence = self.sequence * 10 + 3;
        return @intFromBool(self.fail == 3);
    }
    fn ports(self: *Owner) command.Owner {
        return .{ .context = self, .restore = restore, .mutate = mutate, .publish = publish };
    }
};
test "one restore mutate publish and exact count" {
    var owner: Owner = .{};
    try equal(@as(i32, 7), try command.invoke(owner.ports()));
    try equal(@as(u32, 123), owner.sequence);
}
test "every callback failure stops before the next effect" {
    for ([_]u32{ 1, 2, 3 }, [_]u32{ 1, 12, 123 }) |failure, sequence| {
        var owner: Owner = .{ .fail = failure };
        try std.testing.expectError(error.CallbackFailed, command.invoke(owner.ports()));
        try equal(sequence, owner.sequence);
    }
}
test "empty native store clears repeatedly without invoking persistence" {
    const region = try std.testing.allocator.alignedAlloc(u8, .@"8", root.items.requiredBytes(4));
    defer std.testing.allocator.free(region);
    const store = try root.items.init(region, 4);
    const Save = struct {
        fn save(_: ?*anyopaque) callconv(.c) i32 {
            return 1;
        }
    };
    try equal(@as(i32, 0), try command.clear(store, null, Save.save));
    try equal(@as(i32, 0), try command.clear(store, null, Save.save));
}
const Source = struct {
    calls: u32 = 0,
    fn read(context: ?*anyopaque, event: u32, _: ?*anyopaque, _: *const fn (?*anyopaque) callconv(.c) void, count: *i32) callconv(.c) i32 {
        const self: *Source = @ptrCast(@alignCast(context.?));
        self.calls += 1;
        count.* = 0;
        return if (event == 0) 0 else 1;
    }
};
test "kernel descriptor remains normal action in both startup graphs and pending cancels drain" {
    var source: Source = .{};
    const ports: root.http.Source = .{ .context = &source, .read = Source.read };
    const routes = [_]host.Route{root.http.route(&ports)};
    var output: [256]u8 = undefined;
    for ([_]u32{ 0, 63 }) |allowed| {
        const config: host.Config = .{ .startup = .{ .profile_id = "test", .allowed = allowed }, .normal_actions_allowed = true, .family_routes = &routes };
        const headers = [_]host.Header{.{ .name = "Host", .value = "127.0.0.1:12345" }};
        const request: host.Request = .{ .method = "POST", .target = "/API/KERNEL-LEAKS/HISTORY/CLEAR/?x=1", .headers = &headers, .body = "ignored", .caller_scope = @ptrFromInt(1), .caller = .{ .process_id = 1, .process_created_utc_ticks = 1, .windows_session_id = 0, .is_administrator = false, .windows_sid = &.{'x'} }, .facts = .{ .local = .{ .family = .ipv4, .address = .{ 127, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, .scope = 0, .port = 12345 }, .remote = .{ .family = .ipv4, .address = .{ 127, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, .scope = 0, .port = 23456 } } };
        const admission = try host.admit(config, request, &output);
        try std.testing.expect(admission.rejection == null);
        var op = host.Operation.init(&.{}, .{ .post = struct {
            fn post(_: ?*anyopaque, _: u64) void {}
        }.post });
        try std.testing.expect((try op.start(config, request, admission, &output)) == .pending);
        try std.testing.expect((try op.step(.cancel, &output)) == .done);
        try op.deinit();
        var disabled = config;
        disabled.normal_actions_allowed = false;
        try equal(@as(u16, 403), (try host.admit(disabled, request, &output)).rejection.?.status);
        var admin = request;
        var caller = request.caller.?;
        caller.is_administrator = true;
        admin.caller = caller;
        try std.testing.expect((try host.admit(disabled, admin, &output)).rejection == null);
        var unknown = request;
        unknown.caller = null;
        unknown.caller_scope = null;
        try equal(@as(u16, 403), (try host.admit(config, unknown, &output)).rejection.?.status);
        var head = request;
        head.method = "HEAD";
        try equal(@as(u16, 404), (try host.admit(config, head, &output)).rejection.?.status);
    }
    try equal(@as(u32, 6), source.calls);
}
