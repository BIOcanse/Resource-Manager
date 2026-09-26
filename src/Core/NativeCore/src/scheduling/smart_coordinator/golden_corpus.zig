const protocol = @import("protocol.zig");

pub const default_process_capacity: u32 = 8;
pub const default_software_capacity: u32 = 4;
pub const default_gpu_capacity: u32 = 8;
pub const default_input_capacity: u32 = 64;
pub const default_action_capacity: u32 = default_process_capacity + default_software_capacity;

pub fn explicitConfig(feature_flags: u64) protocol.Config {
    const state_multipliers = [_]f64{ 1.0, 1.35, 1.12, 0.75, 0.55, 0.45, 0.0 };
    const adapter_policy = protocol.AdapterPolicyConfig{
        .state_multipliers = state_multipliers,
        .extreme_minimum_score = 120.0,
        .normal_minimum_score = 60.0,
        .optimize_minimum_score = 25.0,
    };
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = 1,
        .field_mask = protocol.ConfigFields.required,
        .feature_flags = feature_flags,
        .max_processes = default_process_capacity,
        .max_software_groups = default_software_capacity,
        .max_gpu_states = default_gpu_capacity,
        .max_input_rows = default_input_capacity,
        .max_actions = default_action_capacity,
        .max_reservations = default_process_capacity + 2 * default_software_capacity,
        .max_atomic_groups = default_action_capacity,
        .normal_interval_ms = 15_000,
        .event_interval_ms = 1_000,
        .event_boost_ms = 10_000,
        .game_start_grace_ms = 10_000,
        .required_consecutive_decisions = 3,
        .failure_retry_ms = 5_000,
        .reservation_timeout_ms = 30_000,
        .reserved_timing = [_]u32{0} ** 3,
        .process_state_multipliers = state_multipliers,
        .a1_minimum_cpu_score = 120.0,
        .default_minimum_cpu_score_scale = 70.0,
        .level1_maximum_cpu_score_scale = 60.0,
        .level2_maximum_cpu_score_scale = 50.0,
        .level3_maximum_cpu_score_scale = 34.0,
        .low_tier_level4_maximum_cpu_score_scale = 34.0,
        .reserved_process_policy0 = 0,
        .high_tier_minimum_base_score = 81.0,
        .middle_tier_minimum_base_score = 21.0,
        .cpu_adapter = adapter_policy,
        .gpu_adapter = adapter_policy,
        .reserved = [_]u64{0} ** 4,
    };
}

pub fn cycle(
    config: *const protocol.Config,
    sequence: u64,
    observed_at_ms: i64,
    input_count: u32,
    authoritative: bool,
) protocol.CycleInput {
    var flags = protocol.CycleFlags.full_process_snapshot;
    if (authoritative) flags |= protocol.CycleFlags.authoritative_applied_facts;
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CycleInput),
        .input_row_struct_size = @sizeOf(protocol.InputRow),
        .action_struct_size = @sizeOf(protocol.Action),
        .config_generation = config.generation,
        .cycle_sequence = sequence,
        .observed_at_ms = observed_at_ms,
        .valid_mask = protocol.CycleValidity.score_only_mode | protocol.CycleValidity.maximum_actions_this_cycle |
            protocol.CycleValidity.score_scheduling_generation |
            protocol.CycleValidity.cpu_score_source_fingerprint |
            protocol.CycleValidity.gpu_score_source_fingerprint,
        .flags = flags,
        .input_count = input_count,
        .action_capacity = config.max_actions,
        .score_scheduling_generation = sequence,
        .cpu_score_source_fingerprint = 0xC000_0000_0000_0000 | sequence,
        .gpu_score_source_fingerprint = 0xD000_0000_0000_0000 | sequence,
        .maximum_actions_this_cycle = config.max_actions,
        .reserved = 0,
    };
}

pub fn plan(
    session: *@import("session.zig").Session,
    input: *const protocol.CycleInput,
    input_rows: ?[*]const protocol.InputRow,
    input_row_capacity: u32,
    actions_pointer: ?[*]protocol.Action,
    action_capacity: u32,
    snapshot: *protocol.Snapshot,
) protocol.Status {
    return @import("session.zig").plan(session, input, input_rows, input_row_capacity, actions_pointer, action_capacity, snapshot);
}

