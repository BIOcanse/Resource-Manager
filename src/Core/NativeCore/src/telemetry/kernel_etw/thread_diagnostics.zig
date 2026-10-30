const std = @import("std");

pub const Times = struct { birth: i64, exit: i64 = 0, kernel: i64 = 0, user: i64 = 0 };
pub const Name = struct { text: ?[]u16 = null, hresult: ?i32 = null };
pub const Visit = *const fn (*anyopaque, u32, i64, ?u64) anyerror!void;
/// Windows handles and OS-owned description strings remain behind this narrow
/// seam; all retained metadata is allocated by the owning bounded allocator.
pub const Source = struct {
    context: ?*anyopaque = null,
    open_process: *const fn (?*anyopaque, u32, bool) ?*anyopaque,
    process_birth: *const fn (?*anyopaque, *anyopaque) ?i64,
    process_running: *const fn (?*anyopaque, *anyopaque) bool,
    open_thread: *const fn (?*anyopaque, u32, u32) ?*anyopaque,
    times: *const fn (?*anyopaque, *anyopaque) ?Times,
    name: *const fn (?*anyopaque, *anyopaque, std.mem.Allocator, u32) anyerror!Name,
    start: *const fn (?*anyopaque, *anyopaque) ?u64,
    module: *const fn (?*anyopaque, *anyopaque, u64, std.mem.Allocator, std.mem.Allocator, u32, u32) anyerror!?[]u16,
    inventory: *const fn (?*anyopaque, u32, i64, std.mem.Allocator, usize, Visit, *anyopaque) anyerror!void,
    close: *const fn (?*anyopaque, *anyopaque) void,
    now: *const fn (?*anyopaque) i64,
    last_error: *const fn (?*anyopaque) i32,
};

