const std = @import("std");
const checksum = @import("checksum.zig");
const codec = @import("codec.zig");
const corpus = @import("golden_corpus.zig");
const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const state = @import("state.zig");

const maximum_test_image = @sizeOf(protocol.ImageHeader) + 8 * @sizeOf(protocol.Record);

test "content sized journal growth preserves canonical bytes and pending feedback" {
    var config = corpus.createConfig(128);
    const journal = try state.Session.createWithAllocator(&config, std.testing.allocator);
    defer journal.destroy();
    const full = try state.Session.createWithAllocator(&config, std.testing.allocator);
    defer full.destroy();
    state.lockMutex(&full.mutex);
    const reserved = full.ensureRecordCapacityUnlocked(config.record_capacity, .prepare);
    full.mutex.unlock();
    try std.testing.expectEqual(protocol.Status.ok, reserved);
    try std.testing.expectEqual(@as(u32, 16), journal.workspaceCapacity().record_capacity);

    for ([_]*state.Session{ journal, full }) |target| {
        const identity = try prepareAndObserveEffect(target, 7, 1, .durable, 100);
        var feedback = corpus.processFeedback(identity, target.journal_revision, 2, 120, .durable);
        try std.testing.expectEqual(protocol.Status.ok, session_module.stageFeedback(target, &feedback));
        var action_id: u64 = 2;
        while (action_id <= 37) : (action_id += 1) {
            var input = corpus.processPrepare(7, action_id, target.journal_revision, 200, .durable);
            try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(target, &input));
        }
    }
    const actual = journal.workspaceCapacity();
    try std.testing.expectEqual(@as(u64, 2), actual.growth.count);
    for (actual.growth.events[0..2], 0..) |event, row| {
        try std.testing.expectEqual(@as(u64, row + 1), event.sequence);
        try std.testing.expectEqual(@as(u32, 5), event.reason);
        try std.testing.expectEqual(@as(u32, 0), event.unit);
        try std.testing.expectEqual(@as(u64, 16) << @intCast(row), event.old_capacity);
        try std.testing.expectEqual(@as(u64, 32) << @intCast(row), event.new_capacity);
    }
    try std.testing.expectEqual(@as(u32, 64), actual.record_capacity);
    try std.testing.expectEqual(@as(u32, 64), actual.staging_record_capacity);
    try std.testing.expectEqual(@as(u32, 64), actual.sort_capacity);
    try std.testing.expectEqual(@as(u64, 128), actual.index_capacity);
    try std.testing.expectEqual(try state.requiredResidentBytes(64), actual.resident_bytes);
    try std.testing.expect(actual.resident_bytes < journal.capacity().resident_bytes);
    var image: [@sizeOf(protocol.ImageHeader) + 128 * @sizeOf(protocol.Record)]u8 = undefined;
    var full_image: [image.len]u8 = undefined;
    var written: u64 = 0;
    var full_written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(journal, &image, &written));
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(full, &full_image, &full_written));
    try std.testing.expectEqual(written, full_written);
    try std.testing.expectEqualSlices(u8, image[0..@intCast(written)], full_image[0..@intCast(full_written)]);
    const reopened = try openImage(image[0..@intCast(written)], 128);
    defer reopened.destroy();
    try std.testing.expectEqual(@as(u32, 64), reopened.workspaceCapacity().record_capacity);
    try std.testing.expectEqual(@as(u32, 37), reopened.count);
    var pending = std.mem.zeroes(protocol.Record);
    const identity = corpus.processIdentity(7, 1);
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(reopened, &identity, &pending));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.feedback_pending), pending.phase);
    var ack = corpus.ack(identity, reopened.journal_revision, pending.entry_revision, .feedback_pending, .accepted, 130);
    try std.testing.expectEqual(protocol.Status.ok, session_module.acknowledge(reopened, &ack));
    try std.testing.expectEqual(@as(u32, 36), reopened.count);
}

test "journal growth failure at every allocation preserves complete authority and retries" {
    var failure_offset: usize = 0;
    while (failure_offset < 4) : (failure_offset += 1) {
        var allocator = std.testing.FailingAllocator.init(std.testing.allocator, .{});
        var config = corpus.createConfig(64);
        const journal = try state.Session.createWithAllocator(&config, allocator.allocator());
        defer journal.destroy();
        var action_id: u64 = 1;
        while (action_id <= 16) : (action_id += 1) {
            var input = corpus.processPrepare(7, action_id, journal.journal_revision, 100, .durable);
            try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &input));
        }
        var before: [@sizeOf(protocol.ImageHeader) + 16 * @sizeOf(protocol.Record)]u8 = undefined;
        var after: [before.len]u8 = undefined;
        var written: u64 = 0;
        try std.testing.expectEqual(protocol.Status.ok, session_module.encode(journal, &before, &written));
        const records = journal.records.ptr;
        const staging = journal.staging_records.ptr;
        const index = journal.index.ptr;
        const sort = journal.sort_indices.ptr;
        const resident = allocator.allocated_bytes - allocator.freed_bytes;
        allocator.fail_index = allocator.alloc_index + failure_offset;
        var input = corpus.processPrepare(7, 17, journal.journal_revision, 100, .durable);
        try std.testing.expectEqual(protocol.Status.capacity_full, session_module.prepare(journal, &input));
        try std.testing.expectEqual(@as(u64, 0), journal.workspaceCapacity().growth.count);
        try std.testing.expectEqual(records, journal.records.ptr);
        try std.testing.expectEqual(staging, journal.staging_records.ptr);
        try std.testing.expectEqual(index, journal.index.ptr);
        try std.testing.expectEqual(sort, journal.sort_indices.ptr);
        try std.testing.expectEqual(resident, allocator.allocated_bytes - allocator.freed_bytes);
        try std.testing.expectEqual(protocol.Status.ok, session_module.encode(journal, &after, &written));
        try std.testing.expectEqualSlices(u8, &before, &after);
        for (journal.records[0..journal.count], 0..) |*record, row| {
            try std.testing.expectEqual(@as(u32, @intCast(row)), journal.find(&record.identity).?);
        }
        allocator.fail_index = std.math.maxInt(usize);
        try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &input));
        try std.testing.expectEqual(@as(u32, 17), journal.count);
        try std.testing.expectEqual(@as(u32, 32), journal.workspaceCapacity().record_capacity);
    }
}

test "journal growth clamps at the configured limit before allocation" {
    var allocator = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    var config = corpus.createConfig(17);
    const journal = try state.Session.createWithAllocator(&config, allocator.allocator());
    defer journal.destroy();
    var action_id: u64 = 1;
    while (action_id <= 16) : (action_id += 1) {
        var input = corpus.processPrepare(7, action_id, journal.journal_revision, 100, .none_required);
        try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &input));
    }
    var input = corpus.processPrepare(7, 17, journal.journal_revision, 100, .none_required);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &input));
    try std.testing.expectEqual(@as(u32, 17), journal.workspaceCapacity().record_capacity);
    const revision = journal.journal_revision;
    const allocation_count = allocator.alloc_index;
    input = corpus.processPrepare(7, 18, journal.journal_revision, 100, .none_required);
    try std.testing.expectEqual(protocol.Status.capacity_full, session_module.prepare(journal, &input));
    try std.testing.expectEqual(revision, journal.journal_revision);
    try std.testing.expectEqual(allocation_count, allocator.alloc_index);
}

