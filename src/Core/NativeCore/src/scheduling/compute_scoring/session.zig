const std = @import("std");
const protocol = @import("protocol.zig");
const process_scores = @import("process_scores.zig");
const aggregation = @import("software_aggregation.zig");
const cpu_weights = @import("cpu_weights.zig");

const ProcessWork = struct {
    row: protocol.ProcessInput,
    cpu_score: f64,
};

const GpuWork = struct {
    row: protocol.GpuInput,
    gpu_score: f64,
};

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.Config,
    cpu_capacity_shares: []f64,
    last_scheduling_generation: u64,
    last_process_source_generation: u64,
    last_gpu_source_generation: u64,
    last_gpu_topology_generation: u64,
    last_gpu_topology_fingerprint: u64,
    processes: []ProcessWork,
    gpus: []GpuWork,
    process_identity_order: []u32,
    process_aggregate_order: []u32,
    gpu_identity_order: []u32,
    gpu_aggregate_order: []u32,

    pub fn create(config: *const protocol.Config, raw_cpu_weights: []const f64) !*Session {
        if (!protocol.validateConfig(config)) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const shares = try cpu_weights.compile(allocator, raw_cpu_weights, config.cpu_core_count);
        errdefer allocator.free(shares);
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);
        const process_count: usize = @intCast(config.maximum_process_count);
        const gpu_count: usize = @intCast(config.maximum_gpu_row_count);
        const processes = try allocator.alloc(ProcessWork, process_count);
        errdefer allocator.free(processes);
        const gpus = try allocator.alloc(GpuWork, gpu_count);
        errdefer allocator.free(gpus);
        const process_identity_order = try allocator.alloc(u32, process_count);
        errdefer allocator.free(process_identity_order);
        const process_aggregate_order = try allocator.alloc(u32, process_count);
        errdefer allocator.free(process_aggregate_order);
        const gpu_identity_order = try allocator.alloc(u32, gpu_count);
        errdefer allocator.free(gpu_identity_order);
        const gpu_aggregate_order = try allocator.alloc(u32, gpu_count);
        errdefer allocator.free(gpu_aggregate_order);
        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .cpu_capacity_shares = shares,
            .last_scheduling_generation = 0,
            .last_process_source_generation = 0,
            .last_gpu_source_generation = 0,
            .last_gpu_topology_generation = 0,
            .last_gpu_topology_fingerprint = 0,
            .processes = processes,
            .gpus = gpus,
            .process_identity_order = process_identity_order,
            .process_aggregate_order = process_aggregate_order,
            .gpu_identity_order = gpu_identity_order,
            .gpu_aggregate_order = gpu_aggregate_order,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.gpu_aggregate_order);
        allocator.free(self.gpu_identity_order);
        allocator.free(self.process_aggregate_order);
        allocator.free(self.process_identity_order);
        allocator.free(self.gpus);
        allocator.free(self.processes);
        allocator.free(self.cpu_capacity_shares);
        allocator.destroy(self);
    }

    pub fn reconfigure(self: *Session, config: *const protocol.Config, raw_cpu_weights: []const f64) protocol.Status {
        lock(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validateConfig(config)) return .abi_mismatch;
        if (config.maximum_process_count != self.config.maximum_process_count or
            config.maximum_gpu_row_count != self.config.maximum_gpu_row_count or
            config.maximum_output_count != self.config.maximum_output_count or
            config.cpu_core_count != self.config.cpu_core_count) return .recreate_required;
        if (config.generation <= self.config.generation) return .stale_generation;
        const shares = cpu_weights.compile(self.allocator, raw_cpu_weights, config.cpu_core_count) catch |err| return switch (err) {
            error.InvalidConfiguration => .invalid_argument,
            else => .out_of_memory,
        };
        self.allocator.free(self.cpu_capacity_shares);
        self.cpu_capacity_shares = shares;
        self.config = config.*;
        self.last_scheduling_generation = 0;
        self.last_process_source_generation = 0;
        self.last_gpu_source_generation = 0;
        self.last_gpu_topology_generation = 0;
        self.last_gpu_topology_fingerprint = 0;
        return .ok;
    }

    pub fn weightedCpuUse(self: *Session, rows: []const protocol.CpuCoreInput, output: []f64) protocol.Status {
        lock(&self.mutex);
        defer self.mutex.unlock();
        if (output.len > self.config.maximum_process_count or
            rows.len > @as(u64, self.config.maximum_process_count) * self.config.cpu_core_count)
            return .capacity_exceeded;
        return cpu_weights.calculate(self.cpu_capacity_shares, rows, output);
    }

    pub fn softwareBaseMean(self: *Session, values: []const f64, output: *f64) protocol.Status {
        lock(&self.mutex);
        defer self.mutex.unlock();
        const mean = process_scores.softwareBaseMean(values, self.config.maximum_base_importance) catch
            return .invalid_facts;
        output.* = mean;
        return .ok;
    }

    pub fn capacity(self: *Session) protocol.Capacity {
        lock(&self.mutex);
        defer self.mutex.unlock();
        return .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.Capacity),
            .configuration_generation = self.config.generation,
            .process_input_struct_size = @sizeOf(protocol.ProcessInput),
            .gpu_input_struct_size = @sizeOf(protocol.GpuInput),
            .output_struct_size = @sizeOf(protocol.ScoreOutput),
            .process_capacity = self.config.maximum_process_count,
            .gpu_capacity = self.config.maximum_gpu_row_count,
            .output_capacity = self.config.maximum_output_count,
            .reserved = .{ 0, 0, 0, 0 },
        };
    }
};

