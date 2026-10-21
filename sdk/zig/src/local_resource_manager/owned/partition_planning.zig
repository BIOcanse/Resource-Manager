//! Bounded, allocation-free placement and replacement planning for one parent.
//! The caller supplies cells in slot order and one complete summary per direct
//! child. A child cell is a whole interval; empty and barrier cells are one slot.
const std = @import("std");

/// Specialization over the ledger's compile-time-sized aligned activity bitmap.
pub fn For(comptime Bitmap: type) type {
    return struct {
        pub const BitmapType = Bitmap;
        pub const no_child = std.math.maxInt(u32);
        pub const Kind = enum { empty, barrier, child };

        /// `start` is the absolute parent slot. Child descriptors are ordered
        /// by start, and their indexes in `children` appear in the same order.
        pub const Cell = struct {
            start: u32,
            kind: Kind,
            child_index: u32 = no_child,
        };

        /// One complete direct child's precomputed subtree summary. The score,
        /// bitmap, and latest access include the child's shell and descendants;
        /// allocation numbers are strictly descending (newest to oldest). `safe`
        /// reflects resident release protection only: offline Fatal metadata does
        /// not make a child unsafe.
        pub const Child = struct {
            start: u32,
            capacity: u32,
            safe: bool,
            score: u8,
            aligned_bits: Bitmap,
            latest_access: u64,
            allocation_numbers: []const u64,
        };

        /// The slices must not overlap each other or child number lists; each
        /// must hold the sum of all child allocation-number counts.
        /// The two candidate slices alternate for linear merges; best_numbers
        /// preserves the winning candidate while later intervals are scanned.
        pub const Scratch = struct {
            candidate_a: []u64,
            candidate_b: []u64,
            best_numbers: []u64,
        };

        /// `end` is exclusive and may extend past the requested capacity when
        /// the final complete child shell is wider than the remaining request.
        /// The caller releases child cells in [start, end), then places at start.
        pub const Plan = struct {
            start: u32,
            end: u32,
            empty_only: bool,
        };

        pub const Error = error{ InvalidArgument, InsufficientWorkspace };

        const Candidate = struct {
            start: u32,
            end: u32,
            score: u8,
            aligned_bits: Bitmap,
            latest_access: u64,
            allocation_numbers: []const u64,
        };

        const Validated = struct { total_numbers: usize };

        /// Select the lowest empty interval first. Otherwise, consider the
        /// shortest feasible prefix from each possible start; extending a
        /// feasible interval can only add a child summary, so its max score,
        /// OR bitmap, latest access, and number list cannot improve its rank.
        pub fn plan(
            parent_start: u32,
            parent_capacity: u32,
            cells: []const Cell,
            children: []const Child,
            requested: u32,
            scratch: Scratch,
        ) Error!?Plan {
            const validated = try validate(parent_start, parent_capacity, cells, children);
            if (scratch.candidate_a.len < validated.total_numbers or
                scratch.candidate_b.len < validated.total_numbers or
                scratch.best_numbers.len < validated.total_numbers)
                return error.InsufficientWorkspace;
            if (requested == 0) return error.InvalidArgument;
            if (requested > parent_capacity) return null;

            var empty_start: ?u32 = null;
            var empty_count: u32 = 0;
            for (cells) |cell| {
                if (cell.kind == .empty) {
                    if (empty_count == 0) empty_start = cell.start;
                    empty_count += 1;
                    if (empty_count >= requested) {
                        const start = empty_start.?;
                        return .{ .start = start, .end = start + requested, .empty_only = true };
                    }
                } else {
                    empty_start = null;
                    empty_count = 0;
                }
            }

            var best: ?Candidate = null;
            var best_number_count: usize = 0;
            var left: usize = 0;
            while (left < cells.len) : (left += 1) {
                if (isBarrier(cells[left], children)) continue;

                var current_end = cells[left].start;
                var score: u8 = 0;
                var aligned_bits: Bitmap = 0;
                var latest_access: u64 = 0;
                var number_count: usize = 0;
                var candidate_in_a = true;
                var right = left;

                while (right < cells.len) : (right += 1) {
                    const cell = cells[right];
                    if (isBarrier(cell, children)) break;

                    const width: u32 = if (cell.kind == .child)
                        children[cell.child_index].capacity
                    else
                        1;
                    current_end += width;

                    if (cell.kind == .child) {
                        const child = children[cell.child_index];
                        score = @max(score, child.score);
                        aligned_bits |= child.aligned_bits;
                        latest_access = @max(latest_access, child.latest_access);

                        const child_numbers = child.allocation_numbers;
                        if (number_count == 0) {
                            @memcpy(scratch.candidate_a[0..child_numbers.len], child_numbers);
                            number_count = child_numbers.len;
                            candidate_in_a = true;
                        } else {
                            const source = if (candidate_in_a)
                                scratch.candidate_a[0..number_count]
                            else
                                scratch.candidate_b[0..number_count];
                            const destination = if (candidate_in_a)
                                scratch.candidate_b
                            else
                                scratch.candidate_a;
                            number_count = try mergeDescending(
                                destination[0..validated.total_numbers],
                                source,
                                child_numbers,
                            );
                            candidate_in_a = !candidate_in_a;
                        }
                    }

                    if (current_end - cells[left].start >= requested) {
                        const candidate_numbers = if (candidate_in_a)
                            scratch.candidate_a[0..number_count]
                        else
                            scratch.candidate_b[0..number_count];
                        const candidate = Candidate{
                            .start = cells[left].start,
                            .end = current_end,
                            .score = score,
                            .aligned_bits = aligned_bits,
                            .latest_access = latest_access,
                            .allocation_numbers = candidate_numbers,
                        };
                        if (best == null or before(candidate, best.?)) {
                            @memcpy(scratch.best_numbers[0..number_count], candidate_numbers);
                            best_number_count = number_count;
                            best = .{
                                .start = candidate.start,
                                .end = candidate.end,
                                .score = candidate.score,
                                .aligned_bits = candidate.aligned_bits,
                                .latest_access = candidate.latest_access,
                                .allocation_numbers = scratch.best_numbers[0..best_number_count],
                            };
                        }
                        break;
                    }
                }
            }

            const selected = best orelse return null;
            return .{ .start = selected.start, .end = selected.end, .empty_only = false };
        }

        fn validate(
            parent_start: u32,
            parent_capacity: u32,
            cells: []const Cell,
            children: []const Child,
        ) Error!Validated {
            if (parent_start > std.math.maxInt(u32) - parent_capacity)
                return error.InvalidArgument;
            const parent_end = parent_start + parent_capacity;
            var cursor = parent_start;
            var expected_child: usize = 0;
            var total_numbers: usize = 0;

            for (cells) |cell| {
                if (cell.start != cursor or cursor >= parent_end)
                    return error.InvalidArgument;
                switch (cell.kind) {
                    .empty, .barrier => {
                        if (cell.child_index != no_child) return error.InvalidArgument;
                        cursor += 1;
                    },
                    .child => {
                        if (expected_child >= children.len or
                            @as(usize, @intCast(cell.child_index)) != expected_child)
                            return error.InvalidArgument;
                        const child = children[expected_child];
                        if (child.start != cursor or child.capacity == 0 or
                            child.capacity > parent_end - cursor or child.allocation_numbers.len == 0)
                            return error.InvalidArgument;
                        if (child.allocation_numbers.len > std.math.maxInt(usize) - total_numbers)
                            return error.InvalidArgument;
                        total_numbers += child.allocation_numbers.len;
                        var number_index: usize = 1;
                        while (number_index < child.allocation_numbers.len) : (number_index += 1) {
                            if (child.allocation_numbers[number_index - 1] <= child.allocation_numbers[number_index])
                                return error.InvalidArgument;
                        }
                        cursor += child.capacity;
                        expected_child += 1;
                    },
                }
            }
            if (cursor != parent_end or expected_child != children.len)
                return error.InvalidArgument;
            return .{ .total_numbers = total_numbers };
        }

        fn isBarrier(cell: Cell, children: []const Child) bool {
            return switch (cell.kind) {
                .empty => false,
                .barrier => true,
                .child => !children[cell.child_index].safe,
            };
        }

        fn mergeDescending(destination: []u64, left: []const u64, right: []const u64) Error!usize {
            var left_index: usize = 0;
            var right_index: usize = 0;
            var output_index: usize = 0;
            while (left_index < left.len and right_index < right.len) {
                if (left[left_index] == right[right_index]) return error.InvalidArgument;
                if (left[left_index] > right[right_index]) {
                    destination[output_index] = left[left_index];
                    left_index += 1;
                } else {
                    destination[output_index] = right[right_index];
                    right_index += 1;
                }
                output_index += 1;
            }
            while (left_index < left.len) : (left_index += 1) {
                destination[output_index] = left[left_index];
                output_index += 1;
            }
            while (right_index < right.len) : (right_index += 1) {
                destination[output_index] = right[right_index];
                output_index += 1;
            }
            return output_index;
        }

        fn bitmapBefore(left: Bitmap, right: Bitmap) bool {
            const difference = left ^ right;
            if (difference == 0) return false;
            const lowest_difference = difference & (0 -% difference);
            return left & lowest_difference == 0;
        }

        fn numberListBefore(left: []const u64, right: []const u64) bool {
            const common = @min(left.len, right.len);
            for (0..common) |index| {
                if (left[index] != right[index]) return left[index] < right[index];
            }
            return left.len < right.len;
        }

        fn before(left: Candidate, right: Candidate) bool {
            if (left.score != right.score) return left.score < right.score;
            if (left.aligned_bits != right.aligned_bits)
                return bitmapBefore(left.aligned_bits, right.aligned_bits);
            if (left.latest_access != right.latest_access)
                return left.latest_access < right.latest_access;
            if (numberListBefore(left.allocation_numbers, right.allocation_numbers)) return true;
            if (numberListBefore(right.allocation_numbers, left.allocation_numbers)) return false;
            if (left.start != right.start) return left.start < right.start;
            return left.end < right.end;
        }
    };
}

