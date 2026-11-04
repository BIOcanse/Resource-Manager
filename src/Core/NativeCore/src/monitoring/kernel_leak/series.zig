const std = @import("std");

// Capacities and judgment thresholds are the compiled plan supplied by the owner.
pub const Options = extern struct {
    series_capacity: u32,
    maximum_holders: u32,
    minimum_samples: u32,
    minimum_seconds: u32,
    handle_floor: u64,
    minimum_growth: u64,
    edge_percentage: u32,
};

pub const Sample = extern struct {
    pid: i32,
    kind: u32,
    birth: u64,
    value: u64,
    key: [20]u8,
    key_length: u32,
};
pub const Suspect = extern struct {
    pid: i32,
    sample_count: u32,
    birth: u64,
    current: u64,
    growth: u64,
    seconds: f64,
    rate: f64,
    key: [20]u8,
    key_length: u32,
};
pub const Key = extern struct {
    pid: i32,
    reserved: u32,
    birth: u64,
    key: [20]u8,
    key_length: u32,
};
pub const Result = struct { suspect_count: usize, judged_count: usize };
pub const Error = error{ InvalidOptions, BufferTooSmall, CapacityExceeded, InvalidObservation };

const Point = extern struct { ticks: i64, value: u64 };
const Slot = extern struct {
    pid: i32,
    count: u32,
    birth: u64,
    next: u32,
    key_length: u32,
    key: [20]u8,
};

fn valid(options: Options) bool {
    return options.series_capacity >= 2 and options.series_capacity <= 1024 and
        options.maximum_holders > 0 and options.maximum_holders <= 16384 and
        options.minimum_samples >= 2 and options.minimum_samples <= options.series_capacity and
        options.minimum_seconds > 0 and options.minimum_growth > 0 and
        options.edge_percentage >= 1 and options.edge_percentage <= 50 and
        @as(u64, options.series_capacity) * options.maximum_holders <= 524288;
}

fn indexCapacity(options: Options) usize {
    // maximum_holders is validated before this helper is used.
    return std.math.ceilPowerOfTwoAssert(usize, @as(usize, options.maximum_holders) * 2);
}
fn pointsOffset(options: Options) usize {
    return @sizeOf(Series) + @sizeOf(Slot) * @as(usize, options.maximum_holders);
}
fn indexOffset(options: Options) usize {
    return pointsOffset(options) + @sizeOf(Point) * @as(usize, options.maximum_holders) * options.series_capacity;
}
pub fn requiredBytes(options: Options) usize {
    if (!valid(options)) return 0;
    return indexOffset(options) + @sizeOf(u32) * (indexCapacity(options) + options.maximum_holders);
}

