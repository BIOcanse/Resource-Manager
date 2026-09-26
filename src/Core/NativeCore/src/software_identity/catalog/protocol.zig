const std = @import("std");

pub const abi_version: u32 = 0x0005_0000;
pub const maximum_signal_count: u32 = 6;

pub const AliasKind = enum(u32) {
    steam_app_id = 1,
    package_identifier = 2,
    installed_name = 3,
    executable_name = 4,
    product_name = 5,
    launcher_token = 6,
    managed_child_segment = 7,
};

pub const MatchMode = enum(u32) {
    installed_software = 1,
    process = 2,
    portable_process = 3,
};

pub const MatchStatus = enum(u32) {
    no_match = 1,
    matched = 2,
    conflict = 3,
};

pub const MatchConfidence = enum(u32) {
    none = 0,
    candidate = 1,
    confirmed = 2,
};

pub const KnownMatchMode = enum(u32) {
    none = 0,
    identity = 1,
    root = 2,
    launcher_managed_child = 3,
};

pub const SignalKind = enum(u32) {
    process_name = 1,
    product_name = 2,
    file_description = 3,
    window_application_user_model_id = 4,
    application_user_model_id = 5,
    window_title = 6,
};

pub const Evidence = struct {
    pub const steam_app_id: u64 = 1 << 0;
    pub const package_identifier: u64 = 1 << 1;
    pub const installed_name: u64 = 1 << 2;
    pub const executable_name: u64 = 1 << 3;
    pub const product_name: u64 = 1 << 4;
    pub const known: u64 = steam_app_id |
        package_identifier |
        installed_name |
        executable_name |
        product_name;
};

pub const SignalEvidence = struct {
    pub const process_name: u64 = 1 << 0;
    pub const product_name: u64 = 1 << 1;
    pub const file_description: u64 = 1 << 2;
    pub const window_application_user_model_id: u64 = 1 << 3;
    pub const application_user_model_id: u64 = 1 << 4;
    pub const window_title: u64 = 1 << 5;
    pub const known: u64 = process_name |
        product_name |
        file_description |
        window_application_user_model_id |
        application_user_model_id |
        window_title;
};

pub const AliasFlags = struct {
    pub const prohibited: u32 = 1 << 0;
    pub const known: u32 = prohibited;
};

pub const ConfigFlags = struct {
    pub const launcher_non_entry_requires_root: u64 = 1 << 0;
    pub const known: u64 = launcher_non_entry_requires_root;
};

pub const KnownOutputFlags = struct {
    pub const query_launcher: u64 = 1 << 0;
    pub const entry_launcher: u64 = 1 << 1;
    pub const root_hit: u64 = 1 << 2;
    pub const known: u64 = query_launcher | entry_launcher | root_hit;
};

pub const ReplaceValid = struct {
    pub const catalog_generation: u64 = 1 << 0;
    pub const operation_epoch: u64 = 1 << 1;
    pub const entries: u64 = 1 << 2;
    pub const aliases: u64 = 1 << 3;
    pub const roots: u64 = 1 << 4;
    pub const key_bytes: u64 = 1 << 5;
    pub const required: u64 = catalog_generation | operation_epoch | entries | aliases | roots | key_bytes;
    pub const known: u64 = required;
};

pub const QueryValid = struct {
    pub const catalog_generation: u64 = 1 << 0;
    pub const query_epoch: u64 = 1 << 1;
    pub const facts: u64 = 1 << 2;
    pub const key_bytes: u64 = 1 << 3;
    pub const required: u64 = catalog_generation | query_epoch | facts | key_bytes;
    pub const known: u64 = required;
};

