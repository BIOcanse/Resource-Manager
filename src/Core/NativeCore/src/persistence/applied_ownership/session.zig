const std = @import("std");
const checksum = @import("checksum.zig");
const codec = @import("codec.zig");
const protocol = @import("protocol.zig");
const state = @import("state.zig");

pub const Session = state.Session;

pub fn promote(session: *Session, input: *const protocol.PromoteInput) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (input.abi_version != protocol.abi_version or
        input.struct_size != @sizeOf(protocol.PromoteInput) or !allZero(&input.reserved))
    {
        return .abi_mismatch;
    }
    if (input.expected_ledger_revision != session.ledger_revision) return .stale_revision;
    if (!protocol.validPrimary(&input.primary)) return .invalid_argument;
    const scope: protocol.Scope = @enumFromInt(input.primary.scope);
    if (!protocol.validOriginalBinding(&input.original_binding, scope) or
        !protocol.primaryMatchesBinding(&input.primary, &input.original_binding))
    {
        return .identity_mismatch;
    }
    if (!protocol.validPayload(&input.payload)) return .payload_mismatch;
    if (!protocol.validPromotedGrades(&input.current_grades, &input.original_binding, scope))
        return .grade_mismatch;
    if (input.promoted_at_utc_ms == 0) return .invalid_time;
    if (session.active.findPrimary(&input.primary) != null) return .duplicate_identity;
    if (session.active.findPayload(&input.original_binding, &input.payload) != null)
        return .duplicate_payload;
    if (session.active.count == session.config.record_capacity) return .capacity_full;
    const next_ledger_revision = incrementRevision(session.ledger_revision) orelse
        return .revision_exhausted;
    const record = protocol.Record{
        .primary = input.primary,
        .original_binding = input.original_binding,
        .payload = input.payload,
        .current_grades = input.current_grades,
        .record_revision = 1,
        .promoted_at_utc_ms = input.promoted_at_utc_ms,
        .updated_at_utc_ms = input.promoted_at_utc_ms,
        .flags = 0,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0, 0, 0 },
    };
    const validity = protocol.validRecord(&record);
    if (validity != .ok) return validity;
    if (!session.active.append(record)) return .capacity_full;
    session.ledger_revision = next_ledger_revision;
    return .ok;
}

pub fn planTransition(
    session: *Session,
    primary: *const protocol.PrimaryIdentity,
    transition_binding: *const protocol.OriginalBinding,
    transition_payload: *const protocol.DurablePayloadReference,
    output: *protocol.TransitionInput,
) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (!emptyStruct(protocol.TransitionInput, output) or !protocol.validPrimary(primary))
        return .invalid_argument;
    const record_index = session.active.findPrimary(primary) orelse return .no_data;
    const record = &session.active.records[record_index];
    if (!protocol.primaryMatchesBinding(&record.primary, transition_binding))
        return .identity_mismatch;
    if (!protocol.samePayload(&record.payload, transition_payload))
        return .payload_mismatch;
    const scope: protocol.Scope = @enumFromInt(record.primary.scope);
    var target = std.mem.zeroes(protocol.CurrentGrades);
    if (!protocol.projectTransition(
        &record.current_grades,
        transition_binding,
        scope,
        &target,
    )) return .grade_mismatch;
    output.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.TransitionInput),
        .expected_ledger_revision = session.ledger_revision,
        .cas = .{
            .primary = record.primary,
            .journal_instance_low = record.original_binding.journal_instance_low,
            .journal_instance_high = record.original_binding.journal_instance_high,
            .original_action_identity = record.original_binding.action_identity,
            .payload = record.payload,
            .expected_record_revision = record.record_revision,
            .expected_current_grades = record.current_grades,
            .reserved = .{0},
        },
        .transition_binding = transition_binding.*,
        .transition_payload = transition_payload.*,
        .new_current_grades = target,
        .updated_at_utc_ms = 0,
        .reserved = .{ 0, 0 },
    };
    return .ok;
}