test "partition planning takes the lowest sufficient empty interval before any replacement" {
    const P = For(u8);
    const cells = [_]P.Cell{
        .{ .start = 20, .kind = .barrier },
        .{ .start = 21, .kind = .empty },
        .{ .start = 22, .kind = .empty },
        .{ .start = 23, .kind = .child, .child_index = 0 },
        .{ .start = 26, .kind = .empty },
        .{ .start = 27, .kind = .empty },
        .{ .start = 28, .kind = .empty },
    };
    const allocations = [_]u64{ 9, 4 };
    const children = [_]P.Child{.{
        .start = 23,
        .capacity = 3,
        .safe = true,
        .score = 0,
        .aligned_bits = 0,
        .latest_access = 0,
        .allocation_numbers = allocations[0..],
    }};
    var first = [_]u64{0} ** 2;
    var second = [_]u64{0} ** 2;
    var best = [_]u64{0} ** 2;
    const selected = (try P.plan(20, 9, cells[0..], children[0..], 2, .{
        .candidate_a = first[0..],
        .candidate_b = second[0..],
        .best_numbers = best[0..],
    })).?;
    try std.testing.expectEqual(@as(u32, 21), selected.start);
    try std.testing.expectEqual(@as(u32, 23), selected.end);
    try std.testing.expect(selected.empty_only);
}

