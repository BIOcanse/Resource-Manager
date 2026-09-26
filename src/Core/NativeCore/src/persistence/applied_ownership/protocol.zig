const std = @import("std");

pub const abi_version: u32 = 0x0002_0000;
pub const image_magic: u64 = 0x524d_4150_4f57_4e32;

pub const Status = enum(i32) {
    ok = 0,
    invalid_argument = 1,
    abi_mismatch = 2,
    capacity_full = 3,
    no_data = 4,
    stale_revision = 5,
    duplicate_identity = 6,
    duplicate_payload = 7,
    identity_mismatch = 8,
    payload_mismatch = 9,
    grade_mismatch = 10,
    corrupt_image = 11,
    truncated_image = 12,
    checksum_mismatch = 13,
    non_canonical_image = 14,
    configuration_mismatch = 15,
    buffer_too_small = 16,
    revision_exhausted = 17,
    out_of_memory = 18,
    invalid_time = 19,
};

pub const Scope = enum(u32) {
    process = 1,
    adapter = 2,
};

pub const JournalScope = enum(u32) {
    process = 1,
    software = 2,
};

pub const Disposition = enum(u32) {
    apply = 1,
    restore = 2,
};

pub const DomainBits = struct {
    pub const process: u32 = 1 << 0;
    pub const cpu: u32 = 1 << 1;
    pub const gpu: u32 = 1 << 2;
    pub const physical_memory: u32 = 1 << 3;
    pub const known: u32 = process | cpu | gpu | physical_memory;
};

pub const GradeValid = struct {
    pub const process: u32 = 1 << 0;
    pub const cpu: u32 = 1 << 1;
    pub const gpu: u32 = 1 << 2;
    pub const memory: u32 = 1 << 3;
    pub const known: u32 = process | cpu | gpu | memory;
};

pub const ProcessGrade = enum(i32) {
    level_4 = -4,
    level_3 = -3,
    level_2 = -2,
    level_1 = -1,
    normal = 0,
    a1 = 1,
};

pub const AdapterGrade = enum(i32) {
    freeze = 0,
    optimize = 1,
    normal = 2,
    extreme = 3,
};

pub const CreateConfig = extern struct {
    abi_version: u32,
    struct_size: u32,
    ledger_instance_low: u64,
    ledger_instance_high: u64,
    record_capacity: u32,
    primary_index_capacity: u32,
    payload_index_capacity: u32,
    flags: u32,
    maximum_resident_bytes: u64,
    maximum_image_bytes: u64,
    reserved: [5]u64,
};

pub const OpenConfig = extern struct {
    abi_version: u32,
    struct_size: u32,
    maximum_record_capacity: u32,
    maximum_primary_index_capacity: u32,
    maximum_payload_index_capacity: u32,
    flags: u32,
    maximum_resident_bytes: u64,
    maximum_image_bytes: u64,
    reserved: [4]u64,
};

pub const Capacity = extern struct {
    struct_size: u32,
    record_capacity: u32,
    primary_index_capacity: u32,
    payload_index_capacity: u32,
    record_size: u32,
    flags: u32,
    maximum_image_bytes: u64,
    resident_bytes: u64,
    reserved: [3]u64,
};

pub const PrimaryIdentity = extern struct {
    scope: u32,
    reserved_u32: u32,
    target_id: u64,
    software_id: u64,
    process_start_key: u64,
    process_id: u32,
    process_reserved: u32,
};

pub const ActionIdentity = extern struct {
    configuration_generation: u64,
    plan_epoch: u64,
    action_id: u64,
    host_session_incarnation: u64,
    target_id: u64,
    software_id: u64,
    process_start_key: u64,
    process_id: u32,
    reserved: u32,
};

