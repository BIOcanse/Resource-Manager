const std = @import("std");

pub const abi_version: u32 = 0x0001_0000;
pub const no_slot: u32 = std.math.maxInt(u32);

pub const Handle128 = extern struct {
    high: u64,
    low: u64,

    pub fn isZero(self: Handle128) bool {
        return self.high == 0 and self.low == 0;
    }
};

pub const OperationState = enum(u32) {
    queued = 1,
    start_pending = 2,
    running = 3,
    cancel_pending = 4,
    retry_wait = 5,
    recovery_pending = 6,
    succeeded = 7,
    failed = 8,
    canceled = 9,
    state_uncertain = 10,
};

pub const ActionKind = enum(u32) {
    start = 1,
    cancel = 2,
    recover = 3,
};

pub const ActionFeedbackOutcome = enum(u32) {
    started = 1,
    start_retryable_failure = 2,
    start_terminal_failure = 3,
    cancel_completed = 4,
    cancel_retryable_failure = 5,
    recovered_queued = 6,
    recovered_succeeded = 7,
    recovered_failed = 8,
    recovered_canceled = 9,
    recovered_uncertain = 10,
};

pub const CompletionOutcome = enum(u32) {
    succeeded = 1,
    retryable_failure = 2,
    terminal_failure = 3,
    canceled = 4,
    state_uncertain = 5,
};

pub const ConfigFlags = struct {
    pub const known: u64 = 0;
};

pub const SubmitValid = struct {
    pub const domain: u64 = 1 << 0;
    pub const title: u64 = 1 << 1;
    pub const request: u64 = 1 << 2;
    pub const known: u64 = domain | title | request;
};

pub const OperationFlags = struct {
    pub const domain_valid: u64 = 1 << 0;
    pub const title_valid: u64 = 1 << 1;
    pub const request_valid: u64 = 1 << 2;
    pub const result_valid: u64 = 1 << 3;
    pub const error_valid: u64 = 1 << 4;
    pub const progress_valid: u64 = 1 << 5;
    pub const cancel_requested: u64 = 1 << 6;
    pub const terminal: u64 = 1 << 7;
    pub const imported_recovery: u64 = 1 << 8;
    pub const known: u64 = domain_valid | title_valid | request_valid |
        result_valid | error_valid | progress_valid | cancel_requested |
        terminal | imported_recovery;
};

pub const SubmitOutputFlags = struct {
    pub const inserted: u32 = 1 << 0;
    pub const active_domain_duplicate: u32 = 1 << 1;
    pub const operation_id_duplicate: u32 = 1 << 2;
    pub const known: u32 = inserted | active_domain_duplicate | operation_id_duplicate;
};

pub const ActionFlags = struct {
    pub const cancel_requested: u64 = 1 << 0;
    pub const execution_timeout: u64 = 1 << 1;
    pub const imported_recovery: u64 = 1 << 2;
    pub const known: u64 = cancel_requested | execution_timeout | imported_recovery;
};

pub const FeedbackValid = struct {
    pub const result: u64 = 1 << 0;
    pub const error_handle: u64 = 1 << 1;
    pub const known: u64 = result | error_handle;
};

pub const CompletionValid = FeedbackValid;

pub const ProgressValid = struct {
    pub const percent_milli: u64 = 1 << 0;
    pub const bytes_done: u64 = 1 << 1;
    pub const bytes_total: u64 = 1 << 2;
    pub const speed_bytes_per_second: u64 = 1 << 3;
    pub const stage: u64 = 1 << 4;
    pub const message: u64 = 1 << 5;
    pub const checkpoint: u64 = 1 << 6;
    pub const known: u64 = percent_milli | bytes_done | bytes_total |
        speed_bytes_per_second | stage | message | checkpoint;
};

pub const SnapshotFlags = struct {
    pub const next_wake_valid: u64 = 1 << 0;
    pub const known: u64 = next_wake_valid;
};

