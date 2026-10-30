const std = @import("std");

pub const Kind = enum(u32) { isr = 1, dpc = 2, threaded_dpc = 3, timer_dpc = 4 };
pub fn isKernelAddress(address: u64) bool {
    return address >= 0xFFFF800000000000;
}
pub const Config = struct {
    max_modules: u32 = 4096,
    max_path_units: u32 = 32768,
    max_identities: u32 = 4096,
    max_slices: u32 = 128,
    max_slice_drivers: u32 = 16384,
};
pub const Error = error{ CapacityExceeded, InvalidConfig, Invalidated };

/// No localized unknown label crosses this boundary. A null path/name is unknown.
/// Borrowed identities from ModuleMap survive until its next mutation.
pub const Identity = struct {
    path: ?[]const u16 = null,
    name: ?[]const u16 = null,
    tag: u32 = 0,
};

fn whitespace(c: u16) bool {
    return (c >= 9 and c <= 13) or c == 32 or c == 0x85 or c == 0xa0 or c == 0x1680 or
        (c >= 0x2000 and c <= 0x200a) or c == 0x2028 or c == 0x2029 or c == 0x202f or c == 0x205f or c == 0x3000;
}

fn trimmed(path: ?[]const u16) ?[]const u16 {
    const value = path orelse return null;
    var start: usize = 0;
    var end = value.len;
    while (start < end and whitespace(value[start])) : (start += 1) {}
    while (end > start and whitespace(value[end - 1])) : (end -= 1) {}
    return if (start == end) null else value[start..end];
}

fn samePath(a: ?[]const u16, b: ?[]const u16) bool {
    if (a) |left| return if (b) |right| std.mem.eql(u16, left, right) else false;
    return b == null;
}

fn identity(path: ?[]const u16, tag: u32) Identity {
    const p = path orelse return .{ .tag = tag };
    var start: usize = if (p.len >= 2 and p[1] == ':' and ((p[0] >= 'a' and p[0] <= 'z') or (p[0] >= 'A' and p[0] <= 'Z'))) 2 else 0;
    for (p, 0..) |c, i| if (c == '\\' or c == '/') {
        start = i + 1;
    };
    return .{ .path = p, .name = p[start..], .tag = tag };
}

const Range = struct { base: u64, last: u64, path: ?[]u16, tag: u32 = 0 };

