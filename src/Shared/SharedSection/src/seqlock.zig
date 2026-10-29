//! Per-source sequence (seqlock) rounds over a laid-out region: writing, reading
//! with a retry limit, the row-version rule for columns, and `end`.
//!
//! A source's sequence is even while its positions are stable and odd while its
//! writer is inside a round; `layout.ended_bit` marks it ended. Only the source's
//! writer moves its sequence, except `end`, which the region's owner calls before
//! unmapping. Each position has one source and positions never overlap, so
//! writers never exclude each other.
//!
//! Memory ordering. Zig has no standalone fence, so this is the fence-free
//! seqlock: data element stores in a round are release stores and data element
//! loads in a read are acquire loads.
//! - Writer: the odd sequence is stored first. Each later release store orders
//!   that odd store before itself, so a reader whose acquire load returns any
//!   element of the round sees the odd sequence, or a later one, at its re-check.
//!   The closing even store is a release, so a reader whose first acquire load
//!   returns it also sees every element of the round.
//! - Reader: load the sequence (acquire), copy the elements out with acquire
//!   loads, load the sequence again. An acquire load keeps every later load after
//!   it, so the re-check cannot move ahead of the copy. Equal sequences mean the
//!   copy holds one round's values; anything else means retry.
//! On x86-64 these acquire loads and release stores are plain moves.
const std = @import("std");
const layout = @import("layout.zig");
const Layout = layout.Layout;
const Error = layout.Error;
const Failure = layout.Failure;
const PositionEntry = layout.PositionEntry;
const PositionSpec = layout.PositionSpec;
const SourceState = layout.SourceState;
const ended_bit = layout.ended_bit;
const none = layout.none;

/// Retries a read makes before it gives a source up for the round. Callers pass
/// a limit to `read`; this is the default they use. Each retry first issues a
/// spin-loop hint; the limit keeps a reader from spinning on a writer that stays
/// odd, so a read always returns.
pub const default_retry_limit: u32 = 1000;

/// How a round ends.
pub const Outcome = union(enum) {
    has_value,
    /// The source's own reason code.
    unavailable: u32,
    /// Terminal: the source's positions are zeroed and it cannot begin again.
    ended,
};

