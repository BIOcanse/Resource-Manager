const std = @import("std");
const windows = std.os.windows;
const protocol = @import("protocol.zig");
const bank_module = @import("bank.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const Record = bank_module.Record;
const Bank = bank_module.Bank;

pub fn encode(
    session: anytype,
    input: *const protocol.PersistenceInput,
    header: *protocol.PersistenceHeader,
    records: []protocol.PersistenceRecord,
) ResultCode {
    lockMutex(&session.mutex);
    defer unlockMutex(&session.mutex);
    if (!protocol.validPersistenceInput(input, &session.config)) return .abi_mismatch;
    if (input.operation_epoch <= session.last_persistence_epoch) return .stale_frame;
    if (records.len < session.bank.operation_count) return .buffer_too_small;

    var count: u32 = 0;
    for (session.bank.records, 0..) |record, index| {
        if (!record.occupied) continue;
        session.bank.order_scratch[count] = @intCast(index);
        count += 1;
    }
    std.sort.heap(
        u32,
        session.bank.order_scratch[0..count],
        &session.bank,
        operationBySubmitSequence,
    );
    for (session.bank.order_scratch[0..count], 0..) |record_index, output_index| {
        records[output_index] = session.bank.records[record_index].toOutput();
    }
    header.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PersistenceHeader),
        .configuration_generation = session.config.generation,
        .session_instance_id = session.config.session_instance_id,
        .clock_instance_id = session.config.clock_instance_id,
        .operation_epoch = input.operation_epoch,
        .state_revision = session.state_revision,
        .submit_sequence = session.submit_sequence,
        .last_submit_epoch = session.last_submit_epoch,
        .last_request_epoch = session.last_request_epoch,
        .last_feedback_epoch = session.last_feedback_epoch,
        .last_completion_epoch = session.last_completion_epoch,
        .last_plan_epoch = session.last_plan_epoch,
        .last_observed_utc_milliseconds = session.last_observed_utc_milliseconds,
        .last_observed_monotonic_milliseconds = session.last_observed_monotonic_milliseconds,
        .next_action_id = session.next_action_id,
        .operation_count = count,
        .record_size = @sizeOf(protocol.PersistenceRecord),
        .flags = 0,
        .checksum_high = 0,
        .checksum_low = 0,
        .reserved = [_]u64{0} ** 4,
    };
    const checksum = persistenceChecksum(header, records[0..count]);
    header.checksum_high = checksum.high;
    header.checksum_low = checksum.low;
    session.last_persistence_epoch = input.operation_epoch;
    return .ok;
}

