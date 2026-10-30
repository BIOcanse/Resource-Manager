//! Fixed logical epoch windows. Width is resolved by FreedomPoint before build;
//! no lease state, runtime type switch, eager epoch scan or exponential decay.
const std = @import("std");
const defaults = @import("ledger_values");

pub const Error = error{ InvalidArgument, InvalidEpoch };
pub const Configuration = struct { width: u8, score_epochs: u8, idle_epochs: u8 };

/// Compile a specific integer and default policy. No values are read from a
/// registry at runtime; per-table policies arrive already resolved.
pub fn Configured(comptime config: Configuration) type {
    if (config.width != 8 and config.width != 16 and config.width != 32 and config.width != 64)
        @compileError("activity width must be 8, 16, 32 or 64");
    if (config.score_epochs == 0 or config.score_epochs > config.width or config.idle_epochs > config.width)
        @compileError("activity K/N exceed configured width");
    return struct {
        const Self = @This();
        pub const width = config.width;
        pub const Bitmap = std.meta.Int(.unsigned, width);
        const Shift = std.math.Log2Int(Bitmap);
        pub const Policy = struct {
            score_epochs: u8 = config.score_epochs,
            idle_epochs: u8 = config.idle_epochs,
            pub fn valid(self: @This()) bool {
                return self.score_epochs >= 1 and self.score_epochs <= width and self.idle_epochs <= width;
            }
        };
        pub const History = struct {
            bits: Bitmap = 0,
            last_activity_epoch: u64 = 0,

            pub fn withAccess(self: @This(), current_epoch: u64) Error!@This() {
                // Once this round is active, repeated acquisitions do no bitmap work.
                if (current_epoch != 0 and self.last_activity_epoch == current_epoch and self.bits & 1 != 0)
                    return self;
                return .{ .bits = (try self.aligned(current_epoch)) | 1, .last_activity_epoch = current_epoch };
            }
            /// A live lease covers every round from the last materialized activity
            /// through now. Fill the bounded window in constant time, never per epoch.
            pub fn withContinuousUse(self: @This(), current_epoch: u64) Error!@This() {
                if (self.last_activity_epoch == 0) return self.withAccess(current_epoch);
                if (current_epoch == self.last_activity_epoch) return self.withAccess(current_epoch);
                const aligned_bits = try self.aligned(current_epoch);
                const elapsed = current_epoch - self.last_activity_epoch;
                const used: u8 = if (elapsed >= width - 1) width else @intCast(elapsed + 1);
                return .{ .bits = aligned_bits | mask(used), .last_activity_epoch = current_epoch };
            }
            pub fn score(self: @This(), current_epoch: u64, policy: Self.Policy) Error!u8 {
                if (!policy.valid()) return error.InvalidArgument;
                return @intCast(@popCount((try self.aligned(current_epoch)) & mask(policy.score_epochs)));
            }
            pub fn idle(self: @This(), current_epoch: u64, policy: Self.Policy) Error!bool {
                if (!policy.valid()) return error.InvalidArgument;
                const current = try self.aligned(current_epoch);
                return policy.idle_epochs != 0 and current & mask(policy.idle_epochs) == 0;
            }
            pub fn aligned(self: @This(), current_epoch: u64) Error!Bitmap {
                if (current_epoch == 0 or current_epoch < self.last_activity_epoch) return error.InvalidEpoch;
                const elapsed = current_epoch - self.last_activity_epoch;
                if (elapsed >= width) return 0;
                return self.bits << @as(Shift, @intCast(elapsed));
            }
        };

        /// Call only after the COMPLETE composite score compares equal.
        /// Compare the first differing round, then oldest activity epoch, then
        /// lowest allocation number.
        pub fn tieBefore(left: Self.History, right: Self.History, current_epoch: u64, left_allocation_number: u64, right_allocation_number: u64) Error!bool {
            const l = try left.aligned(current_epoch);
            const r = try right.aligned(current_epoch);
            const difference = l ^ r;
            if (difference != 0) {
                const first = @as(Bitmap, 1) << @as(Shift, @intCast(@ctz(difference)));
                return l & first == 0;
            }
            if (left.last_activity_epoch != right.last_activity_epoch)
                return left.last_activity_epoch < right.last_activity_epoch;
            return left_allocation_number < right_allocation_number;
        }
        fn mask(epochs: u8) Bitmap {
            return if (epochs == width) std.math.maxInt(Bitmap) else (@as(Bitmap, 1) << @as(Shift, @intCast(epochs))) - 1;
        }
    };
}

