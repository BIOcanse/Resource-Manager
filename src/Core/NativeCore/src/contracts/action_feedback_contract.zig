pub const Scope = struct {
    pub const process: u32 = 1;
    // Value 2 is retired software-grade dispatch; never reuse.
    pub const resource: u32 = 3;
};

pub const Domain = struct {
    pub const process: u32 = 1 << 0;
    pub const cpu: u32 = 1 << 1;
    pub const gpu: u32 = 1 << 2;
    pub const physical_memory: u32 = 1 << 3;
    pub const virtual_memory: u32 = 1 << 4;
    pub const video_memory: u32 = 1 << 5;
    pub const placement: u32 = 1 << 6;
    pub const shared_resource: u32 = 1 << 7;
    pub const resource_known: u32 = physical_memory |
        virtual_memory |
        video_memory |
        placement |
        shared_resource;
    pub const known: u32 = process | resource_known;
};

pub const Validity = struct {
    pub const completed_at: u64 = 1 << 0;
    pub const actual_process_grade: u64 = 1 << 1;
    pub const actual_cpu_grade: u64 = 1 << 2;
    pub const actual_gpu_grade: u64 = 1 << 3;
    pub const actual_memory_priority: u64 = 1 << 4;
    // Bits 2 and 3 are retired software-grade validity; never reuse.
    pub const grade_mask: u64 = actual_process_grade | actual_memory_priority;
    pub const known: u64 = completed_at | grade_mask;
};

pub const Flags = struct {
    pub const process_owned: u32 = 1 << 0;
    pub const cpu_owned: u32 = 1 << 1;
    pub const gpu_owned: u32 = 1 << 2;
    pub const rollback_payload_persisted: u32 = 1 << 3;
    pub const memory_owned: u32 = 1 << 4;
    // Bits 1 and 2 are retired software-grade ownership; never reuse.
    pub const ownership_mask: u32 = process_owned | memory_owned;
    pub const result_proof_mask: u32 = ownership_mask | rollback_payload_persisted;
    pub const known: u32 = result_proof_mask;
};

pub const Status = struct {
    pub const succeeded: u32 = 1;
    pub const failed_unchanged: u32 = 2;
    pub const rejected: u32 = 3;
    pub const skipped: u32 = 4;
    pub const ownership_lost: u32 = 5;
    pub const state_uncertain: u32 = 6;
};

pub const Shape = struct {
    scope: u32,
    domain_mask: u32,
    process_from_grade: i32,
    process_to_grade: i32,
    cpu_from_grade: i32 = 0,
    cpu_to_grade: i32 = 0,
    gpu_from_grade: i32 = 0,
    gpu_to_grade: i32 = 0,
    valid_mask: u64,
    flags: u32,
    status: u32,
    actual_process_grade: i32,
    actual_cpu_grade: i32 = 0,
    actual_gpu_grade: i32 = 0,
};

pub fn validate(shape: Shape) bool {
    if ((shape.valid_mask & Validity.completed_at) == 0 or
        (shape.valid_mask & ~Validity.known) != 0 or
        (shape.flags & ~Flags.known) != 0 or
        shape.status < Status.succeeded or
        shape.status > Status.state_uncertain or
        (shape.domain_mask & ~Domain.known) != 0 or
        shape.cpu_from_grade != 0 or shape.cpu_to_grade != 0 or
        shape.gpu_from_grade != 0 or shape.gpu_to_grade != 0 or
        shape.actual_cpu_grade != 0 or shape.actual_gpu_grade != 0)
    {
        return false;
    }

    const expected_validity = switch (shape.scope) {
        Scope.process => switch (shape.domain_mask) {
            Domain.process => Validity.actual_process_grade,
            Domain.physical_memory => Validity.actual_memory_priority,
            else => return false,
        },
        Scope.resource => if (shape.domain_mask != 0 and
            (shape.domain_mask & ~Domain.resource_known) == 0)
            0
        else
            return false,
        else => return false,
    };
    const provided_validity = shape.valid_mask & Validity.grade_mask;
    if ((provided_validity & ~expected_validity) != 0 or
        !validTaggedProcessValue(
            (provided_validity & Validity.actual_process_grade) != 0,
            (provided_validity & Validity.actual_memory_priority) != 0,
            shape.actual_process_grade,
        ))
    {
        return false;
    }

    return switch (shape.status) {
        Status.succeeded => validSucceeded(shape, expected_validity, provided_validity),
        Status.failed_unchanged, Status.rejected => validUnchanged(shape, provided_validity),
        Status.skipped, Status.ownership_lost, Status.state_uncertain => provided_validity == 0 and
            (shape.flags & Flags.result_proof_mask) == 0,
        else => false,
    };
}

