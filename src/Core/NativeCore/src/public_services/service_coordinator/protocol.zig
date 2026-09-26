const std = @import("std");

pub const abi_version: u32 = 0x0003_0000;

pub const ConfigFlags = struct {
    pub const loopback_only: u64 = 1 << 0;
    pub const known: u64 = loopback_only;
};

pub const CapabilityFlags = struct {
    pub const enabled: u32 = 1 << 0;
    pub const available: u32 = 1 << 1;
    pub const known: u32 = enabled | available;
};

pub const RouteFlags = struct {
    pub const exact_path: u32 = 1 << 0;
    pub const catalog_route: u32 = 1 << 1;
    pub const bypass_rate_limit: u32 = 1 << 2;
    pub const known: u32 = exact_path | catalog_route | bypass_rate_limit;
};

pub const ModelFlags = struct {
    pub const available: u32 = 1 << 0;
    pub const loaded_instance: u32 = 1 << 1;
    pub const known: u32 = available | loaded_instance;
};

pub const MethodMask = struct {
    pub const get: u32 = 1 << 0;
    pub const head: u32 = 1 << 1;
    pub const post: u32 = 1 << 2;
    pub const put: u32 = 1 << 3;
    pub const delete: u32 = 1 << 4;
    pub const patch: u32 = 1 << 5;
    pub const known: u32 = get | head | post | put | delete | patch;
};

pub const RequestFlags = struct {
    pub const known: u32 = 0;
};

pub const SubscriptionFlags = struct {
    pub const continuous_use: u32 = 1 << 0;
    pub const known: u32 = continuous_use;
};

pub const TaskFlags = struct {
    pub const known: u32 = 0;
};

pub const TaskPlanFlags = struct {
    pub const next_wake_valid: u64 = 1 << 0;
    pub const more_ready: u64 = 1 << 1;
    pub const known: u64 = next_wake_valid | more_ready;
};

pub const SnapshotFlags = struct {
    pub const service_enabled: u64 = 1 << 0;
    pub const next_wake_valid: u64 = 1 << 1;
    pub const model_catalog_usable: u64 = 1 << 2;
    pub const model_acquisition_running: u64 = 1 << 3;
    pub const known: u64 = service_enabled |
        next_wake_valid |
        model_catalog_usable |
        model_acquisition_running;
};

pub const ModelAcquisitionPlanStatus = enum(u32) {
    invalid = 0,
    not_due = 1,
    start = 2,
    running = 3,
};

pub const ModelAcquisitionCompletionStatus = enum(u32) {
    invalid = 0,
    unavailable = 1,
    failed = 2,
};

pub const RemoteScope = enum(u32) {
    invalid = 0,
    loopback = 1,
    remote = 2,
    unknown = 3,
};

pub const HttpMethod = enum(u32) {
    invalid = 0,
    get = 1,
    head = 2,
    post = 3,
    put = 4,
    delete = 5,
    patch = 6,
    other = 7,
};

pub const AccessReason = enum(u32) {
    invalid = 0,
    allowed = 1,
    remote_forbidden = 2,
    service_disabled = 3,
    route_not_found = 4,
    capability_disabled = 5,
    method_not_supported = 6,
    rate_limited = 7,
    caller_inflight_limit = 8,
    coordinator_capacity = 9,
};

pub const ModelResolveStatus = enum(u32) {
    invalid = 0,
    matched = 1,
    not_found = 2,
    catalog_unavailable = 3,
};

pub const TaskKind = enum(u32) {
    invalid = 0,
    load = 1,
    unload = 2,
};

pub const TaskState = enum(u32) {
    empty = 0,
    queued = 1,
    running = 2,
};

pub const TaskEffectOutcome = enum(u32) {
    invalid = 0,
    succeeded = 1,
    provider_unavailable = 2,
    timeout = 3,
    transport_failure = 4,
    http_response = 5,
    rejected = 6,
    failed = 7,
    cancelled = 8,
};