pub fn score(
    session: *Session,
    envelope: *const protocol.GenerationEnvelope,
    process_pointer: ?[*]const protocol.ProcessInput,
    process_capacity: u32,
    gpu_pointer: ?[*]const protocol.GpuInput,
    gpu_capacity: u32,
    output_pointer: ?[*]protocol.ScoreOutput,
    output_capacity: u32,
    snapshot: *protocol.Snapshot,
) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();
    if (!validateEnvelope(session, envelope)) return .abi_mismatch;
    if (envelope.scheduling_generation <= session.last_scheduling_generation) return .stale_generation;
    if (envelope.process_count > session.config.maximum_process_count or
        envelope.gpu_row_count > session.config.maximum_gpu_row_count or
        envelope.output_capacity > session.config.maximum_output_count) return .capacity_exceeded;
    if (process_capacity < envelope.process_count or gpu_capacity < envelope.gpu_row_count or
        output_capacity < envelope.output_capacity) return .buffer_too_small;
    if (envelope.process_count > 0 and process_pointer == null) return .invalid_argument;
    if (envelope.gpu_row_count > 0 and gpu_pointer == null) return .invalid_argument;
    if (envelope.output_capacity > 0 and output_pointer == null) return .invalid_argument;

    const cpu_complete = (envelope.flags & protocol.EnvelopeFlags.cpu_snapshot_complete) != 0;
    const gpu_complete = (envelope.flags & protocol.EnvelopeFlags.gpu_snapshot_complete) != 0;
    if (!cpu_complete and !gpu_complete) return .no_data;
    if (cpu_complete and session.cpu_capacity_shares.len == 0) return .no_data;
    if ((envelope.valid_mask & (protocol.EnvelopeValidity.process_generation |
        protocol.EnvelopeValidity.process_observed_at)) !=
        (protocol.EnvelopeValidity.process_generation | protocol.EnvelopeValidity.process_observed_at)) return .invalid_facts;
    if ((envelope.valid_mask & protocol.EnvelopeValidity.welfare_capacity) == 0 or
        envelope.welfare_eligible_process_count > session.config.maximum_process_count)
        return .invalid_facts;
    const gpu_validity = protocol.EnvelopeValidity.gpu_generation |
        protocol.EnvelopeValidity.gpu_observed_at | protocol.EnvelopeValidity.gpu_topology;
    if (gpu_complete) {
        if ((envelope.valid_mask & gpu_validity) != gpu_validity or
            envelope.gpu_source_generation == 0 or envelope.gpu_topology_generation == 0 or
            envelope.gpu_topology_fingerprint == 0 or envelope.gpu_observed_at_milliseconds <= 0)
            return .invalid_facts;
    } else if ((envelope.valid_mask & gpu_validity) != 0 or
        envelope.gpu_source_generation != 0 or envelope.gpu_topology_generation != 0 or
        envelope.gpu_topology_fingerprint != 0 or envelope.gpu_observed_at_milliseconds != 0 or
        envelope.gpu_row_count != 0 or envelope.gpu_adapter_count != 0)
    {
        return .invalid_facts;
    }

    if (envelope.process_source_generation < session.last_process_source_generation)
        return .stale_generation;
    if (gpu_complete) {
        if (envelope.gpu_source_generation < session.last_gpu_source_generation or
            envelope.gpu_topology_generation < session.last_gpu_topology_generation)
        {
            return .stale_generation;
        }
        if (session.last_gpu_topology_generation != 0 and
            envelope.gpu_topology_generation == session.last_gpu_topology_generation and
            envelope.gpu_topology_fingerprint != session.last_gpu_topology_fingerprint)
        {
            return .conflicting_facts;
        }
    }

    const process_count: usize = @intCast(envelope.process_count);
    const gpu_count: usize = @intCast(envelope.gpu_row_count);
    const process_rows = if (process_count == 0) &[_]protocol.ProcessInput{} else (process_pointer.?[0..process_count]);
    const gpu_rows = if (gpu_count == 0) &[_]protocol.GpuInput{} else (gpu_pointer.?[0..gpu_count]);
    const welfare = process_scores.calculateWelfare(&session.config, envelope) catch
        return .invalid_facts;

    var local_eligible_process_count: u32 = 0;
    for (process_rows, 0..) |row, index| {
        const status = validateProcess(session, envelope, &row, cpu_complete, gpu_complete);
        if (status != .ok) return status;
        if (row.runtime_state != @intFromEnum(protocol.RuntimeState.not_running))
            local_eligible_process_count += 1;
        const score_value = if (cpu_complete) process_scores.cpu(
            &session.config,
            &row,
            0,
        ) catch
            return .invalid_facts else 0;
        session.processes[index] = .{ .row = row, .cpu_score = score_value };
        session.process_identity_order[index] = @intCast(index);
        session.process_aggregate_order[index] = @intCast(index);
    }
    std.sort.heap(u32, session.process_identity_order[0..process_count], session, processIdentityBefore);
    std.sort.heap(u32, session.process_aggregate_order[0..process_count], session, processAggregateBefore);
    if (hasDuplicateProcessIdentity(session, process_count)) return .conflicting_facts;
    if (local_eligible_process_count > welfare.eligible_process_count) return .invalid_facts;

    for (gpu_rows, 0..) |row, index| {
        const process_index = findProcess(session, process_count, row.process_id, row.process_start_key) orelse
            return .conflicting_facts;
        const process = &session.processes[process_index].row;
        const status = validateGpu(envelope, process, &row, gpu_complete);
        if (status != .ok) return status;
        const score_value = if (gpu_complete) process_scores.gpu(
            &session.config,
            process,
            &row,
            welfare.share,
        ) catch
            return .invalid_facts else 0;
        session.gpus[index] = .{ .row = row, .gpu_score = score_value };
        session.gpu_identity_order[index] = @intCast(index);
        session.gpu_aggregate_order[index] = @intCast(index);
    }
    std.sort.heap(u32, session.gpu_identity_order[0..gpu_count], session, gpuIdentityBefore);
    std.sort.heap(u32, session.gpu_aggregate_order[0..gpu_count], session, gpuAggregateBefore);
    if (hasDuplicateGpuIdentity(session, gpu_count)) return .conflicting_facts;
    if (gpu_complete and !validateSparseGpuRows(session, envelope, process_count, gpu_count))
        return .conflicting_facts;

    const outputs = output_pointer.?[0..@intCast(envelope.output_capacity)];
    @memset(outputs, std.mem.zeroes(protocol.ScoreOutput));
    var output_count: usize = 0;
    var process_cpu_count: u32 = 0;
    var process_gpu_count: u32 = 0;
    var software_cpu_count: u32 = 0;
    var software_gpu_count: u32 = 0;
    var software_memory_count: u32 = 0;
    if (cpu_complete) {
        if (!emitProcessCpu(session, envelope, outputs, &output_count, &process_cpu_count, welfare.cpu_bonus) or
            !emitSoftwareCpu(session, envelope, outputs, &output_count, &software_cpu_count, welfare.cpu_bonus, .software_cpu))
            return .buffer_too_small;
    }
    if (gpu_complete) {
        if (!emitProcessGpu(session, envelope, outputs, &output_count, &process_gpu_count) or
            !emitSoftwareGpu(session, envelope, outputs, &output_count, &software_gpu_count))
            return .buffer_too_small;
    }
    if (cpu_complete and !emitSoftwareCpu(session, envelope, outputs, &output_count,
        &software_memory_count, welfare.memory_bonus, .software_memory))
        return .buffer_too_small;

    snapshot.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Snapshot),
        .configuration_generation = session.config.generation,
        .scheduling_generation = envelope.scheduling_generation,
        .process_source_generation = envelope.process_source_generation,
        .gpu_source_generation = if (gpu_complete) envelope.gpu_source_generation else 0,
        .gpu_topology_generation = if (gpu_complete) envelope.gpu_topology_generation else 0,
        .gpu_topology_fingerprint = if (gpu_complete) envelope.gpu_topology_fingerprint else 0,
        .flags = (if (cpu_complete) protocol.SnapshotFlags.cpu_scores_valid else 0) |
            (if (gpu_complete) protocol.SnapshotFlags.gpu_scores_valid else 0),
        .process_input_count = envelope.process_count,
        .gpu_input_count = envelope.gpu_row_count,
        .process_cpu_output_count = process_cpu_count,
        .process_gpu_output_count = process_gpu_count,
        .software_cpu_output_count = software_cpu_count,
        .software_gpu_output_count = software_gpu_count,
        .output_count = @intCast(output_count),
        .invalid_fact_count = 0,
        .cpu_free_ratio = welfare.cpu_free_ratio,
        .gpu_free_ratio = welfare.gpu_free_ratio,
        .vram_free_ratio = welfare.vram_free_ratio,
        .memory_free_ratio = welfare.memory_free_ratio,
        .welfare_multiplier = welfare.multiplier,
        .system_pressure = welfare.system_pressure,
        .welfare_budget = welfare.budget,
        .software_base_mean = envelope.software_base_mean,
        .cpu_welfare_multiplier = welfare.cpu_multiplier,
        .memory_welfare_multiplier = welfare.memory_multiplier,
        .cpu_welfare_bonus = welfare.cpu_bonus,
        .memory_welfare_bonus = welfare.memory_bonus,
        .software_memory_output_count = software_memory_count,
        .reserved1 = 0,
        .welfare_share = welfare.share,
        .welfare_eligible_process_count = welfare.eligible_process_count,
        .reserved0 = 0,
        .reserved = 0,
    };
    session.last_scheduling_generation = envelope.scheduling_generation;
    session.last_process_source_generation = envelope.process_source_generation;
    if (gpu_complete) {
        session.last_gpu_source_generation = envelope.gpu_source_generation;
        session.last_gpu_topology_generation = envelope.gpu_topology_generation;
        session.last_gpu_topology_fingerprint = envelope.gpu_topology_fingerprint;
    }
    return .ok;
}

