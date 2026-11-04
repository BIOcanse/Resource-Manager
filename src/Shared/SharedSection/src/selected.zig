//! Selected read: one call reads the sources a reader names into compact caller
//! memory sized for them only (backend data area, section 12: the buffer is
//! sized by the positions of these sources, never by the whole region).
//!
//! The selection is the named sources plus, recursively, every source owning
//! an identity position that a selected column is written against. All
//! positions of each selected source come with it, so a string column brings
//! its string table, which belongs to the same source. Nothing else of the
//! region is copied or reserved.
//!
//! The copy's tables are dense: entry `j` of `sources` and `source_indices` is
//! the selection's `j`-th source, entry `k` of `positions` and
//! `position_indices` its `k`-th position, both ascending by region index. In
//! a dense position entry `spec.source`, `spec.identity` and `spec.strings` are
//! indexes into these tables and `spec.offset` is the offset in the copy's
//! data. Each position takes its full reserved size at the next multiple of 8,
//! in region order.
//!
//! Every call works the selection out again and checks the positions of the
//! selected sources (`Layout.check`). When the copy's tables already hold
//! exactly that selection and layout, each source keeps its copy and an
//! unchanged sequence copies nothing; otherwise the tables are rewritten and
//! every source is read afresh. A structural failure marks every held source
//! failed, so no value of the copy stays usable.
//!
//! The mapping is the caller's: a binding that maps another region, or
//! another incarnation at the same address, zeroes the copy's tables first.
const policy = @import("NativePolicy.g.zig");
const std = @import("std");
const layout = @import("layout.zig");
const seqlock = @import("seqlock.zig");
const Layout = layout.Layout;
const Error = layout.Error;
const Failure = layout.Failure;
const PositionEntry = layout.PositionEntry;
const SourceCopy = seqlock.SourceCopy;
const PositionValue = seqlock.PositionValue;
const ended_bit = layout.ended_bit;
const none = layout.none;

/// Most sources one selection holds, named and added together. A call works
/// the selection out in a list of this size on its stack (4 KiB), since it
/// allocates nothing.
pub const selected_source_limit: u32 = policy.selected_source_limit;

/// What a selection needs: bytes of data, and entries of `sources` and
/// `source_indices` (`source_count`) and of `positions` and
/// `position_indices` (`position_count`).
pub const SelectedRequirements = extern struct {
    data_bytes: u64 = 0,
    source_count: u32 = 0,
    position_count: u32 = 0,
};

/// A selected copy in caller memory. The caller provides the five tables with
/// their lengths in elements (bytes for `data`); `sources` and
/// `source_indices` share `source_length`, `positions` and `position_indices`
/// share `position_length`. A read sets both lengths to the entries the
/// selection uses. Zero every table before first use.
pub const SelectedCopy = extern struct {
    data: ?[*]u8 = null,
    data_length: u64 = 0,
    sources: ?[*]SourceCopy = null,
    positions: ?[*]PositionEntry = null,
    source_indices: ?[*]u32 = null,
    position_indices: ?[*]u32 = null,
    source_length: u32 = 0,
    position_length: u32 = 0,

    /// Marks every held source failed with `failure`: nothing of the copy is
    /// usable afterwards.
    pub fn markFailed(self: *SelectedCopy, failure: Failure) void {
        const base = self.sources orelse return;
        for (base[0..self.source_length]) |*held| held.* = .{ .status = .failed, .reason = @intFromEnum(failure), .source = held.source };
    }

    fn tables(self: *const SelectedCopy) Error!Tables {
        return .{
            .data = try tableOf(u8, self.data, self.data_length),
            .sources = try tableOf(SourceCopy, self.sources, self.source_length),
            .positions = try tableOf(PositionEntry, self.positions, self.position_length),
            .source_indices = try tableOf(u32, self.source_indices, self.source_length),
            .position_indices = try tableOf(u32, self.position_indices, self.position_length),
        };
    }
};