/// Sorted bases plus a max-last-address segment tree. Queries search the rightmost
/// containing interval in O(log capacity), including nested and crossing ranges.
/// Insertion/removal shifts occupied entries; only affected leaves/ancestors
/// are refreshed. Existing-base updates never scan configured spare capacity.
pub const ModuleMap = struct {
    allocator: std.mem.Allocator,
    ranges: []Range = &.{},
    tree: []u64 = &.{},
    leaves: usize = 0,
    len: usize = 0,
    revision: u64 = 0,
    max_modules: usize,
    max_path_units: usize,

    pub fn init(allocator: std.mem.Allocator, config: Config) !ModuleMap {
        if (config.max_modules == 0 or config.max_modules > 1 << 24 or config.max_path_units == 0) return Error.InvalidConfig;
        return .{ .allocator = allocator, .max_modules = config.max_modules, .max_path_units = config.max_path_units };
    }

    pub fn deinit(self: *ModuleMap) void {
        self.clear();
        self.* = undefined;
    }

    pub fn clear(self: *ModuleMap) void {
        if (self.len != 0) self.revision +%= 1;
        for (self.ranges[0..self.len]) |range| if (range.path) |p| {
            self.allocator.free(p);
        };
        self.len = 0;
        self.releaseIndex();
    }

    fn releaseIndex(self: *ModuleMap) void {
        self.allocator.free(self.tree);
        self.allocator.free(self.ranges);
        self.tree = &.{};
        self.ranges = &.{};
        self.leaves = 0;
    }

    fn ensureCapacity(self: *ModuleMap, required: usize) !void {
        if (required > self.max_modules) return Error.CapacityExceeded;
        if (required <= self.ranges.len) return;
        const next = @min(self.max_modules, @max(required, @max(@as(usize, 4), self.ranges.len * 2)));
        const leaves = std.math.ceilPowerOfTwo(usize, next) catch return Error.InvalidConfig;
        const ranges = try self.allocator.alloc(Range, next);
        errdefer self.allocator.free(ranges);
        const tree = try self.allocator.alloc(u64, leaves * 2);
        @memset(tree, 0);
        @memcpy(ranges[0..self.len], self.ranges[0..self.len]);
        for (ranges[0..self.len], 0..) |range, index| tree[leaves + index] = range.last;
        var node = leaves;
        while (node > 1) {
            node -= 1;
            tree[node] = @max(tree[node * 2], tree[node * 2 + 1]);
        }
        self.releaseIndex();
        self.ranges = ranges;
        self.tree = tree;
        self.leaves = leaves;
    }

    fn lowerBound(self: *const ModuleMap, base: u64) usize {
        var lo: usize = 0;
        var hi = self.len;
        while (lo < hi) {
            const mid = lo + (hi - lo) / 2;
            if (self.ranges[mid].base < base) lo = mid + 1 else hi = mid;
        }
        return lo;
    }

    // Update exactly the changed contiguous leaves and their ancestors. A
    // replacement touches one leaf, whereas insertion/removal also shifts the
    // occupied suffix. Unused configured capacity is never scanned per image.
    fn refresh(self: *ModuleMap, start: usize, end: usize) void {
        std.debug.assert(start < end and end <= self.leaves);
        for (start..end) |index| self.tree[self.leaves + index] = if (index < self.len) self.ranges[index].last else 0;
        var first = (self.leaves + start) / 2;
        var last = (self.leaves + end - 1) / 2;
        while (first != 0) {
            for (first..last + 1) |index| self.tree[index] = @max(self.tree[index * 2], self.tree[index * 2 + 1]);
            first /= 2;
            last /= 2;
        }
    }

    pub fn upsert(self: *ModuleMap, base: u64, size: u64, path: ?[]const u16) !void {
        if (base == 0 or size == 0) return;
        const p = trimmed(path);
        if (p) |value| if (value.len > self.max_path_units) return Error.CapacityExceeded;
        const index = self.lowerBound(base);
        const existing = index < self.len and self.ranges[index].base == base;
        if (!existing and self.len == self.max_modules) return Error.CapacityExceeded;
        if (existing and samePath(self.ranges[index].path, p)) {
            const last = base +| (size - 1);
            if (self.ranges[index].last != last) self.revision +%= 1;
            self.ranges[index].last = last;
            self.ranges[index].tag = 0;
            self.refresh(index, index + 1);
            return;
        }
        const copy = if (p) |value| try self.allocator.dupe(u16, value) else null;
        errdefer if (copy) |value| self.allocator.free(value);
        if (!existing) try self.ensureCapacity(self.len + 1);
        if (existing) {
            if (self.ranges[index].path) |old| self.allocator.free(old);
        } else {
            std.mem.copyBackwards(Range, self.ranges[index + 1 .. self.len + 1], self.ranges[index..self.len]);
            self.len += 1;
        }
        self.ranges[index] = .{ .base = base, .last = base +| (size - 1), .path = copy };
        self.revision +%= 1;
        self.refresh(index, if (existing) index + 1 else self.len);
    }

    pub fn remove(self: *ModuleMap, base: u64) void {
        const index = self.lowerBound(base);
        if (index == self.len or self.ranges[index].base != base) return;
        if (self.ranges[index].path) |p| self.allocator.free(p);
        std.mem.copyForwards(Range, self.ranges[index .. self.len - 1], self.ranges[index + 1 .. self.len]);
        const previous_len = self.len;
        self.len -= 1;
        self.revision +%= 1;
        if (self.len == 0) self.releaseIndex() else self.refresh(index, previous_len);
    }

    pub fn resolve(self: *const ModuleMap, address: u64) ?Identity {
        if (address == 0) return null;
        // Upper bound on base. The disjoint prefix tree nodes are visited from
        // right to left, then the first eligible node descends right first.
        var lo: usize = 0;
        var hi = self.len;
        while (lo < hi) {
            const mid = lo + (hi - lo) / 2;
            if (self.ranges[mid].base <= address) lo = mid + 1 else hi = mid;
        }
        var left = self.leaves;
        var right = self.leaves + lo;
        while (left < right) {
            if (right & 1 != 0) {
                right -= 1;
                if (self.tree[right] >= address) {
                    return self.resolveNode(right, address);
                }
            }
            // For a full prefix the final eligible segment is the tree root.
            if (left & 1 != 0) {
                if (self.tree[left] >= address) return self.resolveNode(left, address);
                left += 1;
            }
            left /= 2;
            right /= 2;
        }
        return null;
    }

    fn resolveNode(self: *const ModuleMap, first: usize, address: u64) Identity {
        var node = first;
        while (node < self.leaves) {
            node *= 2;
            if (self.tree[node + 1] >= address) node += 1;
        }
        const range = self.ranges[node - self.leaves];
        return identity(range.path, range.tag);
    }
};

