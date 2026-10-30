//! Region layout: header, source table, position table, then the data area.
//!
//! Every field sits at a fixed little-endian offset, so the C# and TypeScript
//! readers address it directly; the comptime checks at the end pin the offsets.
//! The creator lays a region out once with `initialize`. Afterwards each source
//! writes only its own entry's dynamic fields and its own positions (seqlock.zig).
//!
//! Alignment rule: every field is naturally aligned within the region, so a web
//! page reading the region as a plain ArrayBuffer can use typed arrays directly:
//! 64-bit fields on 8-byte offsets, 32-bit fields on 4-byte offsets. In
//! particular each source's sequence is exactly 64 bits on an 8-byte offset, so
//! a page reads it with `Atomics.load` on a `BigInt64Array`, reads the data, and
//! reads it again. This holds because the region starts 8-byte aligned (checked
//! by every caller-facing entry), the header and both table entries are
//! multiples of 8 bytes, the data area and every position start on a multiple
//! of 8, and element widths divide 8.
//!
//! Region contents are untrusted. A cross-process writer holds a handle to the
//! whole region, not only to its positions, so every function here loads a field
//! once, checks the loaded value against the region length and uses only that
//! value. Nothing is allocated and no state is kept between calls.
const std = @import("std");
const builtin = @import("builtin");

comptime {
    if (builtin.cpu.arch.endian() != .little) @compileError("the shared section layout is little-endian");
}

/// "RMSS" in memory order.
pub const magic: u32 = 0x5353_4D52;
pub const version: u32 = 1;
/// An absent position reference (`identity`, `strings`).
pub const none: u32 = std.math.maxInt(u32);
pub const owner_name_capacity = 64;
/// Every source starts unavailable with this reason: it has not written yet.
/// Sources use nonzero codes for their own reasons.
pub const not_written_reason: u32 = 0;
/// Bit 63 of a source sequence: the source has ended. Ended is terminal; a
/// writer cannot begin again, and a round in progress learns of it at its end.
pub const ended_bit: u64 = 1 << 63;

pub const Header = extern struct {
    magic: u32,
    version: u32,
    /// Chosen by the creator; tells a reader which decoder applies.
    data_kind: u32,
    source_capacity: u32,
    source_count: u32,
    position_capacity: u32,
    position_count: u32,
    /// Bytes in the data area, a multiple of 8.
    data_capacity: u32,
};

/// Non-exhaustive: a loaded value is checked, never trusted.
pub const SourceState = enum(u32) { has_value = 1, unavailable = 2, ended = 3, _ };

/// Who a source is. The creator writes it once; a reader compares it with the
/// owner it expects (for example the selected desktop observer).
pub const SourceIdentity = extern struct {
    /// Chosen by the creator for its data kind.
    kind: u32 = 0,
    /// Cross-process sources only; zero for an in-process owner.
    process_id: u32 = 0,
    /// The creation time the creator verified for `process_id` (a FILETIME on
    /// Windows); zero for an in-process owner. Not interpreted here.
    process_start_time: u64 = 0,
    owner_name_length: u32 = 0,
    owner_name: [owner_name_capacity]u8 = [_]u8{0} ** owner_name_capacity,

    pub fn init(name: []const u8, kind: u32, process_id: u32, process_start_time: u64) Error!SourceIdentity {
        if (name.len > owner_name_capacity) return error.OwnerName;
        var identity = SourceIdentity{ .kind = kind, .process_id = process_id, .process_start_time = process_start_time, .owner_name_length = @intCast(name.len) };
        @memcpy(identity.owner_name[0..name.len], name);
        return identity;
    }
    /// Only for an identity returned by `describeSource` or built by `init`.
    pub fn owner(self: *const SourceIdentity) []const u8 {
        return self.owner_name[0..self.owner_name_length];
    }
};

pub const SourceEntry = extern struct {
    // Written by the source under its sequence (seqlock.zig).
    sequence: u64,
    /// UTC milliseconds since the Unix epoch, passed by the writer.
    write_time_unix_ms: i64,
    state: SourceState,
    /// The source's own reason code while unavailable.
    reason: u32,
    // Written once by the creator.
    identity: SourceIdentity,
};