pub fn decodeReplace(
    session: anytype,
    input: *const protocol.PersistenceImportInput,
    header: *const protocol.PersistenceHeader,
    records: []const protocol.PersistenceRecord,
) ResultCode {
    lockMutex(&session.mutex);
    defer unlockMutex(&session.mutex);
    if (!protocol.validPersistenceImportInput(input, &session.config)) {
        return .abi_mismatch;
    }
    if (input.operation_epoch <= session.last_persistence_epoch) return .stale_frame;
    if (!validPersistenceHeader(header, &session.config, records.len)) {
        return .abi_mismatch;
    }
    const checksum = persistenceChecksum(header, records);
    if (checksum.high != header.checksum_high or checksum.low != header.checksum_low) {
        return .invalid_argument;
    }
    const same_clock = protocol.equalHandle(
        header.clock_instance_id,
        input.clock_instance_id,
    );
    if (same_clock) {
        if (input.observed_monotonic_milliseconds <
            header.last_observed_monotonic_milliseconds)
        {
            return .stale_frame;
        }
        const monotonic_delta =
            input.observed_monotonic_milliseconds -
            header.last_observed_monotonic_milliseconds;
        if (input.observed_utc_milliseconds >
            header.last_observed_utc_milliseconds)
        {
            const utc_delta: u64 = @intCast(
                input.observed_utc_milliseconds -
                    header.last_observed_utc_milliseconds,
            );
            if (utc_delta > addSaturating(
                monotonic_delta,
                session.config.maximum_future_skew_milliseconds,
            )) return .stale_frame;
        }
    }
    if ((session.bank.operation_count != 0 or session.submit_sequence != 0) and
        (header.state_revision < session.state_revision or
            header.submit_sequence < session.submit_sequence or
            header.next_action_id < session.next_action_id or
            header.last_submit_epoch < session.last_submit_epoch or
            header.last_request_epoch < session.last_request_epoch or
            header.last_feedback_epoch < session.last_feedback_epoch or
            header.last_completion_epoch < session.last_completion_epoch or
            header.last_plan_epoch < session.last_plan_epoch or
            input.observed_monotonic_milliseconds <
                session.last_observed_monotonic_milliseconds))
    {
        return .stale_frame;
    }
    const base_revision = @max(session.state_revision, header.state_revision);
    if (base_revision == std.math.maxInt(u64)) return .out_of_memory;

    session.staging.reset();
    var last_submit_sequence: u64 = 0;
    for (records) |*persisted| {
        const validation = validateRecord(persisted, header);
        if (validation != .ok) {
            session.staging.reset();
            return validation;
        }
        if (persisted.submit_sequence <= last_submit_sequence) {
            session.staging.reset();
            return .invalid_argument;
        }
        last_submit_sequence = persisted.submit_sequence;
        const record_index = session.staging.allocateRecord() orelse {
            session.staging.reset();
            return .out_of_memory;
        };
        var record = recordFromPersistence(persisted);
        if (!same_clock) {
            rebaseRecord(
                &record,
                header.last_observed_utc_milliseconds,
                header.last_observed_monotonic_milliseconds,
                input.observed_utc_milliseconds,
                input.observed_monotonic_milliseconds,
            );
        }
        const original_state = record.state;
        switch (record.state) {
            .queued, .start_pending, .running, .cancel_pending, .retry_wait => {
                record.state = .recovery_pending;
                record.recovery_origin_state = @intFromEnum(original_state);
                record.flags |= protocol.OperationFlags.imported_recovery;
                record.retry_at_monotonic_milliseconds = 0;
                clearPendingAction(&record);
            },
            else => {},
        }
        record.state_revision = base_revision + 1;
        session.staging.records[record_index] = record;
        if (!session.staging.insertOperation(record.operation_id, record_index)) {
            session.staging.reset();
            return .invalid_argument;
        }
        if ((record.flags & protocol.OperationFlags.domain_valid) != 0 and
            !protocol.isTerminal(record.state))
        {
            if (session.staging.domain_count >= session.config.maximum_domain_count or
                !session.staging.insertDomain(record.domain_id, record_index))
            {
                session.staging.reset();
                return .invalid_argument;
            }
        }
    }
    var queue_count: u32 = 0;
    for (session.staging.records, 0..) |record, index| {
        if (!record.occupied) continue;
        session.staging.order_scratch[queue_count] = @intCast(index);
        queue_count += 1;
    }
    std.sort.heap(
        u32,
        session.staging.order_scratch[0..queue_count],
        &session.staging,
        queueOrderBefore,
    );
    var queue_high_water: u64 = 0;
    for (session.staging.order_scratch[0..queue_count]) |record_index| {
        const queue_order = session.staging.records[record_index].queue_order;
        if (queue_order <= queue_high_water) {
            session.staging.reset();
            return .invalid_argument;
        }
        queue_high_water = queue_order;
    }
    const previous = session.bank;
    session.bank = session.staging;
    session.staging = previous;
    session.staging.reset();
    session.state_revision = base_revision + 1;
    session.submit_sequence = header.submit_sequence;
    session.next_action_id = header.next_action_id;
    session.last_submit_epoch = header.last_submit_epoch;
    session.last_request_epoch = header.last_request_epoch;
    session.last_feedback_epoch = header.last_feedback_epoch;
    session.last_completion_epoch = header.last_completion_epoch;
    session.last_plan_epoch = header.last_plan_epoch;
    session.last_plan_configuration_generation = 0;
    session.last_persistence_epoch = input.operation_epoch;
    session.last_observed_utc_milliseconds =
        input.observed_utc_milliseconds;
    session.last_observed_monotonic_milliseconds =
        input.observed_monotonic_milliseconds;
    session.bank.clearActions();
    session.last_action_read_plan_epoch = 0;
    session.imported_queue_order_high_water = queue_high_water;
    _ = session.pruneTerminalsLocked(input.observed_monotonic_milliseconds);
    session.recomputeNextWake();
    return .ok;
}

