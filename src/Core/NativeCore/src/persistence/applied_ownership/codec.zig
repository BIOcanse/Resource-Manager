const std = @import("std");
const checksum = @import("checksum.zig");
const protocol = @import("protocol.zig");

pub const checksum_offset: usize = 64;
pub const checksum_length: usize = 8;

pub fn encodeHeader(
    config: *const protocol.CreateConfig,
    ledger_revision: u64,
    entry_count: u32,
    image_length: u64,
    output: []u8,
) void {
    @memset(output[0..@sizeOf(protocol.ImageHeader)], 0);
    writeU64(output, 0, protocol.image_magic);
    writeU32(output, 8, protocol.abi_version);
    writeU32(output, 12, @sizeOf(protocol.ImageHeader));
    writeU32(output, 16, @sizeOf(protocol.Record));
    writeU32(output, 20, 0);
    writeU64(output, 24, config.ledger_instance_low);
    writeU64(output, 32, config.ledger_instance_high);
    writeU64(output, 40, ledger_revision);
    writeU32(output, 48, entry_count);
    writeU32(output, 52, config.record_capacity);
    writeU64(output, 56, image_length);
    writeU64(output, checksum_offset, 0);
    writeU32(output, 72, config.primary_index_capacity);
    writeU32(output, 76, config.payload_index_capacity);
    writeU64(output, 80, config.maximum_image_bytes);
}

pub fn decodeAndValidateHeader(image: []const u8, header: *protocol.ImageHeader) protocol.Status {
    if (image.len < @sizeOf(protocol.ImageHeader)) return .truncated_image;
    header.* = .{
        .magic = readU64(image, 0),
        .abi_version = readU32(image, 8),
        .header_size = readU32(image, 12),
        .record_size = readU32(image, 16),
        .flags = readU32(image, 20),
        .ledger_instance_low = readU64(image, 24),
        .ledger_instance_high = readU64(image, 32),
        .ledger_revision = readU64(image, 40),
        .entry_count = readU32(image, 48),
        .record_capacity = readU32(image, 52),
        .image_length = readU64(image, 56),
        .crc64_ecma = readU64(image, 64),
        .primary_index_capacity = readU32(image, 72),
        .payload_index_capacity = readU32(image, 76),
        .maximum_image_bytes = readU64(image, 80),
        .reserved = .{
            readU64(image, 88),
            readU64(image, 96),
            readU64(image, 104),
            readU64(image, 112),
            readU64(image, 120),
        },
    };
    if (header.magic != protocol.image_magic) return .corrupt_image;
    if (header.abi_version != protocol.abi_version or
        header.header_size != @sizeOf(protocol.ImageHeader) or
        header.record_size != @sizeOf(protocol.Record) or header.flags != 0 or
        !allZero(&header.reserved))
    {
        return .abi_mismatch;
    }
    if ((header.ledger_instance_low == 0 and header.ledger_instance_high == 0) or
        header.ledger_revision == 0 or header.record_capacity == 0 or
        header.entry_count > header.record_capacity or
        header.primary_index_capacity < header.record_capacity or
        !std.math.isPowerOfTwo(header.primary_index_capacity) or
        header.payload_index_capacity < header.record_capacity or
        !std.math.isPowerOfTwo(header.payload_index_capacity))
    {
        return .corrupt_image;
    }
    const records_length = std.math.mul(u64, header.entry_count, @sizeOf(protocol.Record)) catch
        return .corrupt_image;
    const expected_length = std.math.add(u64, @sizeOf(protocol.ImageHeader), records_length) catch
        return .corrupt_image;
    if (header.image_length != expected_length or header.image_length > header.maximum_image_bytes)
        return .corrupt_image;
    if (header.image_length != image.len) return .truncated_image;
    const actual_crc = checksum.crc64EcmaWithZeroRange(image, checksum_offset, checksum_length);
    if (actual_crc != header.crc64_ecma) return .checksum_mismatch;
    return .ok;
}

