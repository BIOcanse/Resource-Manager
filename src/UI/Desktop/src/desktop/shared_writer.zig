//! Stateless W output leaf. H must establish T provenance, the selected writer,
//! the attested service/control-connection lifetime and the one writable W.
//! The caller borrows both mappings for this call; this module retains neither.
//! S supplies the C04 schema and encoded positions. No schema/opcode is invented
//! here while that integration contract is pending.
const std = @import("std");
const ss = @import("rm_shared_section");

/// Copied from service-owned T, never from W. Header/spec slices must remain
/// immutable for this call. `active` belongs to the corresponding RO T mapping.
/// Its u32 1/0 encoding is L's directory.zig (9a5b9656), pending public wiring.
pub const Description = struct {
    active: *const u32,
    header: ss.Header,
    positions: []const ss.PositionSpec,
    source: u32,
};

/// One complete own position, in little-endian whole elements. Every position
/// of the source must occur once; values are run-local encoding scratch.
pub const Value = struct {
    position: u32,
    bytes: []const u8,
    /// Only used for an identity owned by another source. Local identities use
    /// this round's even sequence; positions without an identity store zero.
    identity_sequence: u64,
};

const Targets = struct {
    positions: []const ss.PositionSpec,
    pub fn get(self: Targets, index: u32) ss.PositionSpec {
        return self.positions[index];
    }
};

fn isActive(description: Description) bool {
    return @atomicLoad(u32, description.active, .acquire) == 1;
}

fn checkedLayout(region: []align(8) u8, description: Description, values: []const Value) !ss.layout.Layout {
    const h = description.header;
    if (!isActive(description)) return error.Retired;
    if (h.magic != ss.magic or h.version != ss.version or
        h.source_count > h.source_capacity or h.position_count > h.position_capacity or
        h.position_count != description.positions.len or h.data_capacity % 8 != 0 or
        description.source >= h.source_count or
        ss.requiredSize(h.source_capacity, h.position_capacity, h.data_capacity) > region.len)
        return error.InvalidDescription;
    const view: ss.layout.Layout = .{
        .header = h,
        .position_table = @intCast(ss.layout.positionTableOffset(h.source_capacity)),
        .data = @intCast(ss.layout.dataOffset(h.source_capacity, h.position_capacity)),
    };
    var own_count: usize = 0;
    for (description.positions, 0..) |spec, index| {
        try ss.layout.checkSpec(@intCast(index), spec, h, Targets{ .positions = description.positions });
        // Trusted input is still structurally checked before the odd sequence:
        // malformed grants cannot overwrite a table or another position.
        for (description.positions[0..index]) |before| {
            if (spec.size == 0 or before.size == 0) continue;
            if (@as(u64, spec.offset) < @as(u64, before.offset) + before.size and
                @as(u64, before.offset) < @as(u64, spec.offset) + spec.size)
                return error.OverlappingPositions;
        }
        if (spec.source == description.source) own_count += 1;
    }
    if (values.len != own_count) return error.IncompleteOutput;
    for (values, 0..) |value, index| {
        if (value.position >= h.position_count) return error.PositionIndex;
        const spec = description.positions[value.position];
        if (spec.source != description.source) return error.NotOwner;
        for (values[0..index]) |before| if (before.position == value.position) return error.DuplicatePosition;
        if (value.bytes.len > spec.size or value.bytes.len % spec.element_width != 0 or
            (spec.kind == .scalar and value.bytes.len != spec.size)) return error.InvalidOutput;
        if (spec.identity == ss.none or description.positions[spec.identity].source == description.source) {
            if (value.identity_sequence != 0) return error.InvalidIdentitySequence;
        } else if (value.identity_sequence == 0 or value.identity_sequence & (ss.ended_bit | 1) != 0) {
            return error.InvalidIdentitySequence;
        }
        if (spec.identity != ss.none and description.positions[spec.identity].source == description.source) {
            const identity = for (values) |candidate| {
                if (candidate.position == spec.identity) break candidate;
            } else return error.IncompleteOutput;
            const identity_spec = description.positions[spec.identity];
            if (value.bytes.len / spec.element_width != identity.bytes.len / identity_spec.element_width)
                return error.RowsMismatch;
        }
    }
    return view;
}