fn validPersistenceHeader(
    header: *const protocol.PersistenceHeader,
    config: *const protocol.Config,
    record_count: usize,
) bool {
    return header.abi_version == protocol.abi_version and
        header.struct_size == @sizeOf(protocol.PersistenceHeader) and
        header.configuration_generation != 0 and
        header.configuration_generation <= config.generation and
        protocol.equalHandle(header.session_instance_id, config.session_instance_id) and
        !header.clock_instance_id.isZero() and
        header.operation_epoch != 0 and
        header.state_revision != 0 and
        ((header.submit_sequence == 0) == (header.last_submit_epoch == 0)) and
        header.operation_count == record_count and
        (header.operation_count == 0 or header.submit_sequence != 0) and
        header.operation_count <= config.maximum_operation_count and
        header.record_size == @sizeOf(protocol.PersistenceRecord) and
        @sizeOf(protocol.PersistenceHeader) +
            @as(u64, header.operation_count) * @sizeOf(protocol.PersistenceRecord) <=
            config.maximum_persistence_byte_count and
        header.next_action_id != 0 and
        header.last_observed_utc_milliseconds >= 0 and
        (header.operation_count == 0 or
            header.last_observed_monotonic_milliseconds != 0) and
        (header.flags & ~protocol.PersistenceFlags.known) == 0 and
        (header.checksum_high != 0 or header.checksum_low != 0) and
        protocol.allZero(&header.reserved);
}

