const std = @import("std");
const protocol = @import("protocol.zig");
const process_scores = @import("process_scores.zig");
const aggregation = @import("software_aggregation.zig");
const cpu_weights = @import("cpu_weights.zig");

const scratch = @import("scratch.zig");

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.Config,
    cpu_capacity_shares: []f64,

    pub fn create(config: *const protocol.Config, raw_cpu_weights: []const f64) !*Session {
        if (!protocol.validateConfig(config)) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const shares = try cpu_weights.compile(allocator, raw_cpu_weights, config.cpu_core_count);
        errdefer allocator.free(shares);
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);
        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .cpu_capacity_shares = shares,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.cpu_capacity_shares);
        allocator.destroy(self);
    }

    pub fn weightedCpuUse(self: *Session, rows: []const protocol.CpuCoreInput, output: []f64) protocol.Status {
        lock(&self.mutex);
        defer self.mutex.unlock();
        if (output.len > self.config.maximum_process_count or
            rows.len > @as(u64, self.config.maximum_process_count) * self.config.cpu_core_count)
            return .capacity_exceeded;
        return cpu_weights.calculate(self.cpu_capacity_shares, rows, output);
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
            .scratch_byte_count = scratch.Layout.init(&self.config).byte_count,
            .scratch_alignment = scratch.alignment,
            .reserved0 = 0,
            .reserved = .{ 0, 0 },
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
    scratch_pointer: ?*anyopaque,
    scratch_length: u64,
    snapshot: *protocol.Snapshot,
) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();
    if (!validateEnvelope(envelope)) return .abi_mismatch;
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
    if ((envelope.valid_mask & protocol.EnvelopeValidity.welfare_capacity) == 0 or
        envelope.welfare_eligible_process_count > session.config.maximum_process_count)
        return .invalid_facts;
    if (!gpu_complete and (envelope.gpu_row_count != 0 or envelope.gpu_adapter_count != 0))
        return .invalid_facts;

    const process_count: usize = @intCast(envelope.process_count);
    const gpu_count: usize = @intCast(envelope.gpu_row_count);
    const process_rows = if (process_count == 0) &[_]protocol.ProcessInput{} else (process_pointer.?[0..process_count]);
    const gpu_rows = if (gpu_count == 0) &[_]protocol.GpuInput{} else (gpu_pointer.?[0..gpu_count]);
    const welfare = process_scores.calculateWelfare(&session.config, envelope) catch
        return .invalid_facts;

    if (scratch_length < scratch.Layout.init(&session.config).byte_count) return .buffer_too_small;
    const work = scratch.View.init(&session.config, scratch_pointer, scratch_length) orelse return .invalid_argument;
    if (!scratch.disjoint(scratch_pointer.?, scratch_length, process_pointer, envelope.process_count, protocol.ProcessInput) or
        !scratch.disjoint(scratch_pointer.?, scratch_length, gpu_pointer, envelope.gpu_row_count, protocol.GpuInput) or
        !scratch.disjoint(scratch_pointer.?, scratch_length, output_pointer, envelope.output_capacity, protocol.ScoreOutput) or
        !scratch.disjoint(scratch_pointer.?, scratch_length, @as(?*const protocol.GenerationEnvelope, envelope), 1, protocol.GenerationEnvelope) or
        !scratch.disjoint(scratch_pointer.?, scratch_length, @as(?*protocol.Snapshot, snapshot), 1, protocol.Snapshot)) return .invalid_argument;

    // Keep identity ordering over every submitted row until ambiguity is removed.
    // An invalid duplicate must not make its otherwise valid twin authoritative.
    var invalid_fact_count: u32 = 0;
    for (process_rows, 0..) |row, index| {
        if (row.struct_size != @sizeOf(protocol.ProcessInput)) return .abi_mismatch;
        work.processes[index] = .{ .row = row, .cpu_score = 0 };
        work.process_identity_order[index] = @intCast(index);
    }
    std.sort.heap(u32, work.process_identity_order[0..process_count], &work, processIdentityBefore);
    var accepted_process_count: usize = 0;
    var cursor: usize = 0;
    while (cursor < process_count) {
        const start = cursor;
        const first = work.processes[work.process_identity_order[start]].row;
        cursor += 1;
        while (cursor < process_count) : (cursor += 1) {
            const next = work.processes[work.process_identity_order[cursor]].row;
            if (first.process_id != next.process_id or first.process_start_key != next.process_start_key) break;
        }
        if (cursor - start != 1) {
            invalid_fact_count += @intCast(cursor - start);
            continue;
        }
        const slot = work.process_identity_order[start];
        const process = &work.processes[slot];
        if (validateProcess(session, &process.row, false, false) != .ok) {
            invalid_fact_count += 1;
            continue;
        }
        if (cpu_complete) {
            if (validateProcess(session, &process.row, true, false) == .ok) {
                process.cpu_score = process_scores.cpu(&session.config, &process.row, 0) catch blk: {
                    invalid_fact_count += 1;
                    process.row.flags &= ~protocol.ProcessFlags.cpu_metrics_complete;
                    break :blk 0;
                };
            } else {
                process.row.flags &= ~protocol.ProcessFlags.cpu_metrics_complete;
                invalid_fact_count += 1;
            }
        }
        work.process_identity_order[accepted_process_count] = slot;
        work.process_aggregate_order[accepted_process_count] = slot;
        accepted_process_count += 1;
    }
    var local_eligible_process_count: u32 = 0;
    for (work.process_identity_order[0..accepted_process_count]) |slot| {
        if (work.processes[slot].row.runtime_state != @intFromEnum(protocol.RuntimeState.not_running))
            local_eligible_process_count += 1;
    }
    if (local_eligible_process_count > welfare.eligible_process_count) return .invalid_facts;
    std.sort.heap(u32, work.process_aggregate_order[0..accepted_process_count], &work, processAggregateBefore);

    for (gpu_rows, 0..) |row, index| {
        if (row.struct_size != @sizeOf(protocol.GpuInput)) return .abi_mismatch;
        work.gpus[index] = .{ .row = row, .gpu_score = 0 };
        work.gpu_identity_order[index] = @intCast(index);
    }
    std.sort.heap(u32, work.gpu_identity_order[0..gpu_count], &work, gpuIdentityBefore);
    var accepted_gpu_count: usize = 0;
    cursor = 0;
    while (cursor < gpu_count) {
        const start = cursor;
        const first = work.gpus[work.gpu_identity_order[start]].row;
        cursor += 1;
        while (cursor < gpu_count) : (cursor += 1) {
            const next = work.gpus[work.gpu_identity_order[cursor]].row;
            if (first.process_id != next.process_id or first.process_start_key != next.process_start_key or
                first.adapter_key != next.adapter_key) break;
        }
        if (cursor - start != 1) {
            invalid_fact_count += @intCast(cursor - start);
            continue;
        }
        const slot = work.gpu_identity_order[start];
        const gpu = &work.gpus[slot];
        const process_index = findProcess(&work, accepted_process_count, first.process_id, first.process_start_key) orelse {
            invalid_fact_count += 1;
            continue;
        };
        const process = &work.processes[process_index].row;
        if ((process.flags & protocol.ProcessFlags.gpu_metrics_complete) == 0 or
            validateGpu(process, &gpu.row, gpu_complete) != .ok or envelope.gpu_adapter_count == 0)
        {
            invalid_fact_count += 1;
            continue;
        }
        gpu.gpu_score = process_scores.gpu(&session.config, process, &gpu.row, welfare.share) catch {
            invalid_fact_count += 1;
            continue;
        };
        work.gpu_identity_order[accepted_gpu_count] = slot;
        work.gpu_aggregate_order[accepted_gpu_count] = slot;
        accepted_gpu_count += 1;
    }
    // Adapter-count accounting belongs to each process, not the whole GPU domain.
    var accounted_gpu_count: usize = 0;
    cursor = 0;
    while (cursor < accepted_gpu_count) {
        const start = cursor;
        const first = work.gpus[work.gpu_identity_order[start]].row;
        cursor += 1;
        while (cursor < accepted_gpu_count) : (cursor += 1) {
            const next = work.gpus[work.gpu_identity_order[cursor]].row;
            if (first.process_id != next.process_id or first.process_start_key != next.process_start_key) break;
        }
        if (cursor - start > envelope.gpu_adapter_count) {
            invalid_fact_count += @intCast(cursor - start);
            continue;
        }
        for (start..cursor) |index| {
            const slot = work.gpu_identity_order[index];
            work.gpu_identity_order[accounted_gpu_count] = slot;
            work.gpu_aggregate_order[accounted_gpu_count] = slot;
            accounted_gpu_count += 1;
        }
    }
    accepted_gpu_count = accounted_gpu_count;
    std.sort.heap(u32, work.gpu_aggregate_order[0..accepted_gpu_count], &work, gpuAggregateBefore);
    var accepted = envelope.*;
    accepted.process_count = @intCast(accepted_process_count);
    accepted.gpu_row_count = @intCast(accepted_gpu_count);

    const outputs = output_pointer.?[0..@intCast(envelope.output_capacity)];
    @memset(outputs, std.mem.zeroes(protocol.ScoreOutput));
    var output_count: usize = 0;
    var process_cpu_count: u32 = 0;
    var process_gpu_count: u32 = 0;
    var software_cpu_count: u32 = 0;
    var software_gpu_count: u32 = 0;
    var software_memory_count: u32 = 0;
    if (cpu_complete) {
        if (!emitProcessCpu(&work, &accepted, outputs, &output_count, &process_cpu_count, welfare.cpu_bonus) or
            !emitSoftwareCpu(&work, &accepted, outputs, &output_count, &software_cpu_count, welfare.cpu_bonus, .software_cpu))
            return .buffer_too_small;
    }
    if (gpu_complete) {
        if (!emitProcessGpu(&work, &accepted, outputs, &output_count, &process_gpu_count) or
            !emitSoftwareGpu(&work, &accepted, outputs, &output_count, &software_gpu_count))
            return .buffer_too_small;
    }
    if (cpu_complete and !emitSoftwareCpu(&work, &accepted, outputs, &output_count, &software_memory_count, welfare.memory_bonus, .software_memory))
        return .buffer_too_small;

    snapshot.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Snapshot),
        .round_number = envelope.round_number,
        .flags = (if (cpu_complete) protocol.SnapshotFlags.cpu_scores_valid else 0) |
            (if (gpu_complete) protocol.SnapshotFlags.gpu_scores_valid else 0),
        .process_input_count = envelope.process_count,
        .gpu_input_count = envelope.gpu_row_count,
        .process_cpu_output_count = process_cpu_count,
        .process_gpu_output_count = process_gpu_count,
        .software_cpu_output_count = software_cpu_count,
        .software_gpu_output_count = software_gpu_count,
        .output_count = @intCast(output_count),
        .invalid_fact_count = invalid_fact_count,
        .cpu_free_ratio = welfare.cpu_free_ratio,
        .gpu_free_ratio = welfare.gpu_free_ratio,
        .vram_free_ratio = welfare.vram_free_ratio,
        .memory_free_ratio = welfare.memory_free_ratio,
        .welfare_multiplier = welfare.multiplier,
        .system_pressure = welfare.system_pressure,
        .welfare_budget = welfare.budget,
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
    return .ok;
}