fn validateEnvelope(session: *const Session, value: *const protocol.GenerationEnvelope) bool {
    return value.abi_version == protocol.abi_version and
        value.struct_size == @sizeOf(protocol.GenerationEnvelope) and
        value.process_input_struct_size == @sizeOf(protocol.ProcessInput) and
        value.gpu_input_struct_size == @sizeOf(protocol.GpuInput) and
        value.output_struct_size == @sizeOf(protocol.ScoreOutput) and
        value.reserved0 == 0 and value.configuration_generation == session.config.generation and
        value.scheduling_generation != 0 and value.process_source_generation != 0 and
        value.process_observed_at_milliseconds > 0 and
        (value.valid_mask & ~protocol.EnvelopeValidity.known) == 0 and
        (value.flags & ~protocol.EnvelopeFlags.known) == 0 and value.output_capacity != 0 and
        value.reserved1 == 0;
}

fn validateProcess(
    session: *const Session,
    envelope: *const protocol.GenerationEnvelope,
    row: *const protocol.ProcessInput,
    cpu_complete: bool,
    gpu_complete: bool,
) protocol.Status {
    if (row.struct_size != @sizeOf(protocol.ProcessInput) or
        (row.flags & ~protocol.ProcessFlags.known) != 0 or
        (row.valid_mask & ~protocol.ProcessValidity.known) != 0 or row.target_key == 0 or
        row.software_key == 0 or row.process_id == 0 or row.process_start_key == 0 or
        row.source_generation != envelope.process_source_generation or row.reserved != 0 or
        row.runtime_state >= protocol.runtime_state_count or
        !protocol.finiteNonNegative(row.base_importance) or
        row.base_importance > session.config.maximum_base_importance) return .invalid_facts;
    if ((row.valid_mask & (protocol.ProcessValidity.identity |
        protocol.ProcessValidity.software_identity | protocol.ProcessValidity.runtime_state |
        protocol.ProcessValidity.base_importance | protocol.ProcessValidity.source_generation)) !=
        (protocol.ProcessValidity.identity | protocol.ProcessValidity.software_identity |
            protocol.ProcessValidity.runtime_state | protocol.ProcessValidity.base_importance |
            protocol.ProcessValidity.source_generation)) return .invalid_facts;
    const is_running = row.runtime_state != @intFromEnum(protocol.RuntimeState.not_running);
    if (((row.flags & protocol.ProcessFlags.running) != 0) != is_running)
        return .invalid_facts;
    if (cpu_complete and ((row.flags & protocol.ProcessFlags.cpu_metrics_complete) == 0 or
        (row.valid_mask & protocol.ProcessValidity.cpu_required) != protocol.ProcessValidity.cpu_required))
        return .invalid_facts;
    if (gpu_complete and (row.flags & protocol.ProcessFlags.gpu_metrics_complete) == 0)
        return .invalid_facts;
    for (row.reserved0) |byte| if (byte != 0) return .abi_mismatch;
    return .ok;
}