test "journal batch growth is atomic on allocation failure and invalid input" {
    var allocator = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    var config = corpus.createConfig(64);
    const journal = try state.Session.createWithAllocator(&config, allocator.allocator());
    defer journal.destroy();
    var action_id: u64 = 1;
    while (action_id <= 15) : (action_id += 1) {
        var input = corpus.processPrepare(7, action_id, journal.journal_revision, 100, .none_required);
        try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &input));
    }
    var inputs = [_]protocol.PrepareInput{
        corpus.processPrepare(7, 16, journal.journal_revision, 200, .durable),
        corpus.processPrepare(7, 17, journal.journal_revision, 200, .durable),
    };
    for (&inputs, 0..) |*input, row| {
        input.atomic_group_id = 55;
        input.group_member_index = @intCast(row);
        input.group_member_count = 2;
        input.process_to_grade = @intFromEnum(protocol.ProcessGrade.level_4);
    }
    var batch: protocol.PrepareBatchInput = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PrepareBatchInput),
        .expected_journal_revision = journal.journal_revision,
        .input_count = 2,
        .flags = 0,
        .reserved = .{ 0, 0, 0 },
    };
    const allocation_count = allocator.alloc_index;
    inputs[1].group_member_index = 0;
    try std.testing.expectEqual(protocol.Status.duplicate_identity, session_module.prepareBatch(journal, &batch, &inputs));
    try std.testing.expectEqual(allocation_count, allocator.alloc_index);
    inputs[1].group_member_index = 1;
    allocator.fail_index = allocator.alloc_index + 2;
    try std.testing.expectEqual(protocol.Status.capacity_full, session_module.prepareBatch(journal, &batch, &inputs));
    try std.testing.expectEqual(@as(u64, 0), journal.workspaceCapacity().growth.count);
    try std.testing.expectEqual(@as(u32, 15), journal.count);
    try std.testing.expectEqual(batch.expected_journal_revision, journal.journal_revision);
    try std.testing.expectEqual(@as(u32, 16), journal.workspaceCapacity().record_capacity);
    allocator.fail_index = std.math.maxInt(usize);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepareBatch(journal, &batch, &inputs));
    try std.testing.expectEqual(@as(u32, 17), journal.count);
    try std.testing.expectEqual(batch.expected_journal_revision + 1, journal.journal_revision);
    try std.testing.expectEqual(@as(u32, 32), journal.workspaceCapacity().record_capacity);
    const batch_event = journal.workspaceCapacity().growth.events[0];
    try std.testing.expectEqual(@as(u64, 1), batch_event.sequence);
    try std.testing.expectEqual(@as(u32, 6), batch_event.reason);
    try std.testing.expectEqual(@as(u64, 16), batch_event.old_capacity);
    try std.testing.expectEqual(@as(u64, 32), batch_event.new_capacity);
    try std.testing.expectEqual(@as(u64, 55), journal.records[15].atomic_group_id);
    try std.testing.expectEqual(@as(u32, 1), journal.records[16].group_member_index);
}

test "journal decode growth failure leaves pending authority intact and content sized retry succeeds" {
    var config = corpus.createConfig(8192);
    const source = try state.Session.createWithAllocator(&config, std.testing.allocator);
    defer source.destroy();
    var action_id: u64 = 1;
    while (action_id <= 33) : (action_id += 1) {
        var input = corpus.processPrepare(7, action_id, source.journal_revision, 100, .durable);
        try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(source, &input));
    }
    var image: [@sizeOf(protocol.ImageHeader) + 33 * @sizeOf(protocol.Record)]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(source, &image, &written));
    var allocator = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    const target = try state.Session.createWithAllocator(&config, allocator.allocator());
    defer target.destroy();
    const identity = try prepareAndObserveEffect(target, 8, 99, .durable, 100);
    const revision = target.journal_revision;
    const old_records = target.records.ptr;
    const old_record = target.records[0];
    allocator.fail_index = allocator.alloc_index + 3;
    try std.testing.expectEqual(protocol.Status.capacity_full, session_module.decode(target, &image));
    try std.testing.expectEqual(@as(u64, 0), target.workspaceCapacity().growth.count);
    try std.testing.expectEqual(old_records, target.records.ptr);
    try std.testing.expectEqual(revision, target.journal_revision);
    try std.testing.expectEqual(@as(u32, 1), target.count);
    try std.testing.expectEqualDeep(old_record, target.records[0]);
    try std.testing.expectEqual(@as(u32, 0), target.find(&identity).?);
    allocator.fail_index = std.math.maxInt(usize);
    try std.testing.expectEqual(protocol.Status.ok, session_module.decode(target, &image));
    try std.testing.expectEqual(source.journal_revision, target.journal_revision);
    try std.testing.expectEqual(@as(u32, 33), target.count);
    try std.testing.expectEqual(@as(u32, 64), target.workspaceCapacity().record_capacity);
    const decode_event = target.workspaceCapacity().growth.events[0];
    try std.testing.expectEqual(@as(u64, 1), decode_event.sequence);
    try std.testing.expectEqual(@as(u32, 7), decode_event.reason);
    try std.testing.expectEqual(@as(u64, 16), decode_event.old_capacity);
    try std.testing.expectEqual(@as(u64, 64), decode_event.new_capacity);
    var roundtrip: [image.len]u8 = undefined;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(target, &roundtrip, &written));
    try std.testing.expectEqualSlices(u8, &image, &roundtrip);
}

test "journal native allocation bytes follow process transaction count independently of thread placement" {
    var allocator = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    var config = corpus.createConfig(8192);
    const journal = try state.Session.createWithAllocator(&config, allocator.allocator());
    defer journal.destroy();
    try std.testing.expectEqual(@as(u64, 10088), journal.workspaceCapacity().resident_bytes);
    var action_id: u64 = 1;
    while (action_id <= 300) : (action_id += 1) {
        var input = corpus.processPrepare(7, action_id, journal.journal_revision, 100, .durable);
        try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &input));
    }
    const actual = journal.workspaceCapacity();
    try std.testing.expectEqual(@as(usize, 424), @sizeOf(state.Session));
    try std.testing.expectEqual(@as(usize, 296), @sizeOf(protocol.WorkspaceCapacity));
    try std.testing.expectEqual(@as(u32, 300), journal.count);
    try std.testing.expectEqual(@as(u32, 512), actual.record_capacity);
    try std.testing.expectEqual(@as(u64, 309672), actual.resident_bytes);
    try std.testing.expectEqual(actual.resident_bytes, allocator.allocated_bytes - allocator.freed_bytes);
    try std.testing.expectEqual(@as(u64, 4948392), journal.capacity().resident_bytes);
}

