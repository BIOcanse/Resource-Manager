//! SharedSection: the memory layout of a shared region (header, source table,
//! position table, data area) and the per-source sequence rules for writing and
//! reading it. Callers create and map sections, duplicate handles and decode
//! their data kind; this package works only on a caller-provided byte region.
const std = @import("std");
pub const layout = @import("layout.zig");
pub const seqlock = @import("seqlock.zig");

pub const magic = layout.magic;
pub const version = layout.version;
pub const none = layout.none;
pub const owner_name_capacity = layout.owner_name_capacity;
pub const not_written_reason = layout.not_written_reason;
pub const ended_bit = layout.ended_bit;
pub const Header = layout.Header;
pub const SourceState = layout.SourceState;
pub const SourceIdentity = layout.SourceIdentity;
pub const SourceEntry = layout.SourceEntry;
pub const PositionKind = layout.PositionKind;
pub const PositionSpec = layout.PositionSpec;
pub const PositionEntry = layout.PositionEntry;
pub const Failure = layout.Failure;
pub const Error = layout.Error;
pub const failureOf = layout.failureOf;
pub const requiredSize = layout.requiredSize;
pub const initialize = layout.initialize;
pub const describe = layout.describe;
pub const describeSource = layout.describeSource;
pub const describePosition = layout.describePosition;

pub const default_retry_limit = seqlock.default_retry_limit;
pub const Outcome = seqlock.Outcome;
pub const Writer = seqlock.Writer;
pub const CopyStatus = seqlock.CopyStatus;
pub const SourceCopy = seqlock.SourceCopy;
pub const SelectedPosition = seqlock.SelectedPosition;
pub const SelectionSize = seqlock.SelectionSize;
pub const Selection = seqlock.Selection;
pub const selectionSize = seqlock.selectionSize;
pub const SourceRead = seqlock.SourceRead;
pub const Value = seqlock.Value;
pub const PositionValue = seqlock.PositionValue;
pub const read = seqlock.read;
pub const positionValue = seqlock.positionValue;
pub const stringAt = seqlock.stringAt;
pub const end = seqlock.end;

// C ABI. Fixed-width arguments; nothing is allocated and nothing is kept
// between calls, so every result goes to caller memory. Functions return 0 or
// a `Failure` code, except `read` and `position_value`, which return a status.
// A region pointer must be 8-byte aligned. `Writer` and `Selection` are caller
// memory: a writer comes from `write_begin`; a selection from `select`, after
// the caller sized its arrays with `selection_size`.

/// Input to `rm_shared_section_initialize`.
pub const LayoutSpec = extern struct {
    sources: ?[*]const SourceIdentity,
    positions: ?[*]const PositionSpec,
    data_kind: u32,
    source_capacity: u32,
    position_capacity: u32,
    data_capacity: u32,
    source_count: u32,
    position_count: u32,
};

/// Returned by `rm_shared_section_read`; details are in the selection's held
/// entry for the source.
pub const ReadStatus = enum(u32) { unchanged = 0, has_value = 1, unavailable = 2, ended = 3, failed = 4 };

/// Returned by `rm_shared_section_position_value`, also in `ValueView.status`.
pub const PositionStatus = enum(u32) { has_value = 1, unavailable = 2, ended = 3, failed = 4, not_arrived = 5 };

pub const ValueView = extern struct {
    /// Byte offset of the value in the selection's data, and its byte length.
    offset: u64 = 0,
    byte_length: u64 = 0,
    /// Elements for a scalar, rows for a column or string column, bytes for a
    /// string table.
    length: u32 = 0,
    /// The source's reason when unavailable; the `Failure` code when failed.
    reason: u32 = 0,
    status: PositionStatus = .not_arrived,
};

fn failureCode(err: Error) u32 {
    return @intFromEnum(failureOf(err));
}
fn statusOf(result: Error!void) u32 {
    result catch |err| return failureCode(err);
    return 0;
}
fn regionOf(pointer: ?[*]const u8, length: u64) Error![]align(8) const u8 {
    const base = pointer orelse return if (length == 0) error.RegionTooSmall else error.InvalidArgument;
    if (@intFromPtr(base) % 8 != 0) return error.RegionMisaligned;
    const aligned: [*]align(8) const u8 = @alignCast(base);
    return aligned[0 .. std.math.cast(usize, length) orelse return error.InvalidArgument];
}
fn regionMutOf(pointer: ?[*]u8, length: u64) Error![]align(8) u8 {
    return @constCast(try regionOf(pointer, length));
}
fn tableOf(comptime T: type, pointer: ?[*]const T, count: u32) Error![]const T {
    if (pointer) |base| return base[0..count];
    return if (count == 0) &.{} else error.InvalidArgument;
}