test "replacement ranks complete child summaries and treats unsafe children and owner slots as barriers" {
    const P = For(u8);
    const first_numbers = [_]u64{ 80, 20 };
    const middle_numbers = [_]u64{ 100, 50 };
    const last_numbers = [_]u64{ 90, 60 };
    const cells = [_]P.Cell{
        .{ .start = 10, .kind = .child, .child_index = 0 },
        .{ .start = 12, .kind = .empty },
        .{ .start = 13, .kind = .child, .child_index = 1 },
        .{ .start = 16, .kind = .empty },
        .{ .start = 17, .kind = .child, .child_index = 2 },
    };
    const children = [_]P.Child{
        .{ .start = 10, .capacity = 2, .safe = true, .score = 5, .aligned_bits = 0, .latest_access = 3, .allocation_numbers = first_numbers[0..] },
        .{ .start = 13, .capacity = 3, .safe = true, .score = 1, .aligned_bits = 0, .latest_access = 7, .allocation_numbers = middle_numbers[0..] },
        .{ .start = 17, .capacity = 2, .safe = true, .score = 3, .aligned_bits = 0, .latest_access = 5, .allocation_numbers = last_numbers[0..] },
    };
    var first = [_]u64{0} ** 6;
    var second = [_]u64{0} ** 6;
    var best = [_]u64{0} ** 6;
    const selected = (try P.plan(10, 9, cells[0..], children[0..], 3, .{
        .candidate_a = first[0..],
        .candidate_b = second[0..],
        .best_numbers = best[0..],
    })).?;
    // The score-1 child beats both score-5/score-3 alternatives. Starting in
    // its preceding empty slot preserves the same release set and lowers start.
    try std.testing.expectEqual(@as(u32, 12), selected.start);
    try std.testing.expectEqual(@as(u32, 16), selected.end);
    try std.testing.expect(!selected.empty_only);

    const split_cells = [_]P.Cell{
        .{ .start = 0, .kind = .empty },
        .{ .start = 1, .kind = .child, .child_index = 0 },
        .{ .start = 3, .kind = .barrier },
        .{ .start = 4, .kind = .empty },
        .{ .start = 5, .kind = .child, .child_index = 1 },
    };
    const unsafe_numbers = [_]u64{ 40, 30 };
    const safe_numbers = [_]u64{ 20, 10 };
    const split_children = [_]P.Child{
        .{ .start = 1, .capacity = 2, .safe = false, .score = 0, .aligned_bits = 0, .latest_access = 0, .allocation_numbers = unsafe_numbers[0..] },
        .{ .start = 5, .capacity = 2, .safe = true, .score = 0, .aligned_bits = 0, .latest_access = 0, .allocation_numbers = safe_numbers[0..] },
    };
    const separated = (try P.plan(0, 7, split_cells[0..], split_children[0..], 2, .{
        .candidate_a = first[0..],
        .candidate_b = second[0..],
        .best_numbers = best[0..],
    })).?;
    try std.testing.expectEqual(@as(u32, 4), separated.start);
    try std.testing.expectEqual(@as(u32, 7), separated.end);

    var all_blocked = split_children;
    all_blocked[1].safe = false;
    const blocked = try P.plan(0, 7, split_cells[0..], all_blocked[0..], 2, .{
        .candidate_a = first[0..],
        .candidate_b = second[0..],
        .best_numbers = best[0..],
    });
    try std.testing.expect(blocked == null);
}