fn validateGpu(
    envelope: *const protocol.GenerationEnvelope,
    process: *const protocol.ProcessInput,
    row: *const protocol.GpuInput,
    gpu_complete: bool,
) protocol.Status {
    if (row.struct_size != @sizeOf(protocol.GpuInput) or row.flags != 0 or
        (row.valid_mask & ~protocol.GpuValidity.known) != 0 or
        row.target_key != process.target_key or row.software_key != process.software_key or
        row.process_id != process.process_id or row.process_start_key != process.process_start_key or
        row.adapter_key == 0 or row.source_generation != envelope.gpu_source_generation or
        row.reserved[0] != 0 or row.reserved[1] != 0) return .invalid_facts;
    if (gpu_complete and (row.valid_mask & protocol.GpuValidity.required) != protocol.GpuValidity.required)
        return .invalid_facts;
    return .ok;
}

fn hasDuplicateProcessIdentity(session: *const Session, count: usize) bool {
    var index: usize = 1;
    while (index < count) : (index += 1) {
        const left = session.processes[session.process_identity_order[index - 1]].row;
        const right = session.processes[session.process_identity_order[index]].row;
        if (left.process_id == right.process_id and left.process_start_key == right.process_start_key)
            return true;
    }
    return false;
}

fn hasDuplicateGpuIdentity(session: *const Session, count: usize) bool {
    var index: usize = 1;
    while (index < count) : (index += 1) {
        const left = session.gpus[session.gpu_identity_order[index - 1]].row;
        const right = session.gpus[session.gpu_identity_order[index]].row;
        if (left.process_id == right.process_id and left.process_start_key == right.process_start_key and
            left.adapter_key == right.adapter_key) return true;
    }
    return false;
}

