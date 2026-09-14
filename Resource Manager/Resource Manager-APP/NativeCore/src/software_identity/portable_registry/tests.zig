const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const abi = @import("abi.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const RegistrationBuffer = [4]protocol.RegistrationSnapshot;
const PathBuffer = [8]protocol.PathSnapshot;
const OperationBuffer = [4]protocol.PersistenceOperation;

test "capacity and resident budget are explicit and shape reconfigure is strict" {
    var config = explicitConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    const capacity = session.capacity();
    try std.testing.expectEqual(config.maximum_registration_count, capacity.registration_capacity);
    try std.testing.expectEqual(config.maximum_path_count, capacity.path_capacity);
    try std.testing.expect(capacity.resident_byte_count != 0);
    try std.testing.expect(capacity.resident_byte_count <= config.resident_byte_budget);

    var too_small = config;
    too_small.generation = 2;
    too_small.resident_byte_budget = capacity.resident_byte_count - 1;
    try std.testing.expectEqual(ResultCode.invalid_argument, session.reconfigure(&too_small));

    var changed_shape = config;
    changed_shape.generation = 2;
    changed_shape.maximum_path_count += 1;
    try std.testing.expectEqual(ResultCode.invalid_argument, session.reconfigure(&changed_shape));

    var hot = config;
    hot.generation = 2;
    hot.maximum_future_skew_ms = 2_000;
    try std.testing.expectEqual(ResultCode.ok, session.reconfigure(&hot));
}

test "persisted import swaps atomically and rejects duplicate or drifted rows" {
    var config = explicitConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();

    const first_executable = "c:/portable/tool/a.exe";
    const first_root = "c:/portable/tool";
    const second_executable = "c:/portable/tool/bin/b.exe";
    const second_root = "c:/portable/tool/bin";
    const keys = first_executable ++ first_root ++ second_executable ++ second_root;
    const first_root_offset = first_executable.len;
    const second_executable_offset = first_root_offset + first_root.len;
    const second_root_offset = second_executable_offset + second_executable.len;
    var rows = [_]protocol.PersistedPathInput{
        persistedRow(10, 100, 1_000, 1_001, 1, 101, 201, 500, 0, first_executable.len, first_root_offset, first_root.len, 0),
        persistedRow(
            10,
            100,
            1_000,
            1_001,
            1,
            102,
            202,
            600,
            second_executable_offset,
            second_executable.len,
            second_root_offset,
            second_root.len,
            protocol.PathFlags.identity_confirmed | protocol.PathFlags.root_confirmed,
        ),
    };
    var input = importInput(&config, 1, 1, 1_000, rows.len, keys.len);
    try std.testing.expectEqual(ResultCode.ok, session.importPersisted(&input, &rows, keys));
    const snapshot = try captureSnapshot(session, &config, 1);
    try std.testing.expectEqual(@as(u32, 1), snapshot.output.registration_count);
    try std.testing.expectEqual(@as(u32, 2), snapshot.output.path_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.output.dirty_path_count);
    try std.testing.expectEqual(@as(u32, 2), snapshot.registrations[0].active_path_count);
    try std.testing.expectEqual(@as(u32, 1), snapshot.registrations[0].confirmed_root_count);

    rows[1].path_handle = rows[0].path_handle;
    input.import_generation = 2;
    input.operation_epoch = 2;
    try std.testing.expectEqual(ResultCode.invalid_argument, session.importPersisted(&input, &rows, keys));
    const after_failure = try captureSnapshot(session, &config, 2);
    try std.testing.expectEqual(@as(u32, 2), after_failure.output.path_count);
    try std.testing.expectEqual(@as(u64, 1), after_failure.output.import_generation);
}

test "atomic import never reuses a mutation version accepted by an older persistence plan" {
    var config = explicitConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    try expectObserve(session, &config, 1, 10, 100, 1_000, 1_001, 1, 101, 201, "c:/portable/tool/a.exe", "c:/portable/tool", 100, 0, .ok);
    const old_plan = try capturePlan(session, &config, 1, 4);
    try std.testing.expectEqual(@as(u64, 1), old_plan.operations[0].mutation_version);

    var import = importInput(&config, 1, 2, 1_000, 0, 0);
    try std.testing.expectEqual(ResultCode.ok, session.importPersisted(&import, &.{}, &.{}));
    try expectObserve(session, &config, 3, 10, 100, 1_000, 1_001, 1, 101, 201, "c:/portable/tool/a.exe", "c:/portable/tool", 200, 0, .ok);
    const new_plan = try capturePlan(session, &config, 2, 4);
    try std.testing.expectEqual(@as(u64, 2), new_plan.operations[0].mutation_version);

    const stale_feedback = feedbackFor(old_plan.operations[0]);
    var stale_input = feedbackInput(&config, 1, 1);
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session.applyPersistenceFeedback(&stale_input, &.{stale_feedback}),
    );
    const still_dirty = try capturePlan(session, &config, 3, 4);
    try std.testing.expectEqual(@as(u64, 2), still_dirty.operations[0].mutation_version);
}