pub const PersistenceFlags = struct {
    pub const known: u64 = 0;
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    session_instance_id: Handle128,
    clock_instance_id: Handle128,
    maximum_operation_count: u32,
    maximum_domain_count: u32,
    maximum_action_count: u32,
    operation_index_capacity: u32,
    domain_index_capacity: u32,
    maximum_global_running_count: u32,
    maximum_recent_terminal_count: u32,
    maximum_read_count: u32,
    maximum_start_actions_per_plan: u32,
    maximum_cancel_actions_per_plan: u32,
    maximum_recover_actions_per_plan: u32,
    reserved_u32: u32,
    maximum_future_skew_milliseconds: u64,
    maximum_persistence_byte_count: u64,
    resident_byte_budget: u64,
    flags: u64,
    reserved: [5]u64,
};

pub const Capacity = extern struct {
    struct_size: u32,
    operation_capacity: u32,
    domain_capacity: u32,
    action_capacity: u32,
    operation_index_capacity: u32,
    domain_index_capacity: u32,
    order_scratch_capacity: u32,
    persistence_capacity: u32,
    session_instance_id: Handle128,
    clock_instance_id: Handle128,
    maximum_persistence_byte_count: u64,
    resident_byte_count: u64,
    reserved: [4]u64,
};

pub const SubmitInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    submit_epoch: u64,
    operation_id: Handle128,
    domain_id: Handle128,
    kind_handle: Handle128,
    title_handle: Handle128,
    request_handle: Handle128,
    priority: i64,
    maximum_attempts: u32,
    retry_delay_milliseconds: u32,
    execution_timeout_milliseconds: u64,
    cancel_grace_milliseconds: u64,
    terminal_retention_milliseconds: u64,
    created_utc_milliseconds: i64,
    created_monotonic_milliseconds: u64,
    observed_utc_milliseconds: i64,
    observed_monotonic_milliseconds: u64,
    valid_mask: u64,
    flags: u64,
    reserved: [4]u64,
};

pub const SubmitOutput = extern struct {
    struct_size: u32,
    flags: u32,
    operation_id: Handle128,
    state_revision: u64,
    submit_sequence: u64,
    state: u32,
    reserved_u32: u32,
    reserved: [3]u64,
};

pub const CancelInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    request_epoch: u64,
    operation_id: Handle128,
    observed_utc_milliseconds: i64,
    observed_monotonic_milliseconds: u64,
    flags: u64,
    reserved: [4]u64,
};

pub const CancelOutput = extern struct {
    struct_size: u32,
    state: u32,
    operation_id: Handle128,
    state_revision: u64,
    flags: u64,
    reserved: [3]u64,
};

pub const PlanInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    plan_epoch: u64,
    observed_utc_milliseconds: i64,
    observed_monotonic_milliseconds: u64,
    action_capacity: u32,
    flags: u32,
    reserved: [4]u64,
};

pub const PlanOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    plan_epoch: u64,
    state_revision: u64,
    action_count: u32,
    active_operation_count: u32,
    running_operation_count: u32,
    terminal_operation_count: u32,
    next_wake_monotonic_milliseconds: u64,
    flags: u64,
    reserved: [4]u64,
};

pub const ActionOutput = extern struct {
    struct_size: u32,
    kind: u32,
    action_id: u64,
    configuration_generation: u64,
    operation_id: Handle128,
    attempt_token: Handle128,
    plan_epoch: u64,
    attempt_number: u32,
    reserved_u32: u32,
    deadline_monotonic_milliseconds: u64,
    flags: u64,
    reserved: [3]u64,
};

pub const ActionFeedbackInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    feedback_epoch: u64,
    action_id: u64,
    plan_epoch: u64,
    operation_id: Handle128,
    attempt_token: Handle128,
    action_kind: u32,
    outcome: u32,
    observed_utc_milliseconds: i64,
    observed_monotonic_milliseconds: u64,
    result_handle: Handle128,
    error_handle: Handle128,
    valid_mask: u64,
    flags: u64,
    reserved: [3]u64,
};

