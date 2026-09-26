const std = @import("std");

pub const abi_version: u32 = 0x0001_0000;

pub const PathFlags = struct {
    pub const identity_confirmed: u32 = 1 << 0;
    pub const root_confirmed: u32 = 1 << 1;
    pub const known: u32 = identity_confirmed | root_confirmed;
};

pub const PersistenceFlags = struct {
    pub const delete: u32 = 1 << 0;
    pub const identity_confirmed: u32 = 1 << 1;
    pub const root_confirmed: u32 = 1 << 2;
    pub const known: u32 = delete | identity_confirmed | root_confirmed;
};

pub const SnapshotFlags = struct {
    pub const identity_confirmed: u32 = 1 << 0;
    pub const requires_root_confirmation: u32 = 1 << 1;
    pub const root_confirmed: u32 = 1 << 2;
    pub const dirty: u32 = 1 << 3;
    pub const known: u32 = identity_confirmed | requires_root_confirmation | root_confirmed | dirty;
};

pub const PlanOutputFlags = struct {
    pub const has_more: u32 = 1 << 0;
    pub const mutation_exhausted: u32 = 1 << 1;
    pub const known: u32 = has_more | mutation_exhausted;
};

pub const SnapshotOutputFlags = struct {
    pub const registration_has_more: u32 = 1 << 0;
    pub const path_has_more: u32 = 1 << 1;
    pub const mutation_exhausted: u32 = 1 << 2;
    pub const known: u32 = registration_has_more | path_has_more | mutation_exhausted;
};

pub const ImportValid = struct {
    pub const import_generation: u64 = 1 << 0;
    pub const operation_epoch: u64 = 1 << 1;
    pub const command_utc_ms: u64 = 1 << 2;
    pub const rows: u64 = 1 << 3;
    pub const key_bytes: u64 = 1 << 4;
    pub const required: u64 = import_generation | operation_epoch | command_utc_ms | rows | key_bytes;
    pub const known: u64 = required;
};

pub const ObserveValid = struct {
    pub const operation_epoch: u64 = 1 << 0;
    pub const command_utc_ms: u64 = 1 << 1;
    pub const observed_at_utc_ms: u64 = 1 << 2;
    pub const identity: u64 = 1 << 3;
    pub const executable_path: u64 = 1 << 4;
    pub const suggested_root: u64 = 1 << 5;
    pub const required: u64 = operation_epoch | command_utc_ms | observed_at_utc_ms | identity |
        executable_path | suggested_root;
    pub const known: u64 = required;
};

pub const ConfirmRootValid = struct {
    pub const operation_epoch: u64 = 1 << 0;
    pub const command_utc_ms: u64 = 1 << 1;
    pub const software_identity: u64 = 1 << 2;
    pub const root_path: u64 = 1 << 3;
    pub const required: u64 = operation_epoch | command_utc_ms | software_identity | root_path;
    pub const known: u64 = required;
};

pub const MarkMissingValid = struct {
    pub const operation_epoch: u64 = 1 << 0;
    pub const command_utc_ms: u64 = 1 << 1;
    pub const software_identity: u64 = 1 << 2;
    pub const path_identity: u64 = 1 << 3;
    pub const required: u64 = operation_epoch | command_utc_ms | software_identity | path_identity;
    pub const known: u64 = required;
};

pub const PlanValid = struct {
    pub const plan_epoch: u64 = 1 << 0;
    pub const output_limit: u64 = 1 << 1;
    pub const required: u64 = plan_epoch | output_limit;
    pub const known: u64 = required;
};

pub const FeedbackValid = struct {
    pub const feedback_epoch: u64 = 1 << 0;
    pub const rows: u64 = 1 << 1;
    pub const required: u64 = feedback_epoch | rows;
    pub const known: u64 = required;
};

pub const SnapshotValid = struct {
    pub const snapshot_epoch: u64 = 1 << 0;
    pub const cursors: u64 = 1 << 1;
    pub const output_limits: u64 = 1 << 2;
    pub const required: u64 = snapshot_epoch | cursors | output_limits;
    pub const known: u64 = required;
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    maximum_registration_count: u32,
    maximum_path_count: u32,
    maximum_persistence_operation_count: u32,
    maximum_registration_snapshot_count: u32,
    maximum_path_snapshot_count: u32,
    maximum_executable_path_byte_count: u32,
    maximum_root_path_byte_count: u32,
    registration_index_capacity: u32,
    path_index_capacity: u32,
    maximum_future_skew_ms: u32,
    flags: u32,
    resident_byte_budget: u64,
    reserved: [5]u64,
};