pub const OriginalBinding = extern struct {
    journal_instance_low: u64,
    journal_instance_high: u64,
    action_identity: ActionIdentity,
    scope: u32,
    disposition: u32,
    domain_mask: u32,
    grade_valid_mask: u32,
    process_from_grade: i32,
    process_to_grade: i32,
    cpu_from_grade: i32,
    cpu_to_grade: i32,
    gpu_from_grade: i32,
    gpu_to_grade: i32,
    stable_system_status: u32,
    stable_system_error: u32,
    maximum_recovery_attempts: u32,
    recovery_reserved: u32,
    recovery_deadline_utc_ms: u64,
    atomic_group_id: u64,
    group_member_index: u32,
    group_member_count: u32,
};

pub const DurablePayloadReference = extern struct {
    slot: u32,
    generation: u32,
    length: u64,
    digest_low: u64,
    digest_high: u64,
};

pub const CurrentGrades = extern struct {
    valid_mask: u32,
    reserved_u32: u32,
    process_grade: i32,
    cpu_grade: i32,
    gpu_grade: i32,
    reserved_i32: i32,
};

pub const OwnershipCas = extern struct {
    primary: PrimaryIdentity,
    journal_instance_low: u64,
    journal_instance_high: u64,
    original_action_identity: ActionIdentity,
    payload: DurablePayloadReference,
    expected_record_revision: u64,
    expected_current_grades: CurrentGrades,
    reserved: [1]u64,
};

pub const Record = extern struct {
    primary: PrimaryIdentity,
    original_binding: OriginalBinding,
    payload: DurablePayloadReference,
    current_grades: CurrentGrades,
    record_revision: u64,
    promoted_at_utc_ms: u64,
    updated_at_utc_ms: u64,
    flags: u32,
    reserved_u32: u32,
    reserved: [4]u64,
};

pub const PromoteInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    expected_ledger_revision: u64,
    primary: PrimaryIdentity,
    original_binding: OriginalBinding,
    payload: DurablePayloadReference,
    current_grades: CurrentGrades,
    promoted_at_utc_ms: u64,
    reserved: [5]u64,
};

pub const TransitionInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    expected_ledger_revision: u64,
    cas: OwnershipCas,
    transition_binding: OriginalBinding,
    transition_payload: DurablePayloadReference,
    new_current_grades: CurrentGrades,
    updated_at_utc_ms: u64,
    reserved: [2]u64,
};

pub const RemoveInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    expected_ledger_revision: u64,
    cas: OwnershipCas,
    removed_at_utc_ms: u64,
    reserved: [1]u64,
};

pub const SnapshotHeader = extern struct {
    abi_version: u32,
    struct_size: u32,
    ledger_revision: u64,
    ledger_instance_low: u64,
    ledger_instance_high: u64,
    entry_count: u32,
    record_capacity: u32,
    primary_index_capacity: u32,
    payload_index_capacity: u32,
    maximum_image_bytes: u64,
    resident_bytes: u64,
    reserved: [4]u64,
};

pub const ImageHeader = extern struct {
    magic: u64,
    abi_version: u32,
    header_size: u32,
    record_size: u32,
    flags: u32,
    ledger_instance_low: u64,
    ledger_instance_high: u64,
    ledger_revision: u64,
    entry_count: u32,
    record_capacity: u32,
    image_length: u64,
    crc64_ecma: u64,
    primary_index_capacity: u32,
    payload_index_capacity: u32,
    maximum_image_bytes: u64,
    reserved: [5]u64,
};

pub fn validCreateConfig(config: *const CreateConfig) bool {
    return config.abi_version == abi_version and
        config.struct_size == @sizeOf(CreateConfig) and
        (config.ledger_instance_low != 0 or config.ledger_instance_high != 0) and
        config.record_capacity != 0 and
        validIndexCapacity(config.primary_index_capacity, config.record_capacity) and
        validIndexCapacity(config.payload_index_capacity, config.record_capacity) and
        config.flags == 0 and
        config.maximum_resident_bytes != 0 and
        validMaximumImageBytes(config.maximum_image_bytes, config.record_capacity) and
        allZero(&config.reserved);
}

