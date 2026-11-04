const std = @import("std");
const owner_module = @import("owner.zig");
const Owner = owner_module.Owner;
const host_module = @import("../../telemetry/kernel_etw/host_allocator.zig");
const policy = @import("../../configuration/NativePolicy.g.zig");
const entries = @import("entries.zig");
const files = @import("store.zig");
const model = @import("../../platform/windows/event_log/model.zig");

extern "kernel32" fn GetEnvironmentVariableW([*:0]const u16, ?[*]u16, u32) callconv(.winapi) u32;
extern "kernel32" fn GetCurrentProcessId() callconv(.winapi) u32;
extern "kernel32" fn GetTickCount64() callconv(.winapi) u64;

var directory_sequence: std.atomic.Value(u32) = .init(0);

// The test runner owns registration and retention. No fallback to a machine/user
// directory is permitted, and no test removes generated trees independently.
// RuntimeRead: EventDriven("A test requests its registered fixture directory")
fn testPath(label: []const u8, create: bool) ![]const u8 {
    const a = std.testing.allocator;
    const name = std.unicode.utf8ToUtf16LeStringLiteral("RM_FREEZE_TEST_ROOT");
    const required = GetEnvironmentVariableW(name, null, 0);
    if (required == 0) return error.MissingRegisteredFreezeTestRoot;
    const wide = try a.alloc(u16, required);
    defer a.free(wide);
    const length = GetEnvironmentVariableW(name, wide.ptr, @intCast(wide.len));
    if (length == 0 or length >= wide.len) return error.InvalidRegisteredFreezeTestRoot;
    const root = try std.unicode.utf16LeToUtf8Alloc(a, wide[0..length]);
    defer a.free(root);
    if (!try files.exists(a, root)) return error.RegisteredFreezeTestRootMissing;
    const path = try std.fmt.allocPrint(a, "{s}\\owner-{s}-{d}-{d}-{d}", .{
        root, label, GetCurrentProcessId(), GetTickCount64(), directory_sequence.fetchAdd(1, .monotonic),
    });
    errdefer a.free(path);
    if (try files.exists(a, path)) return error.TestDirectoryCollision;
    if (create) try files.mkdir(a, path);
    return path;
}

fn changed(_: ?*anyopaque, _: u32) callconv(.c) void {}

const Capture = struct {
    bytes: std.ArrayList(u8) = .empty,
    fail: bool = false,
    fn write(context: ?*anyopaque, text: [*]const u8, length: usize) callconv(.c) i32 {
        const self: *Capture = @ptrCast(@alignCast(context.?));
        if (self.fail) return 1;
        self.bytes.appendSlice(std.testing.allocator, text[0..length]) catch return 1;
        return 0;
    }
    fn output(self: *Capture) owner_module.Output {
        return .{ .context = self, .write = write };
    }
    fn reset(self: *Capture) void {
        self.bytes.clearRetainingCapacity();
        self.fail = false;
    }
    fn deinit(self: *Capture) void {
        self.bytes.deinit(std.testing.allocator);
    }
};

fn writeSettings(owner: *Owner, settings: owner_module.Settings, capture: *Capture) !void {
    const json = try std.json.Stringify.valueAlloc(std.testing.allocator, settings, .{});
    defer std.testing.allocator.free(json);
    try owner.request(8, json, capture.output());
}

fn settingsFrom(owner: *Owner, capture: *Capture) !std.json.Parsed(owner_module.Settings) {
    capture.reset();
    try owner.request(7, "", capture.output());
    return std.json.parseFromSlice(owner_module.Settings, std.testing.allocator, capture.bytes.items, .{ .allocate = .alloc_always });
}

const ItemList = struct { items: []const entries.Entry };

fn itemsFrom(owner: *Owner, capture: *Capture) !std.json.Parsed(ItemList) {
    capture.reset();
    try owner.request(0, "", capture.output());
    return std.json.parseFromSlice(ItemList, std.testing.allocator, capture.bytes.items, .{
        .allocate = .alloc_always,
        .ignore_unknown_fields = true,
    });
}

fn itemFor(items: []const entries.Entry, target: []const u8) !*const entries.Entry {
    var index: usize = 0;
    while (index < items.len) : (index += 1) {
        if (std.mem.eql(u8, items[index].target, target)) return &items[index];
    }
    return error.ExpectedPersistedItemMissing;
}