/// Same fence-free ordering as SharedSection/seqlock.zig: the odd sequence is
/// before each element's release store, paired with reader acquire loads.
fn storeElements(comptime T: type, target: []u8, bytes: []const u8) void {
    const elements: []T = std.mem.bytesAsSlice(T, @as([]align(@alignOf(T)) u8, @alignCast(target)));
    for (elements, 0..) |*element, index| {
        const offset = index * @sizeOf(T);
        const value = if (offset < bytes.len) std.mem.readInt(T, bytes[offset..][0..@sizeOf(T)], .little) else 0;
        @atomicStore(T, element, value, .release);
    }
}

/// Writes only the source's complete output in the granted W. W header/specs
/// are never used to form an address, even if an old/malicious writer changed
/// them. No failure before begin changes W. After begin all addressing and
/// output sizes are fixed; no allocator, timer, lease, registry or current.
pub fn write(region: []align(8) u8, description: Description, values: []const Value, write_time_unix_ms: i64) !u64 {
    const view = try checkedLayout(region, description, values);
    const source = view.sourceMut(region, description.source);
    const previous = @atomicLoad(u64, &source.sequence, .monotonic);
    if (previous & ss.ended_bit != 0) return error.Ended;
    if (previous & 1 != 0) return error.WriteInProgress;
    if (previous >= ss.ended_bit - 2) return error.SequenceExhausted;
    if (!isActive(description)) return error.Retired;
    if (@cmpxchgStrong(u64, &source.sequence, previous, previous + 1, .monotonic, .monotonic) != null)
        return error.SourceChanged;
    const next = previous + 2;
    const data = view.dataAreaMut(region);
    for (values) |value| {
        const spec = description.positions[value.position];
        const target = data[spec.offset..][0..spec.size];
        switch (spec.element_width) {
            1 => storeElements(u8, target, value.bytes),
            2 => storeElements(u16, target, value.bytes),
            4 => storeElements(u32, target, value.bytes),
            8 => storeElements(u64, target, value.bytes),
            else => unreachable,
        }
        const entry = view.positionMut(region, value.position);
        const sequence = if (spec.identity == ss.none) 0 else if (description.positions[spec.identity].source == description.source) next else value.identity_sequence;
        @atomicStore(u32, &entry.length, if (spec.kind == .scalar) 0 else @intCast(value.bytes.len / spec.element_width), .release);
        @atomicStore(u64, &entry.identity_sequence, sequence, .release);
    }
    @atomicStore(ss.SourceState, &source.state, .has_value, .release);
    @atomicStore(u32, &source.reason, 0, .release);
    @atomicStore(i64, &source.write_time_unix_ms, write_time_unix_ms, .release);
    if (@cmpxchgStrong(u64, &source.sequence, previous + 1, next, .release, .monotonic) != null)
        return error.SourceChanged;
    // Service retirement cannot be undone by writing W; S also validates the
    // bound selection at commit. Completing an old W never revives its T.
    if (!isActive(description)) return error.Retired;
    return next;
}

/// `notice.committed()` sends no value and uses the already-bound control
/// connection. Its concrete transport belongs to S/H integration. An error
/// propagates to the original block owner, which closes that binding/map.
pub fn writeAndNotify(region: []align(8) u8, description: Description, values: []const Value, write_time_unix_ms: i64, notice: anytype) !u64 {
    const sequence = try write(region, description, values, write_time_unix_ms);
    try notice.committed();
    return sequence;
}

/// The original resident's last-written-content scratch, local to one granted
/// W lifetime. Integration replaces Workspace.written with this scratch; it
/// must not add a second cache. Reset before closing an old binding and before
/// accepting any new grant, including a service restart with equal sequences.
/// It never answers reads and does not own a mapping or selection.
pub fn ContentScratch(comptime Sample: type) type {
    return struct {
        previous: Sample = undefined,
        has_previous: bool = false,

        pub fn reset(self: *@This()) void {
            self.has_previous = false;
        }

        /// `output.write(sample)` encodes the fixed S schema and calls write
        /// (not writeAndNotify) in W;
        /// `output.committed()` sends only the bound control notification.
        /// sameContent is the existing resident comparison (time excluded).
        /// The caller propagates failure to the original close/retry boundary.
        pub fn publish(self: *@This(), sample: *const Sample, output: anytype) !bool {
            if (self.has_previous and sample.sameContent(&self.previous)) return false;
            errdefer self.reset();
            try output.write(sample);
            try output.committed();
            self.previous = sample.*;
            self.has_previous = true;
            return true;
        }
    };
}