test "observation metadata fan-out produces one exact dirty version per active path" {
    var config = explicitConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();

    try expectObserve(session, &config, 1, 10, 100, 1_000, 1_001, 1, 101, 201, "c:/portable/tool/a.exe", "c:/portable/tool", 100, 0, .ok);
    var first_plan = try capturePlan(session, &config, 1, 4);
    try std.testing.expectEqual(@as(u32, 1), first_plan.output.operation_count);
    try commitOperations(session, &config, 1, first_plan.operations[0..1]);

    try expectObserve(session, &config, 2, 10, 100, 1_000, 1_001, 1, 102, 201, "c:/portable/tool/b.exe", "c:/portable/tool", 200, 0, .ok);
    try expectObserve(session, &config, 3, 10, 200, 2_000, 2_001, 1, 101, 201, "c:/portable/tool/a.exe", "c:/portable/tool", 300, protocol.PathFlags.identity_confirmed, .ok);
    var plan = try capturePlan(session, &config, 2, 4);
    try std.testing.expectEqual(@as(u32, 2), plan.output.total_dirty_count);
    try std.testing.expectEqual(@as(u32, 2), plan.output.operation_count);
    try std.testing.expect(plan.operations[0].mutation_version < plan.operations[1].mutation_version);
    try std.testing.expectEqual(@as(u64, 200), plan.operations[0].catalog_entry_handle);
    try std.testing.expectEqual(@as(u64, 200), plan.operations[1].catalog_entry_handle);
    try std.testing.expectEqual(@as(u64, 2_000), plan.operations[0].display_name_handle);
    try std.testing.expectEqual(@as(u64, 2_000), plan.operations[1].display_name_handle);

    try commitOperations(session, &config, 2, plan.operations[0..1]);
    var stale = feedbackFor(plan.operations[1]);
    stale.mutation_version = plan.operations[0].mutation_version;
    var stale_input = feedbackInput(&config, 3, 1);
    try std.testing.expectEqual(ResultCode.stale_frame, session.applyPersistenceFeedback(&stale_input, &.{stale}));
    plan = try capturePlan(session, &config, 3, 4);
    try std.testing.expectEqual(@as(u32, 1), plan.output.operation_count);
    try std.testing.expectEqual(@as(u64, 101), plan.operations[0].path_handle);
    try commitOperations(session, &config, 4, plan.operations[0..1]);
    const clean = try capturePlan(session, &config, 4, 4);
    try std.testing.expectEqual(@as(u32, 0), clean.output.operation_count);
}

test "root confirmation only upgrades executable paths under the selected root" {
    var config = explicitConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    try expectObserve(session, &config, 1, 10, 100, 1_000, 1_001, 1, 101, 201, "c:/portable/tool/a/a.exe", "c:/portable/tool/a", 100, 0, .ok);
    try expectObserve(session, &config, 2, 10, 100, 1_000, 1_001, 1, 102, 202, "c:/portable/tool/b/b.exe", "c:/portable/tool/b", 200, 0, .ok);
    var confirmed_count: u32 = 0;
    var confirm = confirmRootInput(&config, 3, 10, 301, "c:/portable/tool/a".len);
    try std.testing.expectEqual(
        ResultCode.ok,
        session.confirmRoot(&confirm, "c:/portable/tool/a", &confirmed_count),
    );
    try std.testing.expectEqual(@as(u32, 1), confirmed_count);
    const snapshot = try captureSnapshot(session, &config, 1);
    try std.testing.expectEqual(@as(u32, 1), snapshot.registrations[0].confirmed_root_count);
    try std.testing.expect(
        (snapshot.registrations[0].flags & protocol.SnapshotFlags.requires_root_confirmation) != 0,
    );
    const first = findPathSnapshot(snapshot.paths[0..snapshot.output.path_output_count], 101).?;
    const second = findPathSnapshot(snapshot.paths[0..snapshot.output.path_output_count], 102).?;
    try std.testing.expect((first.flags & protocol.SnapshotFlags.root_confirmed) != 0);
    try std.testing.expectEqual(@as(u64, 301), first.root_handle);
    try std.testing.expectEqual(@as(u32, 0), second.flags & protocol.SnapshotFlags.root_confirmed);
}

