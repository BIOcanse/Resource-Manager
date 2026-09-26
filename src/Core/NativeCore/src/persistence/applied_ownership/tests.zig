const std = @import("std");
const abi = @import("abi.zig");
const checksum = @import("checksum.zig");
const codec = @import("codec.zig");
const corpus = @import("golden_corpus.zig");
const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const state = @import("state.zig");

const maximum_test_image = @sizeOf(protocol.ImageHeader) + 8 * @sizeOf(protocol.Record);

test "ABI sizes and canonical offsets are frozen" {
    try std.testing.expectEqual(@as(u32, 0x0002_0000), protocol.abi_version);
    try std.testing.expectEqual(@as(usize, 96), @sizeOf(protocol.CreateConfig));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(protocol.OpenConfig));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.Capacity));
    try std.testing.expectEqual(@as(usize, 320), @sizeOf(protocol.Record));
    try std.testing.expectEqual(@as(usize, 448), @sizeOf(protocol.TransitionInput));
    try std.testing.expectEqual(@as(usize, 128), @sizeOf(protocol.ImageHeader));
    try std.testing.expectEqual(@as(usize, 40), @offsetOf(protocol.Record, "original_binding"));
    try std.testing.expectEqual(@as(usize, 200), @offsetOf(protocol.Record, "payload"));
    try std.testing.expectEqual(@as(usize, 232), @offsetOf(protocol.Record, "current_grades"));
    try std.testing.expectEqual(@as(usize, 256), @offsetOf(protocol.Record, "record_revision"));
    try std.testing.expectEqual(@as(usize, 64), codec.checksum_offset);
}

test "process and adapter ownership promote into one exact snapshot" {
    var config = corpus.createConfig(4, 8);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var process_input = corpus.processPromote(1, 1, 100);
    var adapter_input = corpus.adapterPromote(2, 2, 110);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &process_input));
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &adapter_input));

    var header = std.mem.zeroes(protocol.SnapshotHeader);
    var records = [_]protocol.Record{std.mem.zeroes(protocol.Record)} ** 4;
    try std.testing.expectEqual(protocol.Status.ok, session_module.snapshot(session, &header, &records));
    try std.testing.expectEqual(@as(u32, 2), header.entry_count);
    try std.testing.expectEqual(@as(u64, 3), header.ledger_revision);
    try std.testing.expect(protocol.primaryLessThan(&records[0].primary, &records[1].primary));
    try std.testing.expectEqual(@intFromEnum(protocol.Scope.process), records[0].primary.scope);
    try std.testing.expectEqual(@intFromEnum(protocol.Scope.adapter), records[1].primary.scope);
}

test "CPU and memory process ownership coexist by target and restore independently" {
    var config = corpus.createConfig(4, 8);
    const session = try state.Session.create(&config);
    defer session.destroy();

    var cpu = corpus.processPromote(1, 1, 100);
    var memory = memoryPromote(1, 2, 3, 110);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &cpu));
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &memory));

    var header = std.mem.zeroes(protocol.SnapshotHeader);
    var records = [_]protocol.Record{std.mem.zeroes(protocol.Record)} ** 4;
    try std.testing.expectEqual(protocol.Status.ok, session_module.snapshot(session, &header, &records));
    try std.testing.expectEqual(@as(u32, 2), header.entry_count);
    try std.testing.expectEqual(cpu.primary.process_id, memory.primary.process_id);
    try std.testing.expectEqual(cpu.primary.process_start_key, memory.primary.process_start_key);
    try std.testing.expect(cpu.primary.target_id != memory.primary.target_id);

    var cpu_record = try getRecord(session, &cpu.primary);
    var memory_record = try getRecord(session, &memory.primary);
    try std.testing.expectEqual(protocol.GradeValid.process, cpu_record.current_grades.valid_mask);
    try std.testing.expectEqual(protocol.GradeValid.memory, memory_record.current_grades.valid_mask);
    try std.testing.expectEqual(@as(i32, 3), memory_record.current_grades.process_grade);

    var lower = memoryTransition(&memory_record, 3, 1, 120);
    try std.testing.expectEqual(protocol.Status.ok, session_module.transition(session, &lower));
    memory_record = try getRecord(session, &memory.primary);
    try std.testing.expectEqual(@as(i32, 1), memory_record.current_grades.process_grade);
    cpu_record = try getRecord(session, &cpu.primary);
    try std.testing.expectEqual(@intFromEnum(protocol.ProcessGrade.level_1), cpu_record.current_grades.process_grade);

    var restore = memoryTransition(&memory_record, 4, 0, 130);
    try std.testing.expectEqual(protocol.Status.ok, session_module.transition(session, &restore));
    var absent = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.no_data, session_module.get(session, &memory.primary, &absent));
    _ = try getRecord(session, &cpu.primary);
}