fn validateSparseGpuRows(
    session: *const Session,
    envelope: *const protocol.GenerationEnvelope,
    process_count: usize,
    gpu_count: usize,
) bool {
    const maximum = std.math.mul(usize, process_count, @as(usize, @intCast(envelope.gpu_adapter_count))) catch
        return false;
    if (gpu_count > maximum) return false;
    if (process_count == 0) return gpu_count == 0;
    if (envelope.gpu_adapter_count == 0 or gpu_count < process_count) return false;
    var gpu_cursor: usize = 0;
    for (session.process_identity_order[0..process_count]) |process_index| {
        const process = session.processes[process_index].row;
        var adapter_count: u32 = 0;
        var previous_adapter_key: u64 = 0;
        while (gpu_cursor < gpu_count) {
            const gpu = session.gpus[session.gpu_identity_order[gpu_cursor]].row;
            if (gpu.process_id != process.process_id or gpu.process_start_key != process.process_start_key) break;
            if (gpu.adapter_key <= previous_adapter_key) return false;
            previous_adapter_key = gpu.adapter_key;
            adapter_count += 1;
            gpu_cursor += 1;
        }
        if (adapter_count == 0 or adapter_count > envelope.gpu_adapter_count) return false;
    }
    return gpu_cursor == gpu_count;
}