pub const PositionKind = enum(u32) {
    /// One fixed value of `size` bytes made of `size / element_width` elements.
    scalar = 1,
    /// Fixed numeric column: up to `size / element_width` rows.
    column = 2,
    /// One u64 per row: byte offset (low 32 bits) and byte length (high 32
    /// bits) in the `strings` table position.
    string_column = 3,
    /// String bytes for this source's string columns; element width 1.
    string_table = 4,
    _,
};

/// The creator's description of one position.
pub const PositionSpec = extern struct {
    /// Index of the owning source entry. Exactly one source writes a position.
    source: u32,
    kind: PositionKind,
    /// Byte offset in the data area, a multiple of 8.
    offset: u32,
    /// Reserved bytes, a multiple of `element_width`.
    size: u32,
    /// 1, 2, 4 or 8; 8 for a string column, 1 for a string table.
    element_width: u32,
    /// For a column or string column: the identity position that says which
    /// rows exist and in what order, or `none` when the column has its own rows.
    identity: u32 = none,
    /// For a string column: its string table position, owned by the same source.
    strings: u32 = none,

    pub fn scalar(source: u32, offset: u32, size: u32, element_width: u32) PositionSpec {
        return .{ .source = source, .kind = .scalar, .offset = offset, .size = size, .element_width = element_width };
    }
    pub fn column(source: u32, offset: u32, rows: u32, element_width: u32, identity: u32) PositionSpec {
        return .{ .source = source, .kind = .column, .offset = offset, .size = std.math.mul(u32, rows, element_width) catch none, .element_width = element_width, .identity = identity };
    }
    pub fn stringColumn(source: u32, offset: u32, rows: u32, identity: u32, strings: u32) PositionSpec {
        return .{ .source = source, .kind = .string_column, .offset = offset, .size = std.math.mul(u32, rows, 8) catch none, .element_width = 8, .identity = identity, .strings = strings };
    }
    pub fn stringTable(source: u32, offset: u32, bytes: u32) PositionSpec {
        return .{ .source = source, .kind = .string_table, .offset = offset, .size = bytes, .element_width = 1 };
    }
};

pub const PositionEntry = extern struct {
    // Written once by the creator.
    spec: PositionSpec,
    // Written by the owning source under its sequence.
    /// Rows for a column or string column, bytes for a string table. A scalar
    /// is always whole; a reader reports its element count instead.
    length: u32 = 0,
    /// The identity source's sequence this column was written against.
    identity_sequence: u64 = 0,
};

/// Why a call was refused or a source read failed. Stable for the C ABI, where
/// 0 means success. Non-exhaustive so a stored code is always representable.
pub const Failure = enum(u32) {
    /// Shorter than the header, or than the header's capacities require.
    region_too_small = 1,
    /// The region does not start on an 8-byte boundary.
    region_misaligned = 2,
    bad_magic = 3,
    bad_version = 4,
    /// A source or position count exceeds its capacity.
    count_over_capacity = 5,
    /// The data capacity is not a multiple of 8.
    data_capacity = 6,
    /// A source index is not below the source count.
    source_index = 7,
    position_index = 8,
    /// A position names a source outside the source table.
    position_source = 9,
    position_kind = 10,
    position_width = 11,
    /// A position offset or size is misaligned or outside the data area.
    position_range = 12,
    position_identity = 13,
    position_strings = 14,
    /// Two positions share bytes (checked when laying out).
    position_overlap = 15,
    owner_name = 16,
    /// A source entry holds a state other than has value, unavailable or ended.
    source_state = 17,
    length_over_capacity = 18,
    /// A string row points outside its string table's written bytes.
    string_range = 19,
    /// A column written against the reader's identity rows has another row count.
    rows_mismatch = 20,
    /// The sequence was odd or changed during every attempt.
    retry_limit = 21,
    /// The caller's selection memory is smaller than the selection needs.
    copy_too_small = 22,
    /// The position belongs to another source.
    not_owner = 23,
    /// The position's kind does not fit the call.
    wrong_kind = 24,
    element_width = 25,
    element_range = 26,
    /// The source's sequence is already odd: a round was begun and not ended.
    write_in_progress = 27,
    /// The writer no longer holds the source's current round.
    not_writing = 28,
    /// The source or the region has ended.
    ended = 29,
    invalid_argument = 30,
    /// The source or position is not in the reader's selection.
    not_selected = 31,
    /// A selected row column's identity position belongs to an unselected source.
    identity_not_selected = 32,
    /// The region's header or a read source's positions differ from the layout
    /// the selection was set up with.
    layout_changed = 33,
    /// Selected source indexes are not strictly ascending.
    selection_order = 34,
    /// A selected read names no source.
    selection_empty = 35,
    /// A selected read's sources, with the identity owners it adds, exceed
    /// `selected.selected_source_limit`.
    selection_too_large = 36,
    _,
};