pub fn processFact(
    source_index: u32,
    target_key: u64,
    software_key: u64,
    process_id: u32,
    start_key: u64,
    base_score: f64,
    software_kind: protocol.SoftwareKind,
    surface_flags: u64,
    applied_process_grade: i8,
    applied_cpu_grade: protocol.AdapterGrade,
    applied_gpu_grade: protocol.AdapterGrade,
    ownership_flags: u64,
) protocol.InputRow {
    return .{
        .struct_size = @sizeOf(protocol.InputRow),
        .metric_kind = @intFromEnum(protocol.MetricKind.none),
        .software_kind = @intFromEnum(software_kind),
        .protection_level = 0,
        .reserved0 = 0,
        .valid_mask = protocol.InputValidity.process_identity |
            protocol.InputValidity.software_identity | protocol.InputValidity.software_kind |
            protocol.InputValidity.base_score | protocol.InputValidity.surface_facts |
            protocol.InputValidity.eligibility | protocol.InputValidity.protection |
            protocol.InputValidity.cpu_capabilities | protocol.InputValidity.gpu_capabilities |
            protocol.InputValidity.applied_process_grade | protocol.InputValidity.applied_cpu_grade |
            protocol.InputValidity.applied_gpu_grade,
        .flags = protocol.InputFlags.running | surface_flags |
            protocol.InputFlags.process_cpu_metrics_complete | protocol.InputFlags.process_gpu_metrics_complete |
            protocol.InputFlags.can_apply_process_policy | protocol.InputFlags.can_apply_adapter_policy |
            protocol.InputFlags.hardware_scheduling_eligible | ownership_flags,
        .target_key = target_key,
        .software_key = software_key,
        .process_start_key = start_key,
        .base_score = base_score,
        .metric_value = 0,
        .source_index = source_index,
        .process_id = process_id,
        .device_index = 0,
        .score_member_count = 0,
        .cpu_capability_mask = allAdapterCapabilities(),
        .gpu_capability_mask = allAdapterCapabilities(),
        .applied_process_grade = applied_process_grade,
        .applied_cpu_grade = @intFromEnum(applied_cpu_grade),
        .applied_gpu_grade = @intFromEnum(applied_gpu_grade),
        .reserved2 = [_]u8{0} ** 3,
        .applied_epoch = if (ownership_flags == 0) 0 else 1,
        .process_score = 0,
        .software_score = 0,
    };
}

pub fn softwareFact(
    source_index: u32,
    software_key: u64,
    applied_cpu_grade: protocol.AdapterGrade,
    applied_gpu_grade: protocol.AdapterGrade,
    ownership_flags: u64,
    applied_epoch: u64,
) protocol.InputRow {
    return .{
        .struct_size = @sizeOf(protocol.InputRow),
        .metric_kind = @intFromEnum(protocol.MetricKind.none),
        .software_kind = @intFromEnum(protocol.SoftwareKind.unknown),
        .protection_level = 0,
        .reserved0 = 0,
        .valid_mask = protocol.InputValidity.software_identity |
            protocol.InputValidity.applied_cpu_grade |
            protocol.InputValidity.applied_gpu_grade,
        .flags = ownership_flags,
        .target_key = 0,
        .software_key = software_key,
        .process_start_key = 0,
        .base_score = 0,
        .metric_value = 0,
        .source_index = source_index,
        .process_id = 0,
        .device_index = 0,
        .score_member_count = 0,
        .cpu_capability_mask = 0,
        .gpu_capability_mask = 0,
        .applied_process_grade = protocol.ProcessGrade.normal,
        .applied_cpu_grade = @intFromEnum(applied_cpu_grade),
        .applied_gpu_grade = @intFromEnum(applied_gpu_grade),
        .reserved2 = [_]u8{0} ** 3,
        .applied_epoch = applied_epoch,
        .process_score = 0,
        .software_score = 0,
    };
}

pub fn metricFact(base: protocol.InputRow, source_index: u32, kind: protocol.MetricKind, value: f64, device_index: u32) protocol.InputRow {
    var row = base;
    row.source_index = source_index;
    row.metric_kind = @intFromEnum(kind);
    row.metric_value = value;
    row.device_index = device_index;
    row.valid_mask |= protocol.InputValidity.metric;
    if ((kind == .cpu_usage_percent or kind == .gpu_usage_percent) and
        (row.valid_mask & protocol.InputValidity.software_identity) != 0)
    {
        const multiplier = runtimeMultiplier(row.flags);
        const score = row.base_score * multiplier * (value / 100);
        row.valid_mask |= protocol.InputValidity.process_score |
            protocol.InputValidity.software_score |
            protocol.InputValidity.score_member_count;
        row.process_score = score;
        row.software_score = score;
        row.score_member_count = 1;
    }
    return row;
}

pub fn setSoftwareScore(row: *protocol.InputRow, score: f64, member_count: u32) void {
    row.valid_mask |= protocol.InputValidity.software_score |
        protocol.InputValidity.score_member_count;
    row.software_score = score;
    row.score_member_count = member_count;
}

pub fn setCanonicalScores(
    row: *protocol.InputRow,
    process_score: f64,
    software_score: f64,
    member_count: u32,
) void {
    row.valid_mask |= protocol.InputValidity.process_score |
        protocol.InputValidity.software_score |
        protocol.InputValidity.score_member_count;
    row.process_score = process_score;
    row.software_score = software_score;
    row.score_member_count = member_count;
}