pub const ThreadView = extern struct {
    thread_instance: i64 = 0, tid: u32 = 0, flags: u32 = 0,
    birth: i64 = 0, start_address: u64 = 0,
    read_from: i64 = 0, read_through: i64 = 0, kernel_delta: i64 = 0, user_delta: i64 = 0,
    description_hresult: i32 = 0, native_error: i32 = 0, start_source: u32 = 0, reserved: u32 = 0,
    description: ?[*]const u16 = null, module_path: ?[*]const u16 = null,
    description_len: u32 = 0, module_path_len: u32 = 0,
    exit_filetime: i64 = 0,
};
// Flags: birth=1, description HRESULT=2, start=4, counters=8, native error=16.
pub const RequestView = extern struct {
    request_id: u64 = 0, process_birth: i64 = 0, requested_at: i64 = 0,
    pid: u32 = 0, flags: u32 = 0, native_error: i32 = 0, thread_count: u32 = 0,
    threads: ?[*]const ThreadView = null,
    sampled_from: i64 = 0, sampled_through: i64 = 0,
};
pub const View = extern struct {
    request_count: u32 = 0, reserved: u32 = 0, requests: ?[*]const RequestView = null,
    native_reads: u64 = 0, open_handles: u64 = 0, peak_handles: u64 = 0,
};
const Entry = struct {
    handle: ?*anyopaque = null, instance: i64, tid: u32, birth: i64 = 0, ended: bool = false,
    expected_birth: i64 = 0, // Inventory hint for matching/retry; never exposed as verified creation time.
    read_from: i64 = 0, kernel: i64 = 0, user: i64 = 0,
    description: Name = .{}, start: ?u64 = null, start_source: u32 = 0, module: ?[]u16 = null, native_error: ?i32 = null,
    sampled: bool = false, sample_error: ?i32 = null, read_through: i64 = 0,
    kernel_delta: i64 = 0, user_delta: i64 = 0, exit_filetime: i64 = 0,
};
const Request = struct {
    id: u64, pid: u32, birth: i64, requested_at: i64,
    process: ?*anyopaque = null, module_process: ?*anyopaque = null, native_error: ?i32 = null,
    entries: std.ArrayList(Entry) = .empty,
    sampled_from: i64 = 0, sampled_through: i64 = 0,
};
pub const Owner = struct {
    allocator: std.mem.Allocator, source: Source,
    max_threads: u32, max_requests: u32, max_path_units: u32, max_modules: u32, max_inventory_bytes: usize,
    requests: std.ArrayList(Request) = .empty, entry_count: usize = 0,
    round: std.heap.ArenaAllocator,
    native_reads: u64 = 0, open_handles: u64 = 0, peak_handles: u64 = 0,
    recorded: bool = false,

    pub fn init(allocator: std.mem.Allocator, source: Source, max_threads: u32, max_requests: u32,
        max_path_units: u32, max_modules: u32, max_inventory_bytes: usize) !Owner {
        if (max_threads == 0 or max_requests == 0 or max_path_units == 0 or max_modules == 0 or max_inventory_bytes < 65536) return error.InvalidConfig;
        return .{ .allocator = allocator, .source = source, .max_threads = max_threads, .max_requests = max_requests,
            .max_path_units = max_path_units, .max_modules = max_modules, .max_inventory_bytes = max_inventory_bytes,
            .round = std.heap.ArenaAllocator.init(allocator) };
    }
    pub fn deinit(self: *Owner) void {
        for (self.requests.items) |*request| self.closeRequest(request);
        self.requests.deinit(self.allocator);
        self.round.deinit();
        std.debug.assert(self.open_handles == 0 and self.entry_count == 0);
        self.* = undefined;
    }
    fn retain(self: *Owner, handle: ?*anyopaque) ?*anyopaque {
        if (handle != null) { self.open_handles += 1; self.peak_handles = @max(self.peak_handles, self.open_handles); }
        return handle;
    }
    fn close(self: *Owner, handle: ?*anyopaque) void {
        if (handle) |value| { self.source.close(self.source.context, value); self.open_handles -= 1; }
    }
    fn closeRequest(self: *Owner, request: *Request) void {
        for (request.entries.items) |entry| {
            self.close(entry.handle);
            if (entry.description.text) |text| self.allocator.free(text);
            if (entry.module) |text| self.allocator.free(text);
        }
        self.entry_count -= request.entries.items.len;
        request.entries.deinit(self.allocator);
        self.close(request.module_process); self.close(request.process);
    }
    pub fn remove(self: *Owner, id: u64) void {
        for (self.requests.items, 0..) |*request, i| if (request.id == id) {
            self.closeRequest(request); _ = self.requests.swapRemove(i); return;
        };
    }
    pub fn register(self: *Owner, id: u64, pid: u32, birth: i64, requested_at: i64) !void {
        defer _ = self.round.reset(.free_all);
        if (id == 0 or pid == 0 or birth <= 0 or requested_at <= 0) return error.InvalidInput;
        for (self.requests.items) |request| if (request.id == id) return error.InvalidInput;
        if (self.requests.items.len >= self.max_requests) return error.CapacityExceeded;
        var request: Request = .{ .id = id, .pid = pid, .birth = birth, .requested_at = requested_at };
        errdefer self.closeRequest(&request);
        if (self.recorded) {
            request.native_error = 50; // Historical metadata cannot be resolved against current Windows identities.
            try self.requests.append(self.allocator, request);
            return;
        }
        self.native_reads += 1;
        request.process = self.retain(self.source.open_process(self.source.context, pid, false));
        if (request.process) |process| {
            if (self.source.process_birth(self.source.context, process) != birth) {
                self.close(process); request.process = null; request.native_error = 1168;
            } else if (!self.source.process_running(self.source.context, process)) {
                request.native_error = 1168;
            } else {
                request.module_process = self.retain(self.source.open_process(self.source.context, pid, true));
                if (request.module_process) |module_process| if (self.source.process_birth(self.source.context, module_process) != birth) {
                    self.close(module_process); request.module_process = null;
                };
                const Inventory = struct {
                    owner: *Owner, request: *Request,
                    fn visit(ctx: *anyopaque, tid: u32, thread_birth: i64, start: ?u64) !void {
                        const value: *@This() = @ptrCast(@alignCast(ctx));
                        try value.owner.observe(value.request, tid, 0, thread_birth, start, false);
                    }
                };
                var inventory: Inventory = .{ .owner = self, .request = &request };
                self.source.inventory(self.source.context, pid, birth, self.round.allocator(), self.max_inventory_bytes, Inventory.visit, &inventory) catch |err| switch (err) {
                    error.SystemInformationUnavailable => request.native_error = self.source.last_error(self.source.context),
                    else => return err,
                };
            }
        } else request.native_error = self.source.last_error(self.source.context);
        try self.requests.append(self.allocator, request);
    }
    pub fn observeThread(self: *Owner, pid: u32, tid: u32, instance: i64, ended: bool, event_filetime: i64) !void {
        defer _ = self.round.reset(.free_all);
        if (self.recorded) return;
        for (self.requests.items) |*request| {
            if (request.pid != pid) continue;
            if (ended) {
                for (request.entries.items) |*entry| if (entry.tid == tid and !entry.ended) { entry.ended = true; };
            } else {
                for (request.entries.items) |*entry| if (!entry.ended and entry.tid == tid and instance != 0 and entry.instance != 0 and entry.instance != instance) {
                    entry.ended = true;
                };
                try self.observe(request, tid, instance, 0, null, event_filetime >= request.requested_at);
            }
        }
    }
    fn observe(self: *Owner, request: *Request, tid: u32, instance: i64, known_birth: i64, fallback_start: ?u64, born_during_request: bool) !void {
        const process = request.process orelse return;
        if (!self.source.process_running(self.source.context, process)) { request.native_error = 1168; return; }
        var retry_instance = instance;
        var expected_birth = known_birth;
        for (request.entries.items, 0..) |*existing, index| {
            if (existing.tid != tid) continue;
            const same_instance = instance != 0 and existing.instance == instance;
            const same_birth = known_birth != 0 and (existing.birth == known_birth or (!existing.ended and existing.expected_birth == known_birth));
            const unbound_failure = !existing.ended and existing.instance == 0 and existing.handle == null and existing.birth == 0 and
                (instance != 0 or (known_birth != 0 and (existing.expected_birth == 0 or existing.expected_birth == known_birth)));
            if (!same_instance and !same_birth and !unbound_failure) continue;
            if (existing.handle != null) {
                if (instance != 0 and !existing.ended) existing.instance = instance;
                return;
            }
            if (existing.ended) continue;
            retry_instance = if (instance != 0) instance else existing.instance;
            expected_birth = if (known_birth != 0) known_birth else existing.expected_birth;
            // Failed opens own no handle or retained strings. Replace this one slot rather than append a failure per retry.
            std.debug.assert(existing.description.text == null and existing.module == null);
            _ = request.entries.swapRemove(index);
            self.entry_count -= 1;
            break;
        }
        if (self.entry_count >= self.max_threads) return error.CapacityExceeded;
        var entry: Entry = .{ .instance = retry_instance, .tid = tid, .expected_birth = expected_birth };
        self.native_reads += 1;
        entry.handle = self.retain(self.source.open_thread(self.source.context, request.pid, tid));
        errdefer {
            self.close(entry.handle);
            if (entry.description.text) |text| self.allocator.free(text);
            if (entry.module) |text| self.allocator.free(text);
        }
        if (entry.handle) |handle| {
            if (self.source.times(self.source.context, handle)) |times| {
                if (expected_birth != 0 and expected_birth != times.birth) {
                    self.close(handle); entry.handle = null; entry.native_error = 1168;
                } else {
                    for (request.entries.items) |*existing| if (existing.tid == tid and existing.birth == times.birth) {
                        if (retry_instance != 0 and !existing.ended) existing.instance = retry_instance;
                        self.close(handle); entry.handle = null; return;
                    };
                    entry.birth = times.birth;
                    const born = born_during_request and times.birth >= request.requested_at;
                    entry.read_from = if (born) times.birth else self.source.now(self.source.context);
                    entry.kernel = if (born) 0 else times.kernel; entry.user = if (born) 0 else times.user;
                    entry.description = try self.source.name(self.source.context, handle, self.allocator, self.max_path_units);
                    entry.start = self.source.start(self.source.context, handle);
                    entry.start_source = if (entry.start != null) 1 else if (fallback_start != null) 2 else 0;
                    if (entry.start == null) entry.start = fallback_start;
                    if (entry.start) |start| if (request.module_process) |modules|
                        { entry.module = try self.source.module(self.source.context, modules, start, self.allocator, self.round.allocator(), self.max_path_units, self.max_modules); };
                }
            } else { entry.native_error = self.source.last_error(self.source.context); self.close(handle); entry.handle = null; }
        } else entry.native_error = self.source.last_error(self.source.context);
        try request.entries.append(self.allocator, entry); self.entry_count += 1;
    }
    /// Binding can discover the verified inventory counterpart of an error-only CPU lifetime.
    /// Transfer that single entry without duplicating ownership or losing its cumulative baseline.
    pub fn mergeErrorRetry(self: *Owner, request_index: usize, error_index: usize, replacement_index: usize) bool {
        const request = &self.requests.items[request_index];
        const target = &request.entries.items[error_index];
        const replacement = request.entries.items[replacement_index];
        if (error_index == replacement_index or target.ended or target.handle != null or target.birth != 0 or
            target.description.text != null or target.module != null or target.instance == 0 or replacement.instance != 0 or target.tid != replacement.tid) return false;
        const instance = target.instance;
        target.* = replacement;
        target.instance = instance;
        _ = request.entries.swapRemove(replacement_index);
        self.entry_count -= 1;
        return true;
    }
    /// Explicit diagnostic business read, executed only by the existing consumer flow.
    /// An absent/removed request is acknowledged as unavailable, never a fatal capture error.
    pub fn sample(self: *Owner, id: u64, from: i64, through: i64) !bool {
        defer _ = self.round.reset(.free_all);
        if (id == 0 or from <= 0 or through < from) return error.InvalidInput;
        for (self.requests.items) |*request| if (request.id == id) {
            if (from < request.requested_at) return false;
            if (!self.recorded) {
                if (request.process) |process| if (self.source.process_running(self.source.context, process)) {
                    const Inventory = struct {
                        owner: *Owner, request: *Request,
                        fn visit(ctx: *anyopaque, tid: u32, birth: i64, start: ?u64) !void {
                            const value: *@This() = @ptrCast(@alignCast(ctx));
                            try value.owner.observe(value.request, tid, 0, birth, start, birth >= value.request.requested_at);
                        }
                    };
                    var inventory: Inventory = .{ .owner = self, .request = request };
                    self.source.inventory(self.source.context, request.pid, request.birth, self.round.allocator(), self.max_inventory_bytes, Inventory.visit, &inventory) catch |err| switch (err) {
                        error.SystemInformationUnavailable => {}, // Existing handles remain usable, as in the managed oracle.
                        else => return err,
                    };
                };
                for (request.entries.items) |*entry| {
                    entry.sampled = false; entry.sample_error = null; entry.read_through = 0;
                    entry.kernel_delta = 0; entry.user_delta = 0; entry.exit_filetime = 0;
                    const handle = entry.handle orelse continue;
                    if (entry.description.text == null)
                        entry.description = try self.source.name(self.source.context, handle, self.allocator, self.max_path_units);
                    self.native_reads += 1;
                    if (self.source.times(self.source.context, handle)) |times| {
                        if (times.birth == entry.birth and times.kernel >= entry.kernel and times.user >= entry.user) {
                            entry.sampled = true; entry.read_through = self.source.now(self.source.context);
                            entry.kernel_delta = times.kernel - entry.kernel; entry.user_delta = times.user - entry.user;
                            entry.exit_filetime = times.exit;
                        } else entry.sample_error = 1168;
                    } else entry.sample_error = self.source.last_error(self.source.context);
                }
            }
            request.sampled_from = from; request.sampled_through = through;
            return true;
        };
        return false;
    }
    pub fn publish(self: *Owner, output: std.mem.Allocator) !View {
        const requests = try output.alloc(RequestView, self.requests.items.len);
        for (self.requests.items, requests) |*request, *view| {
            const rows = try output.alloc(ThreadView, request.entries.items.len);
            for (request.entries.items, rows) |*entry, *row| {
                const native_error: ?i32 = if (entry.sample_error) |value| value else entry.native_error;
                row.* = .{ .thread_instance = entry.instance, .tid = entry.tid, .birth = entry.birth,
                    .flags = @as(u32, if (entry.birth > 0) 1 else 0) | @as(u32, if (entry.description.hresult != null) 2 else 0) |
                        @as(u32, if (entry.start != null) 4 else 0) | @as(u32, if (native_error != null) 16 else 0) |
                        @as(u32, if (entry.sampled) 8 else 0),
                    .description_hresult = entry.description.hresult orelse 0, .native_error = native_error orelse 0,
                    .start_address = entry.start orelse 0, .start_source = entry.start_source, .read_from = entry.read_from,
                    .read_through = entry.read_through, .kernel_delta = entry.kernel_delta, .user_delta = entry.user_delta,
                    .exit_filetime = entry.exit_filetime };
                if (entry.description.text) |text| { const copy = try output.dupe(u16, text); row.description = copy.ptr; row.description_len = @intCast(copy.len); }
                if (entry.module) |text| { const copy = try output.dupe(u16, text); row.module_path = copy.ptr; row.module_path_len = @intCast(copy.len); }
            }
            view.* = .{ .request_id = request.id, .process_birth = request.birth, .requested_at = request.requested_at,
                .pid = request.pid, .flags = if (request.native_error != null) 1 else 0, .native_error = request.native_error orelse 0,
                .thread_count = @intCast(rows.len), .threads = rows.ptr,
                .sampled_from = request.sampled_from, .sampled_through = request.sampled_through };
        }
        return .{ .request_count = @intCast(requests.len), .requests = requests.ptr,
            .native_reads = self.native_reads, .open_handles = self.open_handles, .peak_handles = self.peak_handles };
    }
};

