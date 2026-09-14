const std = @import("std");
const ResultCode = @import("../common/result_codes.zig").ResultCode;

pub const abi_version: u32 = 0x0001_0004;
const empty_index = std.math.maxInt(u32);

pub const Mode = enum(u32) {
    normal = 0,
    emergency = 1,
};

pub const InputFlags = struct {
    pub const can_apply: u32 = 1 << 0;
};

pub const RequestFlags = struct {
    pub const allow_normal: u32 = 1 << 0;
    pub const evaluate_emergency: u32 = 1 << 1;
    pub const force_emergency: u32 = 1 << 2;
    pub const all: u32 = allow_normal | evaluate_emergency | force_emergency;
};

pub const FeedbackFlags = struct {
    pub const attempted: u32 = 1 << 0;
    pub const succeeded: u32 = 1 << 1;
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    state_capacity: u32,
    reserved0: u32,
    critical_free_ratio: f64,
    very_low_free_ratio: f64,
    low_free_ratio: f64,
    guarded_free_ratio: f64,
    physical_emergency_free_ratio: f64,
    virtual_emergency_free_ratio: f64,
    high_tier_minimum_base_score: f64,
    critical_batch_count: u32,
    very_low_batch_count: u32,
    low_batch_count: u32,
    guarded_batch_count: u32,
    emergency_batch_count: u32,
    reserved1: u32,
};

pub const PlanHeader = extern struct {
    abi_version: u32,
    struct_size: u32,
    input_struct_size: u32,
    output_struct_size: u32,
    input_count: u32,
    output_capacity: u32,
    output_count: u32,
    request_flags: u32,
    ordinary_memory_free_ratio: f64,
    physical_memory_free_ratio: f64,
    virtual_memory_free_ratio: f64,
    config_generation: u64,
    state_revision: u64,
};

pub const CandidateInput = extern struct {
    struct_size: u32,
    flags: u32,
    process_id: u32,
    runtime_state: u32,
    process_start_key: u64,
    target_key: u64,
    base_score: f64,
    cpu_score: f64,
    memory_used_percent: f64,
};

pub const DecisionOutput = extern struct {
    struct_size: u32,
    source_input_index: u32,
    process_id: u32,
    state_slot: u32,
    state_generation: u32,
    mode: u32,
    reserved0: u32,
    reserved1: u32,
    process_start_key: u64,
    target_key: u64,
};

pub const FeedbackInput = extern struct {
    struct_size: u32,
    state_slot: u32,
    state_generation: u32,
    flags: u32,
};

const StateSlot = struct {
    process_id: u32 = 0,
    generation: u32 = 0,
    process_start_key: u64 = 0,
};

