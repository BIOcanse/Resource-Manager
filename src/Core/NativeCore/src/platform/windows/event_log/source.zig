const std = @import("std");
const win = @import("windows.zig");
pub const model = @import("model.zig");
pub const rules = @import("rules.zig");
pub const xml = @import("xml.zig");
pub const Api = win.Api;
pub const Event = model.Event;
pub const Sink = model.Sink;
pub const Bookmarks = model.Bookmarks;
pub const Work = *const fn (?*anyopaque, std.mem.Allocator) anyerror!void;
/// run invokes work exactly once, synchronously, while its borrowed region and
/// owner lock remain valid. Neither callback may retain the operation context.
pub const WorkScope = struct {
    context: ?*anyopaque,
    run: *const fn (?*anyopaque, Work, ?*anyopaque) anyerror!void,
};
pub const Limits = struct { event_xml_bytes: usize, bookmark_xml_bytes: usize, xpath_units: usize, thread_stack_bytes: usize };
const Slot = struct { signal: ?win.Handle = null, subscription: ?win.Handle = null, bookmark: ?win.Handle = null };
/// The owner keeps Source, rules and sink context at stable addresses until stop.
/// start/stop are serialized by that owner; only the worker calls sink after start.
/// Allocator is explicitly owned upstream; no global/page allocator is used.
pub const Source = struct {
    allocator: std.mem.Allocator,
    table: []const rules.Rule,
    sink: Sink,
    limits: Limits,
    api: Api,
    /// Set before start; one scope spans render, parse, bookmark and commit.
    scope: ?WorkScope = null,
    slots: [2]Slot = .{ .{}, .{} },
    stop_signal: ?win.Handle = null,
    thread: ?std.Thread = null,
    running: std.atomic.Value(bool) = .init(false),
    /// Read after stop joins. All delivery failures are observable, never dropped.
    failure: ?anyerror = null,
    pub fn init(allocator: std.mem.Allocator, table: []const rules.Rule, sink: Sink, limits: Limits, api: Api) !Source {
        if (limits.event_xml_bytes == 0 or limits.bookmark_xml_bytes == 0 or limits.xpath_units == 0 or limits.thread_stack_bytes == 0) return error.InvalidLimits;
        return .{ .allocator = allocator, .table = table, .sink = sink, .limits = limits, .api = api };
    }
    /// Synchronously replays saved-cursor history before returning. Both live
    /// subscriptions already exist during replay, so no query/subscription gap.
    /// Missing/overwritten bookmarks fail explicitly; caller chooses recovery.
    pub fn start(self: *Source, saved: Bookmarks) !void {
        if (self.stop_signal != null) return error.AlreadyStarted;
        self.failure = null;
        errdefer self.stop();
        self.stop_signal = try self.api.create_signal(self.api.context);
        const marks = [_]?[]const u8{ saved.system, saved.application };
        for (&self.slots, 0..) |*slot, i| {
            const channel: model.Channel = if (i == 0) .system else .application;
            var enabled = false;
            for (self.table) |rule| if (rule.channel == channel) {
                enabled = true;
                break;
            };
            if (!enabled) continue;
            const query = try rules.xpath(self.allocator, self.table, channel);
            defer self.allocator.free(query);
            if (query.len > self.limits.xpath_units) return error.XPathLimitExceeded;
            if (marks[i]) |saved_xml| if (saved_xml.len > self.limits.bookmark_xml_bytes) return error.XmlLimitExceeded;
            const mark: ?[:0]u16 = if (marks[i]) |text| try std.unicode.utf8ToUtf16LeAllocZ(self.allocator, text) else null;
            defer if (mark) |text| self.allocator.free(text);
            slot.bookmark = try self.api.bookmark(self.api.context, if (mark) |text| text.ptr else null);
            slot.signal = try self.api.create_signal(self.api.context);
            slot.subscription = try self.api.subscribe(self.api.context, slot.signal.?, if (i == 0) std.unicode.utf8ToUtf16LeStringLiteral("System") else std.unicode.utf8ToUtf16LeStringLiteral("Application"), query.ptr, slot.bookmark.?, marks[i] != null);
        }
        for (&self.slots) |*slot| try self.drain(slot, .catchup);
        self.running.store(true, .release);
        self.thread = std.Thread.spawn(.{ .stack_size = self.limits.thread_stack_bytes }, worker, .{self}) catch |err| {
            self.running.store(false, .release);
            return err;
        };
    }
    pub fn stop(self: *Source) void {
        if (self.stop_signal) |signal| self.api.set_signal(self.api.context, signal);
        if (self.thread) |thread| {
            thread.join();
            self.thread = null;
        }
        self.running.store(false, .release);
        for (&self.slots) |*slot| {
            if (slot.subscription) |h| self.api.close(self.api.context, h);
            if (slot.bookmark) |h| self.api.close(self.api.context, h);
            if (slot.signal) |h| self.api.close_signal(self.api.context, h);
            slot.* = .{};
        }
        if (self.stop_signal) |h| self.api.close_signal(self.api.context, h);
        self.stop_signal = null;
    }
    const ProcessOne = struct {
        source: *Source,
        slot: *Slot,
        handle: win.Handle,
        delivery: model.Delivery,
    };
    fn drain(self: *Source, slot: *Slot, delivery: model.Delivery) !void {
        const subscription = slot.subscription orelse return;
        while (try self.api.next(self.api.context, subscription)) |handle| {
            defer self.api.close(self.api.context, handle);
            var operation: ProcessOne = .{ .source = self, .slot = slot, .handle = handle, .delivery = delivery };
            if (self.scope) |scope| {
                try scope.run(scope.context, processOne, &operation);
            } else {
                // Test/standalone fallback. Production supplies the parent's
                // synchronous scoped lease, never a per-event native heap.
                try processOne(&operation, self.allocator);
            }
        }
    }
    fn processOne(context: ?*anyopaque, backing: std.mem.Allocator) !void {
        const operation: *ProcessOne = @ptrCast(@alignCast(context.?));
        const self = operation.source;
        const slot = operation.slot;
        var arena = std.heap.ArenaAllocator.init(backing);
        defer arena.deinit();
        const allocator = arena.allocator();
        const raw = try self.api.render(self.api.context, allocator, operation.handle, .event, self.limits.event_xml_bytes);
        const event = try xml.parse(allocator, self.table, raw, operation.delivery);
        try self.api.update(self.api.context, slot.bookmark.?, operation.handle);
        const mark = try self.api.render(self.api.context, allocator, slot.bookmark.?, .bookmark, self.limits.bookmark_xml_bytes);
        const channel: model.Channel = if (slot == &self.slots[0]) .system else .application;
        if (event) |e| if (e.channel != channel) return error.ChannelMismatch;
        try self.sink.commit(self.sink.context, allocator, channel, event, mark);
    }
    fn worker(self: *Source) void {
        defer self.running.store(false, .release);
        self.run() catch |err| {
            self.failure = err;
            if (self.sink.failure) |notify| notify(self.sink.context, err);
        };
    }
    fn run(self: *Source) !void {
        var handles: [3]win.Handle = undefined;
        var indices: [3]usize = undefined;
        handles[0] = self.stop_signal.?;
        var count: usize = 1;
        for (self.slots, 0..) |slot, i| if (slot.signal) |h| {
            handles[count] = h;
            indices[count] = i;
            count += 1;
        };
        while (true) {
            const ready = try self.api.wait(self.api.context, handles[0..count]);
            if (ready >= count) return error.InvalidWaitIndex;
            if (ready == 0) return;
            try self.drain(&self.slots[indices[ready]], .live);
        }
    }
};
pub const live_kernel = @import("live_kernel.zig");
test {
    _ = @import("tests.zig");
}