pub const CompletionInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    completion_epoch: u64,
    operation_id: Handle128,
    attempt_token: Handle128,
    outcome: u32,
    reserved_u32: u32,
    observed_utc_milliseconds: i64,
    observed_monotonic_milliseconds: u64,
    result_handle: Handle128,
    error_handle: Handle128,
    valid_mask: u64,
    flags: u64,
    reserved: [3]u64,
};

pub const ProgressInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_id: Handle128,
    attempt_token: Handle128,
    progress_sequence: u64,
    observed_utc_milliseconds: i64,
    observed_monotonic_milliseconds: u64,
    percent_milli: u32,
    reserved_u32: u32,
    bytes_done: u64,
    bytes_total: u64,
    speed_bytes_per_second: u64,
    stage_handle: Handle128,
    message_handle: Handle128,
    checkpoint_handle: Handle128,
    valid_mask: u64,
    flags: u64,
    reserved: [3]u64,
};

pub const SnapshotOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    state_revision: u64,
    last_plan_epoch: u64,
    last_observed_utc_milliseconds: i64,
    last_observed_monotonic_milliseconds: u64,
    next_wake_monotonic_milliseconds: u64,
    operation_count: u32,
    active_operation_count: u32,
    running_operation_count: u32,
    terminal_operation_count: u32,
    action_count: u32,
    reserved_u32: u32,
    flags: u64,
    reserved: [4]u64,
};

pub const ReadInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    state_revision: u64,
    plan_epoch: u64,
    flags: u64,
    reserved: [3]u64,
};

pub const OperationOutput = extern struct {
    struct_size: u32,
    state: u32,
    operation_id: Handle128,
    domain_id: Handle128,
    kind_handle: Handle128,
    title_handle: Handle128,
    request_handle: Handle128,
    result_handle: Handle128,
    error_handle: Handle128,
    attempt_token: Handle128,
    priority: i64,
    submit_sequence: u64,
    state_revision: u64,
    created_utc_milliseconds: i64,
    created_monotonic_milliseconds: u64,
    updated_utc_milliseconds: i64,
    updated_monotonic_milliseconds: u64,
    started_monotonic_milliseconds: u64,
    retry_at_monotonic_milliseconds: u64,
    terminal_at_monotonic_milliseconds: u64,
    terminal_retire_at_monotonic_milliseconds: u64,
    maximum_attempts: u32,
    attempt_number: u32,
    retry_delay_milliseconds: u32,
    recovery_origin_state: u32,
    execution_timeout_milliseconds: u64,
    cancel_grace_milliseconds: u64,
    terminal_retention_milliseconds: u64,
    progress_sequence: u64,
    progress_valid_mask: u64,
    percent_milli: u32,
    reserved_u32: u32,
    bytes_done: u64,
    bytes_total: u64,
    speed_bytes_per_second: u64,
    stage_handle: Handle128,
    message_handle: Handle128,
    checkpoint_handle: Handle128,
    flags: u64,
    attempt_configuration_generation: u64,
    queue_order: u64,
    reserved: [2]u64,
};

pub const PersistenceInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    flags: u64,
    reserved: [4]u64,
};

pub const PersistenceHeader = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    session_instance_id: Handle128,
    clock_instance_id: Handle128,
    operation_epoch: u64,
    state_revision: u64,
    submit_sequence: u64,
    last_submit_epoch: u64,
    last_request_epoch: u64,
    last_feedback_epoch: u64,
    last_completion_epoch: u64,
    last_plan_epoch: u64,
    last_observed_utc_milliseconds: i64,
    last_observed_monotonic_milliseconds: u64,
    next_action_id: u64,
    operation_count: u32,
    record_size: u32,
    flags: u64,
    checksum_high: u64,
    checksum_low: u64,
    reserved: [4]u64,
};