const Candidate = struct {
    source_input_index: u32 = 0,
    process_id: u32 = 0,
    runtime_state: u32 = 0,
    state_slot: u32 = empty_index,
    process_start_key: u64 = 0,
    target_key: u64 = 0,
    base_score: f64 = 0,
    cpu_score: f64 = 0,
    memory_used_percent: f64 = 0,
    identity_conflict: bool = false,
};

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: Config,
    states: []StateSlot,
    state_index: []u32,
    candidates: []Candidate,
    candidate_index: []u32,
    selected_indices: []u32,
    state_revision: u64 = 1,
    next_generation: u32 = 1,
    in_flight_count: u32 = 0,

    pub fn create(config: *const Config) !*Session {
        if (!validConfig(config)) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);
        const capacity: usize = @intCast(config.state_capacity);
        const index_capacity = try indexCapacity(capacity);
        const states = try allocator.alloc(StateSlot, capacity);
        errdefer allocator.free(states);
        const state_index = try allocator.alloc(u32, index_capacity);
        errdefer allocator.free(state_index);
        const candidates = try allocator.alloc(Candidate, capacity);
        errdefer allocator.free(candidates);
        const candidate_index = try allocator.alloc(u32, index_capacity);
        errdefer allocator.free(candidate_index);
        const selected_indices = try allocator.alloc(u32, capacity);
        @memset(states, StateSlot{});
        @memset(state_index, empty_index);
        @memset(candidates, Candidate{});
        @memset(candidate_index, empty_index);
        @memset(selected_indices, empty_index);
        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .states = states,
            .state_index = state_index,
            .candidates = candidates,
            .candidate_index = candidate_index,
            .selected_indices = selected_indices,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.selected_indices);
        allocator.free(self.candidate_index);
        allocator.free(self.candidates);
        allocator.free(self.state_index);
        allocator.free(self.states);
        allocator.destroy(self);
    }

    pub fn reconfigure(self: *Session, config: *const Config) ResultCode {
        if (!validConfig(config)) return .abi_mismatch;
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (config.state_capacity != self.config.state_capacity) return .invalid_argument;
        if (self.in_flight_count != 0) return .invalid_argument;
        self.config = config.*;
        return .ok;
    }

    pub fn reset(self: *Session) void {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        @memset(self.states, StateSlot{});
        @memset(self.state_index, empty_index);
        @memset(self.candidates, Candidate{});
        @memset(self.candidate_index, empty_index);
        @memset(self.selected_indices, empty_index);
        self.in_flight_count = 0;
        self.bumpRevision();
    }

    pub fn plan(
        self: *Session,
        header: *PlanHeader,
        input_pointer: ?[*]const CandidateInput,
        input_capacity: u32,
        output_pointer: ?[*]DecisionOutput,
        output_capacity: u32,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!validPlanHeader(self, header)) return .abi_mismatch;
        if (input_capacity < header.input_count or output_capacity < header.output_capacity) return .buffer_too_small;
        const inputs = inputSlice(input_pointer, header.input_count) orelse return .invalid_argument;
        const outputs = outputSlice(output_pointer, header.output_capacity) orelse return .invalid_argument;
        header.output_count = 0;
        const mode = resolveMode(self.config, header) orelse {
            header.state_revision = self.state_revision;
            return .ok;
        };

        self.rebuildStateIndex() catch return .out_of_memory;
        @memset(self.candidate_index, empty_index);
        var candidate_count: usize = 0;
        for (inputs, 0..) |input, input_index| {
            if (!validInput(input)) return .abi_mismatch;
            if (!eligible(self.config, input)) continue;
            const existing_state = self.findState(input.process_id, input.process_start_key);
            if (existing_state != null) continue;

            const candidate_slot = self.findOrCreateCandidate(
                input.process_id,
                &candidate_count,
            ) orelse return .buffer_too_small;
            var current = &self.candidates[@intCast(candidate_slot)];
            if (current.process_id != 0 and current.process_start_key != input.process_start_key) {
                current.identity_conflict = true;
                continue;
            }
            const next = Candidate{
                .source_input_index = @intCast(input_index),
                .process_id = input.process_id,
                .runtime_state = input.runtime_state,
                .state_slot = existing_state orelse empty_index,
                .process_start_key = input.process_start_key,
                .target_key = input.target_key,
                .base_score = input.base_score,
                .cpu_score = input.cpu_score,
                .memory_used_percent = input.memory_used_percent,
            };
            if (current.process_id == 0 or candidateLess(next, current.*, mode)) {
                current.* = next;
            }
        }

        const desired_count = @min(resolveBatchCount(
            self.config,
            mode,
            sanitizeRatio(header.ordinary_memory_free_ratio, 1),
        ), header.output_capacity);
        if (desired_count == 0 or candidate_count == 0) {
            header.state_revision = self.state_revision;
            return .ok;
        }

        var selected_count: usize = 0;
        for (self.candidates[0..candidate_count], 0..) |candidate, candidate_index| {
            if (candidate.process_id == 0 or candidate.identity_conflict) continue;
            insertSelected(
                self.candidates,
                self.selected_indices,
                &selected_count,
                @intCast(candidate_index),
                desired_count,
                mode,
            );
        }

        var free_state_count: usize = 0;
        for (self.states) |state| {
            if (state.process_id == 0) free_state_count += 1;
        }
        var new_state_count: usize = 0;
        for (self.selected_indices[0..selected_count]) |candidate_index| {
            if (self.candidates[candidate_index].state_slot == empty_index) new_state_count += 1;
        }
        if (new_state_count > free_state_count) return .buffer_too_small;

        for (self.selected_indices[0..selected_count], 0..) |candidate_index, output_index| {
            var candidate = &self.candidates[candidate_index];
            if (candidate.state_slot == empty_index) {
                candidate.state_slot = self.allocateState(candidate.process_id, candidate.process_start_key) orelse return .buffer_too_small;
            }
            const state = &self.states[candidate.state_slot];
            outputs[output_index] = .{
                .struct_size = @sizeOf(DecisionOutput),
                .source_input_index = candidate.source_input_index,
                .process_id = candidate.process_id,
                .state_slot = candidate.state_slot,
                .state_generation = state.generation,
                .mode = @intFromEnum(mode),
                .reserved0 = 0,
                .reserved1 = 0,
                .process_start_key = candidate.process_start_key,
                .target_key = candidate.target_key,
            };
        }
        self.bumpRevision();
        self.in_flight_count += @intCast(selected_count);
        header.output_count = @intCast(selected_count);
        header.state_revision = self.state_revision;
        return .ok;
    }

    pub fn requiredOutputCapacity(
        self: *Session,
        header: *const PlanHeader,
        input_capacity: u32,
        output_capacity: *u32,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        output_capacity.* = 0;
        if (!validPlanHeader(self, header)) return .abi_mismatch;
        if (input_capacity < header.input_count) return .buffer_too_small;
        const mode = resolveMode(self.config, header) orelse return .ok;
        output_capacity.* = @min(
            header.input_count,
            resolveBatchCount(
                self.config,
                mode,
                sanitizeRatio(header.ordinary_memory_free_ratio, 1),
            ),
        );
        return .ok;
    }

    pub fn complete(
        self: *Session,
        feedback_pointer: ?[*]const FeedbackInput,
        feedback_count: u32,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        const feedbacks = feedbackSlice(feedback_pointer, feedback_count) orelse return .invalid_argument;
        for (feedbacks) |feedback| {
            if (!validFeedback(self, feedback)) return .invalid_argument;
        }
        for (feedbacks, 0..) |feedback, feedback_index| {
            for (feedbacks[0..feedback_index]) |previous| {
                if (previous.state_slot == feedback.state_slot) return .invalid_argument;
            }
        }
        for (feedbacks) |feedback| self.states[@intCast(feedback.state_slot)] = StateSlot{};
        self.in_flight_count -= @intCast(feedbacks.len);
        if (feedbacks.len > 0) self.bumpRevision();
        return .ok;
    }

    fn rebuildStateIndex(self: *Session) !void {
        @memset(self.state_index, empty_index);
        for (self.states, 0..) |*state, state_index| {
            if (state.process_id == 0) continue;
            try insertIndex(
                self.state_index,
                processHash(state.process_id, state.process_start_key),
                @intCast(state_index),
            );
        }
    }

    fn findState(self: *const Session, process_id: u32, process_start_key: u64) ?u32 {
        if (self.state_index.len == 0) return null;
        var index: usize = @intCast(processHash(process_id, process_start_key) % self.state_index.len);
        var probes: usize = 0;
        while (probes < self.state_index.len) : (probes += 1) {
            const state_index = self.state_index[index];
            if (state_index == empty_index) return null;
            const state = self.states[@intCast(state_index)];
            if (state.process_id == process_id and state.process_start_key == process_start_key) return state_index;
            index = (index + 1) % self.state_index.len;
        }
        return null;
    }

    fn findOrCreateCandidate(
        self: *Session,
        process_id: u32,
        candidate_count: *usize,
    ) ?u32 {
        var index: usize = @intCast(processIdHash(process_id) % self.candidate_index.len);
        var probes: usize = 0;
        while (probes < self.candidate_index.len) : (probes += 1) {
            const candidate_slot = self.candidate_index[index];
            if (candidate_slot == empty_index) {
                if (candidate_count.* >= self.candidates.len) return null;
                const created: u32 = @intCast(candidate_count.*);
                candidate_count.* += 1;
                self.candidates[@intCast(created)] = Candidate{};
                self.candidate_index[index] = created;
                return created;
            }
            const candidate = self.candidates[@intCast(candidate_slot)];
            if (candidate.process_id == process_id) return candidate_slot;
            index = (index + 1) % self.candidate_index.len;
        }
        return null;
    }

    fn allocateState(self: *Session, process_id: u32, process_start_key: u64) ?u32 {
        for (self.states, 0..) |*state, state_index| {
            if (state.process_id != 0) continue;
            const generation = self.next_generation;
            self.next_generation +%= 1;
            if (self.next_generation == 0) self.next_generation = 1;
            state.* = .{
                .process_id = process_id,
                .generation = generation,
                .process_start_key = process_start_key,
            };
            insertIndex(
                self.state_index,
                processHash(process_id, process_start_key),
                @intCast(state_index),
            ) catch {
                state.* = StateSlot{};
                return null;
            };
            return @intCast(state_index);
        }
        return null;
    }

    fn bumpRevision(self: *Session) void {
        self.state_revision +%= 1;
        if (self.state_revision == 0) self.state_revision = 1;
    }
};

