const std = @import("std");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;
const version = @import("protocol.zig").abi_version;

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    core_capacity: u32,
    ccd_capacity: u32,
    software_capacity: u32,
    reservation_capacity: u32,
    core_enter: f64,
    core_exit: f64,
    ccd_enter: f64,
    ccd_exit: f64,
    qualification_rounds: u32,
    enabled: u32,
    reserved: u64 = 0,
};
pub const Frame = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    topology_key: u64,
    observation_source: u64,
    observed_through: u64,
    core_count: u32,
    ccd_count: u32,
    software_count: u32,
    usage_count: u32,
    manual_count: u32,
    output_capacity: u32,
    output_count: u32 = 0,
    reserved: u32 = 0,
};
pub const Core = extern struct { ccd_index: u32, reserved: u32 = 0 };
pub const Software = extern struct {
    key: u64,
    instance_digest: u64,
    priority: f64,
    automatic: u32,
    observed: u32,
};
pub const Usage = extern struct { software_index: u32, core_index: u32, percent: f64 };
pub const Reservation = extern struct { software_index: u32, core_index: u32 };
const Owner = struct { key: u64 = 0, instance_digest: u64 = 0, rounds: u32 = 0 };

// This state is owned and reset by the placement session, alongside its action state.
pub const State = struct {
    allocator: std.mem.Allocator,
    config: Config,
    owners: []Owner,
    next_owners: []Owner,
    core_active: []bool,
    next_core_active: []bool,
    ccd_active: []bool,
    next_ccd_active: []bool,
    usage: []f64,
    union_cores: []bool,
    owner_cores: []bool,
    order: []u32,
    output: []Reservation,
    owner_count: usize = 0,
    topology_key: u64 = 0,
    observation_source: u64 = 0,
    observed_through: u64 = 0,

    pub fn create(allocator: std.mem.Allocator, config: Config) !*State {
        if (!validConfig(config)) return error.InvalidConfiguration;
        const self = try allocator.create(State);
        errdefer allocator.destroy(self);
        const core_size = try std.math.mul(usize, config.software_capacity, config.core_capacity);
        const ccd_size = try std.math.mul(usize, config.software_capacity, config.ccd_capacity);
        const owners = try allocator.alloc(Owner, config.software_capacity);
        errdefer allocator.free(owners);
        const next_owners = try allocator.alloc(Owner, config.software_capacity);
        errdefer allocator.free(next_owners);
        const core_active = try allocator.alloc(bool, core_size);
        errdefer allocator.free(core_active);
        const next_core_active = try allocator.alloc(bool, core_size);
        errdefer allocator.free(next_core_active);
        const ccd_active = try allocator.alloc(bool, ccd_size);
        errdefer allocator.free(ccd_active);
        const next_ccd_active = try allocator.alloc(bool, ccd_size);
        errdefer allocator.free(next_ccd_active);
        const usage = try allocator.alloc(f64, config.core_capacity);
        errdefer allocator.free(usage);
        const union_cores = try allocator.alloc(bool, config.core_capacity);
        errdefer allocator.free(union_cores);
        const owner_cores = try allocator.alloc(bool, config.core_capacity);
        errdefer allocator.free(owner_cores);
        const order = try allocator.alloc(u32, config.software_capacity);
        errdefer allocator.free(order);
        const output = try allocator.alloc(Reservation, config.reservation_capacity);
        self.* = .{ .allocator = allocator, .config = config, .owners = owners, .next_owners = next_owners, .core_active = core_active, .next_core_active = next_core_active, .ccd_active = ccd_active, .next_ccd_active = next_ccd_active, .usage = usage, .union_cores = union_cores, .owner_cores = owner_cores, .order = order, .output = output };
        self.reset();
        return self;
    }

    pub fn destroy(self: *State) void {
        const a = self.allocator;
        a.free(self.owners);
        a.free(self.next_owners);
        a.free(self.core_active);
        a.free(self.next_core_active);
        a.free(self.ccd_active);
        a.free(self.next_ccd_active);
        a.free(self.usage);
        a.free(self.union_cores);
        a.free(self.owner_cores);
        a.free(self.order);
        a.free(self.output);
        a.destroy(self);
    }

    pub fn reset(self: *State) void {
        self.owner_count = 0;
        self.topology_key = 0;
        self.observation_source = 0;
        self.observed_through = 0;
        @memset(self.owners, .{});
        @memset(self.core_active, false);
        @memset(self.ccd_active, false);
    }

    pub fn reconfigure(self: *State, config: Config) ResultCode {
        if (!validConfig(config)) return .invalid_argument;
        if (config.generation < self.config.generation) return .stale_frame;
        if (config.core_capacity != self.config.core_capacity or config.ccd_capacity != self.config.ccd_capacity or
            config.software_capacity != self.config.software_capacity or config.reservation_capacity != self.config.reservation_capacity)
            return .invalid_argument;
        self.config = config;
        return .ok;
    }

    pub fn plan(self: *State, frame: *Frame, cores: []const Core, software: []const Software, usage: []const Usage, manual: []const Reservation, output: []Reservation) ResultCode {
        frame.output_count = 0;
        if (frame.abi_version != version or frame.struct_size != @sizeOf(Frame) or frame.reserved != 0) return .abi_mismatch;
        if (frame.generation != self.config.generation) return .stale_frame;
        if (frame.topology_key == 0 or cores.len == 0 or cores.len > self.config.core_capacity or
            frame.ccd_count == 0 or frame.ccd_count > self.config.ccd_capacity or software.len > self.config.software_capacity or
            usage.len > @as(u64, self.config.software_capacity) * self.config.core_capacity or manual.len > self.config.reservation_capacity or
            frame.core_count != cores.len or frame.software_count != software.len or frame.usage_count != usage.len or
            frame.manual_count != manual.len or frame.output_capacity > output.len) return .invalid_argument;
        for (cores) |core| if (core.reserved != 0 or core.ccd_index >= frame.ccd_count) return .invalid_argument;
        var last_key: u64 = 0;
        for (software) |owner| {
            if (owner.key <= last_key or owner.instance_digest == 0 or owner.automatic > 1 or owner.observed > 1 or
                !std.math.isFinite(owner.priority) or owner.priority < 0 or
                (owner.observed == 1 and (frame.observation_source == 0 or frame.observed_through == 0))) return .invalid_argument;
            last_key = owner.key;
        }
        var previous_usage: ?Usage = null;
        for (usage) |row| {
            if (row.software_index >= software.len or row.core_index >= cores.len or
                !std.math.isFinite(row.percent) or row.percent < 0 or software[row.software_index].observed != 1) return .invalid_argument;
            if (previous_usage) |p| {
                if (row.software_index < p.software_index or
                    (row.software_index == p.software_index and row.core_index <= p.core_index)) return .invalid_argument;
            }
            previous_usage = row;
        }
        for (manual) |row| if (row.software_index >= software.len or row.core_index >= cores.len) return .invalid_argument;
        const same_topology = self.topology_key == frame.topology_key;
        const same_source = frame.observation_source == 0 or self.observation_source == frame.observation_source;
        if (same_topology and same_source and frame.observed_through != 0 and frame.observed_through < self.observed_through) return .stale_frame;
        const retain = same_topology and same_source;
        const fresh = frame.observed_through != 0 and (!retain or frame.observed_through > self.observed_through);
        @memset(self.next_core_active, false);
        @memset(self.next_ccd_active, false);
        var prior_index: usize = 0;
        var usage_index: usize = 0;
        for (software, 0..) |owner, i| {
            while (prior_index < self.owner_count and self.owners[prior_index].key < owner.key) : (prior_index += 1) {}
            const prior = retain and prior_index < self.owner_count and self.owners[prior_index].key == owner.key and
                self.owners[prior_index].instance_digest == owner.instance_digest;
            var rounds: u32 = if (prior) self.owners[prior_index].rounds else 0;
            const core_state = self.next_core_active[i * self.config.core_capacity ..][0..cores.len];
            const ccd_state = self.next_ccd_active[i * self.config.ccd_capacity ..][0..frame.ccd_count];
            if (prior and self.config.enabled == 1 and owner.automatic == 1 and rounds >= self.config.qualification_rounds) {
                @memcpy(core_state, self.core_active[prior_index * self.config.core_capacity ..][0..cores.len]);
                @memcpy(ccd_state, self.ccd_active[prior_index * self.config.ccd_capacity ..][0..frame.ccd_count]);
            }
            @memset(self.usage, 0);
            while (usage_index < usage.len and usage[usage_index].software_index == i) : (usage_index += 1) {
                const row = usage[usage_index];
                self.usage[row.core_index] = @min(100, row.percent);
            }
            if (fresh and owner.observed == 1) {
                if (self.config.enabled == 1 and owner.automatic == 1 and rounds >= self.config.qualification_rounds) {
                    for (core_state, 0..) |*active, c| active.* = transition(active.*, self.usage[c], self.config.core_enter, self.config.core_exit);
                    if (frame.ccd_count > 1) {
                        for (ccd_state, 0..) |*active, ccd| {
                            var total: f64 = 0;
                            var count: u32 = 0;
                            for (cores, 0..) |core, c| if (core.ccd_index == ccd) {
                                total += self.usage[c];
                                count += 1;
                            };
                            active.* = count != 0 and transition(active.*, total / @as(f64, @floatFromInt(count)), self.config.ccd_enter, self.config.ccd_exit);
                        }
                    }
                }
                if (rounds < self.config.qualification_rounds) rounds += 1;
            }
            self.next_owners[i] = .{ .key = owner.key, .instance_digest = owner.instance_digest, .rounds = rounds };
            self.order[i] = @intCast(i);
        }
        std.mem.sort(u32, self.order[0..software.len], software, struct {
            fn less(rows: []const Software, a: u32, b: u32) bool {
                return rows[a].priority > rows[b].priority or (rows[a].priority == rows[b].priority and rows[a].key < rows[b].key);
            }
        }.less);
        @memset(self.union_cores, false);
        var output_count: usize = 0;
        for (self.order[0..software.len]) |i| {
            @memset(self.owner_cores, false);
            // Explicit reservations precede this owner's inferred reservations.
            for (manual) |row| if (row.software_index == i) {
                _ = self.acceptCore(row.core_index, cores.len);
            };
            const ccd_state = self.next_ccd_active[i * self.config.ccd_capacity ..][0..frame.ccd_count];
            for (ccd_state, 0..) |active, ccd| {
                if (!active) continue;
                var remaining: usize = 0;
                for (cores, 0..) |core, c| if (!self.union_cores[c] and core.ccd_index != ccd) {
                    remaining += 1;
                };
                if (remaining == 0) continue;
                for (cores, 0..) |core, c| if (core.ccd_index == ccd) {
                    _ = self.acceptCore(c, cores.len);
                };
            }
            const core_state = self.next_core_active[i * self.config.core_capacity ..][0..cores.len];
            for (core_state, 0..) |active, c| if (active) {
                _ = self.acceptCore(c, cores.len);
            };
            for (self.owner_cores[0..cores.len], 0..) |active, c| {
                if (!active) continue;
                if (output_count == self.output.len or output_count == frame.output_capacity) return .buffer_too_small;
                self.output[output_count] = .{ .software_index = i, .core_index = @intCast(c) };
                output_count += 1;
            }
        }
        // Commit only after all validation, clipping and output capacity checks succeeded.
        std.mem.swap([]Owner, &self.owners, &self.next_owners);
        std.mem.swap([]bool, &self.core_active, &self.next_core_active);
        std.mem.swap([]bool, &self.ccd_active, &self.next_ccd_active);
        self.owner_count = software.len;
        self.topology_key = frame.topology_key;
        if (frame.observation_source != 0) {
            self.observation_source = frame.observation_source;
            self.observed_through = frame.observed_through;
        }
        @memcpy(output[0..output_count], self.output[0..output_count]);
        frame.output_count = @intCast(output_count);
        return .ok;
    }

    fn acceptCore(self: *State, c: usize, core_count: usize) bool {
        if (!self.union_cores[c]) {
            var remaining: usize = 0;
            for (self.union_cores[0..core_count], 0..) |active, index| if (!active and index != c) {
                remaining += 1;
            };
            if (remaining == 0) return false;
        }
        self.union_cores[c] = true;
        self.owner_cores[c] = true;
        return true;
    }
};

fn transition(active: bool, usage: f64, enter: f64, exit: f64) bool {
    return if (active) usage >= exit else usage >= enter;
}
pub fn validConfig(c: Config) bool {
    return c.abi_version == version and c.struct_size == @sizeOf(Config) and c.generation != 0 and c.reserved == 0 and
        c.core_capacity > 0 and c.ccd_capacity > 0 and c.software_capacity > 0 and c.reservation_capacity > 0 and c.enabled <= 1 and
        std.math.isFinite(c.core_enter) and std.math.isFinite(c.core_exit) and c.core_exit >= 0 and c.core_exit < c.core_enter and c.core_enter <= 100 and
        std.math.isFinite(c.ccd_enter) and std.math.isFinite(c.ccd_exit) and c.ccd_exit >= 0 and c.ccd_exit < c.ccd_enter and c.ccd_enter <= 100;
}

comptime {
    if (@sizeOf(Config) != 80 or @sizeOf(Frame) != 72 or @sizeOf(Software) != 32 or
        @sizeOf(Core) != 8 or @sizeOf(Usage) != 16 or @sizeOf(Reservation) != 8) @compileError("CPU exclusivity ABI layout changed");
}
