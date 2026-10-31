//! Full-coverage test for the main window, ported from the C#
//! NativeWindowOcclusion: per horizontal band, the union of X intervals of the
//! occluders that span the band must reach from target.left to target.right.
//! All storage is caller-owned and bounded by `max_occluders`.
const std = @import("std");

pub const max_occluders = 2048;

pub const Rect = struct {
    left: i32,
    top: i32,
    right: i32,
    bottom: i32,
    pub fn empty(self: Rect) bool {
        return self.right <= self.left or self.bottom <= self.top;
    }
    fn intersect(a: Rect, b: Rect) Rect {
        return .{ .left = @max(a.left, b.left), .top = @max(a.top, b.top), .right = @min(a.right, b.right), .bottom = @min(a.bottom, b.bottom) };
    }
};

const Interval = struct { left: i32, right: i32 };
pub const Scratch = struct {
    clipped: [max_occluders]Rect = undefined,
    ys: [2 + 2 * max_occluders]i32 = undefined,
    intervals: [max_occluders]Interval = undefined,
};

fn intervalLess(_: void, a: Interval, b: Interval) bool {
    return a.left < b.left;
}

pub fn isFullyCovered(target: Rect, occluders: []const Rect, scratch: *Scratch) bool {
    std.debug.assert(occluders.len <= max_occluders);
    if (target.empty() or occluders.len == 0) return false;
    var clipped: usize = 0;
    var ys: usize = 2;
    scratch.ys[0] = target.top;
    scratch.ys[1] = target.bottom;
    for (occluders) |occluder| {
        const part = Rect.intersect(target, occluder);
        if (part.empty()) continue;
        scratch.clipped[clipped] = part;
        clipped += 1;
        scratch.ys[ys] = part.top;
        scratch.ys[ys + 1] = part.bottom;
        ys += 2;
    }
    if (clipped == 0) return false;
    std.mem.sort(i32, scratch.ys[0..ys], {}, std.sort.asc(i32));
    var band: usize = 0;
    while (band + 1 < ys) : (band += 1) {
        const top = scratch.ys[band];
        const bottom = scratch.ys[band + 1];
        if (top == bottom) continue;
        var count: usize = 0;
        for (scratch.clipped[0..clipped]) |part| {
            if (part.top <= top and part.bottom >= bottom) {
                scratch.intervals[count] = .{ .left = part.left, .right = part.right };
                count += 1;
            }
        }
        std.mem.sort(Interval, scratch.intervals[0..count], {}, intervalLess);
        var covered = target.left;
        for (scratch.intervals[0..count]) |interval| {
            if (interval.left > covered) return false;
            covered = @max(covered, interval.right);
            if (covered >= target.right) break;
        }
        if (covered < target.right) return false;
    }
    return true;
}

test "coverage needs every band closed across the full width" {
    var scratch: Scratch = .{};
    const target: Rect = .{ .left = 0, .top = 0, .right = 100, .bottom = 100 };
    try std.testing.expect(!isFullyCovered(target, &.{}, &scratch));
    try std.testing.expect(isFullyCovered(target, &.{.{ .left = -10, .top = -10, .right = 110, .bottom = 110 }}, &scratch));
    // Two halves side by side cover; a one-pixel gap does not.
    try std.testing.expect(isFullyCovered(target, &.{ .{ .left = 0, .top = 0, .right = 50, .bottom = 100 }, .{ .left = 50, .top = 0, .right = 100, .bottom = 100 } }, &scratch));
    try std.testing.expect(!isFullyCovered(target, &.{ .{ .left = 0, .top = 0, .right = 49, .bottom = 100 }, .{ .left = 50, .top = 0, .right = 100, .bottom = 100 } }, &scratch));
    // Stacked bands with overlap cover; a missing band does not.
    try std.testing.expect(isFullyCovered(target, &.{ .{ .left = 0, .top = 0, .right = 100, .bottom = 60 }, .{ .left = 0, .top = 40, .right = 100, .bottom = 100 } }, &scratch));
    try std.testing.expect(!isFullyCovered(target, &.{ .{ .left = 0, .top = 0, .right = 100, .bottom = 40 }, .{ .left = 0, .top = 60, .right = 100, .bottom = 100 } }, &scratch));
    try std.testing.expect(!isFullyCovered(.{ .left = 0, .top = 0, .right = 0, .bottom = 10 }, &.{.{ .left = 0, .top = 0, .right = 10, .bottom = 10 }}, &scratch));
}