fn validConfig(config: *const Config) bool {
    return config.abi_version == abi_version and
        config.struct_size == @sizeOf(Config) and
        config.generation != 0 and
        config.state_capacity > 0 and
        config.reserved0 == 0 and config.reserved1 == 0 and
        finiteRatio(config.critical_free_ratio) and
        finiteRatio(config.very_low_free_ratio) and
        finiteRatio(config.low_free_ratio) and
        finiteRatio(config.guarded_free_ratio) and
        finiteRatio(config.physical_emergency_free_ratio) and
        finiteRatio(config.virtual_emergency_free_ratio) and
        config.critical_free_ratio < config.very_low_free_ratio and
        config.very_low_free_ratio < config.low_free_ratio and
        config.low_free_ratio < config.guarded_free_ratio and
        std.math.isFinite(config.high_tier_minimum_base_score) and
        config.high_tier_minimum_base_score > 0 and
        config.critical_batch_count >= config.very_low_batch_count and
        config.very_low_batch_count >= config.low_batch_count and
        config.low_batch_count >= config.guarded_batch_count and
        config.guarded_batch_count > 0 and config.emergency_batch_count > 0 and
        config.critical_batch_count <= config.state_capacity and
        config.emergency_batch_count <= config.state_capacity;
}

fn validPlanHeader(self: *const Session, header: *const PlanHeader) bool {
    return header.abi_version == abi_version and
        header.struct_size == @sizeOf(PlanHeader) and
        header.input_struct_size == @sizeOf(CandidateInput) and
        header.output_struct_size == @sizeOf(DecisionOutput) and
        header.config_generation == self.config.generation and
        header.input_count <= self.config.state_capacity and
        header.output_capacity <= self.config.state_capacity and
        (header.request_flags & ~RequestFlags.all) == 0;
}