const Tables = struct {
    data: []u8,
    sources: []SourceCopy,
    positions: []PositionEntry,
    source_indices: []u32,
    position_indices: []u32,
};

fn tableOf(comptime T: type, pointer: ?[*]T, length: u64) Error![]T {
    const count = std.math.cast(usize, length) orelse return error.InvalidArgument;
    if (pointer) |base| return base[0..count];
    return if (count == 0) &.{} else error.InvalidArgument;
}

fn itself(value: u32) u32 {
    return value;
}
fn indexIn(items: []const u32, key: u32) ?u32 {
    return @intCast(seqlock.find(u32, items, key, itself) orelse return null);
}

/// The selection's sources, ascending.
const Sources = struct {
    items: [selected_source_limit]u32 = undefined,
    count: u32 = 0,

    fn slice(self: *const Sources) []const u32 {
        return self.items[0..self.count];
    }
    fn indexOf(self: *const Sources, source: u32) ?u32 {
        return indexIn(self.slice(), source);
    }
    /// Adds a source not yet in the list, keeping it ascending.
    fn add(self: *Sources, source: u32) Error!void {
        if (self.count == selected_source_limit) return error.SelectionTooLarge;
        var at = self.count;
        while (at > 0 and self.items[at - 1] > source) : (at -= 1) self.items[at] = self.items[at - 1];
        self.items[at] = source;
        self.count += 1;
    }
};

const Measure = struct {
    position_count: u32 = 0,
    data_bytes: u64 = 0,
    /// `stored` holds exactly the selection's position indexes in its prefix.
    indices_match: bool = true,
};

/// Works the selection for `named` out into `sources`, checking every
/// position of a selected source, and measures it. `stored` is the copy's
/// position index table, compared on the way (empty when only measuring).
fn select(view: Layout, region: []align(8) const u8, named: []const u32, sources: *Sources, stored: []const u32) Error!Measure {
    if (named.len == 0) return error.SelectionEmpty;
    for (named, 0..) |source, index| {
        if (source >= view.header.source_count) return error.SourceIndex;
        if (index > 0 and named[index - 1] >= source) return error.SelectionOrder;
    }
    if (named.len > selected_source_limit) return error.SelectionTooLarge;
    @memcpy(sources.items[0..named.len], named);
    sources.count = @intCast(named.len);
    // A pass that adds an identity owner is followed by another, so the pass
    // that adds none measured the whole selection. Every pass but the last
    // adds a source, which bounds the passes by the limit.
    var passes: u32 = 0;
    while (passes <= selected_source_limit) : (passes += 1) {
        var measure = Measure{};
        var added = false;
        var index: u32 = 0;
        while (index < view.header.position_count) : (index += 1) {
            const spec = layout.loadSpec(view.position(region, index));
            if (sources.indexOf(spec.source) == null) continue;
            try view.check(region, index, spec);
            if (spec.identity != none) {
                // `check` kept the identity inside the table.
                const owner = @atomicLoad(u32, &view.position(region, spec.identity).spec.source, .monotonic);
                if (owner >= view.header.source_count) return error.PositionSource;
                if (sources.indexOf(owner) == null) {
                    try sources.add(owner);
                    added = true;
                }
            }
            if (measure.position_count >= stored.len or stored[measure.position_count] != index) measure.indices_match = false;
            measure.data_bytes = std.mem.alignForward(u64, measure.data_bytes + spec.size, 8);
            measure.position_count += 1;
        }
        if (added) continue;
        // Positions that share no bytes fit compactly in a data area of u32
        // size, so only overlapping positions can exceed it.
        if (measure.data_bytes > std.math.maxInt(u32)) return error.PositionOverlap;
        return measure;
    }
    return error.SelectionTooLarge;
}