/// One source's open round: `begin`, store into own positions, `end`. Caller
/// memory; extern so the C ABI can hand it out.
pub const Writer = extern struct {
    region: ?[*]align(8) u8,
    region_length: u64,
    /// The odd sequence while the round is open; the published value after `end`.
    sequence: u64,
    source: u32,

    pub fn begin(region: []align(8) u8, source: u32) Error!Writer {
        const view = try Layout.load(region);
        if (source >= view.header.source_count) return error.SourceIndex;
        const entry = view.sourceMut(region, source);
        const current = @atomicLoad(u64, &entry.sequence, .monotonic);
        if (current & ended_bit != 0) return error.Ended;
        if (current & 1 != 0) return error.WriteInProgress;
        // Monotonic suffices: every data store of the round is a release, which
        // orders this odd store before it. The exchange fails only if `end` set
        // the ended bit, or another round began, in between.
        if (@cmpxchgStrong(u64, &entry.sequence, current, current + 1, .monotonic, .monotonic)) |actual|
            return if (actual & ended_bit != 0) error.Ended else error.WriteInProgress;
        return .{ .region = region.ptr, .region_length = region.len, .sequence = current + 1, .source = source };
    }

    /// The even sequence `end` publishes. A column over an identity position of
    /// this same source records it as its identity sequence.
    pub fn roundSequence(self: *const Writer) u64 {
        return self.sequence + 1;
    }

    /// Stores `values` as elements `first..` of an own scalar, column, string
    /// column (u64 rows) or string table (bytes); `T` must have the element width.
    pub fn store(self: *const Writer, comptime T: type, position: u32, first: u32, values: []const T) Error!void {
        const region = try self.bytes();
        const view = try self.open(region);
        const spec = try own(view, region, self.source, position);
        if (@sizeOf(T) != spec.element_width) return error.ElementWidth;
        try storeElements(view, region, spec, first, std.mem.sliceAsBytes(values));
    }

    /// Like `store`, with the element width taken from the position; `values`
    /// holds whole little-endian elements. For the C ABI.
    pub fn storeRaw(self: *const Writer, position: u32, first: u32, values: []const u8) Error!void {
        const region = try self.bytes();
        const view = try self.open(region);
        const spec = try own(view, region, self.source, position);
        if (values.len % spec.element_width != 0) return error.ElementWidth;
        try storeElements(view, region, spec, first, values);
    }

    /// Sets an own column's or string column's row count, or a string table's
    /// used bytes, with the identity sequence the column was written against:
    /// the identity source's sequence whose rows these are (`roundSequence()`
    /// when that is this source). Readers ignore it for positions without one.
    pub fn setLength(self: *const Writer, position: u32, length: u32, identity_sequence: u64) Error!void {
        const region = try self.bytes();
        const view = try self.open(region);
        const spec = try own(view, region, self.source, position);
        if (spec.kind == .scalar) return error.WrongKind;
        if (length > spec.size / spec.element_width) return error.LengthOverCapacity;
        const entry = view.positionMut(region, position);
        @atomicStore(u32, &entry.length, length, .release);
        @atomicStore(u64, &entry.identity_sequence, identity_sequence, .release);
    }

    /// Appends `text` to the string table of own string column `column` and
    /// points row `row` at it. A round that rewrites its strings first sets the
    /// table's length to 0; the column's rows are set with `setLength`.
    pub fn appendString(self: *const Writer, column: u32, row: u32, text: []const u8) Error!void {
        const region = try self.bytes();
        const view = try self.open(region);
        const spec = try own(view, region, self.source, column);
        if (spec.kind != .string_column) return error.WrongKind;
        if (row >= spec.size / 8) return error.ElementRange;
        // checkSpec tied the table to this source as a string table.
        const table = try own(view, region, self.source, spec.strings);
        const entry = view.positionMut(region, spec.strings);
        // Only this source writes the table's length.
        const used = @atomicLoad(u32, &entry.length, .monotonic);
        if (used > table.size or text.len > table.size - used) return error.ElementRange;
        const data = view.dataAreaMut(region);
        storeAs(u8, data[@as(usize, table.offset) + used ..][0..text.len], text);
        @atomicStore(u32, &entry.length, used + @as(u32, @intCast(text.len)), .release);
        const row_value: u64 = @as(u64, used) | (@as(u64, text.len) << 32);
        storeAs(u64, data[@as(usize, spec.offset) + @as(usize, row) * 8 ..][0..8], std.mem.asBytes(&row_value));
    }

    /// Closes the round: stores the state, reason and write time (UTC Unix
    /// milliseconds), then the even sequence, with `ended_bit` for `.ended`.
    /// If the region ended during the round, re-zeroes this source's positions
    /// and returns `error.Ended`, so values stored after `end` zeroed them do
    /// not stay.
    pub fn end(self: *Writer, outcome: Outcome, write_time_unix_ms: i64) Error!void {
        const region = try self.bytes();
        const view = try Layout.load(region);
        if (self.source >= view.header.source_count) return error.SourceIndex;
        const entry = view.sourceMut(region, self.source);
        const current = @atomicLoad(u64, &entry.sequence, .monotonic);
        if (current & ended_bit != 0) {
            zeroSource(view, region, self.source);
            return error.Ended;
        }
        if (current != self.sequence or current & 1 == 0) return error.NotWriting;
        var state: SourceState = .has_value;
        var reason: u32 = 0;
        switch (outcome) {
            .has_value => {},
            .unavailable => |code| {
                state = .unavailable;
                reason = code;
            },
            .ended => {
                state = .ended;
                zeroSource(view, region, self.source);
            },
        }
        @atomicStore(SourceState, &entry.state, state, .release);
        @atomicStore(u32, &entry.reason, reason, .release);
        @atomicStore(i64, &entry.write_time_unix_ms, write_time_unix_ms, .release);
        const next = (self.sequence + 1) | (if (state == .ended) ended_bit else 0);
        // Release: every store of the round is visible before the new sequence.
        if (@cmpxchgStrong(u64, &entry.sequence, self.sequence, next, .release, .monotonic)) |actual| {
            if (actual & ended_bit == 0) return error.NotWriting;
            zeroSource(view, region, self.source);
            return error.Ended;
        }
        self.sequence = next;
    }

    fn bytes(self: *const Writer) Error![]align(8) u8 {
        const base = self.region orelse return error.InvalidArgument;
        return base[0 .. std.math.cast(usize, self.region_length) orelse return error.InvalidArgument];
    }

    /// The checked layout, while this round is still the source's open round.
    fn open(self: *const Writer, region: []align(8) u8) Error!Layout {
        const view = try Layout.load(region);
        if (self.source >= view.header.source_count) return error.SourceIndex;
        const current = @atomicLoad(u64, &view.source(region, self.source).sequence, .monotonic);
        if (current & ended_bit != 0) return error.Ended;
        if (current != self.sequence or current & 1 == 0) return error.NotWriting;
        return view;
    }
};