fn validInput(input: CandidateInput) bool {
    return input.struct_size == @sizeOf(CandidateInput) and
        (input.flags & ~InputFlags.can_apply) == 0 and
        input.process_id > 0 and input.process_start_key > 0 and
        std.math.isFinite(input.base_score) and input.base_score >= 0 and
        std.math.isFinite(input.cpu_score) and input.cpu_score >= 0 and
        std.math.isFinite(input.memory_used_percent);
}

fn validFeedback(self: *const Session, feedback: FeedbackInput) bool {
    if (feedback.struct_size != @sizeOf(FeedbackInput) or
        (feedback.flags & ~(FeedbackFlags.attempted | FeedbackFlags.succeeded)) != 0 or
        @as(usize, @intCast(feedback.state_slot)) >= self.states.len)
    {
        return false;
    }
    const state = self.states[@intCast(feedback.state_slot)];
    return state.process_id != 0 and state.generation == feedback.state_generation;
}

fn eligible(config: Config, input: CandidateInput) bool {
    if ((input.flags & InputFlags.can_apply) == 0) return false;
    if (input.runtime_state == 1 or input.runtime_state == 2) return false;
    return input.base_score < config.high_tier_minimum_base_score;
}

fn resolveBatchCount(config: Config, mode: Mode, memory_free_ratio: f64) u32 {
    if (mode == .emergency) return config.emergency_batch_count;
    const ratio = std.math.clamp(memory_free_ratio, 0, 1);
    if (ratio < config.critical_free_ratio) return config.critical_batch_count;
    if (ratio < config.very_low_free_ratio) return config.very_low_batch_count;
    if (ratio < config.low_free_ratio) return config.low_batch_count;
    if (ratio < config.guarded_free_ratio) return config.guarded_batch_count;
    return 0;
}

fn candidateLess(left: Candidate, right: Candidate, mode: Mode) bool {
    if (left.cpu_score != right.cpu_score) return left.cpu_score < right.cpu_score;
    const left_rank = runtimeSortRank(left.runtime_state);
    const right_rank = runtimeSortRank(right.runtime_state);
    if (left_rank != right_rank) return left_rank < right_rank;
    if (left.memory_used_percent != right.memory_used_percent) return left.memory_used_percent > right.memory_used_percent;
    return if (mode == .normal)
        left.process_id < right.process_id
    else
        left.source_input_index < right.source_input_index;
}

fn insertSelected(
    candidates: []const Candidate,
    selected: []u32,
    selected_count: *usize,
    candidate_index: u32,
    limit: u32,
    mode: Mode,
) void {
    const maximum: usize = @intCast(limit);
    if (maximum == 0) return;
    var insert_at: usize = 0;
    while (insert_at < selected_count.* and
        !candidateLess(candidates[@intCast(candidate_index)], candidates[@intCast(selected[insert_at])], mode)) : (insert_at += 1)
    {}
    if (insert_at >= maximum) return;
    const new_count = @min(selected_count.* + 1, maximum);
    var cursor = new_count;
    while (cursor > insert_at + 1) {
        cursor -= 1;
        selected[cursor] = selected[cursor - 1];
    }
    selected[insert_at] = candidate_index;
    selected_count.* = new_count;
}

fn processHash(process_id: u32, process_start_key: u64) u64 {
    var value = process_start_key ^ (@as(u64, process_id) *% 0x9E3779B97F4A7C15);
    value ^= value >> 30;
    value *%= 0xBF58476D1CE4E5B9;
    value ^= value >> 27;
    return value ^ (value >> 31);
}