fn validateEnvelope(value: *const protocol.GenerationEnvelope) bool {
    return value.abi_version == protocol.abi_version and
        value.struct_size == @sizeOf(protocol.GenerationEnvelope) and
        value.process_input_struct_size == @sizeOf(protocol.ProcessInput) and
        value.gpu_input_struct_size == @sizeOf(protocol.GpuInput) and
        value.output_struct_size == @sizeOf(protocol.ScoreOutput) and
        value.reserved0 == 0 and value.round_number != 0 and
        (value.valid_mask & ~protocol.EnvelopeValidity.known) == 0 and
        (value.flags & ~protocol.EnvelopeFlags.known) == 0 and value.output_capacity != 0 and
        value.reserved1 == 0;
}

fn validateProcess(
    session: *const Session,
    row: *const protocol.ProcessInput,
    cpu_complete: bool,
    gpu_complete: bool,
) protocol.Status {
    if (row.struct_size != @sizeOf(protocol.ProcessInput) or
        (row.flags & ~protocol.ProcessFlags.known) != 0 or
        (row.valid_mask & ~protocol.ProcessValidity.known) != 0 or row.target_key == 0 or
        row.software_key == 0 or row.process_id == 0 or row.process_start_key == 0 or
        row.reserved != 0 or
        row.runtime_state == @intFromEnum(protocol.RuntimeState.unknown) or
        row.runtime_state >= protocol.runtime_state_count or
        !protocol.finiteNonNegative(row.base_importance) or
        row.base_importance > session.config.maximum_base_importance) return .invalid_facts;
    if ((row.valid_mask & (protocol.ProcessValidity.identity |
        protocol.ProcessValidity.software_identity | protocol.ProcessValidity.runtime_state |
        protocol.ProcessValidity.base_importance)) !=
        (protocol.ProcessValidity.identity | protocol.ProcessValidity.software_identity |
            protocol.ProcessValidity.runtime_state | protocol.ProcessValidity.base_importance)) return .invalid_facts;
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
    process: *const protocol.ProcessInput,
    row: *const protocol.GpuInput,
    gpu_complete: bool,
) protocol.Status {
    if (row.struct_size != @sizeOf(protocol.GpuInput) or row.flags != 0 or
        (row.valid_mask & ~protocol.GpuValidity.known) != 0 or
        row.target_key != process.target_key or row.software_key != process.software_key or
        row.process_id != process.process_id or row.process_start_key != process.process_start_key or
        row.adapter_key == 0 or
        row.reserved[0] != 0 or row.reserved[1] != 0) return .invalid_facts;
    if (gpu_complete and (row.valid_mask & protocol.GpuValidity.required) != protocol.GpuValidity.required)
        return .invalid_facts;
    return .ok;
}