test "missing path is removed only by exact persisted feedback and its slot is reusable" {
    var config = explicitConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    try expectObserve(session, &config, 1, 10, 100, 1_000, 1_001, 1, 101, 201, "c:/portable/tool/a.exe", "c:/portable/tool", 100, 0, .ok);
    var plan = try capturePlan(session, &config, 1, 4);
    try commitOperations(session, &config, 1, plan.operations[0..1]);

    var missing = markMissingInput(&config, 2, 10, 101);
    try std.testing.expectEqual(ResultCode.ok, session.markMissing(&missing));
    plan = try capturePlan(session, &config, 2, 4);
    try std.testing.expectEqual(@as(u32, protocol.PersistenceFlags.delete), plan.operations[0].flags & protocol.PersistenceFlags.delete);
    var wrong = feedbackFor(plan.operations[0]);
    wrong.mutation_version += 1;
    var wrong_input = feedbackInput(&config, 2, 1);
    try std.testing.expectEqual(ResultCode.stale_frame, session.applyPersistenceFeedback(&wrong_input, &.{wrong}));
    const before_commit = try captureSnapshot(session, &config, 1);
    try std.testing.expectEqual(@as(u32, 0), before_commit.output.path_count);
    try std.testing.expectEqual(@as(u32, 1), before_commit.output.dirty_path_count);
    try commitOperations(session, &config, 3, plan.operations[0..1]);
    var after_commit = try captureSnapshot(session, &config, 2);
    try std.testing.expectEqual(@as(u32, 0), after_commit.output.registration_count);
    try std.testing.expectEqual(@as(u32, 0), after_commit.output.dirty_path_count);

    try expectObserve(session, &config, 3, 20, 200, 2_000, 2_001, 2, 201, 401, "c:/portable/other/x.exe", "c:/portable/other", 300, 0, .ok);
    after_commit = try captureSnapshot(session, &config, 3);
    try std.testing.expectEqual(@as(u32, 1), after_commit.output.registration_count);
    try std.testing.expectEqual(@as(u32, 1), after_commit.output.path_count);
}

test "plan output limit partial feedback and paging remain explicit" {
    var config = explicitConfig();
    config.maximum_persistence_operation_count = 2;
    config.maximum_registration_snapshot_count = 1;
    config.maximum_path_snapshot_count = 1;
    const session = try state.Session.create(&config);
    defer session.destroy();
    try expectObserve(session, &config, 1, 10, 100, 1_000, 1_001, 1, 101, 201, "c:/portable/a/a.exe", "c:/portable/a", 100, 0, .ok);
    try expectObserve(session, &config, 2, 20, 200, 2_000, 2_001, 2, 201, 401, "c:/portable/b/b.exe", "c:/portable/b", 200, 0, .ok);
    try expectObserve(session, &config, 3, 30, 300, 3_000, 3_001, 3, 301, 601, "c:/portable/c/c.exe", "c:/portable/c", 300, 0, .ok);
    const plan = try capturePlan(session, &config, 1, 2);
    try std.testing.expectEqual(@as(u32, 2), plan.output.operation_count);
    try std.testing.expectEqual(@as(u32, 3), plan.output.total_dirty_count);
    try std.testing.expect((plan.output.flags & protocol.PlanOutputFlags.has_more) != 0);

    var registrations: RegistrationBuffer = std.mem.zeroes(RegistrationBuffer);
    var paths: PathBuffer = std.mem.zeroes(PathBuffer);
    var output: protocol.SnapshotOutput = std.mem.zeroes(protocol.SnapshotOutput);
    var input = snapshotInput(&config, 1, 0, 0, 1, 1);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&input, &registrations, &paths, &output));
    try std.testing.expectEqual(@as(u32, 1), output.registration_output_count);
    try std.testing.expectEqual(@as(u32, 1), output.path_output_count);
    try std.testing.expect((output.flags & protocol.SnapshotOutputFlags.registration_has_more) != 0);
    try std.testing.expect((output.flags & protocol.SnapshotOutputFlags.path_has_more) != 0);
}

