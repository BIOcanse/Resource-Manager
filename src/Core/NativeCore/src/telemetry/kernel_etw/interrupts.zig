const std = @import("std");

pub const Kind = enum(u32) { isr = 1, dpc = 2, threaded_dpc = 3, timer_dpc = 4 };
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
/// Image mutation is cold: insertion/removal shifts entries and rebuilds the tree.
pub const ModuleMap = struct {
    allocator: std.mem.Allocator,
    ranges: []Range,
    tree: []u64,
    leaves: usize,
    len: usize = 0,
    max_path_units: usize,

    pub fn init(allocator: std.mem.Allocator, config: Config) !ModuleMap {
        if (config.max_modules == 0 or config.max_modules > 1 << 24 or config.max_path_units == 0) return Error.InvalidConfig;
        const ranges = try allocator.alloc(Range, config.max_modules);
        errdefer allocator.free(ranges);
        const leaves = std.math.ceilPowerOfTwo(usize, config.max_modules) catch return Error.InvalidConfig;
        const tree = try allocator.alloc(u64, leaves * 2);
        @memset(tree, 0);
        return .{ .allocator = allocator, .ranges = ranges, .tree = tree, .leaves = leaves, .max_path_units = config.max_path_units };
    }

    pub fn deinit(self: *ModuleMap) void {
        self.clear();
        self.allocator.free(self.tree);
        self.allocator.free(self.ranges);
        self.* = undefined;
    }

    pub fn clear(self: *ModuleMap) void {
        for (self.ranges[0..self.len]) |range| if (range.path) |p| {
            self.allocator.free(p);
        };
        self.len = 0;
        @memset(self.tree, 0);
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

    fn rebuild(self: *ModuleMap) void {
        @memset(self.tree[self.leaves..], 0);
        for (self.ranges[0..self.len], 0..) |range, i| self.tree[self.leaves + i] = range.last;
        var i = self.leaves;
        while (i > 1) {
            i -= 1;
            self.tree[i] = @max(self.tree[i * 2], self.tree[i * 2 + 1]);
        }
    }

    pub fn upsert(self: *ModuleMap, base: u64, size: u64, path: ?[]const u16) !void {
        if (base == 0 or size == 0) return;
        const p = trimmed(path);
        if (p) |value| if (value.len > self.max_path_units) return Error.CapacityExceeded;
        const index = self.lowerBound(base);
        const existing = index < self.len and self.ranges[index].base == base;
        if (!existing and self.len == self.ranges.len) return Error.CapacityExceeded;
        const copy = if (p) |value| try self.allocator.dupe(u16, value) else null;
        if (existing) {
            if (self.ranges[index].path) |old| self.allocator.free(old);
        } else {
            std.mem.copyBackwards(Range, self.ranges[index + 1 .. self.len + 1], self.ranges[index..self.len]);
            self.len += 1;
        }
        self.ranges[index] = .{ .base = base, .last = base +| (size - 1), .path = copy };
        self.rebuild();
    }

    pub fn remove(self: *ModuleMap, base: u64) void {
        const index = self.lowerBound(base);
        if (index == self.len or self.ranges[index].base != base) return;
        if (self.ranges[index].path) |p| self.allocator.free(p);
        std.mem.copyForwards(Range, self.ranges[index .. self.len - 1], self.ranges[index + 1 .. self.len]);
        self.len -= 1;
        self.rebuild();
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

    pub fn init(allocator: std.mem.Allocator, config: Config, generation: u64, started_tick_ms: i64, events_lost: u64) !Owner {
        if (config.max_identities < 1 or config.max_slices < 1 or config.max_slice_drivers < 1 or config.max_path_units > std.math.maxInt(c_int)) return Error.InvalidConfig;
        var modules = try ModuleMap.init(allocator, config);
        errdefer modules.deinit();
        const identities = try allocator.alloc(StoredIdentity, config.max_identities);
        errdefer allocator.free(identities);
        @memset(identities, .{});
        identities[0].used = true;
        const slices = try allocator.alloc(Slice, config.max_slices);
        errdefer allocator.free(slices);
        const cells = try allocator.alloc(Cell, config.max_slice_drivers);
        errdefer allocator.free(cells);
        var slice_index: std.AutoHashMapUnmanaged(i64, u32) = .empty;
        errdefer slice_index.deinit(allocator);
        try slice_index.ensureTotalCapacity(allocator, config.max_slices);
        var cell_index: std.AutoHashMapUnmanaged(CellKey, u32) = .empty;
        errdefer cell_index.deinit(allocator);
        try cell_index.ensureTotalCapacity(allocator, config.max_slice_drivers);
        return .{ .allocator = allocator, .config = config, .modules = modules, .identities = identities, .slices = slices, .cells = cells, .slice_index = slice_index, .cell_index = cell_index, .generation = generation, .started_tick_ms = started_tick_ms, .events_lost = events_lost };
    }

    pub fn deinit(self: *Owner) void {
        self.reset(self.started_tick_ms, self.events_lost);
        self.modules.deinit();
        self.slice_index.deinit(self.allocator);
        self.cell_index.deinit(self.allocator);
        self.allocator.free(self.identities);
        self.allocator.free(self.slices);
        self.allocator.free(self.cells);
        self.* = undefined;
    }

    pub fn reset(self: *Owner, started_tick_ms: i64, events_lost: u64) void {
        self.modules.clear();
        for (self.identities) |*item| {
            if (item.path) |p| self.allocator.free(p);
            item.* = .{};
        }
        self.identities[0].used = true;
        self.slice_len = 0;
        self.cell_len = 0;
        self.slice_index.clearRetainingCapacity();
        self.cell_index.clearRetainingCapacity();
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
        for (self.identities[1..], 1..) |item, i| {
            if (!item.used) {
                if (free == null) free = i;
                continue;
            }
            if (std.mem.eql(u16, item.path.?, p)) return @intCast(i);
            if (group == null and compare(item.path.?, p) == .eq) group = item.group;
        }
        const index = free orelse return Error.CapacityExceeded;
        self.identities[index] = .{ .used = true, .path = try self.allocator.dupe(u16, p), .group = group orelse @intCast(index) };
        return @intCast(index);
    }

    pub fn upsertImage(self: *Owner, base: u64, size: u64, path: ?[]const u16, observed_tick_ms: i64) !void {
        if (self.invalidated) return Error.Invalidated;
        errdefer self.invalidated = true;
        if (observed_tick_ms < self.started_tick_ms or base == 0 or size == 0) return;
        const tag = try self.intern(path);
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
        if (old_slice == null and self.slice_len == self.slices.len) return Error.CapacityExceeded;
        if (old_cell == null and self.cell_len == self.cells.len) return Error.CapacityExceeded;
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
        for (self.identities[1..]) |*item| item.used = false;
        for (self.modules.ranges[0..self.modules.len]) |range| self.identities[range.tag].used = true;
        for (self.slices[0..self.slice_len]) |slice| if (slice.maximum_kind != null) {
            self.identities[slice.maximum_identity].used = true;
        };
        for (self.cells[0..self.cell_len]) |cell| {
            self.identities[cell.key.driver].used = true;
            self.identities[cell.identity_tag].used = true;
        }
        for (self.identities[1..]) |item| if (item.used) {
            self.identities[item.group].used = true;
        };
        for (self.identities[1..]) |*item| if (!item.used) {
            if (item.path) |p| self.allocator.free(p);
            item.* = .{};
        };
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
        var round = std.heap.ArenaAllocator.init(self.allocator);
        defer round.deinit();
        const aggregates = try round.allocator().alloc(Aggregate, self.identities.len);
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
