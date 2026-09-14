const std = @import("std");
const protocol = @import("protocol.zig");
const catalog = @import("catalog.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub fn submit(
    session: *state.Session,
    inputs: []const protocol.GpuInventoryInput,
) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (session.phase != .completion_open or
        inputs.len != session.completion.gpu_inventory_count)
    {
        return .unavailable;
    }
    const source_index = session.active.findSource(session.completion.source_handle) orelse
        return .invalid_argument;
    if (session.active.sources[source_index].definition.source_role !=
        @intFromEnum(protocol.SourceRole.gpu_inventory))
    {
        return .invalid_argument;
    }
    const source_capability_mask =
        session.active.sources[source_index].definition.capability_mask;
    clearIdentityScratch(session);
    var previous_adapter_handle: u64 = 0;
    for (inputs, 0..) |input, ordinal| {
        if (!validInput(session, &input) or
            !existingIdentityCompatible(session, &input) or
            input.adapter_handle <= previous_adapter_handle or
            (input.capability_mask & ~source_capability_mask) != 0 or
            !insertScratchIdentity(session, identityFromInput(&input), @intCast(ordinal)))
        {
            return .invalid_argument;
        }
        previous_adapter_handle = input.adapter_handle;
    }

    clearStaging(session);
    for (inputs, 0..) |input, ordinal| {
        const output = outputFromInput(&input);
        session.gpu_staging[ordinal] = .{
            .active = true,
            .output = output,
        };
        if (!insertIndex(
            session.gpu_staging_index,
            session.gpu_staging,
            input.adapter_handle,
            @intCast(ordinal),
            adapterHandle,
        ) or !insertLuidIndex(
            session.gpu_staging_luid_index,
            session.gpu_staging,
            input.adapter_luid_low,
            input.adapter_luid_high,
            @intCast(ordinal),
        ) or !insertIndex(
            session.gpu_staging_key_index,
            session.gpu_staging,
            input.stable_key_handle,
            @intCast(ordinal),
            stableKeyHandle,
        )) {
            unreachable;
        }
    }
    session.gpu_staging_count = @intCast(inputs.len);
    return .ok;
}

pub fn prepareFinalized(session: *state.Session, source_generation: u64) void {
    const status: protocol.SourceStatus = @enumFromInt(session.completion.status);
    const source_index = session.active.findSource(session.completion.source_handle).?;
    const retention = session.active.sources[source_index].definition.retention_policy;
    if (session.active.sources[source_index].definition.source_role !=
        @intFromEnum(protocol.SourceRole.gpu_inventory))
    {
        session.gpu_staging_prepared = false;
        return;
    }
    if (status == .complete) {
        if (!session.gpu_staging_prepared) clearStaging(session);
        for (session.gpu_staging[0..session.gpu_staging_count]) |*gpu| {
            gpu.output.source_generation = source_generation;
            gpu.output.status = @intFromEnum(protocol.InventoryStatus.current);
            gpu.output.semantic_fingerprint = fingerprint(&gpu.output);
        }
        return;
    }
    if (retention == @intFromEnum(protocol.RetentionPolicy.retain_last_good) and
        activeCount(session) != 0)
    {
        copyActiveToStaging(session);
        for (session.gpu_staging) |*gpu| if (gpu.active) {
            gpu.output.status = @intFromEnum(protocol.InventoryStatus.retained);
            gpu.output.semantic_fingerprint = fingerprint(&gpu.output);
        };
        return;
    }
    clearStaging(session);
}

pub fn activeCount(session: *const state.Session) u32 {
    var count: u32 = 0;
    for (session.gpu_active) |gpu| if (gpu.active) {
        count += 1;
    };
    return count;
}

pub fn stagedRetainedCount(session: *const state.Session) u32 {
    var count: u32 = 0;
    for (session.gpu_staging[0..session.gpu_staging_count]) |gpu| {
        if (gpu.active and gpu.output.status ==
            @intFromEnum(protocol.InventoryStatus.retained))
        {
            count += 1;
        }
    }
    return count;
}