test "committed mutation watermark never jumps across an older dirty operation" {
    var config = explicitConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    try expectObserve(session, &config, 1, 10, 100, 1_000, 1_001, 1, 101, 201, "c:/portable/tool/a.exe", "c:/portable/tool", 100, 0, .ok);
    try expectObserve(session, &config, 2, 10, 100, 1_000, 1_001, 1, 102, 201, "c:/portable/tool/b.exe", "c:/portable/tool", 200, 0, .ok);
    const plan = try capturePlan(session, &config, 1, 4);
    try std.testing.expectEqual(@as(u32, 2), plan.output.operation_count);

    var mixed_feedback = [_]protocol.PersistenceFeedback{
        feedbackFor(plan.operations[0]),
        feedbackFor(plan.operations[1]),
    };
    mixed_feedback[1].mutation_version += 10;
    var mixed_input = feedbackInput(&config, 1, mixed_feedback.len);
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session.applyPersistenceFeedback(&mixed_input, &mixed_feedback),
    );
    const unchanged = try capturePlan(session, &config, 2, 4);
    try std.testing.expectEqual(@as(u32, 2), unchanged.output.operation_count);

    try commitOperations(session, &config, 1, plan.operations[1..2]);
    var snapshot = try captureSnapshot(session, &config, 1);
    try std.testing.expectEqual(@as(u64, 0), snapshot.output.committed_mutation_version);
    try std.testing.expectEqual(@as(u32, 1), snapshot.output.dirty_path_count);

    try commitOperations(session, &config, 2, plan.operations[0..1]);
    snapshot = try captureSnapshot(session, &config, 2);
    try std.testing.expectEqual(@as(u64, 2), snapshot.output.committed_mutation_version);
    try std.testing.expectEqual(@as(u32, 0), snapshot.output.dirty_path_count);
}

test "mutation version exhaustion is visible and rejects new state without wrapping" {
    var config = explicitConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    session.next_mutation_version = std.math.maxInt(u64);
    try expectObserve(session, &config, 1, 10, 100, 1_000, 1_001, 1, 101, 201, "c:/portable/tool/a.exe", "c:/portable/tool", 100, 0, .ok);
    const plan = try capturePlan(session, &config, 1, 4);
    try std.testing.expectEqual(std.math.maxInt(u64), plan.operations[0].mutation_version);
    try std.testing.expect((plan.output.flags & protocol.PlanOutputFlags.mutation_exhausted) != 0);

    try expectObserve(session, &config, 2, 10, 100, 1_000, 1_001, 1, 102, 201, "c:/portable/tool/b.exe", "c:/portable/tool", 200, 0, .out_of_memory);
    const snapshot = try captureSnapshot(session, &config, 1);
    try std.testing.expectEqual(@as(u32, 1), snapshot.output.path_count);
    try std.testing.expect((snapshot.output.flags & protocol.SnapshotOutputFlags.mutation_exhausted) != 0);
}

test "time epochs handles packed keys reserved bytes and dirty outputs fail closed" {
    var config = explicitConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    try expectObserve(session, &config, 1, 10, 100, 1_000, 1_001, 1, 101, 201, "c:/portable/tool/a.exe", "c:/portable/tool", 100, 0, .ok);
    try expectObserve(session, &config, 1, 10, 100, 1_000, 1_001, 1, 101, 201, "c:/portable/tool/a.exe", "c:/portable/tool", 100, 0, .stale_frame);
    try expectObserve(session, &config, 2, 10, 100, 1_000, 1_001, 1, 101, 201, "c:/portable/tool/other.exe", "c:/portable/tool", 200, 0, .invalid_argument);
    try expectObserve(session, &config, 2, 10, 100, 1_000, 1_001, 1, 102, 201, "c:/portable/tool/b.exe", "c:/portable/tool", 12_000, 0, .abi_mismatch);

    var input = planInput(&config, 1, 4);
    var operations: OperationBuffer = std.mem.zeroes(OperationBuffer);
    var output: protocol.PersistencePlanOutput = std.mem.zeroes(protocol.PersistencePlanOutput);
    output.flags = 1;
    try std.testing.expectEqual(ResultCode.abi_mismatch, session.planPersistence(&input, &operations, &output));
    output = std.mem.zeroes(protocol.PersistencePlanOutput);
    try std.testing.expectEqual(ResultCode.ok, session.planPersistence(&input, &operations, &output));

    var invalid_config = config;
    invalid_config.reserved[0] = 1;
    try std.testing.expectError(error.InvalidConfiguration, state.Session.create(&invalid_config));
}

