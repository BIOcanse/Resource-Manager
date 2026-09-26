const std = @import("std");

pub const abi_version: u32 = 0x0003_0000;
pub const identity_contract_version: u32 = 0x0001_0000;
pub const capability_contract_version: u32 = 0x0001_0000;
pub const source_count: u32 = 5;
pub const no_text_index: u32 = std.math.maxInt(u32);

pub const SourceId = enum(u32) {
    display_config = 1,
    dxgi = 2,
    edid = 3,
    setup_api_monitor = 4,
    oem_connector_profile = 5,
};

pub const SourceStatus = enum(u32) {
    complete = 1,
    retained = 2,
    unavailable = 3,
    unsupported = 5,
};

pub const TextKind = enum(u32) {
    monitor_device_path = 1,
    source_device_name = 2,
    friendly_name = 3,
    identity_token = 4,
    evidence = 5,
    adapter_device_path = 6,
    adapter_match_token = 7,
};

pub const Phase = enum(u32) {
    warming = 1,
    collecting = 2,
    ready = 3,
    failed_retained = 4,
    failed_no_data = 5,
};

pub const NodeKind = enum(u32) {
    connector = 1,
    monitor = 2,
};

pub const EdgeKind = enum(u32) {
    connector_to_monitor = 1,
};

pub const ChangeKind = enum(u32) {
    added = 1,
    changed = 2,
    removed = 3,
};

pub const UnresolvedReason = enum(u32) {
    conflicting_monitor_path = 1,
    conflicting_target = 2,
    missing_strong_identity = 3,
    conflicting_source_device = 4,
    conflicting_edid_serial = 5,
    conflicting_source_object = 6,
};

pub const EntityKind = enum(u32) {
    node = 1,
    edge = 2,
    capability = 3,
    text = 4,
    unresolved = 5,
};

pub const SourceMask = struct {
    pub const display_config: u64 = 1 << 0;
    pub const dxgi: u64 = 1 << 1;
    pub const edid: u64 = 1 << 2;
    pub const setup_api_monitor: u64 = 1 << 3;
    pub const oem_connector_profile: u64 = 1 << 4;
    pub const known: u64 = display_config | dxgi | edid |
        setup_api_monitor | oem_connector_profile;
};

pub const RefreshValid = struct {
    pub const refresh_epoch: u64 = 1 << 0;
    pub const captured_utc_ms: u64 = 1 << 1;
    pub const monotonic_ms: u64 = 1 << 2;
    pub const source_masks: u64 = 1 << 3;
    pub const required: u64 = refresh_epoch | captured_utc_ms |
        monotonic_ms | source_masks;
    pub const known: u64 = required;
};

pub const BatchValid = struct {
    pub const source_generation: u64 = 1 << 0;
    pub const captured_utc_ms: u64 = 1 << 1;
    pub const status: u64 = 1 << 2;
    pub const facts: u64 = 1 << 3;
    pub const text_bindings: u64 = 1 << 4;
    pub const required: u64 = source_generation | captured_utc_ms |
        status | facts | text_bindings;
    pub const known: u64 = required;
};

pub const TextValid = struct {
    pub const bytes: u64 = 1 << 0;
    pub const required: u64 = bytes;
    pub const known: u64 = required;
};

pub const FactValid = struct {
    pub const adapter_luid: u64 = 1 << 0;
    pub const target_id: u64 = 1 << 1;
    pub const connector_instance: u64 = 1 << 2;
    pub const output_technology: u64 = 1 << 3;
    pub const status_flags: u64 = 1 << 4;
    pub const monitor_path: u64 = 1 << 5;
    pub const source_device_name: u64 = 1 << 6;
    pub const friendly_name: u64 = 1 << 7;
    pub const edid_serial: u64 = 1 << 8;
    pub const evidence: u64 = 1 << 9;
    pub const bounds: u64 = 1 << 10;
    pub const refresh_rate: u64 = 1 << 11;
    pub const bits_per_color_channel: u64 = 1 << 12;
    pub const minimum_luminance: u64 = 1 << 13;
    pub const maximum_luminance: u64 = 1 << 14;
    pub const full_frame_luminance: u64 = 1 << 15;
    pub const capability_flags: u64 = 1 << 16;
    pub const observed_at_utc_ms: u64 = 1 << 17;
    pub const source_object_key: u64 = 1 << 18;
    pub const payload_handle: u64 = 1 << 19;
    pub const adapter_device_path: u64 = 1 << 20;
    pub const preferred_adapter_token: u64 = 1 << 21;
    pub const oem_match_rule: u64 = 1 << 22;
    pub const known: u64 = (1 << 23) - 1;
};