pub fn stageOutputs(
    session: *state.Session,
    inputs: []const protocol.GpuInventoryOutput,
    source: *const protocol.SourcePersistenceOutput,
    definition: *const protocol.SourcePolicyInput,
    captured_at_milliseconds: u64,
) ResultCode {
    if (inputs.len > session.gpu_staging.len or
        definition.source_role != @intFromEnum(protocol.SourceRole.gpu_inventory) or
        source.source_handle != definition.source_handle or
        source.gpu_inventory_count != inputs.len or
        source.gpu_retained_count > source.gpu_inventory_count)
    {
        return .invalid_argument;
    }
    if (source.source_generation == 0) {
        if (inputs.len != 0 or source.gpu_inventory_count != 0 or
            source.gpu_retained_count != 0)
        {
            return .invalid_argument;
        }
        clearStaging(session);
        return .ok;
    }
    if (captured_at_milliseconds == 0) return .invalid_argument;
    const source_status: protocol.SourceStatus = @enumFromInt(source.status);
    const expected_inventory_status: protocol.InventoryStatus = switch (source_status) {
        .complete => .current,
        .partial, .unavailable, .unsupported, .skipped => .retained,
    };
    if (source_status == .complete) {
        if (source.gpu_retained_count != 0) return .invalid_argument;
    } else if (source.gpu_retained_count != inputs.len) {
        return .invalid_argument;
    }
    clearIdentityScratch(session);
    var previous_adapter_handle: u64 = 0;
    for (inputs, 0..) |input, ordinal| {
        if (!validOutput(
            &input,
            definition.source_handle,
            source.source_generation,
            captured_at_milliseconds,
            definition.capability_mask,
        ) or input.adapter_handle <= previous_adapter_handle or
            !existingOutputCompatible(session, &input) or
            !insertScratchIdentity(session, identityFromOutput(&input), @intCast(ordinal)) or
            input.status != @intFromEnum(expected_inventory_status) or
            (expected_inventory_status == .current and
                (input.source_generation != source.source_generation or
                    input.observed_at_milliseconds >
                        source.last_current_at_milliseconds)))
        {
            return .invalid_argument;
        }
        previous_adapter_handle = input.adapter_handle;
    }
    clearStaging(session);
    for (inputs, 0..) |input, ordinal| {
        session.gpu_staging[ordinal] = .{ .active = true, .output = input };
        if (!insertIndex(
            session.gpu_staging_index,
            session.gpu_staging,
            input.adapter_handle,
            @intCast(ordinal),
            adapterHandle,
        ) or !insertLuidIndex(
            session.gpu_staging_luid_index,
            session.gpu_staging,
            input.adapter_luid_low,
            input.adapter_luid_high,
            @intCast(ordinal),
        ) or !insertIndex(
            session.gpu_staging_key_index,
            session.gpu_staging,
            input.stable_key_handle,
            @intCast(ordinal),
            stableKeyHandle,
        )) {
            unreachable;
        }
    }
    session.gpu_staging_count = @intCast(inputs.len);
    return .ok;
}

pub fn commitStaged(session: *state.Session) void {
    if (!session.gpu_staging_prepared) return;
    swapGpuState(session);
    clearTrailing(session.gpu_active, session.gpu_staging_count);
    session.gpu_staging_prepared = false;
    session.gpu_staging_count = 0;
}

pub fn finalizedRows(session: *const state.Session) []const state.GpuRecord {
    return if (session.gpu_staging_prepared)
        session.gpu_staging
    else
        session.gpu_active;
}

pub fn prepareCatalogReplacement(
    session: *state.Session,
    preserve_existing: bool,
) void {
    if (preserve_existing) {
        copyActiveToStaging(session);
        return;
    }
    clearStaging(session);
}

pub fn synchronizeStagingWithActive(session: *state.Session) void {
    copyActiveToStaging(session);
}

