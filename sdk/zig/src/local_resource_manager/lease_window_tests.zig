const std = @import("std");
const window = @import("activity_window.zig");
const memory = @import("memory_resource.zig");

test "all widths lazily fill a long hold and stop at its final return" {
    inline for (.{ 8, 16, 32, 64 }) |width| {
        const R = memory.Configured(.{ .width = width, .score_epochs = width, .idle_epochs = width });
        var row = try R.fixed(0);
        defer row.forceRelease() catch unreachable;
        const loading = (try row.push(1, 1)).fresh;
        const before = row.activity_history;
        const last: u64 = width * 3 + 7;
        const projected = try row.historyAt(last);
        try std.testing.expectEqual(@as(u8, width), try projected.score(last, .{}));
        try std.testing.expectEqualDeep(before, row.activity_history);
        _ = try row.endUse(loading.lease, .complete, last);
        try std.testing.expectEqual(@as(u8, width), try row.activity_history.score(last, .{}));
        try std.testing.expectEqual(@as(u8, width - 1), try row.activity_history.score(last + 1, .{}));
        try std.testing.expect(try row.activity_history.idle(last + width, .{}));
    }
}

test "overlapping leases count the union of covered epochs and no extra same-round activity" {
    const R = memory.Configured(.{ .width = 16, .score_epochs = 16, .idle_epochs = 16 });
    var row = try R.fixed(0);
    defer row.forceRelease() catch unreachable;
    const load = (try row.push(1, 1)).fresh;
    _ = try row.endUse(load.lease, .complete, 1);
    const first = (try row.use(3)).in_memory;
    const once = row.activity_history;
    const second = (try row.use(3)).in_memory;
    try std.testing.expectEqualDeep(once, row.activity_history);
    const before_invalid_return = row;
    try std.testing.expectError(error.InvalidEpoch, row.endUse(first.lease, .complete, 2));
    try std.testing.expectEqualDeep(before_invalid_return, row);
    try std.testing.expectEqual(memory.CloseResult.in_use, try row.close());
    _ = try row.endUse(first.lease, .complete, 7);
    try std.testing.expectEqual(@as(u8, 8), try (try row.historyAt(9)).score(9, .{})); // 1 and 3..9
    _ = try row.endUse(second.lease, .complete, 10);
    try std.testing.expectEqual(@as(u8, 9), try row.activity_history.score(10, .{})); // 1 and 3..10
    try std.testing.expectEqual(@as(u64, 0), row.use_count);
    try std.testing.expectEqualDeep(row.activity_history, try row.historyAt(11));
}

test "continuous bitmap matches an independent epoch reference at every width" {
    inline for (.{ 8, 16, 32, 64 }) |width| {
        const W = window.Window(width);
        var history = W.History{};
        var reference = @as([161]bool, @splat(false));
        var epoch: u64 = 1;
        while (epoch <= 160) : (epoch += 1) {
            if (epoch == 1 or epoch % 17 == 0) {
                history = try history.withAccess(epoch);
                reference[@intCast(epoch)] = true;
            } else if (epoch % 17 >= 1 and epoch % 17 <= 10) {
                history = try history.withContinuousUse(epoch);
                // Only the last materialized interval starts at the previous epoch.
                reference[@intCast(epoch)] = true;
            }
            var k: u8 = 1;
            while (k <= width) : (k += 1) {
                var expected: u8 = 0;
                var e = if (epoch >= k) epoch - k + 1 else 1;
                while (e <= epoch) : (e += 1) if (reference[@intCast(e)]) {
                    expected += 1;
                };
                try std.testing.expectEqual(expected, try history.score(epoch, .{ .score_epochs = k, .idle_epochs = k }));
            }
        }
    }
}