fn processIdHash(process_id: u32) u64 {
    return processHash(process_id, 0xD6E8FEB86659FD93);
}

fn runtimeSortRank(runtime_state: u32) u32 {
    return switch (runtime_state) {
        5 => 1,
        4 => 2,
        3 => 3,
        1 => 4,
        2 => 5,
        else => 0,
    };
}

fn resolveMode(config: Config, header: *const PlanHeader) ?Mode {
    if ((header.request_flags & RequestFlags.force_emergency) != 0) return .emergency;
    if ((header.request_flags & RequestFlags.evaluate_emergency) != 0) {
        const physical_free = sanitizeRatio(header.physical_memory_free_ratio, 1);
        const virtual_free = sanitizeRatio(header.virtual_memory_free_ratio, 1);
        if (physical_free <= config.physical_emergency_free_ratio or
            virtual_free <= config.virtual_emergency_free_ratio)
        {
            return .emergency;
        }
    }
    return if ((header.request_flags & RequestFlags.allow_normal) != 0) .normal else null;
}

fn sanitizeRatio(value: f64, fallback: f64) f64 {
    if (!std.math.isFinite(value)) return fallback;
    return std.math.clamp(value, 0, 1);
}

fn insertIndex(indexes: []u32, hash: u64, value: u32) !void {
    var index: usize = @intCast(hash % indexes.len);
    var probes: usize = 0;
    while (probes < indexes.len) : (probes += 1) {
        if (indexes[index] == empty_index) {
            indexes[index] = value;
            return;
        }
        index = (index + 1) % indexes.len;
    }
    return error.IndexFull;
}

fn indexCapacity(capacity: usize) !usize {
    if (capacity > std.math.maxInt(usize) / 2) return error.OutOfMemory;
    return capacity * 2;
}

fn finiteRatio(value: f64) bool {
    return std.math.isFinite(value) and value >= 0 and value <= 1;
}

fn lockMutex(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}

const empty_inputs = [_]CandidateInput{};
const empty_outputs = [_]DecisionOutput{};
const empty_feedback = [_]FeedbackInput{};

fn inputSlice(pointer: ?[*]const CandidateInput, count: u32) ?[]const CandidateInput {
    if (count == 0) return empty_inputs[0..];
    return (pointer orelse return null)[0..@intCast(count)];
}

fn outputSlice(pointer: ?[*]DecisionOutput, count: u32) ?[]DecisionOutput {
    if (count == 0) return @constCast(empty_outputs[0..]);
    return (pointer orelse return null)[0..@intCast(count)];
}

fn feedbackSlice(pointer: ?[*]const FeedbackInput, count: u32) ?[]const FeedbackInput {
    if (count == 0) return empty_feedback[0..];
    return (pointer orelse return null)[0..@intCast(count)];
}