pub const Error = error{
    RegionTooSmall,
    RegionMisaligned,
    BadMagic,
    BadVersion,
    CountOverCapacity,
    DataCapacity,
    SourceIndex,
    PositionIndex,
    PositionSource,
    PositionKind,
    PositionWidth,
    PositionRange,
    PositionIdentity,
    PositionStrings,
    PositionOverlap,
    OwnerName,
    LengthOverCapacity,
    NotOwner,
    WrongKind,
    ElementWidth,
    ElementRange,
    WriteInProgress,
    NotWriting,
    Ended,
    InvalidArgument,
    CopyTooSmall,
    IdentityNotSelected,
    SelectionOrder,
    SelectionEmpty,
    SelectionTooLarge,
    LayoutChanged,
};

pub fn failureOf(err: Error) Failure {
    return switch (err) {
        error.RegionTooSmall => .region_too_small,
        error.RegionMisaligned => .region_misaligned,
        error.BadMagic => .bad_magic,
        error.BadVersion => .bad_version,
        error.CountOverCapacity => .count_over_capacity,
        error.DataCapacity => .data_capacity,
        error.SourceIndex => .source_index,
        error.PositionIndex => .position_index,
        error.PositionSource => .position_source,
        error.PositionKind => .position_kind,
        error.PositionWidth => .position_width,
        error.PositionRange => .position_range,
        error.PositionIdentity => .position_identity,
        error.PositionStrings => .position_strings,
        error.PositionOverlap => .position_overlap,
        error.OwnerName => .owner_name,
        error.LengthOverCapacity => .length_over_capacity,
        error.NotOwner => .not_owner,
        error.WrongKind => .wrong_kind,
        error.ElementWidth => .element_width,
        error.ElementRange => .element_range,
        error.WriteInProgress => .write_in_progress,
        error.NotWriting => .not_writing,
        error.Ended => .ended,
        error.InvalidArgument => .invalid_argument,
        error.CopyTooSmall => .copy_too_small,
        error.IdentityNotSelected => .identity_not_selected,
        error.SelectionOrder => .selection_order,
        error.SelectionEmpty => .selection_empty,
        error.SelectionTooLarge => .selection_too_large,
        error.LayoutChanged => .layout_changed,
    };
}

pub const header_size: u64 = @sizeOf(Header);
pub const source_entry_size: u64 = @sizeOf(SourceEntry);
pub const position_entry_size: u64 = @sizeOf(PositionEntry);

/// The source table follows the header; the position table follows the source
/// table's full capacity; the data area follows the position table's capacity.
pub fn positionTableOffset(source_capacity: u32) u64 {
    return header_size + source_capacity * source_entry_size;
}
pub fn dataOffset(source_capacity: u32, position_capacity: u32) u64 {
    return positionTableOffset(source_capacity) + position_capacity * position_entry_size;
}
pub fn requiredSize(source_capacity: u32, position_capacity: u32, data_capacity: u32) u64 {
    return dataOffset(source_capacity, position_capacity) + data_capacity;
}