fn runtimeMultiplier(flags: u64) f64 {
    if ((flags & protocol.InputFlags.running) == 0) return 0;
    if ((flags & protocol.InputFlags.foreground_focused) != 0) return 1.35;
    if ((flags & protocol.InputFlags.has_visible_window) != 0) return 1.12;
    if ((flags & protocol.InputFlags.has_background_window) != 0) return 0.75;
    if ((flags & protocol.InputFlags.has_hidden_window) != 0) return 0.55;
    return 0.45;
}

pub fn feedbackFor(action: protocol.Action, status: protocol.FeedbackStatus, completed_at_ms: i64) protocol.Feedback {
    var flags: u32 = 0;
    var valid_mask = protocol.FeedbackValidity.completed_at;
    const reports_target = status == .succeeded;
    const reports_source = status == .failed_unchanged or status == .rejected;
    const reports_grade = reports_target or reports_source;
    const process_grade = if (!reports_grade or
        (action.domain_mask & protocol.GradeDomains.process) == 0)
        0
    else if (reports_target)
        action.to_process_grade
    else
        action.from_process_grade;
    const cpu_grade = if (!reports_grade or
        (action.domain_mask & protocol.GradeDomains.cpu) == 0)
        0
    else if (reports_target)
        action.to_cpu_grade
    else
        action.from_cpu_grade;
    const gpu_grade = if (!reports_grade or
        (action.domain_mask & protocol.GradeDomains.gpu) == 0)
        0
    else if (reports_target)
        action.to_gpu_grade
    else
        action.from_gpu_grade;
    if (reports_grade and (action.domain_mask & protocol.GradeDomains.process) != 0) {
        valid_mask |= protocol.FeedbackValidity.actual_process_grade;
        if (reports_target and process_grade != protocol.ProcessGrade.normal) flags |= protocol.FeedbackFlags.process_owned;
    }
    const normal = @intFromEnum(protocol.AdapterGrade.normal);
    if (reports_grade and (action.domain_mask & protocol.GradeDomains.cpu) != 0) {
        valid_mask |= protocol.FeedbackValidity.actual_cpu_grade;
        if (reports_target and cpu_grade != normal) flags |= protocol.FeedbackFlags.cpu_owned;
    }
    if (reports_grade and (action.domain_mask & protocol.GradeDomains.gpu) != 0) {
        valid_mask |= protocol.FeedbackValidity.actual_gpu_grade;
        if (reports_target and gpu_grade != normal) flags |= protocol.FeedbackFlags.gpu_owned;
    }
    if ((flags & (protocol.FeedbackFlags.process_owned | protocol.FeedbackFlags.cpu_owned |
        protocol.FeedbackFlags.gpu_owned)) != 0)
    {
        flags |= protocol.FeedbackFlags.rollback_payload_persisted;
    }
    return .{
        .struct_size = @sizeOf(protocol.Feedback),
        .flags = flags,
        .valid_mask = valid_mask,
        .action_id = action.action_id,
        .plan_epoch = action.plan_epoch,
        .config_generation = action.config_generation,
        .target_key = action.target_key,
        .software_key = action.software_key,
        .process_start_key = action.process_start_key,
        .completed_at_ms = completed_at_ms,
        .process_id = action.process_id,
        .system_error_code = 0,
        .status = @intFromEnum(status),
        .scope = action.scope,
        .actual_process_grade = process_grade,
        .actual_cpu_grade = cpu_grade,
        .actual_gpu_grade = gpu_grade,
        .reserved0 = [_]u8{0} ** 3,
        .reserved1 = 0,
    };
}

pub fn allAdapterCapabilities() u8 {
    return (@as(u8, 1) << protocol.adapter_grade_count) - 1;
}

pub fn layoutFingerprint(comptime T: type) u64 {
    const fields = @typeInfo(T).@"struct".fields;
    var hash: u64 = 0xcbf29ce484222325;
    hash = fingerprintInteger(hash, @sizeOf(T));
    hash = fingerprintInteger(hash, @alignOf(T));
    hash = fingerprintInteger(hash, fields.len);
    inline for (fields) |field| {
        inline for (field.name) |byte| {
            hash = (hash ^ byte) *% 0x100000001b3;
        }
        hash = (hash ^ 0xff) *% 0x100000001b3;
        hash = fingerprintInteger(hash, @offsetOf(T, field.name));
        hash = fingerprintInteger(hash, @sizeOf(field.type));
        hash = fingerprintInteger(hash, @alignOf(field.type));
    }
    return hash;
}

fn fingerprintInteger(initial: u64, value: usize) u64 {
    var hash = initial;
    var remaining: u64 = @intCast(value);
    for (0..8) |_| {
        hash = (hash ^ @as(u8, @truncate(remaining))) *% 0x100000001b3;
        remaining >>= 8;
    }
    return hash;
}