fn validInput(session: *const state.Session, input: *const protocol.GpuInventoryInput) bool {
    return input.struct_size == @sizeOf(protocol.GpuInventoryInput) and
        input.flags == 0 and input.adapter_handle != 0 and input.source_handle != 0 and
        input.source_handle == session.completion.source_handle and
        input.source_observation_sequence ==
            session.completion.source_observation_sequence and
        input.observed_at_milliseconds != 0 and
        input.observed_at_milliseconds <= session.completion.captured_at_milliseconds and
        session.futureTimeValid(
            input.observed_at_milliseconds,
            session.completion.command_at_milliseconds,
        ) and
        (input.adapter_luid_low != 0 or input.adapter_luid_high != 0) and
        input.stable_key_handle != 0 and
        input.valid_mask == protocol.InventoryValid.required and
        input.status == @intFromEnum(protocol.InventoryStatus.current) and
        input.reserved_u32 == 0 and allZero(input.reserved);
}

fn outputFromInput(input: *const protocol.GpuInventoryInput) protocol.GpuInventoryOutput {
    var output = protocol.GpuInventoryOutput{
        .struct_size = @sizeOf(protocol.GpuInventoryOutput),
        .flags = input.flags,
        .adapter_handle = input.adapter_handle,
        .source_handle = input.source_handle,
        .source_generation = 0,
        .observed_at_milliseconds = input.observed_at_milliseconds,
        .adapter_luid_low = input.adapter_luid_low,
        .adapter_luid_high = input.adapter_luid_high,
        .stable_key_handle = input.stable_key_handle,
        .capability_mask = input.capability_mask,
        .topology_fingerprint = input.topology_fingerprint,
        .valid_mask = input.valid_mask,
        .status = input.status,
        .reserved_u32 = 0,
        .semantic_fingerprint = 0,
        .reserved = .{ 0, 0 },
    };
    output.semantic_fingerprint = fingerprint(&output);
    return output;
}

fn validOutput(
    input: *const protocol.GpuInventoryOutput,
    expected_source_handle: u64,
    maximum_source_generation: u64,
    maximum_observed_at_milliseconds: u64,
    source_capability_mask: u64,
) bool {
    if (input.struct_size != @sizeOf(protocol.GpuInventoryOutput) or
        input.flags != 0 or input.adapter_handle == 0 or
        input.source_handle != expected_source_handle or input.source_generation == 0 or
        input.source_generation > maximum_source_generation or
        input.observed_at_milliseconds == 0 or
        input.observed_at_milliseconds > maximum_observed_at_milliseconds or
        (input.adapter_luid_low == 0 and input.adapter_luid_high == 0) or
        input.stable_key_handle == 0 or
        (input.capability_mask & ~source_capability_mask) != 0 or
        input.valid_mask != protocol.InventoryValid.required or
        input.reserved_u32 != 0 or !allZero(input.reserved) or
        input.semantic_fingerprint == 0 or
        !protocol.knownEnum(protocol.InventoryStatus, input.status))
    {
        return false;
    }
    const status: protocol.InventoryStatus = @enumFromInt(input.status);
    if (status != .current and status != .retained) return false;
    return fingerprint(input) == input.semantic_fingerprint;
}

fn swapGpuState(session: *state.Session) void {
    std.mem.swap([]state.GpuRecord, &session.gpu_active, &session.gpu_staging);
    std.mem.swap([]u32, &session.gpu_index, &session.gpu_staging_index);
    std.mem.swap([]u32, &session.gpu_luid_index, &session.gpu_staging_luid_index);
    std.mem.swap([]u32, &session.gpu_key_index, &session.gpu_staging_key_index);
}

fn copyActiveToStaging(session: *state.Session) void {
    @memcpy(session.gpu_staging, session.gpu_active);
    @memcpy(session.gpu_staging_index, session.gpu_index);
    @memcpy(session.gpu_staging_luid_index, session.gpu_luid_index);
    @memcpy(session.gpu_staging_key_index, session.gpu_key_index);
    session.gpu_staging_count = activeCount(session);
    session.gpu_staging_prepared = true;
}