/// A header that passed its checks, loaded once. Every later offset derives from
/// these values, so a later change to the region's header cannot move a checked
/// access out of the region.
pub const Layout = struct {
    header: Header,
    position_table: usize,
    data: usize,

    pub fn load(region: []align(8) const u8) Error!Layout {
        if (region.len < header_size) return error.RegionTooSmall;
        const h: *const Header = @ptrCast(region[0..@sizeOf(Header)]);
        // Acquire pairs with the release store of the magic in `initialize`: a
        // reader that sees the magic also sees the tables written before it.
        if (@atomicLoad(u32, &h.magic, .acquire) != magic) return error.BadMagic;
        // The remaining header and table fields are loaded monotonic: the
        // creator wrote them before the magic, and a field rewritten later is
        // caught by the checks on the loaded value.
        const header = Header{
            .magic = magic,
            .version = @atomicLoad(u32, &h.version, .monotonic),
            .data_kind = @atomicLoad(u32, &h.data_kind, .monotonic),
            .source_capacity = @atomicLoad(u32, &h.source_capacity, .monotonic),
            .source_count = @atomicLoad(u32, &h.source_count, .monotonic),
            .position_capacity = @atomicLoad(u32, &h.position_capacity, .monotonic),
            .position_count = @atomicLoad(u32, &h.position_count, .monotonic),
            .data_capacity = @atomicLoad(u32, &h.data_capacity, .monotonic),
        };
        if (header.version != version) return error.BadVersion;
        if (header.source_count > header.source_capacity or header.position_count > header.position_capacity) return error.CountOverCapacity;
        if (header.data_capacity % 8 != 0) return error.DataCapacity;
        if (requiredSize(header.source_capacity, header.position_capacity, header.data_capacity) > region.len) return error.RegionTooSmall;
        return .{
            .header = header,
            .position_table = @intCast(positionTableOffset(header.source_capacity)),
            .data = @intCast(dataOffset(header.source_capacity, header.position_capacity)),
        };
    }

    /// `index` must be below `header.source_count`.
    pub fn source(self: Layout, region: []align(8) const u8, index: u32) *const SourceEntry {
        std.debug.assert(index < self.header.source_count);
        return at(SourceEntry, region, @intCast(header_size + index * source_entry_size));
    }
    pub fn sourceMut(self: Layout, region: []align(8) u8, index: u32) *SourceEntry {
        return @constCast(self.source(region, index));
    }
    /// `index` must be below `header.position_count`.
    pub fn position(self: Layout, region: []align(8) const u8, index: u32) *const PositionEntry {
        std.debug.assert(index < self.header.position_count);
        return at(PositionEntry, region, self.position_table + @as(usize, index) * @sizeOf(PositionEntry));
    }
    pub fn positionMut(self: Layout, region: []align(8) u8, index: u32) *PositionEntry {
        return @constCast(self.position(region, index));
    }
    pub fn dataArea(self: Layout, region: []align(8) const u8) []align(8) const u8 {
        return @alignCast(region[self.data..][0..self.header.data_capacity]);
    }
    pub fn dataAreaMut(self: Layout, region: []align(8) u8) []align(8) u8 {
        return @constCast(self.dataArea(region));
    }

    /// Loads position `index` once and checks it; `index` must be in the table.
    pub fn checkedSpec(self: Layout, region: []align(8) const u8, index: u32) Error!PositionSpec {
        const spec = loadSpec(self.position(region, index));
        try self.check(region, index, spec);
        return spec;
    }

    /// Checks `spec`, loaded from position `index`, against this header and
    /// the positions it references; `index` must be in the table.
    pub fn check(self: Layout, region: []align(8) const u8, index: u32, spec: PositionSpec) Error!void {
        try checkSpec(index, spec, self.header, RegionTargets{ .layout = self, .region = region });
    }
};

fn at(comptime T: type, region: []align(8) const u8, offset: usize) *const T {
    return @ptrCast(@alignCast(region[offset..][0..@sizeOf(T)]));
}