/// What `readSelected` needs for `named` (strictly ascending source indexes)
/// over `region`. Writes nothing else.
pub fn selectedRequirements(region: []align(8) const u8, named: []const u32) Error!SelectedRequirements {
    const view = try Layout.load(region);
    var sources = Sources{};
    const measure = try select(view, region, named, &sources, &.{});
    return .{ .data_bytes = measure.data_bytes, .source_count = sources.count, .position_count = measure.position_count };
}

/// Reads the selection for `named` (strictly ascending source indexes) into
/// `copy`: every selected source, each by its own sequence with at most
/// `retry_limit` retries, as `seqlock.read` does. A source's outcome, failed
/// ones included, is in its held entry. A returned error is structural: the
/// call marks every held source failed.
pub fn readSelected(region: []align(8) const u8, named: []const u32, copy: *SelectedCopy, retry_limit: u32) Error!void {
    readInto(region, named, copy, retry_limit) catch |err| {
        copy.markFailed(layout.failureOf(err));
        return err;
    };
}

fn readInto(region: []align(8) const u8, named: []const u32, copy: *SelectedCopy, retry_limit: u32) Error!void {
    const room = try copy.tables();
    const view = try Layout.load(region);
    var sources = Sources{};
    const measure = try select(view, region, named, &sources, room.position_indices);
    const source_count = sources.count;
    const position_count = measure.position_count;
    if (source_count > room.sources.len or position_count > room.positions.len or measure.data_bytes > room.data.len) return error.CopyTooSmall;
    const used = Tables{
        .data = room.data,
        .sources = room.sources[0..source_count],
        .positions = room.positions[0..position_count],
        .source_indices = room.source_indices[0..source_count],
        .position_indices = room.position_indices[0..position_count],
    };
    var same = measure.indices_match;
    if (!same) try recordIndices(view, region, &sources, used.position_indices);
    if (!try layOut(view, region, &sources, used, measure.data_bytes)) same = false;
    for (sources.slice(), used.source_indices, used.sources) |source, index, held| {
        if (index != source or held.source != source) same = false;
    }
    if (!same) {
        for (sources.slice(), used.source_indices, used.sources) |source, *index, *held| {
            index.* = source;
            held.* = .{ .source = source };
        }
    }
    copy.source_length = source_count;
    copy.position_length = position_count;
    for (0..source_count) |dense| readSource(view, region, used, @intCast(dense), retry_limit);
}

/// Writes the region indexes of the selected sources' positions, ascending.
fn recordIndices(view: Layout, region: []align(8) const u8, sources: *const Sources, indices: []u32) Error!void {
    var count: usize = 0;
    var index: u32 = 0;
    while (index < view.header.position_count) : (index += 1) {
        const owner = @atomicLoad(u32, &view.position(region, index).spec.source, .monotonic);
        if (sources.indexOf(owner) == null) continue;
        if (count == indices.len) return error.LayoutChanged;
        indices[count] = index;
        count += 1;
    }
    if (count != indices.len) return error.LayoutChanged;
}

/// Writes each selected position's dense entry from its checked spec.
/// Returns whether every entry already held it. A difference from what
/// `select` saw means the region changed during the call.
fn layOut(view: Layout, region: []align(8) const u8, sources: *const Sources, used: Tables, data_bytes: u64) Error!bool {
    var same = true;
    var offset: u64 = 0;
    for (used.position_indices, used.positions) |index, *entry| {
        if (index >= view.header.position_count) return error.LayoutChanged;
        const spec = layout.loadSpec(view.position(region, index));
        try view.check(region, index, spec);
        var dense = spec;
        dense.source = sources.indexOf(spec.source) orelse return error.LayoutChanged;
        // `offset` stays within `data_bytes`, which `select` kept within u32.
        dense.offset = @intCast(offset);
        if (spec.identity != none) dense.identity = indexIn(used.position_indices, spec.identity) orelse return error.LayoutChanged;
        if (spec.strings != none) dense.strings = indexIn(used.position_indices, spec.strings) orelse return error.LayoutChanged;
        offset = std.mem.alignForward(u64, offset + spec.size, 8);
        if (offset > data_bytes) return error.LayoutChanged;
        if (!std.meta.eql(entry.spec, dense)) {
            same = false;
            entry.* = .{ .spec = dense };
        }
    }
    if (offset != data_bytes) return error.LayoutChanged;
    return same;
}

