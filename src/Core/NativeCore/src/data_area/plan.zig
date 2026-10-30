//! The region owner's layout: from every writer's position declaration to the
//! two regions' SharedSection layouts and each declaration's global indexes.
//! Pure computation; no region is created here (owner.zig does that).
//!
//! Declarations are taken in order. Within its region, a declaration's sources
//! and positions get consecutive global indexes, so a placement is a base and a
//! count. Each position starts at the next multiple of 8 in the data area and
//! reserves `capacity * element_width` bytes. A region's byte length is the
//! SharedSection size rounded up to 64 KiB (backend_data_area §3).
const std = @import("std");
const ss = @import("rm_shared_section");

pub const none = ss.none;
pub const region_count = 2;
/// backend_data_area §3: each region is 64 KiB aligned, so an administrator
/// reader can map both regions as one contiguous block.
pub const region_alignment: u64 = 64 * 1024;

/// privilege_access §4: two levels, the normal region and the administrator
/// region. Non-exhaustive so a value from the C ABI is checked, never trusted.
pub const RegionClass = enum(u32) {
    normal = 0,
    administrator = 1,
    _,

    pub fn index(self: RegionClass) ?usize {
        return switch (self) {
            .normal => 0,
            .administrator => 1,
            _ => null,
        };
    }
    pub fn fromIndex(i: usize) RegionClass {
        return if (i == 0) .normal else .administrator;
    }
};

/// The SharedSection header's `data_kind` of each region: it names the region
/// class (backend_data_area §3 "这是普通区还是管理员区"). The format number and
/// version are the package's own magic and version.
pub const data_kind_normal: u32 = 0x4441_0001;
pub const data_kind_administrator: u32 = 0x4441_0002;

pub fn dataKind(class: RegionClass) u32 {
    return if (class == .administrator) data_kind_administrator else data_kind_normal;
}

/// One position a writer needs. Indexes are local to its declaration: `source`
/// into the declaration's sources, `identity` and `strings` into its positions.
pub const PositionDeclaration = extern struct {
    source: u32,
    kind: ss.PositionKind,
    /// 1, 2, 4 or 8; 8 for a string column, 1 for a string table.
    element_width: u32,
    /// Elements of a scalar, rows of a column or string column, bytes of a
    /// string table.
    capacity: u32,
    identity: u32 = none,
    strings: u32 = none,

    pub fn byteSize(self: PositionDeclaration) ?u32 {
        return std.math.mul(u32, self.capacity, self.element_width) catch null;
    }
};

/// One writer's position declaration (backend_data_area §12 step 1): which
/// sources it is, which positions it needs, and which region they go in.
pub const Declaration = extern struct {
    region: RegionClass,
    source_count: u32,
    position_count: u32,
    reserved: u32 = 0,
    sources: ?[*]const ss.SourceIdentity,
    positions: ?[*]const PositionDeclaration,

    pub fn init(region: RegionClass, sources: []const ss.SourceIdentity, positions: []const PositionDeclaration) Declaration {
        return .{
            .region = region,
            .source_count = @intCast(sources.len),
            .position_count = @intCast(positions.len),
            .sources = sources.ptr,
            .positions = positions.ptr,
        };
    }
    pub fn sourceSlice(self: *const Declaration) ?[]const ss.SourceIdentity {
        if (self.source_count == 0) return &.{};
        const base = self.sources orelse return null;
        return base[0..self.source_count];
    }
    pub fn positionSlice(self: *const Declaration) ?[]const PositionDeclaration {
        if (self.position_count == 0) return &.{};
        const base = self.positions orelse return null;
        return base[0..self.position_count];
    }
};

/// Where one declaration landed: its global source and position indexes are
/// `base .. base + count` in `region`.
pub const Placement = extern struct {
    region: RegionClass,
    source_base: u32,
    source_count: u32,
    position_base: u32,
    position_count: u32,
    reserved: u32 = 0,
};

/// Why `apply` refused a plan. `none` marks a field that does not apply.
pub const ApplyFailure = extern struct {
    declaration: u32 = none,
    /// Local position index in `declaration`.
    position: u32 = none,
    /// The package's reason (`rm_shared_section` Failure), 0 when not from it.
    shared_section_failure: u32 = 0,
    /// GetLastError of a failed section creation or mapping.
    win32_error: u32 = 0,
    region: u32 = none,
    reserved: u32 = 0,
};