fn validateRecord(
    persisted: *const protocol.PersistenceRecord,
    header: *const protocol.PersistenceHeader,
) ResultCode {
    const operation_state = protocol.stateFromInt(persisted.state) orelse
        return .invalid_argument;
    if (persisted.struct_size != @sizeOf(protocol.PersistenceRecord) or
        persisted.operation_id.isZero() or persisted.kind_handle.isZero() or
        persisted.submit_sequence == 0 or
        persisted.submit_sequence > header.submit_sequence or
        persisted.queue_order == 0 or
        persisted.state_revision == 0 or
        persisted.state_revision > header.state_revision or
        persisted.created_utc_milliseconds < 0 or
        persisted.created_monotonic_milliseconds == 0 or
        persisted.updated_utc_milliseconds < 0 or
        persisted.updated_monotonic_milliseconds <
            persisted.created_monotonic_milliseconds or
        persisted.updated_monotonic_milliseconds >
            header.last_observed_monotonic_milliseconds or
        persisted.maximum_attempts == 0 or
        persisted.attempt_number > persisted.maximum_attempts or
        persisted.execution_timeout_milliseconds == 0 or
        persisted.cancel_grace_milliseconds == 0 or
        persisted.terminal_retention_milliseconds == 0 or
        (persisted.flags & ~protocol.OperationFlags.known) != 0 or
        (persisted.progress_valid_mask & ~protocol.ProgressValid.known) != 0 or
        persisted.reserved_u32 != 0 or
        !protocol.allZero(&persisted.reserved))
    {
        return .invalid_argument;
    }
    if (((persisted.flags & protocol.OperationFlags.domain_valid) != 0) ==
        persisted.domain_id.isZero()) return .invalid_argument;
    if (((persisted.flags & protocol.OperationFlags.title_valid) != 0) ==
        persisted.title_handle.isZero()) return .invalid_argument;
    if (((persisted.flags & protocol.OperationFlags.request_valid) != 0) ==
        persisted.request_handle.isZero()) return .invalid_argument;
    if (((persisted.flags & protocol.OperationFlags.result_valid) != 0) ==
        persisted.result_handle.isZero()) return .invalid_argument;
    if (((persisted.flags & protocol.OperationFlags.error_valid) != 0) ==
        persisted.error_handle.isZero()) return .invalid_argument;
    if (((persisted.flags & protocol.OperationFlags.terminal) != 0) !=
        protocol.isTerminal(operation_state)) return .invalid_argument;
    if (((persisted.flags & protocol.OperationFlags.progress_valid) != 0) !=
        (persisted.progress_sequence != 0)) return .invalid_argument;
    const imported_origin = protocol.stateFromInt(persisted.recovery_origin_state);
    if ((persisted.flags & protocol.OperationFlags.imported_recovery) != 0) {
        const origin = imported_origin orelse return .invalid_argument;
        if (origin != .queued and origin != .start_pending and
            origin != .running and origin != .cancel_pending and
            origin != .retry_wait)
        {
            return .invalid_argument;
        }
        if (operation_state != .recovery_pending and
            !protocol.isTerminal(operation_state))
        {
            return .invalid_argument;
        }
    } else if (persisted.recovery_origin_state != 0) {
        return .invalid_argument;
    }
    if (operation_state == .recovery_pending and
        (persisted.flags & protocol.OperationFlags.imported_recovery) == 0)
    {
        return .invalid_argument;
    }
    if ((persisted.attempt_number == 0) != persisted.attempt_token.isZero() or
        (persisted.attempt_number == 0) !=
            (persisted.attempt_configuration_generation == 0) or
        persisted.attempt_configuration_generation >
            header.configuration_generation)
    {
        return .invalid_argument;
    }
    if (protocol.isTerminal(operation_state)) {
        if (persisted.terminal_at_monotonic_milliseconds == 0 or
            persisted.terminal_at_monotonic_milliseconds >
                persisted.updated_monotonic_milliseconds or
            persisted.terminal_retire_at_monotonic_milliseconds <
                persisted.terminal_at_monotonic_milliseconds)
        {
            return .invalid_argument;
        }
    } else if (persisted.terminal_at_monotonic_milliseconds != 0 or
        persisted.terminal_retire_at_monotonic_milliseconds != 0)
    {
        return .invalid_argument;
    }
    if (operation_state == .retry_wait) {
        if (persisted.attempt_number == 0 or
            persisted.retry_at_monotonic_milliseconds <
                persisted.updated_monotonic_milliseconds)
        {
            return .invalid_argument;
        }
    } else if (persisted.retry_at_monotonic_milliseconds != 0) {
        return .invalid_argument;
    }
    switch (operation_state) {
        .start_pending, .running, .cancel_pending => {
            if (persisted.attempt_number == 0 or persisted.attempt_token.isZero()) {
                return .invalid_argument;
            }
        },
        .queued, .retry_wait, .recovery_pending, .succeeded, .failed, .canceled, .state_uncertain => {},
    }
    if ((operation_state == .running or operation_state == .cancel_pending) and
        persisted.started_monotonic_milliseconds == 0)
    {
        return .invalid_argument;
    }
    if (persisted.started_monotonic_milliseconds >
        persisted.updated_monotonic_milliseconds)
    {
        return .invalid_argument;
    }
    if (operation_state == .start_pending and
        (persisted.started_monotonic_milliseconds != 0 or
            persisted.progress_sequence != 0 or
            (persisted.flags &
                (protocol.OperationFlags.result_valid |
                    protocol.OperationFlags.error_valid |
                    protocol.OperationFlags.progress_valid)) != 0))
    {
        return .invalid_argument;
    }
    if (operation_state == .cancel_pending and
        (persisted.flags & protocol.OperationFlags.cancel_requested) == 0)
    {
        return .invalid_argument;
    }
    if ((persisted.flags & protocol.OperationFlags.imported_recovery) != 0) {
        const origin = imported_origin.?;
        if ((origin != .queued and persisted.attempt_number == 0) or
            (origin == .start_pending and
                persisted.started_monotonic_milliseconds != 0) or
            ((origin == .running or origin == .cancel_pending) and
                persisted.started_monotonic_milliseconds == 0))
        {
            return .invalid_argument;
        }
    }
    if ((persisted.progress_valid_mask & protocol.ProgressValid.percent_milli) != 0) {
        if (persisted.percent_milli > 100_000) return .invalid_argument;
    } else if (persisted.percent_milli != 0) return .invalid_argument;
    if ((persisted.progress_valid_mask & protocol.ProgressValid.bytes_done) == 0 and
        persisted.bytes_done != 0) return .invalid_argument;
    if ((persisted.progress_valid_mask & protocol.ProgressValid.bytes_total) == 0 and
        persisted.bytes_total != 0) return .invalid_argument;
    if ((persisted.progress_valid_mask & protocol.ProgressValid.speed_bytes_per_second) == 0 and
        persisted.speed_bytes_per_second != 0) return .invalid_argument;
    if ((persisted.progress_valid_mask &
        (protocol.ProgressValid.bytes_done | protocol.ProgressValid.bytes_total)) ==
        (protocol.ProgressValid.bytes_done | protocol.ProgressValid.bytes_total) and
        persisted.bytes_done > persisted.bytes_total)
    {
        return .invalid_argument;
    }
    if (((persisted.progress_valid_mask & protocol.ProgressValid.stage) != 0) ==
        persisted.stage_handle.isZero()) return .invalid_argument;
    if (((persisted.progress_valid_mask & protocol.ProgressValid.message) != 0) ==
        persisted.message_handle.isZero()) return .invalid_argument;
    if (((persisted.progress_valid_mask & protocol.ProgressValid.checkpoint) != 0) ==
        persisted.checkpoint_handle.isZero()) return .invalid_argument;
    if (persisted.progress_sequence == 0 and persisted.progress_valid_mask != 0) {
        return .invalid_argument;
    }
    return .ok;
}