fn own(view: Layout, region: []align(8) const u8, source: u32, position: u32) Error!PositionSpec {
    if (position >= view.header.position_count) return error.PositionIndex;
    const spec = try view.checkedSpec(region, position);
    if (spec.source != source) return error.NotOwner;
    return spec;
}

fn storeElements(view: Layout, region: []align(8) u8, spec: PositionSpec, first: u32, values: []const u8) Error!void {
    const width = spec.element_width;
    if (@as(u64, first) + values.len / width > spec.size / width) return error.ElementRange;
    const target = view.dataAreaMut(region)[@as(usize, spec.offset) + @as(usize, first) * width ..][0..values.len];
    switch (width) {
        1 => storeAs(u8, target, values),
        2 => storeAs(u16, target, values),
        4 => storeAs(u32, target, values),
        8 => storeAs(u64, target, values),
        else => unreachable, // checkSpec admits only these widths
    }
}

/// Zeroes the source's positions and their lengths in the region.
fn zeroSource(view: Layout, region: []align(8) u8, source: u32) void {
    const data = view.dataAreaMut(region);
    var index: u32 = 0;
    while (index < view.header.position_count) : (index += 1) {
        const spec = view.checkedSpec(region, index) catch continue;
        if (spec.source != source) continue;
        const target = data[spec.offset..][0..spec.size];
        switch (spec.element_width) {
            1 => zeroAs(u8, target),
            2 => zeroAs(u16, target),
            4 => zeroAs(u32, target),
            8 => zeroAs(u64, target),
            else => unreachable,
        }
        const entry = view.positionMut(region, index);
        @atomicStore(u32, &entry.length, 0, .release);
        @atomicStore(u64, &entry.identity_sequence, 0, .release);
    }
}

fn storeAs(comptime U: type, target: []u8, values: []const u8) void {
    const width = @sizeOf(U);
    const aligned: []align(@alignOf(U)) u8 = @alignCast(target);
    const elements: [*]U = @ptrCast(aligned.ptr);
    for (0..values.len / width) |i| @atomicStore(U, &elements[i], std.mem.readInt(U, values[i * width ..][0..width], .little), .release);
}

fn zeroAs(comptime U: type, target: []u8) void {
    const aligned: []align(@alignOf(U)) u8 = @alignCast(target);
    const elements: [*]U = @ptrCast(aligned.ptr);
    for (0..target.len / @sizeOf(U)) |i| @atomicStore(U, &elements[i], 0, .release);
}

fn copyAs(comptime U: type, from: []const u8, to: []u8) void {
    const width = @sizeOf(U);
    const aligned: []align(@alignOf(U)) const u8 = @alignCast(from);
    const elements: [*]const U = @ptrCast(aligned.ptr);
    for (0..from.len / width) |i| std.mem.writeInt(U, to[i * width ..][0..width], @atomicLoad(U, &elements[i], .acquire), .little);
}

/// What a reader holds for a source. `none`: nothing read yet; `failed`: this
/// round's read failed and `reason` holds the `Failure` code.
pub const CopyStatus = enum(u32) { none = 0, has_value = 1, unavailable = 2, ended = 3, failed = 4, _ };

/// Zero-initialized means nothing read yet.
pub const SourceCopy = extern struct {
    /// The sequence the held copy was taken at, unless status is none or failed.
    sequence: u64 = 0,
    write_time_unix_ms: i64 = 0,
    status: CopyStatus = .none,
    /// The source's reason when unavailable; the `Failure` code when failed.
    reason: u32 = 0,
};