pub const windows: Source = @import("thread_diagnostics_windows.zig").source;

test "verified retry replaces an error-only lifetime without duplicating handles or baseline" {
    const Fake = struct {
        allow: bool = false, inventory_ready: bool = false, closed: u32 = 0,
        fn cast(ctx: ?*anyopaque) *@This() { return @ptrCast(@alignCast(ctx.?)); }
        fn process(_: ?*anyopaque, _: u32, modules: bool) ?*anyopaque { return @ptrFromInt(if (modules) @as(usize, 2) else 1); }
        fn birth(_: ?*anyopaque, _: *anyopaque) ?i64 { return 1; }
        fn running(_: ?*anyopaque, _: *anyopaque) bool { return true; }
        fn thread(ctx: ?*anyopaque, _: u32, tid: u32) ?*anyopaque { return if (cast(ctx).allow) @ptrFromInt(tid) else null; }
        fn times(_: ?*anyopaque, _: *anyopaque) ?Times { return .{ .birth = 5, .kernel = 20, .user = 30 }; }
        fn name(_: ?*anyopaque, _: *anyopaque, a: std.mem.Allocator, _: u32) !Name {
            return .{ .text = try a.dupe(u16, std.unicode.utf8ToUtf16LeStringLiteral("recovered")) };
        }
        fn address(_: ?*anyopaque, _: *anyopaque) ?u64 { return 123; }
        fn module(_: ?*anyopaque, _: *anyopaque, _: u64, a: std.mem.Allocator, _: std.mem.Allocator, _: u32, _: u32) !?[]u16 {
            return try a.dupe(u16, std.unicode.utf8ToUtf16LeStringLiteral("worker.dll"));
        }
        fn inventory(ctx: ?*anyopaque, _: u32, _: i64, _: std.mem.Allocator, _: usize, visit: Visit, target: *anyopaque) !void {
            if (cast(ctx).inventory_ready) try visit(target, 20, 5, 123);
        }
        fn close(ctx: ?*anyopaque, _: *anyopaque) void { cast(ctx).closed += 1; }
        fn now(_: ?*anyopaque) i64 { return 200; }
        fn last(_: ?*anyopaque) i32 { return 5; }
    };
    var fake: Fake = .{};
    const source: Source = .{ .context = &fake, .open_process = Fake.process, .process_birth = Fake.birth,
        .process_running = Fake.running, .open_thread = Fake.thread, .times = Fake.times, .name = Fake.name,
        .start = Fake.address, .module = Fake.module, .inventory = Fake.inventory, .close = Fake.close,
        .now = Fake.now, .last_error = Fake.last };
    var owner = try Owner.init(std.testing.allocator, source, 2, 1, 128, 8, 65536);
    defer owner.deinit();
    try owner.register(9, 10, 1, 100);
    try owner.observeThread(10, 20, 7, false, 110);
    fake.allow = true; fake.inventory_ready = true;
    try std.testing.expect(try owner.sample(9, 100, 200));
    try std.testing.expectEqual(@as(usize, 2), owner.entry_count);
    try std.testing.expect(owner.mergeErrorRetry(0, 0, 1));
    try std.testing.expectEqual(@as(usize, 1), owner.entry_count);
    try std.testing.expectEqual(@as(u64, 3), owner.open_handles);
    var bank = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer bank.deinit();
    const row = (try owner.publish(bank.allocator())).requests.?[0].threads.?[0];
    try std.testing.expectEqual(@as(i64, 7), row.thread_instance);
    try std.testing.expectEqual(@as(i64, 5), row.birth);
    try std.testing.expectEqual(@as(u32, 9), row.flags & 9);
    try std.testing.expectEqual(@as(u32, 0), row.flags & 16);
    try std.testing.expectEqual(@as(i64, 200), row.read_from);
    try std.testing.expectEqual(@as(i64, 0), row.kernel_delta);
    try std.testing.expectEqual(@as(i64, 0), row.user_delta);
    try std.testing.expectEqualSlices(u16, std.unicode.utf8ToUtf16LeStringLiteral("recovered"), row.description.?[0..row.description_len]);
    owner.remove(9);
    try std.testing.expectEqual(@as(u64, 0), owner.open_handles);
    try std.testing.expectEqual(@as(u32, 3), fake.closed);
}