/// Loads a position's creator fields once, unchecked.
pub fn loadSpec(entry: *const PositionEntry) PositionSpec {
    return .{
        .source = @atomicLoad(u32, &entry.spec.source, .monotonic),
        .kind = @atomicLoad(PositionKind, &entry.spec.kind, .monotonic),
        .offset = @atomicLoad(u32, &entry.spec.offset, .monotonic),
        .size = @atomicLoad(u32, &entry.spec.size, .monotonic),
        .element_width = @atomicLoad(u32, &entry.spec.element_width, .monotonic),
        .identity = @atomicLoad(u32, &entry.spec.identity, .monotonic),
        .strings = @atomicLoad(u32, &entry.spec.strings, .monotonic),
    };
}

const RegionTargets = struct {
    layout: Layout,
    region: []align(8) const u8,
    fn get(self: RegionTargets, index: u32) PositionSpec {
        return loadSpec(self.layout.position(self.region, index));
    }
};
const SliceTargets = struct {
    positions: []const PositionSpec,
    fn get(self: SliceTargets, index: u32) PositionSpec {
        return self.positions[index];
    }
};

fn hasRows(kind: PositionKind) bool {
    return kind == .column or kind == .string_column;
}

/// Checks one position against the header and the positions it references.
/// `targets.get(i)` returns position `i`'s spec for any `i` below the count.
/// A referenced spec is checked only for what the reference relies on, so
/// checking never walks a chain of references.
pub fn checkSpec(index: u32, spec: PositionSpec, header: Header, targets: anytype) Error!void {
    if (spec.source >= header.source_count) return error.PositionSource;
    const width = spec.element_width;
    switch (spec.kind) {
        .scalar, .column => if (width != 1 and width != 2 and width != 4 and width != 8) return error.PositionWidth,
        .string_column => if (width != 8) return error.PositionWidth,
        .string_table => if (width != 1) return error.PositionWidth,
        _ => return error.PositionKind,
    }
    // An offset that is a multiple of 8 keeps every element naturally aligned.
    if (spec.offset % 8 != 0 or spec.size % width != 0 or @as(u64, spec.offset) + spec.size > header.data_capacity) return error.PositionRange;
    if (spec.identity != none) {
        if (!hasRows(spec.kind) or spec.identity >= header.position_count or spec.identity == index) return error.PositionIdentity;
        // The identity position defines rows itself; identities do not chain.
        const rows = targets.get(spec.identity);
        if (!hasRows(rows.kind) or rows.identity != none) return error.PositionIdentity;
    }
    if (spec.kind == .string_column) {
        if (spec.strings >= header.position_count) return error.PositionStrings;
        // The table belongs to the same source, so its writer never shares it.
        const table = targets.get(spec.strings);
        if (table.kind != .string_table or table.source != spec.source) return error.PositionStrings;
    } else if (spec.strings != none) return error.PositionStrings;
}

/// Lays out `region`: header, every source unavailable with
/// `not_written_reason` at sequence 0, every position at length 0, a zeroed
/// data area. Fails before writing anything if the layout is invalid. The magic
/// is stored last with release, so a reader never sees a partial layout.
/// Nobody may read or write the region while it is being laid out.
pub fn initialize(
    region: []align(8) u8,
    data_kind: u32,
    source_capacity: u32,
    position_capacity: u32,
    data_capacity: u32,
    sources: []const SourceIdentity,
    positions: []const PositionSpec,
) Error!void {
    if (sources.len > source_capacity or positions.len > position_capacity) return error.CountOverCapacity;
    if (data_capacity % 8 != 0) return error.DataCapacity;
    const size = requiredSize(source_capacity, position_capacity, data_capacity);
    if (size > region.len) return error.RegionTooSmall;
    const header = Header{
        .magic = 0,
        .version = version,
        .data_kind = data_kind,
        .source_capacity = source_capacity,
        .source_count = @intCast(sources.len),
        .position_capacity = position_capacity,
        .position_count = @intCast(positions.len),
        .data_capacity = data_capacity,
    };
    for (sources) |identity| if (identity.owner_name_length > owner_name_capacity) return error.OwnerName;
    for (positions, 0..) |spec, index| try checkSpec(@intCast(index), spec, header, SliceTargets{ .positions = positions });
    // Two sources never share bytes, so writers never exclude each other.
    for (positions, 0..) |a, index| {
        for (positions[index + 1 ..]) |b| {
            if (a.size == 0 or b.size == 0) continue;
            if (@as(u64, a.offset) < @as(u64, b.offset) + b.size and @as(u64, b.offset) < @as(u64, a.offset) + a.size) return error.PositionOverlap;
        }
    }

    @memset(region[0..@intCast(size)], 0);
    const h: *Header = @ptrCast(region[0..@sizeOf(Header)]);
    h.* = header;
    const layout = Layout{ .header = header, .position_table = @intCast(positionTableOffset(source_capacity)), .data = @intCast(dataOffset(source_capacity, position_capacity)) };
    for (sources, 0..) |identity, index| {
        layout.sourceMut(region, @intCast(index)).* = .{ .sequence = 0, .write_time_unix_ms = 0, .state = .unavailable, .reason = not_written_reason, .identity = identity };
    }
    for (positions, 0..) |spec, index| layout.positionMut(region, @intCast(index)).* = .{ .spec = spec };
    @atomicStore(u32, &h.magic, magic, .release);
}