test "transaction journal ABI sizes offsets and CRC64 ECMA are fixed" {
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.CreateConfig));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(protocol.OpenConfig));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(protocol.Capacity));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.Identity));
    try std.testing.expectEqual(@as(usize, 224), @sizeOf(protocol.PrepareInput));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(protocol.PrepareBatchInput));
    try std.testing.expectEqual(@as(usize, 160), @sizeOf(protocol.MutationInput));
    try std.testing.expectEqual(@as(usize, 176), @sizeOf(protocol.StageFeedbackInput));
    try std.testing.expectEqual(@as(usize, 160), @sizeOf(protocol.RecoveryEvidenceInput));
    try std.testing.expectEqual(@as(usize, 160), @sizeOf(protocol.AckInput));
    try std.testing.expectEqual(@as(usize, 296), @sizeOf(protocol.Record));
    try std.testing.expectEqual(@as(usize, 128), @sizeOf(protocol.ImageHeader));
    try std.testing.expectEqual(@as(usize, 128), @sizeOf(protocol.SnapshotHeader));
    const create_config = corpus.createConfig(1);
    try std.testing.expectEqual(@as(usize, 3), create_config.reserved.len);
    try std.testing.expectEqual(@as(usize, 32), @offsetOf(protocol.CreateConfig, "maximum_resident_bytes"));
    try std.testing.expectEqual(@as(usize, 16), @offsetOf(protocol.OpenConfig, "maximum_resident_bytes"));
    try std.testing.expectEqual(@as(usize, 16), @offsetOf(protocol.Capacity, "resident_bytes"));
    try std.testing.expectEqual(@as(usize, 16), @offsetOf(protocol.PrepareInput, "identity"));
    try std.testing.expectEqual(@as(usize, 88), @offsetOf(protocol.PrepareInput, "domain_mask"));
    try std.testing.expectEqual(@as(usize, 168), @offsetOf(protocol.PrepareInput, "now_utc_ms"));
    try std.testing.expectEqual(@as(usize, 176), @offsetOf(protocol.PrepareInput, "maximum_recovery_attempts"));
    try std.testing.expectEqual(@as(usize, 184), @offsetOf(protocol.PrepareInput, "recovery_deadline_utc_ms"));
    try std.testing.expectEqual(@as(usize, 192), @offsetOf(protocol.PrepareInput, "atomic_group_id"));
    try std.testing.expectEqual(@as(usize, 200), @offsetOf(protocol.PrepareInput, "group_member_index"));
    try std.testing.expectEqual(@as(usize, 204), @offsetOf(protocol.PrepareInput, "group_member_count"));
    try std.testing.expectEqual(@as(usize, 208), @offsetOf(protocol.PrepareInput, "payload_provenance_digest_low"));
    try std.testing.expectEqual(@as(usize, 216), @offsetOf(protocol.PrepareInput, "payload_provenance_digest_high"));
    try std.testing.expectEqual(@as(usize, 8), @offsetOf(protocol.PrepareBatchInput, "expected_journal_revision"));
    try std.testing.expectEqual(@as(usize, 16), @offsetOf(protocol.PrepareBatchInput, "input_count"));
    try std.testing.expectEqual(@as(usize, 72), @offsetOf(protocol.MutationInput, "expected_journal_revision"));
    try std.testing.expectEqual(@as(usize, 76), @offsetOf(protocol.Record, "phase"));
    try std.testing.expectEqual(@as(usize, 120), @offsetOf(protocol.Record, "payload_kind"));
    try std.testing.expectEqual(@as(usize, 160), @offsetOf(protocol.Record, "prepared_at_utc_ms"));
    try std.testing.expectEqual(@as(usize, 168), @offsetOf(protocol.Record, "updated_at_utc_ms"));
    try std.testing.expectEqual(@as(usize, 176), @offsetOf(protocol.Record, "retry_not_before_utc_ms"));
    try std.testing.expectEqual(@as(usize, 192), @offsetOf(protocol.Record, "feedback_valid_mask"));
    try std.testing.expectEqual(@as(usize, 216), @offsetOf(protocol.Record, "feedback_completed_at_utc_ms"));
    try std.testing.expectEqual(@as(usize, 240), @offsetOf(protocol.Record, "recovery_reason_mask"));
    try std.testing.expectEqual(@as(usize, 248), @offsetOf(protocol.Record, "retry_attempt_count"));
    try std.testing.expectEqual(@as(usize, 252), @offsetOf(protocol.Record, "maximum_recovery_attempts"));
    try std.testing.expectEqual(@as(usize, 256), @offsetOf(protocol.Record, "recovery_deadline_utc_ms"));
    try std.testing.expectEqual(@as(usize, 264), @offsetOf(protocol.Record, "atomic_group_id"));
    try std.testing.expectEqual(@as(usize, 272), @offsetOf(protocol.Record, "group_member_index"));
    try std.testing.expectEqual(@as(usize, 276), @offsetOf(protocol.Record, "group_member_count"));
    try std.testing.expectEqual(@as(usize, 280), @offsetOf(protocol.Record, "payload_provenance_digest_low"));
    try std.testing.expectEqual(@as(usize, 288), @offsetOf(protocol.Record, "payload_provenance_digest_high"));
    try std.testing.expectEqual(@as(usize, 64), @offsetOf(protocol.ImageHeader, "crc64_ecma"));
    try std.testing.expectEqual(
        @as(u64, 0x6c40_df5f_0b49_7347),
        checksum.crc64Ecma("123456789"),
    );
}

test "atomic prepare batch validates every member before one revision commit" {
    var config = corpus.createConfig(4);
    const journal = try state.Session.create(&config);
    defer journal.destroy();

    var inputs = [_]protocol.PrepareInput{
        corpus.processPrepare(7, 1, 1, 100, .durable),
        corpus.processPrepare(7, 2, 1, 100, .durable),
    };
    for (&inputs, 0..) |*input, index| {
        input.atomic_group_id = 55;
        input.group_member_index = @intCast(index);
        input.group_member_count = @intCast(inputs.len);
        input.process_to_grade = @intFromEnum(protocol.ProcessGrade.level_4);
    }
    var batch: protocol.PrepareBatchInput = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PrepareBatchInput),
        .expected_journal_revision = 1,
        .input_count = @intCast(inputs.len),
        .flags = 0,
        .reserved = .{ 0, 0, 0 },
    };

    inputs[1].group_member_index = 0;
    try std.testing.expectEqual(
        protocol.Status.duplicate_identity,
        session_module.prepareBatch(journal, &batch, &inputs),
    );
    try std.testing.expectEqual(@as(u64, 1), journal.journal_revision);
    try std.testing.expectEqual(@as(u32, 0), journal.count);

    inputs[1].group_member_index = 1;
    const second_identity = inputs[1].identity;
    inputs[1].identity.process_id = inputs[0].identity.process_id;
    inputs[1].identity.process_start_key = inputs[0].identity.process_start_key;
    try std.testing.expectEqual(
        protocol.Status.invalid_transition,
        session_module.prepareBatch(journal, &batch, &inputs),
    );
    inputs[1].identity = second_identity;
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepareBatch(journal, &batch, &inputs));
    try std.testing.expectEqual(@as(u64, 2), journal.journal_revision);
    try std.testing.expectEqual(@as(u32, 2), journal.count);
    try std.testing.expectEqual(@as(u64, 55), journal.records[0].atomic_group_id);
    try std.testing.expectEqual(@as(u32, 2), journal.records[1].group_member_count);

    var single = inputs[0];
    single.expected_journal_revision = 2;
    single.identity.action_id = 3;
    single.identity.process_id += 100;
    single.identity.process_start_key += 100;
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.prepare(journal, &single));
    try std.testing.expectEqual(@as(u64, 2), journal.journal_revision);
}

test "full identity controls lookup mutation and canonical tie break" {
    var config = corpus.createConfig(4);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    var prepare = corpus.processPrepare(7, 1, 1, 100, .none_required);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &prepare));

    var altered = prepare.identity;
    altered.target_id += 1;
    var output = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.no_data, session_module.get(journal, &altered, &output));
    altered = prepare.identity;
    altered.software_id += 1;
    try std.testing.expectEqual(protocol.Status.no_data, session_module.get(journal, &altered, &output));
    altered = prepare.identity;
    altered.process_start_key += 1;
    try std.testing.expectEqual(protocol.Status.no_data, session_module.get(journal, &altered, &output));
    altered = prepare.identity;
    altered.process_id += 1;
    var mutation = corpus.mutation(altered, 2, 1, .prepared, .confirm_effect_observed, 110);
    try std.testing.expectEqual(protocol.Status.no_data, session_module.mutate(journal, &mutation));

    var second = corpus.processPrepare(7, 2, 2, 100, .none_required);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &second));
    var snapshot = corpus.emptySnapshot();
    var records = [_]protocol.Record{std.mem.zeroes(protocol.Record)} ** 4;
    try std.testing.expectEqual(protocol.Status.ok, session_module.snapshot(journal, &snapshot, &records));
    try std.testing.expect(records[0].identity.target_id < records[1].identity.target_id);
}

test "scope and surviving grade contracts reject semantic drift" {
    var config = corpus.createConfig(4);
    const journal = try state.Session.create(&config);
    defer journal.destroy();

    var process_a2 = corpus.processPrepare(7, 1, 1, 100, .none_required);
    process_a2.process_to_grade = 2;
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.prepare(journal, &process_a2));

    var process_cpu = corpus.processPrepare(7, 2, 1, 100, .none_required);
    process_cpu.domain_mask = protocol.DomainBits.cpu;
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.prepare(journal, &process_cpu));

    // Surviving process records must reject stray retired grade fields.
    var negative_cpu = corpus.processPrepare(7, 3, 1, 100, .none_required);
    negative_cpu.cpu_to_grade = -1;
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.prepare(journal, &negative_cpu));

    var memory_as_cpu = corpus.resourcePrepare(7, 4, 1, 100);
    memory_as_cpu.grade_valid_mask = protocol.GradeValid.cpu;
    memory_as_cpu.cpu_from_grade = 2;
    memory_as_cpu.cpu_to_grade = 1;
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.prepare(journal, &memory_as_cpu));

    var unknown_domain = corpus.processPrepare(7, 5, 1, 100, .none_required);
    unknown_domain.domain_mask |= 1 << 31;
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.prepare(journal, &unknown_domain));
    var zero_domain = corpus.processPrepare(7, 6, 1, 100, .none_required);
    zero_domain.domain_mask = 0;
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.prepare(journal, &zero_domain));

    var retired_software = corpus.softwarePrepare(7, 7, 1, 100);
    // Scope 2 has no valid identity; prepare rejects it before payload validation.
    try std.testing.expectEqual(protocol.Status.invalid_identity, session_module.prepare(journal, &retired_software));
}