pub const IdentityMask = struct {
    pub const target: u64 = 1 << 0;
    pub const monitor_path: u64 = 1 << 1;
    pub const source_device_name: u64 = 1 << 2;
    pub const edid_serial: u64 = 1 << 3;
    pub const source_object_key: u64 = 1 << 4;
    pub const known: u64 = (1 << 5) - 1;
    pub const strong: u64 = target | monitor_path | source_device_name |
        edid_serial | source_object_key;
};

pub const CapabilityFlags = struct {
    pub const advanced_color_supported: u64 = 1 << 0;
    pub const advanced_color_enabled: u64 = 1 << 1;
    pub const high_dynamic_range_supported: u64 = 1 << 2;
    pub const high_dynamic_range_user_enabled: u64 = 1 << 3;
    pub const high_dynamic_range_active: u64 = 1 << 4;
    pub const absolute_luminance_available: u64 = 1 << 5;
    pub const known: u64 = (1 << 6) - 1;
    pub const input_known: u64 = known & ~absolute_luminance_available;
};

pub const DisplayStatusFlags = struct {
    pub const active: u32 = 1 << 0;
    pub const connected: u32 = 1 << 1;
    pub const primary: u32 = 1 << 2;
    pub const internal: u32 = 1 << 3;
    pub const known: u32 = active | connected | primary | internal;
};

pub const OemMatchFlags = struct {
    pub const allow_any_active_adapter: u32 = 1 << 0;
    pub const known: u32 = allow_any_active_adapter;
};

pub const SnapshotFlags = struct {
    pub const has_last_good: u32 = 1 << 0;
    pub const required_source_incomplete: u32 = 1 << 1;
    pub const has_unresolved: u32 = 1 << 2;
    pub const content_changed: u32 = 1 << 3;
    pub const known: u32 = has_last_good | required_source_incomplete |
        has_unresolved | content_changed;
};

pub const PersistenceValid = struct {
    pub const operation_epoch: u64 = 1 << 0;
    pub const payload: u64 = 1 << 1;
    pub const required: u64 = operation_epoch | payload;
    pub const known: u64 = required;
};

pub const ReadValid = struct {
    pub const state_revision: u64 = 1 << 0;
    pub const content_generation: u64 = 1 << 1;
    pub const required: u64 = state_revision | content_generation;
    pub const known: u64 = required;
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    maximum_source_count: u32,
    maximum_observation_count: u32,
    maximum_node_count: u32,
    maximum_edge_count: u32,
    maximum_capability_count: u32,
    maximum_diff_entry_count: u32,
    maximum_text_binding_count: u32,
    maximum_text_byte_count: u32,
    maximum_unresolved_count: u32,
    maximum_source_batch_count: u32,
    identity_index_capacity: u32,
    text_index_capacity: u32,
    resident_byte_budget: u64,
    required_source_mask: u64,
    optional_source_mask: u64,
    identity_source_priority_order: u64,
    friendly_name_source_priority_order: u64,
    capability_source_priority_order: u64,
    projection_source_priority_order: u64,
    identity_contract_version: u32,
    capability_contract_version: u32,
    maximum_future_skew_ms: u32,
    flags: u32,
    reserved: [3]u64,
};

pub const Capacity = extern struct {
    struct_size: u32,
    maximum_source_count: u32,
    maximum_observation_count: u32,
    maximum_node_count: u32,
    maximum_edge_count: u32,
    maximum_capability_count: u32,
    maximum_diff_entry_count: u32,
    maximum_text_binding_count: u32,
    maximum_text_byte_count: u32,
    maximum_unresolved_count: u32,
    maximum_source_batch_count: u32,
    identity_index_capacity: u32,
    text_index_capacity: u32,
    reserved_u32: u32,
    resident_byte_count: u64,
    reserved: [8]u64,
};

pub const RefreshInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    refresh_epoch: u64,
    captured_utc_ms: i64,
    monotonic_ms: u64,
    required_source_mask: u64,
    requested_source_mask: u64,
    valid_mask: u64,
    flags: u64,
    reserved: [1]u64,
};