// The product is Windows-only. CompareStringOrdinal matches the existing .NET
// OrdinalIgnoreCase path grouping and name tie ordering, without a locale table.
extern "kernel32" fn CompareStringOrdinal([*]const u16, c_int, [*]const u16, c_int, c_int) callconv(.winapi) c_int;
fn compare(a: []const u16, b: []const u16) std.math.Order {
    const result = CompareStringOrdinal(a.ptr, @intCast(a.len), b.ptr, @intCast(b.len), 1);
    return if (result == 1) .lt else if (result == 3) .gt else .eq;
}

const Totals = struct {
    duration_ms: f64 = 0,
    maximum_ms: f64 = 0,
    event_count: u64 = 0,
    long_event_count: u64 = 0,
    fn observe(self: *Totals, duration: f64) void {
        self.duration_ms += duration;
        self.maximum_ms = @max(self.maximum_ms, duration);
        self.event_count += 1;
        if (duration >= 1) self.long_event_count += 1;
    }
    fn add(self: *Totals, other: Totals) void {
        self.duration_ms += other.duration_ms;
        self.maximum_ms = @max(self.maximum_ms, other.maximum_ms);
        self.event_count += other.event_count;
        self.long_event_count += other.long_event_count;
    }
};

const Slice = struct { key: i64, totals: Totals = .{}, maximum_kind: ?Kind = null, maximum_identity: u32 = 0 };
const CellKey = struct { second: i64, driver: u32 };
const Cell = struct { key: CellKey, identity_tag: u32, totals: Totals = .{} };
const StoredIdentity = struct { used: bool = false, path: ?[]u16 = null, group: u32 = 0 };

pub const Bucket = struct {
    started_tick_ms: i64,
    ended_tick_ms: i64,
    total_duration_ms: f64 = 0,
    event_count: u64 = 0,
    cpu_capacity_percent: f64 = 0,
};
pub const Driver = struct {
    module: Identity,
    total_duration_ms: f64,
    maximum_single_duration_ms: f64,
    event_count: u64,
    long_event_count: u64,
    cpu_capacity_percent: f64,
};
pub const Snapshot = struct {
    allocator: std.mem.Allocator,
    strings: []u16,
    drivers: []Driver,
    generation: u64,
    events_lost: u64,
    started_tick_ms: i64,
    ended_tick_ms: i64,
    window_ms: i64,
    processor_count: u32,
    available: bool,
    total_duration_ms: f64 = 0,
    maximum_single_duration_ms: f64 = 0,
    maximum_kind: ?Kind = null,
    maximum_module: Identity = .{},
    event_count: u64 = 0,
    long_event_count: u64 = 0,
    cpu_capacity_percent: f64 = 0,
    buckets: [6]Bucket = undefined,
    bucket_count: u32 = 0,

    pub fn deinit(self: *Snapshot) void {
        self.allocator.free(self.strings);
        self.allocator.free(self.drivers);
        self.* = undefined;
    }
};