test "memory ownership rejects wrong mixed tags and invalid priorities" {
    var config = corpus.createConfig(2, 4);
    const session = try state.Session.create(&config);
    defer session.destroy();

    var wrong_domain = memoryPromote(1, 1, 3, 100);
    wrong_domain.original_binding.domain_mask = protocol.DomainBits.process;
    try std.testing.expectEqual(protocol.Status.identity_mismatch, session_module.promote(session, &wrong_domain));

    var mixed = memoryPromote(2, 1, 3, 100);
    mixed.original_binding.grade_valid_mask |= protocol.GradeValid.process;
    mixed.current_grades.valid_mask |= protocol.GradeValid.process;
    try std.testing.expectEqual(protocol.Status.identity_mismatch, session_module.promote(session, &mixed));

    var invalid_priority = memoryPromote(3, 1, 6, 100);
    try std.testing.expectEqual(protocol.Status.identity_mismatch, session_module.promote(session, &invalid_priority));
    try std.testing.expectEqual(@as(u64, 1), session.ledger_revision);
}

test "grade update preserves original binding and payload and exact remove releases ownership" {
    var config = corpus.createConfig(2, 4);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var promote_input = corpus.processPromote(1, 1, 100);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &promote_input));
    var record = try getRecord(session, &promote_input.primary);
    const original_binding = record.original_binding;
    const original_payload = record.payload;
    var transition = corpus.processTransition(&record, 2, .level_2, 120);
    try std.testing.expectEqual(protocol.Status.ok, session_module.transition(session, &transition));
    record = try getRecord(session, &promote_input.primary);
    try std.testing.expectEqual(@as(u64, 2), record.record_revision);
    try std.testing.expectEqual(@intFromEnum(protocol.ProcessGrade.level_2), record.current_grades.process_grade);
    try std.testing.expect(std.meta.eql(original_binding, record.original_binding));
    try std.testing.expect(std.meta.eql(original_payload, record.payload));
    var remove = corpus.remove(&record, 3, 130);
    try std.testing.expectEqual(protocol.Status.ok, session_module.remove(session, &remove));
    var absent = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.no_data, session_module.get(session, &promote_input.primary, &absent));
}

test "process id reuse during remove never deletes the replacement incarnation" {
    var config = corpus.createConfig(2, 4);
    const session = try state.Session.create(&config);
    defer session.destroy();

    var old = corpus.processPromote(1, 1, 100);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &old));
    const old_record = try getRecord(session, &old.primary);
    var stale_remove = corpus.remove(&old_record, 2, 120);

    var replacement = corpus.processPromote(2, 2, 110);
    replacement.primary.software_id = old.primary.software_id;
    replacement.primary.process_id = old.primary.process_id;
    replacement.original_binding.action_identity.software_id = old.primary.software_id;
    replacement.original_binding.action_identity.process_id = old.primary.process_id;
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &replacement));

    try std.testing.expectEqual(
        protocol.Status.stale_revision,
        session_module.remove(session, &stale_remove),
    );
    _ = try getRecord(session, &old.primary);
    _ = try getRecord(session, &replacement.primary);

    var current_remove = corpus.remove(&old_record, 3, 130);
    try std.testing.expectEqual(protocol.Status.ok, session_module.remove(session, &current_remove));
    var absent = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.no_data, session_module.get(session, &old.primary, &absent));
    const replacement_record = try getRecord(session, &replacement.primary);
    try std.testing.expectEqual(old.primary.process_id, replacement_record.primary.process_id);
    try std.testing.expect(replacement_record.primary.process_start_key != old.primary.process_start_key);
}