/// Reads selected source `dense` (`seqlock.read`'s rule): an unchanged
/// sequence returns at once and copies nothing.
fn readSource(view: Layout, region: []align(8) const u8, used: Tables, dense: u32, retry_limit: u32) void {
    const held = &used.sources[dense];
    const sequence_field = &view.source(region, used.source_indices[dense]).sequence;
    var sequence = @atomicLoad(u64, sequence_field, .acquire);
    if (seqlock.holds(held.status) and sequence == held.sequence) return;
    held.status = .none; // the held copy is about to be overwritten
    var attempt: u32 = 0;
    while (true) : (sequence = @atomicLoad(u64, sequence_field, .acquire)) {
        if (sequence & ended_bit != 0) return settleEnded(used, dense, sequence);
        if (sequence & 1 == 0) {
            const round = copyRound(view, region, used, dense);
            // The re-check. Equal: the copy, and any fault found in it, is one round's content.
            if (@atomicLoad(u64, sequence_field, .acquire) == sequence) return settle(used, dense, sequence, round);
        }
        if (attempt == retry_limit) return fail(held, .retry_limit);
        attempt += 1;
        std.atomic.spinLoopHint();
    }
}

fn fail(held: *SourceCopy, failure: Failure) void {
    held.* = .{ .status = .failed, .reason = @intFromEnum(failure), .source = held.source };
}

/// One attempt: the source's state, then, with a value, each of its positions'
/// length and elements.
fn copyRound(view: Layout, region: []align(8) const u8, used: Tables, dense: u32) seqlock.Round {
    var round = seqlock.loadRound(view.source(region, used.source_indices[dense]));
    if (round.failure != null or round.state != .has_value) return round;
    const from = view.dataArea(region);
    for (used.position_indices, used.positions) |index, *entry| {
        const spec = entry.spec;
        if (spec.source != dense) continue;
        const position = view.position(region, index);
        // The dense entry keeps the copy's offset; the region's is loaded and
        // checked again, against the size `layOut` checked in this call.
        const region_offset = @atomicLoad(u32, &position.spec.offset, .monotonic);
        if (region_offset % 8 != 0 or @as(u64, region_offset) + spec.size > from.len) {
            round.failure = .layout_changed;
            return round;
        }
        const width = spec.element_width;
        const capacity = spec.size / width;
        const length = if (spec.kind == .scalar) capacity else @atomicLoad(u32, &position.length, .acquire);
        if (length > capacity) {
            round.failure = .length_over_capacity;
            return round;
        }
        entry.length = length;
        entry.identity_sequence = @atomicLoad(u64, &position.identity_sequence, .acquire);
        const span = @as(usize, length) * width;
        // `layOut` kept every extent inside the copy's data.
        seqlock.copyElements(width, from[region_offset..][0..span], used.data[spec.offset..][0..span]);
    }
    return round;
}

fn settle(used: Tables, dense: u32, sequence: u64, round: seqlock.Round) void {
    const held = &used.sources[dense];
    if (round.failure) |failure| return fail(held, failure);
    switch (round.state) {
        // An ended state without the ended bit is still ended.
        .ended => return settleEnded(used, dense, sequence),
        .unavailable => {
            held.* = .{ .sequence = sequence, .write_time_unix_ms = round.write_time_unix_ms, .status = .unavailable, .reason = round.reason, .source = held.source };
            return;
        },
        else => {},
    }
    if (checkStrings(used, dense)) |failure| return fail(held, failure);
    held.* = .{ .sequence = sequence, .write_time_unix_ms = round.write_time_unix_ms, .status = .has_value, .source = held.source };
}