pub const Capacity = extern struct {
    struct_size: u32,
    registration_capacity: u32,
    path_capacity: u32,
    persistence_operation_capacity: u32,
    registration_snapshot_capacity: u32,
    path_snapshot_capacity: u32,
    executable_path_byte_capacity_per_path: u32,
    root_path_byte_capacity_per_path: u32,
    registration_index_capacity: u32,
    path_index_capacity: u32,
    reserved_u32: u32,
    resident_byte_count: u64,
    reserved: [4]u64,
};

pub const ImportInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    import_generation: u64,
    operation_epoch: u64,
    command_utc_ms: i64,
    row_count: u32,
    key_byte_count: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const PersistedPathInput = extern struct {
    struct_size: u32,
    flags: u32,
    software_handle: u64,
    catalog_entry_handle: u64,
    display_name_handle: u64,
    software_kind_handle: u64,
    path_handle: u64,
    root_handle: u64,
    first_observed_utc_ms: i64,
    executable_path_offset: u32,
    executable_path_length: u32,
    root_path_offset: u32,
    root_path_length: u32,
    reserved: [2]u64,
};

pub const ObserveInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    command_utc_ms: i64,
    observed_at_utc_ms: i64,
    software_handle: u64,
    catalog_entry_handle: u64,
    display_name_handle: u64,
    software_kind_handle: u64,
    path_handle: u64,
    root_handle: u64,
    executable_path_offset: u32,
    executable_path_length: u32,
    root_path_offset: u32,
    root_path_length: u32,
    path_flags: u32,
    reserved_u32: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const ConfirmRootInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    command_utc_ms: i64,
    software_handle: u64,
    root_handle: u64,
    root_path_offset: u32,
    root_path_length: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const MarkMissingInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    command_utc_ms: i64,
    software_handle: u64,
    path_handle: u64,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const PlanPersistenceInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    plan_epoch: u64,
    maximum_operation_count: u32,
    reserved_u32: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const PersistenceOperation = extern struct {
    struct_size: u32,
    flags: u32,
    mutation_version: u64,
    software_handle: u64,
    catalog_entry_handle: u64,
    display_name_handle: u64,
    software_kind_handle: u64,
    path_handle: u64,
    root_handle: u64,
    first_observed_utc_ms: i64,
    reserved: [2]u64,
};

pub const PersistencePlanOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    state_revision: u64,
    plan_epoch: u64,
    operation_count: u32,
    total_dirty_count: u32,
    flags: u32,
    reserved_u32: u32,
    first_mutation_version: u64,
    last_mutation_version: u64,
    next_mutation_version: u64,
    reserved: [3]u64,
};

pub const PersistenceFeedbackInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    feedback_epoch: u64,
    feedback_count: u32,
    reserved_u32: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const PersistenceFeedback = extern struct {
    struct_size: u32,
    reserved_u32: u32,
    mutation_version: u64,
    software_handle: u64,
    path_handle: u64,
    reserved: [2]u64,
};

pub const SnapshotInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    snapshot_epoch: u64,
    registration_cursor: u32,
    path_cursor: u32,
    maximum_registration_count: u32,
    maximum_path_count: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const RegistrationSnapshot = extern struct {
    struct_size: u32,
    flags: u32,
    software_handle: u64,
    catalog_entry_handle: u64,
    display_name_handle: u64,
    software_kind_handle: u64,
    first_observed_utc_ms: i64,
    active_path_count: u32,
    confirmed_root_count: u32,
    dirty_path_count: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const PathSnapshot = extern struct {
    struct_size: u32,
    flags: u32,
    mutation_version: u64,
    software_handle: u64,
    path_handle: u64,
    root_handle: u64,
    first_observed_utc_ms: i64,
    executable_path_length: u32,
    root_path_length: u32,
    reserved: [3]u64,
};

pub const SnapshotOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    import_generation: u64,
    state_revision: u64,
    last_operation_epoch: u64,
    last_plan_epoch: u64,
    last_feedback_epoch: u64,
    last_snapshot_epoch: u64,
    last_command_utc_ms: i64,
    next_mutation_version: u64,
    committed_mutation_version: u64,
    registration_count: u32,
    path_count: u32,
    dirty_path_count: u32,
    registration_output_count: u32,
    path_output_count: u32,
    next_registration_cursor: u32,
    next_path_cursor: u32,
    flags: u32,
    resident_byte_count: u64,
    reserved: [2]u64,
};

pub fn validConfig(config: *const Config) bool {
    return config.abi_version == abi_version and
        config.struct_size == @sizeOf(Config) and
        config.generation != 0 and
        config.maximum_registration_count != 0 and
        config.maximum_path_count >= config.maximum_registration_count and
        config.maximum_persistence_operation_count != 0 and
        config.maximum_persistence_operation_count <= config.maximum_path_count and
        config.maximum_registration_snapshot_count != 0 and
        config.maximum_registration_snapshot_count <= config.maximum_registration_count and
        config.maximum_path_snapshot_count != 0 and
        config.maximum_path_snapshot_count <= config.maximum_path_count and
        config.maximum_executable_path_byte_count >= 3 and
        config.maximum_root_path_byte_count >= 3 and
        validIndexCapacity(config.registration_index_capacity, config.maximum_registration_count) and
        validIndexCapacity(config.path_index_capacity, config.maximum_path_count) and
        config.flags == 0 and
        config.resident_byte_budget != 0 and
        allZero(&config.reserved);
}