test "adapter CPU then GPU transition merges domains without changing original payload" {
    var config = corpus.createConfig(2, 4);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var promote_input = corpus.adapterCpuPromote(1, 1, 100);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &promote_input));
    var record = try getRecord(session, &promote_input.primary);
    const original_binding = record.original_binding;
    const original_payload = record.payload;
    var transition_binding = record.original_binding;
    transition_binding.journal_instance_low += 1;
    transition_binding.action_identity.plan_epoch += 1;
    transition_binding.action_identity.action_id += 1;
    transition_binding.domain_mask = protocol.DomainBits.gpu;
    transition_binding.grade_valid_mask = protocol.GradeValid.gpu;
    transition_binding.cpu_from_grade = 0;
    transition_binding.cpu_to_grade = 0;
    transition_binding.gpu_from_grade = @intFromEnum(protocol.AdapterGrade.normal);
    transition_binding.gpu_to_grade = @intFromEnum(protocol.AdapterGrade.freeze);
    var transition = std.mem.zeroes(protocol.TransitionInput);
    try std.testing.expectEqual(
        protocol.Status.ok,
        session_module.planTransition(
            session,
            &record.primary,
            &transition_binding,
            &record.payload,
            &transition,
        ),
    );
    try std.testing.expectEqual(@as(u64, 2), transition.expected_ledger_revision);
    try std.testing.expectEqual(
        protocol.GradeValid.cpu | protocol.GradeValid.gpu,
        transition.new_current_grades.valid_mask,
    );
    transition.updated_at_utc_ms = 120;
    try std.testing.expectEqual(protocol.Status.ok, session_module.transition(session, &transition));
    record = try getRecord(session, &promote_input.primary);
    try std.testing.expectEqual(protocol.GradeValid.cpu | protocol.GradeValid.gpu, record.current_grades.valid_mask);
    try std.testing.expectEqual(@intFromEnum(protocol.AdapterGrade.optimize), record.current_grades.cpu_grade);
    try std.testing.expectEqual(@intFromEnum(protocol.AdapterGrade.freeze), record.current_grades.gpu_grade);
    try std.testing.expect(std.meta.eql(original_binding, record.original_binding));
    try std.testing.expect(std.meta.eql(original_payload, record.payload));
}

test "adapter composite transition updates one domain and drops one domain exactly" {
    var config = corpus.createConfig(2, 4);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var promote_input = corpus.adapterPromote(1, 1, 100);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &promote_input));
    var record = try getRecord(session, &promote_input.primary);
    var change_cpu = corpus.adapterApplyTransition(
        &record,
        2,
        protocol.DomainBits.cpu,
        .extreme,
        .normal,
        corpus.adapterGrades(.extreme, .freeze),
        120,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.transition(session, &change_cpu));
    record = try getRecord(session, &promote_input.primary);
    var restore_cpu = corpus.adapterRestoreTransition(
        &record,
        3,
        protocol.DomainBits.cpu,
        .{
            .valid_mask = protocol.GradeValid.gpu,
            .reserved_u32 = 0,
            .process_grade = 0,
            .cpu_grade = 0,
            .gpu_grade = @intFromEnum(protocol.AdapterGrade.freeze),
            .reserved_i32 = 0,
        },
        140,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.transition(session, &restore_cpu));
    record = try getRecord(session, &promote_input.primary);
    try std.testing.expectEqual(protocol.GradeValid.gpu, record.current_grades.valid_mask);
    try std.testing.expectEqual(@as(i32, 0), record.current_grades.cpu_grade);
    try std.testing.expectEqual(@intFromEnum(protocol.AdapterGrade.freeze), record.current_grades.gpu_grade);
}