// All offsets are derived from this header: there are no cached pointers into a
// Ledger region, so the region can be reacquired at a different address.
pub const Series = extern struct {
    options: Options,
    holder_count: u32,
    reserved: u32,
    peak_bytes: u64,

    fn region(self: *Series) [*]align(8) u8 {
        return @ptrCast(self);
    }
    fn slots(self: *Series) []Slot {
        const pointer: [*]Slot = @ptrCast(@alignCast(self.region() + @sizeOf(Series)));
        return pointer[0..self.options.maximum_holders];
    }
    fn points(self: *Series, slot: usize) []Point {
        const pointer: [*]Point = @ptrCast(@alignCast(self.region() + pointsOffset(self.options)));
        const capacity: usize = self.options.series_capacity;
        return pointer[slot * capacity ..][0..capacity];
    }
    fn inputIndex(self: *Series) []u32 {
        const pointer: [*]u32 = @ptrCast(@alignCast(self.region() + indexOffset(self.options)));
        return pointer[0..indexCapacity(self.options)];
    }
    fn sampleSlots(self: *Series) []u32 {
        const pointer: [*]u32 = @ptrCast(@alignCast(self.region() + indexOffset(self.options)));
        return pointer[indexCapacity(self.options)..][0..self.options.maximum_holders];
    }
    pub fn count(self: *const Series) usize {
        return self.holder_count;
    }
    pub fn bytes(self: *const Series) u64 {
        return @as(u64, self.holder_count) * self.options.series_capacity * @sizeOf(Point);
    }
    pub fn peakBytes(self: *const Series) u64 {
        return self.peak_bytes;
    }
    pub fn clear(self: *Series) void {
        @memset(self.slots(), std.mem.zeroes(Slot));
        self.holder_count = 0;
    }

    fn at(self: *Series, slot_index: usize, slot: Slot, index: usize) Point {
        const capacity: usize = self.options.series_capacity;
        return self.points(slot_index)[(@as(usize, slot.next) + capacity - slot.count + index) % capacity];
    }

    pub fn observe(self: *Series, ticks: i64, samples: []const Sample, suspects: []Suspect, judged: []Key) Error!Result {
        if (samples.len > self.options.maximum_holders) return error.CapacityExceeded;
        const table = self.inputIndex();
        const sample_slots = self.sampleSlots();
        @memset(table, 0);
        @memset(sample_slots, 0);
        var eligible: usize = 0;
        // Validate the complete input before changing existing rings. Cache-like
        // quantities count towards input capacity but never enter the series.
        for (samples, 0..) |sample, input_index| {
            if (sample.kind != 0) continue;
            if (sample.pid <= 0 or sample.birth == 0 or sample.key_length == 0 or sample.key_length > sample.key.len)
                return error.InvalidObservation;
            const position = find(table, samples, sample.pid, sample.key[0..sample.key_length]);
            if (table[position] != 0) return error.InvalidObservation;
            table[position] = @intCast(input_index + 1);
            eligible += 1;
        }
        if (suspects.len < eligible or judged.len < eligible) return error.BufferTooSmall;

        const holder_slots = self.slots();
        for (holder_slots, 0..) |*slot, slot_index| {
            if (slot.pid == 0) continue;
            const position = find(table, samples, slot.pid, slot.key[0..slot.key_length]);
            if (table[position] == 0) {
                slot.* = std.mem.zeroes(Slot);
                self.holder_count -= 1;
            } else sample_slots[table[position] - 1] = @intCast(slot_index + 1);
        }

        var result = Result{ .suspect_count = 0, .judged_count = 0 };
        var free_slot: usize = 0;
        for (samples, 0..) |sample, input_index| {
            if (sample.kind != 0) continue;
            var slot_index: usize = undefined;
            if (sample_slots[input_index] != 0) {
                slot_index = sample_slots[input_index] - 1;
            } else {
                while (free_slot < holder_slots.len and holder_slots[free_slot].pid != 0) : (free_slot += 1) {}
                if (free_slot == holder_slots.len) return error.CapacityExceeded;
                slot_index = free_slot;
                holder_slots[slot_index] = .{
                    .pid = sample.pid,
                    .count = 0,
                    .birth = sample.birth,
                    .next = 0,
                    .key_length = sample.key_length,
                    .key = sample.key,
                };
                self.holder_count += 1;
            }
            const slot = &holder_slots[slot_index];
            if (slot.count > 0 and ticks <= self.at(slot_index, slot.*, slot.count - 1).ticks)
                return error.InvalidObservation;
            self.points(slot_index)[slot.next] = .{ .ticks = ticks, .value = sample.value };
            slot.next = (slot.next + 1) % self.options.series_capacity;
            slot.count = @min(slot.count + 1, self.options.series_capacity);
            if (slot.count < self.options.minimum_samples) continue;
            const first = self.at(slot_index, slot.*, 0);
            const last = self.at(slot_index, slot.*, slot.count - 1);
            const span = @subWithOverflow(last.ticks, first.ticks);
            if (span[1] != 0) return error.InvalidObservation;
            // Windows/.NET timestamps have 100 ns precision; this is a unit
            // conversion, not a policy threshold.
            const seconds = @as(f64, @floatFromInt(span[0])) / 10_000_000.0;
            if (seconds < @as(f64, @floatFromInt(self.options.minimum_seconds))) continue;
            judged[result.judged_count] = .{
                .pid = sample.pid,
                .reserved = 0,
                .birth = sample.birth,
                .key = sample.key,
                .key_length = sample.key_length,
            };
            result.judged_count += 1;
            if (sample.value <= self.options.handle_floor) continue;
            const edge_count = @max(1, @as(usize, slot.count) * self.options.edge_percentage / 100);
            var early_maximum = first.value;
            var late_minimum = last.value;
            for (0..edge_count) |index| {
                early_maximum = @max(early_maximum, self.at(slot_index, slot.*, index).value);
                late_minimum = @min(late_minimum, self.at(slot_index, slot.*, slot.count - 1 - index).value);
            }
            if (late_minimum < early_maximum or late_minimum - early_maximum < self.options.minimum_growth) continue;
            const growth = last.value - first.value;
            suspects[result.suspect_count] = .{
                .pid = sample.pid,
                .sample_count = slot.count,
                .birth = sample.birth,
                .current = last.value,
                .growth = growth,
                .seconds = seconds,
                .rate = @as(f64, @floatFromInt(growth)) / seconds,
                .key = sample.key,
                .key_length = sample.key_length,
            };
            result.suspect_count += 1;
        }
        self.peak_bytes = @max(self.peak_bytes, self.bytes());
        return result;
    }
};

// A per-observation open-addressed input index makes pruning and matching O(n)
// without an allocator or retaining a second sample/current-value authority.
fn find(table: []const u32, samples: []const Sample, pid: i32, key: []const u8) usize {
    var hash: u64 = @bitCast(@as(i64, pid));
    for (key) |byte| hash = (hash ^ byte) *% 1099511628211;
    var position: usize = @intCast(hash & (table.len - 1));
    var probes: usize = 0;
    while (probes < table.len) : (probes += 1) {
        const entry = table[position];
        if (entry == 0) return position;
        const candidate = samples[entry - 1];
        if (candidate.pid == pid and std.mem.eql(u8, candidate.key[0..candidate.key_length], key)) return position;
        position = (position + 1) & (table.len - 1);
    }
    unreachable; // The index is twice the validated maximum input length.
}

pub fn init(memory: []align(8) u8, options: Options) Error!*Series {
    if (!valid(options)) return error.InvalidOptions;
    const required = requiredBytes(options);
    if (memory.len < required) return error.BufferTooSmall;
    @memset(memory[0..required], 0);
    const self: *Series = @ptrCast(memory.ptr);
    self.options = options;
    return self;
}