pub fn validOpenConfig(config: *const OpenConfig) bool {
    return config.abi_version == abi_version and
        config.struct_size == @sizeOf(OpenConfig) and
        config.maximum_record_capacity != 0 and
        validIndexCapacity(config.maximum_primary_index_capacity, config.maximum_record_capacity) and
        validIndexCapacity(config.maximum_payload_index_capacity, config.maximum_record_capacity) and
        config.flags == 0 and
        config.maximum_resident_bytes != 0 and
        validMaximumImageBytes(config.maximum_image_bytes, config.maximum_record_capacity) and
        allZero(&config.reserved);
}

pub fn validPrimary(primary: *const PrimaryIdentity) bool {
    if (primary.reserved_u32 != 0 or primary.process_reserved != 0) return false;
    return switch (primary.scope) {
        @intFromEnum(Scope.process) => primary.target_id != 0 and
            primary.process_start_key != 0 and primary.process_id != 0,
        @intFromEnum(Scope.adapter) => primary.target_id == 0 and
            primary.software_id != 0 and primary.process_start_key == 0 and primary.process_id == 0,
        else => false,
    };
}

pub fn validActionIdentity(identity: *const ActionIdentity, scope: Scope) bool {
    if (identity.configuration_generation == 0 or identity.plan_epoch == 0 or
        identity.action_id == 0 or identity.host_session_incarnation == 0 or
        identity.target_id == 0 or identity.reserved != 0)
    {
        return false;
    }
    return switch (scope) {
        .process => identity.process_start_key != 0 and identity.process_id != 0,
        .adapter => identity.software_id != 0 and
            identity.process_start_key == 0 and identity.process_id == 0,
    };
}

pub fn validOriginalBinding(binding: *const OriginalBinding, ownership_scope: Scope) bool {
    if ((binding.journal_instance_low == 0 and binding.journal_instance_high == 0) or
        binding.disposition != @intFromEnum(Disposition.apply) or
        binding.recovery_reserved != 0 or binding.maximum_recovery_attempts == 0 or
        binding.recovery_deadline_utc_ms == 0 or
        !validAtomicGroup(binding.atomic_group_id, binding.group_member_index, binding.group_member_count) or
        !validActionIdentity(&binding.action_identity, ownership_scope))
    {
        return false;
    }
    return switch (ownership_scope) {
        .process => validProcessBinding(binding, .apply),
        .adapter => validAdapterBinding(binding),
    };
}

pub fn validPayload(payload: *const DurablePayloadReference) bool {
    return payload.slot != 0 and payload.generation != 0 and payload.length != 0 and
        payload.digest_low != 0 and payload.digest_high != 0;
}

pub fn validCurrentGrades(grades: *const CurrentGrades, scope: Scope) bool {
    if (grades.reserved_u32 != 0 or grades.reserved_i32 != 0 or
        (grades.valid_mask & ~GradeValid.known) != 0)
    {
        return false;
    }
    return switch (scope) {
        .process => grades.cpu_grade == 0 and grades.gpu_grade == 0 and
            switch (grades.valid_mask) {
                GradeValid.process => validActiveProcessGrade(grades.process_grade),
                GradeValid.memory => validOwnedMemoryPriority(grades.process_grade),
                else => false,
            },
        .adapter => blk: {
            if (grades.valid_mask == 0 or
                (grades.valid_mask & ~(GradeValid.cpu | GradeValid.gpu)) != 0 or
                grades.process_grade != 0)
            {
                break :blk false;
            }
            break :blk validActiveAdapterGradeIfPresent(
                (grades.valid_mask & GradeValid.cpu) != 0,
                grades.cpu_grade,
            ) and validActiveAdapterGradeIfPresent(
                (grades.valid_mask & GradeValid.gpu) != 0,
                grades.gpu_grade,
            );
        },
    };
}

pub fn validPromotedGrades(grades: *const CurrentGrades, binding: *const OriginalBinding, scope: Scope) bool {
    if (!validCurrentGrades(grades, scope)) return false;
    return switch (scope) {
        .process => grades.valid_mask == binding.grade_valid_mask and
            grades.process_grade == binding.process_to_grade,
        .adapter => grades.valid_mask == binding.grade_valid_mask and
            grades.cpu_grade == binding.cpu_to_grade and grades.gpu_grade == binding.gpu_to_grade,
    };
}

