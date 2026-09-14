const std = @import("std");
const protocol = @import("protocol.zig");
const pressure_policy = @import("pressure_policy.zig");
const base_score_policy = @import("base_score_policy.zig");

const SoftwareWork = struct {
    row: protocol.SoftwareInput,
};

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.Config,
    last_scheduling_generation: u64,
    last_snapshot_generation: u64,
    last_memory_source_workspace_identity: u64,
    last_memory_source_committed_generation: u64,
    software: []SoftwareWork,
    rank_order: []u32,

    pub fn create(config: *const protocol.Config) !*Session {
        if (!protocol.validateConfig(config)) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);
        const software = try allocator.alloc(SoftwareWork, config.maximum_software_count);
        errdefer allocator.free(software);
        const rank_order = try allocator.alloc(u32, config.maximum_software_count);
        errdefer allocator.free(rank_order);
        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .last_scheduling_generation = 0,
            .last_snapshot_generation = 0,
            .last_memory_source_workspace_identity = 0,
            .last_memory_source_committed_generation = 0,
            .software = software,
            .rank_order = rank_order,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.rank_order);
        allocator.free(self.software);
        allocator.destroy(self);
    }

    pub fn reconfigure(self: *Session, config: *const protocol.Config) protocol.Status {
        lock(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validateConfig(config)) return .abi_mismatch;
        if (config.maximum_software_count != self.config.maximum_software_count or
            config.maximum_output_count != self.config.maximum_output_count)
        {
            return .recreate_required;
        }
        if (config.generation <= self.config.generation) return .stale_generation;
        self.config = config.*;
        self.last_scheduling_generation = 0;
        self.last_snapshot_generation = 0;
        self.last_memory_source_workspace_identity = 0;
        self.last_memory_source_committed_generation = 0;
        return .ok;
    }

    pub fn capacity(self: *Session) protocol.Capacity {
        lock(&self.mutex);
        defer self.mutex.unlock();
        return .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.Capacity),
            .configuration_generation = self.config.generation,
            .software_input_struct_size = @sizeOf(protocol.SoftwareInput),
            .software_output_struct_size = @sizeOf(protocol.DesiredSoftwareOutput),
            .software_capacity = self.config.maximum_software_count,
            .output_capacity = self.config.maximum_output_count,
            .reserved = .{ 0, 0, 0, 0 },
        };
    }
};

pub fn plan(
    session: *Session,
    envelope: *const protocol.GenerationEnvelope,
    software_pointer: ?[*]const protocol.SoftwareInput,
    software_capacity: u32,
    output_pointer: ?[*]protocol.DesiredSoftwareOutput,
    output_capacity: u32,
    snapshot: *protocol.Snapshot,
) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();

    if (!validateEnvelope(session, envelope)) return .abi_mismatch;
    const generation_status = validateGeneration(session, envelope);
    if (generation_status != .ok) return generation_status;
    if (envelope.software_count > session.config.maximum_software_count or
        envelope.output_capacity > session.config.maximum_output_count)
    {
        return .capacity_exceeded;
    }
    if (envelope.output_capacity < envelope.software_count or
        software_capacity < envelope.software_count or
        output_capacity < envelope.output_capacity)
    {
        return .buffer_too_small;
    }

    const count: usize = envelope.software_count;
    if (count > 0 and (software_pointer == null or output_pointer == null)) {
        return .invalid_argument;
    }
    const rows = if (count == 0)
        &[_]protocol.SoftwareInput{}
    else
        software_pointer.?[0..count];
    const fact_status = ingestFacts(session, envelope, rows);
    if (fact_status != .ok) return fact_status;

    var empty_outputs: [0]protocol.DesiredSoftwareOutput = .{};
    const outputs = if (count == 0)
        empty_outputs[0..]
    else
        output_pointer.?[0..envelope.output_capacity];
    if (outputs.len > 0) @memset(outputs, std.mem.zeroes(protocol.DesiredSoftwareOutput));

    const policy = pressure_policy.plan(
        envelope.memory_free_ratio_units,
        envelope.software_count,
        .{
            .ratio_units_maximum = session.config.ratio_units_maximum,
            .unrestricted_minimum_free_ratio_units = session.config.unrestricted_minimum_free_ratio_units,
            .normal_minimum_free_ratio_units = session.config.normal_minimum_free_ratio_units,
            .strong_begin_free_ratio_units = session.config.strong_begin_free_ratio_units,
        },
        (envelope.flags & protocol.EnvelopeFlags.allow_unrestricted) != 0,
    );
    const counts = emit(session, envelope, outputs[0..count], policy);

    snapshot.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Snapshot),
        .configuration_generation = session.config.generation,
        .scheduling_generation = envelope.scheduling_generation,
        .memory_source_workspace_identity = envelope.memory_source_workspace_identity,
        .memory_source_committed_generation = envelope.memory_source_committed_generation,
        .snapshot_generation = envelope.snapshot_generation,
        .flags = protocol.SnapshotFlags.full_replacement |
            (if ((envelope.flags & protocol.EnvelopeFlags.allow_unrestricted) != 0)
                protocol.SnapshotFlags.unrestricted_allowed
            else
                0),
        .output_count = envelope.software_count,
        .optimize_count = counts.optimize,
        .strongest_count = counts.strongest,
        .reserved0 = 0,
        .reserved = .{ 0, 0, 0 },
    };

    session.last_scheduling_generation = envelope.scheduling_generation;
    session.last_snapshot_generation = envelope.snapshot_generation;
    session.last_memory_source_workspace_identity = envelope.memory_source_workspace_identity;
    session.last_memory_source_committed_generation = envelope.memory_source_committed_generation;
    return .ok;
}