test "normal cleanup repeatedly selects the current lowest score and distinguishes pid reuse" {
    var config = Config{
        .abi_version = abi_version,
        .struct_size = @sizeOf(Config),
        .generation = 1,
        .state_capacity = 8,
        .reserved0 = 0,
        .critical_free_ratio = 0.06,
        .very_low_free_ratio = 0.12,
        .low_free_ratio = 0.20,
        .guarded_free_ratio = 0.30,
        .physical_emergency_free_ratio = 0.06,
        .virtual_emergency_free_ratio = 0.08,
        .high_tier_minimum_base_score = 81,
        .critical_batch_count = 6,
        .very_low_batch_count = 4,
        .low_batch_count = 2,
        .guarded_batch_count = 1,
        .emergency_batch_count = 6,
        .reserved1 = 0,
    };
    const session = try Session.create(&config);
    defer session.destroy();
    var inputs = [_]CandidateInput{
        .{ .struct_size = @sizeOf(CandidateInput), .flags = InputFlags.can_apply, .process_id = 42, .runtime_state = 5, .process_start_key = 100, .target_key = 1, .base_score = 10, .cpu_score = 20, .memory_used_percent = 30 },
        .{ .struct_size = @sizeOf(CandidateInput), .flags = InputFlags.can_apply, .process_id = 43, .runtime_state = 5, .process_start_key = 200, .target_key = 2, .base_score = 20, .cpu_score = 10, .memory_used_percent = 40 },
    };
    var outputs: [8]DecisionOutput = undefined;
    var header = PlanHeader{
        .abi_version = abi_version,
        .struct_size = @sizeOf(PlanHeader),
        .input_struct_size = @sizeOf(CandidateInput),
        .output_struct_size = @sizeOf(DecisionOutput),
        .input_count = @intCast(inputs.len),
        .output_capacity = @intCast(outputs.len),
        .output_count = 0,
        .request_flags = RequestFlags.allow_normal,
        .ordinary_memory_free_ratio = 0.25,
        .physical_memory_free_ratio = 0.25,
        .virtual_memory_free_ratio = 0.25,
        .config_generation = 1,
        .state_revision = 0,
    };
    try std.testing.expectEqual(ResultCode.ok, session.plan(&header, &inputs, @intCast(inputs.len), &outputs, @intCast(outputs.len)));
    try std.testing.expectEqual(@as(u32, 1), header.output_count);
    try std.testing.expectEqual(@as(u64, 200), outputs[0].process_start_key);
    var feedback = [_]FeedbackInput{.{
        .struct_size = @sizeOf(FeedbackInput),
        .state_slot = outputs[0].state_slot,
        .state_generation = outputs[0].state_generation,
        .flags = FeedbackFlags.attempted,
    }};
    try std.testing.expectEqual(ResultCode.ok, session.complete(&feedback, @intCast(feedback.len)));
    try std.testing.expectEqual(ResultCode.ok, session.plan(&header, &inputs, @intCast(inputs.len), &outputs, @intCast(outputs.len)));
    try std.testing.expectEqual(@as(u32, 1), header.output_count);
    try std.testing.expectEqual(@as(u64, 200), outputs[0].process_start_key);

    feedback[0] = .{
        .struct_size = @sizeOf(FeedbackInput),
        .state_slot = outputs[0].state_slot,
        .state_generation = outputs[0].state_generation,
        .flags = FeedbackFlags.attempted | FeedbackFlags.succeeded,
    };
    try std.testing.expectEqual(ResultCode.ok, session.complete(&feedback, @intCast(feedback.len)));
    inputs[1].process_start_key = 201;
    try std.testing.expectEqual(ResultCode.ok, session.plan(&header, &inputs, @intCast(inputs.len), &outputs, @intCast(outputs.len)));
    try std.testing.expectEqual(@as(u64, 201), outputs[0].process_start_key);
}

test "trim admission uses the optimization protected base score boundary" {
    var config = testConfig(1);
    config.guarded_batch_count = 2;
    const session = try Session.create(&config);
    defer session.destroy();

    var inputs = [_]CandidateInput{
        .{ .struct_size = @sizeOf(CandidateInput), .flags = InputFlags.can_apply, .process_id = 44, .runtime_state = 5, .process_start_key = 300, .target_key = 3, .base_score = 70, .cpu_score = 20, .memory_used_percent = 30 },
        .{ .struct_size = @sizeOf(CandidateInput), .flags = InputFlags.can_apply, .process_id = 45, .runtime_state = 5, .process_start_key = 400, .target_key = 4, .base_score = 81, .cpu_score = 10, .memory_used_percent = 40 },
    };
    var outputs: [8]DecisionOutput = undefined;
    var header = testHeader(inputs.len, outputs.len, RequestFlags.allow_normal, 0.25, 1, 1, 1);

    try std.testing.expectEqual(
        ResultCode.ok,
        session.plan(&header, &inputs, @intCast(inputs.len), &outputs, @intCast(outputs.len)),
    );
    try std.testing.expectEqual(@as(u32, 1), header.output_count);
    try std.testing.expectEqual(@as(u32, 44), outputs[0].process_id);
}

test "emergency pressure is resolved in Zig and freezes hot configuration until feedback" {
    var config = testConfig(1);
    const session = try Session.create(&config);
    defer session.destroy();
    var input = [_]CandidateInput{.{
        .struct_size = @sizeOf(CandidateInput),
        .flags = InputFlags.can_apply,
        .process_id = 51,
        .runtime_state = 5,
        .process_start_key = 500,
        .target_key = 5,
        .base_score = 20,
        .cpu_score = 10,
        .memory_used_percent = 10,
    }};
    var outputs: [8]DecisionOutput = undefined;
    var header = testHeader(input.len, outputs.len, RequestFlags.evaluate_emergency, 1, 0.05, 1, 1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session.plan(&header, &input, @intCast(input.len), &outputs, @intCast(outputs.len)),
    );
    try std.testing.expectEqual(@as(u32, 1), header.output_count);
    try std.testing.expectEqual(Mode.emergency, @as(Mode, @enumFromInt(outputs[0].mode)));

    var changed = testConfig(2);
    changed.guarded_free_ratio = 0.31;
    try std.testing.expectEqual(ResultCode.invalid_argument, session.reconfigure(&changed));
    var feedback = [_]FeedbackInput{.{
        .struct_size = @sizeOf(FeedbackInput),
        .state_slot = outputs[0].state_slot,
        .state_generation = outputs[0].state_generation,
        .flags = 0,
    }};
    try std.testing.expectEqual(ResultCode.ok, session.complete(&feedback, @intCast(feedback.len)));
    try std.testing.expectEqual(ResultCode.ok, session.reconfigure(&changed));
}