test "transition to no owned domains removes record atomically" {
    var config = corpus.createConfig(2, 4);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var promote_input = corpus.processPromote(1, 1, 100);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &promote_input));
    const record = try getRecord(session, &promote_input.primary);
    var transition = corpus.processTransition(&record, 2, .normal, 120);
    try std.testing.expectEqual(protocol.Status.ok, session_module.transition(session, &transition));
    var absent = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(
        protocol.Status.no_data,
        session_module.get(session, &promote_input.primary, &absent),
    );
    try std.testing.expectEqual(@as(u64, 3), session.ledger_revision);
}

test "adapter transition rejects unrelated domain mutation and payload drift" {
    var config = corpus.createConfig(2, 4);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var promote_input = corpus.adapterCpuPromote(1, 1, 100);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &promote_input));
    const record = try getRecord(session, &promote_input.primary);
    var hidden_cpu_change = corpus.adapterApplyTransition(
        &record,
        2,
        protocol.DomainBits.gpu,
        .normal,
        .freeze,
        corpus.adapterGrades(.extreme, .freeze),
        120,
    );
    try std.testing.expectEqual(
        protocol.Status.grade_mismatch,
        session_module.transition(session, &hidden_cpu_change),
    );
    var payload_drift = corpus.adapterApplyTransition(
        &record,
        2,
        protocol.DomainBits.gpu,
        .normal,
        .freeze,
        corpus.adapterGrades(.optimize, .freeze),
        120,
    );
    payload_drift.transition_payload.digest_high += 1;
    try std.testing.expectEqual(
        protocol.Status.payload_mismatch,
        session_module.transition(session, &payload_drift),
    );
    const unchanged = try getRecord(session, &promote_input.primary);
    try std.testing.expect(std.meta.eql(record, unchanged));
}

test "duplicate primary and durable payload ownership are rejected without mutation" {
    var config = corpus.createConfig(4, 8);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var first = corpus.processPromote(1, 1, 100);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &first));
    var duplicate_primary = first;
    duplicate_primary.expected_ledger_revision = 2;
    duplicate_primary.original_binding.action_identity.action_id += 100;
    try std.testing.expectEqual(protocol.Status.duplicate_identity, session_module.promote(session, &duplicate_primary));
    var duplicate_payload = corpus.processPromote(2, 2, 110);
    duplicate_payload.original_binding.journal_instance_low = first.original_binding.journal_instance_low;
    duplicate_payload.original_binding.journal_instance_high = first.original_binding.journal_instance_high;
    duplicate_payload.payload = first.payload;
    try std.testing.expectEqual(protocol.Status.duplicate_payload, session_module.promote(session, &duplicate_payload));
    try std.testing.expectEqual(@as(u64, 2), session.ledger_revision);
    try std.testing.expectEqual(@as(u32, 1), session.active.count);
}

test "ledger record identity grade and payload CAS drift fail closed" {
    var config = corpus.createConfig(2, 4);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var promote_input = corpus.processPromote(1, 1, 100);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &promote_input));
    const record = try getRecord(session, &promote_input.primary);

    var stale_ledger = corpus.processTransition(&record, 1, .level_2, 120);
    try std.testing.expectEqual(protocol.Status.stale_revision, session_module.transition(session, &stale_ledger));
    var stale_record = corpus.processTransition(&record, 2, .level_2, 120);
    stale_record.cas.expected_record_revision += 1;
    try std.testing.expectEqual(protocol.Status.stale_revision, session_module.transition(session, &stale_record));
    var action_drift = corpus.processTransition(&record, 2, .level_2, 120);
    action_drift.cas.original_action_identity.plan_epoch += 1;
    try std.testing.expectEqual(protocol.Status.identity_mismatch, session_module.transition(session, &action_drift));
    var payload_drift = corpus.processTransition(&record, 2, .level_2, 120);
    payload_drift.cas.payload.digest_high += 1;
    try std.testing.expectEqual(protocol.Status.payload_mismatch, session_module.transition(session, &payload_drift));
    var grade_drift = corpus.processTransition(&record, 2, .level_2, 120);
    grade_drift.cas.expected_current_grades = corpus.processGrades(.level_3);
    try std.testing.expectEqual(protocol.Status.grade_mismatch, session_module.transition(session, &grade_drift));
    try std.testing.expectEqual(@as(u64, 2), session.ledger_revision);
}