fn validateEnvelope(session: *Session, envelope: *const protocol.GenerationEnvelope) bool {
    return envelope.abi_version == protocol.abi_version and
        envelope.struct_size == @sizeOf(protocol.GenerationEnvelope) and
        envelope.software_input_struct_size == @sizeOf(protocol.SoftwareInput) and
        envelope.software_output_struct_size == @sizeOf(protocol.DesiredSoftwareOutput) and
        envelope.configuration_generation == session.config.generation and
        envelope.scheduling_generation != 0 and
        envelope.memory_source_workspace_identity != 0 and
        envelope.memory_source_committed_generation != 0 and
        envelope.snapshot_generation != 0 and
        envelope.valid_mask == protocol.EnvelopeValidity.required and
        (envelope.flags & protocol.EnvelopeFlags.required) == protocol.EnvelopeFlags.required and
        (envelope.flags & ~protocol.EnvelopeFlags.known) == 0 and
        protocol.validRatioUnits(
            envelope.memory_free_ratio_units,
            session.config.ratio_units_maximum,
        ) and envelope.reserved0 == 0 and
        allZero(std.mem.asBytes(&envelope.reserved));
}

fn validateGeneration(
    session: *Session,
    envelope: *const protocol.GenerationEnvelope,
) protocol.Status {
    if (envelope.scheduling_generation <= session.last_scheduling_generation or
        envelope.snapshot_generation <= session.last_snapshot_generation or
        envelope.memory_source_workspace_identity <
            session.last_memory_source_workspace_identity or
        (envelope.memory_source_workspace_identity ==
            session.last_memory_source_workspace_identity and
            envelope.memory_source_committed_generation <
                session.last_memory_source_committed_generation))
    {
        return .stale_generation;
    }
    return .ok;
}

fn ingestFacts(
    session: *Session,
    envelope: *const protocol.GenerationEnvelope,
    rows: []const protocol.SoftwareInput,
) protocol.Status {
    var previous_key: u64 = 0;
    for (rows, 0..) |row, index| {
        if (row.struct_size != @sizeOf(protocol.SoftwareInput) or row.flags != 0 or
            row.valid_mask != protocol.SoftwareValidity.required or row.software_key == 0 or
            row.software_key <= previous_key or
            row.scheduling_generation != envelope.scheduling_generation or
            !protocol.validScore(row.cpu_score) or !protocol.validScore(row.base_score) or
            row.source_index != index or row.reserved0 != 0 or row.reserved[0] != 0)
        {
            return .invalid_facts;
        }
        previous_key = row.software_key;
        session.software[index] = .{ .row = row };
    }
    return .ok;
}