pub const KnownQueryValid = struct {
    pub const catalog_generation: u64 = 1 << 0;
    pub const query_epoch: u64 = 1 << 1;
    pub const signals: u64 = 1 << 2;
    pub const key_bytes: u64 = 1 << 3;
    pub const executable_path: u64 = 1 << 4;
    pub const required: u64 = catalog_generation | query_epoch | signals | key_bytes;
    pub const known: u64 = required | executable_path;
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    maximum_entry_count: u32,
    maximum_alias_count: u32,
    maximum_root_count: u32,
    maximum_catalog_key_byte_count: u32,
    maximum_query_fact_count: u32,
    maximum_query_signal_count: u32,
    maximum_query_key_byte_count: u32,
    entry_index_capacity: u32,
    alias_index_capacity: u32,
    identity_index_capacity: u32,
    root_index_capacity: u32,
    required_prohibited_alias_count: u32,
    required_launcher_token_count: u32,
    required_managed_child_rule_count: u32,
    minimum_contains_key_length: u32,
    reserved_capacity: u32,
    resident_byte_budget: u64,
    exact_text_score: i32,
    contains_text_score: i32,
    identity_minimum_score: i32,
    strong_evidence_minimum_score: i32,
    root_hit_bonus: i32,
    query_launcher_match_bonus: i32,
    query_launcher_nonmatch_penalty: i32,
    root_launcher_match_bonus: i32,
    root_launcher_nonmatch_penalty: i32,
    root_reject_score: i32,
    signal_weights: [maximum_signal_count]i32,
    root_signal_weights: [maximum_signal_count]i32,
    strong_signal_mask: u32,
    root_signal_mask: u32,
    flags: u64,
    reserved: [4]u64,
};

pub const Capacity = extern struct {
    struct_size: u32,
    entry_capacity: u32,
    alias_capacity: u32,
    root_capacity: u32,
    catalog_key_byte_capacity: u32,
    query_fact_capacity: u32,
    query_signal_capacity: u32,
    query_key_byte_capacity: u32,
    entry_index_capacity: u32,
    alias_index_capacity: u32,
    identity_index_capacity: u32,
    root_index_capacity: u32,
    resident_byte_count: u64,
    reserved: [3]u64,
};

pub const EntryInput = extern struct {
    struct_size: u32,
    flags: u32,
    entry_handle: u64,
    attribution_id_handle: u64,
    display_name_handle: u64,
    source: u32,
    software_kind: u32,
    primary_name_offset: u32,
    primary_name_length: u32,
    reserved: [3]u64,
};

pub const AliasInput = extern struct {
    struct_size: u32,
    kind: u32,
    entry_handle: u64,
    key_offset: u32,
    key_length: u32,
    numeric_value: u64,
    flags: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const RootInput = extern struct {
    struct_size: u32,
    flags: u32,
    root_handle: u64,
    entry_handle: u64,
    key_offset: u32,
    key_length: u32,
    reserved: [3]u64,
};

pub const ReplaceInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    operation_epoch: u64,
    entry_count: u32,
    alias_count: u32,
    root_count: u32,
    key_byte_count: u32,
    prohibited_alias_count: u32,
    launcher_token_count: u32,
    managed_child_rule_count: u32,
    reserved_u32: u32,
    valid_mask: u64,
    reserved: [2]u64,
};

pub const QueryInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    query_epoch: u64,
    mode: u32,
    fact_count: u32,
    key_byte_count: u32,
    flags: u32,
    valid_mask: u64,
    reserved: [3]u64,
};

pub const FactInput = extern struct {
    struct_size: u32,
    kind: u32,
    key_offset: u32,
    key_length: u32,
    numeric_value: u64,
    flags: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const KnownQueryInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    query_epoch: u64,
    signal_count: u32,
    key_byte_count: u32,
    executable_path_offset: u32,
    executable_path_length: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [3]u64,
};

pub const KnownSignalInput = extern struct {
    struct_size: u32,
    kind: u32,
    key_offset: u32,
    key_length: u32,
    flags: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const MatchOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    catalog_fingerprint_low: u64,
    catalog_fingerprint_high: u64,
    query_epoch: u64,
    entry_handle: u64,
    attribution_id_handle: u64,
    display_name_handle: u64,
    source: u32,
    software_kind: u32,
    status: u32,
    confidence: u32,
    evidence_mask: u64,
    conflict_count: u32,
    matched_fact_count: u32,
    flags: u64,
    reserved: [2]u64,
};

pub const KnownMatchOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    catalog_fingerprint_low: u64,
    catalog_fingerprint_high: u64,
    query_epoch: u64,
    entry_handle: u64,
    attribution_id_handle: u64,
    display_name_handle: u64,
    source: u32,
    software_kind: u32,
    status: u32,
    mode: u32,
    score: i32,
    conflict_count: u32,
    matched_root_handle: u64,
    derived_root_byte_length: u32,
    reserved_u32: u32,
    derived_identity_fingerprint_low: u64,
    derived_identity_fingerprint_high: u64,
    evidence_mask: u64,
    flags: u64,
    reserved: [3]u64,
};