pub const TaskOutcomeMask = struct {
    pub const provider_unavailable: u32 = 1 << 2;
    pub const timeout: u32 = 1 << 3;
    pub const transport_failure: u32 = 1 << 4;
    pub const rejected: u32 = 1 << 6;
    pub const failed: u32 = 1 << 7;
    pub const known: u32 = provider_unavailable |
        timeout |
        transport_failure |
        rejected |
        failed;

    pub fn forOutcome(outcome: TaskEffectOutcome) u32 {
        return switch (outcome) {
            .provider_unavailable => provider_unavailable,
            .timeout => timeout,
            .transport_failure => transport_failure,
            .rejected => rejected,
            .failed => failed,
            .invalid, .succeeded, .http_response, .cancelled => 0,
        };
    }
};

pub const TaskHttpRetryPolicyMask = struct {
    pub const request_timeout: u32 = 1 << 0;
    pub const throttled: u32 = 1 << 1;
    pub const server_error: u32 = 1 << 2;
    pub const known: u32 = request_timeout | throttled | server_error;
};

pub const TaskCompletionDisposition = enum(u32) {
    invalid = 0,
    succeeded = 1,
    terminal_failure = 2,
    retry_scheduled = 3,
};

pub const RequestCompletionDisposition = enum(u32) {
    invalid = 0,
    completed = 1,
    expired = 2,
};

pub const TextSpan = extern struct {
    offset: u32,
    length: u32,
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    session_instance_low: u64,
    session_instance_high: u64,
    maximum_capability_count: u32,
    maximum_route_count: u32,
    maximum_model_count: u32,
    maximum_model_alias_count: u32,
    maximum_request_count: u32,
    maximum_rate_bucket_count: u32,
    maximum_lease_count: u32,
    maximum_subscription_count: u32,
    maximum_task_count: u32,
    capability_index_capacity: u32,
    model_index_capacity: u32,
    alias_index_capacity: u32,
    request_index_capacity: u32,
    rate_bucket_index_capacity: u32,
    lease_index_capacity: u32,
    subscription_index_capacity: u32,
    task_index_capacity: u32,
    maximum_catalog_text_bytes: u32,
    maximum_model_text_bytes: u32,
    maximum_concurrent_model_tasks: u32,
    maximum_requests_per_rate_window: u32,
    maximum_inflight_requests_per_caller: u32,
    retryable_task_outcome_mask: u32,
    rate_window_milliseconds: u64,
    request_timeout_milliseconds: u64,
    lease_timeout_milliseconds: u64,
    subscription_timeout_milliseconds: u64,
    task_timeout_milliseconds: u64,
    retry_delay_milliseconds: u64,
    resident_byte_budget: u64,
    flags: u64,
    model_catalog_acquisition_interval_milliseconds: u64,
    model_catalog_last_good_lifetime_milliseconds: u64,
    model_catalog_acquisition_timeout_milliseconds: u64,
    maximum_task_attempt_count: u32,
    retryable_http_status_policy_mask: u32,
};

pub const Capacity = extern struct {
    struct_size: u32,
    capability_capacity: u32,
    route_capacity: u32,
    model_capacity: u32,
    model_alias_capacity: u32,
    request_capacity: u32,
    rate_bucket_capacity: u32,
    lease_capacity: u32,
    subscription_capacity: u32,
    task_capacity: u32,
    capability_index_capacity: u32,
    model_index_capacity: u32,
    alias_index_capacity: u32,
    request_index_capacity: u32,
    rate_bucket_index_capacity: u32,
    lease_index_capacity: u32,
    subscription_index_capacity: u32,
    task_index_capacity: u32,
    catalog_text_capacity: u32,
    model_text_capacity: u32,
    resident_byte_count: u64,
    reserved: [3]u64,
};

pub const CapabilityInput = extern struct {
    struct_size: u32,
    flags: u32,
    capability_handle: u64,
    payload_handle: u64,
    reserved: u64,
};