fn validProcessBinding(binding: *const OriginalBinding, disposition: Disposition) bool {
    if (binding.scope != @intFromEnum(JournalScope.process) or
        binding.cpu_from_grade != 0 or binding.cpu_to_grade != 0 or
        binding.gpu_from_grade != 0 or binding.gpu_to_grade != 0)
    {
        return false;
    }
    return switch (binding.domain_mask) {
        DomainBits.process => binding.grade_valid_mask == GradeValid.process and
            validProcessTransitionPair(
                disposition,
                binding.process_from_grade,
                binding.process_to_grade,
            ),
        DomainBits.physical_memory => binding.grade_valid_mask == GradeValid.memory and
            validMemoryPriorityTransitionPair(
                disposition,
                binding.process_from_grade,
                binding.process_to_grade,
            ),
        else => false,
    };
}

pub fn validRecord(record: *const Record) Status {
    if (!validPrimary(&record.primary)) return .invalid_argument;
    const scope: Scope = @enumFromInt(record.primary.scope);
    if (!validOriginalBinding(&record.original_binding, scope) or
        !primaryMatchesBinding(&record.primary, &record.original_binding))
    {
        return .identity_mismatch;
    }
    if (!validPayload(&record.payload)) return .payload_mismatch;
    if (!validCurrentGrades(&record.current_grades, scope))
        return .grade_mismatch;
    if (record.record_revision == 0 or record.promoted_at_utc_ms == 0 or
        record.updated_at_utc_ms < record.promoted_at_utc_ms)
    {
        return .invalid_time;
    }
    if (record.flags != 0 or record.reserved_u32 != 0 or !allZero(&record.reserved))
        return .abi_mismatch;
    return .ok;
}

pub fn validCas(cas: *const OwnershipCas) bool {
    if (!validPrimary(&cas.primary) or !validPayload(&cas.payload) or
        cas.expected_record_revision == 0 or !allZero(&cas.reserved))
    {
        return false;
    }
    const scope: Scope = @enumFromInt(cas.primary.scope);
    if ((cas.journal_instance_low == 0 and cas.journal_instance_high == 0) or
        !validActionIdentity(&cas.original_action_identity, scope) or
        !primaryMatchesActionIdentity(&cas.primary, &cas.original_action_identity))
    {
        return false;
    }
    return validCurrentGrades(&cas.expected_current_grades, scope);
}

pub fn validTransition(
    current: *const CurrentGrades,
    binding: *const OriginalBinding,
    target: *const CurrentGrades,
    scope: Scope,
) bool {
    var expected = std.mem.zeroes(CurrentGrades);
    return projectTransition(current, binding, scope, &expected) and sameGrades(&expected, target);
}

pub fn projectTransition(
    current: *const CurrentGrades,
    binding: *const OriginalBinding,
    scope: Scope,
    target: *CurrentGrades,
) bool {
    target.* = std.mem.zeroes(CurrentGrades);
    if (!validCurrentGrades(current, scope) or !validTransitionBinding(binding, scope))
        return false;
    return switch (scope) {
        .process => projectProcessTransition(current, binding, target),
        .adapter => projectAdapterTransition(current, binding, target),
    };
}

pub fn primaryMatchesBinding(primary: *const PrimaryIdentity, binding: *const OriginalBinding) bool {
    return primaryMatchesActionIdentity(primary, &binding.action_identity);
}

pub fn primaryMatchesActionIdentity(primary: *const PrimaryIdentity, identity: *const ActionIdentity) bool {
    return switch (@as(Scope, @enumFromInt(primary.scope))) {
        .process => primary.target_id == identity.target_id and
            primary.software_id == identity.software_id and
            primary.process_start_key == identity.process_start_key and
            primary.process_id == identity.process_id,
        .adapter => primary.software_id == identity.software_id,
    };
}