pub const SourceBatchHeader = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    refresh_epoch: u64,
    source_generation: u64,
    captured_utc_ms: i64,
    source_id: u32,
    status: u32,
    fact_count: u32,
    text_binding_count: u32,
    text_byte_count: u32,
    reserved_u32: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const TextInput = extern struct {
    struct_size: u32,
    normalization_kind: u32,
    byte_offset: u32,
    byte_length: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const DisplayFact = extern struct {
    struct_size: u32,
    source_id: u32,
    valid_mask: u64,
    identity_mask: u64,
    source_record_ordinal: u64,
    adapter_luid: u64,
    target_id: u32,
    connector_instance: u32,
    output_technology: u32,
    status_flags: u32,
    monitor_path_text_index: u32,
    source_device_text_index: u32,
    friendly_name_text_index: u32,
    edid_serial_text_index: u32,
    evidence_text_index: u32,
    match_text_index: u32,
    match_flags: u32,
    reserved_u32: u32,
    position_x: i32,
    position_y: i32,
    width: i32,
    height: i32,
    refresh_numerator: u32,
    refresh_denominator: u32,
    bits_per_color_channel: u32,
    minimum_luminance_milli_nits: u32,
    maximum_luminance_milli_nits: u32,
    maximum_full_frame_luminance_milli_nits: u32,
    capability_flags: u64,
    observed_at_utc_ms: i64,
    source_object_key: u64,
    payload_handle: Handle128,
};

pub const FinalizeInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    refresh_epoch: u64,
    expected_source_mask: u64,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const AbortInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    refresh_epoch: u64,
    valid_mask: u64,
    flags: u64,
    reserved: [3]u64,
};

pub const ReadInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    state_revision: u64,
    content_generation: u64,
    valid_mask: u64,
    flags: u64,
    reserved: [1]u64,
};

pub const Handle128 = extern struct {
    high: u64,
    low: u64,

    pub fn isZero(self: Handle128) bool {
        return self.high == 0 and self.low == 0;
    }
};

pub const NodeOutput = extern struct {
    struct_size: u32,
    kind: u32,
    node_handle: Handle128,
    generation: u64,
    canonical_identity_handle: Handle128,
    display_name_handle: Handle128,
    connector_kind: u32,
    connector_instance: u32,
    status_flags: u64,
    source_mask: u64,
    evidence_mask: u64,
    valid_mask: u64,
    identity_mask: u64,
    primary_source_id: u32,
    reserved_u32: u32,
    primary_source_record_ordinal: u64,
    primary_payload_handle: Handle128,
    oem_profile_payload_handle: Handle128,
};

pub const EdgeOutput = extern struct {
    struct_size: u32,
    kind: u32,
    parent_handle: Handle128,
    child_handle: Handle128,
    source_mask: u64,
    flags: u64,
    reserved: [1]u64,
};

pub const DisplayCapabilityOutput = extern struct {
    struct_size: u32,
    flags: u32,
    node_handle: Handle128,
    generation: u64,
    display_identity_handle: Handle128,
    source_device_handle: Handle128,
    friendly_name_handle: Handle128,
    left: i32,
    top: i32,
    right: i32,
    bottom: i32,
    bits_per_color_channel: u32,
    minimum_luminance_milli_nits: u32,
    maximum_luminance_milli_nits: u32,
    maximum_full_frame_luminance_milli_nits: u32,
    output_technology: u32,
    capability_source_mask: u32,
    refresh_numerator: u32,
    refresh_denominator: u32,
    valid_mask: u64,
};

pub const TextOutput = extern struct {
    struct_size: u32,
    normalization_kind: u32,
    handle: Handle128,
    byte_offset: u32,
    byte_length: u32,
    flags: u64,
    reserved: [3]u64,
};

pub const DiffEntry = extern struct {
    struct_size: u32,
    entity_kind: u32,
    change_kind: u32,
    reserved_u32: u32,
    entity_handle: Handle128,
    old_generation: u64,
    new_generation: u64,
    changed_field_mask: u64,
};

pub const UnresolvedOutput = extern struct {
    struct_size: u32,
    reason: u32,
    source_record_ordinal: u64,
    source_mask: u64,
    first_identity: Handle128,
    second_identity: Handle128,
    identity_kind: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const SnapshotOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    state_revision: u64,
    refresh_epoch: u64,
    content_generation: u64,
    last_success_utc_ms: i64,
    last_attempt_utc_ms: i64,
    phase: u32,
    flags: u32,
    current_source_mask: u64,
    retained_source_mask: u64,
    failed_source_mask: u64,
    unsupported_source_mask: u64,
    node_count: u32,
    edge_count: u32,
    capability_count: u32,
    text_count: u32,
    unresolved_count: u32,
    diff_count: u32,
    resident_byte_count: u64,
};