pub const RouteInput = extern struct {
    struct_size: u32,
    flags: u32,
    route_handle: u64,
    capability_handle: u64,
    path: TextSpan,
    method_mask: u32,
    reserved_u32: u32,
    payload_handle: u64,
    reserved: u64,
};

pub const CatalogReplaceInput = extern struct {
    struct_size: u32,
    service_enabled: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    catalog_generation: u64,
    capability_count: u32,
    route_count: u32,
    text_byte_count: u32,
    reserved_u32: u32,
    reserved: [3]u64,
};

pub const ModelInput = extern struct {
    struct_size: u32,
    flags: u32,
    model_handle: u64,
    provider_handle: u64,
    payload_handle: u64,
    reserved: u64,
};

pub const ModelAliasInput = extern struct {
    struct_size: u32,
    flags: u32,
    model_handle: u64,
    alias: TextSpan,
    reserved: [2]u64,
};

pub const ModelReplaceInput = extern struct {
    struct_size: u32,
    reserved_u32: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    model_generation: u64,
    model_count: u32,
    alias_count: u32,
    text_byte_count: u32,
    reserved_u32_2: u32,
    acquisition_attempt_handle: u64,
    reserved: [2]u64,
};

pub const ModelAcquisitionPlanInput = extern struct {
    struct_size: u32,
    reserved_u32: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    reserved: [3]u64,
};

pub const ModelAcquisitionPlanOutput = extern struct {
    struct_size: u32,
    status: u32,
    attempt_handle: u64,
    started_at_monotonic_milliseconds: u64,
    deadline_monotonic_milliseconds: u64,
    next_wake_monotonic_milliseconds: u64,
    model_generation_at_start: u64,
    reserved: [2]u64,
};

pub const ModelAcquisitionCompletionInput = extern struct {
    struct_size: u32,
    status: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    attempt_handle: u64,
    reserved: [3]u64,
};

pub const AccessInput = extern struct {
    struct_size: u32,
    flags: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    caller_handle: u64,
    remote_scope: u32,
    method: u32,
    path: TextSpan,
    reserved: [3]u64,
};

pub const AccessOutput = extern struct {
    struct_size: u32,
    allowed: u32,
    status_code: u32,
    reason: u32,
    request_handle: u64,
    route_handle: u64,
    capability_handle: u64,
    catalog_generation: u64,
    expires_at_monotonic_milliseconds: u64,
    reserved: [2]u64,
};

pub const RequestCompleteInput = extern struct {
    struct_size: u32,
    reserved_u32: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    request_handle: u64,
    reserved: [3]u64,
};

pub const RequestCompleteOutput = extern struct {
    struct_size: u32,
    disposition: u32,
    request_handle: u64,
    reserved: [3]u64,
};

pub const ModelResolveInput = extern struct {
    struct_size: u32,
    reserved_u32: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    alias: TextSpan,
    reserved: [3]u64,
};