/// Bytes a region with these capacities needs.
pub export fn rm_shared_section_required_size(source_capacity: u32, position_capacity: u32, data_capacity: u32) u64 {
    return requiredSize(source_capacity, position_capacity, data_capacity);
}

/// Lays out a region (`layout.initialize`).
pub export fn rm_shared_section_initialize(region: ?[*]u8, length: u64, spec: ?*const LayoutSpec) u32 {
    return statusOf(initializeFrom(region, length, spec));
}
fn initializeFrom(region: ?[*]u8, length: u64, spec_pointer: ?*const LayoutSpec) Error!void {
    const spec = spec_pointer orelse return error.InvalidArgument;
    const sources = try tableOf(SourceIdentity, spec.sources, spec.source_count);
    const positions = try tableOf(PositionSpec, spec.positions, spec.position_count);
    try initialize(try regionMutOf(region, length), spec.data_kind, spec.source_capacity, spec.position_capacity, spec.data_capacity, sources, positions);
}

/// Layout query: the checked header.
pub export fn rm_shared_section_describe(region: ?[*]const u8, length: u64, header: ?*Header) u32 {
    const out = header orelse return failureCode(error.InvalidArgument);
    out.* = describe(regionOf(region, length) catch |err| return failureCode(err)) catch |err| return failureCode(err);
    return 0;
}

/// Layout query: the checked identity of source `index`.
pub export fn rm_shared_section_source(region: ?[*]const u8, length: u64, index: u32, identity: ?*SourceIdentity) u32 {
    const out = identity orelse return failureCode(error.InvalidArgument);
    out.* = describeSource(regionOf(region, length) catch |err| return failureCode(err), index) catch |err| return failureCode(err);
    return 0;
}

/// Layout query: the checked creator description of position `index`.
pub export fn rm_shared_section_position(region: ?[*]const u8, length: u64, index: u32, spec: ?*PositionSpec) u32 {
    const out = spec orelse return failureCode(error.InvalidArgument);
    out.* = describePosition(regionOf(region, length) catch |err| return failureCode(err), index) catch |err| return failureCode(err);
    return 0;
}

/// Opens a round for `source` into caller memory `writer`.
pub export fn rm_shared_section_write_begin(region: ?[*]u8, length: u64, source: u32, writer: ?*Writer) u32 {
    const out = writer orelse return failureCode(error.InvalidArgument);
    out.* = Writer.begin(regionMutOf(region, length) catch |err| return failureCode(err), source) catch |err| return failureCode(err);
    return 0;
}

/// Stores `byte_length` bytes of whole little-endian elements of the
/// position's width as elements `first..` of an own position.
pub export fn rm_shared_section_write_elements(writer: ?*const Writer, position: u32, first: u32, values: ?[*]const u8, byte_length: u64) u32 {
    const open = writer orelse return failureCode(error.InvalidArgument);
    const count = std.math.cast(usize, byte_length) orelse return failureCode(error.InvalidArgument);
    const bytes: []const u8 = if (values) |base| base[0..count] else if (count == 0) &.{} else return failureCode(error.InvalidArgument);
    return statusOf(open.storeRaw(position, first, bytes));
}

/// Sets an own position's length and identity sequence (`Writer.setLength`).
pub export fn rm_shared_section_write_length(writer: ?*const Writer, position: u32, length: u32, identity_sequence: u64) u32 {
    const open = writer orelse return failureCode(error.InvalidArgument);
    return statusOf(open.setLength(position, length, identity_sequence));
}