test "equal-score ranking compares oldest differing bit, latest access, and allocation lists" {
    const P = For(u8);
    var first = [_]u64{0} ** 4;
    var second = [_]u64{0} ** 4;
    var best = [_]u64{0} ** 4;

    const bit_a = [_]u64{ 20 };
    const bit_b = [_]u64{ 10 };
    const bit_cells = [_]P.Cell{
        .{ .start = 0, .kind = .child, .child_index = 0 },
        .{ .start = 1, .kind = .child, .child_index = 1 },
    };
    const bit_children = [_]P.Child{
        .{ .start = 0, .capacity = 1, .safe = true, .score = 2, .aligned_bits = 0b0000_0010, .latest_access = 10, .allocation_numbers = bit_a[0..] },
        .{ .start = 1, .capacity = 1, .safe = true, .score = 2, .aligned_bits = 0b0000_0100, .latest_access = 100, .allocation_numbers = bit_b[0..] },
    };
    const by_bit = (try P.plan(0, 2, bit_cells[0..], bit_children[0..], 1, .{
        .candidate_a = first[0..], .candidate_b = second[0..], .best_numbers = best[0..],
    })).?;
    try std.testing.expectEqual(@as(u32, 1), by_bit.start);

    const history_numbers_a = [_]u64{ 30 };
    const history_numbers_b = [_]u64{ 20 };
    var history_children = bit_children;
    history_children[0].aligned_bits = 0b0000_0100;
    history_children[1].aligned_bits = 0b0000_0100;
    history_children[0].latest_access = 8;
    history_children[1].latest_access = 9;
    history_children[0].allocation_numbers = history_numbers_a[0..];
    history_children[1].allocation_numbers = history_numbers_b[0..];
    const by_access = (try P.plan(0, 2, bit_cells[0..], history_children[0..], 1, .{
        .candidate_a = first[0..], .candidate_b = second[0..], .best_numbers = best[0..],
    })).?;
    try std.testing.expectEqual(@as(u32, 0), by_access.start);

    const number_a = [_]u64{ 100, 80 };
    const number_b = [_]u64{ 90, 70 };
    var number_children = bit_children;
    number_children[0].aligned_bits = 0;
    number_children[1].aligned_bits = 0;
    number_children[0].latest_access = 10;
    number_children[1].latest_access = 10;
    number_children[0].allocation_numbers = number_a[0..];
    number_children[1].allocation_numbers = number_b[0..];
    const by_number = (try P.plan(0, 2, bit_cells[0..], number_children[0..], 1, .{
        .candidate_a = first[0..], .candidate_b = second[0..], .best_numbers = best[0..],
    })).?;
    try std.testing.expectEqual(@as(u32, 1), by_number.start);

    try std.testing.expect(P.numberListBefore(&[_]u64{ 50, 20 }, &[_]u64{ 50, 20, 10 }));
    try std.testing.expect(!P.numberListBefore(&[_]u64{ 50, 20, 10 }, &[_]u64{ 50, 20 }));
}

