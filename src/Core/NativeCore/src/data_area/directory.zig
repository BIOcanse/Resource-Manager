//! Service-written T for one selected desktop source. W is never consulted to
//! construct these bounds or identities. This is a native leaf format pending
//! versioned integration, not a SharedSection v1 header extension or a grant.
//! All descriptions are immutable after publication; only active is revoked.
const std = @import("std");
const ss = @import("rm_shared_section");
const plan = @import("plan.zig");

pub const magic: u32 = 0x4441_4d52; // RMAD
pub const version: u32 = 1;
pub const Error = error{ InvalidArgument, RegionTooLarge };

pub const Header = extern struct {
    magic: u32,
    version: u32,
    active: u32,
    region_class: plan.RegionClass,
    trusted: plan.RegionReference,
    writer: plan.RegionReference,
    publisher: plan.SelectionIdentity,
    constraint: plan.ReadConstraint,
    writer_byte_length: u64,
    writer_header: ss.Header,
};

pub const View = struct {
    header: *const Header,
    sources: []const ss.SourceIdentity,
    positions: []const ss.PositionSpec,

    pub fn isActive(self: View) bool {
        return @atomicLoad(u32, &self.header.active, .acquire) == 1;
    }

    pub fn dataOffset(self: View) u64 {
        return ss.requiredSize(self.header.writer_header.source_capacity, self.header.writer_header.position_capacity, 0);
    }
};

fn contentSize(sources: usize, positions: usize) Error!usize {
    const source_bytes = std.math.mul(usize, sources, @sizeOf(ss.SourceIdentity)) catch return error.RegionTooLarge;
    const position_bytes = std.math.mul(usize, positions, @sizeOf(ss.PositionSpec)) catch return error.RegionTooLarge;
    return std.math.add(usize, std.math.add(usize, @sizeOf(Header), source_bytes) catch return error.RegionTooLarge, position_bytes) catch return error.RegionTooLarge;
}

pub fn requiredSize(layout: plan.RegionLayout) Error!u64 {
    const size = try contentSize(layout.sources.len, layout.positions.len);
    const padded = std.math.add(u64, size, plan.region_alignment - 1) catch return error.RegionTooLarge;
    return padded & ~(plan.region_alignment - 1);
}

pub fn initialize(bytes: []align(8) u8, trusted: plan.RegionReference, writer: plan.RegionReference, publisher: plan.SelectionIdentity, class: plan.RegionClass, data_kind: u32, layout: plan.RegionLayout) Error!void {
    if (bytes.len < try requiredSize(layout) or !publisher.valid() or class.index() == null or trusted.id == writer.id) return error.InvalidArgument;
    @memset(bytes, 0);
    const header: *Header = @ptrCast(bytes.ptr);
    header.* = .{
        .magic = magic,
        .version = version,
        .active = 0,
        .region_class = class,
        .trusted = trusted,
        .writer = writer,
        .publisher = publisher,
        .constraint = .desktop(publisher.session_id),
        .writer_byte_length = layout.byte_length,
        .writer_header = .{ .magic = ss.magic, .version = ss.version, .data_kind = data_kind, .source_capacity = @intCast(layout.sources.len), .source_count = @intCast(layout.sources.len), .position_capacity = @intCast(layout.positions.len), .position_count = @intCast(layout.positions.len), .data_capacity = layout.data_capacity },
    };
    const sources: [*]ss.SourceIdentity = @ptrCast(@alignCast(bytes[@sizeOf(Header)..].ptr));
    var s: usize = 0;
    while (s < layout.sources.len) : (s += 1) {
        const identity = layout.sources[s];
        if (identity.owner_name_length > ss.owner_name_capacity) return error.InvalidArgument;
        // Copy fields, leaving padding and the unused name tail zero in T.
        sources[s].kind = identity.kind;
        sources[s].process_id = identity.process_id;
        sources[s].process_start_time = identity.process_start_time;
        sources[s].owner_name_length = identity.owner_name_length;
        @memcpy(sources[s].owner_name[0..identity.owner_name_length], identity.owner_name[0..identity.owner_name_length]);
    }
    const offset = @sizeOf(Header) + layout.sources.len * @sizeOf(ss.SourceIdentity);
    const positions: [*]ss.PositionSpec = @ptrCast(@alignCast(bytes[offset..].ptr));
    @memcpy(positions[0..layout.positions.len], layout.positions);
    @atomicStore(u32, &header.active, 1, .release);
}

/// Only call on an Owner-created T (never on externally writable bytes).
pub fn invalidate(bytes: []align(8) u8) void {
    const header: *Header = @ptrCast(bytes.ptr);
    @atomicStore(u32, &header.active, 0, .release);
}

/// The provenance of T must be established by H. Parsing an arbitrary buffer
/// does not make its identities trusted. This helper owns no registry/cache.
pub fn describe(bytes: []align(8) const u8) Error!View {
    if (bytes.len < @sizeOf(Header)) return error.InvalidArgument;
    const header: *const Header = @ptrCast(bytes.ptr);
    if (header.magic != magic or header.version != version or !header.constraint.valid() or header.region_class.index() == null) return error.InvalidArgument;
    const sources = header.writer_header.source_count;
    const positions = header.writer_header.position_count;
    if (try contentSize(sources, positions) > bytes.len) return error.InvalidArgument;
    const source_base: [*]const ss.SourceIdentity = @ptrCast(@alignCast(bytes[@sizeOf(Header)..].ptr));
    const offset = @sizeOf(Header) + @as(usize, sources) * @sizeOf(ss.SourceIdentity);
    const position_base: [*]const ss.PositionSpec = @ptrCast(@alignCast(bytes[offset..].ptr));
    return .{ .header = header, .sources = source_base[0..sources], .positions = position_base[0..positions] };
}