fn capacity(duration: f64, window: i64, processors: u32) f64 {
    if (window <= 0) return 0;
    const percent = std.math.clamp(duration * 100 / (@as(f64, @floatFromInt(window)) * @as(f64, @floatFromInt(@max(1, processors)))), 0, 100);
    const scaled = percent * 10000;
    const whole = @floor(scaled);
    const fraction = scaled - whole;
    return (whole + @as(f64, if (fraction > 0.5 or (fraction == 0.5 and @mod(whole, 2) != 0)) 1 else 0)) / 10000;
}

/// Single-consumer owner: callers serialize image/event/publication mutations.
/// Publication copies only final typed rows and UTF16 strings into the injected
/// output allocator; that immutable result survives owner reset and destruction.
pub const Owner = struct {
    allocator: std.mem.Allocator,
    config: Config,
    modules: ModuleMap,
    identities: []StoredIdentity,
    identity_len: usize = 1,
    publication_round: std.heap.ArenaAllocator,
    slices: []Slice,
    slice_len: usize = 0,
    cells: []Cell,
    cell_len: usize = 0,
    slice_index: std.AutoHashMapUnmanaged(i64, u32) = .empty,
    cell_index: std.AutoHashMapUnmanaged(CellKey, u32) = .empty,
    generation: u64,
    started_tick_ms: i64,
    events_lost: u64,
    invalidated: bool = false,
    // Set by the first holder binding. Only that binding may adopt the modules
    // recorded since this owner was created.
    bound: bool = false,

    pub fn init(allocator: std.mem.Allocator, config: Config, generation: u64, started_tick_ms: i64, events_lost: u64) !Owner {
        if (config.max_identities < 1 or config.max_slices < 1 or config.max_slice_drivers < 1 or config.max_path_units > std.math.maxInt(c_int)) return Error.InvalidConfig;
        var modules = try ModuleMap.init(allocator, config);
        errdefer modules.deinit();
        const identities = try allocator.alloc(StoredIdentity, 1);
        errdefer allocator.free(identities);
        @memset(identities, .{});
        identities[0].used = true;
        return .{ .allocator = allocator, .config = config, .modules = modules, .identities = identities, .publication_round = std.heap.ArenaAllocator.init(allocator), .slices = &.{}, .cells = &.{}, .generation = generation, .started_tick_ms = started_tick_ms, .events_lost = events_lost };
    }

    pub fn deinit(self: *Owner) void {
        self.reset(self.started_tick_ms, self.events_lost);
        self.modules.deinit();
        self.slice_index.deinit(self.allocator);
        self.cell_index.deinit(self.allocator);
        self.allocator.free(self.identities);
        self.allocator.free(self.slices);
        self.allocator.free(self.cells);
        self.publication_round.deinit();
        self.* = undefined;
    }

    /// Starts a holder binding window. The ImageLoad rundown is emitted once, at
    /// the keyword change before this binding's command applies, and is never
    /// repeated. The first binding of an intact owner therefore keeps the modules
    /// recorded since the output was created; a loss in that interval has already
    /// reset them. A successor binding or an invalidated owner starts a new
    /// module inventory.
    pub fn bind(self: *Owner, started_tick_ms: i64, events_lost: u64) void {
        const adopt = !self.bound and !self.invalidated;
        self.bound = true;
        if (adopt) self.resetWindow(started_tick_ms, events_lost) else self.reset(started_tick_ms, events_lost);
    }

    pub fn reset(self: *Owner, started_tick_ms: i64, events_lost: u64) void {
        // A successor binding or event loss discards the complete module inventory.
        self.modules.clear();
        self.resetWindow(started_tick_ms, events_lost);
    }

    fn resetWindow(self: *Owner, started_tick_ms: i64, events_lost: u64) void {
        self.slice_len = 0;
        self.cell_len = 0;
        self.slice_index.deinit(self.allocator);
        self.cell_index.deinit(self.allocator);
        self.slice_index = .empty;
        self.cell_index = .empty;
        self.allocator.free(self.slices);
        self.allocator.free(self.cells);
        self.slices = &.{};
        self.cells = &.{};
        _ = self.publication_round.reset(.free_all);
        // Drop duration references before collecting their identity storage.
        self.collectIdentities();
        self.started_tick_ms = started_tick_ms;
        self.events_lost = events_lost;
        self.invalidated = false;
    }

    /// A changed cumulative loss count starts fresh coverage and discards address
    /// ownership, because a lost unload makes attribution unsafe.
    pub fn refreshLoss(self: *Owner, now_tick_ms: i64, lost: u64) bool {
        if (lost == self.events_lost) return false;
        self.reset(now_tick_ms, lost);
        return true;
    }

    fn intern(self: *Owner, path: ?[]const u16) !u32 {
        const p = trimmed(path) orelse return 0;
        if (p.len > self.config.max_path_units) return Error.CapacityExceeded;
        var free: ?usize = null;
        const unknown = std.unicode.utf8ToUtf16LeStringLiteral("未知内核模块");
        var group: ?u32 = if (compare(p, unknown) == .eq) 0 else null;
        for (self.identities[1..self.identity_len], 1..) |item, i| {
            if (!item.used) {
                if (free == null) free = i;
                continue;
            }
            if (std.mem.eql(u16, item.path.?, p)) return @intCast(i);
            if (group == null and compare(item.path.?, p) == .eq) group = item.group;
        }
        const index = free orelse if (self.identity_len < self.config.max_identities) self.identity_len else return Error.CapacityExceeded;
        const copy = try self.allocator.dupe(u16, p);
        errdefer self.allocator.free(copy);
        if (index == self.identities.len) {
            const previous_len = self.identities.len;
            const next = @min(self.config.max_identities, @max(index + 1, previous_len * 2));
            self.identities = try self.allocator.realloc(self.identities, next);
            @memset(self.identities[previous_len..], .{});
        }
        self.identities[index] = .{ .used = true, .path = copy, .group = group orelse @intCast(index) };
        if (index == self.identity_len) self.identity_len += 1;
        return @intCast(index);
    }

    pub fn upsertImage(self: *Owner, base: u64, size: u64, path: ?[]const u16, observed_tick_ms: i64) !void {
        if (self.invalidated) return Error.Invalidated;
        errdefer self.invalidated = true;
        if (observed_tick_ms < self.started_tick_ms or base == 0 or size == 0) return;
        const index = self.modules.lowerBound(base);
        const tag = if (index < self.modules.len and self.modules.ranges[index].base == base and samePath(self.modules.ranges[index].path, trimmed(path)))
            self.modules.ranges[index].tag
        else
            try self.intern(path);
        errdefer self.collectIdentities();
        try self.modules.upsert(base, size, path);
        self.modules.ranges[self.modules.lowerBound(base)].tag = tag;
    }

    pub fn removeImage(self: *Owner, base: u64, observed_tick_ms: i64) void {
        if (observed_tick_ms < self.started_tick_ms) return;
        self.modules.remove(base);
    }

    pub fn observe(self: *Owner, kind: Kind, duration_ms: f64, routine: u64, observed_tick_ms: i64) !void {
        if (self.invalidated) return Error.Invalidated;
        errdefer self.invalidated = true;
        if (!std.math.isFinite(duration_ms) or duration_ms <= 0 or observed_tick_ms < self.started_tick_ms) return;
        const relative = std.math.sub(i64, observed_tick_ms, self.started_tick_ms) catch return Error.InvalidConfig;
        const second = @divTrunc(relative, 1000);
        const module = self.modules.resolve(routine) orelse Identity{};
        const key = CellKey{ .second = second, .driver = self.identities[module.tag].group };
        const old_slice = self.slice_index.get(second);
        const old_cell = self.cell_index.get(key);
        if (old_slice == null) {
            if (self.slice_len == self.config.max_slices) return Error.CapacityExceeded;
            if (self.slice_len == self.slices.len)
                self.slices = try self.allocator.realloc(self.slices, @min(self.config.max_slices, @max(@as(usize, 4), self.slices.len * 2)));
            try self.slice_index.ensureUnusedCapacity(self.allocator, 1);
        }
        if (old_cell == null) {
            if (self.cell_len == self.config.max_slice_drivers) return Error.CapacityExceeded;
            if (self.cell_len == self.cells.len)
                self.cells = try self.allocator.realloc(self.cells, @min(self.config.max_slice_drivers, @max(@as(usize, 4), self.cells.len * 2)));
            try self.cell_index.ensureUnusedCapacity(self.allocator, 1);
        }
        const slice_id = old_slice orelse block: {
            const index: u32 = @intCast(self.slice_len);
            self.slices[index] = .{ .key = second };
            self.slice_len += 1;
            self.slice_index.putAssumeCapacity(second, index);
            break :block index;
        };
        const cell_id = old_cell orelse block: {
            const index: u32 = @intCast(self.cell_len);
            self.cells[index] = .{ .key = key, .identity_tag = module.tag };
            self.cell_len += 1;
            self.cell_index.putAssumeCapacity(key, index);
            break :block index;
        };
        const slice = &self.slices[slice_id];
        if (slice.maximum_kind == null or duration_ms > slice.totals.maximum_ms) {
            slice.maximum_kind = kind;
            slice.maximum_identity = module.tag;
        }
        slice.totals.observe(duration_ms);
        self.cells[cell_id].totals.observe(duration_ms);
    }

    fn collectIdentities(self: *Owner) void {
        // Cold collection only: mark retained module/slice references, then free
        // retired paths. Avoid an ever-growing identity history across windows.
        for (self.identities[1..self.identity_len]) |*item| item.used = false;
        for (self.modules.ranges[0..self.modules.len]) |range| self.identities[range.tag].used = true;
        for (self.slices[0..self.slice_len]) |slice| if (slice.maximum_kind != null) {
            self.identities[slice.maximum_identity].used = true;
        };
        for (self.cells[0..self.cell_len]) |cell| {
            self.identities[cell.key.driver].used = true;
            self.identities[cell.identity_tag].used = true;
        }
        for (self.identities[1..self.identity_len]) |item| if (item.used) {
            self.identities[item.group].used = true;
        };
        for (self.identities[1..self.identity_len]) |*item| if (!item.used) {
            if (item.path) |p| self.allocator.free(p);
            item.* = .{};
        };
        while (self.identity_len > 1 and !self.identities[self.identity_len - 1].used) self.identity_len -= 1;
    }

    fn prune(self: *Owner, cutoff: i64) void {
        var kept: usize = 0;
        self.slice_index.clearRetainingCapacity();
        for (self.slices[0..self.slice_len]) |slice| if (slice.key * 1000 >= cutoff) {
            self.slices[kept] = slice;
            self.slice_index.putAssumeCapacity(slice.key, @intCast(kept));
            kept += 1;
        };
        self.slice_len = kept;
        kept = 0;
        self.cell_index.clearRetainingCapacity();
        for (self.cells[0..self.cell_len]) |cell| if (self.slice_index.contains(cell.key.second)) {
            self.cells[kept] = cell;
            self.cell_index.putAssumeCapacity(cell.key, @intCast(kept));
            kept += 1;
        };
        self.cell_len = kept;
        self.collectIdentities();
    }

    const Aggregate = struct { tag: u32, totals: Totals = .{}, seen: bool = false, order: usize = 0 };
    fn sliceLess(_: void, a: Slice, b: Slice) bool {
        return a.key < b.key;
    }
    fn driverLess(self: *const Owner, a: Aggregate, b: Aggregate) bool {
        if (a.totals.duration_ms != b.totals.duration_ms) return a.totals.duration_ms > b.totals.duration_ms;
        // Unknown's old display label is a presentation concern. Its ordinal
        // position is preserved here for the legacy top-16 tie contract only.
        const unknown = std.unicode.utf8ToUtf16LeStringLiteral("未知内核模块");
        const an = identity(self.identities[a.tag].path, a.tag).name orelse unknown;
        const bn = identity(self.identities[b.tag].path, b.tag).name orelse unknown;
        const order = compare(an, bn);
        return if (order == .eq) a.order < b.order else order == .lt;
    }

    /// now_tick_ms is the same monotonic clock used for input. Snapshot bounds
    /// are absolute; bucket UTC conversion belongs to the caller. Publication
    /// does not reset valid totals and prunes only slices older than 61 seconds.
    pub fn publish(self: *Owner, output: std.mem.Allocator, now_tick_ms: i64, processor_count: u32) !Snapshot {
        if (self.invalidated) return Error.Invalidated;
        errdefer self.invalidated = true;
        const relative = std.math.sub(i64, now_tick_ms, self.started_tick_ms) catch return Error.InvalidConfig;
        const start = @max(@as(i64, 0), relative -| 60000);
        const window = @max(@as(i64, 0), relative - start);
        self.prune(relative -| 61000);
        std.mem.sort(Slice, self.slices[0..self.slice_len], {}, sliceLess);
        self.slice_index.clearRetainingCapacity();
        for (self.slices[0..self.slice_len], 0..) |slice, i| self.slice_index.putAssumeCapacity(slice.key, @intCast(i));
        // Scratch belongs only to this publication. Output strings and rows are
        // copied to the caller's independently retained allocation below.
        defer _ = self.publication_round.reset(.free_all);
        const aggregates = try self.publication_round.allocator().alloc(Aggregate, self.identity_len);
        for (aggregates, 0..) |*a, i| a.* = .{ .tag = @intCast(i) };
        var total: Totals = .{};
        var maximum_kind: ?Kind = null;
        var maximum_tag: u32 = 0;
        var driver_count: usize = 0;
        // Slice order is significant for equal maxima and floating-point sums.
        for (self.slices[0..self.slice_len]) |slice| {
            const at = slice.key * 1000;
            if (at < start or at > relative) continue;
            if (maximum_kind == null or slice.totals.maximum_ms > total.maximum_ms) {
                maximum_kind = slice.maximum_kind;
                maximum_tag = slice.maximum_identity;
            }
            total.add(slice.totals);
            for (self.cells[0..self.cell_len]) |cell| {
                if (cell.key.second != slice.key) continue;
                const a = &aggregates[cell.key.driver];
                if (!a.seen) {
                    a.seen = true;
                    a.tag = cell.identity_tag;
                    a.order = driver_count;
                    driver_count += 1;
                }
                a.totals.add(cell.totals);
            }
        }
        var kept: usize = 0;
        for (aggregates) |a| if (a.seen) {
            aggregates[kept] = a;
            kept += 1;
        };
        std.mem.sort(Aggregate, aggregates[0..kept], self, driverLess);
        driver_count = @min(driver_count, 16);
        const drivers = try output.alloc(Driver, driver_count);
        errdefer output.free(drivers);
        var string_units: usize = 0;
        for (aggregates[0..driver_count]) |a| if (self.identities[a.tag].path) |p| {
            string_units += p.len;
        };
        if (maximum_kind != null) if (self.identities[maximum_tag].path) |p| {
            string_units += p.len;
        };
        const strings = try output.alloc(u16, string_units);
        errdefer output.free(strings);
        var cursor: usize = 0;
        var snapshot = Snapshot{ .allocator = output, .strings = strings, .drivers = drivers, .generation = self.generation, .events_lost = self.events_lost, .started_tick_ms = self.started_tick_ms + start, .ended_tick_ms = now_tick_ms, .window_ms = window, .processor_count = @max(1, processor_count), .available = window >= 10000, .total_duration_ms = total.duration_ms, .maximum_single_duration_ms = total.maximum_ms, .maximum_kind = maximum_kind, .event_count = total.event_count, .long_event_count = total.long_event_count, .cpu_capacity_percent = capacity(total.duration_ms, window, processor_count) };
        for (aggregates[0..driver_count], drivers) |a, *driver| {
            driver.* = .{ .module = copyIdentity(self.identities[a.tag].path, a.tag, strings, &cursor), .total_duration_ms = a.totals.duration_ms, .maximum_single_duration_ms = a.totals.maximum_ms, .event_count = a.totals.event_count, .long_event_count = a.totals.long_event_count, .cpu_capacity_percent = capacity(a.totals.duration_ms, window, processor_count) };
        }
        if (maximum_kind != null) snapshot.maximum_module = copyIdentity(self.identities[maximum_tag].path, maximum_tag, strings, &cursor);
        const complete: i64 = @min(@as(i64, 6), @divTrunc(window, 10000));
        snapshot.bucket_count = @intCast(complete);
        const first_end = start + @divTrunc(window, 10000) * 10000 - (complete - 1) * 10000;
        var i: usize = 0;
        while (i < snapshot.bucket_count) : (i += 1) {
            const end = first_end + @as(i64, @intCast(i)) * 10000;
            const begin = end - 10000;
            var bucket = Bucket{ .started_tick_ms = self.started_tick_ms + begin, .ended_tick_ms = self.started_tick_ms + end };
            for (self.slices[0..self.slice_len]) |slice| {
                const at = slice.key * 1000;
                if (at >= start and at <= relative and at >= begin and at < end) {
                    bucket.total_duration_ms += slice.totals.duration_ms;
                    bucket.event_count += slice.totals.event_count;
                }
            }
            bucket.cpu_capacity_percent = capacity(bucket.total_duration_ms, 10000, processor_count);
            snapshot.buckets[i] = bucket;
        }
        return snapshot;
    }
};

