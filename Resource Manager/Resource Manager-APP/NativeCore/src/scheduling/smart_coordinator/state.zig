const std = @import("std");
const protocol = @import("protocol.zig");

pub const SampleValidity = struct {
    pub const cpu: u8 = 1 << 0;
    pub const gpu: u8 = 1 << 2;
    pub const vram: u8 = 1 << 3;
};

pub const ProcessTransition = struct {
    desired: i8,
    applied: i8,
    pending_to: i8,
    pending_from: i8,
    pending_count: u32,
    pending_first_seen_ms: i64,
    pending_last_seen_ms: i64,
    last_source_fingerprint: u64,
    owned: bool,
    inflight_action_id: u64,
    retry_not_before_ms: i64,
    last_feedback_ms: i64,
    failure_count: u32,
    compensation_required: bool,

    pub fn normal() ProcessTransition {
        return .{
            .desired = protocol.ProcessGrade.normal,
            .applied = protocol.ProcessGrade.normal,
            .pending_to = protocol.ProcessGrade.normal,
            .pending_from = protocol.ProcessGrade.normal,
            .pending_count = 0,
            .pending_first_seen_ms = 0,
            .pending_last_seen_ms = 0,
            .last_source_fingerprint = 0,
            .owned = false,
            .inflight_action_id = 0,
            .retry_not_before_ms = 0,
            .last_feedback_ms = 0,
            .failure_count = 0,
            .compensation_required = false,
        };
    }

    pub fn clearPending(self: *ProcessTransition) void {
        self.pending_to = self.applied;
        self.pending_from = self.applied;
        self.pending_count = 0;
        self.pending_first_seen_ms = 0;
        self.pending_last_seen_ms = 0;
    }
};

pub const AdapterTransition = struct {
    desired: u8,
    applied: u8,
    pending_to: u8,
    pending_from: u8,
    pending_count: u32,
    pending_first_seen_ms: i64,
    pending_last_seen_ms: i64,
    last_source_fingerprint: u64,
    owned: bool,
    inflight_action_id: u64,
    retry_not_before_ms: i64,
    last_feedback_ms: i64,
    failure_count: u32,

    pub fn normal() AdapterTransition {
        const normal_grade = @intFromEnum(protocol.AdapterGrade.normal);
        return .{
            .desired = normal_grade,
            .applied = normal_grade,
            .pending_to = normal_grade,
            .pending_from = normal_grade,
            .pending_count = 0,
            .pending_first_seen_ms = 0,
            .pending_last_seen_ms = 0,
            .last_source_fingerprint = 0,
            .owned = false,
            .inflight_action_id = 0,
            .retry_not_before_ms = 0,
            .last_feedback_ms = 0,
            .failure_count = 0,
        };
    }

    pub fn clearPending(self: *AdapterTransition) void {
        self.pending_to = self.applied;
        self.pending_from = self.applied;
        self.pending_count = 0;
        self.pending_first_seen_ms = 0;
        self.pending_last_seen_ms = 0;
    }
};

pub const ProcessState = struct {
    occupied: bool,
    seen_this_cycle: bool,
    target_key: u64,
    software_key: u64,
    process_start_key: u64,
    process_id: u32,
    source_index: u32,
    last_seen_cycle: u64,
    last_seen_at_ms: i64,
    valid_mask: u64,
    input_flags: u64,
    software_kind: u8,
    protection_level: u8,
    runtime_state: u8,
    cpu_capability_mask: u8,
    gpu_capability_mask: u8,
    base_score: f64,
    cpu_usage_percent: f64,
    cpu_score: f64,
    cpu_score_valid: bool,
    reason_mask: u64,
    candidate_valid: bool,
    candidate_grade: i8,
    freeze_eligible: bool,
    freeze_ready: bool,
    critical_fact: bool,
    game_active: bool,
    critical_reason_mask: u8,
    game_started_at_ms: i64,
    transition: ProcessTransition,

    pub fn empty() ProcessState {
        var value = std.mem.zeroes(ProcessState);
        value.runtime_state = @intFromEnum(protocol.RuntimeState.unknown);
        value.candidate_grade = protocol.ProcessGrade.normal;
        value.transition = ProcessTransition.normal();
        return value;
    }
};

