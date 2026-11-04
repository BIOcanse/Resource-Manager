const std = @import("std");
const root = @import("http_common");
const host = root.host;
const abi = root.abi;
const access = host.access;
const expect = std.testing.expect;
const equal = std.testing.expectEqual;
const string = std.testing.expectEqualStrings;
const address = [_]u8{ 127, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
const facts: host.io.Facts = .{
    .local = .{ .family = .ipv4, .address = address, .scope = 0, .port = 54321 },
    .remote = .{ .family = .ipv4, .address = address, .scope = 0, .port = 54322 },
};
const headers = [_]host.Header{ .{ .name = "Host", .value = "127.0.0.1:54321" }, .{ .name = "Content-Type", .value = "application/json" } };
const config: host.Config = .{ .startup = .{ .profile_id = "full", .allowed = 61 }, .normal_actions_allowed = true, .test_routes = true };
fn request() host.Request {
    return .{ .method = "POST", .target = "/__test/http/echo", .headers = &headers, .body = "{\"value\":\"hi 雪😀\",\"count\":9223372036854775807,\"enabled\":true}", .facts = facts, .caller = null, .caller_scope = null };
}
// Metadata leaf fixtures only. Real Windows identity is separately proved by
// the managed TCP fixture, never by pretending these fields are an OS token.
fn caller(admin: bool) access.Caller {
    return .{ .process_id = 42, .process_created_utc_ticks = 100, .windows_sid = std.unicode.utf8ToUtf16LeStringLiteral("S-1-test"), .windows_session_id = 1, .is_administrator = admin };
}
test "single admission ordinary administrator unknown and action switch matrix" {
    var output: [8192]u8 = undefined;
    for ([_]bool{ false, true }) |actions| for ([_]u8{ 0, 1, 2 }) |level| {
        var options = config;
        options.normal_actions_allowed = actions;
        var input = request();
        if (level != 0) {
            input.caller = caller(level == 2);
            input.caller_scope = &output;
        }
        const admission = try host.admit(options, input, &output);
        const response = try host.dispatchAdmitted(options, input, admission, &output);
        try equal(@as(u16, if (level == 0 or (level == 1 and !actions)) 403 else 200), response.status);
        if (response.status == 200) try expect(std.mem.indexOf(u8, response.body, "9223372036854775807") != null);
    };
}
test "production and read-only registration never contain test route" {
    var input = request();
    var output: [2048]u8 = undefined;
    var options = config;
    options.test_routes = false;
    try equal(@as(u16, 404), (try host.dispatch(options, input, &output)).status);
    options.test_routes = true;
    options.startup = .{ .profile_id = "normal-read-only", .allowed = 0 };
    try equal(@as(u16, 404), (try host.dispatch(options, input, &output)).status);
    input.method = "GET";
    input.target = "/api/runtime/capabilities";
    const response = try host.dispatch(options, input, &output);
    try equal(@as(u16, 200), response.status);
    try expect(std.mem.indexOf(u8, response.body, "\"readOnly\":true") != null);
    try equal(@as(usize, 1), host.product_routes.len);
}
test "exact route ownership raw target escaping method and API404" {
    for ([_][]const u8{ "/api/runtime/capabilities", "/API/runtime/capabilities/?a=1", "/api/runtime/%63apabilities", "/api/runtime/capabilities?next=http://ignored/", "http://127.0.0.1:54321/api/runtime/capabilities" }) |target|
        try expect(host.owns("GET", target));
    for ([_][]const u8{ "/api/runtime/capabilities-extra", "/api/runtime%2fcapabilities", "/api/runtime/capabilities//", "http://127.0.0.1:54321?redirect=/api/runtime/capabilities" }) |target|
        try expect(!host.owns("GET", target));
    try expect(!host.owns("HEAD", "/api/runtime/capabilities"));
    try expect(!host.owns("get", "/api/runtime/capabilities"));
    var input = request();
    input.method = "GET";
    input.target = "/api/missing";
    var output: [1024]u8 = undefined;
    try string("{\"error\":\"api-endpoint-not-found\"}", (try host.dispatch(config, input, &output)).body);
}
test "host origin duplicate and actual connection facts fail closed" {
    var input = request();
    var output: [1024]u8 = undefined;
    input.method = "GET";
    input.target = "/api/runtime/capabilities";
    for ([_][]const u8{ "http://127.0.0.1:54321", "HTTP://127.0.0.1:54321/" }) |origin| {
        input.headers = &.{ headers[0], .{ .name = "Origin", .value = origin } };
        try equal(@as(u16, 200), (try host.dispatch(config, input, &output)).status);
    }
    for ([_][]const u8{ "https://127.0.0.1:54321", "http://localhost:54321", "http://127.0.0.1:54321/x", "null", "http://127.0.0.1:54321/?a", "http://user@127.0.0.1:54321" }) |origin| {
        input.headers = &.{ headers[0], .{ .name = "Origin", .value = origin } };
        try equal(@as(u16, 403), (try host.dispatch(config, input, &output)).status);
    }
    input.headers = &.{ headers[0], headers[0] };
    try equal(@as(u16, 403), (try host.dispatch(config, input, &output)).status);
    input.headers = &headers;
    input.facts.remote.address[0] = 10;
    try equal(@as(u16, 403), (try host.dispatch(config, input, &output)).status);
}
test "typed binding exact integer null unknown duplicate media errors and cancellation" {
    var input = request();
    input.caller = caller(true);
    input.caller_scope = &input;
    var output: [8192]u8 = undefined;
    for ([_][]const u8{ "", "null", "{", "{\"count\":9223372036854775808}", "{\"enabled\":\"true\"}", "{\"value\":[65,66]}", "{\"count\":1.0}", "{\"count\":1e0}" }) |body| {
        input.body = body;
        try equal(@as(u16, 400), (try host.dispatch(config, input, &output)).status);
    }
    input.body = "{\"unknown\":[1,{\"nested\":true}],\"count\":1,\"count\":\"2\"}";
    try string("{\"value\":null,\"count\":2,\"enabled\":null}", (try host.dispatch(config, input, &output)).body);
    input.headers = &.{ headers[0], .{ .name = "Content-Type", .value = "text/plain" } };
    try equal(@as(u16, 415), (try host.dispatch(config, input, &output)).status);
    input.cancelled = true;
    try std.testing.expectError(error.Cancelled, host.dispatch(config, input, &output));
}
test "family registration capability filter and errors remain shared" {
    const family_routes = [_]host.Route{.{ .method = "GET", .template = "/api/family", .required_capabilities = 1, .requirement = .administrator, .handler = .{ .family = .{ .capacity = .{ .state_bytes = 0, .output_bytes = 128 }, .can_suspend = false, .begin = family, .step = noStep, .release = noRelease } } }};
    var options = config;
    options.family_routes = &family_routes;
    var input = request();
    input.method = "GET";
    input.target = "/api/family";
    input.caller = caller(true);
    input.caller_scope = &input;
    var output: [1024]u8 = undefined;
    var operation = host.Operation.init(&.{}, .{});
    const admission = try host.admit(options, input, &output);
    const result = try operation.start(options, input, admission, &output);
    try equal(@as(u16, 500), result.output.status);
    _ = try operation.step(.writable, &output);
    try operation.deinit();
    options.startup.allowed = 0;
    try equal(@as(u16, 404), (try host.dispatch(options, input, &output)).status);
}
fn family(_: ?*anyopaque, _: host.Request, _: *host.Call, _: []u8) !host.Progress {
    return error.DomainFailure;
}
fn noStep(_: ?*anyopaque, _: *host.Call, _: host.Event, _: []u8) !host.Progress {
    return error.UnexpectedResume;
}
fn noRelease(_: ?*anyopaque, _: *host.Call) void {}
test "ABI bad layout cancellation output short and all exports are callable" {
    try equal(@as(i32, 1), abi.rm_http_common_dispatch(null, null));
    try equal(@as(i32, 1), abi.rm_http_common_owns("GET", 3, "/api/runtime/capabilities", 25));
    try expect(abi.rm_http_common_output_bound(4, 2, 0) > host.outputBound(4));
    try equal(@as(usize, 0), abi.rm_http_common_output_bound(std.math.maxInt(usize), 0, 0));
}

const TestOwner = struct {
    starts: usize = 0,
    steps: usize = 0,
    releases: usize = 0,
    cancels: usize = 0,
    wakes: usize = 0,
    ready: bool = false,
    remaining: usize = 0,
    cursor: usize = 0,
    fn from(context: ?*anyopaque) *TestOwner {
        return @ptrCast(@alignCast(context.?));
    }
    fn wake(context: ?*anyopaque, _: u64) void {
        from(context).wakes += 1;
    }
    fn release(context: ?*anyopaque, _: *host.Call) void {
        from(context).releases += 1;
    }
    fn begin(context: ?*anyopaque, input: host.Request, call: *host.Call, _: []u8) !host.Progress {
        const self = from(context);
        self.starts += 1;
        // Typed parameter preparation is synchronous in the live caller scope;
        // only the copied value, never Request/caller/source pointers, survives.
        const id = (try host.parameter(input, "id", call.state[1..])) orelse return error.MissingParameter;
        call.state[0] = @intCast(id.len);
        return .pending;
    }
    fn step(context: ?*anyopaque, call: *host.Call, event: host.Event, output: []u8) !host.Progress {
        const self = from(context);
        self.steps += 1;
        if (event == .cancel) self.cancels += 1;
        if (call.isCancelled()) return if (self.ready) .done else .pending;
        if (!self.ready) return .pending;
        const id = call.state[1 .. 1 + call.state[0]];
        @memcpy(output[0..id.len], id);
        return .{ .output = .{ .status = 202, .body = output[0..id.len], .headers = &.{
            .{ .name = "Location", .value = "/__test/receipt/1" },
            .{ .name = "Cache-Control", .value = "no-store, no-cache, must-revalidate" },
            .{ .name = "Pragma", .value = "no-cache" },
            .{ .name = "Expires", .value = "0" },
        } } };
    }
    fn failCancel(context: ?*anyopaque, _: *host.Call, event: host.Event, _: []u8) !host.Progress {
        if (event != .cancel) return .pending;
        from(context).cancels += 1;
        // Test owner has ended work before surfacing its terminal failure.
        return error.OwnerCancelFailed;
    }
    fn noOutput(context: ?*anyopaque, _: host.Request, _: *host.Call, _: []u8) !host.Progress {
        from(context).starts += 1;
        return .done;
    }
    fn streamBegin(context: ?*anyopaque, _: host.Request, _: *host.Call, output: []u8) !host.Progress {
        from(context).starts += 1;
        return stream(context, output);
    }
    fn streamStep(context: ?*anyopaque, call: *host.Call, event: host.Event, output: []u8) !host.Progress {
        const self = from(context);
        self.steps += 1;
        if (event == .cancel) {
            self.cancels += 1;
            return .done;
        }
        if (call.isCancelled()) return .done;
        if (event != .writable) return error.UnexpectedResume;
        return stream(context, output);
    }
    fn stream(context: ?*anyopaque, output: []u8) !host.Progress {
        const self = from(context);
        const length = @min(self.remaining, output.len);
        for (output[0..length], 0..) |*byte, index| byte.* = @intCast((self.cursor + index) % 251);
        self.cursor += length;
        self.remaining -= length;
        return .{ .output = .{ .status = 200, .body = output[0..length], .more = self.remaining != 0 } };
    }
};
fn ownerRoute(owner: *TestOwner) host.Route {
    return .{ .method = "GET", .template = "/__test/items/{id}", .requirement = .normal_read, .handler = .{ .family = .{
        .context = owner,
        .capacity = .{ .state_bytes = 64, .output_bytes = 128 },
        .begin = TestOwner.begin,
        .step = TestOwner.step,
        .release = TestOwner.release,
    } } };
}
test "template ownership parameter decoding and literal precedence use the admitted route" {
    var owner: TestOwner = .{};
    var routes = [_]host.Route{ ownerRoute(&owner), ownerRoute(&owner) };
    routes[1].template = "/__test/items/catalog";
    var options = config;
    options.family_routes = &routes;
    var input = request();
    input.method = "GET";
    for ([_][]const u8{ "/__test/items/a", "/__TEST/items/a%20b/?q=1", "/__test/items/a%2Fb", "http://127.0.0.1:54321/__test/items/%E9%9B%AA" }) |target| {
        try expect(host.ownsRoutes(&routes, "GET", target));
    }
    for ([_][]const u8{ "/__test/items/", "/__test/items/a/b", "/__test/items/a%", "/__test/items/a%xx" }) |target|
        try expect(!host.ownsRoutes(&routes, "GET", target));
    try expect(!host.ownsRoutes(&routes, "HEAD", "/__test/items/a"));
    input.target = "/__test/items/catalog";
    try string(routes[1].template, host.lookup(options, input).?.template);
    input.target = "/__test/items/a%20b%2Fc+z";
    input.route_template = routes[0].template;
    var scratch: [64]u8 = undefined;
    try string("a%20b%2Fc+z", host.parameterRaw(input, "ID").?);
    try string("a b%2Fc+z", (try host.parameter(input, "id", &scratch)).?);
    try expect(try host.parameter(input, "other", &scratch) == null);
}
test "deferred typed source completes once with copied parameters and response headers" {
    var owner: TestOwner = .{};
    const routes = [_]host.Route{ownerRoute(&owner)};
    var options = config;
    options.family_routes = &routes;
    var input = request();
    input.method = "GET";
    var target = "/__test/items/a%20b".*;
    input.target = &target;
    input.caller = caller(false);
    input.caller_scope = &input;
    var state: [64]u8 align(@alignOf(usize)) = undefined;
    var output: [256]u8 = undefined;
    var operation = host.Operation.init(&state, .{ .context = &owner, .post = TestOwner.wake });
    const admission = try host.admit(options, input, &output);
    try expect(try operation.start(options, input, admission, &output) == .pending);
    try std.testing.expectError(error.OperationNotDrained, operation.deinit());
    @memset(&target, '?'); // borrowed target does not survive the first call
    owner.ready = true;
    operation.call.wake.notify();
    const progress = try operation.step(.ready, &output);
    try equal(@as(u16, 202), progress.output.status);
    try string("a b", progress.output.body);
    try string("Location", progress.output.headers[0].name);
    try string("/__test/receipt/1", progress.output.headers[0].value);
    try equal(@as(usize, 4), progress.output.headers.len);
    try std.testing.expectError(error.OutputStillBorrowed, operation.step(.ready, &output));
    try expect(try operation.step(.writable, &output) == .done);
    try operation.deinit();
    try operation.deinit();
    try equal(@as(usize, 1), owner.starts);
    try equal(@as(usize, 1), owner.steps);
    try equal(@as(usize, 1), owner.releases);
    try equal(@as(usize, 1), owner.wakes);
}
test "live cancellation waits original owner acknowledgement and releases once" {
    var owner: TestOwner = .{};
    const routes = [_]host.Route{ownerRoute(&owner)};
    var options = config;
    options.family_routes = &routes;
    var input = request();
    input.method = "GET";
    input.target = "/__test/items/one";
    input.caller = caller(false);
    input.caller_scope = &input;
    var state: [64]u8 align(@alignOf(usize)) = undefined;
    var output: [256]u8 = undefined;
    var operation = host.Operation.init(&state, .{ .context = &owner, .post = TestOwner.wake });
    _ = try operation.start(options, input, try host.admit(options, input, &output), &output);
    try expect(try operation.cancel(&output) == .pending);
    try expect(operation.call.isCancelled());
    try expect(try operation.cancel(&output) == .pending);
    try equal(@as(usize, 1), owner.cancels);
    try std.testing.expectError(error.OperationNotDrained, operation.deinit());
    owner.ready = true;
    operation.call.wake.notify();
    try expect(try operation.step(.ready, &output) == .done);
    try operation.deinit();
    try equal(@as(usize, 1), owner.starts);
    try equal(@as(usize, 1), owner.releases);
}
test "explicit family capacity and wake fail before any owner side effect" {
    var owner: TestOwner = .{};
    const routes = [_]host.Route{ownerRoute(&owner)};
    var options = config;
    options.family_routes = &routes;
    var input = request();
    input.method = "GET";
    input.target = "/__test/items/one";
    input.caller = caller(false);
    input.caller_scope = &input;
    var state: [64]u8 align(@alignOf(usize)) = undefined;
    var output: [256]u8 = undefined;
    const admission = try host.admit(options, input, &output);
    var no_wake = host.Operation.init(&state, .{});
    try std.testing.expectError(error.CompletionSinkRequired, no_wake.start(options, input, admission, &output));
    var short = host.Operation.init(state[0..32], .{ .context = &owner, .post = TestOwner.wake });
    try std.testing.expectError(error.OutputLimit, short.start(options, input, admission, &output));
    var short_output = host.Operation.init(&state, .{ .context = &owner, .post = TestOwner.wake });
    try std.testing.expectError(error.OutputLimit, short_output.start(options, input, admission, output[0..64]));
    try equal(@as(usize, 0), owner.starts);
}
test "terminal owner failure during cancellation still permits exactly one release" {
    var owner: TestOwner = .{};
    var routes = [_]host.Route{ownerRoute(&owner)};
    routes[0].handler.family.step = TestOwner.failCancel;
    var options = config;
    options.family_routes = &routes;
    var input = request();
    input.method = "GET";
    input.target = "/__test/items/one";
    input.caller = caller(false);
    input.caller_scope = &input;
    var state: [64]u8 align(@alignOf(usize)) = undefined;
    var output: [256]u8 = undefined;
    var operation = host.Operation.init(&state, .{ .context = &owner, .post = TestOwner.wake });
    _ = try operation.start(options, input, try host.admit(options, input, &output), &output);
    try std.testing.expectError(error.OwnerCancelFailed, operation.cancel(&output));
    try expect(operation.done());
    try operation.deinit();
    try equal(@as(usize, 1), owner.cancels);
    try equal(@as(usize, 1), owner.releases);
}
test "done without an explicit response cannot silently become HTTP200" {
    var owner: TestOwner = .{};
    var routes = [_]host.Route{ownerRoute(&owner)};
    routes[0].handler.family.begin = TestOwner.noOutput;
    routes[0].handler.family.can_suspend = false;
    var options = config;
    options.family_routes = &routes;
    var input = request();
    input.method = "GET";
    input.target = "/__test/items/one";
    input.caller = caller(false);
    input.caller_scope = &input;
    var state: [64]u8 align(@alignOf(usize)) = undefined;
    var output: [256]u8 = undefined;
    var operation = host.Operation.init(&state, .{});
    try std.testing.expectError(error.FamilyEndedWithoutResponse, operation.start(options, input, try host.admit(options, input, &output), &output));
    try expect(operation.done());
    try operation.deinit();
    try equal(@as(usize, 1), owner.releases);
    try host.validateResult(.{ .status = 204, .body = "", .content_type = null });
}
test "resumable codec emits over four MiB without replay or overwriting a borrowed segment" {
    const total = 4 * 1024 * 1024 + 173;
    var owner: TestOwner = .{ .remaining = total };
    var routes = [_]host.Route{ownerRoute(&owner)};
    routes[0].handler.family = .{ .context = &owner, .capacity = .{ .state_bytes = 0, .output_bytes = 4096 }, .begin = TestOwner.streamBegin, .step = TestOwner.streamStep, .release = TestOwner.release };
    var options = config;
    options.family_routes = &routes;
    var input = request();
    input.method = "GET";
    input.target = "/__test/items/large";
    input.caller = caller(false);
    input.caller_scope = &input;
    var output: [4096]u8 = undefined;
    var operation = host.Operation.init(&.{}, .{ .context = &owner, .post = TestOwner.wake });
    var progress = try operation.start(options, input, try host.admit(options, input, &output), &output);
    var consumed: usize = 0;
    while (progress == .output) {
        for (progress.output.body, 0..) |byte, index| try equal(@as(u8, @intCast((consumed + index) % 251)), byte);
        consumed += progress.output.body.len;
        progress = try operation.step(.writable, &output);
    }
    try expect(progress == .done);
    try equal(@as(usize, total), consumed);
    try equal(@as(usize, 1), owner.starts);
    try operation.deinit();
    try equal(@as(usize, 1), owner.releases);
}
test "response metadata refuses header injection and family owned framing" {
    try host.validateResult(.{ .status = 202, .body = "", .headers = &.{.{ .name = "Location", .value = "/receipt/1" }} });
    for ([_]host.Header{
        .{ .name = "Location", .value = "/receipt\r\nInjected: true" },
        .{ .name = "Bad Name", .value = "x" },
        .{ .name = "Content-Length", .value = "1" },
        .{ .name = "Transfer-Encoding", .value = "chunked" },
        .{ .name = "Content-Type", .value = "application/json" },
    }) |item| try std.testing.expectError(error.InvalidResponse, host.validateResult(.{ .status = 200, .body = "", .headers = &.{item} }));
}
fn abiWake(_: ?*anyopaque) callconv(.c) void {}
test "temporary ABI reserves common denial bytes for a tiny family window before source open" {
    var owner: TestOwner = .{};
    var route = ownerRoute(&owner);
    var storage: [2048]u8 align(16) = undefined;
    var output: [128]u8 = undefined;
    const input_headers = [_]abi.Header{.{ .name = .{ .ptr = "Host", .len = 4 }, .value = .{ .ptr = "127.0.0.1:54321", .len = 15 } }};
    for ([_]usize{ 1, 37 }) |window| {
        route.handler.family.capacity.output_bytes = window;
        var capacity: abi.FamilyCapacity = undefined;
        try equal(@as(i32, 0), abi.rm_http_common_family_capacity(&route, 1, &capacity));
        try equal(@as(usize, host.errorOutputBound()), capacity.output_bytes);
        try expect(capacity.storage_bytes <= storage.len);
        try equal(@as(i32, 0), abi.rm_http_common_family_init(&storage, storage.len, &route, 1, null, abiWake));
        var input = std.mem.zeroes(abi.Request);
        input.version = 1;
        input.struct_size = @sizeOf(abi.Request);
        input.method = .{ .ptr = "GET", .len = 3 };
        input.target = .{ .ptr = "/__test/items/one", .len = "/__test/items/one".len };
        input.scheme = .{ .ptr = "http", .len = 4 };
        input.profile_id = .{ .ptr = "full", .len = 4 };
        input.allowed_capabilities = 61;
        input.normal_actions_allowed = 1;
        input.headers = &input_headers;
        input.header_count = 1;
        input.local_family = 4;
        input.remote_family = 4;
        input.local_address = address;
        input.remote_address = address;
        input.local_port = 54321;
        input.remote_port = 54322;
        input.output = &output;
        input.output_capacity = capacity.output_bytes;
        var result: abi.FamilyProgress = undefined;
        try equal(@as(i32, 0), abi.rm_http_common_family_admit(&storage, &input, &route, &result));
        try equal(@as(u32, 403), result.status);
        try string("{\"error\":\"local-windows-caller-required\"}", result.body.ptr.?[0..result.body.len]);
        try equal(@as(usize, 0), owner.starts);
        try equal(@as(i32, 0), abi.rm_http_common_family_step(&storage, &route, 1, &output, capacity.output_bytes, &result));
        try equal(@as(u32, 2), result.kind);
        try equal(@as(i32, 0), abi.rm_http_common_family_release(&storage, &route));
    }
}