pub const Error = error{
    InvalidArgument,
    OutOfMemory,
    DeclarationInvalid,
    RegionTooLarge,
};

/// One region's computed layout, ready for `ss.initialize`. Global indexes.
pub const RegionLayout = struct {
    sources: []ss.SourceIdentity = &.{},
    positions: []ss.PositionSpec = &.{},
    data_capacity: u32 = 0,
    /// The section's size: the SharedSection size rounded up to 64 KiB.
    byte_length: u64 = 0,

    /// A region without sources is not created.
    pub fn isEmpty(self: RegionLayout) bool {
        return self.sources.len == 0;
    }

    pub fn eql(a: RegionLayout, b: RegionLayout) bool {
        if (a.data_capacity != b.data_capacity or a.byte_length != b.byte_length) return false;
        if (a.sources.len != b.sources.len or a.positions.len != b.positions.len) return false;
        for (a.sources, b.sources) |x, y| if (!sameIdentity(x, y)) return false;
        for (a.positions, b.positions) |x, y| if (!std.meta.eql(x, y)) return false;
        return true;
    }

    pub fn deinit(self: *RegionLayout, allocator: std.mem.Allocator) void {
        if (self.sources.len != 0) allocator.free(self.sources);
        if (self.positions.len != 0) allocator.free(self.positions);
        self.* = .{};
    }
};

fn sameIdentity(a: ss.SourceIdentity, b: ss.SourceIdentity) bool {
    return a.kind == b.kind and a.process_id == b.process_id and a.process_start_time == b.process_start_time and
        a.owner_name_length == b.owner_name_length and a.owner_name_length <= ss.owner_name_capacity and
        std.mem.eql(u8, a.owner_name[0..a.owner_name_length], b.owner_name[0..b.owner_name_length]);
}

pub const Plan = struct {
    regions: [region_count]RegionLayout = .{ .{}, .{} },
    /// One per declaration, in declaration order.
    placements: []Placement = &.{},

    pub fn deinit(self: *Plan, allocator: std.mem.Allocator) void {
        for (&self.regions) |*region| region.deinit(allocator);
        if (self.placements.len != 0) allocator.free(self.placements);
        self.* = .{};
    }
};

const Totals = struct { sources: u32 = 0, positions: u32 = 0, data: u64 = 0 };