pub const SoftwareState = struct {
    occupied: bool,
    seen_this_cycle: bool,
    software_key: u64,
    source_index: u32,
    last_seen_cycle: u64,
    last_seen_at_ms: i64,
    valid_mask: u64,
    input_flags: u64,
    software_kind: u8,
    runtime_state: u8,
    cpu_capability_mask: u8,
    gpu_capability_mask: u8,
    base_score: f64,
    member_cpu_score: f64,
    member_gpu_score: f64,
    member_cpu_score_valid: bool,
    member_gpu_score_valid: bool,
    scored_cpu_member_count: u32,
    scored_gpu_member_count: u32,
    adapter_cpu_score: f64,
    adapter_gpu_score: f64,
    adapter_cpu_score_valid: bool,
    adapter_gpu_score_valid: bool,
    reason_mask: u64,
    cpu_candidate_valid: bool,
    gpu_candidate_valid: bool,
    cpu_candidate_grade: u8,
    gpu_candidate_grade: u8,
    member_count: u32,
    member_signature: u64,
    critical_fact: bool,
    previous_critical_fact: bool,
    previous_member_signature: u64,
    planning_atomic_group_id: u64,
    planning_process_member_count: u32,
    planning_freeze_eligible_count: u32,
    planning_compensation_pending_count: u32,
    freeze_compensation_active: bool,
    cpu_transition: AdapterTransition,
    gpu_transition: AdapterTransition,

    pub fn empty() SoftwareState {
        var value = std.mem.zeroes(SoftwareState);
        value.runtime_state = @intFromEnum(protocol.RuntimeState.unknown);
        value.cpu_candidate_grade = @intFromEnum(protocol.AdapterGrade.normal);
        value.gpu_candidate_grade = @intFromEnum(protocol.AdapterGrade.normal);
        value.cpu_transition = AdapterTransition.normal();
        value.gpu_transition = AdapterTransition.normal();
        return value;
    }
};

pub const GpuState = struct {
    occupied: bool,
    seen_this_cycle: bool,
    target_key: u64,
    process_start_key: u64,
    process_id: u32,
    device_index: u32,
    last_seen_cycle: u64,
    valid_mask: u8,
    gpu_usage_percent: f64,
    vram_usage_percent: f64,
};

pub const StagedProcess = struct {
    occupied: bool,
    target_key: u64,
    software_key: u64,
    process_start_key: u64,
    process_id: u32,
    source_index: u32,
    valid_mask: u64,
    input_flags: u64,
    software_kind: u8,
    protection_level: u8,
    runtime_state: u8,
    cpu_capability_mask: u8,
    gpu_capability_mask: u8,
    base_score: f64,
    cpu_usage_percent: f64,
    cpu_metric_valid: bool,
    cpu_score: f64,
    cpu_score_valid: bool,
    software_cpu_score: f64,
    software_cpu_score_member_count: u32,
    software_cpu_score_valid: bool,
    software_slot: u32,
    applied_process_grade: i8,
    applied_cpu_grade: u8,
    applied_gpu_grade: u8,
    applied_epoch: u64,
};

pub const StagedSoftware = struct {
    occupied: bool,
    software_key: u64,
    source_index: u32,
    valid_mask: u64,
    input_flags: u64,
    software_kind: u8,
    runtime_state: u8,
    cpu_capability_mask: u8,
    gpu_capability_mask: u8,
    base_score: f64,
    cpu_score: f64,
    cpu_score_member_count: u32,
    cpu_scored_member_count: u32,
    cpu_score_valid: bool,
    member_count: u32,
    member_signature: u64,
    critical_fact: bool,
    applied_cpu_grade: u8,
    applied_gpu_grade: u8,
    applied_epoch: u64,
    selected_gpu_present: bool,
    selected_gpu_index: u32,
    selected_gpu_usage: f64,
    selected_vram_usage: f64,
    selected_gpu_score: f64,
    selected_gpu_score_member_count: u32,
    selected_gpu_score_valid: bool,
};

pub const StagedGpu = struct {
    occupied: bool,
    process_slot: u32,
    target_key: u64,
    process_start_key: u64,
    process_id: u32,
    device_index: u32,
    valid_mask: u8,
    gpu_usage_percent: f64,
    vram_usage_percent: f64,
    gpu_score: f64,
    gpu_score_valid: bool,
    software_gpu_score: f64,
    software_gpu_score_member_count: u32,
    software_gpu_score_valid: bool,
};

pub const StagedSoftwareGpu = struct {
    occupied: bool,
    software_slot: u32,
    device_index: u32,
    gpu_usage_percent: f64,
    vram_usage_percent: f64,
    gpu_score: f64,
    gpu_score_member_count: u32,
    gpu_scored_member_count: u32,
    gpu_score_valid: bool,
};

pub const Reservation = struct {
    occupied: bool,
    action: protocol.Action,
    process_slot: u32,
    software_slot: u32,
    atomic_group_slot: u32,
    created_at_ms: i64,
    deadline_ms: i64,
    validation_epoch: u64,
    validation_feedback_index: u32,
    feedback_received: bool,
    feedback: protocol.Feedback,
};