test "denied diagnostic thread opens retry one error-only entry per known lifetime" {
    const Fake = struct {
        opens: u32 = 0,
        fn cast(ctx: ?*anyopaque) *@This() { return @ptrCast(@alignCast(ctx.?)); }
        fn process(_: ?*anyopaque, _: u32, modules: bool) ?*anyopaque { return @ptrFromInt(if (modules) @as(usize, 2) else 1); }
        fn birth(_: ?*anyopaque, _: *anyopaque) ?i64 { return 1; }
        fn running(_: ?*anyopaque, _: *anyopaque) bool { return true; }
        fn thread(ctx: ?*anyopaque, _: u32, _: u32) ?*anyopaque { cast(ctx).opens += 1; return null; }
        fn times(_: ?*anyopaque, _: *anyopaque) ?Times { @panic("A denied thread handle cannot be sampled."); }
        fn name(_: ?*anyopaque, _: *anyopaque, _: std.mem.Allocator, _: u32) !Name { @panic("A denied thread handle has no description."); }
        fn address(_: ?*anyopaque, _: *anyopaque) ?u64 { @panic("A denied thread handle has no start address."); }
        fn module(_: ?*anyopaque, _: *anyopaque, _: u64, _: std.mem.Allocator, _: std.mem.Allocator, _: u32, _: u32) !?[]u16 { return null; }
        fn inventory(_: ?*anyopaque, _: u32, _: i64, _: std.mem.Allocator, _: usize, visit: Visit, ctx: *anyopaque) !void { try visit(ctx, 20, 5, null); }
        fn close(_: ?*anyopaque, _: *anyopaque) void {}
        fn now(_: ?*anyopaque) i64 { return 100; }
        fn last(_: ?*anyopaque) i32 { return 5; }
    };
    var fake: Fake = .{};
    const source: Source = .{ .context = &fake, .open_process = Fake.process, .process_birth = Fake.birth,
        .process_running = Fake.running, .open_thread = Fake.thread, .times = Fake.times, .name = Fake.name,
        .start = Fake.address, .module = Fake.module, .inventory = Fake.inventory, .close = Fake.close,
        .now = Fake.now, .last_error = Fake.last };
    var owner = try Owner.init(std.testing.allocator, source, 2, 1, 128, 8, 65536);
    defer owner.deinit();
    try owner.register(9, 10, 1, 100);
    try owner.observeThread(10, 20, 7, false, 110); // Known CPU lifetime, unverified OS birth.
    try std.testing.expectEqual(@as(usize, 1), owner.entry_count);
    var bank = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer bank.deinit();
    for (0..10) |round| {
        try std.testing.expect(try owner.sample(9, @intCast(100 + round * 100), @intCast(200 + round * 100)));
        try std.testing.expectEqual(@as(usize, 1), owner.entry_count);
        const view = try owner.publish(bank.allocator());
        try std.testing.expectEqual(@as(u32, 1), view.requests.?[0].thread_count);
        const row = view.requests.?[0].threads.?[0];
        try std.testing.expectEqual(@as(i64, 7), row.thread_instance);
        try std.testing.expectEqual(@as(i64, 0), row.birth);
        try std.testing.expectEqual(@as(u32, 0), row.flags & 9);
        try std.testing.expectEqual(@as(u32, 16), row.flags & 16);
        try std.testing.expectEqual(@as(i32, 5), row.native_error);
        _ = bank.reset(.retain_capacity);
    }
    try std.testing.expectEqual(@as(u32, 12), fake.opens); // Register, lifecycle retry, and one retry per explicit sample.
    owner.remove(9);
    try std.testing.expectEqual(@as(u64, 0), owner.open_handles);
}