/// Deliberately enumerates every feasible interval, including wider intervals,
/// and builds each number list by insertion. Tests use it as a bounded oracle
/// for the planner's minimal-prefix enumeration.
fn oraclePlan(
    comptime P: type,
    parent_start: u32,
    parent_capacity: u32,
    cells: []const P.Cell,
    children: []const P.Child,
    requested: u32,
) P.Error!?P.Plan {
    if (requested == 0) return error.InvalidArgument;
    var empty_start: ?u32 = null;
    var empty_count: u32 = 0;
    for (cells) |cell| {
        if (cell.kind == .empty) {
            if (empty_count == 0) empty_start = cell.start;
            empty_count += 1;
            if (empty_count >= requested) return .{ .start = empty_start.?, .end = empty_start.? + requested, .empty_only = true };
        } else {
            empty_start = null;
            empty_count = 0;
        }
    }

    var best: ?P.Plan = null;
    var best_score: u8 = 0;
    var best_bits: P.BitmapType = 0;
    var best_latest: u64 = 0;
    var best_numbers = [_]u64{0} ** 256;
    var best_number_count: usize = 0;
    var left: usize = 0;
    while (left < cells.len) : (left += 1) {
        if (oracleBarrier(P, cells[left], children)) continue;
        var width: u32 = 0;
        var score: u8 = 0;
        var bits: P.BitmapType = 0;
        var latest: u64 = 0;
        var numbers = [_]u64{0} ** 256;
        var number_count: usize = 0;
        var right = left;
        while (right < cells.len) : (right += 1) {
            const cell = cells[right];
            if (oracleBarrier(P, cell, children)) break;
            if (cell.kind == .child) {
                const child = children[cell.child_index];
                width += child.capacity;
                score = @max(score, child.score);
                bits |= child.aligned_bits;
                latest = @max(latest, child.latest_access);
                for (child.allocation_numbers) |number| {
                    if (number_count == numbers.len) return error.InsufficientWorkspace;
                    var insertion = number_count;
                    while (insertion > 0 and numbers[insertion - 1] < number) : (insertion -= 1) {
                        numbers[insertion] = numbers[insertion - 1];
                    }
                    if ((insertion > 0 and numbers[insertion - 1] == number) or
                        (insertion < number_count and numbers[insertion] == number)) return error.InvalidArgument;
                    numbers[insertion] = number;
                    number_count += 1;
                }
            } else {
                width += 1;
            }
            if (width < requested) continue;

            const candidate_numbers = numbers[0..number_count];
            const incumbent_numbers = best_numbers[0..best_number_count];
            const start = cells[left].start;
            const end = if (cell.kind == .child) cell.start + children[cell.child_index].capacity else cell.start + 1;
            var candidate_before = best == null;
            if (best) |incumbent| {
                if (score != best_score) {
                    candidate_before = score < best_score;
                } else if (bits != best_bits) {
                    candidate_before = P.bitmapBefore(bits, best_bits);
                } else if (latest != best_latest) {
                    candidate_before = latest < best_latest;
                } else if (P.numberListBefore(candidate_numbers, incumbent_numbers)) {
                    candidate_before = true;
                } else if (P.numberListBefore(incumbent_numbers, candidate_numbers)) {
                    candidate_before = false;
                } else if (start != incumbent.start) {
                    candidate_before = start < incumbent.start;
                } else {
                    candidate_before = end < incumbent.end;
                }
            }
            if (candidate_before) {
                @memcpy(best_numbers[0..number_count], candidate_numbers);
                best_number_count = number_count;
                best_score = score;
                best_bits = bits;
                best_latest = latest;
                best = .{ .start = start, .end = end, .empty_only = false };
            }
        }
    }

    _ = parent_start;
    if (requested > parent_capacity) return null;
    return best;
}