pub const AtomicGroup = struct {
    occupied: bool,
    group_id: u64,
    expected_count: u32,
    feedback_count: u32,
    failed_count: u32,
    order_score: f64,
};

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.Config,
    state_revision: u64,
    last_cycle_sequence: u64,
    plan_epoch: u64,
    next_action_id: u64,
    next_group_id: u64,
    validation_epoch: u64,
    last_observed_at_ms: i64,
    event_boost_until_ms: i64,
    next_wake_at_ms: i64,
    wake_after_ms: u32,
    last_reason_mask: u64,
    invalid_fact_count: u32,
    inflight_count: u32,
    first_cycle_completed: bool,
    requires_authoritative_resync: bool,
    last_cycle_score_only: bool,

    processes: []ProcessState,
    softwares: []SoftwareState,
    gpus: []GpuState,

    staged_processes: []StagedProcess,
    staged_softwares: []StagedSoftware,
    staged_gpus: []StagedGpu,
    staged_software_gpus: []StagedSoftwareGpu,
    staged_process_count: u32,
    staged_software_count: u32,
    staged_gpu_count: u32,
    staged_software_gpu_count: u32,

    process_allocate_cursor: u32,
    software_allocate_cursor: u32,
    gpu_allocate_cursor: u32,
    reservation_allocate_cursor: u32,
    atomic_group_allocate_cursor: u32,

    process_index: []u32,
    software_index: []u32,
    gpu_index: []u32,
    staged_process_index: []u32,
    staged_software_index: []u32,
    staged_gpu_index: []u32,
    staged_software_gpu_index: []u32,

    reservations: []Reservation,
    atomic_groups: []AtomicGroup,
    reservation_index: []u32,
    atomic_group_index: []u32,

    pub fn create(config: *const protocol.Config) !*Session {
        if (!protocol.isValidConfig(config)) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);

        const process_count: usize = @intCast(config.max_processes);
        const software_count: usize = @intCast(config.max_software_groups);
        const gpu_count: usize = @intCast(config.max_gpu_states);
        const process_index_count = try hashIndexCapacity(config.max_processes);
        const software_index_count = try hashIndexCapacity(config.max_software_groups);
        const gpu_index_count = try hashIndexCapacity(config.max_gpu_states);
        const reservation_index_count = try hashIndexCapacity(config.max_reservations);
        const atomic_group_index_count = try hashIndexCapacity(config.max_atomic_groups);

        const processes = try allocator.alloc(ProcessState, process_count);
        errdefer allocator.free(processes);
        const softwares = try allocator.alloc(SoftwareState, software_count);
        errdefer allocator.free(softwares);
        const gpus = try allocator.alloc(GpuState, gpu_count);
        errdefer allocator.free(gpus);

        const staged_processes = try allocator.alloc(StagedProcess, process_count);
        errdefer allocator.free(staged_processes);
        const staged_softwares = try allocator.alloc(StagedSoftware, software_count);
        errdefer allocator.free(staged_softwares);
        const staged_gpus = try allocator.alloc(StagedGpu, gpu_count);
        errdefer allocator.free(staged_gpus);
        const staged_software_gpus = try allocator.alloc(StagedSoftwareGpu, gpu_count);
        errdefer allocator.free(staged_software_gpus);

        const process_index = try allocator.alloc(u32, process_index_count);
        errdefer allocator.free(process_index);
        const software_index = try allocator.alloc(u32, software_index_count);
        errdefer allocator.free(software_index);
        const gpu_index = try allocator.alloc(u32, gpu_index_count);
        errdefer allocator.free(gpu_index);
        const staged_process_index = try allocator.alloc(u32, process_index_count);
        errdefer allocator.free(staged_process_index);
        const staged_software_index = try allocator.alloc(u32, software_index_count);
        errdefer allocator.free(staged_software_index);
        const staged_gpu_index = try allocator.alloc(u32, gpu_index_count);
        errdefer allocator.free(staged_gpu_index);
        const staged_software_gpu_index = try allocator.alloc(u32, gpu_index_count);
        errdefer allocator.free(staged_software_gpu_index);

        const reservations = try allocator.alloc(Reservation, @intCast(config.max_reservations));
        errdefer allocator.free(reservations);
        const atomic_groups = try allocator.alloc(AtomicGroup, @intCast(config.max_atomic_groups));
        errdefer allocator.free(atomic_groups);
        const reservation_index = try allocator.alloc(u32, reservation_index_count);
        errdefer allocator.free(reservation_index);
        const atomic_group_index = try allocator.alloc(u32, atomic_group_index_count);
        errdefer allocator.free(atomic_group_index);

        for (processes) |*item| item.* = ProcessState.empty();
        for (softwares) |*item| item.* = SoftwareState.empty();
        @memset(gpus, std.mem.zeroes(GpuState));
        @memset(staged_processes, std.mem.zeroes(StagedProcess));
        @memset(staged_softwares, std.mem.zeroes(StagedSoftware));
        @memset(staged_gpus, std.mem.zeroes(StagedGpu));
        @memset(staged_software_gpus, std.mem.zeroes(StagedSoftwareGpu));
        @memset(process_index, 0);
        @memset(software_index, 0);
        @memset(gpu_index, 0);
        @memset(staged_process_index, 0);
        @memset(staged_software_index, 0);
        @memset(staged_gpu_index, 0);
        @memset(staged_software_gpu_index, 0);
        @memset(reservations, std.mem.zeroes(Reservation));
        @memset(atomic_groups, std.mem.zeroes(AtomicGroup));
        @memset(reservation_index, 0);
        @memset(atomic_group_index, 0);

        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .state_revision = 1,
            .last_cycle_sequence = 0,
            .plan_epoch = 0,
            .next_action_id = 1,
            .next_group_id = 1,
            .validation_epoch = 1,
            .last_observed_at_ms = 0,
            .event_boost_until_ms = 0,
            .next_wake_at_ms = 0,
            .wake_after_ms = config.normal_interval_ms,
            .last_reason_mask = 0,
            .invalid_fact_count = 0,
            .inflight_count = 0,
            .first_cycle_completed = false,
            .requires_authoritative_resync = false,
            .last_cycle_score_only = false,
            .processes = processes,
            .softwares = softwares,
            .gpus = gpus,
            .staged_processes = staged_processes,
            .staged_softwares = staged_softwares,
            .staged_gpus = staged_gpus,
            .staged_software_gpus = staged_software_gpus,
            .staged_process_count = 0,
            .staged_software_count = 0,
            .staged_gpu_count = 0,
            .staged_software_gpu_count = 0,
            .process_allocate_cursor = 0,
            .software_allocate_cursor = 0,
            .gpu_allocate_cursor = 0,
            .reservation_allocate_cursor = 0,
            .atomic_group_allocate_cursor = 0,
            .process_index = process_index,
            .software_index = software_index,
            .gpu_index = gpu_index,
            .staged_process_index = staged_process_index,
            .staged_software_index = staged_software_index,
            .staged_gpu_index = staged_gpu_index,
            .staged_software_gpu_index = staged_software_gpu_index,
            .reservations = reservations,
            .atomic_groups = atomic_groups,
            .reservation_index = reservation_index,
            .atomic_group_index = atomic_group_index,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.atomic_group_index);
        allocator.free(self.reservation_index);
        allocator.free(self.atomic_groups);
        allocator.free(self.reservations);
        allocator.free(self.staged_software_gpu_index);
        allocator.free(self.staged_gpu_index);
        allocator.free(self.staged_software_index);
        allocator.free(self.staged_process_index);
        allocator.free(self.gpu_index);
        allocator.free(self.software_index);
        allocator.free(self.process_index);
        allocator.free(self.staged_software_gpus);
        allocator.free(self.staged_gpus);
        allocator.free(self.staged_softwares);
        allocator.free(self.staged_processes);
        allocator.free(self.gpus);
        allocator.free(self.softwares);
        allocator.free(self.processes);
        allocator.destroy(self);
    }

    pub fn reconfigure(self: *Session, config: *const protocol.Config) protocol.Status {
        const validation = protocol.validateConfig(config);
        if (validation != .ok) return validation;
        if (!sameCapacityShape(&self.config, config)) return .recreate_required;
        if (config.generation <= self.config.generation) return .stale_generation;
        if (self.inflightCount() != 0) return .unavailable;
        self.config = config.*;
        self.bumpRevision();
        return .ok;
    }

    pub fn reset(self: *Session) protocol.Status {
        if (self.inflightCount() != 0 or self.hasOwnedState()) return .unavailable;
        for (self.processes) |*item| item.* = ProcessState.empty();
        for (self.softwares) |*item| item.* = SoftwareState.empty();
        @memset(self.gpus, std.mem.zeroes(GpuState));
        @memset(self.reservations, std.mem.zeroes(Reservation));
        @memset(self.atomic_groups, std.mem.zeroes(AtomicGroup));
        @memset(self.reservation_index, 0);
        @memset(self.atomic_group_index, 0);
        self.last_cycle_sequence = 0;
        self.plan_epoch = 0;
        self.next_action_id = 1;
        self.next_group_id = 1;
        self.validation_epoch = 1;
        self.last_observed_at_ms = 0;
        self.event_boost_until_ms = 0;
        self.next_wake_at_ms = 0;
        self.wake_after_ms = self.config.normal_interval_ms;
        self.last_reason_mask = 0;
        self.invalid_fact_count = 0;
        self.inflight_count = 0;
        self.first_cycle_completed = false;
        self.requires_authoritative_resync = false;
        self.last_cycle_score_only = false;
        self.process_allocate_cursor = 0;
        self.software_allocate_cursor = 0;
        self.gpu_allocate_cursor = 0;
        self.reservation_allocate_cursor = 0;
        self.atomic_group_allocate_cursor = 0;
        self.clearStage();
        self.rebuildPersistentIndices();
        self.bumpRevision();
        return .ok;
    }

    pub fn clearStage(self: *Session) void {
        @memset(self.staged_processes, std.mem.zeroes(StagedProcess));
        @memset(self.staged_softwares, std.mem.zeroes(StagedSoftware));
        @memset(self.staged_gpus, std.mem.zeroes(StagedGpu));
        @memset(self.staged_software_gpus, std.mem.zeroes(StagedSoftwareGpu));
        @memset(self.staged_process_index, 0);
        @memset(self.staged_software_index, 0);
        @memset(self.staged_gpu_index, 0);
        @memset(self.staged_software_gpu_index, 0);
        self.staged_process_count = 0;
        self.staged_software_count = 0;
        self.staged_gpu_count = 0;
        self.staged_software_gpu_count = 0;
    }

    pub fn rebuildPersistentIndices(self: *Session) void {
        @memset(self.process_index, 0);
        @memset(self.software_index, 0);
        @memset(self.gpu_index, 0);
        for (self.processes, 0..) |item, index| if (item.occupied) self.insertProcessIndex(@intCast(index));
        for (self.softwares, 0..) |item, index| if (item.occupied) self.insertSoftwareIndex(@intCast(index));
        for (self.gpus, 0..) |item, index| if (item.occupied) self.insertGpuIndex(@intCast(index));
    }

    pub fn findProcess(self: *const Session, target_key: u64, process_id: u32, start_key: u64) ?u32 {
        return findProcessIn(self.process_index, self.processes, target_key, process_id, start_key);
    }

    pub fn findSoftware(self: *const Session, software_key: u64) ?u32 {
        return findSoftwareIn(self.software_index, self.softwares, software_key);
    }

    pub fn findGpu(self: *const Session, target_key: u64, process_id: u32, start_key: u64, device_index: u32) ?u32 {
        return findGpuIn(self.gpu_index, self.gpus, target_key, process_id, start_key, device_index);
    }

    pub fn findStagedProcess(self: *const Session, target_key: u64, process_id: u32, start_key: u64) ?u32 {
        return findStagedProcessIn(self.staged_process_index, self.staged_processes, target_key, process_id, start_key);
    }

    pub fn findStagedSoftware(self: *const Session, software_key: u64) ?u32 {
        return findStagedSoftwareIn(self.staged_software_index, self.staged_softwares, software_key);
    }

    pub fn findStagedGpu(self: *const Session, target_key: u64, process_id: u32, start_key: u64, device_index: u32) ?u32 {
        return findStagedGpuIn(self.staged_gpu_index, self.staged_gpus, target_key, process_id, start_key, device_index);
    }

    pub fn findStagedSoftwareGpu(self: *const Session, software_slot: u32, device_index: u32) ?u32 {
        return findStagedSoftwareGpuIn(self.staged_software_gpu_index, self.staged_software_gpus, software_slot, device_index);
    }

    pub fn addStagedProcess(self: *Session, value: StagedProcess) ?u32 {
        if (self.staged_process_count >= self.staged_processes.len) return null;
        const slot = self.staged_process_count;
        self.staged_processes[slot] = value;
        self.staged_process_count += 1;
        insertStagedProcessIndex(self.staged_process_index, self.staged_processes, slot);
        return slot;
    }

    pub fn addStagedSoftware(self: *Session, value: StagedSoftware) ?u32 {
        if (self.staged_software_count >= self.staged_softwares.len) return null;
        const slot = self.staged_software_count;
        self.staged_softwares[slot] = value;
        self.staged_software_count += 1;
        insertStagedSoftwareIndex(self.staged_software_index, self.staged_softwares, slot);
        return slot;
    }

    pub fn addStagedGpu(self: *Session, value: StagedGpu) ?u32 {
        if (self.staged_gpu_count >= self.staged_gpus.len) return null;
        const slot = self.staged_gpu_count;
        self.staged_gpus[slot] = value;
        self.staged_gpu_count += 1;
        insertStagedGpuIndex(self.staged_gpu_index, self.staged_gpus, slot);
        return slot;
    }

    pub fn addStagedSoftwareGpu(self: *Session, value: StagedSoftwareGpu) ?u32 {
        if (self.staged_software_gpu_count >= self.staged_software_gpus.len) return null;
        const slot = self.staged_software_gpu_count;
        self.staged_software_gpus[slot] = value;
        self.staged_software_gpu_count += 1;
        insertStagedSoftwareGpuIndex(self.staged_software_gpu_index, self.staged_software_gpus, slot);
        return slot;
    }

    pub fn allocateProcess(self: *Session) ?u32 {
        var probes: usize = 0;
        var slot = self.process_allocate_cursor;
        while (probes < self.processes.len) : (probes += 1) {
            if (!self.processes[slot].occupied) {
                self.processes[slot] = ProcessState.empty();
                self.processes[slot].occupied = true;
                self.process_allocate_cursor = nextSlot(slot, self.processes.len);
                return slot;
            }
            slot = nextSlot(slot, self.processes.len);
        }
        return null;
    }

    pub fn allocateSoftware(self: *Session) ?u32 {
        var probes: usize = 0;
        var slot = self.software_allocate_cursor;
        while (probes < self.softwares.len) : (probes += 1) {
            if (!self.softwares[slot].occupied) {
                self.softwares[slot] = SoftwareState.empty();
                self.softwares[slot].occupied = true;
                self.software_allocate_cursor = nextSlot(slot, self.softwares.len);
                return slot;
            }
            slot = nextSlot(slot, self.softwares.len);
        }
        return null;
    }

    pub fn allocateGpu(self: *Session) ?u32 {
        var probes: usize = 0;
        var slot = self.gpu_allocate_cursor;
        while (probes < self.gpus.len) : (probes += 1) {
            if (!self.gpus[slot].occupied) {
                self.gpus[slot] = std.mem.zeroes(GpuState);
                self.gpus[slot].occupied = true;
                self.gpu_allocate_cursor = nextSlot(slot, self.gpus.len);
                return slot;
            }
            slot = nextSlot(slot, self.gpus.len);
        }
        return null;
    }

    pub fn releaseProcess(self: *Session, slot: u32) void {
        self.processes[slot] = ProcessState.empty();
        self.process_allocate_cursor = slot;
    }

    pub fn releaseSoftware(self: *Session, slot: u32) void {
        self.softwares[slot] = SoftwareState.empty();
        self.software_allocate_cursor = slot;
    }

    pub fn releaseGpu(self: *Session, slot: u32) void {
        self.gpus[slot] = std.mem.zeroes(GpuState);
        self.gpu_allocate_cursor = slot;
    }

    pub fn insertProcessIndex(self: *Session, slot: u32) void {
        insertProcessIndexInto(self.process_index, self.processes, slot);
    }

    pub fn insertSoftwareIndex(self: *Session, slot: u32) void {
        insertSoftwareIndexInto(self.software_index, self.softwares, slot);
    }

    pub fn insertGpuIndex(self: *Session, slot: u32) void {
        insertGpuIndexInto(self.gpu_index, self.gpus, slot);
    }

    pub fn findReservation(self: *Session, action_id: u64) ?u32 {
        var cursor = indexStart(numericHash(action_id), self.reservation_index);
        var probes: usize = 0;
        while (probes < self.reservation_index.len) : (probes += 1) {
            const encoded = self.reservation_index[cursor];
            if (encoded == 0) return null;
            const slot = encoded - 1;
            const item = self.reservations[slot];
            if (item.occupied and item.action.action_id == action_id) return slot;
            cursor = (cursor + 1) & (self.reservation_index.len - 1);
        }
        return null;
    }

    pub fn allocateReservation(self: *Session) ?u32 {
        var probes: usize = 0;
        var slot = self.reservation_allocate_cursor;
        while (probes < self.reservations.len) : (probes += 1) {
            if (!self.reservations[slot].occupied) {
                self.reservation_allocate_cursor = nextSlot(slot, self.reservations.len);
                return slot;
            }
            slot = nextSlot(slot, self.reservations.len);
        }
        return null;
    }

    pub fn findAtomicGroup(self: *Session, group_id: u64) ?u32 {
        var cursor = indexStart(numericHash(group_id), self.atomic_group_index);
        var probes: usize = 0;
        while (probes < self.atomic_group_index.len) : (probes += 1) {
            const encoded = self.atomic_group_index[cursor];
            if (encoded == 0) return null;
            const slot = encoded - 1;
            const item = self.atomic_groups[slot];
            if (item.occupied and item.group_id == group_id) return slot;
            cursor = (cursor + 1) & (self.atomic_group_index.len - 1);
        }
        return null;
    }

    pub fn allocateAtomicGroup(self: *Session) ?u32 {
        var probes: usize = 0;
        var slot = self.atomic_group_allocate_cursor;
        while (probes < self.atomic_groups.len) : (probes += 1) {
            if (!self.atomic_groups[slot].occupied) {
                self.atomic_group_allocate_cursor = nextSlot(slot, self.atomic_groups.len);
                return slot;
            }
            slot = nextSlot(slot, self.atomic_groups.len);
        }
        return null;
    }

    pub fn releaseReservation(self: *Session, slot: u32) void {
        if (self.reservations[slot].occupied) self.inflight_count -= 1;
        self.reservations[slot] = std.mem.zeroes(Reservation);
        self.reservation_allocate_cursor = slot;
    }

    pub fn releaseAtomicGroup(self: *Session, slot: u32) void {
        self.atomic_groups[slot] = std.mem.zeroes(AtomicGroup);
        self.atomic_group_allocate_cursor = slot;
    }

    pub fn insertReservationIndex(self: *Session, slot: u32) void {
        insertNumericIndex(self.reservation_index, slot, self.reservations[slot].action.action_id);
    }

    pub fn insertAtomicGroupIndex(self: *Session, slot: u32) void {
        insertNumericIndex(self.atomic_group_index, slot, self.atomic_groups[slot].group_id);
    }

    pub fn rebuildReservationIndex(self: *Session) void {
        @memset(self.reservation_index, 0);
        for (self.reservations, 0..) |item, slot| {
            if (item.occupied) self.insertReservationIndex(@intCast(slot));
        }
    }

    pub fn rebuildAtomicGroupIndex(self: *Session) void {
        @memset(self.atomic_group_index, 0);
        for (self.atomic_groups, 0..) |item, slot| {
            if (item.occupied) self.insertAtomicGroupIndex(@intCast(slot));
        }
    }

    pub fn inflightCount(self: *const Session) u32 {
        return self.inflight_count;
    }

    pub fn bumpRevision(self: *Session) void {
        self.state_revision +%= 1;
        if (self.state_revision == 0) self.state_revision = 1;
    }

    pub fn takeActionId(self: *Session) u64 {
        const result = self.next_action_id;
        self.next_action_id +%= 1;
        if (self.next_action_id == 0) self.next_action_id = 1;
        return result;
    }

    pub fn takeGroupId(self: *Session) u64 {
        const result = self.next_group_id;
        self.next_group_id +%= 1;
        if (self.next_group_id == 0) self.next_group_id = 1;
        return result;
    }

    pub fn takeValidationEpoch(self: *Session) u64 {
        const result = self.validation_epoch;
        self.validation_epoch +%= 1;
        if (self.validation_epoch == 0) {
            self.validation_epoch = 1;
            for (self.reservations) |*reservation| reservation.validation_epoch = 0;
        }
        return result;
    }

    fn hasOwnedState(self: *const Session) bool {
        for (self.processes) |item| if (item.occupied and item.transition.owned) return true;
        for (self.softwares) |item| {
            if (item.occupied and (item.cpu_transition.owned or item.gpu_transition.owned)) return true;
        }
        return false;
    }
};