pub fn validImport(input: *const ImportInput, config: *const Config) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(ImportInput) and
        input.configuration_generation == config.generation and
        input.import_generation != 0 and
        input.operation_epoch != 0 and
        input.command_utc_ms >= 0 and
        input.row_count <= config.maximum_path_count and
        input.valid_mask == ImportValid.required and
        input.flags == 0 and
        allZero(&input.reserved);
}

pub fn validPersistedPath(row: *const PersistedPathInput, config: *const Config, key_bytes: []const u8) bool {
    return row.struct_size == @sizeOf(PersistedPathInput) and
        (row.flags & ~PathFlags.known) == 0 and
        row.software_handle != 0 and row.catalog_entry_handle != 0 and
        row.display_name_handle != 0 and row.software_kind_handle != 0 and
        row.path_handle != 0 and row.root_handle != 0 and
        row.first_observed_utc_ms >= 0 and
        row.executable_path_length <= config.maximum_executable_path_byte_count and
        row.root_path_length <= config.maximum_root_path_byte_count and
        validCanonicalPath(key_bytes, row.executable_path_offset, row.executable_path_length) and
        validCanonicalPath(key_bytes, row.root_path_offset, row.root_path_length) and
        allZero(&row.reserved);
}

pub fn validObserve(input: *const ObserveInput, config: *const Config, key_bytes: []const u8) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(ObserveInput) and
        input.configuration_generation == config.generation and
        input.operation_epoch != 0 and
        input.command_utc_ms >= 0 and input.observed_at_utc_ms >= 0 and
        withinFutureSkew(input.command_utc_ms, input.observed_at_utc_ms, config.maximum_future_skew_ms) and
        input.software_handle != 0 and input.catalog_entry_handle != 0 and
        input.display_name_handle != 0 and input.software_kind_handle != 0 and
        input.path_handle != 0 and input.root_handle != 0 and
        input.executable_path_length <= config.maximum_executable_path_byte_count and
        input.root_path_length <= config.maximum_root_path_byte_count and
        input.path_flags & ~PathFlags.known == 0 and
        input.reserved_u32 == 0 and
        input.valid_mask == ObserveValid.required and input.flags == 0 and
        validCanonicalPath(key_bytes, input.executable_path_offset, input.executable_path_length) and
        validCanonicalPath(key_bytes, input.root_path_offset, input.root_path_length) and
        allZero(&input.reserved);
}

pub fn validConfirmRoot(input: *const ConfirmRootInput, config: *const Config, key_bytes: []const u8) bool {
    return input.abi_version == abi_version and input.struct_size == @sizeOf(ConfirmRootInput) and
        input.configuration_generation == config.generation and input.operation_epoch != 0 and
        input.command_utc_ms >= 0 and input.software_handle != 0 and input.root_handle != 0 and
        input.root_path_offset == 0 and input.root_path_length <= config.maximum_root_path_byte_count and
        input.valid_mask == ConfirmRootValid.required and input.flags == 0 and
        validCanonicalPath(key_bytes, input.root_path_offset, input.root_path_length) and
        input.root_path_length == key_bytes.len and allZero(&input.reserved);
}

pub fn validMarkMissing(input: *const MarkMissingInput, config: *const Config) bool {
    return input.abi_version == abi_version and input.struct_size == @sizeOf(MarkMissingInput) and
        input.configuration_generation == config.generation and input.operation_epoch != 0 and
        input.command_utc_ms >= 0 and input.software_handle != 0 and input.path_handle != 0 and
        input.valid_mask == MarkMissingValid.required and input.flags == 0 and allZero(&input.reserved);
}

pub fn validPlan(input: *const PlanPersistenceInput, config: *const Config) bool {
    return input.abi_version == abi_version and input.struct_size == @sizeOf(PlanPersistenceInput) and
        input.configuration_generation == config.generation and input.plan_epoch != 0 and
        input.maximum_operation_count != 0 and
        input.maximum_operation_count <= config.maximum_persistence_operation_count and
        input.reserved_u32 == 0 and input.valid_mask == PlanValid.required and
        input.flags == 0 and allZero(&input.reserved);
}

pub fn validFeedbackInput(input: *const PersistenceFeedbackInput, config: *const Config) bool {
    return input.abi_version == abi_version and input.struct_size == @sizeOf(PersistenceFeedbackInput) and
        input.configuration_generation == config.generation and input.feedback_epoch != 0 and
        input.feedback_count != 0 and input.feedback_count <= config.maximum_persistence_operation_count and
        input.reserved_u32 == 0 and
        input.valid_mask == FeedbackValid.required and input.flags == 0 and allZero(&input.reserved);
}

