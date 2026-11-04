//! Native report business owner. The host supplies only lifetime, existing frames,
//! Ledger allocation callbacks and transport. No event-log or metric polling.
const std = @import("std");
const p = @import("../../configuration/NativePolicy.g.zig");
const host = @import("../../telemetry/kernel_etw/host_allocator.zig");
const events = @import("../../platform/windows/event_log/source.zig");
const live = @import("../../platform/windows/event_log/live_kernel.zig");
const freeze = @import("../freeze_recorder/root.zig");
const t = @import("../freeze_recorder/types.zig");
const entries = @import("entries.zig");
const files = @import("store.zig");
extern "kernel32" fn GetSystemTimeAsFileTime(*std.os.windows.FILETIME) callconv(.winapi) void;
extern "kernel32" fn GetTickCount64() callconv(.winapi) u64;
extern "kernel32" fn AcquireSRWLockExclusive(*usize) callconv(.winapi) void;
extern "kernel32" fn ReleaseSRWLockExclusive(*usize) callconv(.winapi) void;
extern "kernel32" fn CreateEventW(?*anyopaque, i32, i32, ?[*:0]const u16) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn SetEvent(?*anyopaque) callconv(.winapi) i32;
extern "kernel32" fn WaitForSingleObject(?*anyopaque, u32) callconv(.winapi) u32;
extern "kernel32" fn CloseHandle(?*anyopaque) callconv(.winapi) i32;
pub const Changed = *const fn (?*anyopaque, u32) callconv(.c) void;
pub const ProcessReader = *const fn (?*anyopaque, usize, *u32, *u64, [*]u8, usize, *usize) callconv(.c) i32;
pub const ScratchCallbacks = extern struct {
    context: ?*anyopaque,
    run: ?*const fn (?*anyopaque, usize, *const fn (?*anyopaque, ?[*]u8, usize) callconv(.c) i32, ?*anyopaque) callconv(.c) i32,
};
pub const Output = struct {
    context: ?*anyopaque,
    write: *const fn (?*anyopaque, [*]const u8, usize) callconv(.c) i32,
    remaining: ?*usize = null,
    pub fn bytes(self: Output, text: []const u8) !void {
        if (self.remaining) |remaining| {
            if (text.len > remaining.*) return error.DocumentCapacityExceeded;
            remaining.* -= text.len;
        }
        if (self.write(self.context, text.ptr, text.len) != 0) return error.OutputFailed;
    }
    pub fn value(self: Output, a: std.mem.Allocator, value_: anytype) !void {
        const json = try std.json.Stringify.valueAlloc(a, value_, .{});
        defer a.free(json);
        try self.bytes(json);
    }
};
pub const EventSettings = struct {
    enabled: bool = p.system_events_enabled,
    history_after_ms: i64 = p.system_events_history_after_ms,
    event_tail_capacity: usize = p.system_events_event_tail_capacity,
    entry_capacity: usize = p.system_events_entry_capacity,
    rules: []const events.rules.Rule = &events.rules.defaults,
    live_kernel_root: []const u8 = "C:\\Windows\\LiveKernelReports",
    live_kernel_directories: []const []const u8 = &.{"WATCHDOG"},
    live_kernel_entry_capacity: usize = p.system_events_entry_capacity,
};
pub const Settings = struct {
    systemEvents: EventSettings = .{},
    freezeRecorder: t.Config = .{},
    ring_path: ?[]const u8 = null,
};
pub const Evidence = struct {
    category: events.model.Category,
    targetKind: events.model.TargetKind,
    target: []const u8,
    raw: entries.RawEvent,
    actualShutdownAt: ?i64 = null,
};
const ReviewDocument = struct {
    schemaVersion: u32 = 1,
    id: []const u8,
    category: []const u8 = "unexpected-shutdown",
    state: entries.State = .history,
    detailsRevision: u64 = 1,
    review: t.ReviewResult,
    tags: []const []const u8,
    records: []const t.Record,
    events: []const Evidence,
    dumpPath: ?[]const u8,
    thresholds: t.Config,
};
pub const Owner = struct {
    callbacks: host.Callbacks,
    scratch: ?ScratchCallbacks = null,
    root_bytes: usize,
    accounting: host.Accounting,
    source_accounting: host.Accounting,
    terminal: std.atomic.Value(u32) = .init(0),
    allocator_context: host.Context = undefined,
    source_allocator: host.Context = undefined,
    mutex: usize = 0,
    root: []const u8 = "",
    settings_path: []const u8 = "",
    entries_dir: []const u8 = "",
    evidence_dir: []const u8 = "",
    reviews_dir: []const u8 = "",
    settings: Settings = .{},
    parsed_settings: ?std.json.Parsed(Settings) = null,
    changed: Changed,
    changed_context: ?*anyopaque,
    source: ?events.Source = null,
    event_holders: u32 = 0,
    source_rules: ?std.json.Parsed([]const events.rules.Rule) = null,
    source_failure: ?[]const u8 = null,
    live_kernel_failure: ?[]const u8 = null,
    recorder_failure: ?[]const u8 = null,
    file: ?freeze.WindowsFile = null,
    ring: ?freeze.Ring = null,
    sample_gaps: u32 = std.math.maxInt(u32),
    sample_gap_reasons: [gap_names.len]?t.GapReason = @splat(null),
    process_top: [t.process_capacity]t.ProcessSample = @splat(.{}),
    process_count: u8 = 0,
    process_at: ?i64 = null,
    processes_complete: bool = false,
    startup_ms: i64,
    boot_ms: i64,
    boot_identity: [16]u8 = @splat(0),
    startup_reviewed: bool = false,
    signals: ?*anyopaque = null,
    deadline_thread: ?std.Thread = null,
    deadline_stop: std.atomic.Value(bool) = .init(false),
    next_deadline: ?i64 = null,
    unrecorded: u64 = 0,
    live_after_ms: i64 = 0,
    live_candidate_ms: i64 = 0,

    pub fn create(path: []const u8, callbacks: host.Callbacks, changed: Changed, context: ?*anyopaque) !*Owner {
        return createScoped(path, callbacks, null, changed, context);
    }
    // RuntimeRead: EventDriven("Service report owner created")
    pub fn createScoped(path: []const u8, callbacks: host.Callbacks, scratch: ?ScratchCallbacks, changed: Changed, context: ?*anyopaque) !*Owner {
        try callbacks.validate();
        const size = std.mem.alignForward(usize, @sizeOf(Owner), callbacks.page_size);
        const memory = callbacks.allocate.?(callbacks.context, 0x900, size, @backingInt(std.mem.Alignment.of(Owner))) orelse return error.OutOfMemory;
        const self: *Owner = @ptrCast(@alignCast(memory));
        const now = nowMs();
        self.* = .{ .callbacks = callbacks, .scratch = scratch, .root_bytes = size, .accounting = .{ .cap = p.freeze_recorder_owner_memory_bytes }, .source_accounting = .{ .cap = p.freeze_recorder_source_memory_bytes }, .changed = changed, .changed_context = context, .startup_ms = now, .boot_ms = now - @as(i64, @intCast(GetTickCount64())) };
        self.boot_identity = @import("../../platform/windows/event_log/boot_identity.zig").read() catch |err| {
            _ = callbacks.release.?(callbacks.context, 0x900, self, size, @backingInt(std.mem.Alignment.of(Owner)));
            return err;
        };
        self.allocator_context = try host.Context.init(callbacks, 0x901, &self.accounting, &self.terminal);
        self.source_allocator = try host.Context.init(callbacks, 0x902, &self.source_accounting, &self.terminal);
        errdefer self.destroy();
        const a = self.allocator();
        self.root = try a.dupe(u8, path);
        self.settings_path = try files.join(a, path, "FreezeRecorder\\settings.json");
        self.entries_dir = try files.join(a, path, "SystemEvents\\entries");
        self.evidence_dir = try files.join(a, path, "FreezeRecorder\\events");
        self.reviews_dir = try files.join(a, path, "FreezeRecorder\\reviews");
        if (try files.read(a, self.settings_path, p.freeze_recorder_max_document_bytes)) |json| {
            defer a.free(json);
            self.parsed_settings = try std.json.parseFromSlice(Settings, a, json, .{ .allocate = .alloc_always });
            self.settings = self.parsed_settings.?.value;
        }
        const capacity_path = try files.join(a, self.root, "SystemEvents\\capacity.json");
        defer a.free(capacity_path);
        if (try files.read(a, capacity_path, p.freeze_recorder_max_document_bytes)) |json| {
            defer a.free(json);
            var parsed = try std.json.parseFromSlice(struct { unrecordedItemCount: u64 }, a, json, .{});
            defer parsed.deinit();
            self.unrecorded = parsed.value.unrecordedItemCount;
        }
        try validate(self.settings);
        return self;
    }
    fn allocator(self: *Owner) std.mem.Allocator {
        return self.allocator_context.allocator();
    }
    pub fn lock(self: *Owner) void {
        AcquireSRWLockExclusive(&self.mutex);
    }
    pub fn unlock(self: *Owner) void {
        ReleaseSRWLockExclusive(&self.mutex);
    }
    fn scoped(self: *Owner, comptime body: anytype, arguments: anytype) !void {
        const Call = struct {
            owner: *Owner,
            args: @TypeOf(arguments),
            fn invoke(context: ?*anyopaque, a: std.mem.Allocator) !void {
                const call: *@This() = @ptrCast(@alignCast(context.?));
                try @call(.auto, body, .{ call.owner, a } ++ call.args);
            }
        };
        var call: Call = .{ .owner = self, .args = arguments };
        self.lock();
        defer self.unlock();
        try self.borrowLocked(Call.invoke, &call);
    }
    fn borrowLocked(self: *Owner, body: events.Work, context: ?*anyopaque) !void {
        if (self.scratch) |scratch| {
            const Call = struct {
                body: events.Work,
                context: ?*anyopaque,
                failure: ?anyerror = null,
                fn invoke(ctx: ?*anyopaque, memory: ?[*]u8, length: usize) callconv(.c) i32 {
                    const call: *@This() = @ptrCast(@alignCast(ctx.?));
                    const bytes = memory orelse return 503;
                    var fixed = std.heap.FixedBufferAllocator.init(bytes[0..length]);
                    var arena = std.heap.ArenaAllocator.init(fixed.allocator());
                    defer arena.deinit();
                    call.body(call.context, arena.allocator()) catch |err| {
                        call.failure = err;
                        return 500;
                    };
                    return 0;
                }
            };
            var call: Call = .{ .body = body, .context = context };
            const result = (scratch.run orelse return error.WorkspaceUnavailable)(scratch.context, p.freeze_recorder_owner_memory_bytes, Call.invoke, &call);
            if (call.failure) |err| return err;
            if (result != 0) return error.WorkspaceUnavailable;
        } else {
            // Unit tests supply a leak-checking allocator. Product construction
            // always provides the sole scoped workspace callbacks.
            var arena = std.heap.ArenaAllocator.init(self.allocator());
            defer arena.deinit();
            try body(context, arena.allocator());
        }
    }
    fn sourceScope(context: ?*anyopaque, body: events.Work, operation: ?*anyopaque) !void {
        const self: *Owner = @ptrCast(@alignCast(context.?));
        self.lock();
        defer self.unlock();
        try self.borrowLocked(body, operation);
    }
    fn changedStream(self: *Owner, stream: u32) void {
        self.changed(self.changed_context, stream);
    }
    fn ensureDirectories(self: *Owner, a: std.mem.Allocator) !void {
        const se = try files.join(a, self.root, "SystemEvents");
        defer a.free(se);
        const fr = try files.join(a, self.root, "FreezeRecorder");
        defer a.free(fr);
        try files.mkdir(a, self.root);
        try files.mkdir(a, se);
        try files.mkdir(a, fr);
        try files.mkdir(a, self.entries_dir);
        try files.mkdir(a, self.evidence_dir);
        try files.mkdir(a, self.reviews_dir);
    }
    fn markPath(self: *Owner, a: std.mem.Allocator, channel: events.model.Channel) ![]const u8 {
        return files.join(a, self.root, if (channel == .system) "SystemEvents\\system.bookmark.xml" else "SystemEvents\\application.bookmark.xml");
    }
    fn ringPath(self: *Owner, a: std.mem.Allocator) ![]const u8 {
        return if (self.settings.ring_path) |path| a.dupe(u8, path) else self.defaultRingPath(a);
    }
    fn defaultRingPath(self: *Owner, a: std.mem.Allocator) ![]const u8 {
        return files.join(a, self.root, "FreezeRecorder\\ring.bin");
    }
    fn defaults(self: *Owner, a: std.mem.Allocator) !Settings {
        var result: Settings = .{};
        result.ring_path = try self.defaultRingPath(a);
        // This line is supplied by the existing memory-protection authority,
        // not an independent recorder default or a saved threshold override.
        result.freezeRecorder.commit_immediate_percent = self.settings.freezeRecorder.commit_immediate_percent;
        return result;
    }

    /// No lock is held while starting/joining the source, because its sink uses
    /// the owner lock. Controller lifetime calls are serialized by the host.
    pub fn startEvents(self: *Owner) !void {
        try self.acquireEvents(1);
    }
    fn acquireEvents(self: *Owner, holder: u32) !void {
        self.lock();
        if (self.event_holders & holder != 0) {
            self.unlock();
            return error.AlreadyStarted;
        }
        const first = self.event_holders == 0;
        self.event_holders |= holder;
        self.unlock();
        errdefer {
            self.lock();
            self.event_holders &= ~holder;
            self.unlock();
        }
        if (first) self.startEventSource() catch |err| {
            sourceFailed(self, err);
            return err;
        };
        if (self.signals) |signal| _ = SetEvent(signal);
    }
    fn releaseEvents(self: *Owner, holder: u32) void {
        self.lock();
        self.event_holders &= ~holder;
        const last = self.event_holders == 0;
        self.unlock();
        if (last) self.stopEventSource();
    }
    fn startEventSource(self: *Owner) !void {
        var arena = std.heap.ArenaAllocator.init(self.allocator());
        const a = arena.allocator();
        defer {
            self.lock();
            arena.deinit();
            self.unlock();
        }
        const saved = try self.prepareEvents(a);
        errdefer self.stopEventSource();
        try self.source.?.start(saved);
        try self.finishStart(a);
    }
    fn prepareEvents(self: *Owner, a: std.mem.Allocator) !events.Bookmarks {
        self.lock();
        defer self.unlock();
        if (self.source != null) return error.AlreadyStarted;
        try self.ensureDirectories(a);
        const system_path = try self.markPath(a, .system);
        const app_path = try self.markPath(a, .application);
        const system = try files.read(a, system_path, p.system_events_source_bookmark_xml_bytes);
        const application = try files.read(a, app_path, p.system_events_source_bookmark_xml_bytes);
        self.source_failure = null;
        const rules_json = try std.json.Stringify.valueAlloc(a, self.settings.systemEvents.rules, .{});
        self.source_rules = try std.json.parseFromSlice([]const events.rules.Rule, self.source_allocator.allocator(), rules_json, .{ .allocate = .alloc_always });
        errdefer {
            self.source_rules.?.deinit();
            self.source_rules = null;
        }
        self.source = try events.Source.init(self.source_allocator.allocator(), self.source_rules.?.value, .{ .context = self, .commit = commit, .failure = sourceFailed }, .{
            .event_xml_bytes = p.system_events_source_event_xml_bytes,
            .bookmark_xml_bytes = p.system_events_source_bookmark_xml_bytes,
            .xpath_units = p.system_events_source_xpath_units,
            .thread_stack_bytes = p.system_events_source_thread_stack_bytes,
        }, .{});
        self.source.?.scope = .{ .context = self, .run = sourceScope };
        return .{ .system = system, .application = application };
    }
    fn finishStart(self: *Owner, a: std.mem.Allocator) !void {
        try self.scoped(finishStartIn, .{});
        _ = a;
    }
    fn finishStartIn(self: *Owner, a: std.mem.Allocator) !void {
        self.scanLive(a) catch |err| {
            self.live_kernel_failure = @errorName(err);
        };
        try self.expireAll(a, nowMs());
        self.reviewStartup(a) catch |err| {
            self.recorder_failure = @errorName(err);
            self.changedStream(1);
        };
        self.signals = CreateEventW(null, 0, 0, null) orelse return error.CreateEventFailed;
        self.deadline_stop.store(false, .release);
        self.deadline_thread = try std.Thread.spawn(.{ .stack_size = p.freeze_recorder_thread_stack_bytes }, deadlineRun, .{self});
        self.changedStream(0);
    }
    pub fn stopEvents(self: *Owner) void {
        self.releaseEvents(1);
    }
    fn stopEventSource(self: *Owner) void {
        self.deadline_stop.store(true, .release);
        if (self.signals) |signal| _ = SetEvent(signal);
        if (self.deadline_thread) |thread| {
            thread.join();
            self.deadline_thread = null;
        }
        if (self.source) |*source| source.stop();
        self.lock();
        defer self.unlock();
        if (self.signals) |signal| {
            _ = CloseHandle(signal);
            self.signals = null;
        }
        self.source = null;
        if (self.source_rules) |*rules_| rules_.deinit();
        self.source_rules = null;
        self.changedStream(0);
    }
    fn sourceFailed(context: *anyopaque, err: anyerror) void {
        const self: *Owner = @ptrCast(@alignCast(context));
        self.lock();
        defer self.unlock();
        self.source_failure = @errorName(err);
        self.changedStream(0);
    }
    fn commit(context: *anyopaque, a: std.mem.Allocator, channel: events.model.Channel, event: ?events.Event, bookmark: []const u8) !void {
        const self: *Owner = @ptrCast(@alignCast(context));
        // Source.scope owns the mutex and the local Ledger lease through commit.
        if (event) |e| {
            try self.ingest(a, e);
            if (e.category == .gpu_reset) self.scanLive(a) catch |err| {
                self.live_kernel_failure = @errorName(err);
            };
            if (e.category == .unexpected_shutdown and self.startup_reviewed and e.timestamp_ms >= self.boot_ms) {
                self.startup_ms = @max(self.startup_ms, nowMs());
                self.reviewStartup(a) catch |err| {
                    self.recorder_failure = @errorName(err);
                    self.changedStream(1);
                };
            }
        }
        // Evidence is durable before the cursor. A crash between replaces can
        // replay one event; channel+record identity makes ingest idempotent.
        const path = try self.markPath(a, channel);
        try files.atomic(a, path, bookmark);
        self.changedStream(0);
        if (self.signals) |signal| _ = SetEvent(signal);
    }
    fn ingest(self: *Owner, a: std.mem.Allocator, e: events.Event) !void {
        const evidence: Evidence = .{ .category = e.category, .targetKind = e.target_kind, .target = e.target, .raw = .{ .channel = @tagName(e.channel), .provider = e.provider, .eventId = e.event_id, .recordId = e.record_id, .at = e.timestamp_ms, .xml = e.raw_xml, .bugcheckCode = e.bugcheck_code, .dumpPath = e.dump_path }, .actualShutdownAt = e.actual_shutdown_ms };
        const journal_name = try std.fmt.allocPrint(a, "{s}-{d}-{d}.json", .{ @tagName(e.channel), e.record_id, e.timestamp_ms });
        const journal = try files.join(a, self.evidence_dir, journal_name);
        const replay = try files.exists(a, journal);
        try self.save(a, journal, evidence);
        if (e.category == .unexpected_shutdown or !self.settings.systemEvents.enabled) return;
        var id: [64]u8 = undefined;
        entries.identity(@tagName(e.category), e.target, &id);
        const name = try std.fmt.allocPrint(a, "{s}.json", .{id});
        const path = try files.join(a, self.entries_dir, name);
        var item: entries.Entry = .{ .id = &id, .category = @tagName(e.category), .target = e.target, .firstObservedAt = e.timestamp_ms, .lastObservedAt = e.timestamp_ms };
        if (try files.read(a, path, p.freeze_recorder_max_document_bytes)) |json| {
            const parsed = try std.json.parseFromSlice(entries.Entry, a, json, .{});
            item = parsed.value;
        } else {
            var counter: Counter = .{};
            try files.each(a, self.entries_dir, &counter, Counter.visit);
            if (counter.count >= self.settings.systemEvents.entry_capacity) {
                if (!replay) {
                    self.unrecorded +|= 1;
                    try self.save(a, try files.join(a, self.root, "SystemEvents\\capacity.json"), .{ .unrecordedItemCount = self.unrecorded });
                }
                return;
            }
        }
        if (!try entries.merge(a, &item, evidence.raw, self.settings.systemEvents.event_tail_capacity)) return;
        _ = entries.expire(&item, nowMs(), self.settings.systemEvents.history_after_ms);
        try self.save(a, path, item);
        if (item.state == .active) self.addDeadline(item.lastObservedAt +| self.settings.systemEvents.history_after_ms);
    }
    pub fn ingestForTest(self: *Owner, event: events.Event) !void {
        if (!@import("builtin").is_test) @compileError("test-only entry");
        try self.scoped(ingestTestIn, .{event});
    }
    fn ingestTestIn(self: *Owner, a: std.mem.Allocator, event: events.Event) !void {
        try self.ensureDirectories(a);
        try self.ingest(a, event);
    }
    fn save(_: *Owner, a: std.mem.Allocator, path: []const u8, value_: anytype) !void {
        const json = try std.json.Stringify.valueAlloc(a, value_, .{});
        if (json.len > p.freeze_recorder_max_document_bytes) return error.DocumentCapacityExceeded;
        try files.atomic(a, path, json);
    }
    fn scanLive(self: *Owner, a: std.mem.Allocator) !void {
        const marker = try files.join(a, self.root, "SystemEvents\\live-kernel.json");
        if (self.live_after_ms == 0) if (try files.read(a, marker, p.freeze_recorder_max_document_bytes)) |json| {
            const parsed = try std.json.parseFromSlice(struct { through: i64 }, a, json, .{});
            self.live_after_ms = parsed.value.through;
        };
        self.live_candidate_ms = self.live_after_ms;
        var ctx: LiveContext = .{ .owner = self, .a = a };
        try live.scan(a, .{ .root = self.settings.systemEvents.live_kernel_root, .display_directories = self.settings.systemEvents.live_kernel_directories, .after_ms = self.live_after_ms, .max_entries = self.settings.systemEvents.live_kernel_entry_capacity }, .{ .context = &ctx, .display = liveReport });
        try self.save(a, marker, .{ .through = self.live_candidate_ms });
        self.live_after_ms = self.live_candidate_ms;
        self.live_kernel_failure = null;
    }
    fn liveReport(context: *anyopaque, report: events.model.LiveKernelReport) !void {
        const ctx: *LiveContext = @ptrCast(@alignCast(context));
        const self = ctx.owner;
        var arena = std.heap.ArenaAllocator.init(ctx.a);
        defer arena.deinit();
        try self.ingest(arena.allocator(), .{ .category = .gpu_reset, .target_kind = .gpu_device, .target = "gpu:unresolved", .channel = .system, .provider = "LiveKernelReports", .event_id = 0, .record_id = std.hash.Wyhash.hash(@bitCast(report.timestamp_ms), report.path), .timestamp_ms = report.timestamp_ms, .timestamp_utc = "", .raw_xml = "", .dump_path = report.path });
        self.live_candidate_ms = @max(self.live_candidate_ms, report.timestamp_ms);
    }
    fn addDeadline(self: *Owner, at: i64) void {
        self.next_deadline = @min(self.next_deadline orelse at, at);
    }
    fn deadlineRun(self: *Owner) void {
        while (!self.deadline_stop.load(.acquire)) {
            self.lock();
            const wait: u32 = if (self.next_deadline) |at| @intCast(@min(@as(u64, @intCast(@max(0, at - nowMs()))), std.math.maxInt(u32) - 1)) else std.math.maxInt(u32);
            self.unlock();
            const result = WaitForSingleObject(self.signals, wait);
            if (self.deadline_stop.load(.acquire)) return;
            if (result != 0 and result != 258) {
                sourceFailed(self, error.DeadlineWaitFailed);
                return;
            }
            self.scoped(expireAll, .{nowMs()}) catch |err| {
                self.lock();
                defer self.unlock();
                self.source_failure = @errorName(err);
                self.changedStream(0);
            };
        }
    }
    fn expireAll(self: *Owner, a: std.mem.Allocator, now: i64) !void {
        self.next_deadline = null;
        if (!self.settings.systemEvents.enabled) return;
        var scan: Scan = .{ .owner = self, .a = a, .now = now, .mode = .expire };
        try files.each(a, self.entries_dir, &scan, Scan.visit);
        if (scan.changed) self.changedStream(0);
    }
    pub fn startRecorder(self: *Owner) !void {
        self.startRecorderOwned() catch |err| {
            self.lock();
            defer self.unlock();
            self.recorder_failure = @errorName(err);
            self.changedStream(1);
            return err;
        };
    }
    fn startRecorderOwned(self: *Owner) !void {
        // A named recorder holder keeps shutdown/event evidence available even
        // when the independent system-event report producer is switched off.
        try self.acquireEvents(2);
        errdefer self.releaseEvents(2);
        try self.scoped(startRecorderIn, .{});
    }
    fn startRecorderIn(self: *Owner, a: std.mem.Allocator) !void {
        if (self.ring != null) return error.AlreadyStarted;
        try self.ensureDirectories(a);
        // Capture the old window before any new frame can overwrite it. If the
        // event block is not ready yet, submit remains gated until catch-up.
        const path = try self.ringPath(a);
        const wide = try files.wide(a, path);
        self.file = try freeze.WindowsFile.open(wide.ptr, true);
        self.ring = freeze.Ring.init(self.file.?.ops(), self.settings.freezeRecorder, true) catch |err| {
            freeze.WindowsFile.close(&self.file.?);
            self.file = null;
            return err;
        };
        self.recorder_failure = null;
        self.changedStream(1);
    }
    pub fn stopRecorder(self: *Owner) void {
        self.lock();
        if (self.ring) |*ring| ring.close();
        self.ring = null;
        self.file = null;
        self.process_top = @splat(.{});
        self.process_count = 0;
        self.process_at = null;
        self.processes_complete = false;
        self.changedStream(1);
        self.unlock();
        self.releaseEvents(2);
    }
    pub fn submit(self: *Owner, input: []const u8) !void {
        try self.scoped(submitIn, .{input});
    }
    fn submitIn(self: *Owner, a: std.mem.Allocator, input: []const u8) !void {
        const ring = if (self.ring) |*ring| ring else return error.NotRunning;
        if (self.settings.systemEvents.enabled and !self.startup_reviewed) return; // preserve preboot ring until F3 catchup
        const parsed = try std.json.parseFromSlice(SampleInput, a, input, .{});
        const frame = parsed.value;
        var sample: t.Sample = .{ .timestamp_ms = frame.timestamp_ms, .commit_bytes = frame.commit_bytes, .commit_limit_bytes = frame.commit_limit_bytes, .physical_total_bytes = frame.physical_total_bytes, .physical_available_bytes = frame.physical_available_bytes, .paged_pool_bytes = frame.paged_pool_bytes, .nonpaged_pool_bytes = frame.nonpaged_pool_bytes, .interrupt_cpu_percent = frame.interrupt_cpu_percent, .longest_interrupt_us = frame.longest_interrupt_us, .kernel_leak_active = frame.kernel_leak_active, .disks_complete = frame.disks_complete and frame.disks.len <= t.disk_capacity, .processes_complete = self.processes_complete, .top_processes = self.process_top, .process_count = self.process_count };
        sample.interrupt_timestamp_ms = frame.interrupt_timestamp_ms;
        sample.interrupt_window_ms = frame.interrupt_window_ms;
        sample.interrupt_unavailable_reason = frame.interrupt_unavailable_reason;
        sample.kernel_leak_unavailable_reason = if (sample.kernel_leak_active == null) frame.kernel_leak_unavailable_reason else null;
        if (sample.interrupt_timestamp_ms) |at| {
            const max_age = @max(self.settings.freezeRecorder.sample_interval_ms, sample.interrupt_window_ms orelse 0);
            const invalid = at > sample.timestamp_ms;
            if (invalid or sample.timestamp_ms -| at > max_age) {
                sample.interrupt_cpu_percent = null;
                sample.longest_interrupt_us = null;
                sample.interrupt_unavailable_reason = if (invalid) .@"window-mismatch" else .@"stale-publication";
            }
        }
        if (sample.interrupt_cpu_percent != null and sample.longest_interrupt_us != null)
            sample.interrupt_unavailable_reason = null;
        if (self.process_at) |at| {
            if (at > frame.timestamp_ms or frame.timestamp_ms - at > self.settings.freezeRecorder.sample_interval_ms) sample.processes_complete = false;
        } else sample.processes_complete = false;
        sample.disk_count = @intCast(@min(frame.disks.len, t.disk_capacity));
        @memcpy(sample.disks[0..sample.disk_count], frame.disks[0..sample.disk_count]);
        try self.setThresholdLocked(a, frame.commit_immediate_percent);
        ring.append(&sample) catch |err| {
            self.recorder_failure = @errorName(err);
            self.changedStream(1);
            return err;
        };
        self.sample_gaps = gapsFor(&sample);
        self.sample_gap_reasons = reasonsFor(&sample);
        self.changedStream(1);
    }
    /// Borrow the existing managed publication during this call. Only three
    /// process fields and their source timestamp remain as join metadata.
    pub fn processFrame(self: *Owner, at: i64, available: bool, count: usize, reader: ProcessReader, context: ?*anyopaque) !void {
        self.lock();
        defer self.unlock();
        self.process_at = at;
        self.processes_complete = false;
        self.process_count = 0;
        var sample: t.Sample = .{};
        var complete = available and count <= p.freeze_recorder_process_input_capacity;
        for (0..@min(count, p.freeze_recorder_process_input_capacity)) |i| {
            var row: t.ProcessSample = .{};
            var len: usize = 0;
            const result = reader(context, i, &row.pid, &row.private_commit_bytes, &row.image_name, row.image_name.len, &len);
            if (result == 1) {
                complete = false;
                continue;
            }
            if (result != 0) return error.ProcessProjectionFailed;
            if (len == 0 or len > row.image_name.len or row.pid == 0) {
                complete = false;
                continue;
            }
            row.image_name_len = @intCast(len);
            sample.considerProcess(row);
        }
        self.process_top = sample.top_processes;
        self.process_count = sample.process_count;
        self.processes_complete = complete;
    }
    pub fn setting(self: *Owner, key: u32) f64 {
        self.lock();
        defer self.unlock();
        return switch (key) {
            0 => if (self.settings.systemEvents.enabled) 1 else 0,
            1 => if (self.settings.freezeRecorder.enabled) 1 else 0,
            2 => @floatFromInt(self.settings.freezeRecorder.sample_interval_ms),
            else => -1,
        };
    }
    pub fn setThreshold(self: *Owner, threshold: f64) !void {
        self.lock();
        defer self.unlock();
        var arena = std.heap.ArenaAllocator.init(self.allocator());
        defer arena.deinit();
        try self.setThresholdLocked(arena.allocator(), if (threshold < 0) null else threshold);
    }
    fn setThresholdLocked(self: *Owner, a: std.mem.Allocator, threshold: ?f64) !void {
        if (threshold) |value| if (!std.math.isFinite(value) or value > 100 or value < 0) return error.InvalidConfig;
        self.settings.freezeRecorder.commit_immediate_percent = threshold;
        _ = a;
    }
    fn validate(settings: Settings) !void {
        try settings.freezeRecorder.validate();
        if (settings.systemEvents.history_after_ms <= 0 or settings.systemEvents.event_tail_capacity == 0 or settings.systemEvents.entry_capacity == 0 or settings.systemEvents.live_kernel_entry_capacity == 0) return error.InvalidConfig;
        if (settings.ring_path) |path| {
            if (path.len < 3 or !(path[1] == ':' or std.mem.startsWith(u8, path, "\\\\")) or std.mem.indexOfScalar(u8, path, 0) != null) return error.InvalidPath;
        }
    }
    pub fn request(self: *Owner, operation: u32, input: []const u8, output: Output) !void {
        var budget: usize = p.freeze_recorder_max_document_bytes;
        var bounded = output;
        bounded.remaining = &budget;
        try self.scoped(requestIn, .{ operation, input, bounded });
    }
    fn requestIn(self: *Owner, a: std.mem.Allocator, operation: u32, input: []const u8, output: Output) !void {
        switch (operation) {
            0 => {
                try output.bytes("{\"schemaVersion\":1,\"type\":\"system-events\",\"status\":");
                try output.value(a, if (self.source_failure != null) "unavailable" else if (self.source != null and self.settings.systemEvents.enabled) "observing" else "not-observed");
                try output.bytes(",\"failure\":");
                try output.value(a, self.source_failure);
                try output.bytes(",\"liveKernelFailure\":");
                try output.value(a, self.live_kernel_failure);
                try output.bytes(",\"unrecordedItemCount\":");
                try output.value(a, self.unrecorded);
                try output.bytes(",\"items\":[");
                try self.list(a, self.entries_dir, output);
                try output.bytes("]}");
            },
            1, 6 => {
                try self.ensureDirectories(a);
                var scan: Scan = .{ .owner = self, .a = a, .mode = if (operation == 1) .clear_history else .clear_review };
                try files.each(a, if (operation == 1) self.entries_dir else self.reviews_dir, &scan, Scan.visit);
                try output.value(a, .{ .removedCount = scan.count });
                self.changedStream(if (operation == 1) 0 else 1);
            },
            2 => {
                try output.bytes("{\"schemaVersion\":1,\"type\":\"freeze-recorder\",\"running\":");
                try output.value(a, self.ring != null);
                try output.bytes(",\"lastRecordAt\":");
                try output.value(a, if (self.ring) |ring| ring.latest_timestamp_ms else null);
                try output.bytes(",\"failure\":");
                try output.value(a, self.recorder_failure);
                try output.bytes(",\"gaps\":");
                try self.writeGaps(a, output);
                try output.bytes(",\"gapDetails\":");
                try self.writeGapDetails(a, output);
                try output.bytes(",\"reviews\":[");
                try self.listReviews(a, output);
                try output.bytes("]}");
            },
            3 => {
                try output.bytes("{\"schemaVersion\":1,\"type\":\"freeze-reviews\",\"items\":[");
                try self.listReviews(a, output);
                try output.bytes("]}");
            },
            4, 5 => {
                if (input.len != 16) return error.NotFound;
                for (input) |c| if (!std.ascii.isHex(c)) return error.NotFound;
                const name = try std.fmt.allocPrint(a, "{s}.json", .{input});
                const path = try files.join(a, self.reviews_dir, name);
                const json = (try files.read(a, path, p.freeze_recorder_max_document_bytes)) orelse return error.NotFound;
                try output.bytes(json);
            },
            7 => try output.value(a, self.settings),
            9 => try output.value(a, try self.defaults(a)),
            8 => {
                var next = try std.json.parseFromSlice(Settings, self.allocator(), input, .{ .allocate = .alloc_always });
                var transferred = false;
                defer if (!transferred) next.deinit();
                try validate(next.value);
                const old = self.settings;
                const old_rules = try std.json.Stringify.valueAlloc(a, old.systemEvents.rules, .{});
                const new_rules = try std.json.Stringify.valueAlloc(a, next.value.systemEvents.rules, .{});
                if (self.source != null and !std.mem.eql(u8, old_rules, new_rules)) return error.Busy;
                if (self.ring != null and (old.freezeRecorder.slots != next.value.freezeRecorder.slots or !optionalEqual(old.ring_path, next.value.ring_path))) return error.Busy;
                if (old.freezeRecorder.slots != next.value.freezeRecorder.slots and optionalEqual(old.ring_path, next.value.ring_path)) {
                    const old_path = try self.ringPath(a);
                    if (try files.exists(a, old_path)) return error.RingMigrationRequired;
                }
                // Validate editable rule syntax before committing settings.
                for ([_]events.model.Channel{ .system, .application }) |channel| _ = try events.rules.xpath(a, next.value.systemEvents.rules, channel);
                try self.ensureDirectories(a);
                try self.save(a, self.settings_path, next.value);
                if (self.parsed_settings) |*parsed| parsed.deinit();
                self.parsed_settings = next;
                self.settings = next.value;
                transferred = true;
                try output.value(a, self.settings);
                self.changedStream(2);
                if (self.signals) |signal| _ = SetEvent(signal);
            },
            else => return error.InvalidOperation,
        }
    }
    fn writeGaps(self: *Owner, a: std.mem.Allocator, output: Output) !void {
        var gaps: std.ArrayList([]const u8) = .empty;
        if (self.sample_gaps == std.math.maxInt(u32)) try gaps.append(a, "no-sample") else {
            for (gap_names, 0..) |name, bit| if (self.sample_gaps & (@as(u32, 1) << @intCast(bit)) != 0) {
                try gaps.append(a, name);
            };
        }
        try output.value(a, gaps.items);
    }
    fn writeGapDetails(self: *Owner, a: std.mem.Allocator, output: Output) !void {
        const Gap = struct { field: []const u8, reason: t.GapReason };
        var rows: [gap_names.len]Gap = undefined;
        var count: usize = 0;
        if (self.sample_gaps == std.math.maxInt(u32)) {
            rows[0] = .{ .field = "no-sample", .reason = .@"not-published" };
            count = 1;
        } else for (gap_names, 0..) |name, bit| {
            if (self.sample_gaps & (@as(u32, 1) << @intCast(bit)) == 0) continue;
            rows[count] = .{ .field = name, .reason = self.sample_gap_reasons[bit] orelse .@"source-unavailable" };
            count += 1;
        }
        try output.value(a, rows[0..count]);
    }
    fn list(self: *Owner, a: std.mem.Allocator, dir: []const u8, output: Output) !void {
        var scan: Scan = .{ .owner = self, .a = a, .mode = .list, .output = output };
        try files.each(a, dir, &scan, Scan.visit);
    }
    fn listReviews(self: *Owner, a: std.mem.Allocator, output: Output) !void {
        var scan: Scan = .{ .owner = self, .a = a, .mode = .list_review, .output = output };
        try files.each(a, self.reviews_dir, &scan, Scan.visit);
    }
    fn reviewStartup(self: *Owner, a: std.mem.Allocator) !void {
        const pending_path = try files.join(a, self.root, "FreezeRecorder\\startup-window.json");
        var collect: ReviewCollect = undefined;
        if (self.startup_reviewed) {
            const json = (try files.read(a, pending_path, p.freeze_recorder_max_document_bytes)) orelse return;
            const parsed = try std.json.parseFromSlice(ReviewDocument, a, json, .{});
            collect = .{ .owner = self, .a = a, .review = try freeze.Review.init(self.settings.freezeRecorder, parsed.value.review.last_record_timestamp_ms, self.startup_ms) };
            for (parsed.value.records) |record| try ReviewCollect.record(&collect, &record);
        } else {
            var local_file: freeze.WindowsFile = undefined;
            var local_ring: freeze.Ring = undefined;
            var opened = false;
            defer if (opened) local_ring.close();
            const ring: *freeze.Ring = if (self.ring) |*r| r else blk: {
                const path = try self.ringPath(a);
                const wide = try files.wide(a, path);
                // A missing ring means there was no recorder evidence; don't invent it.
                if (!try files.exists(a, path)) {
                    self.startup_reviewed = true;
                    return;
                }
                local_file = try freeze.WindowsFile.open(wide.ptr, false);
                local_ring = freeze.Ring.init(local_file.ops(), self.settings.freezeRecorder, false) catch |err| {
                    freeze.WindowsFile.close(&local_file);
                    return err;
                };
                opened = true;
                break :blk &local_ring;
            };
            const last = (try ring.latest()) orelse {
                self.startup_reviewed = true;
                return;
            };
            // Service restart in the current boot is not a second shutdown incident.
            if (last.sample.timestamp_ms >= self.boot_ms) {
                self.startup_reviewed = true;
                return;
            }
            collect = .{
                .owner = self,
                .a = a,
                .review = try freeze.Review.init(self.settings.freezeRecorder, last.sample.timestamp_ms, self.startup_ms),
            };
            const stream = try ring.stream(collect.review.result.review_from_ms, last.sample.timestamp_ms, &collect, ReviewCollect.record);
            if (stream.missing_records != 0 or ring.invalid_slots != 0) collect.review.markIncomplete();
        }
        try files.each(a, self.evidence_dir, &collect, ReviewCollect.event);
        collect.review.setEventsComplete();
        const result = collect.review.finish();
        const stable_key = std.hash.Wyhash.hash(@bitCast(result.last_record_timestamp_ms), &self.boot_identity);
        const id = try std.fmt.allocPrint(a, "{x:0>16}", .{stable_key});
        var tags: std.ArrayList([]const u8) = .empty;
        inline for (@typeInfo(t.Tags).@"struct".field_names) |field| if (@field(result.tags, field)) {
            const text = try a.dupe(u8, field);
            for (text) |*c| if (c.* == '_') {
                c.* = '-';
            };
            try tags.append(a, text);
        };
        var document: ReviewDocument = .{ .id = id, .review = result, .tags = tags.items, .records = collect.records.items, .events = collect.event_rows.items, .dumpPath = collect.dump, .thresholds = self.settings.freezeRecorder };
        // One startup snapshot on disk lets late boot markers enrich the same
        // incident after live sampling has already wrapped the old ring window.
        try self.save(a, pending_path, document);
        if (result.unexpected_shutdown) {
            const path = try files.join(a, self.reviews_dir, try std.fmt.allocPrint(a, "{s}.json", .{id}));
            // Clear-history removes reports, but this checkpoint survives clearing
            // and prevents this old boot from being reported again after restart.
            const checkpoint = try files.join(a, self.root, "FreezeRecorder\\reviewed-incident.json");
            var already = false;
            if (try files.read(a, checkpoint, p.freeze_recorder_max_document_bytes)) |json| {
                const parsed = try std.json.parseFromSlice(struct { id: []const u8 }, a, json, .{});
                already = std.mem.eql(u8, parsed.value.id, id);
            }
            if (!already or try files.exists(a, path)) {
                // A cleared incident stays cleared; an existing one can gain
                // a delayed bugcheck code/dump while keeping its stable identity.
                if (try files.read(a, path, p.freeze_recorder_max_document_bytes)) |json| {
                    const old = try std.json.parseFromSlice(ReviewDocument, a, json, .{});
                    document.detailsRevision = old.value.detailsRevision +| 1;
                }
                try self.save(a, path, document);
                try self.save(a, checkpoint, .{ .id = id });
                self.changedStream(1);
            }
        }
        self.startup_reviewed = true;
    }
    pub fn destroy(self: *Owner) void {
        self.stopEvents();
        self.stopRecorder();
        const a = self.allocator();
        if (self.parsed_settings) |*parsed| parsed.deinit();
        a.free(self.root);
        a.free(self.settings_path);
        a.free(self.entries_dir);
        a.free(self.evidence_dir);
        a.free(self.reviews_dir);
        self.source_allocator.deinit();
        self.allocator_context.deinit();
        const callbacks = self.callbacks;
        const size = self.root_bytes;
        _ = callbacks.release.?(callbacks.context, 0x900, self, size, @backingInt(std.mem.Alignment.of(Owner)));
    }
};
fn optionalEqual(a: ?[]const u8, b: ?[]const u8) bool {
    if (a == null or b == null) return a == null and b == null;
    return std.mem.eql(u8, a.?, b.?);
}
// RuntimeRead: Sampled
pub fn nowMs() i64 {
    var time: std.os.windows.FILETIME = undefined;
    GetSystemTimeAsFileTime(&time);
    const ticks = (@as(u64, time.dwHighDateTime) << 32) | time.dwLowDateTime;
    return @as(i64, @intCast(ticks / 10000)) - 11644473600000;
}
const Counter = struct {
    count: usize = 0,
    fn visit(context: ?*anyopaque, _: []const u8) !void {
        const self: *Counter = @ptrCast(@alignCast(context.?));
        self.count += 1;
    }
};
const LiveContext = struct { owner: *Owner, a: std.mem.Allocator };
const gap_names = [_][]const u8{ "interrupt-cpu-share", "longest-interrupt", "active-kernel-leak-system-item", "process-private-commit", "physical-disk-inventory", "physical-disk-completed-operations", "commit", "physical-memory", "kernel-pools" };
fn reasonsFor(s: *const t.Sample) [gap_names.len]?t.GapReason {
    var result: [gap_names.len]?t.GapReason = @splat(null);
    result[0] = s.interrupt_unavailable_reason;
    result[1] = s.interrupt_unavailable_reason;
    result[2] = s.kernel_leak_unavailable_reason;
    for (s.disks[0..s.disk_count]) |disk| if (disk.completed_operations == null) {
        result[5] = disk.completed_operations_unavailable_reason;
        break;
    };
    return result;
}
fn gapsFor(s: *const t.Sample) u32 {
    var result: u32 = 0;
    const flags = [_]bool{ s.interrupt_cpu_percent == null, s.longest_interrupt_us == null, s.kernel_leak_active == null, !s.processes_complete, !s.disks_complete, false, s.commit_bytes == null or s.commit_limit_bytes == null, s.physical_available_bytes == null or s.physical_total_bytes == null, s.paged_pool_bytes == null or s.nonpaged_pool_bytes == null };
    for (flags, 0..) |missing, bit| if (missing) {
        result |= @as(u32, 1) << @intCast(bit);
    };
    for (s.disks[0..s.disk_count]) |disk| if (disk.completed_operations == null) {
        result |= 1 << 5;
        break;
    };
    return result;
}
const ReviewCollect = struct {
    owner: *Owner,
    a: std.mem.Allocator,
    review: freeze.Review,
    records: std.ArrayList(t.Record) = .empty,
    event_rows: std.ArrayList(Evidence) = .empty,
    dump: ?[]const u8 = null,
    fn record(context: ?*anyopaque, value: *const t.Record) !void {
        const self: *ReviewCollect = @ptrCast(@alignCast(context.?));
        self.review.addSample(value);
        try self.records.append(self.a, value.*);
    }
    fn event(context: ?*anyopaque, path: []const u8) !void {
        const self: *ReviewCollect = @ptrCast(@alignCast(context.?));
        var arena = std.heap.ArenaAllocator.init(self.a);
        defer arena.deinit();
        const a = arena.allocator();
        const json = (try files.read(a, path, p.freeze_recorder_max_document_bytes)) orelse return;
        const parsed = try std.json.parseFromSlice(Evidence, a, json, .{});
        const evidence = parsed.value;
        if (evidence.category == .unexpected_shutdown) {
            if (evidence.raw.at < self.owner.boot_ms or evidence.raw.at > self.owner.startup_ms) return;
        } else if (evidence.raw.at < self.review.result.review_from_ms or evidence.raw.at > self.review.result.review_to_ms) return;
        const kind: ?t.EventKind = switch (evidence.category) {
            .memory_exhausted => .resource_exhaustion,
            .storage_retry_reset => .storage_retry_reset,
            .gpu_reset => .gpu_reset,
            .hardware_error => .hardware_error,
            .unexpected_shutdown => if (evidence.raw.bugcheckCode != null or evidence.raw.eventId == 1001) .bugcheck else .unexpected_shutdown,
            .desktop_crash => null,
        };
        if (kind) |k| self.review.addEvent(&.{ .kind = k, .timestamp_ms = evidence.raw.at, .identity = evidence.raw.recordId, .shutdown_timestamp_ms = evidence.actualShutdownAt, .bugcheck_code = evidence.raw.bugcheckCode });
        // Include raw fields in the immutable review; later ring wrap, event-tail
        // truncation and history clear cannot change a past incident's evidence.
        const retained = try std.json.parseFromSlice(Evidence, self.a, json, .{ .allocate = .alloc_always });
        try self.event_rows.append(self.a, retained.value);
        if (retained.value.raw.dumpPath) |dump| self.dump = dump;
    }
};
const Scan = struct {
    owner: *Owner,
    a: std.mem.Allocator,
    mode: enum { expire, clear_history, clear_review, list, list_review },
    now: i64 = 0,
    count: usize = 0,
    changed: bool = false,
    output: ?Output = null,
    fn visit(context: ?*anyopaque, path: []const u8) !void {
        const self: *Scan = @ptrCast(@alignCast(context.?));
        // A fresh arena per row bounds scans independently of total history.
        var arena = std.heap.ArenaAllocator.init(self.a);
        defer arena.deinit();
        const a = arena.allocator();
        const json = (try files.read(a, path, p.freeze_recorder_max_document_bytes)) orelse return;
        if (self.mode == .list or self.mode == .list_review) {
            if (self.count != 0) try self.output.?.bytes(",");
            if (self.mode == .list_review) {
                const parsed = try std.json.parseFromSlice(ReviewDocument, a, json, .{});
                const row = parsed.value;
                try self.output.?.value(a, .{ .id = row.id, .category = row.category, .state = row.state, .detailsRevision = row.detailsRevision, .lastRecordAt = row.review.last_record_timestamp_ms, .shutdownAt = row.review.shutdown_timestamp_ms, .gapMs = row.review.gap_ms, .tags = row.tags, .inputsComplete = row.review.inputs_complete, .bugcheckCode = row.review.bugcheck_code });
            } else try self.output.?.bytes(json);
            self.count += 1;
            return;
        }
        if (self.mode == .clear_review) {
            try files.remove(a, path);
            self.count += 1;
            return;
        }
        const parsed = try std.json.parseFromSlice(entries.Entry, a, json, .{});
        var item = parsed.value;
        if (self.mode == .clear_history) {
            if (item.state == .history) {
                try files.remove(a, path);
                self.count += 1;
            }
            return;
        }
        if (entries.expire(&item, self.now, self.owner.settings.systemEvents.history_after_ms)) {
            try self.owner.save(a, path, item);
            self.changed = true;
        }
        if (item.state == .active) self.owner.addDeadline(item.lastObservedAt +| self.owner.settings.systemEvents.history_after_ms);
    }
};
const SampleInput = struct {
    timestamp_ms: i64,
    commit_bytes: ?u64 = null,
    commit_limit_bytes: ?u64 = null,
    physical_total_bytes: ?u64 = null,
    physical_available_bytes: ?u64 = null,
    paged_pool_bytes: ?u64 = null,
    nonpaged_pool_bytes: ?u64 = null,
    interrupt_cpu_percent: ?f32 = null,
    longest_interrupt_us: ?u64 = null,
    interrupt_timestamp_ms: ?i64 = null,
    interrupt_window_ms: ?u32 = null,
    interrupt_unavailable_reason: ?t.GapReason = null,
    kernel_leak_active: ?bool = null,
    kernel_leak_unavailable_reason: ?t.GapReason = null,
    commit_immediate_percent: ?f64 = null,
    disks: []const t.DiskSample = &.{},
    disks_complete: bool = false,
    processes_timestamp_ms: ?i64 = null,
    processes_complete: bool = false,
    processes: []const struct { pid: u32, image_name: []const u8, private_commit_bytes: u64 } = &.{},
};
test {
    _ = @import("owner_tests.zig");
}