/// Caller memory that reads copy into. `data` mirrors the data area at the same
/// offsets. `positions` holds each position's checked layout and the length and
/// identity sequence copied with it; `sources` holds each source's result.
/// Lengths are element counts; data needs at least the data capacity. Zeroed
/// `sources` and `positions` mean nothing read yet.
pub const Copy = extern struct {
    data: ?[*]u8 = null,
    data_length: u64 = 0,
    sources: ?[*]SourceCopy = null,
    positions: ?[*]PositionEntry = null,
    source_length: u32 = 0,
    position_length: u32 = 0,

    pub fn init(data: []u8, sources: []SourceCopy, positions: []PositionEntry) Copy {
        @memset(sources, .{});
        @memset(std.mem.sliceAsBytes(positions), 0);
        return .{ .data = data.ptr, .data_length = data.len, .sources = sources.ptr, .positions = positions.ptr, .source_length = @intCast(sources.len), .position_length = @intCast(positions.len) };
    }
    pub fn dataSlice(self: *const Copy) []u8 {
        const base = self.data orelse return &.{};
        return base[0..@intCast(self.data_length)];
    }
    pub fn sourceSlice(self: *const Copy) []SourceCopy {
        const base = self.sources orelse return &.{};
        return base[0..self.source_length];
    }
    pub fn positionSlice(self: *const Copy) []PositionEntry {
        const base = self.positions orelse return &.{};
        return base[0..self.position_length];
    }
};

pub const SourceRead = union(enum) {
    /// Same sequence as the held copy: nothing was copied, nothing to decode.
    unchanged,
    /// A consistent copy at this sequence; the source has a value.
    value: u64,
    /// The source wrote unavailable with this reason.
    unavailable: u32,
    /// The source has ended; the copy holds no data for it.
    ended,
    /// This round's read failed: malformed content or the retry limit.
    failed: Failure,
};

/// Reads source `source` into `copy`, its positions landing at their data area
/// offsets. An unchanged sequence returns at once. Otherwise copies, loads the
/// sequence again and retries, at most `retry_limit` times; past that the
/// source is failed for this round. Region contents are untrusted: a malformed
/// region fails with a reason and is never read out of bounds.
/// `copy.sources[source]` keeps the result for `positionValue`.
pub fn read(region: []align(8) const u8, copy: *Copy, source: u32, retry_limit: u32) SourceRead {
    const sources = copy.sourceSlice();
    const view = Layout.load(region) catch |err| return fail(sources, source, layout.failureOf(err));
    const header = view.header;
    if (source >= header.source_count) return fail(sources, source, .source_index);
    const positions = copy.positionSlice();
    if (copy.dataSlice().len < header.data_capacity or sources.len < header.source_count or positions.len < header.position_count)
        return fail(sources, source, .copy_too_small);
    const held = &sources[source];
    const sequence_field = &view.source(region, source).sequence;
    var sequence = @atomicLoad(u64, sequence_field, .acquire);
    // An unchanged source costs this one load.
    if (holds(held.status) and sequence == held.sequence) return .unchanged;
    // The position table is checked once per changed read, into the copy.
    var index: u32 = 0;
    while (index < header.position_count) : (index += 1) {
        positions[index].spec = view.checkedSpec(region, index) catch |err| return fail(sources, source, layout.failureOf(err));
    }
    held.status = .none; // the held copy is about to be overwritten
    var attempt: u32 = 0;
    while (true) : (sequence = @atomicLoad(u64, sequence_field, .acquire)) {
        if (sequence & ended_bit != 0) return settleEnded(copy, header.position_count, source, sequence);
        if (sequence & 1 == 0) {
            const round = copyRound(view, region, copy, source);
            // The re-check. Equal: the copy, and any fault found in it, is one round's content.
            if (@atomicLoad(u64, sequence_field, .acquire) == sequence) return settle(copy, header.position_count, source, sequence, round);
        }
        if (attempt == retry_limit) return fail(sources, source, .retry_limit);
        attempt += 1;
        std.atomic.spinLoopHint();
    }
}

fn holds(status: CopyStatus) bool {
    return status == .has_value or status == .unavailable or status == .ended;
}

fn fail(sources: []SourceCopy, source: u32, failure: Failure) SourceRead {
    if (source < sources.len) sources[source] = .{ .status = .failed, .reason = @intFromEnum(failure) };
    return .{ .failed = failure };
}