fn event(target: []const u8, id: u64, at: i64, xml: []const u8) model.Event {
    return .{ .category = .storage_retry_reset, .target_kind = .physical_disk, .target = target, .channel = .system, .provider = "disk", .event_id = 153, .record_id = id, .timestamp_ms = at, .timestamp_utc = "", .raw_xml = xml };
}

test "native owner create and settings read do not create user files or acquire sources" {
    const path = try testPath("create-readonly", false);
    defer std.testing.allocator.free(path);
    var host: host_module.TestHost = .{};
    var capture: Capture = .{};
    defer capture.deinit();
    {
        const owner = try Owner.create(path, host.callbacks(), changed, null);
        defer owner.destroy();
        try std.testing.expect(owner.source == null and owner.ring == null);
        var parsed = try settingsFrom(owner, &capture);
        defer parsed.deinit();
        try std.testing.expectEqual(owner.settings.freezeRecorder.sample_interval_ms, parsed.value.freezeRecorder.sample_interval_ms);
        try std.testing.expect(!try files.exists(std.testing.allocator, path));
    }
    try std.testing.expect(host.empty());
    try std.testing.expect(!try files.exists(std.testing.allocator, path));
}

test "native defaults remain independent of saved rule and ring overrides and can restore both" {
    const path = try testPath("restore-defaults", false);
    defer std.testing.allocator.free(path);
    const expected_path = try files.join(std.testing.allocator, path, "FreezeRecorder\\ring.bin");
    defer std.testing.allocator.free(expected_path);
    const custom_path = try files.join(std.testing.allocator, path, "custom.bin");
    defer std.testing.allocator.free(custom_path);
    var host: host_module.TestHost = .{};
    var capture: Capture = .{};
    defer capture.deinit();
    {
        const owner = try Owner.create(path, host.callbacks(), changed, null);
        defer owner.destroy();
        try owner.request(9, "", capture.output());
        var initial = try std.json.parseFromSlice(owner_module.Settings, std.testing.allocator, capture.bytes.items, .{ .allocate = .alloc_always });
        defer initial.deinit();
        try std.testing.expectEqualStrings(expected_path, initial.value.ring_path.?);
        try std.testing.expectEqual(@import("../../platform/windows/event_log/rules.zig").defaults.len, initial.value.systemEvents.rules.len);
        try std.testing.expect(!try files.exists(std.testing.allocator, path));

        var custom = initial.value;
        custom.systemEvents.rules = &.{};
        custom.freezeRecorder.sample_interval_ms = initial.value.freezeRecorder.sample_interval_ms + 1;
        custom.ring_path = custom_path;
        capture.reset();
        try writeSettings(owner, custom, &capture);
        capture.reset();
        try owner.request(9, "", capture.output());
        var defaults = try std.json.parseFromSlice(owner_module.Settings, std.testing.allocator, capture.bytes.items, .{ .allocate = .alloc_always });
        defer defaults.deinit();
        try std.testing.expectEqualStrings(expected_path, defaults.value.ring_path.?);
        try std.testing.expectEqual(initial.value.freezeRecorder.sample_interval_ms, defaults.value.freezeRecorder.sample_interval_ms);
        try std.testing.expectEqual(initial.value.systemEvents.rules.len, defaults.value.systemEvents.rules.len);

        capture.reset();
        try writeSettings(owner, defaults.value, &capture);
        var restored = try settingsFrom(owner, &capture);
        defer restored.deinit();
        try std.testing.expectEqualStrings(expected_path, restored.value.ring_path.?);
        try std.testing.expectEqual(defaults.value.systemEvents.rules.len, restored.value.systemEvents.rules.len);
        try std.testing.expectEqual(defaults.value.freezeRecorder.sample_interval_ms, restored.value.freezeRecorder.sample_interval_ms);
    }
    try std.testing.expect(host.empty());
}