test "physical memory process lifecycle preserves the memory tag and ownership" {
    var config = corpus.createConfig(2);
    const journal = try state.Session.create(&config);
    defer journal.destroy();

    var prepare = corpus.processPrepare(7, 1, 1, 100, .durable);
    prepare.domain_mask = protocol.DomainBits.physical_memory;
    prepare.grade_valid_mask = protocol.GradeValid.memory;
    prepare.process_from_grade = 0;
    prepare.process_to_grade = 3;

    var wrong_tag = prepare;
    wrong_tag.grade_valid_mask = protocol.GradeValid.process;
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.prepare(journal, &wrong_tag));

    var invalid_priority = prepare;
    invalid_priority.process_to_grade = 6;
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.prepare(journal, &invalid_priority));

    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &prepare));
    var effect = corpus.mutation(prepare.identity, 2, 1, .prepared, .confirm_effect_observed, 110);
    try std.testing.expectEqual(protocol.Status.ok, session_module.mutate(journal, &effect));

    var feedback = corpus.processFeedback(prepare.identity, 3, 2, 120, .durable);
    feedback.actual_process_grade = 3;
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.stageFeedback(journal, &feedback));

    feedback.feedback_valid_mask = protocol.FeedbackValid.completed_at |
        protocol.FeedbackValid.actual_memory_priority;
    feedback.feedback_flags = protocol.FeedbackFlags.memory_owned |
        protocol.FeedbackFlags.rollback_payload_persisted;
    try std.testing.expectEqual(protocol.Status.ok, session_module.stageFeedback(journal, &feedback));

    var record = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &prepare.identity, &record));
    try std.testing.expectEqual(protocol.GradeValid.memory, record.grade_valid_mask);
    try std.testing.expectEqual(
        protocol.FeedbackValid.completed_at | protocol.FeedbackValid.actual_memory_priority,
        record.feedback_valid_mask,
    );
    try std.testing.expectEqual(
        protocol.FeedbackFlags.memory_owned | protocol.FeedbackFlags.rollback_payload_persisted,
        record.feedback_flags,
    );
}

test "physical memory restore success releases memory ownership" {
    var config = corpus.createConfig(1);
    const journal = try state.Session.create(&config);
    defer journal.destroy();

    var prepare = corpus.processPrepare(7, 1, 1, 100, .durable);
    prepare.disposition = @intFromEnum(protocol.Disposition.restore);
    prepare.domain_mask = protocol.DomainBits.physical_memory;
    prepare.grade_valid_mask = protocol.GradeValid.memory;
    prepare.process_from_grade = 3;
    prepare.process_to_grade = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &prepare));

    var effect = corpus.mutation(prepare.identity, 2, 1, .prepared, .confirm_effect_observed, 110);
    try std.testing.expectEqual(protocol.Status.ok, session_module.mutate(journal, &effect));

    var feedback = corpus.processFeedback(prepare.identity, 3, 2, 120, .durable);
    feedback.feedback_valid_mask = protocol.FeedbackValid.completed_at |
        protocol.FeedbackValid.actual_memory_priority;
    feedback.feedback_flags = 0;
    feedback.actual_process_grade = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.stageFeedback(journal, &feedback));
}

test "old and new action generations coexist and reopen together" {
    var config = corpus.createConfig(4);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    var old_generation = corpus.processPrepare(7, 1, 1, 100, .none_required);
    var new_generation = corpus.processPrepare(8, 2, 2, 110, .none_required);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &old_generation));
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &new_generation));

    var image: [@sizeOf(protocol.ImageHeader) + 4 * @sizeOf(protocol.Record)]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(journal, &image, &written));
    var opened: ?*state.Session = null;
    var open_config = corpus.openConfig(4);
    try std.testing.expectEqual(
        protocol.Status.ok,
        session_module.openExisting(&open_config, image[0..@intCast(written)], &opened),
    );
    defer opened.?.destroy();
    var output = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(opened.?, &old_generation.identity, &output));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(opened.?, &new_generation.identity, &output));
}

test "normal lifecycle allows direct effect feedback and exact accepted settlement" {
    var config = corpus.createConfig(2);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    const identity = try prepareAndObserveEffect(journal, 7, 1, .durable, 100);
    var feedback = corpus.processFeedback(identity, 3, 2, 120, .durable);
    try std.testing.expectEqual(protocol.Status.ok, session_module.stageFeedback(journal, &feedback));
    var ack = corpus.ack(identity, 4, 3, .feedback_pending, .accepted, 130);
    try std.testing.expectEqual(protocol.Status.ok, session_module.acknowledge(journal, &ack));
    var output = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.no_data, session_module.get(journal, &identity, &output));
    try std.testing.expectEqual(protocol.Status.no_data, session_module.acknowledge(journal, &ack));
}

test "state uncertain and ownership lost feedback cannot terminal settle" {
    var config = corpus.createConfig(4);
    const journal = try state.Session.create(&config);
    defer journal.destroy();

    const uncertain_id = try prepareAndObserveEffect(journal, 7, 1, .durable, 100);
    var uncertain = corpus.processFeedback(uncertain_id, 3, 2, 120, .durable);
    uncertain.feedback_status = @intFromEnum(protocol.FeedbackStatus.state_uncertain);
    uncertain.feedback_valid_mask = protocol.FeedbackValid.completed_at;
    uncertain.feedback_flags = 0;
    uncertain.actual_process_grade = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.stageFeedback(journal, &uncertain));
    var uncertain_ack = corpus.ack(uncertain_id, 4, 3, .feedback_pending, .accepted, 130);
    try std.testing.expectEqual(protocol.Status.ok, session_module.acknowledge(journal, &uncertain_ack));
    var output = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &uncertain_id, &output));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.reconciliation_pending), output.phase);

    var second_prepare = corpus.processPrepare(7, 2, journal.journal_revision, 140, .durable);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &second_prepare));
    var effect = corpus.mutation(
        second_prepare.identity,
        journal.journal_revision,
        1,
        .prepared,
        .confirm_effect_observed,
        150,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.mutate(journal, &effect));
    var lost = corpus.processFeedback(
        second_prepare.identity,
        journal.journal_revision,
        2,
        160,
        .durable,
    );
    lost.feedback_status = @intFromEnum(protocol.FeedbackStatus.ownership_lost);
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.stageFeedback(journal, &lost));
    lost.feedback_valid_mask = protocol.FeedbackValid.completed_at;
    lost.feedback_flags = 0;
    lost.actual_process_grade = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.stageFeedback(journal, &lost));
    var lost_ack = corpus.ack(
        second_prepare.identity,
        journal.journal_revision,
        3,
        .feedback_pending,
        .accepted,
        170,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.acknowledge(journal, &lost_ack));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &second_prepare.identity, &output));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.authoritative_resync_pending), output.phase);
}