fn findProcess(session: *const Session, count: usize, process_id: u32, start_key: u64) ?usize {
    var low: usize = 0;
    var high = count;
    while (low < high) {
        const middle = low + (high - low) / 2;
        const slot = session.process_identity_order[middle];
        const row = session.processes[slot].row;
        if (row.process_id < process_id or
            (row.process_id == process_id and row.process_start_key < start_key))
        {
            low = middle + 1;
        } else {
            high = middle;
        }
    }
    if (low >= count) return null;
    const slot = session.process_identity_order[low];
    const row = session.processes[slot].row;
    return if (row.process_id == process_id and row.process_start_key == start_key) slot else null;
}

fn emitProcessCpu(
    session: *const Session,
    envelope: *const protocol.GenerationEnvelope,
    outputs: []protocol.ScoreOutput,
    cursor: *usize,
    count: *u32,
    welfare_bonus: f64,
) bool {
    for (session.process_identity_order[0..@intCast(envelope.process_count)]) |slot| {
        if (cursor.* >= outputs.len) return false;
        const work = session.processes[slot];
        const score_value = process_scores.addWelfare(work.cpu_score, work.row.runtime_state, welfare_bonus) catch return false;
        outputs[cursor.*] = processOutput(envelope, &work.row, 0, score_value, .process_cpu);
        cursor.* += 1;
        count.* += 1;
    }
    return true;
}

fn emitProcessGpu(
    session: *const Session,
    envelope: *const protocol.GenerationEnvelope,
    outputs: []protocol.ScoreOutput,
    cursor: *usize,
    count: *u32,
) bool {
    for (session.gpu_identity_order[0..@intCast(envelope.gpu_row_count)]) |slot| {
        if (cursor.* >= outputs.len) return false;
        const work = session.gpus[slot];
        const process_index = findProcess(
            session,
            @intCast(envelope.process_count),
            work.row.process_id,
            work.row.process_start_key,
        ) orelse return false;
        outputs[cursor.*] = processOutput(
            envelope,
            &session.processes[process_index].row,
            work.row.adapter_key,
            work.gpu_score,
            .process_gpu,
        );
        cursor.* += 1;
        count.* += 1;
    }
    return true;
}

fn emitSoftwareCpu(
    session: *const Session,
    envelope: *const protocol.GenerationEnvelope,
    outputs: []protocol.ScoreOutput,
    cursor: *usize,
    count: *u32,
    welfare_bonus: f64,
    kind: protocol.OutputKind,
) bool {
    var index: usize = 0;
    const order = session.process_aggregate_order[0..@intCast(envelope.process_count)];
    while (index < order.len) {
        const software_key = session.processes[order[index]].row.software_key;
        var sum = aggregation.DeterministicSum{};
        var members: u32 = 0;
        var running = false;
        while (index < order.len and session.processes[order[index]].row.software_key == software_key) : (index += 1) {
            const work = session.processes[order[index]];
            if (!sum.add(work.cpu_score)) return false;
            running = running or work.row.runtime_state != @intFromEnum(protocol.RuntimeState.not_running);
            members += 1;
        }
        if (running and !sum.add(welfare_bonus)) return false;
        if (cursor.* >= outputs.len) return false;
        outputs[cursor.*] = softwareOutput(
            envelope,
            software_key,
            0,
            sum.total() orelse return false,
            members,
            kind,
        );
        cursor.* += 1;
        count.* += 1;
    }
    return true;
}

fn emitSoftwareGpu(
    session: *const Session,
    envelope: *const protocol.GenerationEnvelope,
    outputs: []protocol.ScoreOutput,
    cursor: *usize,
    count: *u32,
) bool {
    var index: usize = 0;
    const order = session.gpu_aggregate_order[0..@intCast(envelope.gpu_row_count)];
    while (index < order.len) {
        const first = session.gpus[order[index]].row;
        var sum = aggregation.DeterministicSum{};
        var members: u32 = 0;
        while (index < order.len) : (index += 1) {
            const row = session.gpus[order[index]].row;
            if (row.adapter_key != first.adapter_key or row.software_key != first.software_key) break;
            if (!sum.add(session.gpus[order[index]].gpu_score)) return false;
            members += 1;
        }
        if (cursor.* >= outputs.len) return false;
        outputs[cursor.*] = softwareOutput(
            envelope,
            first.software_key,
            first.adapter_key,
            sum.total() orelse return false,
            members,
            .software_gpu,
        );
        cursor.* += 1;
        count.* += 1;
    }
    return true;
}