fn findProcess(workspace: *const scratch.View, count: usize, process_id: u32, start_key: u64) ?usize {
    var low: usize = 0;
    var high = count;
    while (low < high) {
        const middle = low + (high - low) / 2;
        const slot = workspace.process_identity_order[middle];
        const row = workspace.processes[slot].row;
        if (row.process_id < process_id or
            (row.process_id == process_id and row.process_start_key < start_key))
        {
            low = middle + 1;
        } else {
            high = middle;
        }
    }
    if (low >= count) return null;
    const slot = workspace.process_identity_order[low];
    const row = workspace.processes[slot].row;
    return if (row.process_id == process_id and row.process_start_key == start_key) slot else null;
}

fn emitProcessCpu(
    workspace: *const scratch.View,
    envelope: *const protocol.GenerationEnvelope,
    outputs: []protocol.ScoreOutput,
    cursor: *usize,
    count: *u32,
    welfare_bonus: f64,
) bool {
    for (workspace.process_identity_order[0..@intCast(envelope.process_count)]) |slot| {
        if (cursor.* >= outputs.len) return false;
        const work = workspace.processes[slot];
        if ((work.row.flags & protocol.ProcessFlags.cpu_metrics_complete) == 0) continue;
        const score_value = process_scores.addWelfare(work.cpu_score, work.row.runtime_state, welfare_bonus) catch return false;
        outputs[cursor.*] = processOutput(envelope, &work.row, 0, score_value, .process_cpu);
        cursor.* += 1;
        count.* += 1;
    }
    return true;
}