pub const PersistenceInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    valid_mask: u64,
    flags: u64,
    reserved: [3]u64,
};

pub const PersistenceHeader = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    content_generation: u64,
    state_revision: u64,
    last_refresh_epoch: u64,
    last_success_utc_ms: i64,
    last_attempt_utc_ms: i64,
    current_source_mask: u64,
    retained_source_mask: u64,
    failed_source_mask: u64,
    unsupported_source_mask: u64,
    source_generations: [source_count]u64,
    fact_count: u32,
    text_count: u32,
    text_byte_count: u32,
    phase: u32,
    checksum: Handle128,
    last_monotonic_ms: u64,
    reserved: [1]u64,
};

pub fn sourceFromInt(value: u32) ?SourceId {
    return switch (value) {
        1 => .display_config,
        2 => .dxgi,
        3 => .edid,
        4 => .setup_api_monitor,
        5 => .oem_connector_profile,
        else => null,
    };
}

pub fn statusFromInt(value: u32) ?SourceStatus {
    return switch (value) {
        1 => .complete,
        2 => .retained,
        3 => .unavailable,
        5 => .unsupported,
        else => null,
    };
}

pub fn textKindFromInt(value: u32) ?TextKind {
    return switch (value) {
        1 => .monitor_device_path,
        2 => .source_device_name,
        3 => .friendly_name,
        4 => .identity_token,
        5 => .evidence,
        6 => .adapter_device_path,
        7 => .adapter_match_token,
        else => null,
    };
}

pub fn unresolvedReasonFromInt(value: u32) ?UnresolvedReason {
    return switch (value) {
        1 => .conflicting_monitor_path,
        2 => .conflicting_target,
        3 => .missing_strong_identity,
        4 => .conflicting_source_device,
        5 => .conflicting_edid_serial,
        6 => .conflicting_source_object,
        else => null,
    };
}

pub fn sourceBit(source: SourceId) u64 {
    return @as(u64, 1) << @intCast(@intFromEnum(source) - 1);
}

pub fn validConfig(config: *const Config) bool {
    const configured_mask = config.required_source_mask | config.optional_source_mask;
    const maximum_possible_nodes = std.math.mul(
        u32,
        config.maximum_observation_count,
        2,
    ) catch return false;
    const primary_diffs = std.math.add(
        u32,
        config.maximum_node_count,
        std.math.add(
            u32,
            config.maximum_edge_count,
            config.maximum_capability_count,
        ) catch return false,
    ) catch return false;
    const maximum_entities = std.math.add(
        u32,
        primary_diffs,
        std.math.add(
            u32,
            config.maximum_text_binding_count,
            config.maximum_unresolved_count,
        ) catch return false,
    ) catch return false;
    const maximum_possible_diffs = std.math.mul(
        u32,
        maximum_entities,
        2,
    ) catch return false;
    const minimum_identity_index_capacity = std.math.mul(
        u32,
        config.maximum_observation_count,
        5,
    ) catch return false;
    return config.abi_version == abi_version and
        config.struct_size == @sizeOf(Config) and
        config.generation != 0 and
        config.maximum_source_count == source_count and
        config.maximum_observation_count != 0 and
        config.maximum_node_count >= config.maximum_observation_count and
        config.maximum_node_count <= maximum_possible_nodes and
        config.maximum_edge_count != 0 and
        config.maximum_capability_count != 0 and
        config.maximum_diff_entry_count >= maximum_possible_diffs and
        config.maximum_text_binding_count != 0 and
        config.maximum_text_byte_count != 0 and
        config.maximum_unresolved_count != 0 and
        config.maximum_source_batch_count == source_count and
        validIndexCapacity(config.identity_index_capacity, minimum_identity_index_capacity) and
        validIndexCapacity(config.text_index_capacity, config.maximum_text_binding_count) and
        config.resident_byte_budget != 0 and
        config.required_source_mask != 0 and
        (config.required_source_mask & config.optional_source_mask) == 0 and
        configured_mask != 0 and (configured_mask & ~SourceMask.known) == 0 and
        validPriorityOrder(config.identity_source_priority_order, SourceMask.known) and
        validPriorityOrder(config.friendly_name_source_priority_order, SourceMask.known) and
        validPriorityOrder(config.capability_source_priority_order, SourceMask.known) and
        validPriorityOrder(config.projection_source_priority_order, SourceMask.known) and
        config.identity_contract_version == identity_contract_version and
        config.capability_contract_version == capability_contract_version and
        config.flags == 0 and allZero(&config.reserved);
}

