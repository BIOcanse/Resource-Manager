const std = @import("std");
const memory = @import("memory.zig");
const values = @import("ledger_values").Bound;

test {
    _ = @import("main.zig");
    _ = @import("hotkeys/force_terminate.zig");
    _ = @import("hotkeys/overlay_toggle.zig");
}

test "Ledger workspace reuses content, protects live scopes, reclaims idle pages and re-pushes" {
    if (values.table_0_idle_epochs == 0) return error.SkipZigTest;
    var owner: memory.Owner = .{};
    try owner.init();
    defer owner.deinit() catch @panic("test owner has a live lease");
    var first = try owner.acquire(.tray_call, 8192);
    defer _ = first.endUse();
    try std.testing.expect(first.newly_allocated);
    first.bytes()[0] = 0xa5;
    const allocation = first.borrow.lease.allocation_number;
    first.publish();
    try std.testing.expectEqual(memory.Status.completed, first.endUse());
    {
        var reused = try owner.acquire(.tray_call, 8192);
        defer _ = reused.endUse();
        try std.testing.expect(!reused.newly_allocated);
        try std.testing.expectEqual(allocation, reused.borrow.lease.allocation_number);
        var tick: usize = 0;
        while (tick < values.table_0_idle_epochs + 2) : (tick += 1) _ = try owner.tick();
        try std.testing.expectEqual(@as(u8, 0xa5), reused.bytes()[0]);
    }
    var tick: usize = 0;
    while (tick < values.table_0_idle_epochs + 2) : (tick += 1) _ = try owner.tick();
    try std.testing.expect((try owner.use(.tray_call)) == null);
    var rebuilt = try owner.acquire(.tray_call, 8192);
    defer _ = rebuilt.endUse();
    try std.testing.expect(rebuilt.newly_allocated);
    try std.testing.expect(rebuilt.borrow.lease.allocation_number != allocation);
    for (rebuilt.bytes()) |byte| try std.testing.expectEqual(@as(u8, 0), byte);
    rebuilt.publish();
}

test "Ledger loading stays private, incomplete loading discards and scoped end is idempotent" {
    var owner: memory.Owner = .{};
    try owner.init();
    defer owner.deinit() catch @panic("test owner has a live lease");
    var loading = try owner.acquire(.overlay_hotkey_call, 8192);
    defer _ = loading.endUse();
    loading.bytes()[0] = 0xaa;
    try std.testing.expectError(error.LedgerLoading, owner.use(.overlay_hotkey_call));
    try std.testing.expectError(error.LedgerLoading, owner.acquire(.overlay_hotkey_call, 8192));
    try std.testing.expectEqual(memory.Status.completed, loading.endUse());
    try std.testing.expectEqual(memory.Status.completed, loading.endUse());
    try std.testing.expect((try owner.use(.overlay_hotkey_call)) == null);
    var replacement = try owner.acquire(.overlay_hotkey_call, 8192);
    defer _ = replacement.endUse();
    try std.testing.expect(replacement.newly_allocated);
    try std.testing.expectEqual(@as(u8, 0), replacement.bytes()[0]);
    replacement.publish();
}