test "canonical path validation accepts drive and UNC roots while rejecting ambiguous segments" {
    try std.testing.expect(protocol.validCanonicalPath("c:/", 0, "c:/".len));
    try std.testing.expect(protocol.validCanonicalPath("c:/portable/tool.exe", 0, "c:/portable/tool.exe".len));
    try std.testing.expect(protocol.validCanonicalPath("//server/share", 0, "//server/share".len));
    try std.testing.expect(!protocol.validCanonicalPath("C:/portable/tool.exe", 0, "C:/portable/tool.exe".len));
    try std.testing.expect(!protocol.validCanonicalPath("c:/portable/../tool.exe", 0, "c:/portable/../tool.exe".len));
    try std.testing.expect(!protocol.validCanonicalPath("//server", 0, "//server".len));
    try std.testing.expect(protocol.isSameOrUnder("c:/portable/tool.exe", "c:/"));

    var config = explicitConfig();
    const executable = "c:/portable/tool.exe";
    const root = "c:/";
    const keys = executable ++ root;
    var input = observeInput(
        &config,
        1,
        std.math.maxInt(i64),
        std.math.maxInt(i64),
        10,
        100,
        1_000,
        1,
        101,
        201,
        executable.len,
        root.len,
        0,
    );
    try std.testing.expect(!protocol.validObserve(&input, &config, keys));
}

test "module local C ABI creates observes plans and destroys without root export" {
    var config = explicitConfig();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(@as(i32, 0), abi.rm_software_identity_portable_registry_create(&config, &handle));
    defer abi.rm_software_identity_portable_registry_destroy(handle);
    var capacity: protocol.Capacity = undefined;
    try std.testing.expectEqual(@as(i32, 0), abi.rm_software_identity_portable_registry_query_capacity(
        handle,
        &capacity,
        @sizeOf(protocol.Capacity),
    ));
    try std.testing.expectEqual(config.maximum_path_count, capacity.path_capacity);

    const executable = "c:/portable/tool/a.exe";
    const root = "c:/portable/tool";
    const keys = executable ++ root;
    var input = observeInput(&config, 1, 1_000, 100, 10, 100, 1_000, 1, 101, 201, executable.len, root.len, 0);
    try std.testing.expectEqual(@as(i32, 0), abi.rm_software_identity_portable_registry_observe(
        handle,
        &input,
        keys.ptr,
        keys.len,
    ));
    var plan = planInput(&config, 1, 4);
    var operations: OperationBuffer = std.mem.zeroes(OperationBuffer);
    var output: protocol.PersistencePlanOutput = std.mem.zeroes(protocol.PersistencePlanOutput);
    try std.testing.expectEqual(@as(i32, 0), abi.rm_software_identity_portable_registry_plan_persistence(
        handle,
        &plan,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(@as(u32, 1), output.operation_count);
}

const SnapshotCapture = struct {
    output: protocol.SnapshotOutput,
    registrations: RegistrationBuffer,
    paths: PathBuffer,
};

const PlanCapture = struct {
    output: protocol.PersistencePlanOutput,
    operations: OperationBuffer,
};

fn explicitConfig() protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = 1,
        .maximum_registration_count = 4,
        .maximum_path_count = 8,
        .maximum_persistence_operation_count = 4,
        .maximum_registration_snapshot_count = 4,
        .maximum_path_snapshot_count = 8,
        .maximum_executable_path_byte_count = 128,
        .maximum_root_path_byte_count = 128,
        .registration_index_capacity = 8,
        .path_index_capacity = 16,
        .maximum_future_skew_ms = 1_000,
        .flags = 0,
        .resident_byte_budget = 1_000_000,
        .reserved = .{ 0, 0, 0, 0, 0 },
    };
}