pub const PersistenceRecord = OperationOutput;

pub const PersistenceImportInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    observed_utc_milliseconds: i64,
    observed_monotonic_milliseconds: u64,
    clock_instance_id: Handle128,
    flags: u64,
    reserved: [4]u64,
};

pub fn validConfig(config: *const Config) bool {
    if (config.abi_version != abi_version or
        config.struct_size != @sizeOf(Config) or
        config.generation == 0 or
        config.session_instance_id.isZero() or
        config.clock_instance_id.isZero() or
        config.maximum_operation_count == 0 or
        config.maximum_domain_count == 0 or
        config.maximum_domain_count > config.maximum_operation_count or
        config.maximum_action_count == 0 or
        config.maximum_action_count > config.maximum_operation_count or
        config.maximum_global_running_count == 0 or
        config.maximum_global_running_count > config.maximum_operation_count or
        config.maximum_recent_terminal_count > config.maximum_operation_count or
        config.maximum_read_count != config.maximum_operation_count or
        config.maximum_start_actions_per_plan == 0 or
        config.maximum_start_actions_per_plan > config.maximum_action_count or
        config.maximum_cancel_actions_per_plan == 0 or
        config.maximum_cancel_actions_per_plan > config.maximum_action_count or
        config.maximum_recover_actions_per_plan == 0 or
        config.maximum_recover_actions_per_plan > config.maximum_action_count or
        !validIndexCapacity(config.operation_index_capacity, config.maximum_operation_count) or
        !validIndexCapacity(config.domain_index_capacity, config.maximum_domain_count) or
        config.maximum_future_skew_milliseconds == 0 or
        config.maximum_persistence_byte_count <
            @sizeOf(PersistenceHeader) +
                @as(u64, config.maximum_operation_count) * @sizeOf(PersistenceRecord) or
        config.resident_byte_budget == 0 or
        (config.flags & ~ConfigFlags.known) != 0 or
        config.reserved_u32 != 0 or
        !allZero(&config.reserved))
    {
        return false;
    }
    return true;
}

pub fn validSubmit(input: *const SubmitInput, config: *const Config) bool {
    return commonInput(input.abi_version, input.struct_size, @sizeOf(SubmitInput), input.configuration_generation, config) and
        input.submit_epoch != 0 and
        !input.operation_id.isZero() and
        !input.kind_handle.isZero() and
        input.maximum_attempts != 0 and
        input.execution_timeout_milliseconds != 0 and
        input.cancel_grace_milliseconds != 0 and
        input.terminal_retention_milliseconds != 0 and
        input.created_utc_milliseconds >= 0 and
        input.created_monotonic_milliseconds != 0 and
        input.observed_utc_milliseconds >= 0 and
        input.observed_monotonic_milliseconds >=
            input.created_monotonic_milliseconds and
        (input.valid_mask & ~SubmitValid.known) == 0 and
        input.flags == 0 and allZero(&input.reserved) and
        handleMatchesValidity(input.domain_id, input.valid_mask, SubmitValid.domain) and
        handleMatchesValidity(input.title_handle, input.valid_mask, SubmitValid.title) and
        handleMatchesValidity(input.request_handle, input.valid_mask, SubmitValid.request);
}

pub fn validCancel(input: *const CancelInput, config: *const Config) bool {
    return commonInput(input.abi_version, input.struct_size, @sizeOf(CancelInput), input.configuration_generation, config) and
        input.request_epoch != 0 and !input.operation_id.isZero() and
        input.observed_utc_milliseconds >= 0 and
        input.observed_monotonic_milliseconds != 0 and
        input.flags == 0 and allZero(&input.reserved);
}