fn copyIdentity(path: ?[]const u16, tag: u32, strings: []u16, cursor: *usize) Identity {
    const p = path orelse return .{ .tag = tag };
    const copy = strings[cursor.* .. cursor.* + p.len];
    @memcpy(copy, p);
    cursor.* += p.len;
    return identity(copy, tag);
}
test "IRQ publication returns round scratch while immutable output islands remain" {
    const Region = @import("region_allocator.zig").RegionAllocator;
    const Budget = @import("budget.zig").Budget;
    const backing = try std.testing.allocator.alloc(u8, 8 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    var region = try Region.init(backing);
    defer region.deinit();
    var owner_budget: Budget = .{ .backing = region.allocator(), .cap = backing.len };
    var other_budget: Budget = .{ .backing = region.allocator(), .cap = 1024 * 1024 };
    var owner = try Owner.init(owner_budget.allocator(), .{ .max_modules = 8, .max_path_units = 128, .max_identities = 32768, .max_slices = 8, .max_slice_drivers = 16 }, 1, 0, 0);
    defer owner.deinit();
    try owner.upsertImage(100, 16, std.unicode.utf8ToUtf16LeStringLiteral("Z:\\driver.sys"), 0);
    try owner.observe(.isr, 1, 101, 1);
    const initial_owner_bytes = owner_budget.current;
    var publications: [8]?Snapshot = @splat(null);
    var pins: [8]?[]u8 = @splat(null);
    defer {
        for (&publications) |*item| if (item.*) |*snapshot| snapshot.deinit();
        for (pins) |item| if (item) |pin| other_budget.allocator().free(pin);
    }
    for (0..8) |index| {
        // Final publication strings/rows remain alive past the large temporary
        // aggregate. A following long-lived small owner allocation punctures
        // its freed span, as CPU metadata does between ETW buffers.
        publications[index] = try owner.publish(other_budget.allocator(), 10000 + @as(i64, @intCast(index)), 1);
        pins[index] = try other_budget.allocator().alloc(u8, 256);
        try std.testing.expectEqual(initial_owner_bytes, owner_budget.current);
    }
    for (publications) |item| try std.testing.expectEqual(@as(u64, 1), item.?.event_count);
    try std.testing.expect(other_budget.current < 8 * 1024);
    try std.testing.expect(owner_budget.current - initial_owner_bytes < 256 * 1024);
    try std.testing.expect(region.peak < 4 * 1024 * 1024);
}
