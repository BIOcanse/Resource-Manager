const std = @import("std");
const runtime = @import("runtime").runtime;
const host = runtime.host;
test "runtime query binding preserves missing, empty, duplicate, escaped and trimmed values" {
    try std.testing.expect(!try runtime.bindRefresh("/api/browser-runtimes"));
    try std.testing.expectError(error.InvalidBoolean, runtime.bindRefresh("/api/browser-runtimes?refresh="));
    try std.testing.expectError(error.InvalidBoolean, runtime.bindRefresh("/api/browser-runtimes?refresh"));
    try std.testing.expect(try runtime.bindRefresh("/?re%66resh=+%54rue%00"));
    try std.testing.expect(try runtime.bindRefresh("/?refresh=%E3%80%80true%C2%A0"));
    try std.testing.expect(!try runtime.bindRefresh("/?Refresh=fALSe"));
    try std.testing.expectError(error.InvalidBoolean, runtime.bindRefresh("/?refresh=true&REFRESH=true"));
    try std.testing.expectError(error.InvalidBoolean, runtime.bindRefresh("/?refresh=+"));
    try std.testing.expectError(error.InvalidBoolean, runtime.bindRefresh("/?refresh=tr+ue"));
    var challenge: [64]u8 = undefined;
    try std.testing.expect(!runtime.bindChallenge("/?challenge=", &challenge));
    try std.testing.expect(runtime.bindChallenge("/?CHALLENGE=" ++ "Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01", &challenge));
    try std.testing.expectEqualStrings("Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01Ab01", &challenge);
    try std.testing.expect(!runtime.bindChallenge("/?challenge=" ++ "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" ++ "&Challenge=" ++ "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", &challenge));
}
test "runtime registration preserves the full and read-only graph, HEAD and literal boundaries" {
    const config: host.Config = .{ .startup = .{ .profile_id = "normal-read-only", .allowed = 0 }, .normal_actions_allowed = false };
    for (runtime.paths, 0..) |path, index| {
        const route = runtime.route(@fromBackingInt(@as(u32, @intCast(index))), null, runtime.minimum_window);
        try std.testing.expect(host.matchRank(route, "GET", path) > 0);
        try std.testing.expectEqual(@as(u32, 0), route.required_capabilities);
        try std.testing.expectEqual(@as(u32, 0), host.matchRank(route, "HEAD", path));
        try std.testing.expectEqual(if (index == 0) host.access.Requirement.anonymous else .normal_read, route.requirement);
    }
    _ = config;
}
test "runtime health has no source, content type, body or framing entity" {
    var state: [@sizeOf(runtime.State)]u8 align(@alignOf(runtime.State)) = undefined;
    var output: [runtime.minimum_window]u8 = undefined;
    var operation = host.Operation.init(&state, .{ .post = post });
    var route = runtime.route(.health, null, output.len);
    const request = requestFor(runtime.paths[0]);
    const config: host.Config = .{ .startup = .{ .profile_id = "full", .allowed = 0x3d }, .normal_actions_allowed = false, .family_routes = @as([*]const host.Route, @ptrCast(&route))[0..1] };
    const admission = try host.admit(config, request, &output);
    const result = try operation.start(config, request, admission, &output);
    try std.testing.expectEqual(@as(u16, 204), result.output.status);
    try std.testing.expectEqual(@as(usize, 0), result.output.body.len);
    try std.testing.expect(result.output.content_type == null);
    try std.testing.expect(!result.output.more);
    _ = try operation.step(.writable, &output);
    try operation.deinit();
}
fn post(_: ?*anyopaque, _: u64) void {}
fn requestFor(target: []const u8) host.Request {
    const address: [16]u8 = .{ 127, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    return .{ .method = "GET", .target = target, .headers = &.{.{ .name = "Host", .value = "127.0.0.1:12345" }}, .body = &.{}, .caller = null, .caller_scope = null, .facts = .{ .local = .{ .family = .ipv4, .address = address, .port = 12345, .scope = 0 }, .remote = .{ .family = .ipv4, .address = address, .port = 12346, .scope = 0 } } };
}