fn validSucceeded(shape: Shape, expected_validity: u64, provided_validity: u64) bool {
    if (provided_validity != expected_validity) return false;
    if ((expected_validity & Validity.actual_process_grade) != 0 and
        shape.actual_process_grade != shape.process_to_grade) return false;
    if ((expected_validity & Validity.actual_memory_priority) != 0 and
        shape.actual_process_grade != shape.process_to_grade) return false;

    const expected_ownership =
        (if ((expected_validity & Validity.actual_process_grade) != 0 and
            shape.process_to_grade != 0) Flags.process_owned else 0);
    const expected_memory_ownership =
        if ((expected_validity & Validity.actual_memory_priority) != 0 and
            shape.process_to_grade != 0) Flags.memory_owned else 0;
    if ((shape.flags & Flags.ownership_mask) !=
        (expected_ownership | expected_memory_ownership)) return false;
    return ((shape.flags & Flags.rollback_payload_persisted) != 0) ==
        ((expected_ownership | expected_memory_ownership) != 0);
}

fn validUnchanged(shape: Shape, provided_validity: u64) bool {
    if ((shape.flags & Flags.result_proof_mask) != 0) return false;
    if ((provided_validity & Validity.actual_process_grade) != 0 and
        shape.actual_process_grade != shape.process_from_grade) return false;
    if ((provided_validity & Validity.actual_memory_priority) != 0 and
        shape.actual_process_grade != shape.process_from_grade) return false;
    return true;
}

fn validTaggedProcessValue(process_present: bool, memory_present: bool, value: i32) bool {
    if (process_present and memory_present) return false;
    if (process_present) return value >= -4 and value <= 1;
    if (memory_present) return value >= 0 and value <= 5;
    return value == 0;
}

test "skipped and uncertain feedback reject actual grades and result proof" {
    const std = @import("std");
    var shape = Shape{
        .scope = Scope.process,
        .domain_mask = Domain.process,
        .process_from_grade = 0,
        .process_to_grade = -1,
        .cpu_from_grade = 0,
        .cpu_to_grade = 0,
        .gpu_from_grade = 0,
        .gpu_to_grade = 0,
        .valid_mask = Validity.completed_at,
        .flags = 0,
        .status = Status.skipped,
        .actual_process_grade = 0,
        .actual_cpu_grade = 0,
        .actual_gpu_grade = 0,
    };
    try std.testing.expect(validate(shape));

    shape.valid_mask |= Validity.actual_process_grade;
    try std.testing.expect(!validate(shape));
    shape.valid_mask = Validity.completed_at;
    shape.flags = Flags.rollback_payload_persisted;
    try std.testing.expect(!validate(shape));

    shape.flags = 0;
    shape.status = Status.state_uncertain;
    try std.testing.expect(validate(shape));
    shape.valid_mask |= Validity.actual_process_grade;
    try std.testing.expect(!validate(shape));
    shape.valid_mask = Validity.completed_at;
    shape.flags = Flags.process_owned;
    try std.testing.expect(!validate(shape));
}

test "resource feedback accepts exact resource domains without grades" {
    const std = @import("std");
    const shape = Shape{
        .scope = Scope.resource,
        .domain_mask = Domain.physical_memory | Domain.shared_resource,
        .process_from_grade = 0,
        .process_to_grade = 0,
        .cpu_from_grade = 0,
        .cpu_to_grade = 0,
        .gpu_from_grade = 0,
        .gpu_to_grade = 0,
        .valid_mask = Validity.completed_at,
        .flags = 0,
        .status = Status.succeeded,
        .actual_process_grade = 0,
        .actual_cpu_grade = 0,
        .actual_gpu_grade = 0,
    };
    try std.testing.expect(validate(shape));

    var invalid = shape;
    invalid.domain_mask = Domain.cpu | Domain.physical_memory;
    try std.testing.expect(!validate(invalid));
    invalid = shape;
    invalid.valid_mask |= Validity.actual_cpu_grade;
    try std.testing.expect(!validate(invalid));
}