test "native recorder retains observed fields and reason metadata but rejects stale interrupt joins" {
    const path = try testPath("metric-provenance", true);
    defer std.testing.allocator.free(path);
    var host: host_module.TestHost = .{};
    var capture: Capture = .{};
    defer capture.deinit();
    {
        const owner = try Owner.create(path, host.callbacks(), changed, null);
        defer owner.destroy();
        owner.settings.systemEvents.rules = &.{};
        owner.settings.systemEvents.live_kernel_directories = &.{};
        owner.settings.freezeRecorder.slots = 3;
        try owner.startRecorder();
        const json =
            \\{"timestamp_ms":10000,"interrupt_cpu_percent":10,"longest_interrupt_us":20,"interrupt_timestamp_ms":9000,"interrupt_window_ms":2000,"kernel_leak_unavailable_reason":"system-item-not-supported","disks_complete":true,"disks":[{"physical_disk_id":1,"queue_depth":1,"completed_operations_unavailable_reason":"counter-warmup"}]}
        ;
        try owner.submit(json);
        const first = blk: {
            owner.lock();
            defer owner.unlock();
            break :blk (try owner.ring.?.latest()).?;
        };
        try std.testing.expectEqual(@as(?f32, 10), first.sample.interrupt_cpu_percent);
        try std.testing.expectEqual(@as(?u64, 20), first.sample.longest_interrupt_us);
        try std.testing.expectEqual(@as(?i64, 9000), first.sample.interrupt_timestamp_ms);
        try std.testing.expectEqualStrings("system-item-not-supported", @tagName(first.sample.kernel_leak_unavailable_reason.?));
        try std.testing.expectEqualStrings("counter-warmup", @tagName(first.sample.disks[0].completed_operations_unavailable_reason.?));

        // The same publication cannot remain a current observation forever.
        const stale =
            \\{"timestamp_ms":20000,"interrupt_cpu_percent":10,"longest_interrupt_us":20,"interrupt_timestamp_ms":9000,"interrupt_window_ms":2000,"kernel_leak_unavailable_reason":"system-item-not-supported"}
        ;
        try owner.submit(stale);
        const second = blk: {
            owner.lock();
            defer owner.unlock();
            break :blk (try owner.ring.?.latest()).?;
        };
        try std.testing.expectEqual(@as(?f32, null), second.sample.interrupt_cpu_percent);
        try std.testing.expectEqual(@as(?u64, null), second.sample.longest_interrupt_us);
        try std.testing.expectEqualStrings("stale-publication", @tagName(second.sample.interrupt_unavailable_reason.?));
        capture.reset();
        try owner.request(2, "", capture.output());
        var status = try std.json.parseFromSlice(std.json.Value, std.testing.allocator, capture.bytes.items, .{});
        defer status.deinit();
        const rows = status.value.object.get("gapDetails").?.array.items;
        try std.testing.expectEqualStrings("interrupt-cpu-share", rows[0].object.get("field").?.string);
        try std.testing.expectEqualStrings("stale-publication", rows[0].object.get("reason").?.string);
    }
    try std.testing.expect(host.empty());
}