fn clearTrailing(records: []state.GpuRecord, active_count: u32) void {
    if (active_count >= records.len) return;
    @memset(records[active_count..], .{});
}

fn clearStaging(session: *state.Session) void {
    session.gpu_staging_count = 0;
    @memset(session.gpu_staging, .{});
    @memset(session.gpu_staging_index, catalog.no_index);
    @memset(session.gpu_staging_luid_index, catalog.no_index);
    @memset(session.gpu_staging_key_index, catalog.no_index);
    session.gpu_staging_prepared = true;
}

fn clearIdentityScratch(session: *state.Session) void {
    @memset(session.gpu_identity_index, catalog.no_index);
    @memset(session.gpu_identity_luid_index, catalog.no_index);
    @memset(session.gpu_identity_key_index, catalog.no_index);
}

fn identityFromInput(input: *const protocol.GpuInventoryInput) state.GpuIdentity {
    return .{
        .adapter_handle = input.adapter_handle,
        .adapter_luid_low = input.adapter_luid_low,
        .adapter_luid_high = input.adapter_luid_high,
        .stable_key_handle = input.stable_key_handle,
    };
}

fn identityFromOutput(input: *const protocol.GpuInventoryOutput) state.GpuIdentity {
    return .{
        .adapter_handle = input.adapter_handle,
        .adapter_luid_low = input.adapter_luid_low,
        .adapter_luid_high = input.adapter_luid_high,
        .stable_key_handle = input.stable_key_handle,
    };
}

fn insertScratchIdentity(
    session: *state.Session,
    identity: state.GpuIdentity,
    ordinal: u32,
) bool {
    if (ordinal >= session.gpu_identity_scratch.len) return false;
    session.gpu_identity_scratch[ordinal] = identity;
    return insertIdentityIndex(
        session.gpu_identity_index,
        session.gpu_identity_scratch,
        identity.adapter_handle,
        ordinal,
        identityAdapterHandle,
    ) and insertIdentityLuidIndex(
        session.gpu_identity_luid_index,
        session.gpu_identity_scratch,
        identity.adapter_luid_low,
        identity.adapter_luid_high,
        ordinal,
    ) and insertIdentityIndex(
        session.gpu_identity_key_index,
        session.gpu_identity_scratch,
        identity.stable_key_handle,
        ordinal,
        identityStableKeyHandle,
    );
}

fn identityAdapterHandle(identity: *const state.GpuIdentity) u64 {
    return identity.adapter_handle;
}

fn identityStableKeyHandle(identity: *const state.GpuIdentity) u64 {
    return identity.stable_key_handle;
}

fn insertIdentityIndex(
    index: []u32,
    identities: []const state.GpuIdentity,
    key: u64,
    ordinal: u32,
    comptime keyAt: fn (*const state.GpuIdentity) u64,
) bool {
    if (key == 0 or index.len == 0 or ordinal >= identities.len) return false;
    const mask = index.len - 1;
    var position = hashKey(key) & mask;
    var visited: usize = 0;
    while (visited < index.len) : (visited += 1) {
        const existing = index[position];
        if (existing == catalog.no_index) {
            index[position] = ordinal;
            return true;
        }
        if (existing < identities.len and keyAt(&identities[existing]) == key) return false;
        position = (position + 1) & mask;
    }
    return false;
}

fn insertIdentityLuidIndex(
    index: []u32,
    identities: []const state.GpuIdentity,
    low: u64,
    high: u64,
    ordinal: u32,
) bool {
    if ((low == 0 and high == 0) or index.len == 0 or ordinal >= identities.len) {
        return false;
    }
    const mask = index.len - 1;
    var position = hashLuid(low, high) & mask;
    var visited: usize = 0;
    while (visited < index.len) : (visited += 1) {
        const existing = index[position];
        if (existing == catalog.no_index) {
            index[position] = ordinal;
            return true;
        }
        if (existing < identities.len) {
            const identity = identities[existing];
            if (identity.adapter_luid_low == low and identity.adapter_luid_high == high) {
                return false;
            }
        }
        position = (position + 1) & mask;
    }
    return false;
}