const Round = struct { state: SourceState, reason: u32, write_time_unix_ms: i64, failure: ?Failure = null };

/// One attempt: the source's state, then, with a value, each own position's
/// length and elements. Only checked layout from the copy bounds the accesses.
fn copyRound(view: Layout, region: []align(8) const u8, copy: *Copy, source: u32) Round {
    const entry = view.source(region, source);
    var round = Round{
        .state = @atomicLoad(SourceState, &entry.state, .acquire),
        .reason = @atomicLoad(u32, &entry.reason, .acquire),
        .write_time_unix_ms = @atomicLoad(i64, &entry.write_time_unix_ms, .acquire),
    };
    switch (round.state) {
        .has_value => {},
        .unavailable, .ended => return round,
        _ => {
            round.failure = .source_state;
            return round;
        },
    }
    const from = view.dataArea(region);
    const to = copy.dataSlice();
    const positions = copy.positionSlice();
    var index: u32 = 0;
    while (index < view.header.position_count) : (index += 1) {
        const slot = &positions[index];
        if (slot.spec.source != source) continue;
        const width = slot.spec.element_width;
        const capacity = slot.spec.size / width;
        const position = view.position(region, index);
        const length = if (slot.spec.kind == .scalar) capacity else @atomicLoad(u32, &position.length, .acquire);
        if (length > capacity) {
            round.failure = .length_over_capacity;
            return round;
        }
        slot.length = length;
        slot.identity_sequence = @atomicLoad(u64, &position.identity_sequence, .acquire);
        const span = @as(usize, length) * width;
        const source_bytes = from[slot.spec.offset..][0..span];
        const target = to[slot.spec.offset..][0..span];
        switch (width) {
            1 => copyAs(u8, source_bytes, target),
            2 => copyAs(u16, source_bytes, target),
            4 => copyAs(u32, source_bytes, target),
            8 => copyAs(u64, source_bytes, target),
            else => unreachable,
        }
    }
    return round;
}

fn settle(copy: *Copy, count: u32, source: u32, sequence: u64, round: Round) SourceRead {
    const sources = copy.sourceSlice();
    if (round.failure) |failure| return fail(sources, source, failure);
    switch (round.state) {
        // An ended state without the ended bit is still ended.
        .ended => return settleEnded(copy, count, source, sequence),
        .unavailable => {
            sources[source] = .{ .sequence = sequence, .write_time_unix_ms = round.write_time_unix_ms, .status = .unavailable, .reason = round.reason };
            return .{ .unavailable = round.reason };
        },
        else => {},
    }
    if (checkStrings(copy, count, source)) |failure| return fail(sources, source, failure);
    sources[source] = .{ .sequence = sequence, .write_time_unix_ms = round.write_time_unix_ms, .status = .has_value };
    return .{ .value = sequence };
}

/// "No data": the copy keeps nothing of an ended source.
fn settleEnded(copy: *Copy, count: u32, source: u32, sequence: u64) SourceRead {
    const data = copy.dataSlice();
    for (copy.positionSlice()[0..count]) |*slot| {
        if (slot.spec.source != source) continue;
        @memset(data[slot.spec.offset..][0..slot.spec.size], 0);
        slot.length = 0;
        slot.identity_sequence = 0;
    }
    copy.sourceSlice()[source] = .{ .sequence = sequence, .status = .ended };
    return .ended;
}

/// Every string row of the source lies inside its table's copied bytes. The
/// column and its table belong to this source, so one round copied both.
fn checkStrings(copy: *const Copy, count: u32, source: u32) ?Failure {
    const data = copy.dataSlice();
    const positions = copy.positionSlice();
    for (positions[0..count]) |column| {
        if (column.spec.source != source or column.spec.kind != .string_column) continue;
        const table = positions[column.spec.strings];
        const rows = data[column.spec.offset..][0 .. @as(usize, column.length) * 8];
        for (0..column.length) |row| {
            const value = std.mem.readInt(u64, rows[row * 8 ..][0..8], .little);
            if ((value & 0xFFFF_FFFF) + (value >> 32) > table.length) return .string_range;
        }
    }
    return null;
}