pub fn transition(session: *Session, input: *const protocol.TransitionInput) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (input.abi_version != protocol.abi_version or
        input.struct_size != @sizeOf(protocol.TransitionInput) or !allZero(&input.reserved))
    {
        return .abi_mismatch;
    }
    if (input.expected_ledger_revision != session.ledger_revision) return .stale_revision;
    if (!protocol.validCas(&input.cas)) return .invalid_argument;
    const record_index = session.active.findPrimary(&input.cas.primary) orelse return .no_data;
    const record = &session.active.records[record_index];
    const cas_status = validateCas(record, &input.cas);
    if (cas_status != .ok) return cas_status;
    const scope: protocol.Scope = @enumFromInt(record.primary.scope);
    if (!protocol.primaryMatchesBinding(&record.primary, &input.transition_binding))
        return .identity_mismatch;
    if (!protocol.samePayload(&record.payload, &input.transition_payload))
        return .payload_mismatch;
    if (!protocol.validTransition(
        &record.current_grades,
        &input.transition_binding,
        &input.new_current_grades,
        scope,
    )) return .grade_mismatch;
    if (protocol.sameGrades(&record.current_grades, &input.new_current_grades))
        return .grade_mismatch;
    if (input.updated_at_utc_ms < record.updated_at_utc_ms) return .invalid_time;
    const next_ledger_revision = incrementRevision(session.ledger_revision) orelse
        return .revision_exhausted;
    if (input.new_current_grades.valid_mask == 0) {
        if (!session.active.removeAt(record_index)) return .corrupt_image;
        session.ledger_revision = next_ledger_revision;
        return .ok;
    }
    const next_record_revision = incrementRevision(record.record_revision) orelse
        return .revision_exhausted;
    record.current_grades = input.new_current_grades;
    record.record_revision = next_record_revision;
    record.updated_at_utc_ms = input.updated_at_utc_ms;
    session.ledger_revision = next_ledger_revision;
    return .ok;
}

pub fn remove(session: *Session, input: *const protocol.RemoveInput) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (input.abi_version != protocol.abi_version or
        input.struct_size != @sizeOf(protocol.RemoveInput) or !allZero(&input.reserved))
    {
        return .abi_mismatch;
    }
    if (input.expected_ledger_revision != session.ledger_revision) return .stale_revision;
    if (!protocol.validCas(&input.cas)) return .invalid_argument;
    const record_index = session.active.findPrimary(&input.cas.primary) orelse return .no_data;
    const record = &session.active.records[record_index];
    const cas_status = validateCas(record, &input.cas);
    if (cas_status != .ok) return cas_status;
    if (input.removed_at_utc_ms < record.updated_at_utc_ms) return .invalid_time;
    const next_ledger_revision = incrementRevision(session.ledger_revision) orelse
        return .revision_exhausted;
    if (!session.active.removeAt(record_index)) return .corrupt_image;
    session.ledger_revision = next_ledger_revision;
    return .ok;
}

pub fn get(
    session: *Session,
    primary: *const protocol.PrimaryIdentity,
    output: *protocol.Record,
) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (!protocol.validPrimary(primary) or !emptyStruct(protocol.Record, output))
        return .invalid_argument;
    const record_index = session.active.findPrimary(primary) orelse return .no_data;
    output.* = session.active.records[record_index];
    return .ok;
}

pub fn snapshot(
    session: *Session,
    header: *protocol.SnapshotHeader,
    records: []protocol.Record,
) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (!emptyStruct(protocol.SnapshotHeader, header)) return .abi_mismatch;
    if (records.len < session.active.count) return .buffer_too_small;
    for (records[0..session.active.count]) |*record| {
        if (!emptyStruct(protocol.Record, record)) return .abi_mismatch;
    }
    const sorted = session.prepareSortIndices();
    for (sorted, 0..) |record_index, output_index| {
        records[output_index] = session.active.records[record_index];
    }
    const capacity = session.capacity();
    header.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.SnapshotHeader),
        .ledger_revision = session.ledger_revision,
        .ledger_instance_low = session.config.ledger_instance_low,
        .ledger_instance_high = session.config.ledger_instance_high,
        .entry_count = session.active.count,
        .record_capacity = session.config.record_capacity,
        .primary_index_capacity = session.config.primary_index_capacity,
        .payload_index_capacity = session.config.payload_index_capacity,
        .maximum_image_bytes = session.config.maximum_image_bytes,
        .resident_bytes = capacity.resident_bytes,
        .reserved = .{ 0, 0, 0, 0 },
    };
    return .ok;
}

pub fn encode(session: *Session, output: []u8, written: *u64) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (written.* != 0) return .invalid_argument;
    const records_bytes = std.math.mul(u64, session.active.count, @sizeOf(protocol.Record)) catch
        return .out_of_memory;
    const image_length = std.math.add(u64, @sizeOf(protocol.ImageHeader), records_bytes) catch
        return .out_of_memory;
    if (image_length > session.config.maximum_image_bytes) return .configuration_mismatch;
    if (output.len < image_length) return .buffer_too_small;
    const image = output[0..@intCast(image_length)];
    @memset(image, 0);
    codec.encodeHeader(&session.config, session.ledger_revision, session.active.count, image_length, image);
    const sorted = session.prepareSortIndices();
    for (sorted, 0..) |record_index, output_index| {
        codec.encodeRecord(
            &session.active.records[record_index],
            image,
            @sizeOf(protocol.ImageHeader) + output_index * @sizeOf(protocol.Record),
        );
    }
    const crc = checksum.crc64EcmaWithZeroRange(image, codec.checksum_offset, codec.checksum_length);
    codec.writeU64(image, codec.checksum_offset, crc);
    written.* = image_length;
    return .ok;
}