fn sameCapacityShape(left: *const protocol.Config, right: *const protocol.Config) bool {
    return left.max_processes == right.max_processes and
        left.max_software_groups == right.max_software_groups and
        left.max_gpu_states == right.max_gpu_states and
        left.max_input_rows == right.max_input_rows and
        left.max_actions == right.max_actions and
        left.max_reservations == right.max_reservations and
        left.max_atomic_groups == right.max_atomic_groups;
}

fn nextSlot(slot: u32, length: usize) u32 {
    const next = @as(usize, slot) + 1;
    return if (next == length) 0 else @intCast(next);
}

fn hashIndexCapacity(maximum: u32) !usize {
    const doubled = std.math.mul(u64, maximum, 2) catch return error.OutOfMemory;
    var capacity: u64 = 2;
    while (capacity < doubled) {
        capacity = std.math.mul(u64, capacity, 2) catch return error.OutOfMemory;
    }
    if (capacity > std.math.maxInt(usize)) return error.OutOfMemory;
    return @intCast(capacity);
}

fn mix64(input: u64) u64 {
    var value = input;
    value ^= value >> 30;
    value *%= 0xBF58476D1CE4E5B9;
    value ^= value >> 27;
    value *%= 0x94D049BB133111EB;
    return value ^ (value >> 31);
}