/// Explicit specialization for adopters and width-comparison evidence.
pub fn Window(comptime width: u8) type {
    return Configured(.{ .width = width, .score_epochs = width, .idle_epochs = width });
}
pub const Active = Configured(.{
    .width = @intCast(defaults.Bound.activity_bitmap_width),
    .score_epochs = @intCast(defaults.Bound.table_0_activity_score_epochs),
    .idle_epochs = @intCast(defaults.Bound.table_0_idle_epochs),
});
pub const Policy = Active.Policy;
pub const History = Active.History;
pub const tieBefore = Active.tieBefore;

test "all configured integer widths preserve reference counts and expire the entire window" {
    inline for (.{ 8, 16, 32, 64 }) |width| {
        const W = Window(width);
        try std.testing.expectEqual(@as(usize, width / 8), @sizeOf(W.Bitmap));
        var seen = [_]bool{false} ** 130;
        var history = W.History{};
        var epoch: u64 = 1;
        while (epoch <= 128) : (epoch += 1) {
            if (epoch % 3 == 0 or (epoch >= 60 and epoch <= 68)) {
                seen[@intCast(epoch)] = true;
                history = try history.withAccess(epoch);
                history = try history.withAccess(epoch);
            }
            var k: u8 = 1;
            while (k <= width) : (k += 1) {
                var count: u8 = 0;
                var candidate = if (epoch >= k) epoch - k + 1 else 1;
                while (candidate <= epoch) : (candidate += 1) {
                    if (seen[@intCast(candidate)]) count += 1;
                }
                const policy = W.Policy{ .score_epochs = k, .idle_epochs = k };
                try std.testing.expectEqual(count, try history.score(epoch, policy));
                try std.testing.expectEqual(count == 0, try history.idle(epoch, policy));
            }
        }
        history = try (W.History{}).withAccess(10);
        try std.testing.expectEqual(@as(u8, 1), try history.score(10 + width - 1, .{}));
        try std.testing.expectEqual(@as(u8, 0), try history.score(10 + width, .{}));
        try std.testing.expectEqual(@as(W.Bitmap, 1), (try history.withAccess(10 + width)).bits);
        try std.testing.expectError(error.InvalidArgument, history.score(10, .{ .score_epochs = width + 1 }));
    }
}

test "equal scores compare aligned bits then last activity epoch then allocation number" {
    inline for (.{ 8, 16, 32, 64 }) |width| {
        const W = Window(width);
        const older = try (try (W.History{}).withAccess(16)).withAccess(19);
        const newer = try (try (W.History{}).withAccess(17)).withAccess(19);
        try std.testing.expectEqual(try older.score(20, .{}), try newer.score(20, .{}));
        try std.testing.expectEqual(older.last_activity_epoch, newer.last_activity_epoch);
        try std.testing.expect(try W.tieBefore(older, newer, 20, 9, 1));
        try std.testing.expect(!try W.tieBefore(newer, older, 20, 1, 9));
        const equivalent = W.History{ .bits = try older.aligned(20), .last_activity_epoch = 20 };
        // Identical aligned bitmaps use the absolute last-activity epoch
        // before allocation number, even once both bitmaps have expired.
        try std.testing.expect(try W.tieBefore(older, equivalent, 20, 100, 1));
        try std.testing.expect(!try W.tieBefore(equivalent, older, 20, 1, 100));
        const same_history = W.History{ .bits = older.bits, .last_activity_epoch = older.last_activity_epoch };
        try std.testing.expect(try W.tieBefore(older, same_history, 20, 1, 9));
        try std.testing.expect(!try W.tieBefore(older, same_history, 20, 9, 1));
        try std.testing.expect(try W.tieBefore(older, newer, 20 + width, 1, 9));
        try std.testing.expect(!try W.tieBefore(older, newer, 20 + width, 9, 1));
        try std.testing.expectError(error.InvalidEpoch, W.tieBefore(older, newer, 18, 1, 9));
    }
}