/// Validates every declaration and computes both regions' layouts. On failure
/// nothing is allocated and `failure` names the declaration and position.
pub fn compute(allocator: std.mem.Allocator, declarations: []const Declaration, failure: *ApplyFailure) Error!Plan {
    failure.* = .{};
    if (declarations.len > std.math.maxInt(u32)) return error.InvalidArgument;

    // Pass 1: local checks and per-region totals.
    var totals = [_]Totals{ .{}, .{} };
    for (declarations, 0..) |*declaration, d| {
        const index: u32 = @intCast(d);
        const region = declaration.region.index() orelse return refuse(failure, index, none, 0);
        failure.region = @intCast(region);
        const sources = declaration.sourceSlice() orelse return refuse(failure, index, none, 0);
        const positions = declaration.positionSlice() orelse return refuse(failure, index, none, 0);
        // Positions belong to sources; a declaration names at least one.
        if (sources.len == 0) return refuse(failure, index, none, @intFromEnum(ss.Failure.position_source));
        for (sources) |identity| if (identity.owner_name_length > ss.owner_name_capacity)
            return refuse(failure, index, none, @intFromEnum(ss.Failure.owner_name));
        for (positions, 0..) |position, p| {
            const local: u32 = @intCast(p);
            // References stay inside the declaration, so they stay in its region.
            if (position.source >= sources.len) return refuse(failure, index, local, @intFromEnum(ss.Failure.position_source));
            if (position.identity != none and position.identity >= positions.len) return refuse(failure, index, local, @intFromEnum(ss.Failure.position_identity));
            if (position.strings != none and position.strings >= positions.len) return refuse(failure, index, local, @intFromEnum(ss.Failure.position_strings));
            const size = position.byteSize() orelse return refuse(failure, index, local, @intFromEnum(ss.Failure.position_range));
            totals[region].data = std.mem.alignForward(u64, totals[region].data, 8) + size;
        }
        totals[region].sources = std.math.add(u32, totals[region].sources, @intCast(sources.len)) catch return tooLarge(failure, index);
        totals[region].positions = std.math.add(u32, totals[region].positions, @intCast(positions.len)) catch return tooLarge(failure, index);
        if (totals[region].data > std.math.maxInt(u32) - 7) return tooLarge(failure, index);
    }
    failure.* = .{};

    var plan = Plan{};
    errdefer plan.deinit(allocator);
    if (declarations.len != 0) plan.placements = try allocator.alloc(Placement, declarations.len);
    for (&plan.regions, totals) |*region, total| {
        if (total.sources != 0) region.sources = try allocator.alloc(ss.SourceIdentity, total.sources);
        if (total.positions != 0) region.positions = try allocator.alloc(ss.PositionSpec, total.positions);
        region.data_capacity = @intCast(std.mem.alignForward(u64, total.data, 8));
        if (total.sources != 0) {
            const size = ss.requiredSize(total.sources, total.positions, region.data_capacity);
            region.byte_length = std.mem.alignForward(u64, size, region_alignment);
        }
    }

    // Pass 2: global indexes and offsets, in declaration order.
    var used = [_]Totals{ .{}, .{} };
    for (declarations, plan.placements) |*declaration, *placement| {
        const region = declaration.region.index().?;
        const sources = declaration.sourceSlice().?;
        const positions = declaration.positionSlice().?;
        const at = &used[region];
        placement.* = .{
            .region = declaration.region,
            .source_base = at.sources,
            .source_count = @intCast(sources.len),
            .position_base = at.positions,
            .position_count = @intCast(positions.len),
        };
        const layout = &plan.regions[region];
        @memcpy(layout.sources[at.sources..][0..sources.len], sources);
        for (positions, 0..) |position, p| {
            const offset = std.mem.alignForward(u64, at.data, 8);
            const size = position.byteSize().?;
            layout.positions[@as(usize, at.positions) + p] = .{
                .source = placement.source_base + position.source,
                .kind = position.kind,
                .offset = @intCast(offset),
                .size = size,
                .element_width = position.element_width,
                .identity = if (position.identity == none) none else placement.position_base + position.identity,
                .strings = if (position.strings == none) none else placement.position_base + position.strings,
            };
            at.data = offset + size;
        }
        at.sources += placement.source_count;
        at.positions += placement.position_count;
    }

    // Pass 3: the package's own position rules, attributed to a declaration.
    for (&plan.regions) |*layout| {
        const header = ss.Header{
            .magic = ss.magic,
            .version = ss.version,
            .data_kind = 0,
            .source_capacity = @intCast(layout.sources.len),
            .source_count = @intCast(layout.sources.len),
            .position_capacity = @intCast(layout.positions.len),
            .position_count = @intCast(layout.positions.len),
            .data_capacity = layout.data_capacity,
        };
        for (layout.positions, 0..) |spec, p| {
            ss.layout.checkSpec(@intCast(p), spec, header, Targets{ .positions = layout.positions }) catch |err| {
                const class: RegionClass = if (layout == &plan.regions[0]) .normal else .administrator;
                const owner = owningDeclaration(plan.placements, class, @intCast(p));
                failure.region = @intFromEnum(class);
                return refuse(failure, owner.declaration, owner.position, @intFromEnum(ss.failureOf(err)));
            };
        }
    }
    return plan;
}

const Targets = struct {
    positions: []const ss.PositionSpec,
    pub fn get(self: Targets, index: u32) ss.PositionSpec {
        return self.positions[index];
    }
};

const Owning = struct { declaration: u32, position: u32 };

fn owningDeclaration(placements: []const Placement, layout_region: RegionClass, global: u32) Owning {
    for (placements, 0..) |placement, d| {
        if (placement.region == layout_region and global >= placement.position_base and global < placement.position_base + placement.position_count)
            return .{ .declaration = @intCast(d), .position = global - placement.position_base };
    }
    return .{ .declaration = none, .position = none };
}

fn refuse(failure: *ApplyFailure, declaration: u32, position: u32, reason: u32) Error {
    failure.declaration = declaration;
    failure.position = position;
    failure.shared_section_failure = reason;
    return error.DeclarationInvalid;
}

fn tooLarge(failure: *ApplyFailure, declaration: u32) Error {
    failure.declaration = declaration;
    return error.RegionTooLarge;
}