test "explicit capacity resident and revision limits never partially mutate" {
    var invalid = corpus.createConfig(2, 3);
    try std.testing.expectError(error.InvalidConfiguration, state.Session.create(&invalid));
    var insufficient = corpus.createConfig(2, 4);
    insufficient.maximum_resident_bytes = 1;
    try std.testing.expectError(error.ResidentBudgetExceeded, state.Session.create(&insufficient));

    var config = corpus.createConfig(1, 1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var first = corpus.processPromote(1, 1, 100);
    var second = corpus.processPromote(2, 2, 110);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &first));
    try std.testing.expectEqual(protocol.Status.capacity_full, session_module.promote(session, &second));
    const record = try getRecord(session, &first.primary);
    session.ledger_revision = std.math.maxInt(u64);
    var transition = corpus.processTransition(&record, std.math.maxInt(u64), .level_2, 120);
    try std.testing.expectEqual(protocol.Status.revision_exhausted, session_module.transition(session, &transition));
    try std.testing.expectEqual(@intFromEnum(protocol.ProcessGrade.level_1), session.active.records[0].current_grades.process_grade);
}

test "canonical image is insertion-order independent and restarts exactly" {
    var config = corpus.createConfig(4, 8);
    const left = try state.Session.create(&config);
    defer left.destroy();
    const right = try state.Session.create(&config);
    defer right.destroy();
    var left_two = corpus.adapterPromote(2, 1, 110);
    var left_one = corpus.processPromote(1, 2, 100);
    var right_one = corpus.processPromote(1, 1, 100);
    var right_two = corpus.adapterPromote(2, 2, 110);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(left, &left_two));
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(left, &left_one));
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(right, &right_one));
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(right, &right_two));
    var left_image: [maximum_test_image]u8 = undefined;
    var right_image: [maximum_test_image]u8 = undefined;
    var left_written: u64 = 0;
    var right_written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(left, &left_image, &left_written));
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(right, &right_image, &right_written));
    try std.testing.expectEqual(left_written, right_written);
    try std.testing.expectEqualSlices(u8, left_image[0..left_written], right_image[0..right_written]);

    var open_config = corpus.openConfig(4, 8);
    var reopened: ?*state.Session = null;
    try std.testing.expectEqual(protocol.Status.ok, session_module.openExisting(
        &open_config,
        left_image[0..left_written],
        &reopened,
    ));
    defer reopened.?.destroy();
    try std.testing.expectEqual(@as(u64, 3), reopened.?.ledger_revision);
    try std.testing.expectEqual(@as(u32, 2), reopened.?.active.count);
    try std.testing.expectEqual(protocol.Status.stale_revision, session_module.decodeReplace(
        reopened.?,
        left_image[0..left_written],
    ));
}