pub fn validRefresh(input: *const RefreshInput, config: *const Config) bool {
    const configured_mask = config.required_source_mask | config.optional_source_mask;
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(RefreshInput) and
        input.configuration_generation == config.generation and
        input.refresh_epoch != 0 and input.captured_utc_ms >= 0 and
        input.monotonic_ms != 0 and
        input.required_source_mask == config.required_source_mask and
        (input.requested_source_mask & config.required_source_mask) ==
            config.required_source_mask and
        (input.requested_source_mask & ~configured_mask) == 0 and
        input.valid_mask == RefreshValid.required and input.flags == 0 and
        allZero(&input.reserved);
}

pub fn validBatch(
    header: *const SourceBatchHeader,
    config: *const Config,
    refresh_epoch: u64,
    facts: []const DisplayFact,
    texts: []const TextInput,
    bytes: []const u8,
) bool {
    const source = sourceFromInt(header.source_id) orelse return false;
    const status = statusFromInt(header.status) orelse return false;
    if (header.abi_version != abi_version or
        header.struct_size != @sizeOf(SourceBatchHeader) or
        header.configuration_generation != config.generation or
        header.refresh_epoch != refresh_epoch or header.source_generation == 0 or
        header.captured_utc_ms < 0 or header.fact_count != facts.len or
        header.text_binding_count != texts.len or header.text_byte_count != bytes.len or
        header.fact_count > config.maximum_observation_count or
        header.text_binding_count > config.maximum_text_binding_count or
        header.text_byte_count > config.maximum_text_byte_count or
        header.reserved_u32 != 0 or header.valid_mask != BatchValid.required or
        header.flags != 0 or !allZero(&header.reserved))
    {
        return false;
    }
    if (status != .complete and (facts.len != 0 or texts.len != 0 or bytes.len != 0)) {
        return false;
    }
    for (texts) |*text| if (!validText(text, bytes)) return false;
    for (facts) |*fact| if (!validFact(fact, source, texts.len)) return false;
    for (0..texts.len) |text_index| {
        if (!textBindingReferenced(facts, @intCast(text_index))) return false;
    }
    return true;
}

pub fn validText(input: *const TextInput, bytes: []const u8) bool {
    _ = textKindFromInt(input.normalization_kind) orelse return false;
    const value = byteSlice(bytes, input.byte_offset, input.byte_length) orelse return false;
    return input.struct_size == @sizeOf(TextInput) and
        input.byte_length != 0 and std.unicode.utf8ValidateSlice(value) and
        std.mem.indexOfScalar(u8, value, 0) == null and
        input.valid_mask == TextValid.required and input.flags == 0 and
        allZero(&input.reserved);
}