pub fn describe(region: []align(8) const u8) Error!Header {
    return (try Layout.load(region)).header;
}

/// The identity of source `index`, loaded once and checked.
pub fn describeSource(region: []align(8) const u8, index: u32) Error!SourceIdentity {
    const layout = try Layout.load(region);
    if (index >= layout.header.source_count) return error.SourceIndex;
    const entry = &layout.source(region, index).identity;
    var identity = SourceIdentity{
        .kind = @atomicLoad(u32, &entry.kind, .monotonic),
        .process_id = @atomicLoad(u32, &entry.process_id, .monotonic),
        .process_start_time = @atomicLoad(u64, &entry.process_start_time, .monotonic),
        .owner_name_length = @atomicLoad(u32, &entry.owner_name_length, .monotonic),
    };
    if (identity.owner_name_length > owner_name_capacity) return error.OwnerName;
    for (&identity.owner_name, &entry.owner_name) |*to, *from| to.* = @atomicLoad(u8, from, .monotonic);
    @memset(identity.owner_name[identity.owner_name_length..], 0);
    return identity;
}

/// The creator description of position `index`, loaded once and checked.
pub fn describePosition(region: []align(8) const u8, index: u32) Error!PositionSpec {
    const layout = try Layout.load(region);
    if (index >= layout.header.position_count) return error.PositionIndex;
    return layout.checkedSpec(region, index);
}

comptime {
    const assert = std.debug.assert;
    // The alignment rule: table strides and starts stay multiples of 8, and the
    // 64-bit fields sit on 8-byte offsets within their entries.
    assert(header_size % 8 == 0 and source_entry_size % 8 == 0 and position_entry_size % 8 == 0);
    assert(@sizeOf(@FieldType(SourceEntry, "sequence")) == 8 and @offsetOf(SourceEntry, "sequence") % 8 == 0);
    assert(@offsetOf(SourceEntry, "write_time_unix_ms") % 8 == 0);
    assert((@offsetOf(SourceEntry, "identity") + @offsetOf(SourceIdentity, "process_start_time")) % 8 == 0);
    assert(@offsetOf(PositionEntry, "identity_sequence") % 8 == 0);
    assert(@sizeOf(Header) == 32);
    assert(@sizeOf(SourceIdentity) == 88 and @offsetOf(SourceIdentity, "owner_name_length") == 16 and @offsetOf(SourceIdentity, "owner_name") == 20);
    assert(@sizeOf(SourceEntry) == 112 and @offsetOf(SourceEntry, "state") == 16 and @offsetOf(SourceEntry, "identity") == 24);
    assert(@sizeOf(PositionSpec) == 28);
    assert(@sizeOf(PositionEntry) == 40 and @offsetOf(PositionEntry, "length") == 28 and @offsetOf(PositionEntry, "identity_sequence") == 32);
}