test "native owner settings roundtrip invalid rollback and output failure retain committed ownership" {
    const path = try testPath("settings-transaction", true);
    defer std.testing.allocator.free(path);
    const ring_path = try files.join(std.testing.allocator, path, "ring-测试.bin");
    defer std.testing.allocator.free(ring_path);
    var host: host_module.TestHost = .{};
    var capture: Capture = .{};
    defer capture.deinit();
    var next: owner_module.Settings = .{};
    next.freezeRecorder.enabled = false;
    next.freezeRecorder.sample_interval_ms = 7_000;
    next.systemEvents.event_tail_capacity = 2;
    next.ring_path = ring_path;
    {
        const owner = try Owner.create(path, host.callbacks(), changed, null);
        defer owner.destroy();
        try writeSettings(owner, next, &capture);
        try std.testing.expectEqual(@as(f64, 0), owner.setting(1));
        try std.testing.expectEqual(@as(f64, 7_000), owner.setting(2));
        var invalid = next;
        invalid.freezeRecorder.sample_interval_ms = 0;
        capture.reset();
        try std.testing.expectError(error.InvalidConfiguration, writeSettings(owner, invalid, &capture));
        try std.testing.expectEqual(@as(f64, 7_000), owner.setting(2));
        try std.testing.expectEqualStrings(ring_path, owner.settings.ring_path.?);
        const saved_json = (try files.read(std.testing.allocator, owner.settings_path, policy.freeze_recorder_max_document_bytes)) orelse return error.SettingsFileMissing;
        defer std.testing.allocator.free(saved_json);
        var saved = try std.json.parseFromSlice(owner_module.Settings, std.testing.allocator, saved_json, .{});
        defer saved.deinit();
        try std.testing.expectEqual(@as(u32, 7_000), saved.value.freezeRecorder.sample_interval_ms);

        // The durable commit succeeds before the reply fails. Its parsed settings
        // arena must transfer exactly once; subsequent reads/destroy expose UAF or
        // double-free, and reopening proves that committed values survive.
        next.freezeRecorder.sample_interval_ms = 9_000;
        capture.fail = true;
        try std.testing.expectError(error.OutputFailed, writeSettings(owner, next, &capture));
        try std.testing.expectEqual(@as(f64, 9_000), owner.setting(2));
        var parsed = try settingsFrom(owner, &capture);
        defer parsed.deinit();
        try std.testing.expectEqualStrings(ring_path, parsed.value.ring_path.?);
        try std.testing.expectEqual(@as(u32, 9_000), parsed.value.freezeRecorder.sample_interval_ms);
    }
    try std.testing.expect(host.empty());
    {
        const restored = try Owner.create(path, host.callbacks(), changed, null);
        defer restored.destroy();
        var parsed = try settingsFrom(restored, &capture);
        defer parsed.deinit();
        try std.testing.expectEqualStrings(ring_path, parsed.value.ring_path.?);
        try std.testing.expectEqual(@as(u32, 9_000), parsed.value.freezeRecorder.sample_interval_ms);
        try std.testing.expectEqual(@as(usize, 2), parsed.value.systemEvents.event_tail_capacity);
        try std.testing.expect(!parsed.value.freezeRecorder.enabled);
    }
    try std.testing.expect(host.empty());
}

test "native owner persisted merges reactivate history bound evidence and clear only history across restart" {
    const path = try testPath("event-lifecycle", true);
    defer std.testing.allocator.free(path);
    var host: host_module.TestHost = .{};
    var capture: Capture = .{};
    defer capture.deinit();
    const now = owner_module.nowMs();
    {
        const owner = try Owner.create(path, host.callbacks(), changed, null);
        defer owner.destroy();
        var settings: owner_module.Settings = .{};
        settings.systemEvents.event_tail_capacity = 2;
        try writeSettings(owner, settings, &capture);
        try owner.ingestForTest(event("disk:expired", 1, 1_000, "<expired/>"));
        try owner.ingestForTest(event("disk:reactivated", 10, 1_000, "<first/>"));
        try owner.ingestForTest(event("disk:reactivated", 11, 2_000, "<older/>"));
        const newest = event("disk:reactivated", 12, now, "<newer/>");
        try owner.ingestForTest(newest);
        try owner.ingestForTest(newest); // catch-up replay is idempotent
        var parsed = try itemsFrom(owner, &capture);
        defer parsed.deinit();
        try std.testing.expectEqual(@as(usize, 2), parsed.value.items.len);
        const active = try itemFor(parsed.value.items, "disk:reactivated");
        try std.testing.expectEqual(entries.State.active, active.state);
        try std.testing.expectEqual(@as(u64, 3), active.count);
        try std.testing.expectEqual(@as(i64, 1_000), active.firstObservedAt);
        try std.testing.expectEqual(now, active.lastObservedAt);
        try std.testing.expectEqual(@as(usize, 2), active.events.len);
        try std.testing.expectEqualStrings("<older/>", active.events[0].xml);
        try std.testing.expectEqualStrings("<newer/>", active.events[1].xml);
        try std.testing.expectEqual(entries.State.history, (try itemFor(parsed.value.items, "disk:expired")).state);
    }
    try std.testing.expect(host.empty());
    {
        const restored = try Owner.create(path, host.callbacks(), changed, null);
        defer restored.destroy();
        var before = try itemsFrom(restored, &capture);
        defer before.deinit();
        try std.testing.expectEqual(@as(usize, 2), before.value.items.len);
        const before_active = try itemFor(before.value.items, "disk:reactivated");
        try std.testing.expectEqual(@as(u64, 3), before_active.count);
        try std.testing.expectEqualStrings("<older/>", before_active.events[0].xml);
        try std.testing.expectEqualStrings("<newer/>", before_active.events[1].xml);
        capture.reset();
        try restored.request(1, "", capture.output());
        var cleared = try std.json.parseFromSlice(struct { removedCount: usize }, std.testing.allocator, capture.bytes.items, .{});
        defer cleared.deinit();
        try std.testing.expectEqual(@as(usize, 1), cleared.value.removedCount);
        var after = try itemsFrom(restored, &capture);
        defer after.deinit();
        try std.testing.expectEqual(@as(usize, 1), after.value.items.len);
        const active = try itemFor(after.value.items, "disk:reactivated");
        try std.testing.expectEqual(entries.State.active, active.state);
        try std.testing.expectEqualStrings("<newer/>", active.events[1].xml);
        // Evidence identity includes the timestamp so record IDs reused after
        // an event-log clear do not overwrite an earlier incident's evidence.
        const journal = try files.join(std.testing.allocator, path, "FreezeRecorder\\events\\system-10-1000.json");
        defer std.testing.allocator.free(journal);
        try std.testing.expect(try files.exists(std.testing.allocator, journal));
    }
    try std.testing.expect(host.empty());
    {
        const restored = try Owner.create(path, host.callbacks(), changed, null);
        defer restored.destroy();
        var parsed = try itemsFrom(restored, &capture);
        defer parsed.deinit();
        try std.testing.expectEqual(@as(usize, 1), parsed.value.items.len);
        const active = try itemFor(parsed.value.items, "disk:reactivated");
        try std.testing.expectEqual(entries.State.active, active.state);
        try std.testing.expectEqual(@as(u64, 3), active.count);
    }
    try std.testing.expect(host.empty());
}

