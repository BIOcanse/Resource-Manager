const std = @import("std");
const checksum = @import("checksum.zig");
const protocol = @import("protocol.zig");
const state = @import("state.zig");

pub const header_size: usize = @sizeOf(protocol.ImageHeader);
pub const record_size: usize = @sizeOf(protocol.Record);
pub const checksum_offset: usize = 64;
pub const checksum_length: usize = 8;

pub const ImageMetadata = struct {
    journal_instance_low: u64,
    journal_instance_high: u64,
    journal_revision: u64,
    entry_count: u32,
    capacity: u32,
    image_length: usize,
};

pub fn encodeUnlocked(session: *state.Session, output: []u8, written: *u64) protocol.Status {
    written.* = 0;
    const count: usize = session.count;
    const image_length = header_size + count * record_size;
    if (output.len < image_length) return .buffer_too_small;
    @memset(output[0..image_length], 0);

    writeU64(output, 0, protocol.image_magic);
    writeU32(output, 8, protocol.abi_version);
    writeU32(output, 12, header_size);
    writeU32(output, 16, record_size);
    writeU32(output, 20, 0);
    writeU64(output, 24, session.config.journal_instance_low);
    writeU64(output, 32, session.config.journal_instance_high);
    writeU64(output, 40, session.journal_revision);
    writeU32(output, 48, session.count);
    writeU32(output, 52, session.config.record_capacity);
    writeU64(output, 56, image_length);

    const indices = session.prepareSortIndices();
    for (indices, 0..) |record_index, canonical_index| {
        encodeRecord(
            &session.records[record_index],
            output,
            header_size + canonical_index * record_size,
        );
    }
    const crc = checksum.crc64EcmaWithZeroRange(
        output[0..image_length],
        checksum_offset,
        checksum_length,
    );
    writeU64(output, checksum_offset, crc);
    written.* = image_length;
    return .ok;
}

pub fn decodeUnlocked(session: *state.Session, image: []const u8) protocol.Status {
    var metadata: ImageMetadata = undefined;
    const inspection = inspectImage(image, &metadata);
    if (inspection != .ok) return inspection;
    if (metadata.capacity != session.config.record_capacity or
        metadata.journal_instance_low != session.config.journal_instance_low or
        metadata.journal_instance_high != session.config.journal_instance_high)
    {
        return .configuration_mismatch;
    }

    var index: usize = 0;
    while (index < metadata.entry_count) : (index += 1) {
        const record = decodeRecord(image, header_size + index * record_size);
        const validity = protocol.validRecord(&record);
        if (validity != .ok) return validity;
        if (index != 0) {
            const previous = &session.staging_records[index - 1].identity;
            if (protocol.sameIdentity(previous, &record.identity)) return .duplicate_identity;
            if (!protocol.identityLessThan(previous, &record.identity)) return .non_canonical_image;
        }
        session.staging_records[index] = record;
    }

    @memset(session.records, std.mem.zeroes(protocol.Record));
    std.mem.copyForwards(
        protocol.Record,
        session.records[0..metadata.entry_count],
        session.staging_records[0..metadata.entry_count],
    );
    session.count = metadata.entry_count;
    session.journal_revision = metadata.journal_revision;
    if (!session.rebuildIndex()) return .corrupt_image;
    return .ok;
}

pub fn inspectImage(image: []const u8, metadata: *ImageMetadata) protocol.Status {
    if (image.len < header_size) return .truncated_image;
    if (readU64(image, 0) != protocol.image_magic) return .corrupt_image;
    if (readU32(image, 8) != protocol.abi_version or
        readU32(image, 12) != header_size or
        readU32(image, 16) != record_size or
        readU32(image, 20) != 0)
    {
        return .abi_mismatch;
    }
    const instance_low = readU64(image, 24);
    const instance_high = readU64(image, 32);
    const journal_revision = readU64(image, 40);
    const entry_count = readU32(image, 48);
    const capacity = readU32(image, 52);
    const encoded_length_u64 = readU64(image, 56);
    if ((instance_low == 0 and instance_high == 0) or
        journal_revision == 0 or
        capacity == 0 or
        encoded_length_u64 > std.math.maxInt(usize)) return .corrupt_image;
    const encoded_length: usize = @intCast(encoded_length_u64);
    if (encoded_length > image.len) return .truncated_image;
    if (encoded_length != image.len) return .corrupt_image;
    if (entry_count > capacity) return .corrupt_image;
    const expected_length = header_size + @as(usize, entry_count) * record_size;
    if (encoded_length != expected_length) return .corrupt_image;
    var reserved_offset: usize = 72;
    while (reserved_offset < header_size) : (reserved_offset += 8) {
        if (readU64(image, reserved_offset) != 0) return .abi_mismatch;
    }
    const stored_crc = readU64(image, checksum_offset);
    const actual_crc = checksum.crc64EcmaWithZeroRange(image, checksum_offset, checksum_length);
    if (stored_crc != actual_crc) return .checksum_mismatch;

    metadata.* = .{
        .journal_instance_low = instance_low,
        .journal_instance_high = instance_high,
        .journal_revision = journal_revision,
        .entry_count = entry_count,
        .capacity = capacity,
        .image_length = encoded_length,
    };
    return .ok;
}