fn importInput(
    config: *const protocol.Config,
    import_generation: u64,
    operation_epoch: u64,
    command_utc_ms: i64,
    row_count: usize,
    key_byte_count: usize,
) protocol.ImportInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ImportInput),
        .configuration_generation = config.generation,
        .import_generation = import_generation,
        .operation_epoch = operation_epoch,
        .command_utc_ms = command_utc_ms,
        .row_count = @intCast(row_count),
        .key_byte_count = @intCast(key_byte_count),
        .valid_mask = protocol.ImportValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn persistedRow(
    software_handle: u64,
    catalog_entry_handle: u64,
    display_name_handle: u64,
    software_kind_handle: u64,
    unused: u64,
    path_handle: u64,
    root_handle: u64,
    first_observed: i64,
    executable_offset: usize,
    executable_length: usize,
    root_offset: usize,
    root_length: usize,
    flags: u32,
) protocol.PersistedPathInput {
    _ = unused;
    return .{
        .struct_size = @sizeOf(protocol.PersistedPathInput),
        .flags = flags,
        .software_handle = software_handle,
        .catalog_entry_handle = catalog_entry_handle,
        .display_name_handle = display_name_handle,
        .software_kind_handle = software_kind_handle,
        .path_handle = path_handle,
        .root_handle = root_handle,
        .first_observed_utc_ms = first_observed,
        .executable_path_offset = @intCast(executable_offset),
        .executable_path_length = @intCast(executable_length),
        .root_path_offset = @intCast(root_offset),
        .root_path_length = @intCast(root_length),
        .reserved = .{ 0, 0 },
    };
}

fn observeInput(
    config: *const protocol.Config,
    operation_epoch: u64,
    command_utc_ms: i64,
    observed_at_utc_ms: i64,
    software_handle: u64,
    catalog_entry_handle: u64,
    display_name_handle: u64,
    software_kind_handle: u64,
    path_handle: u64,
    root_handle: u64,
    executable_length: usize,
    root_length: usize,
    path_flags: u32,
) protocol.ObserveInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ObserveInput),
        .configuration_generation = config.generation,
        .operation_epoch = operation_epoch,
        .command_utc_ms = command_utc_ms,
        .observed_at_utc_ms = observed_at_utc_ms,
        .software_handle = software_handle,
        .catalog_entry_handle = catalog_entry_handle,
        .display_name_handle = display_name_handle,
        .software_kind_handle = software_kind_handle,
        .path_handle = path_handle,
        .root_handle = root_handle,
        .executable_path_offset = 0,
        .executable_path_length = @intCast(executable_length),
        .root_path_offset = @intCast(executable_length),
        .root_path_length = @intCast(root_length),
        .path_flags = path_flags,
        .reserved_u32 = 0,
        .valid_mask = protocol.ObserveValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn expectObserve(
    session: *state.Session,
    config: *const protocol.Config,
    operation_epoch: u64,
    software_handle: u64,
    catalog_entry_handle: u64,
    display_name_handle: u64,
    software_kind_handle: u64,
    unused: u64,
    path_handle: u64,
    root_handle: u64,
    executable: []const u8,
    root: []const u8,
    observed_at_utc_ms: i64,
    path_flags: u32,
    expected: ResultCode,
) !void {
    _ = unused;
    var key_bytes: [256]u8 = undefined;
    @memcpy(key_bytes[0..executable.len], executable);
    @memcpy(key_bytes[executable.len .. executable.len + root.len], root);
    var input = observeInput(
        config,
        operation_epoch,
        1_000,
        observed_at_utc_ms,
        software_handle,
        catalog_entry_handle,
        display_name_handle,
        software_kind_handle,
        path_handle,
        root_handle,
        executable.len,
        root.len,
        path_flags,
    );
    try std.testing.expectEqual(expected, session.observe(&input, key_bytes[0 .. executable.len + root.len]));
}