test "physical memory process feedback uses the tagged memory priority slot" {
    const std = @import("std");
    var shape = Shape{
        .scope = Scope.process,
        .domain_mask = Domain.physical_memory,
        .process_from_grade = 0,
        .process_to_grade = 3,
        .cpu_from_grade = 0,
        .cpu_to_grade = 0,
        .gpu_from_grade = 0,
        .gpu_to_grade = 0,
        .valid_mask = Validity.completed_at | Validity.actual_memory_priority,
        .flags = Flags.memory_owned | Flags.rollback_payload_persisted,
        .status = Status.succeeded,
        .actual_process_grade = 3,
        .actual_cpu_grade = 0,
        .actual_gpu_grade = 0,
    };
    try std.testing.expect(validate(shape));

    shape.process_from_grade = 3;
    shape.process_to_grade = 0;
    shape.actual_process_grade = 0;
    shape.flags = 0;
    try std.testing.expect(validate(shape));
}

test "physical memory process feedback rejects CPU tags ownership and invalid priorities" {
    const std = @import("std");
    const valid = Shape{
        .scope = Scope.process,
        .domain_mask = Domain.physical_memory,
        .process_from_grade = 0,
        .process_to_grade = 3,
        .cpu_from_grade = 0,
        .cpu_to_grade = 0,
        .gpu_from_grade = 0,
        .gpu_to_grade = 0,
        .valid_mask = Validity.completed_at | Validity.actual_memory_priority,
        .flags = Flags.memory_owned | Flags.rollback_payload_persisted,
        .status = Status.succeeded,
        .actual_process_grade = 3,
        .actual_cpu_grade = 0,
        .actual_gpu_grade = 0,
    };

    var invalid = valid;
    invalid.valid_mask = Validity.completed_at | Validity.actual_process_grade;
    invalid.flags = Flags.process_owned | Flags.rollback_payload_persisted;
    try std.testing.expect(!validate(invalid));

    invalid = valid;
    invalid.flags = Flags.process_owned | Flags.rollback_payload_persisted;
    try std.testing.expect(!validate(invalid));

    invalid = valid;
    invalid.process_to_grade = 6;
    invalid.actual_process_grade = 6;
    try std.testing.expect(!validate(invalid));
}

test "retired software scope and grade validity ownership or reserved values are rejected" {
    const std = @import("std");
    const process = Shape{
        .scope = Scope.process,
        .domain_mask = Domain.process,
        .process_from_grade = 0,
        .process_to_grade = -1,
        .valid_mask = Validity.completed_at | Validity.actual_process_grade,
        .flags = Flags.process_owned | Flags.rollback_payload_persisted,
        .status = Status.succeeded,
        .actual_process_grade = -1,
    };
    try std.testing.expect(validate(process));
    var retired_scope = process;
    retired_scope.scope = 2;
    try std.testing.expect(!validate(retired_scope));
    var retired_validity = process;
    retired_validity.valid_mask |= Validity.actual_cpu_grade;
    try std.testing.expect(!validate(retired_validity));
    retired_validity.valid_mask = process.valid_mask | Validity.actual_gpu_grade;
    try std.testing.expect(!validate(retired_validity));
    var retired_ownership = process;
    retired_ownership.flags |= Flags.cpu_owned;
    try std.testing.expect(!validate(retired_ownership));
    retired_ownership.flags = process.flags | Flags.gpu_owned;
    try std.testing.expect(!validate(retired_ownership));
    var reserved_value = process;
    reserved_value.actual_cpu_grade = 1;
    try std.testing.expect(!validate(reserved_value));
    reserved_value.actual_cpu_grade = 0;
    reserved_value.gpu_to_grade = 1;
    try std.testing.expect(!validate(reserved_value));
}