test "native owner recorder and report holders share event source until last holder releases" {
    const path = try testPath("independent-event-holders", true);
    defer std.testing.allocator.free(path);
    var host: host_module.TestHost = .{};
    var capture: Capture = .{};
    defer capture.deinit();
    {
        const owner = try Owner.create(path, host.callbacks(), changed, null);
        defer owner.destroy();
        // Empty rules acquire only the source's stop signal/waiting worker. No
        // EvtSubscribe or real LiveKernelReports directory is touched.
        owner.settings.systemEvents.rules = &.{};
        owner.settings.systemEvents.live_kernel_directories = &.{};
        owner.settings.systemEvents.enabled = false;
        owner.settings.freezeRecorder.slots = 3;
        try owner.startRecorder();
        try std.testing.expect(owner.ring != null and owner.source != null);
        try std.testing.expectEqual(@as(u32, 2), owner.event_holders);
        const source_signal = owner.source.?.stop_signal;
        try std.testing.expect(source_signal != null);

        // F4 still archives evidence while the independent F3 report producer is
        // off. This must not create an F3 entry merely to keep the source alive.
        const recorder_event = event("disk:recorder-only", 9_001, owner_module.nowMs(), "<recorder-only/>");
        try owner.ingestForTest(recorder_event);
        var recorder_only = try itemsFrom(owner, &capture);
        defer recorder_only.deinit();
        try std.testing.expectEqual(@as(usize, 0), recorder_only.value.items.len);
        const journal_name = try std.fmt.allocPrint(std.testing.allocator, "FreezeRecorder\\events\\system-9001-{d}.json", .{recorder_event.timestamp_ms});
        defer std.testing.allocator.free(journal_name);
        const journal = try files.join(std.testing.allocator, path, journal_name);
        defer std.testing.allocator.free(journal);
        try std.testing.expect(try files.exists(std.testing.allocator, journal));

        owner.lock();
        owner.settings.systemEvents.enabled = true;
        owner.unlock();
        try owner.startEvents();
        try std.testing.expectEqual(@as(u32, 3), owner.event_holders);
        try std.testing.expect(owner.ring != null and owner.source != null);
        try std.testing.expectEqual(source_signal, owner.source.?.stop_signal);
        owner.stopRecorder();
        try std.testing.expect(owner.ring == null and owner.source != null);
        try std.testing.expectEqual(@as(u32, 1), owner.event_holders);
        try std.testing.expectEqual(source_signal, owner.source.?.stop_signal);
        owner.stopEvents();
        try std.testing.expect(owner.source == null);
        try std.testing.expectEqual(@as(u32, 0), owner.event_holders);
    }
    try std.testing.expect(host.empty());
}