fn encodeRecord(record: *const protocol.Record, output: []u8, base: usize) void {
    encodeIdentity(&record.identity, output, base);
    writeU32(output, base + 64, record.scope);
    writeU32(output, base + 68, record.disposition);
    writeU32(output, base + 72, record.domain_mask);
    writeU32(output, base + 76, record.phase);
    writeU32(output, base + 80, record.grade_valid_mask);
    writeU32(output, base + 84, record.grade_reserved);
    writeU32(output, base + 88, record.stable_system_status);
    writeU32(output, base + 92, record.stable_system_error);
    writeI32(output, base + 96, record.process_from_grade);
    writeI32(output, base + 100, record.process_to_grade);
    writeI32(output, base + 104, record.cpu_from_grade);
    writeI32(output, base + 108, record.cpu_to_grade);
    writeI32(output, base + 112, record.gpu_from_grade);
    writeI32(output, base + 116, record.gpu_to_grade);
    writeU32(output, base + 120, record.payload_kind);
    writeU32(output, base + 124, record.payload_slot);
    writeU32(output, base + 128, record.payload_generation);
    writeU32(output, base + 132, record.payload_reserved);
    writeU64(output, base + 136, record.payload_length);
    writeU64(output, base + 144, record.payload_digest_low);
    writeU64(output, base + 152, record.payload_digest_high);
    writeU64(output, base + 160, record.prepared_at_utc_ms);
    writeU64(output, base + 168, record.updated_at_utc_ms);
    writeU64(output, base + 176, record.retry_not_before_utc_ms);
    writeU64(output, base + 184, record.entry_revision);
    writeU32(output, base + 192, record.feedback_valid_mask);
    writeU32(output, base + 196, record.feedback_flags);
    writeU32(output, base + 200, record.feedback_status);
    writeU32(output, base + 204, record.feedback_system_status);
    writeU32(output, base + 208, record.feedback_system_error);
    writeU32(output, base + 212, record.feedback_reserved);
    writeU64(output, base + 216, record.feedback_completed_at_utc_ms);
    writeI32(output, base + 224, record.actual_process_grade);
    writeI32(output, base + 228, record.actual_cpu_grade);
    writeI32(output, base + 232, record.actual_gpu_grade);
    writeU32(output, base + 236, record.actual_reserved);
    writeU64(output, base + 240, record.recovery_reason_mask);
    writeU32(output, base + 248, record.retry_attempt_count);
    writeU32(output, base + 252, record.maximum_recovery_attempts);
    writeU64(output, base + 256, record.recovery_deadline_utc_ms);
    writeU64(output, base + 264, record.atomic_group_id);
    writeU32(output, base + 272, record.group_member_index);
    writeU32(output, base + 276, record.group_member_count);
    writeU64(output, base + 280, record.payload_provenance_digest_low);
    writeU64(output, base + 288, record.payload_provenance_digest_high);
}