pub const CatalogSummary = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    state_revision: u64,
    last_operation_epoch: u64,
    last_query_epoch: u64,
    entry_count: u32,
    alias_count: u32,
    root_count: u32,
    key_byte_count: u32,
    prohibited_alias_count: u32,
    launcher_token_count: u32,
    managed_child_rule_count: u32,
    flags: u32,
    catalog_fingerprint_low: u64,
    catalog_fingerprint_high: u64,
    resident_byte_count: u64,
    reserved: [3]u64,
};

pub fn validConfig(config: *const Config) bool {
    const required_rule_count = @as(u64, config.required_prohibited_alias_count) +
        config.required_launcher_token_count + config.required_managed_child_rule_count;
    if (config.abi_version != abi_version or
        config.struct_size != @sizeOf(Config) or
        config.generation == 0 or
        config.maximum_entry_count == 0 or
        config.maximum_alias_count == 0 or
        config.maximum_catalog_key_byte_count == 0 or
        config.maximum_query_fact_count == 0 or
        config.maximum_query_signal_count > maximum_signal_count or
        config.maximum_query_key_byte_count == 0 or
        !validIndexCapacity(config.entry_index_capacity, config.maximum_entry_count) or
        !validIndexCapacity(config.alias_index_capacity, config.maximum_alias_count) or
        !validIndexCapacity(config.identity_index_capacity, config.maximum_entry_count) or
        !validIndexCapacity(config.root_index_capacity, config.maximum_root_count) or
        config.required_prohibited_alias_count > config.maximum_alias_count or
        config.required_launcher_token_count > config.maximum_alias_count or
        config.required_managed_child_rule_count > config.maximum_alias_count or
        required_rule_count > config.maximum_alias_count or
        config.minimum_contains_key_length == 0 or
        config.reserved_capacity != 0 or
        config.resident_byte_budget == 0 or
        config.exact_text_score <= 0 or config.contains_text_score <= 0 or
        config.identity_minimum_score < 0 or config.strong_evidence_minimum_score < 0 or
        config.root_hit_bonus < 0 or config.query_launcher_match_bonus < 0 or
        config.query_launcher_nonmatch_penalty < 0 or config.root_launcher_match_bonus < 0 or
        config.root_launcher_nonmatch_penalty < 0 or
        (config.strong_signal_mask & ~@as(u32, @intCast(SignalEvidence.known))) != 0 or
        (config.root_signal_mask & ~@as(u32, @intCast(SignalEvidence.known))) != 0 or
        (config.flags & ~ConfigFlags.known) != 0 or
        !allZero(&config.reserved))
    {
        return false;
    }
    for (config.signal_weights) |weight| if (weight < 0) return false;
    for (config.root_signal_weights) |weight| if (weight < 0) return false;
    return validScoreBounds(config);
}

fn validIndexCapacity(index_capacity: u32, item_capacity: u32) bool {
    return index_capacity != 0 and index_capacity >= item_capacity and std.math.isPowerOfTwo(index_capacity);
}

fn validScoreBounds(config: *const Config) bool {
    const text_score = @max(config.exact_text_score, config.contains_text_score);
    var root_total: i64 = 0;
    var strongest: i64 = 0;
    for (config.signal_weights) |weight| {
        const signal_score = @as(i64, text_score) + weight;
        strongest = @max(strongest, signal_score);
    }
    for (config.root_signal_weights) |weight| root_total += @as(i64, text_score) + weight;
    const identity_total = strongest + config.root_hit_bonus + config.query_launcher_match_bonus;
    root_total += config.root_launcher_match_bonus;
    return identity_total <= std.math.maxInt(i32) and root_total <= std.math.maxInt(i32);
}