pub fn encodeRecord(record: *const protocol.Record, output: []u8, base: usize) void {
    encodePrimary(&record.primary, output, base);
    encodeBinding(&record.original_binding, output, base + 40);
    encodePayload(&record.payload, output, base + 200);
    encodeGrades(&record.current_grades, output, base + 232);
    writeU64(output, base + 256, record.record_revision);
    writeU64(output, base + 264, record.promoted_at_utc_ms);
    writeU64(output, base + 272, record.updated_at_utc_ms);
    writeU32(output, base + 280, record.flags);
    writeU32(output, base + 284, record.reserved_u32);
    var reserved_index: usize = 0;
    while (reserved_index < record.reserved.len) : (reserved_index += 1) {
        writeU64(output, base + 288 + reserved_index * 8, record.reserved[reserved_index]);
    }
}

pub fn decodeRecord(input: []const u8, base: usize) protocol.Record {
    return .{
        .primary = decodePrimary(input, base),
        .original_binding = decodeBinding(input, base + 40),
        .payload = decodePayload(input, base + 200),
        .current_grades = decodeGrades(input, base + 232),
        .record_revision = readU64(input, base + 256),
        .promoted_at_utc_ms = readU64(input, base + 264),
        .updated_at_utc_ms = readU64(input, base + 272),
        .flags = readU32(input, base + 280),
        .reserved_u32 = readU32(input, base + 284),
        .reserved = .{
            readU64(input, base + 288),
            readU64(input, base + 296),
            readU64(input, base + 304),
            readU64(input, base + 312),
        },
    };
}

fn encodePrimary(value: *const protocol.PrimaryIdentity, output: []u8, base: usize) void {
    writeU32(output, base, value.scope);
    writeU32(output, base + 4, value.reserved_u32);
    writeU64(output, base + 8, value.target_id);
    writeU64(output, base + 16, value.software_id);
    writeU64(output, base + 24, value.process_start_key);
    writeU32(output, base + 32, value.process_id);
    writeU32(output, base + 36, value.process_reserved);
}

fn decodePrimary(input: []const u8, base: usize) protocol.PrimaryIdentity {
    return .{
        .scope = readU32(input, base),
        .reserved_u32 = readU32(input, base + 4),
        .target_id = readU64(input, base + 8),
        .software_id = readU64(input, base + 16),
        .process_start_key = readU64(input, base + 24),
        .process_id = readU32(input, base + 32),
        .process_reserved = readU32(input, base + 36),
    };
}

fn encodeActionIdentity(value: *const protocol.ActionIdentity, output: []u8, base: usize) void {
    writeU64(output, base, value.configuration_generation);
    writeU64(output, base + 8, value.plan_epoch);
    writeU64(output, base + 16, value.action_id);
    writeU64(output, base + 24, value.host_session_incarnation);
    writeU64(output, base + 32, value.target_id);
    writeU64(output, base + 40, value.software_id);
    writeU64(output, base + 48, value.process_start_key);
    writeU32(output, base + 56, value.process_id);
    writeU32(output, base + 60, value.reserved);
}

fn decodeActionIdentity(input: []const u8, base: usize) protocol.ActionIdentity {
    return .{
        .configuration_generation = readU64(input, base),
        .plan_epoch = readU64(input, base + 8),
        .action_id = readU64(input, base + 16),
        .host_session_incarnation = readU64(input, base + 24),
        .target_id = readU64(input, base + 32),
        .software_id = readU64(input, base + 40),
        .process_start_key = readU64(input, base + 48),
        .process_id = readU32(input, base + 56),
        .reserved = readU32(input, base + 60),
    };
}

