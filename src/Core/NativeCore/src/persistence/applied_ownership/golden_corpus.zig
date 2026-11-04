// Historical software-grade values are only used to construct upgrade/rejection fixtures.
const LegacyAdapterGrade = enum(i32) { freeze = 0, optimize = 1, normal = 2, extreme = 3 };
const std = @import("std");
const protocol = @import("protocol.zig");

pub fn createConfig(capacity: u32, index_capacity: u32) protocol.CreateConfig {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CreateConfig),
        .ledger_instance_low = 0x1111_2222_3333_4444,
        .ledger_instance_high = 0xaaaa_bbbb_cccc_dddd,
        .record_capacity = capacity,
        .primary_index_capacity = index_capacity,
        .payload_index_capacity = index_capacity,
        .flags = 0,
        .maximum_resident_bytes = 16 * 1024 * 1024,
        .maximum_image_bytes = @sizeOf(protocol.ImageHeader) +
            @as(u64, capacity) * @sizeOf(protocol.Record),
        .reserved = .{ 0, 0, 0, 0, 0 },
    };
}

pub fn openConfig(capacity: u32, index_capacity: u32) protocol.OpenConfig {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.OpenConfig),
        .maximum_record_capacity = capacity,
        .maximum_primary_index_capacity = index_capacity,
        .maximum_payload_index_capacity = index_capacity,
        .flags = 0,
        .maximum_resident_bytes = 16 * 1024 * 1024,
        .maximum_image_bytes = @sizeOf(protocol.ImageHeader) +
            @as(u64, capacity) * @sizeOf(protocol.Record),
        .reserved = .{ 0, 0, 0, 0 },
    };
}

pub fn processPrimary(id: u64) protocol.PrimaryIdentity {
    return .{
        .scope = @intFromEnum(protocol.Scope.process),
        .reserved_u32 = 0,
        .target_id = 100 + id,
        .software_id = 200 + id,
        .process_start_key = 300 + id,
        .process_id = @intCast(400 + id),
        .process_reserved = 0,
    };
}

pub fn adapterPrimary(id: u64) protocol.PrimaryIdentity {
    return .{
        .scope = 2,
        .reserved_u32 = 0,
        .target_id = 0,
        .software_id = 500 + id,
        .process_start_key = 0,
        .process_id = 0,
        .process_reserved = 0,
    };
}

pub fn processBinding(id: u64) protocol.OriginalBinding {
    const primary = processPrimary(id);
    return .{
        .journal_instance_low = 0x0102_0304_0506_0708,
        .journal_instance_high = 0x1112_1314_1516_1718,
        .action_identity = .{
            .configuration_generation = 17,
            .plan_epoch = 19,
            .action_id = 600 + id,
            .host_session_incarnation = 23,
            .target_id = primary.target_id,
            .software_id = primary.software_id,
            .process_start_key = primary.process_start_key,
            .process_id = primary.process_id,
            .reserved = 0,
        },
        .scope = @intFromEnum(protocol.JournalScope.process),
        .disposition = @intFromEnum(protocol.Disposition.apply),
        .domain_mask = protocol.DomainBits.process,
        .grade_valid_mask = protocol.GradeValid.process,
        .process_from_grade = @intFromEnum(protocol.ProcessGrade.normal),
        .process_to_grade = @intFromEnum(protocol.ProcessGrade.level_1),
        .cpu_from_grade = 0,
        .cpu_to_grade = 0,
        .gpu_from_grade = 0,
        .gpu_to_grade = 0,
        .stable_system_status = 29,
        .stable_system_error = 0,
        .maximum_recovery_attempts = 3,
        .recovery_reserved = 0,
        .recovery_deadline_utc_ms = 20_000,
        .atomic_group_id = 0,
        .group_member_index = 0,
        .group_member_count = 0,
    };
}

pub fn adapterBinding(id: u64) protocol.OriginalBinding {
    const primary = adapterPrimary(id);
    return .{
        .journal_instance_low = 0x2122_2324_2526_2728,
        .journal_instance_high = 0x3132_3334_3536_3738,
        .action_identity = .{
            .configuration_generation = 17,
            .plan_epoch = 31,
            .action_id = 700 + id,
            .host_session_incarnation = 37,
            .target_id = 800 + id,
            .software_id = primary.software_id,
            .process_start_key = 0,
            .process_id = 0,
            .reserved = 0,
        },
        .scope = 2,
        .disposition = @intFromEnum(protocol.Disposition.apply),
        .domain_mask = protocol.DomainBits.cpu | protocol.DomainBits.gpu,
        .grade_valid_mask = protocol.GradeValid.cpu | protocol.GradeValid.gpu,
        .process_from_grade = 0,
        .process_to_grade = 0,
        .cpu_from_grade = @intFromEnum(LegacyAdapterGrade.normal),
        .cpu_to_grade = @intFromEnum(LegacyAdapterGrade.optimize),
        .gpu_from_grade = @intFromEnum(LegacyAdapterGrade.normal),
        .gpu_to_grade = @intFromEnum(LegacyAdapterGrade.freeze),
        .stable_system_status = 41,
        .stable_system_error = 0,
        .maximum_recovery_attempts = 5,
        .recovery_reserved = 0,
        .recovery_deadline_utc_ms = 30_000,
        .atomic_group_id = 0,
        .group_member_index = 0,
        .group_member_count = 0,
    };
}

