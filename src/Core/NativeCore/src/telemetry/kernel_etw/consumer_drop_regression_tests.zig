const std = @import("std");
const counters = @import("admission_counters.zig");

// The old managed dispatcher could have captured a callback before a binding
// changed. Raw native intake instead consumes ordered retirement and never
// invents an old-generation record. Preserve that exact historical admission
// counterexample at the production Store boundary, which has no payload to read.
test "all five captured residency callbacks reject stale generation once before other facts" {
    var store = try counters.Store.init(std.testing.allocator, 1);
    defer store.deinit();
    const kinds = .{ counters.Kind.process_start, counters.Kind.process_end, counters.Kind.thread_start,
        counters.Kind.thread_end, counters.Kind.context_switch };
    var mask: u64 = 0;
    inline for (kinds) |kind| mask |= counters.bit(kind);
    var config = counters.Config{ .token = 1, .generation = 17, .kind_mask = mask, .output_mask = 1,
        .flags = counters.active | counters.current_binding, .window_start = 100,
        .incomplete_through = 100 };
    try store.configure(config);
    inline for (kinds) |kind| {
        // Stale wins even if CPU mapping and inactive-window checks would reject.
        try std.testing.expect(!try store.observeAdmitted(1, kind,
            .{ .generation = 18, .qpc = 1, .mapped_processor = false }));
        try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.query(1, kind, .none).?);
        try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.query(1, kind, .generation_mismatch).?);
        inline for (std.meta.tags(counters.Reason)) |reason| {
            if (reason != .none and reason != .generation_mismatch)
                try std.testing.expectEqual(counters.Pair{}, store.query(1, kind, reason).?);
        }
    }
    // Physical receipt is a distinct fact; no fake physical input was injected.
    inline for (std.meta.tags(counters.PhysicalKind)) |kind|
        try std.testing.expectEqual(counters.Pair{}, store.queryPhysical(kind));
    config.flags &= ~counters.active;
    try store.configure(config);
    inline for (kinds) |kind| {
        const after = try store.observe(1, kind, .{ .generation = 17 });
        try std.testing.expect(!after.had_handler);
        try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, store.query(1, kind, .none).?);
    }
    try store.retireToSnapshot(1);
    const frame = store.takeRetired(1).?;
    inline for (kinds) |kind| {
        try std.testing.expectEqual(counters.Pair{ .received = 1, .dropped = 1 }, frame.totals[@intFromEnum(kind)]);
        try std.testing.expectEqual(@as(u64, 1), frame.rejected[@intFromEnum(kind)][@intFromEnum(counters.Reason.generation_mismatch)]);
    }
    try std.testing.expect(store.query(1, .context_switch, .none) == null);
    try std.testing.expectEqual(@as(usize, 0), store.consumers.capacity());
    try std.testing.expect(store.retired == null);
}