fn recordFromPersistence(persisted: *const protocol.PersistenceRecord) Record {
    return .{
        .occupied = true,
        .state = protocol.stateFromInt(persisted.state).?,
        .operation_id = persisted.operation_id,
        .domain_id = persisted.domain_id,
        .kind_handle = persisted.kind_handle,
        .title_handle = persisted.title_handle,
        .request_handle = persisted.request_handle,
        .result_handle = persisted.result_handle,
        .error_handle = persisted.error_handle,
        .attempt_token = persisted.attempt_token,
        .priority = persisted.priority,
        .submit_sequence = persisted.submit_sequence,
        .state_revision = persisted.state_revision,
        .created_utc_milliseconds = persisted.created_utc_milliseconds,
        .created_monotonic_milliseconds = persisted.created_monotonic_milliseconds,
        .updated_utc_milliseconds = persisted.updated_utc_milliseconds,
        .updated_monotonic_milliseconds = persisted.updated_monotonic_milliseconds,
        .started_monotonic_milliseconds = persisted.started_monotonic_milliseconds,
        .retry_at_monotonic_milliseconds = persisted.retry_at_monotonic_milliseconds,
        .terminal_at_monotonic_milliseconds = persisted.terminal_at_monotonic_milliseconds,
        .terminal_retire_at_monotonic_milliseconds = persisted.terminal_retire_at_monotonic_milliseconds,
        .maximum_attempts = persisted.maximum_attempts,
        .attempt_number = persisted.attempt_number,
        .retry_delay_milliseconds = persisted.retry_delay_milliseconds,
        .recovery_origin_state = persisted.recovery_origin_state,
        .execution_timeout_milliseconds = persisted.execution_timeout_milliseconds,
        .cancel_grace_milliseconds = persisted.cancel_grace_milliseconds,
        .terminal_retention_milliseconds = persisted.terminal_retention_milliseconds,
        .progress_sequence = persisted.progress_sequence,
        .progress_valid_mask = persisted.progress_valid_mask,
        .percent_milli = persisted.percent_milli,
        .bytes_done = persisted.bytes_done,
        .bytes_total = persisted.bytes_total,
        .speed_bytes_per_second = persisted.speed_bytes_per_second,
        .stage_handle = persisted.stage_handle,
        .message_handle = persisted.message_handle,
        .checkpoint_handle = persisted.checkpoint_handle,
        .flags = persisted.flags,
        .attempt_configuration_generation = persisted.attempt_configuration_generation,
        .queue_order = persisted.queue_order,
        .session_local = false,
    };
}

