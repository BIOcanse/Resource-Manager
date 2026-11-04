const std = @import("std");
const s = @import("source.zig");
const m = s.model;
const win = @import("windows.zig");
const xml = s.xml;
const a = std.testing.allocator;
fn parseFixture(comptime path: []const u8, arena: *std.heap.ArenaAllocator) !m.Event {
    return (try xml.parse(arena.allocator(), &s.rules.defaults, @embedFile(path), .catchup)) orelse error.ExpectedEvent;
}
test "real System XML covers disk target, exhaustion UserData, GPU and bugcheck" {
    var arena = std.heap.ArenaAllocator.init(a);
    defer arena.deinit();
    const disk = try parseFixture("fixtures/disk153.xml", &arena);
    try std.testing.expectEqual(m.Category.storage_retry_reset, disk.category);
    try std.testing.expectEqualStrings("\\Device\\Harddisk1\\DR1", disk.target);
    try std.testing.expectEqual(@as(u64, 225302), disk.record_id);
    const exhausted = try parseFixture("fixtures/exhaustion2004.xml", &arena);
    try std.testing.expectEqual(m.Category.memory_exhausted, exhausted.category);
    try std.testing.expect(std.mem.find(u8, exhausted.raw_xml, "ProcessInfo") != null);
    const gpu = try parseFixture("fixtures/nvidia153.xml", &arena);
    try std.testing.expectEqual(m.Category.gpu_reset, gpu.category);
    const bugcheck = try parseFixture("fixtures/bugcheck1001.xml", &arena);
    try std.testing.expectEqual(@as(?u32, 0xef), bugcheck.bugcheck_code);
    try std.testing.expectEqualStrings("C:\\WINDOWS\\Minidumps\\100726-13781-01.dmp", bugcheck.dump_path.?);
}
test "F3 storage defaults accept observed and explicitly synthetic missing-history events" {
    var arena = std.heap.ArenaAllocator.init(a);
    defer arena.deinit();
    const Case = struct { path: []const u8, kind: m.TargetKind, target: []const u8 };
    inline for ([_]Case{
        .{ .path = "fixtures/synthetic-disk7.xml", .kind = .physical_disk, .target = "\\Device\\Harddisk9\\DR9" },
        .{ .path = "fixtures/disk11.xml", .kind = .physical_disk, .target = "\\Device\\Harddisk2\\DR2" },
        .{ .path = "fixtures/synthetic-disk51.xml", .kind = .physical_disk, .target = "\\Device\\Harddisk9\\DR9" },
        .{ .path = "fixtures/disk153.xml", .kind = .physical_disk, .target = "\\Device\\Harddisk1\\DR1" },
        .{ .path = "fixtures/synthetic-storport129.xml", .kind = .storage_port, .target = "\\Device\\RaidPort9" },
        .{ .path = "fixtures/synthetic-stornvme129.xml", .kind = .storage_port, .target = "\\Device\\RaidPort9" },
        .{ .path = "fixtures/uasp129.xml", .kind = .storage_port, .target = "\\Device\\RaidPort1" },
        .{ .path = "fixtures/ntfs50.xml", .kind = .volume, .target = "unknown" },
        .{ .path = "fixtures/synthetic-ntfs140.xml", .kind = .volume, .target = "unknown" },
        .{ .path = "fixtures/synthetic-ntfs-manifest140.xml", .kind = .volume, .target = "\\\\?\\Volume{00000000-0000-0000-0000-000000000009}\\" },
    }) |case| {
        const event = try parseFixture(case.path, &arena);
        try std.testing.expectEqual(m.Category.storage_retry_reset, event.category);
        try std.testing.expectEqual(case.kind, event.target_kind);
        try std.testing.expectEqualStrings(case.target, event.target);
    }
}
test "F3 GPU desktop hang and all-ID hardware defaults accept synthetic parser cases" {
    var arena = std.heap.ArenaAllocator.init(a);
    defer arena.deinit();
    const display = try parseFixture("fixtures/synthetic-display4101.xml", &arena);
    try std.testing.expectEqual(m.Category.gpu_reset, display.category);
    try std.testing.expectEqualStrings("synthetic-display-driver", display.target);
    const nvidia = try parseFixture("fixtures/synthetic-nvidia13.xml", &arena);
    try std.testing.expectEqual(m.Category.gpu_reset, nvidia.category);
    try std.testing.expectEqualStrings("\\Device\\Video9", nvidia.target);
    const hang = try parseFixture("fixtures/synthetic-desktop-hang1002.xml", &arena);
    try std.testing.expectEqual(m.Category.desktop_crash, hang.category);
    try std.testing.expectEqualStrings("dwm.exe", hang.target);
    const ordinary = try std.mem.replaceOwned(u8, a, hang.raw_xml, "dwm.exe", "ordinary.exe");
    defer a.free(ordinary);
    try std.testing.expectEqual(@as(?m.Event, null), try xml.parse(arena.allocator(), &s.rules.defaults, ordinary, .live));
    const whea = try parseFixture("fixtures/synthetic-whea17.xml", &arena);
    try std.testing.expectEqual(m.Category.hardware_error, whea.category);
    // Hardware errors are not restricted to the single synthetic fixture's ID.
    try std.testing.expect(s.rules.match(&s.rules.defaults, .system, "Microsoft-Windows-WHEA-Logger", 999999) != null);
    try std.testing.expect(s.rules.match(&s.rules.defaults, .application, "disk", 7) == null);
    try std.testing.expect(s.rules.match(&s.rules.defaults, .system, "disk", 8) == null);
    try std.testing.expect(s.rules.match(&s.rules.defaults, .system, "Microsoft-Windows-Ntfs", 141) == null);
}
test "6008 uses UTC binary shutdown time before marker emitted at boot" {
    var arena = std.heap.ArenaAllocator.init(a);
    defer arena.deinit();
    const e = try parseFixture("fixtures/shutdown6008.xml", &arena);
    try std.testing.expect(e.actual_shutdown_ms.? < e.timestamp_ms);
    // Original binary SYSTEMTIME UTC = 2026-10-11T01:56:42.529Z.
    try std.testing.expectEqual(try xml.timestampMs("2026-10-11T01:56:42.529Z"), e.actual_shutdown_ms.?);
}
test "desktop rule accepts Explorer case-insensitively and excludes other apps and localized Popup26" {
    var arena = std.heap.ArenaAllocator.init(a);
    defer arena.deinit();
    const e = try parseFixture("fixtures/desktop1000.xml", &arena);
    try std.testing.expectEqualStrings("explorer.exe", e.target);
    const raw = @embedFile("fixtures/desktop1000.xml");
    const replaced = try std.mem.replaceOwned(u8, a, raw, "Explorer.EXE", "ordinary.exe");
    defer a.free(replaced);
    try std.testing.expectEqual(@as(?m.Event, null), try xml.parse(arena.allocator(), &s.rules.defaults, replaced, .live));
    try std.testing.expectEqual(@as(?m.Event, null), try xml.parse(arena.allocator(), &s.rules.defaults, @embedFile("fixtures/startup26.xml"), .catchup));
}
test "XPath compiles selected channels and rejects injected publisher expression" {
    const query = try s.rules.xpath(a, &s.rules.defaults, .application);
    defer a.free(query);
    const text = try std.unicode.utf16LeToUtf8Alloc(a, query);
    defer a.free(text);
    try std.testing.expect(std.mem.find(u8, text, "Application Error") != null);
    try std.testing.expect(std.mem.find(u8, text, "Kernel-Power") == null);
    var malicious = s.rules.defaults[0];
    malicious.provider = "x'] or *[";
    try std.testing.expectError(error.InvalidProvider, s.rules.xpath(a, &.{malicious}, .system));
}
const Fake = struct {
    next_handle: usize = 10,
    emitted: bool = false,
    delivered: usize = 0,
    saved_subscribe: bool = false,
    seen_saved: bool = false,
    reject: bool = false,
    handles: usize = 0,
    require_scope: bool = false,
    in_scope: bool = false,
    scope_runs: usize = 0,
    scope_start: usize = 0,
    scope_length: usize = 0,
    fn self(ctx: ?*anyopaque) *Fake {
        return @ptrCast(@alignCast(ctx.?));
    }
    fn handle(f: *Fake) win.Handle {
        f.next_handle += 1;
        f.handles += 1;
        return @ptrFromInt(f.next_handle);
    }
    fn signal(ctx: ?*anyopaque) !win.Handle {
        return self(ctx).handle();
    }
    fn set(_: ?*anyopaque, _: win.Handle) void {}
    fn close(ctx: ?*anyopaque, _: win.Handle) void {
        self(ctx).handles -= 1;
    }
    fn bookmark(ctx: ?*anyopaque, mark: ?[*:0]const u16) !win.Handle {
        const f = self(ctx);
        f.seen_saved = mark != null;
        return f.handle();
    }
    fn subscribe(ctx: ?*anyopaque, _: win.Handle, _: [*:0]const u16, _: [*:0]const u16, _: win.Handle, saved: bool) !win.Handle {
        const f = self(ctx);
        f.saved_subscribe = saved;
        return f.handle();
    }
    fn next(ctx: ?*anyopaque, _: win.Handle) !?win.Handle {
        const f = self(ctx);
        if (f.emitted) return null;
        f.emitted = true;
        return f.handle();
    }
    fn update(_: ?*anyopaque, _: win.Handle, _: win.Handle) !void {}
    fn render(_: ?*anyopaque, allocator: std.mem.Allocator, _: win.Handle, kind: win.Render, _: usize) ![]u8 {
        return allocator.dupe(u8, if (kind == .event) @embedFile("fixtures/disk153.xml") else "<BookmarkList><Bookmark Channel='System' RecordId='225302' IsCurrent='true'/></BookmarkList>");
    }
    fn wait(_: ?*anyopaque, _: []const win.Handle) !usize {
        return 0;
    }
    fn commit(ctx: *anyopaque, allocator: std.mem.Allocator, channel: m.Channel, event: ?m.Event, mark: []const u8) !void {
        const f: *Fake = @ptrCast(@alignCast(ctx));
        if (f.require_scope) {
            try std.testing.expect(f.in_scope);
            const borrowed = try allocator.alloc(u8, 8);
            const first = @intFromPtr(borrowed.ptr);
            try std.testing.expect(first >= f.scope_start and first + borrowed.len <= f.scope_start + f.scope_length);
            const raw_first = @intFromPtr(event.?.raw_xml.ptr);
            try std.testing.expect(raw_first >= f.scope_start and raw_first + event.?.raw_xml.len <= f.scope_start + f.scope_length);
        }
        try std.testing.expectEqual(m.Channel.system, channel);
        try std.testing.expectEqual(m.Delivery.catchup, event.?.delivery);
        try std.testing.expect(std.mem.find(u8, mark, "225302") != null);
        if (f.reject) return error.PersistenceFailed;
        f.delivered += 1;
    }
    fn api(f: *Fake) s.Api {
        return .{ .context = f, .create_signal = signal, .set_signal = set, .close_signal = close, .bookmark = bookmark, .subscribe = subscribe, .next = next, .update = update, .render = render, .close = close, .wait = wait };
    }
};
const limits: s.Limits = .{ .event_xml_bytes = 65536, .bookmark_xml_bytes = 16384, .xpath_units = 16384, .thread_stack_bytes = 262144 };
test "injected API restores bookmark and synchronously commits catch-up before start returns" {
    var f: Fake = .{};
    var source = try s.Source.init(a, s.rules.defaults[0..1], .{ .context = &f, .commit = Fake.commit }, limits, f.api());
    try source.start(.{ .system = "<BookmarkList/>" });
    try std.testing.expect(f.seen_saved and f.saved_subscribe);
    try std.testing.expectEqual(@as(usize, 1), f.delivered);
    source.stop();
    try std.testing.expectEqual(@as(usize, 0), f.handles);
}
test "injected persistence failure stops before live worker and releases every handle" {
    var f: Fake = .{ .reject = true };
    var source = try s.Source.init(a, s.rules.defaults[0..1], .{ .context = &f, .commit = Fake.commit }, limits, f.api());
    try std.testing.expectError(error.PersistenceFailed, source.start(.{}));
    try std.testing.expectEqual(@as(usize, 0), f.delivered);
    try std.testing.expectEqual(@as(usize, 0), f.handles);
    try std.testing.expect(source.thread == null);
}