pub fn validReplace(input: *const ReplaceInput, config: *const Config) bool {
    const actual_rule_count = @as(u64, input.prohibited_alias_count) +
        input.launcher_token_count + input.managed_child_rule_count;
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(ReplaceInput) and
        input.configuration_generation == config.generation and
        input.catalog_generation != 0 and
        input.operation_epoch != 0 and
        input.entry_count <= config.maximum_entry_count and
        input.alias_count <= config.maximum_alias_count and
        input.root_count <= config.maximum_root_count and
        input.key_byte_count <= config.maximum_catalog_key_byte_count and
        input.prohibited_alias_count == config.required_prohibited_alias_count and
        input.launcher_token_count == config.required_launcher_token_count and
        input.managed_child_rule_count == config.required_managed_child_rule_count and
        actual_rule_count <= input.alias_count and
        input.reserved_u32 == 0 and
        input.valid_mask == ReplaceValid.required and
        allZero(&input.reserved);
}

pub fn validEntry(input: *const EntryInput, key_bytes: []const u8) bool {
    return input.struct_size == @sizeOf(EntryInput) and
        input.flags == 0 and
        input.entry_handle != 0 and
        input.attribution_id_handle != 0 and
        input.display_name_handle != 0 and
        input.source != 0 and
        input.software_kind != 0 and
        validNormalizedTextKey(key_bytes, input.primary_name_offset, input.primary_name_length) and
        allZero(&input.reserved);
}

pub fn validAlias(input: *const AliasInput, key_bytes: []const u8) bool {
    const kind = aliasKind(input.kind) orelse return false;
    const prohibited = (input.flags & AliasFlags.prohibited) != 0;
    if (input.struct_size != @sizeOf(AliasInput) or
        (input.flags & ~AliasFlags.known) != 0 or
        input.reserved_u32 != 0 or
        !allZero(&input.reserved))
    {
        return false;
    }
    if (prohibited) {
        return kind == .executable_name and
            input.entry_handle == 0 and
            input.numeric_value == 0 and
            validCanonicalKey(key_bytes, input.key_offset, input.key_length);
    }
    if (kind == .launcher_token or kind == .managed_child_segment) {
        return input.entry_handle == 0 and input.numeric_value == 0 and
            validNormalizedTextKey(key_bytes, input.key_offset, input.key_length);
    }
    if (input.entry_handle == 0) return false;
    return switch (kind) {
        .steam_app_id => input.numeric_value != 0 and
            input.key_offset == 0 and input.key_length == 0,
        .package_identifier, .installed_name, .executable_name, .product_name => input.numeric_value == 0 and validCanonicalKey(key_bytes, input.key_offset, input.key_length),
        .launcher_token, .managed_child_segment => unreachable,
    };
}

pub fn validRoot(input: *const RootInput, key_bytes: []const u8) bool {
    return input.struct_size == @sizeOf(RootInput) and input.flags == 0 and
        input.root_handle != 0 and input.entry_handle != 0 and
        validCanonicalPath(key_bytes, input.key_offset, input.key_length) and
        allZero(&input.reserved);
}

pub fn validQuery(input: *const QueryInput, config: *const Config, catalog_generation: u64) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(QueryInput) and
        input.configuration_generation == config.generation and
        input.catalog_generation == catalog_generation and
        input.catalog_generation != 0 and
        input.query_epoch != 0 and
        matchMode(input.mode) != null and
        input.fact_count <= config.maximum_query_fact_count and
        input.key_byte_count <= config.maximum_query_key_byte_count and
        input.flags == 0 and
        input.valid_mask == QueryValid.required and
        allZero(&input.reserved);
}

pub fn validFact(input: *const FactInput, key_bytes: []const u8, mode: MatchMode) bool {
    const kind = aliasKind(input.kind) orelse return false;
    if (!kindAllowedForMode(kind, mode) or
        input.struct_size != @sizeOf(FactInput) or
        input.flags != 0 or
        input.reserved_u32 != 0 or
        !allZero(&input.reserved))
    {
        return false;
    }
    return switch (kind) {
        .steam_app_id => input.numeric_value != 0 and
            input.key_offset == 0 and input.key_length == 0,
        .package_identifier, .installed_name, .executable_name, .product_name => input.numeric_value == 0 and validCanonicalKey(key_bytes, input.key_offset, input.key_length),
        .launcher_token, .managed_child_segment => false,
    };
}