fn insertIndex(
    index: []u32,
    records: []const state.GpuRecord,
    key: u64,
    record_index: u32,
    comptime keyAt: fn (*const state.GpuRecord) u64,
) bool {
    if (key == 0 or index.len == 0 or record_index >= records.len) return false;
    const mask = index.len - 1;
    var position = hashKey(key) & mask;
    var visited: usize = 0;
    while (visited < index.len) : (visited += 1) {
        const existing = index[position];
        if (existing == catalog.no_index) {
            index[position] = record_index;
            return true;
        }
        if (existing < records.len and keyAt(&records[existing]) == key) return false;
        position = (position + 1) & mask;
    }
    return false;
}

fn adapterHandle(record: *const state.GpuRecord) u64 {
    return record.output.adapter_handle;
}

fn stableKeyHandle(record: *const state.GpuRecord) u64 {
    return record.output.stable_key_handle;
}

fn hashLuid(low: u64, high: u64) usize {
    const values = [_]u64{ low, high };
    return @intCast(std.hash.Wyhash.hash(
        0x726d_6770_756c_7569,
        std.mem.asBytes(&values),
    ));
}

fn hashKey(key: u64) usize {
    return @intCast(std.hash.Wyhash.hash(
        0x726d_6770_7569_6e76,
        std.mem.asBytes(&key),
    ));
}

fn insertLuidIndex(
    index: []u32,
    records: []const state.GpuRecord,
    low: u64,
    high: u64,
    record_index: u32,
) bool {
    if ((low == 0 and high == 0) or index.len == 0 or record_index >= records.len) {
        return false;
    }
    const mask = index.len - 1;
    var position = hashLuid(low, high) & mask;
    var visited: usize = 0;
    while (visited < index.len) : (visited += 1) {
        const existing = index[position];
        if (existing == catalog.no_index) {
            index[position] = record_index;
            return true;
        }
        if (existing < records.len) {
            const output = records[existing].output;
            if (output.adapter_luid_low == low and output.adapter_luid_high == high) {
                return false;
            }
        }
        position = (position + 1) & mask;
    }
    return false;
}

fn existingIdentityCompatible(
    session: *const state.Session,
    input: *const protocol.GpuInventoryInput,
) bool {
    if (findIndexed(
        session.gpu_index,
        session.gpu_active,
        input.adapter_handle,
        adapterHandle,
    )) |record_index| {
        const active = session.gpu_active[record_index].output;
        if (!sameIdentity(&active, input) or
            input.observed_at_milliseconds < active.observed_at_milliseconds)
        {
            return false;
        }
    }
    if (findLuidIndexed(
        session.gpu_luid_index,
        session.gpu_active,
        input.adapter_luid_low,
        input.adapter_luid_high,
    )) |record_index| {
        if (!sameIdentity(&session.gpu_active[record_index].output, input)) return false;
    }
    if (findIndexed(
        session.gpu_key_index,
        session.gpu_active,
        input.stable_key_handle,
        stableKeyHandle,
    )) |record_index| {
        if (!sameIdentity(&session.gpu_active[record_index].output, input)) return false;
    }
    return true;
}