const BorrowedScope = struct {
    fake: *Fake,
    bytes: [32768]u8 = undefined,
    fn run(context: ?*anyopaque, work: s.Work, operation: ?*anyopaque) !void {
        const scope: *BorrowedScope = @ptrCast(@alignCast(context.?));
        const f = scope.fake;
        try std.testing.expect(!f.in_scope);
        f.scope_runs += 1;
        f.in_scope = true;
        f.scope_start = @intFromPtr(&scope.bytes);
        f.scope_length = scope.bytes.len;
        defer f.in_scope = false;
        var fixed = std.heap.FixedBufferAllocator.init(&scope.bytes);
        try work(operation, fixed.allocator());
    }
};
test "one borrowed scope covers XML and commit and releases on persistence error" {
    var f: Fake = .{ .require_scope = true };
    var lease: BorrowedScope = .{ .fake = &f };
    var source = try s.Source.init(a, s.rules.defaults[0..1], .{ .context = &f, .commit = Fake.commit }, limits, f.api());
    source.scope = .{ .context = &lease, .run = BorrowedScope.run };
    try source.start(.{});
    source.stop();
    try std.testing.expectEqual(@as(usize, 1), f.scope_runs);
    try std.testing.expectEqual(@as(usize, 1), f.delivered);
    try std.testing.expect(!f.in_scope);
    try std.testing.expectEqual(@as(usize, 0), f.handles);

    f = .{ .require_scope = true, .reject = true };
    try std.testing.expectError(error.PersistenceFailed, source.start(.{}));
    try std.testing.expectEqual(@as(usize, 1), f.scope_runs);
    try std.testing.expect(!f.in_scope);
    try std.testing.expectEqual(@as(usize, 0), f.handles);
}