pub fn validKnownQuery(input: *const KnownQueryInput, config: *const Config, catalog_generation: u64) bool {
    const has_path = (input.valid_mask & KnownQueryValid.executable_path) != 0;
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(KnownQueryInput) and
        input.configuration_generation == config.generation and
        input.catalog_generation == catalog_generation and input.catalog_generation != 0 and
        input.query_epoch != 0 and input.signal_count <= config.maximum_query_signal_count and
        input.key_byte_count <= config.maximum_query_key_byte_count and
        input.flags == 0 and (input.valid_mask & ~KnownQueryValid.known) == 0 and
        (input.valid_mask & KnownQueryValid.required) == KnownQueryValid.required and
        (if (has_path) input.executable_path_length != 0 else input.executable_path_offset == 0 and input.executable_path_length == 0) and
        allZero(&input.reserved);
}

pub fn validKnownSignal(input: *const KnownSignalInput, key_bytes: []const u8) bool {
    return input.struct_size == @sizeOf(KnownSignalInput) and signalKind(input.kind) != null and
        input.flags == 0 and input.reserved_u32 == 0 and
        validNormalizedTextKey(key_bytes, input.key_offset, input.key_length) and
        allZero(&input.reserved);
}

pub fn emptyMatchOutput(output: *const MatchOutput) bool {
    return output.abi_version == abi_version and
        output.struct_size == @sizeOf(MatchOutput) and
        output.configuration_generation == 0 and
        output.catalog_generation == 0 and
        output.catalog_fingerprint_low == 0 and output.catalog_fingerprint_high == 0 and
        output.query_epoch == 0 and output.entry_handle == 0 and
        output.attribution_id_handle == 0 and output.display_name_handle == 0 and
        output.source == 0 and output.software_kind == 0 and
        output.status == 0 and output.confidence == 0 and
        output.evidence_mask == 0 and output.conflict_count == 0 and
        output.matched_fact_count == 0 and output.flags == 0 and
        allZero(&output.reserved);
}

pub fn emptyKnownMatchOutput(output: *const KnownMatchOutput) bool {
    return output.abi_version == abi_version and output.struct_size == @sizeOf(KnownMatchOutput) and
        output.configuration_generation == 0 and output.catalog_generation == 0 and
        output.catalog_fingerprint_low == 0 and output.catalog_fingerprint_high == 0 and
        output.query_epoch == 0 and output.entry_handle == 0 and
        output.attribution_id_handle == 0 and output.display_name_handle == 0 and
        output.source == 0 and output.software_kind == 0 and output.status == 0 and
        output.mode == 0 and output.score == 0 and output.conflict_count == 0 and
        output.matched_root_handle == 0 and output.derived_root_byte_length == 0 and
        output.reserved_u32 == 0 and output.derived_identity_fingerprint_low == 0 and
        output.derived_identity_fingerprint_high == 0 and output.evidence_mask == 0 and
        output.flags == 0 and allZero(&output.reserved);
}

pub fn emptySummary(output: *const CatalogSummary) bool {
    return output.abi_version == abi_version and output.struct_size == @sizeOf(CatalogSummary) and
        output.configuration_generation == 0 and output.catalog_generation == 0 and
        output.state_revision == 0 and output.last_operation_epoch == 0 and
        output.last_query_epoch == 0 and output.entry_count == 0 and output.alias_count == 0 and
        output.root_count == 0 and output.key_byte_count == 0 and
        output.prohibited_alias_count == 0 and output.launcher_token_count == 0 and
        output.managed_child_rule_count == 0 and output.flags == 0 and
        output.catalog_fingerprint_low == 0 and output.catalog_fingerprint_high == 0 and
        output.resident_byte_count == 0 and allZero(&output.reserved);
}

pub fn aliasKind(value: u32) ?AliasKind {
    return switch (value) {
        1 => .steam_app_id,
        2 => .package_identifier,
        3 => .installed_name,
        4 => .executable_name,
        5 => .product_name,
        6 => .launcher_token,
        7 => .managed_child_segment,
        else => null,
    };
}

pub fn matchMode(value: u32) ?MatchMode {
    return switch (value) {
        1 => .installed_software,
        2 => .process,
        3 => .portable_process,
        else => null,
    };
}

pub fn signalKind(value: u32) ?SignalKind {
    return switch (value) {
        1 => .process_name,
        2 => .product_name,
        3 => .file_description,
        4 => .window_application_user_model_id,
        5 => .application_user_model_id,
        6 => .window_title,
        else => null,
    };
}