test "recovery evidence owns retry blocked and authoritative resync classification" {
    var config = corpus.createConfig(5);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    var output = std.mem.zeroes(protocol.Record);

    var restored_prepare = corpus.processPrepare(7, 1, 1, 100, .durable);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &restored_prepare));
    var restored = corpus.recoveryEvidence(restored_prepare.identity, 2, 1, .prepared, .restored, 110);
    try std.testing.expectEqual(protocol.Status.ok, session_module.applyRecoveryEvidence(journal, &restored));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &restored_prepare.identity, &output));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.authoritative_resync_pending), output.phase);
    var missing_generation = corpus.recoveryEvidence(
        restored_prepare.identity,
        journal.journal_revision,
        output.entry_revision,
        .authoritative_resync_pending,
        .authoritative_resync_completed,
        120,
    );
    missing_generation.authoritative_facts_generation = 0;
    try std.testing.expectEqual(
        protocol.Status.invalid_argument,
        session_module.applyRecoveryEvidence(journal, &missing_generation),
    );
    var completed = corpus.recoveryEvidence(
        restored_prepare.identity,
        journal.journal_revision,
        output.entry_revision,
        .authoritative_resync_pending,
        .authoritative_resync_completed,
        120,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.applyRecoveryEvidence(journal, &completed));
    try std.testing.expectEqual(protocol.Status.no_data, session_module.get(journal, &restored_prepare.identity, &output));

    var retry_prepare = corpus.processPrepare(7, 2, journal.journal_revision, 130, .durable);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &retry_prepare));
    var retry = corpus.recoveryEvidence(
        retry_prepare.identity,
        journal.journal_revision,
        1,
        .prepared,
        .retryable_failure,
        140,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.applyRecoveryEvidence(journal, &retry));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &retry_prepare.identity, &output));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.recovery_retry_pending), output.phase);
    try std.testing.expect(output.retry_not_before_utc_ms != 0);

    var blocked_prepare = corpus.processPrepare(7, 3, journal.journal_revision, 150, .durable);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &blocked_prepare));
    var blocked = corpus.recoveryEvidence(
        blocked_prepare.identity,
        journal.journal_revision,
        1,
        .prepared,
        .invalid_proof,
        160,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.applyRecoveryEvidence(journal, &blocked));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &blocked_prepare.identity, &output));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.recovery_blocked), output.phase);

    var exited_prepare = corpus.processPrepare(7, 4, journal.journal_revision, 170, .durable);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &exited_prepare));
    var exited = corpus.recoveryEvidence(
        exited_prepare.identity,
        journal.journal_revision,
        1,
        .prepared,
        .process_exited,
        180,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.applyRecoveryEvidence(journal, &exited));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &exited_prepare.identity, &output));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.authoritative_resync_pending), output.phase);

    var software_prepare = corpus.resourcePrepare(7, 5, journal.journal_revision, 190);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &software_prepare));
    var invalid_process_exit = corpus.recoveryEvidence(
        software_prepare.identity,
        journal.journal_revision,
        1,
        .prepared,
        .process_exited,
        200,
    );
    try std.testing.expectEqual(
        protocol.Status.invalid_argument,
        session_module.applyRecoveryEvidence(journal, &invalid_process_exit),
    );
}

test "recovery retry count is a hard limit and wall-clock deadline is ignored" {
    var config = corpus.createConfig(1);
    const journal = try state.Session.create(&config);
    defer journal.destroy();

    var prepare = corpus.processPrepare(7, 1, 1, 100, .durable);
    prepare.maximum_recovery_attempts = 2;
    prepare.recovery_deadline_utc_ms = 90;
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &prepare));

    var retry = corpus.recoveryEvidence(
        prepare.identity,
        2,
        1,
        .prepared,
        .retryable_failure,
        3_000,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.applyRecoveryEvidence(journal, &retry));

    var record = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &prepare.identity, &record));
    try std.testing.expectEqual(@as(u32, 1), record.retry_attempt_count);
    try std.testing.expectEqual(@as(u32, 2), record.maximum_recovery_attempts);
    try std.testing.expectEqual(@as(u64, 90), record.recovery_deadline_utc_ms);
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.recovery_retry_pending), record.phase);
    try std.testing.expect(record.retry_not_before_utc_ms > record.recovery_deadline_utc_ms);

    var second_retry = corpus.recoveryEvidence(
        prepare.identity,
        3,
        2,
        .recovery_retry_pending,
        .retryable_failure,
        retry.retry_not_before_utc_ms,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.applyRecoveryEvidence(journal, &second_retry));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &prepare.identity, &record));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.recovery_retry_pending), record.phase);
    try std.testing.expectEqual(@as(u32, 2), record.retry_attempt_count);

    var exhausted = corpus.recoveryEvidence(
        prepare.identity,
        4,
        3,
        .recovery_retry_pending,
        .unavailable,
        second_retry.retry_not_before_utc_ms,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.applyRecoveryEvidence(journal, &exhausted));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &prepare.identity, &record));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.recovery_blocked), record.phase);
    try std.testing.expectEqual(@as(u64, 0), record.retry_not_before_utc_ms);
    try std.testing.expectEqual(@as(u32, 2), record.retry_attempt_count);
    try std.testing.expect(
        (record.recovery_reason_mask & protocol.RecoveryReason.retry_budget_exhausted) != 0,
    );
}

test "invoked external effect can become explicitly uncertain without forged observation" {
    var config = corpus.createConfig(1);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    var prepare = corpus.processPrepare(7, 1, 1, 100, .durable);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &prepare));

    var uncertain = corpus.recoveryEvidence(
        prepare.identity,
        2,
        1,
        .prepared,
        .effect_invocation_uncertain,
        110,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.applyRecoveryEvidence(journal, &uncertain));

    var record = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &prepare.identity, &record));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.effect_invocation_uncertain), record.phase);
    try std.testing.expectEqual(protocol.RecoveryReason.effect_invocation_uncertain, record.recovery_reason_mask);
}

test "exact revisions phase order and updated time reject ABA jumps and rollback" {
    var config = corpus.createConfig(2);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    var prepare = corpus.processPrepare(7, 1, 1, 100, .none_required);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &prepare));

    var jump = corpus.mutation(prepare.identity, 2, 1, .prepared, .confirm_effect_observed, 120);
    try std.testing.expectEqual(protocol.Status.ok, session_module.mutate(journal, &jump));
    try std.testing.expectEqual(protocol.Status.stale_revision, session_module.mutate(journal, &jump));

    var backwards = corpus.processFeedback(prepare.identity, 3, 2, 119, .none_required);
    try std.testing.expectEqual(protocol.Status.invalid_time, session_module.stageFeedback(journal, &backwards));
    var wrong_phase = corpus.mutation(
        prepare.identity,
        3,
        2,
        .effect_observed,
        .confirm_previous_effect_restored,
        130,
    );
    try std.testing.expectEqual(protocol.Status.invalid_transition, session_module.mutate(journal, &wrong_phase));
}

test "typed no effect phase accepts only unchanged terminal feedback" {
    var config = corpus.createConfig(2);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    var prepare = corpus.resourcePrepare(7, 1, 1, 100);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &prepare));

    var no_effect = corpus.mutation(
        prepare.identity,
        2,
        1,
        .prepared,
        .confirm_previous_effect_restored,
        110,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.mutate(journal, &no_effect));

    var feedback = corpus.processFeedback(prepare.identity, 3, 2, 120, .durable);
    feedback.expected_phase = @intFromEnum(protocol.Phase.previous_effect_restored);
    feedback.feedback_valid_mask = protocol.FeedbackValid.completed_at;
    feedback.feedback_flags = 0;
    feedback.feedback_status = @intFromEnum(protocol.FeedbackStatus.rejected);
    feedback.actual_process_grade = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.stageFeedback(journal, &feedback));

    var second_prepare = corpus.resourcePrepare(7, 2, 4, 130);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &second_prepare));
    var second_no_effect = corpus.mutation(
        second_prepare.identity,
        5,
        1,
        .prepared,
        .confirm_previous_effect_restored,
        140,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.mutate(journal, &second_no_effect));
    var forged_success = corpus.processFeedback(second_prepare.identity, 6, 2, 150, .durable);
    forged_success.expected_phase = @intFromEnum(protocol.Phase.previous_effect_restored);
    forged_success.feedback_valid_mask = protocol.FeedbackValid.completed_at;
    forged_success.feedback_flags = 0;
    forged_success.actual_process_grade = 0;
    try std.testing.expectEqual(
        protocol.Status.invalid_argument,
        session_module.stageFeedback(journal, &forged_success),
    );
}

test "payload and feedback validity are strict" {
    var config = corpus.createConfig(3);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    var missing_payload = corpus.processPrepare(7, 1, 1, 100, .durable);
    missing_payload.payload_digest_high = 0;
    try std.testing.expectEqual(protocol.Status.invalid_payload, session_module.prepare(journal, &missing_payload));
    var invented_payload = corpus.processPrepare(7, 2, 1, 100, .none_required);
    invented_payload.payload_slot = 1;
    try std.testing.expectEqual(protocol.Status.invalid_payload, session_module.prepare(journal, &invented_payload));

    const identity = try prepareAndObserveEffect(journal, 7, 3, .durable, 100);
    var missing_actual = corpus.processFeedback(identity, 3, 2, 120, .durable);
    missing_actual.feedback_valid_mask = protocol.FeedbackValid.completed_at;
    missing_actual.actual_process_grade = 0;
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.stageFeedback(journal, &missing_actual));
    var unknown_flags = corpus.processFeedback(identity, 3, 2, 120, .durable);
    unknown_flags.feedback_flags |= 1 << 31;
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.stageFeedback(journal, &unknown_flags));
    var unknown_status = corpus.processFeedback(identity, 3, 2, 120, .durable);
    unknown_status.feedback_status = 0xffff_ffff;
    try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.stageFeedback(journal, &unknown_status));
}