fn confirmRootInput(
    config: *const protocol.Config,
    operation_epoch: u64,
    software_handle: u64,
    root_handle: u64,
    root_length: usize,
) protocol.ConfirmRootInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ConfirmRootInput),
        .configuration_generation = config.generation,
        .operation_epoch = operation_epoch,
        .command_utc_ms = 1_000,
        .software_handle = software_handle,
        .root_handle = root_handle,
        .root_path_offset = 0,
        .root_path_length = @intCast(root_length),
        .valid_mask = protocol.ConfirmRootValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn markMissingInput(
    config: *const protocol.Config,
    operation_epoch: u64,
    software_handle: u64,
    path_handle: u64,
) protocol.MarkMissingInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.MarkMissingInput),
        .configuration_generation = config.generation,
        .operation_epoch = operation_epoch,
        .command_utc_ms = 1_000,
        .software_handle = software_handle,
        .path_handle = path_handle,
        .valid_mask = protocol.MarkMissingValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn planInput(config: *const protocol.Config, plan_epoch: u64, maximum_count: u32) protocol.PlanPersistenceInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PlanPersistenceInput),
        .configuration_generation = config.generation,
        .plan_epoch = plan_epoch,
        .maximum_operation_count = maximum_count,
        .reserved_u32 = 0,
        .valid_mask = protocol.PlanValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn capturePlan(
    session: *state.Session,
    config: *const protocol.Config,
    plan_epoch: u64,
    maximum_count: u32,
) !PlanCapture {
    var capture: PlanCapture = std.mem.zeroes(PlanCapture);
    var input = planInput(config, plan_epoch, maximum_count);
    try std.testing.expectEqual(ResultCode.ok, session.planPersistence(&input, &capture.operations, &capture.output));
    return capture;
}

fn feedbackInput(
    config: *const protocol.Config,
    feedback_epoch: u64,
    feedback_count: usize,
) protocol.PersistenceFeedbackInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PersistenceFeedbackInput),
        .configuration_generation = config.generation,
        .feedback_epoch = feedback_epoch,
        .feedback_count = @intCast(feedback_count),
        .reserved_u32 = 0,
        .valid_mask = protocol.FeedbackValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn feedbackFor(operation: protocol.PersistenceOperation) protocol.PersistenceFeedback {
    return .{
        .struct_size = @sizeOf(protocol.PersistenceFeedback),
        .reserved_u32 = 0,
        .mutation_version = operation.mutation_version,
        .software_handle = operation.software_handle,
        .path_handle = operation.path_handle,
        .reserved = .{ 0, 0 },
    };
}

fn commitOperations(
    session: *state.Session,
    config: *const protocol.Config,
    feedback_epoch: u64,
    operations: []const protocol.PersistenceOperation,
) !void {
    var feedbacks: [4]protocol.PersistenceFeedback = undefined;
    for (operations, 0..) |operation, index| feedbacks[index] = feedbackFor(operation);
    var input = feedbackInput(config, feedback_epoch, operations.len);
    try std.testing.expectEqual(ResultCode.ok, session.applyPersistenceFeedback(&input, feedbacks[0..operations.len]));
}

fn snapshotInput(
    config: *const protocol.Config,
    snapshot_epoch: u64,
    registration_cursor: u32,
    path_cursor: u32,
    maximum_registrations: u32,
    maximum_paths: u32,
) protocol.SnapshotInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.SnapshotInput),
        .configuration_generation = config.generation,
        .snapshot_epoch = snapshot_epoch,
        .registration_cursor = registration_cursor,
        .path_cursor = path_cursor,
        .maximum_registration_count = maximum_registrations,
        .maximum_path_count = maximum_paths,
        .valid_mask = protocol.SnapshotValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn captureSnapshot(session: *state.Session, config: *const protocol.Config, snapshot_epoch: u64) !SnapshotCapture {
    var capture: SnapshotCapture = std.mem.zeroes(SnapshotCapture);
    var input = snapshotInput(
        config,
        snapshot_epoch,
        0,
        0,
        config.maximum_registration_snapshot_count,
        config.maximum_path_snapshot_count,
    );
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(
        &input,
        &capture.registrations,
        &capture.paths,
        &capture.output,
    ));
    return capture;
}

fn findPathSnapshot(paths: []const protocol.PathSnapshot, path_handle: u64) ?protocol.PathSnapshot {
    for (paths) |path| if (path.path_handle == path_handle) return path;
    return null;
}