pub fn validFact(
    fact: *const DisplayFact,
    source: SourceId,
    text_count: usize,
) bool {
    if (fact.struct_size != @sizeOf(DisplayFact) or
        fact.source_id != @intFromEnum(source) or
        fact.source_record_ordinal == 0 or
        fact.valid_mask == 0 or (fact.valid_mask & ~FactValid.known) != 0 or
        fact.identity_mask == 0 or (fact.identity_mask & ~IdentityMask.known) != 0 or
        (fact.identity_mask & IdentityMask.strong) == 0 or
        fact.capability_flags & ~CapabilityFlags.input_known != 0 or
        fact.status_flags & ~DisplayStatusFlags.known != 0 or
        fact.match_flags & ~OemMatchFlags.known != 0 or
        fact.reserved_u32 != 0 or
        (fact.valid_mask & FactValid.payload_handle) == 0 or
        fact.payload_handle.isZero())
    {
        return false;
    }
    if (!identityMaskBackedByValidity(fact)) return false;
    if (!validOptionalTextIndex(fact, FactValid.monitor_path, fact.monitor_path_text_index, text_count) or
        !validOptionalTextIndex(fact, FactValid.source_device_name, fact.source_device_text_index, text_count) or
        !validOptionalTextIndex(fact, FactValid.friendly_name, fact.friendly_name_text_index, text_count) or
        !validOptionalTextIndex(fact, FactValid.edid_serial, fact.edid_serial_text_index, text_count) or
        !validOptionalTextIndex(fact, FactValid.evidence, fact.evidence_text_index, text_count) or
        !validMatchTextIndex(fact, text_count))
    {
        return false;
    }
    const is_oem = source == .oem_connector_profile;
    const has_oem_rule = (fact.valid_mask & FactValid.oem_match_rule) != 0;
    const has_preferred_adapter =
        (fact.valid_mask & FactValid.preferred_adapter_token) != 0;
    const has_adapter_path =
        (fact.valid_mask & FactValid.adapter_device_path) != 0;
    if (is_oem != has_oem_rule or
        (has_oem_rule and
            ((fact.valid_mask & FactValid.output_technology) == 0 or
                (!has_preferred_adapter and
                    (fact.match_flags & OemMatchFlags.allow_any_active_adapter) == 0))) or
        (!has_oem_rule and fact.match_flags != 0) or
        (has_preferred_adapter and !is_oem) or
        (has_adapter_path and source != .display_config))
    {
        return false;
    }
    if ((fact.valid_mask & FactValid.target_id) != 0 and
        (fact.valid_mask & FactValid.adapter_luid) == 0) return false;
    if ((fact.identity_mask & IdentityMask.target) != 0 and fact.adapter_luid == 0) {
        return false;
    }
    if ((fact.identity_mask & IdentityMask.source_object_key) != 0 and
        fact.source_object_key == 0) return false;
    if ((fact.valid_mask & FactValid.connector_instance) != 0 and
        fact.connector_instance == 0) return false;
    if ((fact.valid_mask & FactValid.bounds) != 0) {
        if (fact.width <= 0 or fact.height <= 0) return false;
        _ = std.math.add(i32, fact.position_x, fact.width) catch return false;
        _ = std.math.add(i32, fact.position_y, fact.height) catch return false;
    }
    if ((fact.valid_mask & FactValid.refresh_rate) != 0 and
        (fact.refresh_numerator == 0 or fact.refresh_denominator == 0)) return false;
    if ((fact.valid_mask & FactValid.bits_per_color_channel) != 0 and
        fact.bits_per_color_channel == 0) return false;
    const luminance_mask = FactValid.minimum_luminance |
        FactValid.maximum_luminance | FactValid.full_frame_luminance;
    const supplied_luminance_mask = fact.valid_mask & luminance_mask;
    if (supplied_luminance_mask != 0 and supplied_luminance_mask != luminance_mask) {
        return false;
    }
    if (supplied_luminance_mask != 0 and
        (fact.maximum_luminance_milli_nits == 0 or
            fact.maximum_full_frame_luminance_milli_nits == 0 or
            fact.minimum_luminance_milli_nits >
                fact.maximum_full_frame_luminance_milli_nits or
            fact.maximum_full_frame_luminance_milli_nits >
                fact.maximum_luminance_milli_nits))
    {
        return false;
    }
    if ((fact.valid_mask & FactValid.observed_at_utc_ms) != 0 and
        fact.observed_at_utc_ms < 0) return false;
    return absentFieldsAreZero(fact);
}

pub fn validFinalize(
    input: *const FinalizeInput,
    config: *const Config,
    refresh_epoch: u64,
    requested_source_mask: u64,
) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(FinalizeInput) and
        input.configuration_generation == config.generation and
        input.refresh_epoch == refresh_epoch and
        input.expected_source_mask == requested_source_mask and
        input.valid_mask == 1 and input.flags == 0 and allZero(&input.reserved);
}

pub fn validRead(input: *const ReadInput, config: *const Config) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(ReadInput) and
        input.configuration_generation == config.generation and
        input.state_revision != 0 and
        input.valid_mask == ReadValid.required and input.flags == 0 and
        allZero(&input.reserved);
}

pub fn validAbort(
    input: *const AbortInput,
    config: *const Config,
    refresh_epoch: u64,
) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(AbortInput) and
        input.configuration_generation == config.generation and
        input.refresh_epoch == refresh_epoch and input.valid_mask == 1 and
        input.flags == 0 and allZero(&input.reserved);
}

pub fn validPersistenceInput(input: *const PersistenceInput, config: *const Config) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(PersistenceInput) and
        input.configuration_generation == config.generation and
        input.operation_epoch != 0 and
        input.valid_mask == PersistenceValid.required and input.flags == 0 and
        allZero(&input.reserved);
}