const EffectiveCounts = struct {
    optimize: u32 = 0,
    strongest: u32 = 0,
};

fn emit(
    session: *Session,
    envelope: *const protocol.GenerationEnvelope,
    outputs: []protocol.DesiredSoftwareOutput,
    policy: pressure_policy.Plan,
) EffectiveCounts {
    for (outputs, 0..) |*output, index| {
        session.rank_order[index] = @intCast(index);
        const row = session.software[index].row;
        output.* = .{
            .struct_size = @sizeOf(protocol.DesiredSoftwareOutput),
            .mode = @intFromEnum(protocol.MemoryMode.normal),
            .flags = 0,
            .base_score_allowed_grades = base_score_policy.allowedGrades(row.base_score, .{
                .middle_minimum = session.config.middle_tier_minimum_base_score,
                .high_minimum = session.config.high_tier_minimum_base_score,
            }),
            .reserved0 = 0,
            .valid_mask = protocol.DesiredSoftwareValidity.required,
            .software_key = row.software_key,
            .snapshot_generation = envelope.snapshot_generation,
            .scheduling_generation = envelope.scheduling_generation,
            .score = row.cpu_score,
            .base_score = row.base_score,
            .rank = 0,
            .source_index = row.source_index,
        };
    }
    std.sort.heap(u32, session.rank_order[0..outputs.len], session, scoreBefore);
    var counts = EffectiveCounts{};
    for (session.rank_order[0..outputs.len], 0..) |slot, rank| {
        const requested = memoryMode(policy.baseline);
        const decision = base_score_policy.applyAllowed(
            requested,
            outputs[slot].base_score_allowed_grades,
        );
        outputs[slot].rank = @intCast(rank);
        outputs[slot].mode = @intFromEnum(decision.mode);
        if (decision.clamped) outputs[slot].flags |= protocol.DesiredSoftwareFlags.base_score_clamped;
    }

    var strongest_remaining = policy.strongest_count;
    if (strongest_remaining != 0) {
        for (session.rank_order[0..outputs.len]) |slot| {
            if (strongest_remaining == 0) break;
            const decision = base_score_policy.applyAllowed(
                .paged_frozen,
                outputs[slot].base_score_allowed_grades,
            );
            if (decision.mode == .paged_frozen) {
                outputs[slot].mode = @intFromEnum(protocol.MemoryMode.paged_frozen);
                counts.strongest += 1;
                strongest_remaining -= 1;
            } else if (decision.clamped) {
                outputs[slot].flags |= protocol.DesiredSoftwareFlags.base_score_clamped;
            }
        }
    }

    const restricted_target = policy.strongest_count + policy.optimize_count;
    var optimize_remaining = restricted_target - counts.strongest;
    if (optimize_remaining != 0) {
        for (session.rank_order[0..outputs.len]) |slot| {
            if (optimize_remaining == 0) break;
            if (outputs[slot].mode == @intFromEnum(protocol.MemoryMode.paged_frozen)) continue;
            const decision = base_score_policy.applyAllowed(
                .optimize,
                outputs[slot].base_score_allowed_grades,
            );
            if (decision.mode == .optimize) {
                outputs[slot].mode = @intFromEnum(protocol.MemoryMode.optimize);
                counts.optimize += 1;
                optimize_remaining -= 1;
            } else if (decision.clamped) {
                outputs[slot].flags |= protocol.DesiredSoftwareFlags.base_score_clamped;
            }
        }
    }
    return counts;
}

fn scoreBefore(session: *Session, left: u32, right: u32) bool {
    const a = session.software[left].row;
    const b = session.software[right].row;
    return a.cpu_score < b.cpu_score or
        (a.cpu_score == b.cpu_score and a.software_key < b.software_key);
}

fn memoryMode(intensity: pressure_policy.Intensity) protocol.MemoryMode {
    return switch (intensity) {
        .unrestricted => .unrestricted,
        .normal => .normal,
        .optimize => .optimize,
        .strongest => .paged_frozen,
    };
}

fn allZero(bytes: []const u8) bool {
    for (bytes) |byte| if (byte != 0) return false;
    return true;
}

fn lock(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}