fn rebaseRecord(
    record: *Record,
    previous_utc: i64,
    previous_monotonic: u64,
    current_utc: i64,
    current_monotonic: u64,
) void {
    const offline_elapsed: u64 = if (current_utc > previous_utc)
        @intCast(current_utc - previous_utc)
    else
        0;
    record.created_monotonic_milliseconds = rebaseHistorical(
        record.created_monotonic_milliseconds,
        previous_monotonic,
        current_monotonic,
        offline_elapsed,
    );
    record.updated_monotonic_milliseconds = rebaseHistorical(
        record.updated_monotonic_milliseconds,
        previous_monotonic,
        current_monotonic,
        offline_elapsed,
    );
    record.started_monotonic_milliseconds = rebaseHistorical(
        record.started_monotonic_milliseconds,
        previous_monotonic,
        current_monotonic,
        offline_elapsed,
    );
    record.terminal_at_monotonic_milliseconds = rebaseHistorical(
        record.terminal_at_monotonic_milliseconds,
        previous_monotonic,
        current_monotonic,
        offline_elapsed,
    );
    record.retry_at_monotonic_milliseconds = rebaseDeadline(
        record.retry_at_monotonic_milliseconds,
        previous_monotonic,
        current_monotonic,
        offline_elapsed,
    );
    record.terminal_retire_at_monotonic_milliseconds = rebaseDeadline(
        record.terminal_retire_at_monotonic_milliseconds,
        previous_monotonic,
        current_monotonic,
        offline_elapsed,
    );
}

fn rebaseHistorical(
    value: u64,
    previous_monotonic: u64,
    current_monotonic: u64,
    offline_elapsed: u64,
) u64 {
    if (value == 0) return 0;
    const prior_age = if (value <= previous_monotonic)
        previous_monotonic - value
    else
        0;
    const total_age = addSaturating(prior_age, offline_elapsed);
    return if (total_age >= current_monotonic)
        1
    else
        current_monotonic - total_age;
}

fn rebaseDeadline(
    value: u64,
    previous_monotonic: u64,
    current_monotonic: u64,
    offline_elapsed: u64,
) u64 {
    if (value == 0) return 0;
    if (value <= previous_monotonic) return current_monotonic;
    const remaining_at_export = value - previous_monotonic;
    const remaining = remaining_at_export -| offline_elapsed;
    return addSaturating(current_monotonic, remaining);
}

fn addSaturating(left: u64, right: u64) u64 {
    return std.math.add(u64, left, right) catch std.math.maxInt(u64);
}

fn persistenceChecksum(
    header: *const protocol.PersistenceHeader,
    records: []const protocol.PersistenceRecord,
) protocol.Handle128 {
    var copy = header.*;
    copy.checksum_high = 0;
    copy.checksum_low = 0;
    var high: u64 = 0xcbf29ce484222325;
    var low: u64 = 0x84222325cbf29ce4;
    hashBytes(&high, &low, std.mem.asBytes(&copy));
    hashBytes(&high, &low, std.mem.sliceAsBytes(records));
    if (high == 0 and low == 0) low = 1;
    return .{ .high = high, .low = low };
}

fn hashBytes(high: *u64, low: *u64, bytes: []const u8) void {
    for (bytes) |byte| {
        high.* ^= byte;
        high.* *%= 0x100000001b3;
        low.* ^= @as(u64, byte) +% 0x9e;
        low.* *%= 0x100000001b3;
        low.* = std.math.rotl(u64, low.*, 7);
    }
}