test "explicit diagnostic samples preserve cumulative baselines names exits and failed-read fields" {
    const Fake = struct {
        tick: i64 = 100, kernel: i64 = 2, user: i64 = 3, exit: i64 = 0,
        name_error: ?i32 = -1, fail_times: bool = false, wrong_birth: bool = false,
        times_calls: u32 = 0, name_calls: u32 = 0, now_calls: u32 = 0,
        fn cast(ctx: ?*anyopaque) *@This() { return @ptrCast(@alignCast(ctx.?)); }
        fn process(_: ?*anyopaque, _: u32, modules: bool) ?*anyopaque { return @ptrFromInt(if (modules) @as(usize, 2) else 1); }
        fn birth(_: ?*anyopaque, _: *anyopaque) ?i64 { return 1; }
        fn running(_: ?*anyopaque, _: *anyopaque) bool { return true; }
        fn thread(_: ?*anyopaque, _: u32, tid: u32) ?*anyopaque { return @ptrFromInt(tid); }
        fn times(ctx: ?*anyopaque, handle: *anyopaque) ?Times {
            const self = cast(ctx); self.times_calls += 1;
            if (self.fail_times) return null;
            return .{ .birth = if (self.wrong_birth) 999 else if (@intFromPtr(handle) == 20) 5 else 150,
                .exit = self.exit, .kernel = self.kernel, .user = self.user };
        }
        fn name(ctx: ?*anyopaque, _: *anyopaque, a: std.mem.Allocator, _: u32) !Name {
            const self = cast(ctx); self.name_calls += 1;
            if (self.name_error) |value| return .{ .hresult = value };
            return .{ .text = try a.dupe(u16, std.unicode.utf8ToUtf16LeStringLiteral("late name")) };
        }
        fn address(_: ?*anyopaque, _: *anyopaque) ?u64 { return null; }
        fn module(_: ?*anyopaque, _: *anyopaque, _: u64, _: std.mem.Allocator, _: std.mem.Allocator, _: u32, _: u32) !?[]u16 { return null; }
        fn inventory(_: ?*anyopaque, _: u32, _: i64, _: std.mem.Allocator, _: usize, visit: Visit, ctx: *anyopaque) !void { try visit(ctx, 20, 5, null); }
        fn close(_: ?*anyopaque, _: *anyopaque) void {}
        fn now(ctx: ?*anyopaque) i64 { const self = cast(ctx); self.now_calls += 1; return self.tick; }
        fn last(_: ?*anyopaque) i32 { return 5; }
    };
    var fake: Fake = .{};
    const source: Source = .{ .context = &fake, .open_process = Fake.process, .process_birth = Fake.birth,
        .process_running = Fake.running, .open_thread = Fake.thread, .times = Fake.times, .name = Fake.name,
        .start = Fake.address, .module = Fake.module, .inventory = Fake.inventory, .close = Fake.close,
        .now = Fake.now, .last_error = Fake.last };
    var owner = try Owner.init(std.testing.allocator, source, 2, 1, 128, 8, 65536);
    defer owner.deinit();
    try owner.register(9, 10, 1, 100);
    fake.tick = 160;
    try owner.observeThread(10, 30, 2, false, 150);
    try owner.observeThread(10, 30, 0, true, 170);
    var bank = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer bank.deinit();
    const initial = try owner.publish(bank.allocator());
    try std.testing.expectEqual(@as(u32, 0), initial.requests.?[0].threads.?[0].flags & 8);
    fake.tick = 200; fake.kernel = 20; fake.user = 30; fake.exit = 180; fake.name_error = -2;
    const before = fake;
    try std.testing.expect(try owner.sample(9, 100, 200));
    try std.testing.expectEqual(before.times_calls + 2, fake.times_calls);
    try std.testing.expectEqual(before.name_calls + 2, fake.name_calls);
    try std.testing.expectEqual(before.now_calls + 2, fake.now_calls);
    const first = try owner.publish(bank.allocator());
    const old = first.requests.?[0].threads.?[0];
    const born = first.requests.?[0].threads.?[1];
    try std.testing.expectEqual(@as(i64, 18), old.kernel_delta);
    try std.testing.expectEqual(@as(i64, 27), old.user_delta);
    try std.testing.expectEqual(@as(i64, 100), old.read_from);
    try std.testing.expectEqual(@as(i64, 200), old.read_through);
    try std.testing.expectEqual(@as(i32, -2), old.description_hresult);
    try std.testing.expectEqual(@as(i64, 180), old.exit_filetime);
    try std.testing.expectEqual(@as(i64, 20), born.kernel_delta);
    try std.testing.expectEqual(@as(i64, 30), born.user_delta);
    try std.testing.expectEqual(@as(i64, 150), born.read_from);
    fake.tick = 300; fake.kernel = 25; fake.user = 35; fake.name_error = null;
    try std.testing.expect(try owner.sample(9, 200, 300));
    const second = try owner.publish(bank.allocator());
    const current = second.requests.?[0].threads.?[0];
    try std.testing.expectEqual(@as(i64, 23), current.kernel_delta);
    try std.testing.expectEqual(@as(i64, 32), current.user_delta);
    try std.testing.expectEqual(@as(u32, 0), current.flags & 2);
    try std.testing.expectEqualSlices(u16, std.unicode.utf8ToUtf16LeStringLiteral("late name"), current.description.?[0..current.description_len]);
    try std.testing.expectEqual(@as(i64, 18), old.kernel_delta); // The previously leased bank stays immutable.
    const cached = fake;
    for (0..50) |_| { _ = try owner.publish(bank.allocator()); }
    try std.testing.expectEqualDeep(cached, fake);
    fake.fail_times = true;
    try std.testing.expect(try owner.sample(9, 300, 400));
    const failed = (try owner.publish(bank.allocator())).requests.?[0].threads.?[0];
    try std.testing.expectEqual(@as(u32, 0), failed.flags & 8);
    try std.testing.expectEqual(@as(u32, 16), failed.flags & 16);
    try std.testing.expectEqual(@as(i32, 5), failed.native_error);
    try std.testing.expectEqual(@as(i64, 0), failed.kernel_delta);
    try std.testing.expectEqual(@as(i64, 0), failed.read_through);
    try std.testing.expectEqual(cached.name_calls, fake.name_calls);
    fake.fail_times = false; fake.wrong_birth = true;
    try std.testing.expect(try owner.sample(9, 400, 500));
    const mismatch = (try owner.publish(bank.allocator())).requests.?[0].threads.?[0];
    try std.testing.expectEqual(@as(i32, 1168), mismatch.native_error);
    try std.testing.expectEqual(@as(u32, 0), mismatch.flags & 8);
    fake.wrong_birth = false; fake.kernel = 1; fake.user = 1;
    try std.testing.expect(try owner.sample(9, 500, 600));
    const regressed = (try owner.publish(bank.allocator())).requests.?[0].threads.?[0];
    try std.testing.expectEqual(@as(i32, 1168), regressed.native_error);
    try std.testing.expectEqual(@as(u32, 0), regressed.flags & 8);
    owner.remove(9);
    const removed = fake;
    try std.testing.expect(!(try owner.sample(9, 600, 700)));
    try std.testing.expectEqualDeep(removed, fake);
    try std.testing.expectEqual(@as(u64, 0), owner.open_handles);
}