/// Closes the round. `state` is a `SourceState` value; `reason` applies to
/// unavailable.
pub export fn rm_shared_section_write_end(writer: ?*Writer, state: u32, reason: u32, write_time_unix_ms: i64) u32 {
    const open = writer orelse return failureCode(error.InvalidArgument);
    const outcome: Outcome = switch (state) {
        @intFromEnum(SourceState.has_value) => .has_value,
        @intFromEnum(SourceState.unavailable) => .{ .unavailable = reason },
        @intFromEnum(SourceState.ended) => .ended,
        else => return failureCode(error.InvalidArgument),
    };
    return statusOf(open.end(outcome, write_time_unix_ms));
}

/// Layout query for a reader: what `select` needs for `sources` (strictly
/// ascending, `source_count` entries) over the region.
pub export fn rm_shared_section_selection_size(region: ?[*]const u8, length: u64, sources: ?[*]const u32, source_count: u32, size: ?*SelectionSize) u32 {
    const out = size orelse return failureCode(error.InvalidArgument);
    const list = tableOf(u32, sources, source_count) catch |err| return failureCode(err);
    out.* = selectionSize(regionOf(region, length) catch |err| return failureCode(err), list) catch |err| return failureCode(err);
    return 0;
}

/// Sets up `selection` (`Selection.init`). The caller fills its `held`,
/// `positions` and `data` pointers and lengths, sized with `selection_size`;
/// this fills the rest and sets `position_length` to the entries used.
pub export fn rm_shared_section_select(region: ?[*]const u8, length: u64, sources: ?[*]const u32, source_count: u32, selection: ?*Selection) u32 {
    const out = selection orelse return failureCode(error.InvalidArgument);
    return statusOf(selectInto(region, length, sources, source_count, out));
}
fn selectInto(region: ?[*]const u8, length: u64, sources: ?[*]const u32, source_count: u32, out: *Selection) Error!void {
    const list = try tableOf(u32, sources, source_count);
    const held: []SourceCopy = if (out.held) |base| base[0..out.held_length] else &.{};
    const positions: []SelectedPosition = if (out.positions) |base| base[0..out.position_length] else &.{};
    const data: []u8 = if (out.data) |base| base[0 .. std.math.cast(usize, out.data_length) orelse return error.InvalidArgument] else &.{};
    out.* = try Selection.init(try regionOf(region, length), list, held, positions, data);
}

/// Reads one selected source into `selection` (`seqlock.read`). Returns a
/// `ReadStatus`.
pub export fn rm_shared_section_read(region: ?[*]const u8, length: u64, selection: ?*Selection, source: u32, retry_limit: u32) u32 {
    const target = selection orelse return @intFromEnum(ReadStatus.failed);
    const bytes = regionOf(region, length) catch |err| {
        for (target.heldSlice()) |*held| {
            if (held.source == source) held.* = .{ .status = .failed, .reason = failureCode(err), .source = source };
        }
        return @intFromEnum(ReadStatus.failed);
    };
    const status: ReadStatus = switch (read(bytes, target, source, retry_limit)) {
        .unchanged => .unchanged,
        .value => .has_value,
        .unavailable => .unavailable,
        .ended => .ended,
        .failed => .failed,
    };
    return @intFromEnum(status);
}

/// The value of `position` (its index in the region's position table) in
/// `selection` (`seqlock.positionValue`). Returns a `PositionStatus` and fills
/// `value`; a value's offset indexes the selection's data.
pub export fn rm_shared_section_position_value(selection: ?*const Selection, position: u32, value: ?*ValueView) u32 {
    const out = value orelse return @intFromEnum(PositionStatus.failed);
    const source = selection orelse {
        out.* = .{ .status = .failed, .reason = failureCode(error.InvalidArgument) };
        return @intFromEnum(out.status);
    };
    out.* = switch (positionValue(source, position)) {
        .value => |found| .{
            .offset = found.offset,
            .byte_length = found.bytes.len,
            .length = found.length,
            .status = .has_value,
        },
        .not_arrived => .{ .status = .not_arrived },
        .unavailable => |reason| .{ .reason = reason, .status = .unavailable },
        .ended => .{ .status = .ended },
        .failed => |failure| .{ .reason = @intFromEnum(failure), .status = .failed },
    };
    return @intFromEnum(out.status);
}

/// "No data" before unmapping (`seqlock.end`).
pub export fn rm_shared_section_end(region: ?[*]u8, length: u64, write_time_unix_ms: i64) u32 {
    return statusOf(end(regionMutOf(region, length) catch |err| return failureCode(err), write_time_unix_ms));
}