fn numericHash(value: u64) u64 {
    return mix64(value);
}

fn insertNumericIndex(index: []u32, slot: u32, value: u64) void {
    var cursor = indexStart(numericHash(value), index);
    while (index[cursor] != 0) cursor = (cursor + 1) & (index.len - 1);
    index[cursor] = slot + 1;
}

pub fn processHash(target_key: u64, process_id: u32, start_key: u64) u64 {
    return mix64(target_key ^ std.math.rotl(u64, start_key, 23) ^ (@as(u64, process_id) *% 0x9E3779B97F4A7C15));
}

pub fn softwareHash(software_key: u64) u64 {
    return mix64(software_key);
}

pub fn gpuHash(target_key: u64, process_id: u32, start_key: u64, device_index: u32) u64 {
    return mix64(processHash(target_key, process_id, start_key) ^ (@as(u64, device_index) *% 0xD6E8FEB86659FD93));
}

fn indexStart(hash: u64, index: []const u32) usize {
    return @intCast(hash & @as(u64, @intCast(index.len - 1)));
}

fn findProcessIn(index: []const u32, values: []const ProcessState, target_key: u64, process_id: u32, start_key: u64) ?u32 {
    var cursor = indexStart(processHash(target_key, process_id, start_key), index);
    var probes: usize = 0;
    while (probes < index.len) : (probes += 1) {
        const encoded = index[cursor];
        if (encoded == 0) return null;
        const slot = encoded - 1;
        const item = values[slot];
        if (item.occupied and item.target_key == target_key and item.process_id == process_id and item.process_start_key == start_key) return slot;
        cursor = (cursor + 1) & (index.len - 1);
    }
    return null;
}