pub fn samePrimary(left: *const PrimaryIdentity, right: *const PrimaryIdentity) bool {
    return left.scope == right.scope and left.target_id == right.target_id and
        left.software_id == right.software_id and
        left.process_start_key == right.process_start_key and left.process_id == right.process_id;
}

pub fn primaryLessThan(left: *const PrimaryIdentity, right: *const PrimaryIdentity) bool {
    if (left.scope != right.scope) return left.scope < right.scope;
    if (left.target_id != right.target_id) return left.target_id < right.target_id;
    if (left.software_id != right.software_id) return left.software_id < right.software_id;
    if (left.process_start_key != right.process_start_key)
        return left.process_start_key < right.process_start_key;
    return left.process_id < right.process_id;
}

pub fn sameActionIdentity(left: *const ActionIdentity, right: *const ActionIdentity) bool {
    return std.meta.eql(left.*, right.*);
}

pub fn samePayload(left: *const DurablePayloadReference, right: *const DurablePayloadReference) bool {
    return std.meta.eql(left.*, right.*);
}

pub fn samePayloadKey(
    left_binding: *const OriginalBinding,
    left_payload: *const DurablePayloadReference,
    right_binding: *const OriginalBinding,
    right_payload: *const DurablePayloadReference,
) bool {
    return left_binding.journal_instance_low == right_binding.journal_instance_low and
        left_binding.journal_instance_high == right_binding.journal_instance_high and
        left_payload.slot == right_payload.slot and left_payload.generation == right_payload.generation;
}

pub fn sameGrades(left: *const CurrentGrades, right: *const CurrentGrades) bool {
    return std.meta.eql(left.*, right.*);
}

pub fn bindingMatchesCas(binding: *const OriginalBinding, cas: *const OwnershipCas) bool {
    return binding.journal_instance_low == cas.journal_instance_low and
        binding.journal_instance_high == cas.journal_instance_high and
        sameActionIdentity(&binding.action_identity, &cas.original_action_identity);
}

pub fn payloadMatchesCas(record: *const Record, cas: *const OwnershipCas) bool {
    return record.original_binding.journal_instance_low == cas.journal_instance_low and
        record.original_binding.journal_instance_high == cas.journal_instance_high and
        samePayload(&record.payload, &cas.payload);
}

fn validAdapterBinding(binding: *const OriginalBinding) bool {
    if (binding.scope != @intFromEnum(JournalScope.software) or
        binding.disposition != @intFromEnum(Disposition.apply) or
        binding.domain_mask == 0 or (binding.domain_mask & ~(DomainBits.cpu | DomainBits.gpu)) != 0 or
        binding.process_from_grade != 0 or binding.process_to_grade != 0)
    {
        return false;
    }
    const required = adapterGradeMask(binding.domain_mask) orelse return false;
    return binding.grade_valid_mask == required and
        validAdapterTransitionPair(
            (required & GradeValid.cpu) != 0,
            .apply,
            binding.cpu_from_grade,
            binding.cpu_to_grade,
        ) and
        validAdapterTransitionPair(
            (required & GradeValid.gpu) != 0,
            .apply,
            binding.gpu_from_grade,
            binding.gpu_to_grade,
        );
}

fn validTransitionBinding(binding: *const OriginalBinding, ownership_scope: Scope) bool {
    if ((binding.journal_instance_low == 0 and binding.journal_instance_high == 0) or
        binding.recovery_reserved != 0 or binding.maximum_recovery_attempts == 0 or
        binding.recovery_deadline_utc_ms == 0 or
        !validAtomicGroup(binding.atomic_group_id, binding.group_member_index, binding.group_member_count) or
        !validActionIdentity(&binding.action_identity, ownership_scope))
    {
        return false;
    }
    const disposition: Disposition = switch (binding.disposition) {
        @intFromEnum(Disposition.apply) => .apply,
        @intFromEnum(Disposition.restore) => .restore,
        else => return false,
    };
    return switch (ownership_scope) {
        .process => validProcessBinding(binding, disposition),
        .adapter => validAdapterTransitionBinding(binding, disposition),
    };
}