pub fn validPersistenceHeader(
    header: *const PersistenceHeader,
    config: *const Config,
    input: *const PersistenceInput,
    facts: []const DisplayFact,
    texts: []const TextInput,
    bytes: []const u8,
) bool {
    const configured_mask = config.required_source_mask | config.optional_source_mask;
    const payload_mask = header.current_source_mask | header.retained_source_mask;
    const state_mask = payload_mask | header.failed_source_mask |
        header.unsupported_source_mask;
    const phase = phaseFromInt(header.phase) orelse return false;
    if (header.abi_version != abi_version or
        header.struct_size != @sizeOf(PersistenceHeader) or
        header.configuration_generation != config.generation or
        header.operation_epoch != input.operation_epoch or
        header.content_generation == 0 or header.state_revision == 0 or
        header.last_refresh_epoch == 0 or header.last_success_utc_ms < 0 or
        header.last_attempt_utc_ms < header.last_success_utc_ms or
        header.last_monotonic_ms == 0 or
        (state_mask & ~configured_mask) != 0 or
        (header.current_source_mask & header.retained_source_mask) != 0 or
        (header.current_source_mask & header.failed_source_mask) != 0 or
        (header.current_source_mask & header.unsupported_source_mask) != 0 or
        (header.retained_source_mask & header.unsupported_source_mask) != 0 or
        (header.failed_source_mask & header.unsupported_source_mask) != 0 or
        (phase == .ready and
            (header.current_source_mask & config.required_source_mask) !=
                config.required_source_mask) or
        (phase == .failed_retained and
            (header.current_source_mask != 0 or
                (header.retained_source_mask & config.required_source_mask) !=
                    config.required_source_mask or
                (header.failed_source_mask & config.required_source_mask) == 0)) or
        (phase != .ready and phase != .failed_retained) or
        header.fact_count != facts.len or header.fact_count > config.maximum_observation_count or
        header.text_count != texts.len or header.text_count > config.maximum_text_binding_count or
        header.text_byte_count != bytes.len or header.text_byte_count > config.maximum_text_byte_count or
        header.checksum.isZero() or !allZero(&header.reserved))
    {
        return false;
    }
    for (header.source_generations, 0..) |generation, index| {
        const bit = @as(u64, 1) << @intCast(index);
        if (((state_mask & bit) != 0) != (generation != 0)) return false;
    }
    for (texts) |*text| if (!validText(text, bytes)) return false;
    for (facts, 0..) |*fact, index| {
        const source = sourceFromInt(fact.source_id) orelse return false;
        const source_bit = sourceBit(source);
        if ((payload_mask & source_bit) == 0 or
            !validFact(fact, source, texts.len))
        {
            return false;
        }
        for (facts[0..index]) |prior| {
            if (prior.source_id == fact.source_id and
                prior.source_record_ordinal == fact.source_record_ordinal)
            {
                return false;
            }
        }
    }
    for (0..texts.len) |text_index| {
        if (!textBindingReferenced(facts, @intCast(text_index))) return false;
    }
    return true;
}

pub fn phaseFromInt(value: u32) ?Phase {
    return switch (value) {
        1 => .warming,
        2 => .collecting,
        3 => .ready,
        4 => .failed_retained,
        5 => .failed_no_data,
        else => null,
    };
}

pub fn priorityRank(order: u64, source: SourceId) u32 {
    var rank: u32 = 0;
    while (rank < source_count) : (rank += 1) {
        const value: u8 = @truncate(order >> @intCast(rank * 8));
        if (value == @intFromEnum(source)) return rank;
    }
    return std.math.maxInt(u32);
}

pub fn byteSlice(bytes: []const u8, offset: u32, length: u32) ?[]const u8 {
    const end = std.math.add(u32, offset, length) catch return null;
    if (end > bytes.len) return null;
    return bytes[offset..end];
}

pub fn allZero(values: []const u64) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

pub fn emptyBytes(comptime T: type, value: *const T) bool {
    for (std.mem.asBytes(value)) |byte| if (byte != 0) return false;
    return true;
}

fn validIndexCapacity(capacity: u32, count: u32) bool {
    return capacity >= count and std.math.isPowerOfTwo(capacity);
}

fn validPriorityOrder(order: u64, configured_mask: u64) bool {
    var seen: u64 = 0;
    var index: u32 = 0;
    while (index < source_count) : (index += 1) {
        const raw: u8 = @truncate(order >> @intCast(index * 8));
        const source = sourceFromInt(raw) orelse return false;
        const bit = sourceBit(source);
        if ((configured_mask & bit) == 0 or (seen & bit) != 0) return false;
        seen |= bit;
    }
    return seen == configured_mask;
}