fn existingOutputCompatible(
    session: *const state.Session,
    input: *const protocol.GpuInventoryOutput,
) bool {
    const record_index = findIndexed(
        session.gpu_index,
        session.gpu_active,
        input.adapter_handle,
        adapterHandle,
    ) orelse {
        if (findLuidIndexed(
            session.gpu_luid_index,
            session.gpu_active,
            input.adapter_luid_low,
            input.adapter_luid_high,
        ) != null or
            findIndexed(
                session.gpu_key_index,
                session.gpu_active,
                input.stable_key_handle,
                stableKeyHandle,
            ) != null)
        {
            return false;
        }
        return true;
    };
    const active = session.gpu_active[record_index].output;
    if (active.adapter_luid_low != input.adapter_luid_low or
        active.adapter_luid_high != input.adapter_luid_high or
        active.stable_key_handle != input.stable_key_handle or
        input.source_generation < active.source_generation or
        input.observed_at_milliseconds < active.observed_at_milliseconds)
    {
        return false;
    }
    if (input.source_generation != active.source_generation or
        input.observed_at_milliseconds != active.observed_at_milliseconds)
    {
        return true;
    }
    return input.semantic_fingerprint == active.semantic_fingerprint or
        input.status == @intFromEnum(protocol.InventoryStatus.retained) and
            samePayloadExceptStatus(&active, input);
}

fn samePayloadExceptStatus(
    active: *const protocol.GpuInventoryOutput,
    input: *const protocol.GpuInventoryOutput,
) bool {
    return active.struct_size == input.struct_size and
        active.flags == input.flags and
        active.adapter_handle == input.adapter_handle and
        active.source_handle == input.source_handle and
        active.source_generation == input.source_generation and
        active.observed_at_milliseconds == input.observed_at_milliseconds and
        active.adapter_luid_low == input.adapter_luid_low and
        active.adapter_luid_high == input.adapter_luid_high and
        active.stable_key_handle == input.stable_key_handle and
        active.capability_mask == input.capability_mask and
        active.topology_fingerprint == input.topology_fingerprint and
        active.valid_mask == input.valid_mask and
        active.reserved_u32 == input.reserved_u32 and
        std.mem.eql(u64, &active.reserved, &input.reserved);
}

fn findIndexed(
    index: []const u32,
    records: []const state.GpuRecord,
    key: u64,
    comptime keyAt: fn (*const state.GpuRecord) u64,
) ?u32 {
    if (key == 0 or index.len == 0) return null;
    const mask = index.len - 1;
    var position = hashKey(key) & mask;
    var visited: usize = 0;
    while (visited < index.len) : (visited += 1) {
        const existing = index[position];
        if (existing == catalog.no_index) return null;
        if (existing < records.len and keyAt(&records[existing]) == key) return existing;
        position = (position + 1) & mask;
    }
    return null;
}

fn findLuidIndexed(
    index: []const u32,
    records: []const state.GpuRecord,
    low: u64,
    high: u64,
) ?u32 {
    if ((low == 0 and high == 0) or index.len == 0) return null;
    const mask = index.len - 1;
    var position = hashLuid(low, high) & mask;
    var visited: usize = 0;
    while (visited < index.len) : (visited += 1) {
        const existing = index[position];
        if (existing == catalog.no_index) return null;
        if (existing < records.len) {
            const output = records[existing].output;
            if (output.adapter_luid_low == low and output.adapter_luid_high == high) {
                return existing;
            }
        }
        position = (position + 1) & mask;
    }
    return null;
}

fn sameIdentity(
    output: *const protocol.GpuInventoryOutput,
    input: *const protocol.GpuInventoryInput,
) bool {
    return output.adapter_handle == input.adapter_handle and
        output.adapter_luid_low == input.adapter_luid_low and
        output.adapter_luid_high == input.adapter_luid_high and
        output.stable_key_handle == input.stable_key_handle;
}

fn fingerprint(output: *const protocol.GpuInventoryOutput) u64 {
    const semantic_fields = [_]u64{
        output.flags,
        output.adapter_handle,
        output.source_handle,
        output.adapter_luid_low,
        output.adapter_luid_high,
        output.stable_key_handle,
        output.capability_mask,
        output.topology_fingerprint,
        output.valid_mask,
        output.status,
    };
    const value = std.hash.Wyhash.hash(
        0x726d_6770_756f_7574,
        std.mem.asBytes(&semantic_fields),
    );
    return if (value == 0) 1 else value;
}

fn allZero(values: anytype) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