test "required output capacity is resolved by the session configuration" {
    var config = testConfig(1);
    config.guarded_batch_count = 2;
    config.emergency_batch_count = 6;
    const session = try Session.create(&config);
    defer session.destroy();

    var normal_header = testHeader(5, 0, RequestFlags.allow_normal, 0.25, 1, 1, 1);
    var capacity: u32 = std.math.maxInt(u32);
    try std.testing.expectEqual(
        ResultCode.ok,
        session.requiredOutputCapacity(&normal_header, 5, &capacity),
    );
    try std.testing.expectEqual(@as(u32, 2), capacity);

    var emergency_header = testHeader(5, 0, RequestFlags.force_emergency, 1, 1, 1, 1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session.requiredOutputCapacity(&emergency_header, 5, &capacity),
    );
    try std.testing.expectEqual(@as(u32, 5), capacity);

    var disabled_header = testHeader(5, 0, 0, 1, 1, 1, 1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session.requiredOutputCapacity(&disabled_header, 5, &capacity),
    );
    try std.testing.expectEqual(@as(u32, 0), capacity);
}

test "normal cleanup five bands preserve exact inclusive boundaries" {
    const cases = [_]struct {
        free_ratio: f64,
        expected_count: u32,
    }{
        .{ .free_ratio = -0.01, .expected_count = 6 },
        .{ .free_ratio = 1.00, .expected_count = 0 },
        .{ .free_ratio = 0.30, .expected_count = 0 },
        .{ .free_ratio = nextPositiveTestF64(0.30), .expected_count = 0 },
        .{ .free_ratio = 0.50, .expected_count = 0 },
        .{ .free_ratio = 1.01, .expected_count = 0 },
        .{ .free_ratio = std.math.nan(f64), .expected_count = 0 },
        .{ .free_ratio = std.math.inf(f64), .expected_count = 0 },
        .{ .free_ratio = -std.math.inf(f64), .expected_count = 0 },
        .{ .free_ratio = previousPositiveTestF64(0.30), .expected_count = 1 },
        .{ .free_ratio = 0.20, .expected_count = 1 },
        .{ .free_ratio = nextPositiveTestF64(0.20), .expected_count = 1 },
        .{ .free_ratio = previousPositiveTestF64(0.20), .expected_count = 2 },
        .{ .free_ratio = 0.12, .expected_count = 2 },
        .{ .free_ratio = nextPositiveTestF64(0.12), .expected_count = 2 },
        .{ .free_ratio = previousPositiveTestF64(0.12), .expected_count = 4 },
        .{ .free_ratio = 0.06, .expected_count = 4 },
        .{ .free_ratio = nextPositiveTestF64(0.06), .expected_count = 4 },
        .{ .free_ratio = previousPositiveTestF64(0.06), .expected_count = 6 },
        .{ .free_ratio = 0.00, .expected_count = 6 },
    };
    var inputs: [8]CandidateInput = undefined;
    for (&inputs, 0..) |*input, index| {
        input.* = .{
            .struct_size = @sizeOf(CandidateInput),
            .flags = InputFlags.can_apply,
            .process_id = @intCast(100 + index),
            .runtime_state = 5,
            .process_start_key = @intCast(1_000 + index),
            .target_key = @intCast(2_000 + index),
            .base_score = 20,
            .cpu_score = @floatFromInt(index + 1),
            .memory_used_percent = 10,
        };
    }
    var outputs: [8]DecisionOutput = undefined;

    for (cases) |case| {
        const config = testConfig(1);
        const session = try Session.create(&config);
        defer session.destroy();
        var header = testHeader(
            inputs.len,
            outputs.len,
            RequestFlags.allow_normal,
            case.free_ratio,
            1,
            1,
            config.generation,
        );
        var required_capacity: u32 = std.math.maxInt(u32);

        try std.testing.expectEqual(
            ResultCode.ok,
            session.requiredOutputCapacity(
                &header,
                @intCast(inputs.len),
                &required_capacity,
            ),
        );
        try std.testing.expectEqual(case.expected_count, required_capacity);
        try std.testing.expectEqual(
            ResultCode.ok,
            session.plan(
                &header,
                &inputs,
                @intCast(inputs.len),
                &outputs,
                @intCast(outputs.len),
            ),
        );
        try std.testing.expectEqual(case.expected_count, header.output_count);
        for (outputs[0..@intCast(header.output_count)], 0..) |output, index| {
            try std.testing.expectEqual(Mode.normal, @as(Mode, @enumFromInt(output.mode)));
            try std.testing.expectEqual(@as(u32, @intCast(100 + index)), output.process_id);
        }
    }
}