fn decodeRecord(image: []const u8, base: usize) protocol.Record {
    return .{
        .identity = decodeIdentity(image, base),
        .scope = readU32(image, base + 64),
        .disposition = readU32(image, base + 68),
        .domain_mask = readU32(image, base + 72),
        .phase = readU32(image, base + 76),
        .grade_valid_mask = readU32(image, base + 80),
        .grade_reserved = readU32(image, base + 84),
        .stable_system_status = readU32(image, base + 88),
        .stable_system_error = readU32(image, base + 92),
        .process_from_grade = readI32(image, base + 96),
        .process_to_grade = readI32(image, base + 100),
        .cpu_from_grade = readI32(image, base + 104),
        .cpu_to_grade = readI32(image, base + 108),
        .gpu_from_grade = readI32(image, base + 112),
        .gpu_to_grade = readI32(image, base + 116),
        .payload_kind = readU32(image, base + 120),
        .payload_slot = readU32(image, base + 124),
        .payload_generation = readU32(image, base + 128),
        .payload_reserved = readU32(image, base + 132),
        .payload_length = readU64(image, base + 136),
        .payload_digest_low = readU64(image, base + 144),
        .payload_digest_high = readU64(image, base + 152),
        .prepared_at_utc_ms = readU64(image, base + 160),
        .updated_at_utc_ms = readU64(image, base + 168),
        .retry_not_before_utc_ms = readU64(image, base + 176),
        .entry_revision = readU64(image, base + 184),
        .feedback_valid_mask = readU32(image, base + 192),
        .feedback_flags = readU32(image, base + 196),
        .feedback_status = readU32(image, base + 200),
        .feedback_system_status = readU32(image, base + 204),
        .feedback_system_error = readU32(image, base + 208),
        .feedback_reserved = readU32(image, base + 212),
        .feedback_completed_at_utc_ms = readU64(image, base + 216),
        .actual_process_grade = readI32(image, base + 224),
        .actual_cpu_grade = readI32(image, base + 228),
        .actual_gpu_grade = readI32(image, base + 232),
        .actual_reserved = readU32(image, base + 236),
        .recovery_reason_mask = readU64(image, base + 240),
        .retry_attempt_count = readU32(image, base + 248),
        .maximum_recovery_attempts = readU32(image, base + 252),
        .recovery_deadline_utc_ms = readU64(image, base + 256),
        .atomic_group_id = readU64(image, base + 264),
        .group_member_index = readU32(image, base + 272),
        .group_member_count = readU32(image, base + 276),
        .payload_provenance_digest_low = readU64(image, base + 280),
        .payload_provenance_digest_high = readU64(image, base + 288),
    };
}

fn encodeIdentity(identity: *const protocol.Identity, output: []u8, base: usize) void {
    writeU64(output, base, identity.configuration_generation);
    writeU64(output, base + 8, identity.plan_epoch);
    writeU64(output, base + 16, identity.action_id);
    writeU64(output, base + 24, identity.host_session_incarnation);
    writeU64(output, base + 32, identity.target_id);
    writeU64(output, base + 40, identity.software_id);
    writeU64(output, base + 48, identity.process_start_key);
    writeU32(output, base + 56, identity.process_id);
    writeU32(output, base + 60, identity.reserved);
}

fn decodeIdentity(image: []const u8, base: usize) protocol.Identity {
    return .{
        .configuration_generation = readU64(image, base),
        .plan_epoch = readU64(image, base + 8),
        .action_id = readU64(image, base + 16),
        .host_session_incarnation = readU64(image, base + 24),
        .target_id = readU64(image, base + 32),
        .software_id = readU64(image, base + 40),
        .process_start_key = readU64(image, base + 48),
        .process_id = readU32(image, base + 56),
        .reserved = readU32(image, base + 60),
    };
}

pub fn writeU32(output: []u8, offset: usize, value: u32) void {
    var index: usize = 0;
    while (index < 4) : (index += 1) {
        const shift: u5 = @intCast(index * 8);
        output[offset + index] = @truncate(value >> shift);
    }
}

pub fn writeI32(output: []u8, offset: usize, value: i32) void {
    writeU32(output, offset, @bitCast(value));
}

pub fn writeU64(output: []u8, offset: usize, value: u64) void {
    var index: usize = 0;
    while (index < 8) : (index += 1) {
        const shift: u6 = @intCast(index * 8);
        output[offset + index] = @truncate(value >> shift);
    }
}

pub fn readU32(input: []const u8, offset: usize) u32 {
    var value: u32 = 0;
    var index: usize = 0;
    while (index < 4) : (index += 1) value |= @as(u32, input[offset + index]) << @intCast(index * 8);
    return value;
}

pub fn readI32(input: []const u8, offset: usize) i32 {
    return @bitCast(readU32(input, offset));
}

pub fn readU64(input: []const u8, offset: usize) u64 {
    var value: u64 = 0;
    var index: usize = 0;
    while (index < 8) : (index += 1) value |= @as(u64, input[offset + index]) << @intCast(index * 8);
    return value;
}