fn findStagedProcessIn(index: []const u32, values: []const StagedProcess, target_key: u64, process_id: u32, start_key: u64) ?u32 {
    var cursor = indexStart(processHash(target_key, process_id, start_key), index);
    var probes: usize = 0;
    while (probes < index.len) : (probes += 1) {
        const encoded = index[cursor];
        if (encoded == 0) return null;
        const slot = encoded - 1;
        const item = values[slot];
        if (item.occupied and item.target_key == target_key and item.process_id == process_id and item.process_start_key == start_key) return slot;
        cursor = (cursor + 1) & (index.len - 1);
    }
    return null;
}

fn insertProcessIndexInto(index: []u32, values: []const ProcessState, slot: u32) void {
    const item = values[slot];
    insertEncoded(index, processHash(item.target_key, item.process_id, item.process_start_key), slot + 1);
}

fn insertStagedProcessIndex(index: []u32, values: []const StagedProcess, slot: u32) void {
    const item = values[slot];
    insertEncoded(index, processHash(item.target_key, item.process_id, item.process_start_key), slot + 1);
}

fn findSoftwareIn(index: []const u32, values: []const SoftwareState, key: u64) ?u32 {
    var cursor = indexStart(softwareHash(key), index);
    var probes: usize = 0;
    while (probes < index.len) : (probes += 1) {
        const encoded = index[cursor];
        if (encoded == 0) return null;
        const slot = encoded - 1;
        const item = values[slot];
        if (item.occupied and item.software_key == key) return slot;
        cursor = (cursor + 1) & (index.len - 1);
    }
    return null;
}