test "original recovery budget and atomic binding survive canonical restart exactly" {
    var config = corpus.createConfig(4, 8);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var first = corpus.processPromote(1, 1, 100);
    first.original_binding.atomic_group_id = 0x4444;
    first.original_binding.group_member_index = 0;
    first.original_binding.group_member_count = 2;
    first.original_binding.maximum_recovery_attempts = 9;
    first.original_binding.recovery_deadline_utc_ms = 40_000;
    var second = corpus.processPromote(2, 2, 100);
    second.original_binding.atomic_group_id = 0x4444;
    second.original_binding.group_member_index = 1;
    second.original_binding.group_member_count = 2;
    second.original_binding.maximum_recovery_attempts = 9;
    second.original_binding.recovery_deadline_utc_ms = 40_000;
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &first));
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &second));
    var image: [maximum_test_image]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(session, &image, &written));
    var open_config = corpus.openConfig(4, 8);
    var reopened: ?*state.Session = null;
    try std.testing.expectEqual(protocol.Status.ok, session_module.openExisting(
        &open_config,
        image[0..written],
        &reopened,
    ));
    defer reopened.?.destroy();
    const first_record = try getRecord(reopened.?, &first.primary);
    const second_record = try getRecord(reopened.?, &second.primary);
    try std.testing.expectEqual(@as(u64, 0x4444), first_record.original_binding.atomic_group_id);
    try std.testing.expectEqual(@as(u32, 2), first_record.original_binding.group_member_count);
    try std.testing.expectEqual(@as(u32, 1), second_record.original_binding.group_member_index);
    try std.testing.expectEqual(@as(u32, 9), second_record.original_binding.maximum_recovery_attempts);
    try std.testing.expectEqual(@as(u64, 40_000), second_record.original_binding.recovery_deadline_utc_ms);
}

test "decode rejects identity grade payload and duplicate payload drift" {
    var config = corpus.createConfig(4, 8);
    const source = try state.Session.create(&config);
    defer source.destroy();
    var first = corpus.processPromote(1, 1, 100);
    var second = corpus.adapterPromote(2, 2, 110);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(source, &first));
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(source, &second));
    var image: [maximum_test_image]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(source, &image, &written));
    const first_offset = @sizeOf(protocol.ImageHeader);
    const second_offset = first_offset + @sizeOf(protocol.Record);
    var open_config = corpus.openConfig(4, 8);

    var identity_drift = image;
    codec.writeU64(identity_drift[0..written], first_offset + 40 + 16 + 32, first.primary.target_id + 1);
    rewriteChecksum(identity_drift[0..written]);
    try expectOpenStatus(&open_config, identity_drift[0..written], .identity_mismatch);

    var grade_drift = image;
    codec.writeI32(grade_drift[0..written], first_offset + 232 + 8, @intFromEnum(protocol.ProcessGrade.normal));
    rewriteChecksum(grade_drift[0..written]);
    try expectOpenStatus(&open_config, grade_drift[0..written], .grade_mismatch);

    var unreachable_original_apply = image;
    const adapter_cpu_to_offset = second_offset +
        @offsetOf(protocol.Record, "original_binding") +
        @offsetOf(protocol.OriginalBinding, "cpu_to_grade");
    codec.writeI32(
        unreachable_original_apply[0..written],
        adapter_cpu_to_offset,
        @intFromEnum(protocol.AdapterGrade.normal),
    );
    rewriteChecksum(unreachable_original_apply[0..written]);
    try expectOpenStatus(
        &open_config,
        unreachable_original_apply[0..written],
        .identity_mismatch,
    );

    var payload_drift = image;
    codec.writeU32(payload_drift[0..written], first_offset + 200, 0);
    rewriteChecksum(payload_drift[0..written]);
    try expectOpenStatus(&open_config, payload_drift[0..written], .payload_mismatch);

    var duplicate_payload = image;
    codec.writeU64(duplicate_payload[0..written], second_offset + 40, first.original_binding.journal_instance_low);
    codec.writeU64(duplicate_payload[0..written], second_offset + 48, first.original_binding.journal_instance_high);
    @memcpy(
        duplicate_payload[second_offset + 200 .. second_offset + 232],
        duplicate_payload[first_offset + 200 .. first_offset + 232],
    );
    rewriteChecksum(duplicate_payload[0..written]);
    try expectOpenStatus(&open_config, duplicate_payload[0..written], .duplicate_payload);
}