test "successful restore feedback releases process ownership" {
    var config = corpus.createConfig(2);
    const journal = try state.Session.create(&config);
    defer journal.destroy();

    var process_prepare = corpus.processPrepare(7, 1, 1, 100, .durable);
    process_prepare.disposition = @intFromEnum(protocol.Disposition.restore);
    process_prepare.process_from_grade = @intFromEnum(protocol.ProcessGrade.level_1);
    process_prepare.process_to_grade = @intFromEnum(protocol.ProcessGrade.normal);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &process_prepare));
    var process_effect = corpus.mutation(
        process_prepare.identity,
        2,
        1,
        .prepared,
        .confirm_effect_observed,
        110,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.mutate(journal, &process_effect));
    var process_feedback = corpus.processFeedback(process_prepare.identity, 3, 2, 120, .durable);
    process_feedback.feedback_flags = 0;
    process_feedback.actual_process_grade = @intFromEnum(protocol.ProcessGrade.normal);
    try std.testing.expectEqual(protocol.Status.ok, session_module.stageFeedback(journal, &process_feedback));


}

test "capacity full is explicit and does not mutate revision" {
    var config = corpus.createConfig(1);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    var first = corpus.processPrepare(7, 1, 1, 100, .none_required);
    var second = corpus.processPrepare(7, 2, 2, 100, .none_required);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &first));
    try std.testing.expectEqual(protocol.Status.capacity_full, session_module.prepare(journal, &second));
    try std.testing.expectEqual(@as(u64, 2), journal.journal_revision);
}

test "prepare protects only overlapping unresolved target domains" {
    var config = corpus.createConfig(12);
    const journal = try state.Session.create(&config);
    defer journal.destroy();

    var process = corpus.processPrepare(7, 1, 1, 100, .durable);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &process));

    var same_process = corpus.processPrepare(7, 2, 2, 110, .durable);
    same_process.identity.process_id = process.identity.process_id;
    same_process.identity.process_start_key = process.identity.process_start_key;
    same_process.identity.software_id += 100;
    same_process.identity.target_id += 100;
    try std.testing.expectEqual(protocol.Status.invalid_transition, session_module.prepare(journal, &same_process));

    var memory = corpus.processPrepare(7, 3, 2, 120, .durable);
    memory.identity.process_id = process.identity.process_id;
    memory.identity.process_start_key = process.identity.process_start_key;
    memory.domain_mask = protocol.DomainBits.physical_memory;
    memory.grade_valid_mask = protocol.GradeValid.memory;
    memory.process_from_grade = 0;
    memory.process_to_grade = 3;
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &memory));

    var same_memory = corpus.processPrepare(7, 4, 3, 130, .durable);
    same_memory.identity.process_id = process.identity.process_id;
    same_memory.identity.process_start_key = process.identity.process_start_key;
    same_memory.domain_mask = protocol.DomainBits.physical_memory;
    same_memory.grade_valid_mask = protocol.GradeValid.memory;
    same_memory.process_from_grade = 0;
    same_memory.process_to_grade = 4;
    try std.testing.expectEqual(protocol.Status.invalid_transition, session_module.prepare(journal, &same_memory));

    var unrelated_process = corpus.processPrepare(7, 5, 3, 140, .durable);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &unrelated_process));

    var resource = corpus.resourcePrepare(7, 9, 4, 180);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &resource));
    var same_resource = corpus.resourcePrepare(7, 10, 5, 190);
    same_resource.identity.target_id = resource.identity.target_id;
    try std.testing.expectEqual(protocol.Status.invalid_transition, session_module.prepare(journal, &same_resource));
    var other_resource = corpus.resourcePrepare(7, 11, 5, 200);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &other_resource));
}

test "resident byte budget is explicit and enforced on create and reopen" {
    var insufficient = corpus.createConfig(1);
    insufficient.maximum_resident_bytes = 1;
    try std.testing.expectError(error.ResidentBudgetExceeded, state.Session.create(&insufficient));

    var config = corpus.createConfig(2);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    const capacity = journal.capacity();
    try std.testing.expectEqual(try state.requiredResidentBytes(2), capacity.resident_bytes);
    try std.testing.expect(capacity.resident_bytes <= config.maximum_resident_bytes);

    var image: [@sizeOf(protocol.ImageHeader) + 4 * @sizeOf(protocol.Record)]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(journal, &image, &written));
    var larger_open_config = corpus.openConfig(4);
    var opened: ?*state.Session = null;
    try std.testing.expectEqual(
        protocol.Status.ok,
        session_module.openExisting(&larger_open_config, image[0..@intCast(written)], &opened),
    );
    try std.testing.expectEqual(@as(u32, 2), opened.?.capacity().record_capacity);
    opened.?.destroy();

    var smaller_open_config = corpus.openConfig(1);
    opened = null;
    try std.testing.expectEqual(
        protocol.Status.capacity_full,
        session_module.openExisting(&smaller_open_config, image[0..@intCast(written)], &opened),
    );
    try std.testing.expect(opened == null);

    var open_config = corpus.openConfig(2);
    open_config.maximum_resident_bytes = capacity.resident_bytes - 1;
    opened = null;
    try std.testing.expectEqual(
        protocol.Status.capacity_full,
        session_module.openExisting(&open_config, image[0..@intCast(written)], &opened),
    );
    try std.testing.expect(opened == null);
}

test "effect observed survives restart and stages feedback after reopen" {
    var config = corpus.createConfig(2);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    const identity = try prepareAndObserveEffect(journal, 7, 1, .durable, 100);
    var image: [@sizeOf(protocol.ImageHeader) + 4 * @sizeOf(protocol.Record)]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(journal, &image, &written));
    const opened = try openImage(image[0..@intCast(written)], 2);
    defer opened.destroy();
    var feedback = corpus.processFeedback(identity, 3, 2, 120, .durable);
    try std.testing.expectEqual(protocol.Status.ok, session_module.stageFeedback(opened, &feedback));
}

test "durable feedback survives restart and exact ACK completes it" {
    var config = corpus.createConfig(2);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    const identity = try prepareAndObserveEffect(journal, 7, 1, .durable, 100);
    var feedback = corpus.processFeedback(identity, 3, 2, 120, .durable);
    try std.testing.expectEqual(protocol.Status.ok, session_module.stageFeedback(journal, &feedback));
    var image: [@sizeOf(protocol.ImageHeader) + 4 * @sizeOf(protocol.Record)]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(journal, &image, &written));
    const opened = try openImage(image[0..@intCast(written)], 2);
    defer opened.destroy();
    var ack = corpus.ack(identity, 4, 3, .feedback_pending, .accepted, 130);
    try std.testing.expectEqual(protocol.Status.ok, session_module.acknowledge(opened, &ack));
    try std.testing.expectEqual(protocol.Status.no_data, session_module.acknowledge(opened, &ack));
}

test "canonical bytes are independent of insertion order" {
    var config = corpus.createConfig(4);
    const left = try state.Session.create(&config);
    defer left.destroy();
    const right = try state.Session.create(&config);
    defer right.destroy();
    var left_two = corpus.processPrepare(7, 2, 1, 100, .none_required);
    var left_one = corpus.processPrepare(7, 1, 2, 100, .none_required);
    var right_one = corpus.processPrepare(7, 1, 1, 100, .none_required);
    var right_two = corpus.processPrepare(7, 2, 2, 100, .none_required);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(left, &left_two));
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(left, &left_one));
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(right, &right_one));
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(right, &right_two));
    var left_image: [maximum_test_image]u8 = undefined;
    var right_image: [maximum_test_image]u8 = undefined;
    var left_written: u64 = 0;
    var right_written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(left, &left_image, &left_written));
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(right, &right_image, &right_written));
    try std.testing.expectEqual(left_written, right_written);
    try std.testing.expectEqualSlices(
        u8,
        left_image[0..@intCast(left_written)],
        right_image[0..@intCast(right_written)],
    );
}