test "a placement is consecutive and every position starts on 8 bytes" {
    const sources = [_]ss.SourceIdentity{ try ss.SourceIdentity.init("a", 1, 0, 0), try ss.SourceIdentity.init("b", 1, 0, 0) };
    const positions = [_]PositionDeclaration{
        .{ .source = 0, .kind = .column, .element_width = 8, .capacity = 3 },
        .{ .source = 1, .kind = .column, .element_width = 4, .capacity = 3, .identity = 0 },
        .{ .source = 1, .kind = .scalar, .element_width = 1, .capacity = 5 },
    };
    const declarations = [_]Declaration{
        Declaration.init(.normal, &sources, &positions),
        Declaration.init(.administrator, sources[0..1], positions[0..1]),
        Declaration.init(.normal, &sources, &positions),
    };
    var failure = ApplyFailure{};
    var plan = try compute(std.testing.allocator, &declarations, &failure);
    defer plan.deinit(std.testing.allocator);
    try std.testing.expectEqual(Placement{ .region = .normal, .source_base = 2, .source_count = 2, .position_base = 3, .position_count = 3 }, plan.placements[2]);
    try std.testing.expectEqual(Placement{ .region = .administrator, .source_base = 0, .source_count = 1, .position_base = 0, .position_count = 1 }, plan.placements[1]);
    const normal = plan.regions[0];
    // 24, then 12 padded to 16, then 5 padded to 8, twice.
    const offsets = [_]u32{ 0, 24, 40, 48, 72, 88 };
    for (normal.positions, offsets) |spec, offset| try std.testing.expectEqual(offset, spec.offset);
    try std.testing.expectEqual(@as(u32, 96), normal.data_capacity);
    try std.testing.expectEqual(@as(u32, 3), normal.positions[4].identity);
    try std.testing.expectEqual(@as(u32, 3), normal.positions[4].source);
    try std.testing.expectEqual(region_alignment, normal.byte_length);
}

test "a reference outside its declaration or a bad width names the declaration" {
    const sources = [_]ss.SourceIdentity{try ss.SourceIdentity.init("a", 1, 0, 0)};
    const good = [_]PositionDeclaration{.{ .source = 0, .kind = .column, .element_width = 8, .capacity = 2 }};
    const outside = [_]PositionDeclaration{.{ .source = 0, .kind = .column, .element_width = 8, .capacity = 2, .identity = 1 }};
    var failure = ApplyFailure{};
    const first = [_]Declaration{ Declaration.init(.normal, &sources, &good), Declaration.init(.normal, &sources, &outside) };
    try std.testing.expectError(error.DeclarationInvalid, compute(std.testing.allocator, &first, &failure));
    try std.testing.expectEqual(@as(u32, 1), failure.declaration);
    try std.testing.expectEqual(@as(u32, 0), failure.position);
    try std.testing.expectEqual(@intFromEnum(ss.Failure.position_identity), failure.shared_section_failure);

    const odd = [_]PositionDeclaration{ good[0], .{ .source = 0, .kind = .column, .element_width = 3, .capacity = 2 } };
    const second = [_]Declaration{ Declaration.init(.administrator, &sources, &good), Declaration.init(.administrator, &sources, &odd) };
    try std.testing.expectError(error.DeclarationInvalid, compute(std.testing.allocator, &second, &failure));
    try std.testing.expectEqual(@as(u32, 1), failure.declaration);
    try std.testing.expectEqual(@as(u32, 1), failure.position);
    try std.testing.expectEqual(@as(u32, 1), failure.region);
    try std.testing.expectEqual(@intFromEnum(ss.Failure.position_width), failure.shared_section_failure);

    const no_sources = [_]Declaration{Declaration.init(.normal, &.{}, &.{})};
    try std.testing.expectError(error.DeclarationInvalid, compute(std.testing.allocator, &no_sources, &failure));
    const bad_region = [_]Declaration{.{ .region = @enumFromInt(2), .source_count = 1, .position_count = 0, .sources = &sources, .positions = null }};
    try std.testing.expectError(error.DeclarationInvalid, compute(std.testing.allocator, &bad_region, &failure));
}

test "a region without declarations has no layout" {
    var failure = ApplyFailure{};
    var plan = try compute(std.testing.allocator, &.{}, &failure);
    defer plan.deinit(std.testing.allocator);
    try std.testing.expect(plan.regions[0].isEmpty() and plan.regions[1].isEmpty());
    try std.testing.expectEqual(@as(u64, 0), plan.regions[0].byte_length);
}