fn operationBySubmitSequence(bank: *Bank, left_index: u32, right_index: u32) bool {
    const left = bank.records[left_index];
    const right = bank.records[right_index];
    if (left.submit_sequence != right.submit_sequence) {
        return left.submit_sequence < right.submit_sequence;
    }
    return left.operation_id.high < right.operation_id.high or
        (left.operation_id.high == right.operation_id.high and
            left.operation_id.low < right.operation_id.low);
}

fn queueOrderBefore(bank: *Bank, left_index: u32, right_index: u32) bool {
    const left = bank.records[left_index];
    const right = bank.records[right_index];
    if (left.queue_order != right.queue_order) {
        return left.queue_order < right.queue_order;
    }
    return left.operation_id.high < right.operation_id.high or
        (left.operation_id.high == right.operation_id.high and
            left.operation_id.low < right.operation_id.low);
}

fn clearPendingAction(record: *Record) void {
    record.pending_action_id = 0;
    record.pending_action_kind = 0;
    record.pending_plan_epoch = 0;
    record.pending_configuration_generation = 0;
    record.pending_action_token = .{ .high = 0, .low = 0 };
    record.pending_deadline_monotonic_milliseconds = 0;
}

fn lockMutex(mutex: *windows.SRWLOCK) void {
    windows.ntdll.RtlAcquireSRWLockExclusive(mutex);
}

fn unlockMutex(mutex: *windows.SRWLOCK) void {
    windows.ntdll.RtlReleaseSRWLockExclusive(mutex);
}

test "persistence record validation closes state-specific fields" {
    var header = std.mem.zeroes(protocol.PersistenceHeader);
    header.configuration_generation = 1;
    header.state_revision = 7;
    header.submit_sequence = 1;
    header.last_observed_utc_milliseconds = 1_000;
    header.last_observed_monotonic_milliseconds = 1_000;

    var record = validTestRecord();
    try std.testing.expectEqual(ResultCode.ok, validateRecord(&record, &header));

    record.state = @intFromEnum(protocol.OperationState.start_pending);
    record.attempt_number = 1;
    record.attempt_token = .{ .high = 0, .low = 3 };
    record.attempt_configuration_generation = 1;
    try std.testing.expectEqual(ResultCode.ok, validateRecord(&record, &header));
    record.started_monotonic_milliseconds = 10;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        validateRecord(&record, &header),
    );

    record = validTestRecord();
    record.state = @intFromEnum(protocol.OperationState.cancel_pending);
    record.attempt_number = 1;
    record.attempt_token = .{ .high = 0, .low = 3 };
    record.attempt_configuration_generation = 1;
    record.started_monotonic_milliseconds = 10;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        validateRecord(&record, &header),
    );
    record.flags |= protocol.OperationFlags.cancel_requested;
    try std.testing.expectEqual(ResultCode.ok, validateRecord(&record, &header));

    record = validTestRecord();
    record.state = @intFromEnum(protocol.OperationState.recovery_pending);
    record.attempt_number = 1;
    record.attempt_token = .{ .high = 0, .low = 3 };
    record.attempt_configuration_generation = 1;
    record.started_monotonic_milliseconds = 10;
    record.recovery_origin_state = @intFromEnum(protocol.OperationState.running);
    record.flags |= protocol.OperationFlags.imported_recovery;
    try std.testing.expectEqual(ResultCode.ok, validateRecord(&record, &header));
    record.recovery_origin_state = @intFromEnum(protocol.OperationState.start_pending);
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        validateRecord(&record, &header),
    );

    record = validTestRecord();
    record.state = @intFromEnum(protocol.OperationState.recovery_pending);
    record.recovery_origin_state = @intFromEnum(protocol.OperationState.queued);
    record.flags |= protocol.OperationFlags.imported_recovery;
    try std.testing.expectEqual(ResultCode.ok, validateRecord(&record, &header));
    inline for ([_]protocol.OperationState{
        .start_pending,
        .running,
        .cancel_pending,
        .retry_wait,
    }) |origin| {
        record.recovery_origin_state = @intFromEnum(origin);
        try std.testing.expectEqual(
            ResultCode.invalid_argument,
            validateRecord(&record, &header),
        );
    }

    record = validTestRecord();
    record.state = @intFromEnum(protocol.OperationState.succeeded);
    record.flags = protocol.OperationFlags.terminal |
        protocol.OperationFlags.imported_recovery;
    record.terminal_at_monotonic_milliseconds = 10;
    record.terminal_retire_at_monotonic_milliseconds = 20;
    record.recovery_origin_state = @intFromEnum(protocol.OperationState.queued);
    try std.testing.expectEqual(ResultCode.ok, validateRecord(&record, &header));
    inline for ([_]protocol.OperationState{
        .start_pending,
        .running,
        .cancel_pending,
        .retry_wait,
    }) |origin| {
        record.recovery_origin_state = @intFromEnum(origin);
        record.started_monotonic_milliseconds =
            if (origin == .running or origin == .cancel_pending) 10 else 0;
        try std.testing.expectEqual(
            ResultCode.invalid_argument,
            validateRecord(&record, &header),
        );
    }

    record = validTestRecord();
    record.state = @intFromEnum(protocol.OperationState.succeeded);
    record.flags |= protocol.OperationFlags.terminal;
    record.terminal_at_monotonic_milliseconds = 10;
    record.terminal_retire_at_monotonic_milliseconds = 20;
    try std.testing.expectEqual(ResultCode.ok, validateRecord(&record, &header));
    record.terminal_retire_at_monotonic_milliseconds = 9;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        validateRecord(&record, &header),
    );

    record = validTestRecord();
    record.progress_sequence = 1;
    record.flags |= protocol.OperationFlags.progress_valid;
    try std.testing.expectEqual(ResultCode.ok, validateRecord(&record, &header));
    record.reserved_u32 = 1;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        validateRecord(&record, &header),
    );
}