pub const ModelResolveOutput = extern struct {
    struct_size: u32,
    status: u32,
    model_handle: u64,
    provider_handle: u64,
    payload_handle: u64,
    model_generation: u64,
    model_flags: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const LeaseBeginInput = extern struct {
    struct_size: u32,
    reserved_u32: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    caller_handle: u64,
    model_handle: u64,
    requested_timeout_milliseconds: u64,
    reserved: [2]u64,
};

pub const LeaseOutput = extern struct {
    struct_size: u32,
    reserved_u32: u32,
    lease_handle: u64,
    model_handle: u64,
    caller_handle: u64,
    expires_at_monotonic_milliseconds: u64,
    reserved: [2]u64,
};

pub const LeaseEndInput = extern struct {
    struct_size: u32,
    reserved_u32: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    lease_handle: u64,
    reserved: [3]u64,
};

pub const SubscriptionUpsertInput = extern struct {
    struct_size: u32,
    flags: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    subscription_handle: u64,
    caller_handle: u64,
    model_handle: u64,
    base_score: i64,
    requested_timeout_milliseconds: u64,
    reserved: [2]u64,
};

pub const SubscriptionOutput = extern struct {
    struct_size: u32,
    flags: u32,
    subscription_handle: u64,
    caller_handle: u64,
    model_handle: u64,
    base_score: i64,
    expires_at_monotonic_milliseconds: u64,
    reserved: [2]u64,
};

pub const SubscriptionRemoveInput = extern struct {
    struct_size: u32,
    reserved_u32: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    subscription_handle: u64,
    reserved: [3]u64,
};

pub const TaskEnqueueInput = extern struct {
    struct_size: u32,
    flags: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    caller_handle: u64,
    model_handle: u64,
    payload_handle: u64,
    base_score: i64,
    kind: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const TaskOutput = extern struct {
    struct_size: u32,
    flags: u32,
    task_handle: u64,
    caller_handle: u64,
    model_handle: u64,
    payload_handle: u64,
    base_score: i64,
    enqueued_at_monotonic_milliseconds: u64,
    deadline_monotonic_milliseconds: u64,
    kind: u32,
    state: u32,
    attempt: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const TaskPlanInput = extern struct {
    struct_size: u32,
    maximum_output_count: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    reserved: [3]u64,
};

pub const TaskPlanOutput = extern struct {
    struct_size: u32,
    output_count: u32,
    next_wake_monotonic_milliseconds: u64,
    flags: u64,
    reserved: [3]u64,
};

pub const TaskCompletionInput = extern struct {
    struct_size: u32,
    outcome: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    task_handle: u64,
    attempt: u32,
    http_status_code: u32,
    reserved: [2]u64,
};

pub const TaskCompletionOutput = extern struct {
    struct_size: u32,
    disposition: u32,
    task_handle: u64,
    attempt: u32,
    reserved_u32: u32,
    next_wake_monotonic_milliseconds: u64,
    reserved: [2]u64,
};

pub const TaskCancelInput = extern struct {
    struct_size: u32,
    maximum_output_count: u32,
    command_epoch: u64,
    command_monotonic_milliseconds: u64,
    reserved: [3]u64,
};

pub const TaskCancelOutput = extern struct {
    struct_size: u32,
    output_count: u32,
    remaining_task_count: u32,
    reserved_u32: u32,
    reserved: [4]u64,
};

pub const CapabilityOutput = extern struct {
    struct_size: u32,
    flags: u32,
    capability_handle: u64,
    payload_handle: u64,
    catalog_generation: u64,
    reserved: [2]u64,
};

pub const Snapshot = extern struct {
    struct_size: u32,
    reserved_u32: u32,
    generation: u64,
    session_instance_low: u64,
    session_instance_high: u64,
    last_command_epoch: u64,
    last_command_monotonic_milliseconds: u64,
    catalog_generation: u64,
    model_generation: u64,
    capability_count: u32,
    route_count: u32,
    model_count: u32,
    alias_count: u32,
    request_count: u32,
    rate_bucket_count: u32,
    lease_count: u32,
    subscription_count: u32,
    queued_task_count: u32,
    running_task_count: u32,
    next_wake_monotonic_milliseconds: u64,
    flags: u64,
    model_acquisition_attempt_handle: u64,
    model_catalog_usable_until_monotonic_milliseconds: u64,
    next_model_acquisition_due_monotonic_milliseconds: u64,
};

pub fn methodMask(method: HttpMethod) u32 {
    return switch (method) {
        .get => MethodMask.get,
        .head => MethodMask.head,
        .post => MethodMask.post,
        .put => MethodMask.put,
        .delete => MethodMask.delete,
        .patch => MethodMask.patch,
        .other => 0,
        .invalid => 0,
    };
}

pub fn validPowerOfTwoCapacity(value: u32, minimum: u32) bool {
    return value >= minimum and value != 0 and std.math.isPowerOfTwo(value);
}

pub fn allZero(comptime T: type, values: T) bool {
    return std.mem.allEqual(u8, std.mem.asBytes(&values), 0);
}