test "corrupt truncated reserved and noncanonical images preserve live bank" {
    var config = corpus.createConfig(4, 8);
    const source = try state.Session.create(&config);
    defer source.destroy();
    var one = corpus.processPromote(1, 1, 100);
    var two = corpus.adapterPromote(2, 2, 110);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(source, &one));
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(source, &two));
    var image: [maximum_test_image]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(source, &image, &written));

    const target = try state.Session.create(&config);
    defer target.destroy();
    var existing = corpus.processPromote(3, 1, 90);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(target, &existing));
    const original = try getRecord(target, &existing.primary);
    try std.testing.expectEqual(protocol.Status.truncated_image, session_module.decodeReplace(target, image[0 .. written - 1]));
    var corrupt = image;
    corrupt[@sizeOf(protocol.ImageHeader) + 10] ^= 0x80;
    try std.testing.expectEqual(protocol.Status.checksum_mismatch, session_module.decodeReplace(target, corrupt[0..written]));
    var reserved = image;
    codec.writeU64(reserved[0..written], @sizeOf(protocol.ImageHeader) + 288, 1);
    rewriteChecksum(reserved[0..written]);
    try std.testing.expectEqual(protocol.Status.abi_mismatch, session_module.decodeReplace(target, reserved[0..written]));
    var unordered = image;
    const first_offset = @sizeOf(protocol.ImageHeader);
    const second_offset = first_offset + @sizeOf(protocol.Record);
    var temporary: [@sizeOf(protocol.Record)]u8 = undefined;
    @memcpy(&temporary, unordered[first_offset..second_offset]);
    @memcpy(unordered[first_offset..second_offset], unordered[second_offset .. second_offset + @sizeOf(protocol.Record)]);
    @memcpy(unordered[second_offset .. second_offset + @sizeOf(protocol.Record)], &temporary);
    rewriteChecksum(unordered[0..written]);
    try std.testing.expectEqual(protocol.Status.non_canonical_image, session_module.decodeReplace(target, unordered[0..written]));
    const after = try getRecord(target, &existing.primary);
    try std.testing.expect(std.meta.eql(original, after));
    try std.testing.expectEqual(@as(u32, 1), target.active.count);
}

test "hot mutations snapshot encode and staging decode allocate nothing" {
    var backing: [64 * 1024]u8 = undefined;
    var fixed = std.heap.FixedBufferAllocator.init(&backing);
    var config = corpus.createConfig(2, 4);
    const session = try state.Session.createWithAllocator(&config, fixed.allocator());
    defer session.destroy();
    const decode_target = try state.Session.createWithAllocator(&config, fixed.allocator());
    defer decode_target.destroy();
    const allocation_mark = fixed.end_index;
    var promote_input = corpus.processPromote(1, 1, 100);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(session, &promote_input));
    var record = try getRecord(session, &promote_input.primary);
    var transition = corpus.processTransition(&record, 2, .level_2, 120);
    try std.testing.expectEqual(protocol.Status.ok, session_module.transition(session, &transition));
    record = try getRecord(session, &promote_input.primary);
    var header = std.mem.zeroes(protocol.SnapshotHeader);
    var records = [_]protocol.Record{std.mem.zeroes(protocol.Record)} ** 2;
    try std.testing.expectEqual(protocol.Status.ok, session_module.snapshot(session, &header, &records));
    var image: [maximum_test_image]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(session, &image, &written));
    var remove = corpus.remove(&record, 3, 130);
    try std.testing.expectEqual(protocol.Status.ok, session_module.remove(session, &remove));
    try std.testing.expectEqual(allocation_mark, fixed.end_index);

    const source = try state.Session.create(&config);
    defer source.destroy();
    var source_promote = corpus.adapterPromote(2, 1, 140);
    try std.testing.expectEqual(protocol.Status.ok, session_module.promote(source, &source_promote));
    written = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(source, &image, &written));
    try std.testing.expectEqual(protocol.Status.ok, session_module.decodeReplace(decode_target, image[0..written]));
    try std.testing.expectEqual(allocation_mark, fixed.end_index);
}