test "conflicting pid identities fail closed and runtime ordering is policy-defined" {
    var config = testConfig(1);
    config.guarded_batch_count = 1;
    const session = try Session.create(&config);
    defer session.destroy();
    var conflict_inputs = [_]CandidateInput{
        .{ .struct_size = @sizeOf(CandidateInput), .flags = InputFlags.can_apply, .process_id = 60, .runtime_state = 5, .process_start_key = 600, .target_key = 6, .base_score = 20, .cpu_score = 10, .memory_used_percent = 10 },
        .{ .struct_size = @sizeOf(CandidateInput), .flags = InputFlags.can_apply, .process_id = 60, .runtime_state = 5, .process_start_key = 601, .target_key = 7, .base_score = 10, .cpu_score = 20, .memory_used_percent = 20 },
    };
    var outputs: [8]DecisionOutput = undefined;
    var header = testHeader(conflict_inputs.len, outputs.len, RequestFlags.allow_normal, 0.25, 1, 1, 1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session.plan(&header, &conflict_inputs, @intCast(conflict_inputs.len), &outputs, @intCast(outputs.len)),
    );
    try std.testing.expectEqual(@as(u32, 0), header.output_count);

    var ordered_inputs = [_]CandidateInput{
        .{ .struct_size = @sizeOf(CandidateInput), .flags = InputFlags.can_apply, .process_id = 61, .runtime_state = 3, .process_start_key = 610, .target_key = 8, .base_score = 20, .cpu_score = 10, .memory_used_percent = 10 },
        .{ .struct_size = @sizeOf(CandidateInput), .flags = InputFlags.can_apply, .process_id = 62, .runtime_state = 5, .process_start_key = 620, .target_key = 9, .base_score = 20, .cpu_score = 10, .memory_used_percent = 10 },
    };
    header = testHeader(ordered_inputs.len, outputs.len, RequestFlags.allow_normal, 0.25, 1, 1, 1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session.plan(&header, &ordered_inputs, @intCast(ordered_inputs.len), &outputs, @intCast(outputs.len)),
    );
    try std.testing.expectEqual(@as(u32, 1), header.output_count);
    try std.testing.expectEqual(@as(u32, 62), outputs[0].process_id);
}

fn testConfig(generation: u64) Config {
    return .{
        .abi_version = abi_version,
        .struct_size = @sizeOf(Config),
        .generation = generation,
        .state_capacity = 8,
        .reserved0 = 0,
        .critical_free_ratio = 0.06,
        .very_low_free_ratio = 0.12,
        .low_free_ratio = 0.20,
        .guarded_free_ratio = 0.30,
        .physical_emergency_free_ratio = 0.06,
        .virtual_emergency_free_ratio = 0.08,
        .high_tier_minimum_base_score = 81,
        .critical_batch_count = 6,
        .very_low_batch_count = 4,
        .low_batch_count = 2,
        .guarded_batch_count = 1,
        .emergency_batch_count = 6,
        .reserved1 = 0,
    };
}

fn testHeader(
    input_count: usize,
    output_capacity: usize,
    request_flags: u32,
    ordinary_free: f64,
    physical_free: f64,
    virtual_free: f64,
    generation: u64,
) PlanHeader {
    return .{
        .abi_version = abi_version,
        .struct_size = @sizeOf(PlanHeader),
        .input_struct_size = @sizeOf(CandidateInput),
        .output_struct_size = @sizeOf(DecisionOutput),
        .input_count = @intCast(input_count),
        .output_capacity = @intCast(output_capacity),
        .output_count = 0,
        .request_flags = request_flags,
        .ordinary_memory_free_ratio = ordinary_free,
        .physical_memory_free_ratio = physical_free,
        .virtual_memory_free_ratio = virtual_free,
        .config_generation = generation,
        .state_revision = 0,
    };
}

fn previousPositiveTestF64(value: f64) f64 {
    std.debug.assert(std.math.isFinite(value) and value > 0);
    const bits: u64 = @bitCast(value);
    return @bitCast(bits - 1);
}

fn nextPositiveTestF64(value: f64) f64 {
    std.debug.assert(std.math.isFinite(value) and value >= 0);
    const bits: u64 = @bitCast(value);
    return @bitCast(bits + 1);
}