fn findStagedSoftwareIn(index: []const u32, values: []const StagedSoftware, key: u64) ?u32 {
    var cursor = indexStart(softwareHash(key), index);
    var probes: usize = 0;
    while (probes < index.len) : (probes += 1) {
        const encoded = index[cursor];
        if (encoded == 0) return null;
        const slot = encoded - 1;
        const item = values[slot];
        if (item.occupied and item.software_key == key) return slot;
        cursor = (cursor + 1) & (index.len - 1);
    }
    return null;
}

fn insertSoftwareIndexInto(index: []u32, values: []const SoftwareState, slot: u32) void {
    insertEncoded(index, softwareHash(values[slot].software_key), slot + 1);
}

fn insertStagedSoftwareIndex(index: []u32, values: []const StagedSoftware, slot: u32) void {
    insertEncoded(index, softwareHash(values[slot].software_key), slot + 1);
}

fn findGpuIn(index: []const u32, values: []const GpuState, target_key: u64, process_id: u32, start_key: u64, device_index: u32) ?u32 {
    var cursor = indexStart(gpuHash(target_key, process_id, start_key, device_index), index);
    var probes: usize = 0;
    while (probes < index.len) : (probes += 1) {
        const encoded = index[cursor];
        if (encoded == 0) return null;
        const slot = encoded - 1;
        const item = values[slot];
        if (item.occupied and item.target_key == target_key and item.process_id == process_id and
            item.process_start_key == start_key and item.device_index == device_index) return slot;
        cursor = (cursor + 1) & (index.len - 1);
    }
    return null;
}