fn projectProcessTransition(
    current: *const CurrentGrades,
    binding: *const OriginalBinding,
    target: *CurrentGrades,
) bool {
    if (current.valid_mask != binding.grade_valid_mask or
        current.process_grade != binding.process_from_grade) return false;
    const disposition: Disposition = @enumFromInt(binding.disposition);
    return switch (disposition) {
        .apply => blk: {
            target.* = .{
                .valid_mask = binding.grade_valid_mask,
                .reserved_u32 = 0,
                .process_grade = binding.process_to_grade,
                .cpu_grade = 0,
                .gpu_grade = 0,
                .reserved_i32 = 0,
            };
            break :blk validCurrentGrades(target, .process);
        },
        .restore => emptyCurrentGrades(target),
    };
}

fn projectAdapterTransition(
    current: *const CurrentGrades,
    binding: *const OriginalBinding,
    target: *CurrentGrades,
) bool {
    var expected = current.*;
    const disposition: Disposition = @enumFromInt(binding.disposition);
    if ((binding.domain_mask & DomainBits.cpu) != 0) {
        const current_cpu = if ((current.valid_mask & GradeValid.cpu) != 0)
            current.cpu_grade
        else
            @intFromEnum(AdapterGrade.normal);
        if (binding.cpu_from_grade != current_cpu) return false;
        switch (disposition) {
            .apply => {
                expected.valid_mask |= GradeValid.cpu;
                expected.cpu_grade = binding.cpu_to_grade;
            },
            .restore => {
                if ((current.valid_mask & GradeValid.cpu) == 0) return false;
                expected.valid_mask &= ~GradeValid.cpu;
                expected.cpu_grade = 0;
            },
        }
    }
    if ((binding.domain_mask & DomainBits.gpu) != 0) {
        const current_gpu = if ((current.valid_mask & GradeValid.gpu) != 0)
            current.gpu_grade
        else
            @intFromEnum(AdapterGrade.normal);
        if (binding.gpu_from_grade != current_gpu) return false;
        switch (disposition) {
            .apply => {
                expected.valid_mask |= GradeValid.gpu;
                expected.gpu_grade = binding.gpu_to_grade;
            },
            .restore => {
                if ((current.valid_mask & GradeValid.gpu) == 0) return false;
                expected.valid_mask &= ~GradeValid.gpu;
                expected.gpu_grade = 0;
            },
        }
    }
    target.* = expected;
    return if (target.valid_mask == 0)
        emptyCurrentGrades(target)
    else
        validCurrentGrades(target, .adapter);
}

fn validAdapterTransitionBinding(binding: *const OriginalBinding, disposition: Disposition) bool {
    if (binding.scope != @intFromEnum(JournalScope.software) or
        binding.domain_mask == 0 or
        (binding.domain_mask & ~(DomainBits.cpu | DomainBits.gpu)) != 0 or
        binding.process_from_grade != 0 or binding.process_to_grade != 0)
    {
        return false;
    }
    const required = adapterGradeMask(binding.domain_mask) orelse return false;
    return binding.grade_valid_mask == required and
        validAdapterTransitionPair(
            (required & GradeValid.cpu) != 0,
            disposition,
            binding.cpu_from_grade,
            binding.cpu_to_grade,
        ) and
        validAdapterTransitionPair(
            (required & GradeValid.gpu) != 0,
            disposition,
            binding.gpu_from_grade,
            binding.gpu_to_grade,
        );
}

fn adapterGradeMask(domain_mask: u32) ?u32 {
    if (domain_mask == 0 or (domain_mask & ~(DomainBits.cpu | DomainBits.gpu)) != 0) return null;
    return (if ((domain_mask & DomainBits.cpu) != 0) GradeValid.cpu else 0) |
        (if ((domain_mask & DomainBits.gpu) != 0) GradeValid.gpu else 0);
}