fn emitProcessGpu(
    workspace: *const scratch.View,
    envelope: *const protocol.GenerationEnvelope,
    outputs: []protocol.ScoreOutput,
    cursor: *usize,
    count: *u32,
) bool {
    for (workspace.gpu_identity_order[0..@intCast(envelope.gpu_row_count)]) |slot| {
        if (cursor.* >= outputs.len) return false;
        const work = workspace.gpus[slot];
        const process_index = findProcess(
            workspace,
            @intCast(envelope.process_count),
            work.row.process_id,
            work.row.process_start_key,
        ) orelse return false;
        outputs[cursor.*] = processOutput(
            envelope,
            &workspace.processes[process_index].row,
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
    workspace: *const scratch.View,
    envelope: *const protocol.GenerationEnvelope,
    outputs: []protocol.ScoreOutput,
    cursor: *usize,
    count: *u32,
    welfare_bonus: f64,
    kind: protocol.OutputKind,
) bool {
    var index: usize = 0;
    const order = workspace.process_aggregate_order[0..@intCast(envelope.process_count)];
    while (index < order.len) {
        const software_key = workspace.processes[order[index]].row.software_key;
        var sum = aggregation.DeterministicSum{};
        var members: u32 = 0;
        var running = false;
        while (index < order.len and workspace.processes[order[index]].row.software_key == software_key) : (index += 1) {
            const work = workspace.processes[order[index]];
            if ((work.row.flags & protocol.ProcessFlags.cpu_metrics_complete) == 0) continue;
            if (!sum.add(work.cpu_score)) return false;
            running = running or work.row.runtime_state != @intFromEnum(protocol.RuntimeState.not_running);
            members += 1;
        }
        if (members == 0) continue;
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
    workspace: *const scratch.View,
    envelope: *const protocol.GenerationEnvelope,
    outputs: []protocol.ScoreOutput,
    cursor: *usize,
    count: *u32,
) bool {
    var index: usize = 0;
    const order = workspace.gpu_aggregate_order[0..@intCast(envelope.gpu_row_count)];
    while (index < order.len) {
        const first = workspace.gpus[order[index]].row;
        var sum = aggregation.DeterministicSum{};
        var members: u32 = 0;
        while (index < order.len) : (index += 1) {
            const row = workspace.gpus[order[index]].row;
            if (row.adapter_key != first.adapter_key or row.software_key != first.software_key) break;
            if (!sum.add(workspace.gpus[order[index]].gpu_score)) return false;
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
        .valid_mask = protocol.OutputValidity.round_number |
            protocol.OutputValidity.software_identity | protocol.OutputValidity.process_identity |
            protocol.OutputValidity.score |
            (if (adapter_key != 0) protocol.OutputValidity.adapter_identity else 0),
        .round_number = envelope.round_number,
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
        .valid_mask = protocol.OutputValidity.round_number |
            protocol.OutputValidity.software_identity | protocol.OutputValidity.score |
            protocol.OutputValidity.member_count |
            (if (adapter_key != 0) protocol.OutputValidity.adapter_identity else 0),
        .round_number = envelope.round_number,
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

fn processIdentityBefore(workspace: *const scratch.View, left_slot: u32, right_slot: u32) bool {
    const left = workspace.processes[left_slot].row;
    const right = workspace.processes[right_slot].row;
    if (left.process_id != right.process_id) return left.process_id < right.process_id;
    if (left.process_start_key != right.process_start_key) return left.process_start_key < right.process_start_key;
    if (left.target_key != right.target_key) return left.target_key < right.target_key;
    return left.software_key < right.software_key;
}

fn processAggregateBefore(workspace: *const scratch.View, left_slot: u32, right_slot: u32) bool {
    const left = workspace.processes[left_slot].row;
    const right = workspace.processes[right_slot].row;
    if (left.software_key != right.software_key) return left.software_key < right.software_key;
    if (left.process_id != right.process_id) return left.process_id < right.process_id;
    if (left.process_start_key != right.process_start_key) return left.process_start_key < right.process_start_key;
    return left.target_key < right.target_key;
}

fn gpuIdentityBefore(workspace: *const scratch.View, left_slot: u32, right_slot: u32) bool {
    const left = workspace.gpus[left_slot].row;
    const right = workspace.gpus[right_slot].row;
    if (left.process_id != right.process_id) return left.process_id < right.process_id;
    if (left.process_start_key != right.process_start_key) return left.process_start_key < right.process_start_key;
    return left.adapter_key < right.adapter_key;
}

fn gpuAggregateBefore(workspace: *const scratch.View, left_slot: u32, right_slot: u32) bool {
    const left = workspace.gpus[left_slot].row;
    const right = workspace.gpus[right_slot].row;
    if (left.adapter_key != right.adapter_key) return left.adapter_key < right.adapter_key;
    if (left.software_key != right.software_key) return left.software_key < right.software_key;
    if (left.process_id != right.process_id) return left.process_id < right.process_id;
    if (left.process_start_key != right.process_start_key) return left.process_start_key < right.process_start_key;
    return left.target_key < right.target_key;
}

fn lock(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}