fn encodeBinding(value: *const protocol.OriginalBinding, output: []u8, base: usize) void {
    writeU64(output, base, value.journal_instance_low);
    writeU64(output, base + 8, value.journal_instance_high);
    encodeActionIdentity(&value.action_identity, output, base + 16);
    writeU32(output, base + 80, value.scope);
    writeU32(output, base + 84, value.disposition);
    writeU32(output, base + 88, value.domain_mask);
    writeU32(output, base + 92, value.grade_valid_mask);
    writeI32(output, base + 96, value.process_from_grade);
    writeI32(output, base + 100, value.process_to_grade);
    writeI32(output, base + 104, value.cpu_from_grade);
    writeI32(output, base + 108, value.cpu_to_grade);
    writeI32(output, base + 112, value.gpu_from_grade);
    writeI32(output, base + 116, value.gpu_to_grade);
    writeU32(output, base + 120, value.stable_system_status);
    writeU32(output, base + 124, value.stable_system_error);
    writeU32(output, base + 128, value.maximum_recovery_attempts);
    writeU32(output, base + 132, value.recovery_reserved);
    writeU64(output, base + 136, value.recovery_deadline_utc_ms);
    writeU64(output, base + 144, value.atomic_group_id);
    writeU32(output, base + 152, value.group_member_index);
    writeU32(output, base + 156, value.group_member_count);
}

fn decodeBinding(input: []const u8, base: usize) protocol.OriginalBinding {
    return .{
        .journal_instance_low = readU64(input, base),
        .journal_instance_high = readU64(input, base + 8),
        .action_identity = decodeActionIdentity(input, base + 16),
        .scope = readU32(input, base + 80),
        .disposition = readU32(input, base + 84),
        .domain_mask = readU32(input, base + 88),
        .grade_valid_mask = readU32(input, base + 92),
        .process_from_grade = readI32(input, base + 96),
        .process_to_grade = readI32(input, base + 100),
        .cpu_from_grade = readI32(input, base + 104),
        .cpu_to_grade = readI32(input, base + 108),
        .gpu_from_grade = readI32(input, base + 112),
        .gpu_to_grade = readI32(input, base + 116),
        .stable_system_status = readU32(input, base + 120),
        .stable_system_error = readU32(input, base + 124),
        .maximum_recovery_attempts = readU32(input, base + 128),
        .recovery_reserved = readU32(input, base + 132),
        .recovery_deadline_utc_ms = readU64(input, base + 136),
        .atomic_group_id = readU64(input, base + 144),
        .group_member_index = readU32(input, base + 152),
        .group_member_count = readU32(input, base + 156),
    };
}

fn encodePayload(value: *const protocol.DurablePayloadReference, output: []u8, base: usize) void {
    writeU32(output, base, value.slot);
    writeU32(output, base + 4, value.generation);
    writeU64(output, base + 8, value.length);
    writeU64(output, base + 16, value.digest_low);
    writeU64(output, base + 24, value.digest_high);
}

fn decodePayload(input: []const u8, base: usize) protocol.DurablePayloadReference {
    return .{
        .slot = readU32(input, base),
        .generation = readU32(input, base + 4),
        .length = readU64(input, base + 8),
        .digest_low = readU64(input, base + 16),
        .digest_high = readU64(input, base + 24),
    };
}

fn encodeGrades(value: *const protocol.CurrentGrades, output: []u8, base: usize) void {
    writeU32(output, base, value.valid_mask);
    writeU32(output, base + 4, value.reserved_u32);
    writeI32(output, base + 8, value.process_grade);
    writeI32(output, base + 12, value.cpu_grade);
    writeI32(output, base + 16, value.gpu_grade);
    writeI32(output, base + 20, value.reserved_i32);
}

fn decodeGrades(input: []const u8, base: usize) protocol.CurrentGrades {
    return .{
        .valid_mask = readU32(input, base),
        .reserved_u32 = readU32(input, base + 4),
        .process_grade = readI32(input, base + 8),
        .cpu_grade = readI32(input, base + 12),
        .gpu_grade = readI32(input, base + 16),
        .reserved_i32 = readI32(input, base + 20),
    };
}

pub fn writeU32(output: []u8, offset: usize, value: u32) void {
    var index: usize = 0;
    while (index < 4) : (index += 1) {
        output[offset + index] = @truncate(value >> @intCast(index * 8));
    }
}

pub fn writeI32(output: []u8, offset: usize, value: i32) void {
    writeU32(output, offset, @bitCast(value));
}

pub fn writeU64(output: []u8, offset: usize, value: u64) void {
    var index: usize = 0;
    while (index < 8) : (index += 1) {
        output[offset + index] = @truncate(value >> @intCast(index * 8));
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

fn allZero(values: []const u64) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