fn findStagedGpuIn(index: []const u32, values: []const StagedGpu, target_key: u64, process_id: u32, start_key: u64, device_index: u32) ?u32 {
    var cursor = indexStart(gpuHash(target_key, process_id, start_key, device_index), index);
    var probes: usize = 0;
    while (probes < index.len) : (probes += 1) {
        const encoded = index[cursor];
        if (encoded == 0) return null;
        const slot = encoded - 1;
        const item = values[slot];
        if (item.occupied and item.target_key == target_key and item.process_id == process_id and
            item.process_start_key == start_key and item.device_index == device_index) return slot;
        cursor = (cursor + 1) & (index.len - 1);
    }
    return null;
}

fn insertGpuIndexInto(index: []u32, values: []const GpuState, slot: u32) void {
    const item = values[slot];
    insertEncoded(index, gpuHash(item.target_key, item.process_id, item.process_start_key, item.device_index), slot + 1);
}

fn insertStagedGpuIndex(index: []u32, values: []const StagedGpu, slot: u32) void {
    const item = values[slot];
    insertEncoded(index, gpuHash(item.target_key, item.process_id, item.process_start_key, item.device_index), slot + 1);
}

fn findStagedSoftwareGpuIn(index: []const u32, values: []const StagedSoftwareGpu, software_slot: u32, device_index: u32) ?u32 {
    const hash = mix64((@as(u64, software_slot) << 32) | device_index);
    var cursor = indexStart(hash, index);
    var probes: usize = 0;
    while (probes < index.len) : (probes += 1) {
        const encoded = index[cursor];
        if (encoded == 0) return null;
        const slot = encoded - 1;
        const item = values[slot];
        if (item.occupied and item.software_slot == software_slot and item.device_index == device_index) return slot;
        cursor = (cursor + 1) & (index.len - 1);
    }
    return null;
}

fn insertStagedSoftwareGpuIndex(index: []u32, values: []const StagedSoftwareGpu, slot: u32) void {
    const item = values[slot];
    insertEncoded(index, mix64((@as(u64, item.software_slot) << 32) | item.device_index), slot + 1);
}

fn insertEncoded(index: []u32, hash: u64, encoded: u32) void {
    var cursor = indexStart(hash, index);
    while (index[cursor] != 0) cursor = (cursor + 1) & (index.len - 1);
    index[cursor] = encoded;
}