test "decode isolates invalid rows while preserving image structure checks" {
    var config = corpus.createConfig(4);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    var first = corpus.processPrepare(7, 1, 1, 100, .durable);
    var second = corpus.processPrepare(7, 2, 2, 100, .none_required);
    var third = corpus.processPrepare(7, 3, 2, 100, .none_required);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &first));
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &second));
    third.expected_journal_revision = 3;
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &third));
    var image: [@sizeOf(protocol.ImageHeader) + 4 * @sizeOf(protocol.Record)]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(journal, &image, &written));
    const length: usize = @intCast(written);

    try std.testing.expectEqual(protocol.Status.truncated_image, session_module.decode(journal, image[0 .. length - 1]));
    var corrupt = image;
    corrupt[@sizeOf(protocol.ImageHeader) + 20] ^= 0x80;
    try std.testing.expectEqual(protocol.Status.checksum_mismatch, session_module.decode(journal, corrupt[0..length]));

    var duplicate = image;
    const first_offset = @sizeOf(protocol.ImageHeader);
    const second_offset = first_offset + @sizeOf(protocol.Record);
    std.mem.copyForwards(u8, duplicate[second_offset .. second_offset + 64], duplicate[first_offset .. first_offset + 64]);
    rewriteChecksum(duplicate[0..length]);
    try std.testing.expectEqual(protocol.Status.ok, session_module.decode(journal, duplicate[0..length]));
    try std.testing.expectEqual(@as(u32, 2), journal.count);
    var record = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &first.identity, &record));
    try std.testing.expect(protocol.isQuarantinedInvalidProof(&record));
    try std.testing.expectEqual(@as(u32, @intFromEnum(protocol.PayloadKind.none_required)), record.payload_kind);
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &third.identity, &record));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.prepared), record.phase);

    var nonadjacent_duplicate = image;
    const third_offset = second_offset + @sizeOf(protocol.Record);
    std.mem.copyForwards(u8, nonadjacent_duplicate[third_offset .. third_offset + 64], nonadjacent_duplicate[first_offset .. first_offset + 64]);
    rewriteChecksum(nonadjacent_duplicate[0..length]);
    try std.testing.expectEqual(protocol.Status.ok, session_module.decode(journal, nonadjacent_duplicate[0..length]));
    try std.testing.expectEqual(@as(u32, 2), journal.count);
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &first.identity, &record));
    try std.testing.expect(protocol.isQuarantinedInvalidProof(&record));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &second.identity, &record));
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.prepared), record.phase);

    var invalid_identity = image;
    codec.writeU64(invalid_identity[0..length], first_offset + 32, 0);
    rewriteChecksum(invalid_identity[0..length]);
    try std.testing.expectEqual(protocol.Status.ok, session_module.decode(journal, invalid_identity[0..length]));
    try std.testing.expectEqual(@as(u32, 2), journal.count);
    try std.testing.expectEqual(protocol.Status.no_data, session_module.get(journal, &first.identity, &record));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &second.identity, &record));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &third.identity, &record));

    var invalid_reserved_identity = image;
    codec.writeU32(invalid_reserved_identity[0..length], first_offset + 60, 1);
    rewriteChecksum(invalid_reserved_identity[0..length]);
    try std.testing.expectEqual(protocol.Status.ok, session_module.decode(journal, invalid_reserved_identity[0..length]));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &first.identity, &record));
    try std.testing.expect(protocol.isQuarantinedInvalidProof(&record));

    var unknown_phase = image;
    codec.writeU32(unknown_phase[0..length], first_offset + 76, 99);
    rewriteChecksum(unknown_phase[0..length]);
    try std.testing.expectEqual(protocol.Status.ok, session_module.decode(journal, unknown_phase[0..length]));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &first.identity, &record));
    try std.testing.expect(protocol.isQuarantinedInvalidProof(&record));
    try std.testing.expectEqual(@intFromEnum(protocol.PayloadKind.durable), record.payload_kind);
    try std.testing.expectEqual(first.payload_slot, record.payload_slot);
    var snapshot = corpus.emptySnapshot();
    var snapshot_records = [_]protocol.Record{std.mem.zeroes(protocol.Record)} ** 4;
    try std.testing.expectEqual(protocol.Status.ok, session_module.snapshot(journal, &snapshot, &snapshot_records));
    try std.testing.expectEqual(@as(u32, 1), snapshot.recovery_blocked_count);
    try std.testing.expectEqual(@as(u32, 3), snapshot.entry_count);

    var impossible_feedback = image;
    codec.writeU32(
        impossible_feedback[0..length],
        first_offset + 192,
        protocol.FeedbackValid.completed_at | protocol.FeedbackValid.actual_process_grade,
    );
    codec.writeU32(impossible_feedback[0..length], first_offset + 196, protocol.FeedbackFlags.process_owned);
    codec.writeU32(impossible_feedback[0..length], first_offset + 200, @intFromEnum(protocol.FeedbackStatus.succeeded));
    codec.writeU64(impossible_feedback[0..length], first_offset + 216, 100);
    codec.writeI32(impossible_feedback[0..length], first_offset + 224, @intFromEnum(protocol.ProcessGrade.normal));
    rewriteChecksum(impossible_feedback[0..length]);
    try std.testing.expectEqual(protocol.Status.ok, session_module.decode(journal, impossible_feedback[0..length]));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &first.identity, &record));
    try std.testing.expect(protocol.isQuarantinedInvalidProof(&record));

    var live_recovery_reason = image;
    codec.writeU64(live_recovery_reason[0..length], first_offset + 240, protocol.RecoveryReason.restored);
    rewriteChecksum(live_recovery_reason[0..length]);
    try std.testing.expectEqual(protocol.Status.ok, session_module.decode(journal, live_recovery_reason[0..length]));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &first.identity, &record));
    try std.testing.expect(protocol.isQuarantinedInvalidProof(&record));

    var bad_payload = image;
    codec.writeU32(bad_payload[0..length], first_offset + 120, 0xffff_ffff);
    rewriteChecksum(bad_payload[0..length]);
    try std.testing.expectEqual(protocol.Status.ok, session_module.decode(journal, bad_payload[0..length]));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &first.identity, &record));
    try std.testing.expect(protocol.isQuarantinedInvalidProof(&record));
    try std.testing.expectEqual(@intFromEnum(protocol.PayloadKind.none_required), record.payload_kind);

    var invalid_identity_out_of_order = image;
    codec.writeU64(invalid_identity_out_of_order[0..length], first_offset + 16, 99);
    codec.writeU32(invalid_identity_out_of_order[0..length], first_offset + 76, 99);
    rewriteChecksum(invalid_identity_out_of_order[0..length]);
    try std.testing.expectEqual(protocol.Status.ok, session_module.decode(journal, invalid_identity_out_of_order[0..length]));
    var out_of_order_identity = first.identity;
    out_of_order_identity.action_id = 99;
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(journal, &out_of_order_identity, &record));
    try std.testing.expect(protocol.isQuarantinedInvalidProof(&record));

    var noncanonical = image;
    var temp_record: [@sizeOf(protocol.Record)]u8 = undefined;
    @memcpy(&temp_record, noncanonical[first_offset..second_offset]);
    std.mem.copyForwards(u8, noncanonical[first_offset..second_offset], noncanonical[second_offset..third_offset]);
    std.mem.copyForwards(u8, noncanonical[second_offset..third_offset], &temp_record);
    rewriteChecksum(noncanonical[0..length]);
    try std.testing.expectEqual(protocol.Status.non_canonical_image, session_module.decode(journal, noncanonical[0..length]));

    var unknown_schema = image;
    codec.writeU32(unknown_schema[0..length], 8, protocol.image_schema_version + 1);
    rewriteChecksum(unknown_schema[0..length]);
    try std.testing.expectEqual(protocol.Status.abi_mismatch, session_module.decode(journal, unknown_schema[0..length]));

    var wrong_record_size = image;
    codec.writeU32(wrong_record_size[0..length], 16, @intCast(@sizeOf(protocol.Record) + 8));
    rewriteChecksum(wrong_record_size[0..length]);
    try std.testing.expectEqual(protocol.Status.abi_mismatch, session_module.decode(journal, wrong_record_size[0..length]));
}