fn validTestRecord() protocol.PersistenceRecord {
    return .{
        .struct_size = @sizeOf(protocol.PersistenceRecord),
        .state = @intFromEnum(protocol.OperationState.queued),
        .operation_id = .{ .high = 0, .low = 1 },
        .domain_id = .{ .high = 0, .low = 0 },
        .kind_handle = .{ .high = 0, .low = 2 },
        .title_handle = .{ .high = 0, .low = 0 },
        .request_handle = .{ .high = 0, .low = 0 },
        .result_handle = .{ .high = 0, .low = 0 },
        .error_handle = .{ .high = 0, .low = 0 },
        .attempt_token = .{ .high = 0, .low = 0 },
        .priority = 0,
        .submit_sequence = 1,
        .state_revision = 7,
        .created_utc_milliseconds = 10,
        .created_monotonic_milliseconds = 10,
        .updated_utc_milliseconds = 10,
        .updated_monotonic_milliseconds = 10,
        .started_monotonic_milliseconds = 0,
        .retry_at_monotonic_milliseconds = 0,
        .terminal_at_monotonic_milliseconds = 0,
        .terminal_retire_at_monotonic_milliseconds = 0,
        .maximum_attempts = 3,
        .attempt_number = 0,
        .retry_delay_milliseconds = 0,
        .recovery_origin_state = 0,
        .execution_timeout_milliseconds = 100,
        .cancel_grace_milliseconds = 20,
        .terminal_retention_milliseconds = 100,
        .progress_sequence = 0,
        .progress_valid_mask = 0,
        .percent_milli = 0,
        .reserved_u32 = 0,
        .bytes_done = 0,
        .bytes_total = 0,
        .speed_bytes_per_second = 0,
        .stage_handle = .{ .high = 0, .low = 0 },
        .message_handle = .{ .high = 0, .low = 0 },
        .checkpoint_handle = .{ .high = 0, .low = 0 },
        .flags = 0,
        .attempt_configuration_generation = 0,
        .queue_order = 1,
        .reserved = [_]u64{0} ** 2,
    };
}