pub const Value = struct {
    /// In the copy's data: a scalar's whole size, a column's or string column's
    /// written rows, a string table's written bytes.
    bytes: []const u8,
    /// Elements for a scalar, rows for a column or string column, bytes for a
    /// string table.
    length: u32,
};

pub const PositionValue = union(enum) {
    value: Value,
    /// Nothing read yet, or a column written against identity rows other than
    /// the ones the copy holds: the column has not arrived for this round.
    not_arrived,
    /// The owning source wrote unavailable with this reason.
    unavailable: u32,
    ended,
    failed: Failure,
};

/// The value of `position` in `copy`. A column with an identity position is
/// used only when its identity sequence equals the sequence of the identity
/// source's held copy, so old rows are never paired with new values.
pub fn positionValue(copy: *const Copy, position: u32) PositionValue {
    const positions = copy.positionSlice();
    const sources = copy.sourceSlice();
    if (position >= positions.len) return .{ .failed = .position_index };
    const slot = positions[position];
    if (slot.spec.source >= sources.len) return .{ .failed = .position_source };
    const owner = sources[slot.spec.source];
    switch (owner.status) {
        .has_value => {},
        .unavailable => return .{ .unavailable = owner.reason },
        .ended => return .ended,
        .failed => return .{ .failed = @enumFromInt(owner.reason) },
        else => return .not_arrived,
    }
    if (slot.spec.identity != none) {
        if (slot.spec.identity >= positions.len) return .{ .failed = .position_identity };
        const rows = positions[slot.spec.identity];
        if (rows.spec.source >= sources.len) return .{ .failed = .position_source };
        const rows_source = sources[rows.spec.source];
        if (rows_source.status != .has_value or rows_source.sequence != slot.identity_sequence) return .not_arrived;
        if (rows.length != slot.length) return .{ .failed = .rows_mismatch };
    }
    const data = copy.dataSlice();
    const span = @as(u64, slot.length) * slot.spec.element_width;
    if (@as(u64, slot.spec.offset) + span > data.len) return .{ .failed = .position_range };
    return .{ .value = .{ .bytes = data[slot.spec.offset..][0..@intCast(span)], .length = slot.length } };
}

/// Row `row` of string column `column`; null when the column has no value this
/// round or the row is out of range.
pub fn stringAt(copy: *const Copy, column: u32, row: u32) ?[]const u8 {
    const rows = switch (positionValue(copy, column)) {
        .value => |found| found,
        else => return null,
    };
    const spec = copy.positionSlice()[column].spec;
    if (spec.kind != .string_column or row >= rows.length) return null;
    const table = switch (positionValue(copy, spec.strings)) {
        .value => |found| found,
        else => return null,
    };
    const value = std.mem.readInt(u64, rows.bytes[@as(usize, row) * 8 ..][0..8], .little);
    const offset = value & 0xFFFF_FFFF;
    const length = value >> 32;
    if (offset + length > table.bytes.len) return null;
    return table.bytes[@intCast(offset)..][0..@intCast(length)];
}

/// "No data": marks every source ended, then zeroes every position's length
/// and the whole data area. The region's owner calls it before unmapping,
/// after stopping its own writers. A round still open elsewhere fails at its
/// end and re-zeroes its own positions. The area is zeroed with u64 stores;
/// no reader uses an ended source's data, so mixed widths are never read.
pub fn end(region: []align(8) u8, write_time_unix_ms: i64) Error!void {
    const view = try Layout.load(region);
    var index: u32 = 0;
    while (index < view.header.source_count) : (index += 1) {
        const entry = view.sourceMut(region, index);
        // The ended bit first: no round can begin, an open round fails at its
        // end, and a reader stops using the data.
        _ = @atomicRmw(u64, &entry.sequence, .Or, ended_bit, .acq_rel);
        @atomicStore(SourceState, &entry.state, .ended, .release);
        @atomicStore(u32, &entry.reason, 0, .release);
        @atomicStore(i64, &entry.write_time_unix_ms, write_time_unix_ms, .release);
    }
    index = 0;
    while (index < view.header.position_count) : (index += 1) {
        const entry = view.positionMut(region, index);
        @atomicStore(u32, &entry.length, 0, .release);
        @atomicStore(u64, &entry.identity_sequence, 0, .release);
    }
    zeroAs(u64, view.dataAreaMut(region));
}