/// "No data": the copy keeps nothing of an ended source.
fn settleEnded(used: Tables, dense: u32, sequence: u64) void {
    for (used.positions) |*entry| {
        if (entry.spec.source != dense) continue;
        if (@as(u64, entry.spec.offset) + entry.spec.size <= used.data.len) @memset(used.data[entry.spec.offset..][0..entry.spec.size], 0);
        entry.length = 0;
        entry.identity_sequence = 0;
    }
    const held = &used.sources[dense];
    held.* = .{ .sequence = sequence, .status = .ended, .source = held.source };
}

/// Every string row of the source lies inside its table's copied bytes. The
/// column and its table belong to this source, so one round copied both.
fn checkStrings(used: Tables, dense: u32) ?Failure {
    for (used.positions) |column| {
        if (column.spec.source != dense or column.spec.kind != .string_column) continue;
        if (column.spec.strings >= used.positions.len) return .position_strings;
        const table = used.positions[column.spec.strings];
        if (table.spec.source != dense or table.spec.kind != .string_table) return .position_strings;
        const rows = used.data[column.spec.offset..][0 .. @as(usize, column.length) * 8];
        for (0..column.length) |row| {
            const value = std.mem.readInt(u64, rows[row * 8 ..][0..8], .little);
            if ((value & 0xFFFF_FFFF) + (value >> 32) > table.length) return .string_range;
        }
    }
    return null;
}

/// The value of `position` (its index in the region's position table) in
/// `copy`, by `seqlock.positionValue`'s rules over the copy's dense tables. A
/// value's offset indexes the copy's data.
pub fn selectedPositionValue(copy: *const SelectedCopy, position: u32) PositionValue {
    const used = copy.tables() catch return .{ .failed = .invalid_argument };
    const entry = used.positions[indexIn(used.position_indices, position) orelse return .{ .failed = .not_selected }];
    if (entry.spec.source >= used.sources.len) return .{ .failed = .not_selected };
    const owner = used.sources[entry.spec.source];
    switch (owner.status) {
        .has_value => {},
        .unavailable => return .{ .unavailable = owner.reason },
        .ended => return .ended,
        .failed => return .{ .failed = @enumFromInt(owner.reason) },
        else => return .not_arrived,
    }
    if (entry.spec.identity != none) {
        if (entry.spec.identity >= used.positions.len) return .{ .failed = .identity_not_selected };
        const rows = used.positions[entry.spec.identity];
        if (rows.spec.source >= used.sources.len) return .{ .failed = .identity_not_selected };
        const rows_owner = used.sources[rows.spec.source];
        if (rows_owner.status != .has_value or rows_owner.sequence != entry.identity_sequence) return .not_arrived;
        if (rows.length != entry.length) return .{ .failed = .rows_mismatch };
    }
    const span = @as(u64, entry.length) * entry.spec.element_width;
    if (@as(u64, entry.spec.offset) + span > used.data.len) return .{ .failed = .copy_too_small };
    return .{ .value = .{ .bytes = used.data[entry.spec.offset..][0..@intCast(span)], .offset = entry.spec.offset, .length = entry.length } };
}

comptime {
    const assert = std.debug.assert;
    assert(@sizeOf(SelectedRequirements) == 16 and @offsetOf(SelectedRequirements, "source_count") == 8 and @offsetOf(SelectedRequirements, "position_count") == 12);
    assert(@sizeOf(SelectedCopy) == 56);
    assert(@offsetOf(SelectedCopy, "data_length") == 8 and @offsetOf(SelectedCopy, "sources") == 16 and @offsetOf(SelectedCopy, "positions") == 24);
    assert(@offsetOf(SelectedCopy, "source_indices") == 32 and @offsetOf(SelectedCopy, "position_indices") == 40);
    assert(@offsetOf(SelectedCopy, "source_length") == 48 and @offsetOf(SelectedCopy, "position_length") == 52);
}