fn oracleBarrier(comptime P: type, cell: P.Cell, children: []const P.Child) bool {
    return switch (cell.kind) {
        .empty => false,
        .barrier => true,
        .child => !children[cell.child_index].safe,
    };
}

test "minimal-prefix enumeration agrees with a full-window oracle across bounded layouts" {
    const P = For(u8);
    var number_store: [5][2]u64 = undefined;
    var cells: [5]P.Cell = undefined;
    var children: [5]P.Child = undefined;
    var first = [_]u64{0} ** 10;
    var second = [_]u64{0} ** 10;
    var best = [_]u64{0} ** 10;
    var layout: u32 = 0;
    while (layout < 1024) : (layout += 1) {
        var child_count: usize = 0;
        var slot: usize = 0;
        while (slot < cells.len) : (slot += 1) {
            const kind = (layout >> @as(u5, @intCast(slot * 2))) & 3;
            switch (kind) {
                0 => cells[slot] = .{ .start = @intCast(slot), .kind = .empty },
                1 => cells[slot] = .{ .start = @intCast(slot), .kind = .barrier },
                else => {
                    const newer = 100 - @as(u64, @intCast(child_count * 4));
                    number_store[child_count] = .{ newer, newer - 1 };
                    children[child_count] = .{
                        .start = @intCast(slot),
                        .capacity = 1,
                        .safe = kind == 2,
                        .score = @intCast((slot + child_count) % 4),
                        .aligned_bits = @intCast((slot * 7 + child_count * 3) % 16),
                        .latest_access = @intCast((slot * 5 + child_count) % 13),
                        .allocation_numbers = number_store[child_count][0..],
                    };
                    cells[slot] = .{ .start = @intCast(slot), .kind = .child, .child_index = @intCast(child_count) };
                    child_count += 1;
                },
            }
        }

        var requested: u32 = 1;
        while (requested <= @as(u32, @intCast(cells.len))) : (requested += 1) {
            const planned = try P.plan(0, @intCast(cells.len), cells[0..], children[0..child_count], requested, .{
                .candidate_a = first[0..], .candidate_b = second[0..], .best_numbers = best[0..],
            });
            const oracle = try oraclePlan(P, 0, @intCast(cells.len), cells[0..], children[0..child_count], requested);
            try std.testing.expectEqual(oracle != null, planned != null);
            if (planned) |plan| {
                try std.testing.expectEqual(oracle.?.start, plan.start);
                try std.testing.expectEqual(oracle.?.end, plan.end);
                try std.testing.expectEqual(oracle.?.empty_only, plan.empty_only);
            }
        }
    }
}

test "whole child intervals may overshoot requested capacity but are never cut" {
    const P = For(u8);
    const first_numbers = [_]u64{ 40, 30 };
    const second_numbers = [_]u64{ 20 };
    const cells = [_]P.Cell{
        .{ .start = 5, .kind = .empty },
        .{ .start = 6, .kind = .child, .child_index = 0 },
        .{ .start = 8, .kind = .child, .child_index = 1 },
        .{ .start = 9, .kind = .barrier },
    };
    const children = [_]P.Child{
        .{ .start = 6, .capacity = 2, .safe = true, .score = 1, .aligned_bits = 0, .latest_access = 0, .allocation_numbers = first_numbers[0..] },
        .{ .start = 8, .capacity = 1, .safe = true, .score = 1, .aligned_bits = 0, .latest_access = 0, .allocation_numbers = second_numbers[0..] },
    };
    var first = [_]u64{0} ** 3;
    var second = [_]u64{0} ** 3;
    var best = [_]u64{0} ** 3;
    const selected = (try P.plan(5, 5, cells[0..], children[0..], 3, .{
        .candidate_a = first[0..], .candidate_b = second[0..], .best_numbers = best[0..],
    })).?;
    try std.testing.expectEqual(@as(u32, 5), selected.start);
    try std.testing.expectEqual(@as(u32, 8), selected.end);
    try std.testing.expectEqual((try oraclePlan(P, 5, 5, cells[0..], children[0..], 3)).?.end, selected.end);
}