fn identityMaskBackedByValidity(fact: *const DisplayFact) bool {
    if ((fact.identity_mask & IdentityMask.target) != 0 and
        (fact.valid_mask & (FactValid.adapter_luid | FactValid.target_id)) !=
            (FactValid.adapter_luid | FactValid.target_id)) return false;
    if ((fact.identity_mask & IdentityMask.monitor_path) != 0 and
        (fact.valid_mask & FactValid.monitor_path) == 0) return false;
    if ((fact.identity_mask & IdentityMask.source_device_name) != 0 and
        (fact.valid_mask & FactValid.source_device_name) == 0) return false;
    if ((fact.identity_mask & IdentityMask.edid_serial) != 0 and
        (fact.valid_mask & FactValid.edid_serial) == 0) return false;
    if ((fact.identity_mask & IdentityMask.source_object_key) != 0 and
        (fact.valid_mask & FactValid.source_object_key) == 0) return false;
    return true;
}

fn validOptionalTextIndex(
    fact: *const DisplayFact,
    bit: u64,
    index: u32,
    text_count: usize,
) bool {
    return if ((fact.valid_mask & bit) != 0)
        index < text_count
    else
        index == 0;
}

fn absentFieldsAreZero(fact: *const DisplayFact) bool {
    return ((fact.valid_mask & FactValid.adapter_luid) != 0 or fact.adapter_luid == 0) and
        ((fact.valid_mask & FactValid.target_id) != 0 or fact.target_id == 0) and
        ((fact.valid_mask & FactValid.connector_instance) != 0 or fact.connector_instance == 0) and
        ((fact.valid_mask & FactValid.output_technology) != 0 or fact.output_technology == 0) and
        ((fact.valid_mask & FactValid.status_flags) != 0 or fact.status_flags == 0) and
        ((fact.valid_mask & FactValid.bounds) != 0 or
            (fact.position_x == 0 and fact.position_y == 0 and fact.width == 0 and fact.height == 0)) and
        ((fact.valid_mask & FactValid.refresh_rate) != 0 or
            (fact.refresh_numerator == 0 and fact.refresh_denominator == 0)) and
        ((fact.valid_mask & FactValid.bits_per_color_channel) != 0 or
            fact.bits_per_color_channel == 0) and
        ((fact.valid_mask & FactValid.minimum_luminance) != 0 or
            fact.minimum_luminance_milli_nits == 0) and
        ((fact.valid_mask & FactValid.maximum_luminance) != 0 or
            fact.maximum_luminance_milli_nits == 0) and
        ((fact.valid_mask & FactValid.full_frame_luminance) != 0 or
            fact.maximum_full_frame_luminance_milli_nits == 0) and
        ((fact.valid_mask & FactValid.capability_flags) != 0 or fact.capability_flags == 0) and
        ((fact.valid_mask & FactValid.observed_at_utc_ms) != 0 or fact.observed_at_utc_ms == 0) and
        ((fact.valid_mask & FactValid.source_object_key) != 0 or fact.source_object_key == 0) and
        ((fact.valid_mask & FactValid.oem_match_rule) != 0 or fact.match_flags == 0);
}

fn textBindingReferenced(facts: []const DisplayFact, text_index: u32) bool {
    for (facts) |fact| {
        const match_text_valid =
            (fact.valid_mask & (FactValid.adapter_device_path |
                FactValid.preferred_adapter_token)) != 0;
        if (((fact.valid_mask & FactValid.monitor_path) != 0 and
            fact.monitor_path_text_index == text_index) or
            ((fact.valid_mask & FactValid.source_device_name) != 0 and
                fact.source_device_text_index == text_index) or
            ((fact.valid_mask & FactValid.friendly_name) != 0 and
                fact.friendly_name_text_index == text_index) or
            ((fact.valid_mask & FactValid.edid_serial) != 0 and
                fact.edid_serial_text_index == text_index) or
            ((fact.valid_mask & FactValid.evidence) != 0 and
                fact.evidence_text_index == text_index) or
            (match_text_valid and fact.match_text_index == text_index))
        {
            return true;
        }
    }
    return false;
}

fn validMatchTextIndex(
    fact: *const DisplayFact,
    text_count: usize,
) bool {
    const mask = FactValid.adapter_device_path | FactValid.preferred_adapter_token;
    const supplied = fact.valid_mask & mask;
    return if (supplied == 0)
        fact.match_text_index == 0
    else
        (supplied == FactValid.adapter_device_path or
            supplied == FactValid.preferred_adapter_token) and
            fact.match_text_index < text_count;
}