pub fn validPlan(input: *const PlanInput, config: *const Config) bool {
    return commonInput(input.abi_version, input.struct_size, @sizeOf(PlanInput), input.configuration_generation, config) and
        input.plan_epoch != 0 and input.observed_utc_milliseconds >= 0 and
        input.observed_monotonic_milliseconds != 0 and
        input.action_capacity <= config.maximum_action_count and
        input.flags == 0 and allZero(&input.reserved);
}

pub fn validFeedback(input: *const ActionFeedbackInput, config: *const Config) bool {
    _ = config;
    return attemptInput(input.abi_version, input.struct_size, @sizeOf(ActionFeedbackInput), input.configuration_generation) and
        input.feedback_epoch != 0 and input.action_id != 0 and input.plan_epoch != 0 and
        !input.operation_id.isZero() and !input.attempt_token.isZero() and
        actionKindFromInt(input.action_kind) != null and
        feedbackOutcomeFromInt(input.outcome) != null and
        input.observed_utc_milliseconds >= 0 and
        input.observed_monotonic_milliseconds != 0 and
        (input.valid_mask & ~FeedbackValid.known) == 0 and
        input.flags == 0 and allZero(&input.reserved) and
        handleMatchesValidity(input.result_handle, input.valid_mask, FeedbackValid.result) and
        handleMatchesValidity(input.error_handle, input.valid_mask, FeedbackValid.error_handle);
}

pub fn validCompletion(input: *const CompletionInput, config: *const Config) bool {
    _ = config;
    return attemptInput(input.abi_version, input.struct_size, @sizeOf(CompletionInput), input.configuration_generation) and
        input.completion_epoch != 0 and !input.operation_id.isZero() and
        !input.attempt_token.isZero() and completionOutcomeFromInt(input.outcome) != null and
        input.reserved_u32 == 0 and
        input.observed_utc_milliseconds >= 0 and
        input.observed_monotonic_milliseconds != 0 and
        (input.valid_mask & ~CompletionValid.known) == 0 and
        input.flags == 0 and allZero(&input.reserved) and
        handleMatchesValidity(input.result_handle, input.valid_mask, CompletionValid.result) and
        handleMatchesValidity(input.error_handle, input.valid_mask, CompletionValid.error_handle);
}

pub fn validProgress(input: *const ProgressInput, config: *const Config) bool {
    _ = config;
    if (!attemptInput(input.abi_version, input.struct_size, @sizeOf(ProgressInput), input.configuration_generation) or
        input.operation_id.isZero() or input.attempt_token.isZero() or
        input.progress_sequence == 0 or input.observed_utc_milliseconds < 0 or
        input.observed_monotonic_milliseconds == 0 or
        input.reserved_u32 != 0 or
        (input.valid_mask & ~ProgressValid.known) != 0 or
        input.flags != 0 or !allZero(&input.reserved) or
        !handleMatchesValidity(input.stage_handle, input.valid_mask, ProgressValid.stage) or
        !handleMatchesValidity(input.message_handle, input.valid_mask, ProgressValid.message) or
        !handleMatchesValidity(input.checkpoint_handle, input.valid_mask, ProgressValid.checkpoint))
    {
        return false;
    }
    if ((input.valid_mask & ProgressValid.percent_milli) != 0) {
        if (input.percent_milli > 100_000) return false;
    } else if (input.percent_milli != 0) return false;
    if ((input.valid_mask & ProgressValid.bytes_done) == 0 and input.bytes_done != 0) return false;
    if ((input.valid_mask & ProgressValid.bytes_total) == 0 and input.bytes_total != 0) return false;
    if ((input.valid_mask & ProgressValid.speed_bytes_per_second) == 0 and
        input.speed_bytes_per_second != 0) return false;
    if ((input.valid_mask & (ProgressValid.bytes_done | ProgressValid.bytes_total)) ==
        (ProgressValid.bytes_done | ProgressValid.bytes_total) and input.bytes_done > input.bytes_total)
    {
        return false;
    }
    return true;
}