pub fn payload(id: u64) protocol.DurablePayloadReference {
    return .{
        .slot = @intCast(900 + id),
        .generation = 3,
        .length = 128 + id,
        .digest_low = 0x4142_4344_4546_4700 + id,
        .digest_high = 0x5152_5354_5556_5700 + id,
    };
}

pub fn processGrades(grade: protocol.ProcessGrade) protocol.CurrentGrades {
    return .{
        .valid_mask = protocol.GradeValid.process,
        .reserved_u32 = 0,
        .process_grade = @intFromEnum(grade),
        .cpu_grade = 0,
        .gpu_grade = 0,
        .reserved_i32 = 0,
    };
}

pub fn adapterGrades(cpu: LegacyAdapterGrade, gpu: LegacyAdapterGrade) protocol.CurrentGrades {
    return .{
        .valid_mask = protocol.GradeValid.cpu | protocol.GradeValid.gpu,
        .reserved_u32 = 0,
        .process_grade = 0,
        .cpu_grade = @intFromEnum(cpu),
        .gpu_grade = @intFromEnum(gpu),
        .reserved_i32 = 0,
    };
}

pub fn emptyGrades() protocol.CurrentGrades {
    return std.mem.zeroes(protocol.CurrentGrades);
}

pub fn processPromote(id: u64, expected_revision: u64, now_utc_ms: u64) protocol.PromoteInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PromoteInput),
        .expected_ledger_revision = expected_revision,
        .primary = processPrimary(id),
        .original_binding = processBinding(id),
        .payload = payload(id),
        .current_grades = processGrades(.level_1),
        .promoted_at_utc_ms = now_utc_ms,
        .reserved = .{ 0, 0, 0, 0, 0 },
    };
}

pub fn adapterPromote(id: u64, expected_revision: u64, now_utc_ms: u64) protocol.PromoteInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PromoteInput),
        .expected_ledger_revision = expected_revision,
        .primary = adapterPrimary(id),
        .original_binding = adapterBinding(id),
        .payload = payload(100 + id),
        .current_grades = adapterGrades(.optimize, .freeze),
        .promoted_at_utc_ms = now_utc_ms,
        .reserved = .{ 0, 0, 0, 0, 0 },
    };
}

pub fn cas(record: *const protocol.Record) protocol.OwnershipCas {
    return .{
        .primary = record.primary,
        .journal_instance_low = record.original_binding.journal_instance_low,
        .journal_instance_high = record.original_binding.journal_instance_high,
        .original_action_identity = record.original_binding.action_identity,
        .payload = record.payload,
        .expected_record_revision = record.record_revision,
        .expected_current_grades = record.current_grades,
        .reserved = .{0},
    };
}

pub fn processTransition(
    record: *const protocol.Record,
    expected_ledger_revision: u64,
    to_grade: protocol.ProcessGrade,
    now_utc_ms: u64,
) protocol.TransitionInput {
    var binding = record.original_binding;
    binding.journal_instance_low += 1;
    binding.action_identity.plan_epoch += 1;
    binding.action_identity.action_id += 1;
    binding.process_from_grade = record.current_grades.process_grade;
    binding.process_to_grade = @intFromEnum(to_grade);
    binding.disposition = if (to_grade == .normal)
        @intFromEnum(protocol.Disposition.restore)
    else
        @intFromEnum(protocol.Disposition.apply);
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.TransitionInput),
        .expected_ledger_revision = expected_ledger_revision,
        .cas = cas(record),
        .transition_binding = binding,
        .transition_payload = record.payload,
        .new_current_grades = if (to_grade == .normal)
            emptyGrades()
        else
            processGrades(to_grade),
        .updated_at_utc_ms = now_utc_ms,
        .reserved = .{ 0, 0 },
    };
}

pub fn remove(
    record: *const protocol.Record,
    expected_ledger_revision: u64,
    now_utc_ms: u64,
) protocol.RemoveInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.RemoveInput),
        .expected_ledger_revision = expected_ledger_revision,
        .cas = cas(record),
        .removed_at_utc_ms = now_utc_ms,
        .reserved = .{0},
    };
}