test "repeated diagnostic publications perform no OS reads for retained requests" {
    const Fake = struct {
        times_calls: u64 = 0, name_calls: u64 = 0, now_calls: u64 = 0, other_calls: u64 = 0,
        fn cast(ctx: ?*anyopaque) *@This() { return @ptrCast(@alignCast(ctx.?)); }
        fn process(ctx: ?*anyopaque, _: u32, modules: bool) ?*anyopaque {
            cast(ctx).other_calls += 1;
            return @ptrFromInt(if (modules) @as(usize, 2) else 1);
        }
        fn birth(ctx: ?*anyopaque, _: *anyopaque) ?i64 { cast(ctx).other_calls += 1; return 1; }
        fn running(ctx: ?*anyopaque, _: *anyopaque) bool { cast(ctx).other_calls += 1; return true; }
        fn thread(ctx: ?*anyopaque, _: u32, tid: u32) ?*anyopaque {
            cast(ctx).other_calls += 1; return @ptrFromInt(tid);
        }
        fn times(ctx: ?*anyopaque, _: *anyopaque) ?Times {
            cast(ctx).times_calls += 1;
            return .{ .birth = 50, .kernel = 2, .user = 3 };
        }
        fn name(ctx: ?*anyopaque, _: *anyopaque, _: std.mem.Allocator, _: u32) !Name {
            cast(ctx).name_calls += 1;
            return .{ .hresult = -2147467259 };
        }
        fn address(ctx: ?*anyopaque, _: *anyopaque) ?u64 { cast(ctx).other_calls += 1; return null; }
        fn module(ctx: ?*anyopaque, _: *anyopaque, _: u64, _: std.mem.Allocator, _: std.mem.Allocator, _: u32, _: u32) !?[]u16 {
            cast(ctx).other_calls += 1; return null;
        }
        fn inventory(ctx: ?*anyopaque, _: u32, _: i64, _: std.mem.Allocator, _: usize, visit: Visit, target: *anyopaque) !void {
            cast(ctx).other_calls += 1;
            for (0..500) |index| try visit(target, @intCast(1000 + index), 50, null);
        }
        fn close(ctx: ?*anyopaque, _: *anyopaque) void { cast(ctx).other_calls += 1; }
        fn now(ctx: ?*anyopaque) i64 { cast(ctx).now_calls += 1; return 100; }
        fn last(ctx: ?*anyopaque) i32 { cast(ctx).other_calls += 1; return 5; }
    };
    var fake: Fake = .{};
    const source: Source = .{ .context = &fake, .open_process = Fake.process, .process_birth = Fake.birth,
        .process_running = Fake.running, .open_thread = Fake.thread, .times = Fake.times, .name = Fake.name,
        .start = Fake.address, .module = Fake.module, .inventory = Fake.inventory, .close = Fake.close,
        .now = Fake.now, .last_error = Fake.last };
    var owner = try Owner.init(std.testing.allocator, source, 500, 1, 128, 8, 65536);
    defer owner.deinit();
    try owner.register(9, 10, 1, 100);
    try std.testing.expectEqual(@as(usize, 500), owner.entry_count);
    const before = fake;
    var bank = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer bank.deinit();
    for (0..50) |_| {
        const view = try owner.publish(bank.allocator());
        try std.testing.expectEqual(@as(u32, 1), view.request_count);
        try std.testing.expectEqual(@as(u32, 500), view.requests.?[0].thread_count);
        _ = bank.reset(.retain_capacity);
    }
    try std.testing.expectEqual(before.times_calls, fake.times_calls);
    try std.testing.expectEqual(before.name_calls, fake.name_calls);
    try std.testing.expectEqual(before.now_calls, fake.now_calls);
    try std.testing.expectEqual(before.other_calls, fake.other_calls);
    owner.remove(9);
    owner.recorded = true;
    const recorded_before = fake;
    try owner.register(10, 10, 1, 100);
    try std.testing.expect(try owner.sample(10, 100, 200));
    const recorded_view = try owner.publish(bank.allocator());
    try std.testing.expectEqual(@as(i32, 50), recorded_view.requests.?[0].native_error);
    try std.testing.expectEqual(@as(u32, 0), recorded_view.requests.?[0].thread_count);
    try std.testing.expectEqualDeep(recorded_before, fake);
}