test "fixed window defaults and exact inclusive boundaries have no truncation" {
    const TestWindow = Window(64);

    try std.testing.expect((TestWindow.Policy{}).valid());
    try std.testing.expectEqual(@as(u8, 64), (TestWindow.Policy{}).score_epochs);
    try std.testing.expectEqual(@as(u8, 64), (TestWindow.Policy{}).idle_epochs);
    var history = try (TestWindow.History{}).withAccess(1);
    try std.testing.expectEqual(@as(u8, 1), try history.score(64, .{}));
    try std.testing.expect(!try history.idle(64, .{}));
    try std.testing.expectEqual(@as(u8, 0), try history.score(65, .{}));
    try std.testing.expect(try history.idle(65, .{}));
    const same_epoch = try history.withAccess(1);
    try std.testing.expectEqualDeep(history, same_epoch);
    history = try history.withAccess(65);
    try std.testing.expectEqual(@as(u64, 1), history.bits);
    try std.testing.expectEqual(@as(u8, 1), try history.score(65, .{ .score_epochs = 1, .idle_epochs = 1 }));
    try std.testing.expectEqual(@as(u8, 0), try history.score(66, .{ .score_epochs = 1, .idle_epochs = 1 }));
    try std.testing.expect(try history.idle(66, .{ .score_epochs = 1, .idle_epochs = 1 }));
    try std.testing.expect(!try history.idle(1000, .{ .idle_epochs = 0 }));
}

test "all window widths match independent access-round reference over sparse and burst history" {
    const TestWindow = Window(64);

    var seen = [_]bool{false} ** 129;
    var history = TestWindow.History{};
    var epoch: u64 = 1;
    while (epoch <= 128) : (epoch += 1) {
        if (epoch % 3 == 0 or (epoch >= 60 and epoch <= 68)) {
            seen[@intCast(epoch)] = true;
            history = try history.withAccess(epoch);
            // Repeated accesses in a round mark that round once.
            history = try history.withAccess(epoch);
        }
        const before = history;
        var width: u8 = 1;
        while (width <= 64) : (width += 1) {
            var count: u8 = 0;
            var candidate = if (epoch >= width) epoch - width + 1 else 1;
            while (candidate <= epoch) : (candidate += 1) {
                if (seen[@intCast(candidate)]) count += 1;
            }
            const policy = TestWindow.Policy{ .score_epochs = width, .idle_epochs = width };
            try std.testing.expectEqual(count, try history.score(epoch, policy));
            try std.testing.expectEqual(count == 0, try history.idle(epoch, policy));
        }
        try std.testing.expectEqualDeep(before, history);
    }
}

test "window validates policy and epoch before writes and handles full epoch gaps" {
    const TestWindow = Window(64);

    const history = try (TestWindow.History{}).withAccess(20);
    try std.testing.expectError(error.InvalidEpoch, history.withAccess(19));
    try std.testing.expectError(error.InvalidEpoch, history.score(0, .{}));
    try std.testing.expectError(error.InvalidEpoch, history.idle(19, .{}));
    try std.testing.expectError(error.InvalidArgument, history.score(20, .{ .score_epochs = 0 }));
    try std.testing.expectError(error.InvalidArgument, history.score(20, .{ .score_epochs = 65 }));
    try std.testing.expectError(error.InvalidArgument, history.idle(20, .{ .idle_epochs = 65 }));
    try std.testing.expectEqual(@as(u8, 0), try history.score(std.math.maxInt(u64), .{}));
    const latest = try history.withAccess(std.math.maxInt(u64));
    try std.testing.expectEqual(@as(u64, 1), latest.bits);
    try std.testing.expectEqual(@as(u8, 1), try latest.score(std.math.maxInt(u64), .{}));
    try std.testing.expectEqual(@as(usize, 16), @sizeOf(TestWindow.History));
}