fn validProcessTransitionPair(disposition: Disposition, from: i32, to: i32) bool {
    if (!validProcessGrade(from) or !validProcessGrade(to) or from == to) return false;
    return switch (disposition) {
        .apply => to != @intFromEnum(ProcessGrade.normal),
        .restore => from != @intFromEnum(ProcessGrade.normal) and
            to == @intFromEnum(ProcessGrade.normal),
    };
}

fn validMemoryPriorityTransitionPair(disposition: Disposition, from: i32, to: i32) bool {
    return switch (disposition) {
        .apply => from >= 0 and from <= 5 and validOwnedMemoryPriority(to) and from != to,
        .restore => validOwnedMemoryPriority(from) and to == 0,
    };
}

fn validOwnedMemoryPriority(value: i32) bool {
    return value >= 1 and value <= 5;
}

fn validProcessGrade(value: i32) bool {
    return value >= @intFromEnum(ProcessGrade.level_4) and value <= @intFromEnum(ProcessGrade.a1);
}

fn validActiveProcessGrade(value: i32) bool {
    return validProcessGrade(value) and value != @intFromEnum(ProcessGrade.normal);
}

fn validActiveAdapterGradeIfPresent(present: bool, value: i32) bool {
    return if (present)
        validAdapterGrade(value) and value != @intFromEnum(AdapterGrade.normal)
    else
        value == 0;
}

fn validAdapterGrade(value: i32) bool {
    return value >= @intFromEnum(AdapterGrade.freeze) and value <= @intFromEnum(AdapterGrade.extreme);
}

fn validAdapterTransitionPair(
    present: bool,
    disposition: Disposition,
    from: i32,
    to: i32,
) bool {
    if (!present) return from == 0 and to == 0;
    if (!validAdapterGrade(from) or !validAdapterGrade(to) or from == to) return false;
    return switch (disposition) {
        .apply => to != @intFromEnum(AdapterGrade.normal),
        .restore => from != @intFromEnum(AdapterGrade.normal) and
            to == @intFromEnum(AdapterGrade.normal),
    };
}

fn emptyCurrentGrades(grades: *const CurrentGrades) bool {
    return grades.valid_mask == 0 and grades.reserved_u32 == 0 and
        grades.process_grade == 0 and grades.cpu_grade == 0 and
        grades.gpu_grade == 0 and grades.reserved_i32 == 0;
}

fn validAtomicGroup(group_id: u64, member_index: u32, member_count: u32) bool {
    if (group_id == 0) return member_index == 0 and member_count == 0;
    return member_count > 1 and member_index < member_count;
}

fn validIndexCapacity(capacity: u32, record_capacity: u32) bool {
    return capacity >= record_capacity and std.math.isPowerOfTwo(capacity);
}

fn validMaximumImageBytes(maximum: u64, record_capacity: u32) bool {
    const records_bytes = std.math.mul(u64, record_capacity, @sizeOf(Record)) catch return false;
    const required = std.math.add(u64, @sizeOf(ImageHeader), records_bytes) catch return false;
    return maximum >= required;
}

fn allZero(values: []const u64) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

comptime {
    if (@sizeOf(CreateConfig) != 96 or @sizeOf(OpenConfig) != 72 or @sizeOf(Capacity) != 64 or
        @sizeOf(PrimaryIdentity) != 40 or @sizeOf(ActionIdentity) != 64 or
        @sizeOf(OriginalBinding) != 160 or @sizeOf(DurablePayloadReference) != 32 or
        @sizeOf(CurrentGrades) != 24 or @sizeOf(OwnershipCas) != 192 or
        @sizeOf(Record) != 320 or @sizeOf(PromoteInput) != 320 or
        @sizeOf(TransitionInput) != 448 or @sizeOf(RemoveInput) != 224 or
        @sizeOf(SnapshotHeader) != 96 or @sizeOf(ImageHeader) != 128)
    {
        @compileError("applied ownership ABI layout drifted");
    }
}