test "diagnostic owner retains born and exited lifetime metadata until explicit removal" {
    const Fake = struct {
        ticks: i64 = 100, kernel: i64 = 2, closed: u32 = 0,
        fn cast(ctx: ?*anyopaque) *@This() { return @ptrCast(@alignCast(ctx.?)); }
        fn process(_: ?*anyopaque, _: u32, modules: bool) ?*anyopaque { return @ptrFromInt(if (modules) @as(usize, 2) else 1); }
        fn birth(_: ?*anyopaque, _: *anyopaque) ?i64 { return 1; }
        fn running(_: ?*anyopaque, _: *anyopaque) bool { return true; }
        fn thread(_: ?*anyopaque, _: u32, tid: u32) ?*anyopaque { return @ptrFromInt(tid); }
        fn times(ctx: ?*anyopaque, handle: *anyopaque) ?Times {
            return .{ .birth = if (@intFromPtr(handle) == 20) 5 else 150, .kernel = cast(ctx).kernel, .user = cast(ctx).kernel };
        }
        fn name(_: ?*anyopaque, _: *anyopaque, a: std.mem.Allocator, _: u32) !Name {
            return .{ .text = try a.dupe(u16, std.unicode.utf8ToUtf16LeStringLiteral("named worker")) };
        }
        fn address(_: ?*anyopaque, _: *anyopaque) ?u64 { return 123; }
        fn module(_: ?*anyopaque, _: *anyopaque, _: u64, a: std.mem.Allocator, _: std.mem.Allocator, _: u32, _: u32) !?[]u16 {
            return try a.dupe(u16, std.unicode.utf8ToUtf16LeStringLiteral("worker.dll"));
        }
        fn inventory(_: ?*anyopaque, _: u32, _: i64, _: std.mem.Allocator, _: usize, visit: Visit, ctx: *anyopaque) !void { try visit(ctx, 20, 5, 123); }
        fn close(ctx: ?*anyopaque, _: *anyopaque) void { cast(ctx).closed += 1; }
        fn now(ctx: ?*anyopaque) i64 { return cast(ctx).ticks; }
        fn last(_: ?*anyopaque) i32 { return 5; }
    };
    var fake: Fake = .{};
    const source: Source = .{ .context = &fake, .open_process = Fake.process, .process_birth = Fake.birth,
        .process_running = Fake.running, .open_thread = Fake.thread, .times = Fake.times, .name = Fake.name,
        .start = Fake.address, .module = Fake.module, .inventory = Fake.inventory, .close = Fake.close,
        .now = Fake.now, .last_error = Fake.last };
    var owner = try Owner.init(std.testing.allocator, source, 2, 2, 128, 8, 65536);
    defer owner.deinit();
    try owner.register(9, 10, 1, 100);
    try owner.observeThread(10, 20, 1, false, 110);
    fake.ticks = 160;
    try owner.observeThread(10, 30, 2, false, 150);
    try owner.observeThread(10, 30, 0, true, 170);
    try std.testing.expectError(error.CapacityExceeded, owner.observeThread(10, 40, 3, false, 180));
    fake.ticks = 200; fake.kernel = 20;
    try std.testing.expect(try owner.sample(9, 100, 200));
    var bank = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer bank.deinit();
    const published = try owner.publish(bank.allocator());
    try std.testing.expectEqual(@as(u32, 1), published.request_count);
    const request = published.requests.?[0];
    try std.testing.expectEqual(@as(u32, 2), request.thread_count);
    const born = request.threads.?[1];
    try std.testing.expectEqual(@as(i64, 2), born.thread_instance);
    try std.testing.expectEqual(@as(i64, 150), born.birth);
    try std.testing.expectEqual(@as(i64, 150), born.read_from);
    try std.testing.expectEqual(@as(i64, 20), born.kernel_delta);
    try std.testing.expectEqual(@as(u32, 13), born.flags & 13);
    owner.remove(9);
    try std.testing.expectEqual(@as(u64, 0), owner.open_handles);
    try std.testing.expectEqual(@as(usize, 0), owner.entry_count);
    try std.testing.expectEqual(@as(u32, 5), fake.closed); // Includes the verified duplicate inventory handle.
    try std.testing.expectEqualSlices(u16, std.unicode.utf8ToUtf16LeStringLiteral("named worker"), born.description.?[0..born.description_len]);
    try std.testing.expectEqualSlices(u16, std.unicode.utf8ToUtf16LeStringLiteral("worker.dll"), born.module_path.?[0..born.module_path_len]);
}