test "module-local C ABI traverses create promote snapshot encode open and remove" {
    var config = corpus.createConfig(2, 4);
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(@intFromEnum(protocol.Status.ok), abi.rm_applied_ownership_create_new(&config, &handle));
    defer abi.rm_applied_ownership_destroy(handle);
    var promote_input = corpus.processPromote(1, 1, 100);
    try std.testing.expectEqual(@intFromEnum(protocol.Status.ok), abi.rm_applied_ownership_promote(handle, &promote_input));
    var header = std.mem.zeroes(protocol.SnapshotHeader);
    var records = [_]protocol.Record{std.mem.zeroes(protocol.Record)} ** 2;
    try std.testing.expectEqual(@intFromEnum(protocol.Status.ok), abi.rm_applied_ownership_snapshot(handle, &header, &records, records.len));
    var image: [maximum_test_image]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(@intFromEnum(protocol.Status.ok), abi.rm_applied_ownership_encode(handle, &image, image.len, &written));
    var open_config = corpus.openConfig(2, 4);
    var reopened: ?*anyopaque = null;
    try std.testing.expectEqual(@intFromEnum(protocol.Status.ok), abi.rm_applied_ownership_open_existing(&open_config, &image, written, &reopened));
    defer abi.rm_applied_ownership_destroy(reopened);
    var remove = corpus.remove(&records[0], header.ledger_revision, 130);
    try std.testing.expectEqual(@intFromEnum(protocol.Status.ok), abi.rm_applied_ownership_remove(reopened, &remove));
}

fn memoryPromote(
    id: u64,
    expected_revision: u64,
    priority: i32,
    now_utc_ms: u64,
) protocol.PromoteInput {
    var input = corpus.processPromote(id, expected_revision, now_utc_ms);
    input.primary.target_id += 10_000;
    input.original_binding.action_identity.target_id = input.primary.target_id;
    input.original_binding.domain_mask = protocol.DomainBits.physical_memory;
    input.original_binding.grade_valid_mask = protocol.GradeValid.memory;
    input.original_binding.process_from_grade = 0;
    input.original_binding.process_to_grade = priority;
    input.payload = corpus.payload(1_000 + id);
    input.current_grades = memoryGrades(priority);
    return input;
}

fn memoryTransition(
    record: *const protocol.Record,
    expected_revision: u64,
    priority: i32,
    now_utc_ms: u64,
) protocol.TransitionInput {
    var binding = record.original_binding;
    binding.journal_instance_low += 1;
    binding.action_identity.plan_epoch += 1;
    binding.action_identity.action_id += 1;
    binding.disposition = if (priority == 0)
        @intFromEnum(protocol.Disposition.restore)
    else
        @intFromEnum(protocol.Disposition.apply);
    binding.domain_mask = protocol.DomainBits.physical_memory;
    binding.grade_valid_mask = protocol.GradeValid.memory;
    binding.process_from_grade = record.current_grades.process_grade;
    binding.process_to_grade = priority;
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.TransitionInput),
        .expected_ledger_revision = expected_revision,
        .cas = corpus.cas(record),
        .transition_binding = binding,
        .transition_payload = record.payload,
        .new_current_grades = if (priority == 0)
            corpus.emptyGrades()
        else
            memoryGrades(priority),
        .updated_at_utc_ms = now_utc_ms,
        .reserved = .{ 0, 0 },
    };
}

fn memoryGrades(priority: i32) protocol.CurrentGrades {
    return .{
        .valid_mask = protocol.GradeValid.memory,
        .reserved_u32 = 0,
        .process_grade = priority,
        .cpu_grade = 0,
        .gpu_grade = 0,
        .reserved_i32 = 0,
    };
}

fn getRecord(session: *state.Session, primary: *const protocol.PrimaryIdentity) !protocol.Record {
    var record = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(session, primary, &record));
    return record;
}

fn rewriteChecksum(image: []u8) void {
    codec.writeU64(image, codec.checksum_offset, 0);
    const crc = checksum.crc64EcmaWithZeroRange(image, codec.checksum_offset, codec.checksum_length);
    codec.writeU64(image, codec.checksum_offset, crc);
}

fn expectOpenStatus(
    config: *const protocol.OpenConfig,
    image: []const u8,
    expected: protocol.Status,
) !void {
    var opened: ?*state.Session = null;
    try std.testing.expectEqual(expected, session_module.openExisting(config, image, &opened));
    try std.testing.expect(opened == null);
}