fn processOutput(
    envelope: *const protocol.GenerationEnvelope,
    process: *const protocol.ProcessInput,
    adapter_key: u64,
    score_value: f64,
    kind: protocol.OutputKind,
) protocol.ScoreOutput {
    return .{
        .struct_size = @sizeOf(protocol.ScoreOutput),
        .kind = @intFromEnum(kind),
        .runtime_state = process.runtime_state,
        .reserved0 = 0,
        .valid_mask = protocol.OutputValidity.scheduling_generation |
            protocol.OutputValidity.software_identity | protocol.OutputValidity.process_identity |
            protocol.OutputValidity.score |
            (if (adapter_key != 0) protocol.OutputValidity.adapter_identity else 0),
        .scheduling_generation = envelope.scheduling_generation,
        .target_key = process.target_key,
        .software_key = process.software_key,
        .process_start_key = process.process_start_key,
        .adapter_key = adapter_key,
        .score = score_value,
        .source_index = process.source_index,
        .process_id = process.process_id,
        .member_count = 1,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn softwareOutput(
    envelope: *const protocol.GenerationEnvelope,
    software_key: u64,
    adapter_key: u64,
    score_value: f64,
    member_count: u32,
    kind: protocol.OutputKind,
) protocol.ScoreOutput {
    return .{
        .struct_size = @sizeOf(protocol.ScoreOutput),
        .kind = @intFromEnum(kind),
        .runtime_state = @intFromEnum(protocol.RuntimeState.unknown),
        .reserved0 = 0,
        .valid_mask = protocol.OutputValidity.scheduling_generation |
            protocol.OutputValidity.software_identity | protocol.OutputValidity.score |
            protocol.OutputValidity.member_count |
            (if (adapter_key != 0) protocol.OutputValidity.adapter_identity else 0),
        .scheduling_generation = envelope.scheduling_generation,
        .target_key = 0,
        .software_key = software_key,
        .process_start_key = 0,
        .adapter_key = adapter_key,
        .score = score_value,
        .source_index = 0,
        .process_id = 0,
        .member_count = member_count,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn processIdentityBefore(session: *const Session, left_slot: u32, right_slot: u32) bool {
    const left = session.processes[left_slot].row;
    const right = session.processes[right_slot].row;
    if (left.process_id != right.process_id) return left.process_id < right.process_id;
    if (left.process_start_key != right.process_start_key) return left.process_start_key < right.process_start_key;
    if (left.target_key != right.target_key) return left.target_key < right.target_key;
    return left.software_key < right.software_key;
}

fn processAggregateBefore(session: *const Session, left_slot: u32, right_slot: u32) bool {
    const left = session.processes[left_slot].row;
    const right = session.processes[right_slot].row;
    if (left.software_key != right.software_key) return left.software_key < right.software_key;
    if (left.process_id != right.process_id) return left.process_id < right.process_id;
    if (left.process_start_key != right.process_start_key) return left.process_start_key < right.process_start_key;
    return left.target_key < right.target_key;
}

fn gpuIdentityBefore(session: *const Session, left_slot: u32, right_slot: u32) bool {
    const left = session.gpus[left_slot].row;
    const right = session.gpus[right_slot].row;
    if (left.process_id != right.process_id) return left.process_id < right.process_id;
    if (left.process_start_key != right.process_start_key) return left.process_start_key < right.process_start_key;
    return left.adapter_key < right.adapter_key;
}

fn gpuAggregateBefore(session: *const Session, left_slot: u32, right_slot: u32) bool {
    const left = session.gpus[left_slot].row;
    const right = session.gpus[right_slot].row;
    if (left.adapter_key != right.adapter_key) return left.adapter_key < right.adapter_key;
    if (left.software_key != right.software_key) return left.software_key < right.software_key;
    if (left.process_id != right.process_id) return left.process_id < right.process_id;
    if (left.process_start_key != right.process_start_key) return left.process_start_key < right.process_start_key;
    return left.target_key < right.target_key;
}

fn lock(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}