test "Windows diagnostics retain a real named thread after exit" {
    if (@import("builtin").os.tag != .windows) return error.SkipZigTest;
    const Win = struct {
        extern "kernel32" fn GetCurrentProcessId() callconv(.winapi) u32;
        extern "kernel32" fn GetCurrentThreadId() callconv(.winapi) u32;
        extern "kernel32" fn GetCurrentThread() callconv(.winapi) *anyopaque;
        extern "kernel32" fn SetThreadDescription(*anyopaque, [*:0]const u16) callconv(.winapi) i32;
        const Context = struct { tid: std.atomic.Value(u32) = .init(0), release: std.atomic.Value(bool) = .init(false) };
        fn worker(ctx: *Context) void {
            _ = SetThreadDescription(GetCurrentThread(), std.unicode.utf8ToUtf16LeStringLiteral("native-exit-worker"));
            ctx.tid.store(GetCurrentThreadId(), .release);
            while (!ctx.release.load(.acquire)) std.atomic.spinLoopHint();
        }
    };
    const pid = Win.GetCurrentProcessId();
    const process = windows.open_process(null, pid, false) orelse return error.ProcessUnavailable;
    defer windows.close(null, process);
    const birth = windows.process_birth(null, process) orelse return error.ProcessUnavailable;
    var owner = try Owner.init(std.testing.allocator, windows, 4096, 4, 32768, 4096, 16 * 1024 * 1024);
    defer owner.deinit();
    const requested_at = windows.now(null);
    try owner.register(1, pid, birth, requested_at);
    var context: Win.Context = .{};
    const worker = try std.Thread.spawn(.{}, Win.worker, .{&context});
    var joined = false;
    defer if (!joined) { context.release.store(true, .release); worker.join(); };
    while (context.tid.load(.acquire) == 0) std.atomic.spinLoopHint();
    const tid = context.tid.load(.acquire);
    try owner.observeThread(pid, tid, 123, false, windows.now(null));
    context.release.store(true, .release); worker.join(); joined = true;
    try owner.observeThread(pid, tid, 0, true, windows.now(null));
    try std.testing.expect(try owner.sample(1, requested_at, windows.now(null)));
    var bank = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer bank.deinit();
    const view = try owner.publish(bank.allocator());
    var found = false;
    for (view.requests.?[0].threads.?[0..view.requests.?[0].thread_count]) |row| if (row.thread_instance == 123) {
        found = true;
        try std.testing.expectEqual(@as(u32, 13), row.flags & 13);
        try std.testing.expect(row.module_path_len > 0);
        try std.testing.expectEqualSlices(u16, std.unicode.utf8ToUtf16LeStringLiteral("native-exit-worker"), row.description.?[0..row.description_len]);
        try std.testing.expect(row.read_through >= row.read_from);
    };
    try std.testing.expect(found);
    owner.remove(1);
    try std.testing.expectEqual(@as(u64, 0), owner.open_handles);
}