pub fn kindAllowedForMode(kind: AliasKind, mode: MatchMode) bool {
    return switch (mode) {
        .installed_software => switch (kind) {
            .steam_app_id, .package_identifier, .installed_name => true,
            else => false,
        },
        .process, .portable_process => switch (kind) {
            .executable_name, .product_name => true,
            else => false,
        },
    };
}

pub fn evidenceFor(kind: AliasKind) u64 {
    return switch (kind) {
        .steam_app_id => Evidence.steam_app_id,
        .package_identifier => Evidence.package_identifier,
        .installed_name => Evidence.installed_name,
        .executable_name => Evidence.executable_name,
        .product_name => Evidence.product_name,
        .launcher_token, .managed_child_segment => 0,
    };
}

pub fn signalEvidenceFor(kind: SignalKind) u64 {
    return @as(u64, 1) << @intCast(@intFromEnum(kind) - 1);
}

pub fn keySlice(bytes: []const u8, offset: u32, length: u32) ?[]const u8 {
    const start: usize = offset;
    const len: usize = length;
    const end = std.math.add(usize, start, len) catch return null;
    if (end > bytes.len) return null;
    return bytes[start..end];
}

pub fn validCanonicalKey(bytes: []const u8, offset: u32, length: u32) bool {
    const key = keySlice(bytes, offset, length) orelse return false;
    if (key.len == 0 or !std.unicode.utf8ValidateSlice(key)) return false;
    if (key[0] == ' ' or key[key.len - 1] == ' ') return false;
    for (key) |value| {
        if (value == 0 or (value >= 'A' and value <= 'Z')) return false;
    }
    return true;
}

pub fn validNormalizedTextKey(bytes: []const u8, offset: u32, length: u32) bool {
    const key = keySlice(bytes, offset, length) orelse return false;
    if (key.len == 0 or !std.unicode.utf8ValidateSlice(key)) return false;
    for (key) |value| {
        if (value < 0x80 and !((value >= 'a' and value <= 'z') or (value >= '0' and value <= '9'))) {
            return false;
        }
    }
    return true;
}

pub fn validCanonicalPath(bytes: []const u8, offset: u32, length: u32) bool {
    const path = keySlice(bytes, offset, length) orelse return false;
    if (path.len == 0 or !std.unicode.utf8ValidateSlice(path)) return false;
    const drive_absolute = path.len >= 3 and path[0] >= 'a' and path[0] <= 'z' and
        path[1] == ':' and path[2] == '/';
    const unc_absolute = path.len >= 3 and path[0] == '/' and path[1] == '/' and path[2] != '/';
    if (!drive_absolute and !unc_absolute) return false;
    if (path[path.len - 1] == '/' and !(path.len == 3 and path[1] == ':')) return false;
    for (path, 0..) |value, index| {
        if (value == 0 or value == '\\' or (value >= 'A' and value <= 'Z')) return false;
        if (value == '/' and index > 1 and path[index - 1] == '/') return false;
    }
    return true;
}

pub fn allZero(values: anytype) bool {
    for (values.*) |value| if (value != 0) return false;
    return true;
}

comptime {
    if (@sizeOf(Config) != 224) @compileError("software identity Config size changed");
    if (@sizeOf(Capacity) != 80) @compileError("software identity Capacity size changed");
    if (@sizeOf(EntryInput) != 72) @compileError("software identity EntryInput size changed");
    if (@sizeOf(AliasInput) != 56) @compileError("software identity AliasInput size changed");
    if (@sizeOf(RootInput) != 56) @compileError("software identity RootInput size changed");
    if (@sizeOf(ReplaceInput) != 88) @compileError("software identity ReplaceInput size changed");
    if (@sizeOf(QueryInput) != 80) @compileError("software identity QueryInput size changed");
    if (@sizeOf(FactInput) != 48) @compileError("software identity FactInput size changed");
    if (@sizeOf(KnownQueryInput) != 88) @compileError("software identity KnownQueryInput size changed");
    if (@sizeOf(KnownSignalInput) != 40) @compileError("software identity KnownSignalInput size changed");
    if (@sizeOf(MatchOutput) != 128) @compileError("software identity MatchOutput size changed");
    if (@sizeOf(KnownMatchOutput) != 168) @compileError("software identity KnownMatchOutput size changed");
    if (@sizeOf(CatalogSummary) != 128) @compileError("software identity CatalogSummary size changed");
}