pub fn validRead(input: *const ReadInput, config: *const Config) bool {
    return commonInput(input.abi_version, input.struct_size, @sizeOf(ReadInput), input.configuration_generation, config) and
        input.state_revision != 0 and input.flags == 0 and allZero(&input.reserved);
}

pub fn validActionRead(input: *const ReadInput) bool {
    return attemptInput(
        input.abi_version,
        input.struct_size,
        @sizeOf(ReadInput),
        input.configuration_generation,
    ) and input.state_revision != 0 and
        input.flags == 0 and allZero(&input.reserved);
}

pub fn validPersistenceInput(input: *const PersistenceInput, config: *const Config) bool {
    return commonInput(input.abi_version, input.struct_size, @sizeOf(PersistenceInput), input.configuration_generation, config) and
        input.operation_epoch != 0 and input.flags == 0 and allZero(&input.reserved);
}

pub fn validPersistenceImportInput(
    input: *const PersistenceImportInput,
    config: *const Config,
) bool {
    return commonInput(
        input.abi_version,
        input.struct_size,
        @sizeOf(PersistenceImportInput),
        input.configuration_generation,
        config,
    ) and input.operation_epoch != 0 and
        input.observed_utc_milliseconds >= 0 and
        input.observed_monotonic_milliseconds != 0 and
        equalHandle(input.clock_instance_id, config.clock_instance_id) and
        input.flags == 0 and allZero(&input.reserved);
}

pub fn stateFromInt(value: u32) ?OperationState {
    return switch (value) {
        1 => .queued,
        2 => .start_pending,
        3 => .running,
        4 => .cancel_pending,
        5 => .retry_wait,
        6 => .recovery_pending,
        7 => .succeeded,
        8 => .failed,
        9 => .canceled,
        10 => .state_uncertain,
        else => null,
    };
}

pub fn actionKindFromInt(value: u32) ?ActionKind {
    return switch (value) {
        1 => .start,
        2 => .cancel,
        3 => .recover,
        else => null,
    };
}

pub fn feedbackOutcomeFromInt(value: u32) ?ActionFeedbackOutcome {
    return switch (value) {
        1 => .started,
        2 => .start_retryable_failure,
        3 => .start_terminal_failure,
        4 => .cancel_completed,
        5 => .cancel_retryable_failure,
        6 => .recovered_queued,
        7 => .recovered_succeeded,
        8 => .recovered_failed,
        9 => .recovered_canceled,
        10 => .recovered_uncertain,
        else => null,
    };
}

pub fn completionOutcomeFromInt(value: u32) ?CompletionOutcome {
    return switch (value) {
        1 => .succeeded,
        2 => .retryable_failure,
        3 => .terminal_failure,
        4 => .canceled,
        5 => .state_uncertain,
        else => null,
    };
}

pub fn isTerminal(state: OperationState) bool {
    return switch (state) {
        .succeeded, .failed, .canceled, .state_uncertain => true,
        else => false,
    };
}

pub fn equalHandle(a: Handle128, b: Handle128) bool {
    return a.high == b.high and a.low == b.low;
}

pub fn allZero(values: []const u64) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

fn commonInput(
    version: u32,
    size: u32,
    expected_size: usize,
    generation: u64,
    config: *const Config,
) bool {
    return version == abi_version and size == expected_size and
        generation == config.generation;
}

fn attemptInput(
    version: u32,
    size: u32,
    expected_size: usize,
    generation: u64,
) bool {
    return version == abi_version and size == expected_size and generation != 0;
}

fn validIndexCapacity(capacity: u32, count: u32) bool {
    const doubled = std.math.mul(u32, count, 2) catch return false;
    return capacity >= doubled and std.math.isPowerOfTwo(capacity);
}

fn handleMatchesValidity(handle: Handle128, valid_mask: u64, bit: u64) bool {
    return ((valid_mask & bit) != 0) != handle.isZero();
}