pub fn validFeedback(row: *const PersistenceFeedback) bool {
    return row.struct_size == @sizeOf(PersistenceFeedback) and row.reserved_u32 == 0 and
        row.mutation_version != 0 and row.software_handle != 0 and row.path_handle != 0 and
        allZero(&row.reserved);
}

pub fn validSnapshot(input: *const SnapshotInput, config: *const Config) bool {
    return input.abi_version == abi_version and input.struct_size == @sizeOf(SnapshotInput) and
        input.configuration_generation == config.generation and input.snapshot_epoch != 0 and
        input.registration_cursor <= config.maximum_registration_count and
        input.path_cursor <= config.maximum_path_count and
        input.maximum_registration_count != 0 and
        input.maximum_registration_count <= config.maximum_registration_snapshot_count and
        input.maximum_path_count != 0 and input.maximum_path_count <= config.maximum_path_snapshot_count and
        input.valid_mask == SnapshotValid.required and input.flags == 0 and allZero(&input.reserved);
}

pub fn emptyPlanOutput(output: *const PersistencePlanOutput) bool {
    return allBytesZero(PersistencePlanOutput, output);
}

pub fn emptySnapshotOutput(output: *const SnapshotOutput) bool {
    return allBytesZero(SnapshotOutput, output);
}

pub fn emptyPersistenceOperation(output: *const PersistenceOperation) bool {
    return allBytesZero(PersistenceOperation, output);
}

pub fn emptyRegistrationSnapshot(output: *const RegistrationSnapshot) bool {
    return allBytesZero(RegistrationSnapshot, output);
}

pub fn emptyPathSnapshot(output: *const PathSnapshot) bool {
    return allBytesZero(PathSnapshot, output);
}

pub fn keySlice(key_bytes: []const u8, offset: u32, length: u32) ?[]const u8 {
    const end = std.math.add(u32, offset, length) catch return null;
    if (end > key_bytes.len) return null;
    return key_bytes[offset..end];
}

pub fn validCanonicalPath(key_bytes: []const u8, offset: u32, length: u32) bool {
    const key = keySlice(key_bytes, offset, length) orelse return false;
    if (key.len < 3 or !std.unicode.utf8ValidateSlice(key)) return false;
    const drive_path = key.len >= 3 and key[0] >= 'a' and key[0] <= 'z' and key[1] == ':' and key[2] == '/';
    const unc_path = key.len >= 5 and key[0] == '/' and key[1] == '/' and key[2] != '/';
    if (!drive_path and !unc_path) return false;
    const drive_root = drive_path and key.len == 3;
    if (key[key.len - 1] == '/' and !drive_root) return false;
    var previous_separator = false;
    for (key, 0..) |value, index| {
        if (value == 0 or value == '\\' or (value >= 'A' and value <= 'Z') or
            (value == ':' and !(drive_path and index == 1))) return false;
        if (value == '/') {
            if (previous_separator and !(unc_path and index == 1)) return false;
            previous_separator = true;
        } else previous_separator = false;
    }
    const segment_start: usize = if (drive_path) 3 else 2;
    var segment_count: u32 = 0;
    var cursor = segment_start;
    while (cursor < key.len) {
        const separator = std.mem.indexOfScalarPos(u8, key, cursor, '/') orelse key.len;
        const segment = key[cursor..separator];
        if (segment.len == 0 or std.mem.eql(u8, segment, ".") or std.mem.eql(u8, segment, "..")) return false;
        segment_count += 1;
        cursor = separator + 1;
    }
    return drive_path or segment_count >= 2;
}

pub fn isSameOrUnder(candidate: []const u8, root: []const u8) bool {
    if (std.mem.eql(u8, candidate, root)) return true;
    if (!std.mem.startsWith(u8, candidate, root) or candidate.len <= root.len) return false;
    return root[root.len - 1] == '/' or candidate[root.len] == '/';
}

pub fn withinFutureSkew(command_utc_ms: i64, observed_at_utc_ms: i64, maximum_future_skew_ms: u32) bool {
    const maximum_observed = std.math.add(i64, command_utc_ms, maximum_future_skew_ms) catch return false;
    return observed_at_utc_ms <= maximum_observed;
}

fn validIndexCapacity(capacity: u32, item_count: u32) bool {
    return capacity >= item_count and std.math.isPowerOfTwo(capacity);
}

fn allZero(values: []const u64) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

fn allBytesZero(comptime T: type, value: *const T) bool {
    const bytes = std.mem.asBytes(value);
    for (bytes) |byte| if (byte != 0) return false;
    return true;
}