test "resource recovery already restored removes only its journal row" {
    var config = corpus.createConfig(1);
    const journal = try state.Session.create(&config);
    defer journal.destroy();

    var prepare = corpus.resourcePrepare(7, 1, 1, 100);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &prepare));
    var already_restored = corpus.recoveryEvidence(
        prepare.identity,
        2,
        1,
        .prepared,
        .already_restored,
        110,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.applyRecoveryEvidence(journal, &already_restored));
    var record = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.no_data, session_module.get(journal, &prepare.identity, &record));
}

test "hot mutation snapshot encode decode paths do not allocate" {
    var backing: [16 * 1024]u8 = undefined;
    var fixed = std.heap.FixedBufferAllocator.init(&backing);
    var config = corpus.createConfig(2);
    const journal = try state.Session.createWithAllocator(&config, fixed.allocator());
    defer journal.destroy();
    const allocation_mark = fixed.end_index;
    const identity = try prepareAndObserveEffect(journal, 7, 1, .durable, 100);
    var feedback = corpus.processFeedback(identity, 3, 2, 120, .durable);
    try std.testing.expectEqual(protocol.Status.ok, session_module.stageFeedback(journal, &feedback));
    var snapshot = corpus.emptySnapshot();
    var records = [_]protocol.Record{std.mem.zeroes(protocol.Record)} ** 2;
    try std.testing.expectEqual(protocol.Status.ok, session_module.snapshot(journal, &snapshot, &records));
    var image: [@sizeOf(protocol.ImageHeader) + 4 * @sizeOf(protocol.Record)]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(journal, &image, &written));
    try std.testing.expectEqual(protocol.Status.ok, session_module.decode(journal, image[0..@intCast(written)]));
    try std.testing.expectEqual(allocation_mark, fixed.end_index);
}

fn prepareAndObserveEffect(
    journal: *state.Session,
    generation: u64,
    action_id: u64,
    payload_kind: protocol.PayloadKind,
    prepared_at: u64,
) !protocol.Identity {
    var prepare = corpus.processPrepare(
        generation,
        action_id,
        journal.journal_revision,
        prepared_at,
        payload_kind,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &prepare));
    var effect = corpus.mutation(
        prepare.identity,
        journal.journal_revision,
        1,
        .prepared,
        .confirm_effect_observed,
        prepared_at + 10,
    );
    try std.testing.expectEqual(protocol.Status.ok, session_module.mutate(journal, &effect));
    return prepare.identity;
}

fn openImage(image: []const u8, maximum_capacity: u32) !*state.Session {
    var config = corpus.openConfig(maximum_capacity);
    var opened: ?*state.Session = null;
    try std.testing.expectEqual(
        protocol.Status.ok,
        session_module.openExisting(&config, image, &opened),
    );
    return opened.?;
}

fn rewriteChecksum(image: []u8) void {
    const crc = checksum.crc64EcmaWithZeroRange(
        image,
        codec.checksum_offset,
        codec.checksum_length,
    );
    codec.writeU64(image, codec.checksum_offset, crc);
}

test "empty snapshot query reports actual rows and phase counts for exact buffers" {
    var config = corpus.createConfig(8192);
    const journal = try state.Session.create(&config);
    defer journal.destroy();
    var header = corpus.emptySnapshot();
    var empty: [0]protocol.Record = .{};
    try std.testing.expectEqual(protocol.Status.ok, session_module.snapshot(journal, &header, &empty));
    try std.testing.expectEqual(@as(u32, 0), header.entry_count);
    var input = corpus.processPrepare(7, 1, 1, 100, .none_required);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(journal, &input));
    header = corpus.emptySnapshot();
    try std.testing.expectEqual(protocol.Status.buffer_too_small, session_module.snapshot(journal, &header, &empty));
    try std.testing.expectEqual(@as(u32, 1), header.entry_count);
    try std.testing.expectEqual(@as(u32, 8192), header.capacity);
    try std.testing.expectEqual(@as(u32, 1), header.prepared_count);
    try std.testing.expectEqual(@as(u64, 2), header.journal_revision);
    var records = [_]protocol.Record{std.mem.zeroes(protocol.Record)};
    header = corpus.emptySnapshot();
    try std.testing.expectEqual(protocol.Status.ok, session_module.snapshot(journal, &header, &records));
    var image: [@sizeOf(protocol.ImageHeader) + @sizeOf(protocol.Record)]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(journal, &image, &written));
    try std.testing.expectEqual(@as(u64, image.len), written);
}

test "upgrade drops retired software journal rows and preserves process and resource rows" {
    var config = corpus.createConfig(4);
    const source = try state.Session.create(&config);
    defer source.destroy();
    var process = corpus.processPrepare(7, 1, 1, 100, .durable);
    var retired = corpus.resourcePrepare(7, 2, 2, 110);
    var resource = corpus.resourcePrepare(7, 3, 3, 120);
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(source, &process));
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(source, &retired));
    try std.testing.expectEqual(protocol.Status.ok, session_module.prepare(source, &resource));
    var image: [@sizeOf(protocol.ImageHeader) + 4 * @sizeOf(protocol.Record)]u8 = undefined;
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(source, &image, &written));
    const base = codec.header_size + codec.record_size;
    codec.writeU32(&image, base + 64, 2); // Retired Software scope, never reused.
    codec.writeU32(&image, base + 72, protocol.DomainBits.cpu);
    codec.writeU32(&image, base + 80, protocol.GradeValid.cpu);
    codec.writeI32(&image, base + 104, 2); // Historical Normal -> Optimize.
    codec.writeI32(&image, base + 108, 1);
    rewriteChecksum(image[0..written]);
    var open_config = corpus.openConfig(4);
    var reopened: ?*state.Session = null;
    try std.testing.expectEqual(protocol.Status.ok, session_module.openExisting(&open_config, image[0..written], &reopened));
    defer reopened.?.destroy();
    try std.testing.expectEqual(@as(u32, 2), reopened.?.count);
    try std.testing.expectEqual(@as(u64, 4), reopened.?.journal_revision);
    var record = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(reopened.?, &process.identity, &record));
    try std.testing.expect(std.meta.eql(source.records[0], record));
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(reopened.?, &resource.identity, &record));
    try std.testing.expectEqual(@as(u32, 3), record.scope);
    try std.testing.expect(std.meta.eql(source.records[2], record));
    try std.testing.expectEqual(protocol.Status.no_data, session_module.get(reopened.?, &retired.identity, &record));
    var canonical: [@sizeOf(protocol.ImageHeader) + 4 * @sizeOf(protocol.Record)]u8 = undefined;
    var canonical_written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session_module.encode(reopened.?, &canonical, &canonical_written));
    try std.testing.expectEqual(@as(u32, 2), codec.readU32(&canonical, 48));
    var again: ?*state.Session = null;
    try std.testing.expectEqual(protocol.Status.ok, session_module.openExisting(&open_config, canonical[0..canonical_written], &again));
    defer again.?.destroy();
    try std.testing.expectEqual(@as(u32, 2), again.?.count);
    // A scope-2 row with process/system-state evidence is quarantined, never discarded.
    codec.writeU64(&image, base + 48, 123);
    codec.writeU32(&image, base + 56, 123);
    codec.writeU32(&image, base + 72, protocol.DomainBits.process);
    rewriteChecksum(image[0..written]);
    var protected: ?*state.Session = null;
    try std.testing.expectEqual(protocol.Status.ok, session_module.openExisting(&open_config, image[0..written], &protected));
    defer protected.?.destroy();
    try std.testing.expectEqual(@as(u32, 3), protected.?.count);
    var protected_identity = retired.identity;
    protected_identity.process_start_key = 123;
    protected_identity.process_id = 123;
    try std.testing.expectEqual(protocol.Status.ok, session_module.get(protected.?, &protected_identity, &record));
    try std.testing.expectEqual(@intFromEnum(protocol.Scope.process), record.scope);
    try std.testing.expectEqual(@intFromEnum(protocol.Phase.recovery_blocked), record.phase);
}