pub fn decodeReplace(session: *Session, image: []const u8) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    return decodeReplaceUnlocked(session, image);
}

pub fn openExisting(
    config: *const protocol.OpenConfig,
    image: []const u8,
    output: *?*Session,
) protocol.Status {
    if (output.* != null) return .invalid_argument;
    if (!protocol.validOpenConfig(config)) return .abi_mismatch;
    var header = std.mem.zeroes(protocol.ImageHeader);
    const header_status = codec.decodeAndValidateHeader(image, &header);
    if (header_status != .ok) return header_status;
    if (header.record_capacity > config.maximum_record_capacity or
        header.primary_index_capacity > config.maximum_primary_index_capacity or
        header.payload_index_capacity > config.maximum_payload_index_capacity or
        header.maximum_image_bytes > config.maximum_image_bytes)
    {
        return .configuration_mismatch;
    }
    const create_config = protocol.CreateConfig{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CreateConfig),
        .ledger_instance_low = header.ledger_instance_low,
        .ledger_instance_high = header.ledger_instance_high,
        .record_capacity = header.record_capacity,
        .primary_index_capacity = header.primary_index_capacity,
        .payload_index_capacity = header.payload_index_capacity,
        .flags = 0,
        .maximum_resident_bytes = config.maximum_resident_bytes,
        .maximum_image_bytes = header.maximum_image_bytes,
        .reserved = .{ 0, 0, 0, 0, 0 },
    };
    const session = state.Session.create(&create_config) catch |err| return switch (err) {
        error.ResidentBudgetExceeded => .configuration_mismatch,
        error.OutOfMemory => .out_of_memory,
        else => .invalid_argument,
    };
    session.ledger_revision = 0;
    const decode_status = decodeReplaceUnlocked(session, image);
    if (decode_status != .ok) {
        session.destroy();
        return decode_status;
    }
    output.* = session;
    return .ok;
}

fn decodeReplaceUnlocked(session: *Session, image: []const u8) protocol.Status {
    var header = std.mem.zeroes(protocol.ImageHeader);
    const header_status = codec.decodeAndValidateHeader(image, &header);
    if (header_status != .ok) return header_status;
    if (header.ledger_instance_low != session.config.ledger_instance_low or
        header.ledger_instance_high != session.config.ledger_instance_high or
        header.record_capacity != session.config.record_capacity or
        header.primary_index_capacity != session.config.primary_index_capacity or
        header.payload_index_capacity != session.config.payload_index_capacity or
        header.maximum_image_bytes != session.config.maximum_image_bytes)
    {
        return .configuration_mismatch;
    }
    if (header.ledger_revision <= session.ledger_revision) return .stale_revision;

    session.staging.clear();
    var previous_primary: ?protocol.PrimaryIdentity = null;
    var record_index: u32 = 0;
    while (record_index < header.entry_count) : (record_index += 1) {
        const base = @sizeOf(protocol.ImageHeader) +
            @as(usize, record_index) * @sizeOf(protocol.Record);
        const record = codec.decodeRecord(image, base);
        const validity = protocol.validRecord(&record);
        if (validity != .ok) {
            session.staging.clear();
            return validity;
        }
        if (record.record_revision > header.ledger_revision) {
            session.staging.clear();
            return .stale_revision;
        }
        if (previous_primary) |previous| {
            if (!protocol.primaryLessThan(&previous, &record.primary)) {
                session.staging.clear();
                return .non_canonical_image;
            }
        }
        if (session.staging.findPrimary(&record.primary) != null) {
            session.staging.clear();
            return .duplicate_identity;
        }
        if (session.staging.findPayload(&record.original_binding, &record.payload) != null) {
            session.staging.clear();
            return .duplicate_payload;
        }
        if (!session.staging.append(record)) {
            session.staging.clear();
            return .capacity_full;
        }
        previous_primary = record.primary;
    }
    session.publishStaging(header.ledger_revision);
    return .ok;
}

fn validateCas(record: *const protocol.Record, cas: *const protocol.OwnershipCas) protocol.Status {
    if (record.record_revision != cas.expected_record_revision) return .stale_revision;
    if (!protocol.samePrimary(&record.primary, &cas.primary) or
        !protocol.bindingMatchesCas(&record.original_binding, cas))
    {
        return .identity_mismatch;
    }
    if (!protocol.payloadMatchesCas(record, cas)) return .payload_mismatch;
    if (!protocol.sameGrades(&record.current_grades, &cas.expected_current_grades))
        return .grade_mismatch;
    return .ok;
}

fn incrementRevision(value: u64) ?u64 {
    if (value == std.math.maxInt(u64)) return null;
    return value + 1;
}

fn allZero(values: []const u64) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

fn emptyStruct(comptime T: type, value: *const T) bool {
    for (std.mem.asBytes(value)) |byte| if (byte != 0) return false;
    return true;
}
