const std = @import("std");
const windows = std.os.windows;
const protocol = @import("protocol.zig");
const bank_module = @import("bank.zig");
const persistence = @import("persistence.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const Bank = bank_module.Bank;
const Record = bank_module.Record;

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: windows.SRWLOCK = windows.SRWLOCK_INIT,
    config: protocol.Config,
    resident_byte_count: u64,
    bank: Bank,
    staging: Bank,
    state_revision: u64 = 1,
    submit_sequence: u64 = 0,
    next_action_id: u64 = 1,
    last_submit_epoch: u64 = 0,
    last_request_epoch: u64 = 0,
    last_feedback_epoch: u64 = 0,
    last_completion_epoch: u64 = 0,
    last_plan_epoch: u64 = 0,
    last_plan_configuration_generation: u64 = 0,
    last_persistence_epoch: u64 = 0,
    last_observed_utc_milliseconds: i64 = 0,
    last_observed_monotonic_milliseconds: u64 = 0,
    next_wake_monotonic_milliseconds: u64 = 0,
    last_action_read_plan_epoch: u64 = 0,
    imported_queue_order_high_water: u64 = 0,

    pub fn create(config: *const protocol.Config) !*Session {
        if (!protocol.validConfig(config)) return error.InvalidConfiguration;
        const page_size: u64 = @intCast(std.heap.pageSize());
        const bank_resident = bank_module.committedByteCount(config, page_size) catch
            return error.InvalidConfiguration;
        const bank_buffers = std.math.mul(u64, bank_resident, 2) catch
            return error.InvalidConfiguration;
        const session_padded = std.math.add(
            u64,
            @sizeOf(Session),
            page_size - 1,
        ) catch return error.InvalidConfiguration;
        const session_resident = (session_padded / page_size) * page_size;
        const resident = std.math.add(u64, session_resident, bank_buffers) catch
            return error.InvalidConfiguration;
        if (resident > config.resident_byte_budget) return error.InvalidConfiguration;

        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);
        var bank = try Bank.create(allocator, config);
        errdefer bank.destroy();
        var staging = try Bank.create(allocator, config);
        errdefer staging.destroy();
        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .resident_byte_count = resident,
            .bank = bank,
            .staging = staging,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        self.staging.destroy();
        self.bank.destroy();
        allocator.destroy(self);
    }

    pub fn capacity(self: *Session) protocol.Capacity {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .operation_capacity = self.config.maximum_operation_count,
            .domain_capacity = self.config.maximum_domain_count,
            .action_capacity = self.config.maximum_action_count,
            .operation_index_capacity = self.config.operation_index_capacity,
            .domain_index_capacity = self.config.domain_index_capacity,
            .order_scratch_capacity = self.config.maximum_operation_count,
            .persistence_capacity = self.config.maximum_operation_count,
            .session_instance_id = self.config.session_instance_id,
            .clock_instance_id = self.config.clock_instance_id,
            .maximum_persistence_byte_count = self.config.maximum_persistence_byte_count,
            .resident_byte_count = self.resident_byte_count,
            .reserved = [_]u64{0} ** 4,
        };
    }

    pub fn reconfigure(self: *Session, config: *const protocol.Config) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (!protocol.validConfig(config)) return .abi_mismatch;
        if (config.generation <= self.config.generation) return .stale_frame;
        if (!sameStaticCapacity(config, &self.config)) return .invalid_argument;
        if (self.resident_byte_count > config.resident_byte_budget) {
            return .invalid_argument;
        }
        const prune_required =
            self.terminalCount() > config.maximum_recent_terminal_count;
        if (prune_required and !self.canAdvanceRevision()) return .out_of_memory;
        self.config = config.*;
        if (prune_required) {
            _ = self.pruneTerminalsLocked(self.last_observed_monotonic_milliseconds);
            self.advanceRevisionAll();
        }
        self.recomputeNextWake();
        return .ok;
    }

    pub fn submit(
        self: *Session,
        input: *const protocol.SubmitInput,
        output: *protocol.SubmitOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (!protocol.validSubmit(input, &self.config)) return .abi_mismatch;
        if (input.submit_epoch <= self.last_submit_epoch) return .stale_frame;
        if (!self.validAfter(
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
            input.created_utc_milliseconds,
            input.created_monotonic_milliseconds,
        )) return .stale_frame;
        if (self.bank.findOperation(input.operation_id)) |existing_index| {
            const existing = &self.bank.records[existing_index];
            if (!sameSubmissionIdentity(existing, input)) return .invalid_argument;
            self.last_submit_epoch = input.submit_epoch;
            self.commitObserved(
                input.observed_utc_milliseconds,
                input.observed_monotonic_milliseconds,
            );
            output.* = .{
                .struct_size = @sizeOf(protocol.SubmitOutput),
                .flags = protocol.SubmitOutputFlags.operation_id_duplicate,
                .operation_id = existing.operation_id,
                .state_revision = self.state_revision,
                .submit_sequence = existing.submit_sequence,
                .state = @intFromEnum(existing.state),
                .reserved_u32 = 0,
                .reserved = [_]u64{0} ** 3,
            };
            return .ok;
        }

        if ((input.valid_mask & protocol.SubmitValid.domain) != 0) {
            if (self.bank.findDomain(input.domain_id)) |existing_index| {
                const existing = &self.bank.records[existing_index];
                self.last_submit_epoch = input.submit_epoch;
                self.commitObserved(
                    input.observed_utc_milliseconds,
                    input.observed_monotonic_milliseconds,
                );
                output.* = .{
                    .struct_size = @sizeOf(protocol.SubmitOutput),
                    .flags = protocol.SubmitOutputFlags.active_domain_duplicate,
                    .operation_id = existing.operation_id,
                    .state_revision = self.state_revision,
                    .submit_sequence = existing.submit_sequence,
                    .state = @intFromEnum(existing.state),
                    .reserved_u32 = 0,
                    .reserved = [_]u64{0} ** 3,
                };
                return .ok;
            }
            if (self.bank.domain_count >= self.config.maximum_domain_count) {
                return .out_of_memory;
            }
        }

        if (!self.canAdvanceRevision() or self.submit_sequence == std.math.maxInt(u64)) {
            return .out_of_memory;
        }
        const next_local_queue_count = self.sessionLocalQueueCount() + 1;
        if (self.imported_queue_order_high_water >
            std.math.maxInt(u64) - next_local_queue_count)
        {
            return .out_of_memory;
        }
        const record_index = self.bank.allocateRecord() orelse return .out_of_memory;
        errdefer self.bank.releaseRecord(record_index);
        const allocated_record = &self.bank.records[record_index];
        allocated_record.operation_id = input.operation_id;
        allocated_record.domain_id = input.domain_id;
        if ((input.valid_mask & protocol.SubmitValid.domain) != 0) {
            allocated_record.flags |= protocol.OperationFlags.domain_valid;
        }
        if (!self.bank.insertOperation(input.operation_id, record_index)) {
            self.bank.releaseRecord(record_index);
            return .out_of_memory;
        }
        if ((input.valid_mask & protocol.SubmitValid.domain) != 0 and
            !self.bank.insertDomain(input.domain_id, record_index))
        {
            self.bank.releaseRecord(record_index);
            return .out_of_memory;
        }

        self.submit_sequence += 1;
        var flags: u64 = 0;
        if ((input.valid_mask & protocol.SubmitValid.domain) != 0) {
            flags |= protocol.OperationFlags.domain_valid;
        }
        if ((input.valid_mask & protocol.SubmitValid.title) != 0) {
            flags |= protocol.OperationFlags.title_valid;
        }
        if ((input.valid_mask & protocol.SubmitValid.request) != 0) {
            flags |= protocol.OperationFlags.request_valid;
        }
        const record = &self.bank.records[record_index];
        record.* = .{
            .occupied = true,
            .state = .queued,
            .operation_id = input.operation_id,
            .domain_id = input.domain_id,
            .kind_handle = input.kind_handle,
            .title_handle = input.title_handle,
            .request_handle = input.request_handle,
            .priority = input.priority,
            .submit_sequence = self.submit_sequence,
            .state_revision = self.state_revision + 1,
            .created_utc_milliseconds = input.created_utc_milliseconds,
            .created_monotonic_milliseconds = input.created_monotonic_milliseconds,
            .updated_utc_milliseconds = input.observed_utc_milliseconds,
            .updated_monotonic_milliseconds = input.observed_monotonic_milliseconds,
            .maximum_attempts = input.maximum_attempts,
            .retry_delay_milliseconds = input.retry_delay_milliseconds,
            .execution_timeout_milliseconds = input.execution_timeout_milliseconds,
            .cancel_grace_milliseconds = input.cancel_grace_milliseconds,
            .terminal_retention_milliseconds = input.terminal_retention_milliseconds,
            .flags = flags,
            .session_local = true,
        };
        self.rerankSessionLocalQueue();
        self.advanceRevisionAll();
        self.last_submit_epoch = input.submit_epoch;
        self.commitObserved(
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
        );
        self.recomputeNextWake();
        output.* = .{
            .struct_size = @sizeOf(protocol.SubmitOutput),
            .flags = protocol.SubmitOutputFlags.inserted,
            .operation_id = input.operation_id,
            .state_revision = self.state_revision,
            .submit_sequence = self.submit_sequence,
            .state = @intFromEnum(protocol.OperationState.queued),
            .reserved_u32 = 0,
            .reserved = [_]u64{0} ** 3,
        };
        return .ok;
    }

    pub fn cancel(
        self: *Session,
        input: *const protocol.CancelInput,
        output: *protocol.CancelOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (!protocol.validCancel(input, &self.config)) return .abi_mismatch;
        if (input.request_epoch <= self.last_request_epoch) return .stale_frame;
        const index = self.bank.findOperation(input.operation_id) orelse return .no_data;
        const record = &self.bank.records[index];
        if (!self.validAfter(
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
            record.updated_utc_milliseconds,
            record.updated_monotonic_milliseconds,
        )) return .stale_frame;
        var changed = false;
        switch (record.state) {
            .queued, .retry_wait => {
                if (!self.canAdvanceRevision()) return .out_of_memory;
                self.finishRecord(
                    record,
                    .canceled,
                    input.observed_utc_milliseconds,
                    input.observed_monotonic_milliseconds,
                );
                changed = true;
            },
            .start_pending, .running, .recovery_pending => {
                if ((record.flags & protocol.OperationFlags.cancel_requested) == 0) {
                    if (!self.canAdvanceRevision()) return .out_of_memory;
                    record.flags |= protocol.OperationFlags.cancel_requested;
                    updateRecordObserved(
                        record,
                        input.observed_utc_milliseconds,
                        input.observed_monotonic_milliseconds,
                    );
                    changed = true;
                }
            },
            .cancel_pending => {},
            .succeeded, .failed, .canceled, .state_uncertain => {},
        }
        const prune_terminal = changed and protocol.isTerminal(record.state);
        if (changed) self.advanceRevisionAll();
        self.last_request_epoch = input.request_epoch;
        self.commitObserved(
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
        );
        output.* = .{
            .struct_size = @sizeOf(protocol.CancelOutput),
            .state = @intFromEnum(record.state),
            .operation_id = record.operation_id,
            .state_revision = self.state_revision,
            .flags = record.flags,
            .reserved = [_]u64{0} ** 3,
        };
        if (prune_terminal) _ = self.pruneTerminalsLocked(
            input.observed_monotonic_milliseconds,
        );
        self.recomputeNextWake();
        return .ok;
    }

    pub fn plan(
        self: *Session,
        input: *const protocol.PlanInput,
        output: *protocol.PlanOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (!protocol.validPlan(input, &self.config)) return .abi_mismatch;
        if (input.plan_epoch <= self.last_plan_epoch) return .stale_frame;
        if (!self.validGlobalObserved(
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
        )) return .stale_frame;
        if (self.bank.action_count != 0 and
            self.last_action_read_plan_epoch != self.last_plan_epoch)
        {
            return .unavailable;
        }
        if (!self.canAdvanceRevision()) return .out_of_memory;
        const action_limit: u32 = @min(input.action_capacity, self.config.maximum_action_count);
        if (action_limit != 0 and
            (self.next_action_id == 0 or
                self.next_action_id >
                    std.math.maxInt(u64) - @as(u64, action_limit)))
        {
            return .out_of_memory;
        }

        self.bank.clearActions();
        self.last_action_read_plan_epoch = 0;
        var changed = false;
        var index: u32 = 0;
        while (index < self.bank.records.len) : (index += 1) {
            const record = &self.bank.records[index];
            if (!record.occupied) continue;
            switch (record.state) {
                .start_pending, .recovery_pending => {
                    if (record.pending_action_id != 0 and
                        record.pending_deadline_monotonic_milliseconds <=
                            input.observed_monotonic_milliseconds)
                    {
                        self.finishRecord(
                            record,
                            .state_uncertain,
                            input.observed_utc_milliseconds,
                            input.observed_monotonic_milliseconds,
                        );
                        changed = true;
                    }
                },
                .retry_wait => {
                    if (record.retry_at_monotonic_milliseconds <=
                        input.observed_monotonic_milliseconds)
                    {
                        record.state = .queued;
                        record.retry_at_monotonic_milliseconds = 0;
                        updateRecordObserved(
                            record,
                            input.observed_utc_milliseconds,
                            input.observed_monotonic_milliseconds,
                        );
                        changed = true;
                    }
                },
                .running => {
                    const deadline = addSaturating(
                        record.started_monotonic_milliseconds,
                        record.execution_timeout_milliseconds,
                    );
                    if (deadline <= input.observed_monotonic_milliseconds and
                        (record.flags & protocol.OperationFlags.cancel_requested) == 0)
                    {
                        record.flags |= protocol.OperationFlags.cancel_requested;
                        updateRecordObserved(
                            record,
                            input.observed_utc_milliseconds,
                            input.observed_monotonic_milliseconds,
                        );
                        changed = true;
                    }
                },
                .cancel_pending => {
                    if (record.pending_deadline_monotonic_milliseconds <=
                        input.observed_monotonic_milliseconds)
                    {
                        self.finishRecord(
                            record,
                            .state_uncertain,
                            input.observed_utc_milliseconds,
                            input.observed_monotonic_milliseconds,
                        );
                        changed = true;
                    }
                },
                else => {},
            }
        }

        if (self.pruneTerminalsLocked(input.observed_monotonic_milliseconds)) {
            changed = true;
        }

        var candidate_count: u32 = 0;
        index = 0;
        while (index < self.bank.records.len) : (index += 1) {
            const record = self.bank.records[index];
            if (record.occupied and record.pending_action_id == 0 and
                record.state == .recovery_pending)
            {
                self.bank.order_scratch[candidate_count] = index;
                candidate_count += 1;
            }
        }
        std.sort.heap(
            u32,
            self.bank.order_scratch[0..candidate_count],
            self,
            operationBySubmitSequence,
        );
        var candidate: u32 = 0;
        while (candidate < candidate_count and
            self.bank.action_count < action_limit and
            candidate < self.config.maximum_recover_actions_per_plan) : (candidate += 1)
        {
            const record = &self.bank.records[self.bank.order_scratch[candidate]];
            if (record.state == .recovery_pending and record.pending_action_id == 0) {
                if (!self.emitAction(
                    record,
                    .recover,
                    input.plan_epoch,
                    input.observed_utc_milliseconds,
                    input.observed_monotonic_milliseconds,
                    0,
                )) {
                    return .out_of_memory;
                }
                changed = true;
            }
        }

        candidate_count = 0;
        index = 0;
        while (index < self.bank.records.len) : (index += 1) {
            const record = self.bank.records[index];
            if (record.occupied and record.pending_action_id == 0 and
                record.state == .running and
                (record.flags & protocol.OperationFlags.cancel_requested) != 0)
            {
                self.bank.order_scratch[candidate_count] = index;
                candidate_count += 1;
            }
        }
        std.sort.heap(
            u32,
            self.bank.order_scratch[0..candidate_count],
            self,
            cancelBefore,
        );
        candidate = 0;
        while (candidate < candidate_count and
            self.bank.action_count < action_limit and
            candidate < self.config.maximum_cancel_actions_per_plan) : (candidate += 1)
        {
            const record = &self.bank.records[self.bank.order_scratch[candidate]];
            var reason: u64 = protocol.ActionFlags.cancel_requested;
            const execution_deadline = addSaturating(
                record.started_monotonic_milliseconds,
                record.execution_timeout_milliseconds,
            );
            if (execution_deadline <= input.observed_monotonic_milliseconds) {
                reason |= protocol.ActionFlags.execution_timeout;
            }
            if (!self.emitAction(
                record,
                .cancel,
                input.plan_epoch,
                input.observed_utc_milliseconds,
                input.observed_monotonic_milliseconds,
                reason,
            )) return .out_of_memory;
            changed = true;
        }

        var slot_count = self.slotConsumingCount();
        candidate_count = 0;
        if (slot_count < self.config.maximum_global_running_count and
            self.bank.action_count < action_limit)
        {
            index = 0;
            while (index < self.bank.records.len) : (index += 1) {
                const record = self.bank.records[index];
                if (record.occupied and record.state == .queued) {
                    self.bank.order_scratch[candidate_count] = index;
                    candidate_count += 1;
                }
            }
            std.sort.heap(
                u32,
                self.bank.order_scratch[0..candidate_count],
                self,
                operationBefore,
            );
            candidate = 0;
            var start_action_count: u32 = 0;
            while (candidate < candidate_count and
                slot_count < self.config.maximum_global_running_count and
                self.bank.action_count < action_limit and
                start_action_count < self.config.maximum_start_actions_per_plan) : (candidate += 1)
            {
                const record = &self.bank.records[self.bank.order_scratch[candidate]];
                if (record.attempt_number >= record.maximum_attempts) {
                    self.finishRecord(
                        record,
                        .failed,
                        input.observed_utc_milliseconds,
                        input.observed_monotonic_milliseconds,
                    );
                    changed = true;
                    continue;
                }
                record.attempt_number += 1;
                resetAttemptTransient(record);
                record.attempt_configuration_generation = self.config.generation;
                record.attempt_token = deriveAttemptToken(
                    record.operation_id,
                    record.attempt_number,
                    self.next_action_id,
                );
                if (!self.emitAction(
                    record,
                    .start,
                    input.plan_epoch,
                    input.observed_utc_milliseconds,
                    input.observed_monotonic_milliseconds,
                    0,
                )) return .out_of_memory;
                slot_count += 1;
                start_action_count += 1;
                changed = true;
            }
        }

        if (self.pruneTerminalsLocked(input.observed_monotonic_milliseconds)) {
            changed = true;
        }
        self.last_plan_epoch = input.plan_epoch;
        self.last_plan_configuration_generation = self.config.generation;
        self.commitObserved(
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
        );
        if (changed) {
            if (!self.canAdvanceRevision()) return .out_of_memory;
            self.advanceRevisionAll();
        }
        self.recomputeNextWake();
        const counts = self.countStates();
        output.* = .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.PlanOutput),
            .configuration_generation = self.config.generation,
            .plan_epoch = input.plan_epoch,
            .state_revision = self.state_revision,
            .action_count = self.bank.action_count,
            .active_operation_count = counts.active,
            .running_operation_count = counts.running,
            .terminal_operation_count = counts.terminal,
            .next_wake_monotonic_milliseconds = self.next_wake_monotonic_milliseconds,
            .flags = if (self.next_wake_monotonic_milliseconds != 0)
                protocol.SnapshotFlags.next_wake_valid
            else
                0,
            .reserved = [_]u64{0} ** 4,
        };
        return .ok;
    }

    pub fn feedback(self: *Session, input: *const protocol.ActionFeedbackInput) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (!protocol.validFeedback(input, &self.config)) return .abi_mismatch;
        if (input.feedback_epoch <= self.last_feedback_epoch) return .stale_frame;
        if (!self.canAdvanceRevision()) return .out_of_memory;
        const index = self.bank.findOperation(input.operation_id) orelse return .no_data;
        const record = &self.bank.records[index];
        if (record.pending_action_id != input.action_id or
            record.pending_plan_epoch != input.plan_epoch or
            record.pending_configuration_generation !=
                input.configuration_generation or
            record.pending_action_kind != input.action_kind or
            !protocol.equalHandle(record.pending_action_token, input.attempt_token))
        {
            return .stale_frame;
        }
        if (!self.validAfter(
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
            record.updated_utc_milliseconds,
            record.updated_monotonic_milliseconds,
        )) return .stale_frame;
        const kind = protocol.actionKindFromInt(input.action_kind) orelse
            return .abi_mismatch;
        const outcome = protocol.feedbackOutcomeFromInt(input.outcome) orelse
            return .abi_mismatch;
        if (!compatibleFeedback(kind, outcome)) return .invalid_argument;
        if (input.observed_monotonic_milliseconds >=
            record.pending_deadline_monotonic_milliseconds)
        {
            return .stale_frame;
        }

        self.applyOutcomeHandles(record, input.result_handle, input.error_handle, input.valid_mask);
        switch (outcome) {
            .started => {
                record.state = .running;
                record.started_monotonic_milliseconds =
                    input.observed_monotonic_milliseconds;
                clearPendingAction(record);
            },
            .start_retryable_failure => {
                clearPendingAction(record);
                if ((record.flags & protocol.OperationFlags.cancel_requested) != 0) {
                    self.finishRecord(
                        record,
                        .canceled,
                        input.observed_utc_milliseconds,
                        input.observed_monotonic_milliseconds,
                    );
                } else {
                    self.retryOrFail(
                        record,
                        input.observed_utc_milliseconds,
                        input.observed_monotonic_milliseconds,
                    );
                }
            },
            .start_terminal_failure => {
                self.finishRecord(
                    record,
                    .failed,
                    input.observed_utc_milliseconds,
                    input.observed_monotonic_milliseconds,
                );
            },
            .cancel_completed => {
                self.finishRecord(
                    record,
                    .canceled,
                    input.observed_utc_milliseconds,
                    input.observed_monotonic_milliseconds,
                );
            },
            .cancel_retryable_failure => {
                record.state = .running;
                clearPendingAction(record);
            },
            .recovered_queued => {
                clearPendingAction(record);
                if ((record.flags & protocol.OperationFlags.cancel_requested) != 0) {
                    self.finishRecord(
                        record,
                        .canceled,
                        input.observed_utc_milliseconds,
                        input.observed_monotonic_milliseconds,
                    );
                } else {
                    record.state = .queued;
                    record.retry_at_monotonic_milliseconds = 0;
                    record.recovery_origin_state = 0;
                    record.flags &= ~protocol.OperationFlags.imported_recovery;
                }
            },
            .recovered_succeeded => self.finishRecord(
                record,
                .succeeded,
                input.observed_utc_milliseconds,
                input.observed_monotonic_milliseconds,
            ),
            .recovered_failed => self.finishRecord(
                record,
                .failed,
                input.observed_utc_milliseconds,
                input.observed_monotonic_milliseconds,
            ),
            .recovered_canceled => self.finishRecord(
                record,
                .canceled,
                input.observed_utc_milliseconds,
                input.observed_monotonic_milliseconds,
            ),
            .recovered_uncertain => self.finishRecord(
                record,
                .state_uncertain,
                input.observed_utc_milliseconds,
                input.observed_monotonic_milliseconds,
            ),
        }
        updateRecordObserved(
            record,
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
        );
        const prune_terminal = protocol.isTerminal(record.state);
        self.last_feedback_epoch = input.feedback_epoch;
        self.commitObserved(
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
        );
        self.advanceRevisionAll();
        if (prune_terminal) _ = self.pruneTerminalsLocked(
            input.observed_monotonic_milliseconds,
        );
        self.recomputeNextWake();
        return .ok;
    }

    pub fn complete(self: *Session, input: *const protocol.CompletionInput) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (!protocol.validCompletion(input, &self.config)) return .abi_mismatch;
        if (input.completion_epoch <= self.last_completion_epoch) return .stale_frame;
        if (!self.canAdvanceRevision()) return .out_of_memory;
        const index = self.bank.findOperation(input.operation_id) orelse return .no_data;
        const record = &self.bank.records[index];
        if ((record.state != .running and record.state != .cancel_pending) or
            record.attempt_configuration_generation !=
                input.configuration_generation or
            !protocol.equalHandle(record.attempt_token, input.attempt_token))
        {
            return .stale_frame;
        }
        if (!self.validAfter(
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
            record.updated_utc_milliseconds,
            record.updated_monotonic_milliseconds,
        )) return .stale_frame;
        const outcome = protocol.completionOutcomeFromInt(input.outcome) orelse
            return .abi_mismatch;
        self.applyOutcomeHandles(record, input.result_handle, input.error_handle, input.valid_mask);
        switch (outcome) {
            .succeeded => self.finishRecord(
                record,
                .succeeded,
                input.observed_utc_milliseconds,
                input.observed_monotonic_milliseconds,
            ),
            .retryable_failure => {
                clearPendingAction(record);
                if ((record.flags & protocol.OperationFlags.cancel_requested) != 0) {
                    self.finishRecord(
                        record,
                        .canceled,
                        input.observed_utc_milliseconds,
                        input.observed_monotonic_milliseconds,
                    );
                } else {
                    self.retryOrFail(
                        record,
                        input.observed_utc_milliseconds,
                        input.observed_monotonic_milliseconds,
                    );
                }
            },
            .terminal_failure => self.finishRecord(
                record,
                .failed,
                input.observed_utc_milliseconds,
                input.observed_monotonic_milliseconds,
            ),
            .canceled => self.finishRecord(
                record,
                .canceled,
                input.observed_utc_milliseconds,
                input.observed_monotonic_milliseconds,
            ),
            .state_uncertain => self.finishRecord(
                record,
                .state_uncertain,
                input.observed_utc_milliseconds,
                input.observed_monotonic_milliseconds,
            ),
        }
        updateRecordObserved(
            record,
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
        );
        const prune_terminal = protocol.isTerminal(record.state);
        self.last_completion_epoch = input.completion_epoch;
        self.commitObserved(
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
        );
        self.advanceRevisionAll();
        if (prune_terminal) _ = self.pruneTerminalsLocked(
            input.observed_monotonic_milliseconds,
        );
        self.recomputeNextWake();
        return .ok;
    }

    pub fn reportProgress(self: *Session, input: *const protocol.ProgressInput) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (!protocol.validProgress(input, &self.config)) return .abi_mismatch;
        if (!self.canAdvanceRevision()) return .out_of_memory;
        const index = self.bank.findOperation(input.operation_id) orelse return .no_data;
        const record = &self.bank.records[index];
        if ((record.state != .running and record.state != .cancel_pending) or
            record.attempt_configuration_generation !=
                input.configuration_generation or
            !protocol.equalHandle(record.attempt_token, input.attempt_token) or
            input.progress_sequence <= record.progress_sequence)
        {
            return .stale_frame;
        }
        if (!self.validAfter(
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
            record.updated_utc_milliseconds,
            record.updated_monotonic_milliseconds,
        )) return .stale_frame;
        if ((input.valid_mask & protocol.ProgressValid.percent_milli) != 0 and
            (record.progress_valid_mask & protocol.ProgressValid.percent_milli) != 0 and
            input.percent_milli < record.percent_milli)
        {
            return .invalid_argument;
        }
        if ((input.valid_mask & protocol.ProgressValid.bytes_done) != 0 and
            (record.progress_valid_mask & protocol.ProgressValid.bytes_done) != 0 and
            input.bytes_done < record.bytes_done)
        {
            return .invalid_argument;
        }
        const resulting_done = if ((input.valid_mask & protocol.ProgressValid.bytes_done) != 0)
            input.bytes_done
        else
            record.bytes_done;
        const resulting_done_valid =
            ((input.valid_mask | record.progress_valid_mask) &
                protocol.ProgressValid.bytes_done) != 0;
        const resulting_total = if ((input.valid_mask & protocol.ProgressValid.bytes_total) != 0)
            input.bytes_total
        else
            record.bytes_total;
        const resulting_total_valid =
            ((input.valid_mask | record.progress_valid_mask) &
                protocol.ProgressValid.bytes_total) != 0;
        if (resulting_done_valid and resulting_total_valid and
            resulting_done > resulting_total)
        {
            return .invalid_argument;
        }

        if ((input.valid_mask & protocol.ProgressValid.percent_milli) != 0) {
            record.percent_milli = input.percent_milli;
        }
        if ((input.valid_mask & protocol.ProgressValid.bytes_done) != 0) {
            record.bytes_done = input.bytes_done;
        }
        if ((input.valid_mask & protocol.ProgressValid.bytes_total) != 0) {
            record.bytes_total = input.bytes_total;
        }
        if ((input.valid_mask & protocol.ProgressValid.speed_bytes_per_second) != 0) {
            record.speed_bytes_per_second = input.speed_bytes_per_second;
        }
        if ((input.valid_mask & protocol.ProgressValid.stage) != 0) {
            record.stage_handle = input.stage_handle;
        }
        if ((input.valid_mask & protocol.ProgressValid.message) != 0) {
            record.message_handle = input.message_handle;
        }
        if ((input.valid_mask & protocol.ProgressValid.checkpoint) != 0) {
            record.checkpoint_handle = input.checkpoint_handle;
        }
        record.progress_sequence = input.progress_sequence;
        record.progress_valid_mask |= input.valid_mask;
        record.flags |= protocol.OperationFlags.progress_valid;
        updateRecordObserved(
            record,
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
        );
        self.commitObserved(
            input.observed_utc_milliseconds,
            input.observed_monotonic_milliseconds,
        );
        self.advanceRevisionRecord(record);
        return .ok;
    }

    pub fn snapshot(self: *Session, output: *protocol.SnapshotOutput) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        const counts = self.countStates();
        output.* = .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.SnapshotOutput),
            .configuration_generation = self.config.generation,
            .state_revision = self.state_revision,
            .last_plan_epoch = self.last_plan_epoch,
            .last_observed_utc_milliseconds = self.last_observed_utc_milliseconds,
            .last_observed_monotonic_milliseconds = self.last_observed_monotonic_milliseconds,
            .next_wake_monotonic_milliseconds = self.next_wake_monotonic_milliseconds,
            .operation_count = self.bank.operation_count,
            .active_operation_count = counts.active,
            .running_operation_count = counts.running,
            .terminal_operation_count = counts.terminal,
            .action_count = self.bank.action_count,
            .reserved_u32 = 0,
            .flags = if (self.next_wake_monotonic_milliseconds != 0)
                protocol.SnapshotFlags.next_wake_valid
            else
                0,
            .reserved = [_]u64{0} ** 4,
        };
        return .ok;
    }

    pub fn readOperations(
        self: *Session,
        input: *const protocol.ReadInput,
        outputs: []protocol.OperationOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        const validation = self.validateRead(input, false);
        if (validation != .ok) return validation;
        if (outputs.len < self.bank.operation_count) return .buffer_too_small;
        var count: u32 = 0;
        for (self.bank.records, 0..) |record, index| {
            if (!record.occupied) continue;
            self.bank.order_scratch[count] = @intCast(index);
            count += 1;
        }
        std.sort.heap(
            u32,
            self.bank.order_scratch[0..count],
            self,
            operationBySubmitSequence,
        );
        for (self.bank.order_scratch[0..count], 0..) |record_index, output_index| {
            outputs[output_index] = self.bank.records[record_index].toOutput();
        }
        return .ok;
    }

    pub fn readActions(
        self: *Session,
        input: *const protocol.ReadInput,
        outputs: []protocol.ActionOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        const validation = self.validateRead(input, true);
        if (validation != .ok) return validation;
        if (outputs.len < self.bank.action_count) return .buffer_too_small;
        @memcpy(
            outputs[0..self.bank.action_count],
            self.bank.actions[0..self.bank.action_count],
        );
        self.last_action_read_plan_epoch = input.plan_epoch;
        return .ok;
    }

    pub fn exportPersistence(
        self: *Session,
        input: *const protocol.PersistenceInput,
        header: *protocol.PersistenceHeader,
        records: []protocol.PersistenceRecord,
    ) ResultCode {
        return persistence.encode(self, input, header, records);
    }

    pub fn importPersistence(
        self: *Session,
        input: *const protocol.PersistenceImportInput,
        header: *const protocol.PersistenceHeader,
        records: []const protocol.PersistenceRecord,
    ) ResultCode {
        return persistence.decodeReplace(self, input, header, records);
    }

    fn validateRead(
        self: *const Session,
        input: *const protocol.ReadInput,
        require_plan: bool,
    ) ResultCode {
        if (require_plan) {
            if (!protocol.validActionRead(input)) return .abi_mismatch;
        } else if (!protocol.validRead(input, &self.config)) {
            return .abi_mismatch;
        }
        if (input.state_revision != self.state_revision) return .stale_frame;
        if (require_plan) {
            if (input.plan_epoch == 0 or input.plan_epoch != self.last_plan_epoch or
                input.configuration_generation !=
                    self.last_plan_configuration_generation)
            {
                return .stale_frame;
            }
        } else if (input.plan_epoch != 0) {
            return .invalid_argument;
        }
        return .ok;
    }

    fn emitAction(
        self: *Session,
        record: *Record,
        kind: protocol.ActionKind,
        plan_epoch: u64,
        now_utc: i64,
        now_monotonic: u64,
        reason_flags: u64,
    ) bool {
        if (self.next_action_id == 0 or self.next_action_id == std.math.maxInt(u64)) {
            return false;
        }
        const action_id = self.next_action_id;
        self.next_action_id += 1;
        const action_token = if (kind == .recover)
            deriveAttemptToken(record.operation_id, record.attempt_number, action_id)
        else
            record.attempt_token;
        const deadline = switch (kind) {
            .start => addSaturating(now_monotonic, record.execution_timeout_milliseconds),
            .cancel => addSaturating(now_monotonic, record.cancel_grace_milliseconds),
            .recover => addSaturating(now_monotonic, record.cancel_grace_milliseconds),
        };
        var flags = reason_flags;
        if (kind == .recover) flags |= protocol.ActionFlags.imported_recovery;
        const action = protocol.ActionOutput{
            .struct_size = @sizeOf(protocol.ActionOutput),
            .kind = @intFromEnum(kind),
            .action_id = action_id,
            .configuration_generation = self.config.generation,
            .operation_id = record.operation_id,
            .attempt_token = action_token,
            .plan_epoch = plan_epoch,
            .attempt_number = record.attempt_number,
            .reserved_u32 = 0,
            .deadline_monotonic_milliseconds = deadline,
            .flags = flags,
            .reserved = [_]u64{0} ** 3,
        };
        if (!self.bank.appendAction(action)) return false;
        record.pending_action_id = action_id;
        record.pending_action_kind = @intFromEnum(kind);
        record.pending_plan_epoch = plan_epoch;
        record.pending_configuration_generation = self.config.generation;
        record.pending_action_token = action_token;
        record.pending_deadline_monotonic_milliseconds = deadline;
        if (kind == .cancel) record.last_cancel_action_id = action_id;
        updateRecordObserved(record, now_utc, now_monotonic);
        record.state = switch (kind) {
            .start => .start_pending,
            .cancel => .cancel_pending,
            .recover => .recovery_pending,
        };
        return true;
    }

    fn retryOrFail(
        self: *Session,
        record: *Record,
        observed_utc: i64,
        observed_monotonic: u64,
    ) void {
        if (record.attempt_number < record.maximum_attempts) {
            record.state = .retry_wait;
            record.retry_at_monotonic_milliseconds =
                addSaturating(observed_monotonic, record.retry_delay_milliseconds);
            updateRecordObserved(record, observed_utc, observed_monotonic);
        } else {
            self.finishRecord(record, .failed, observed_utc, observed_monotonic);
        }
    }

    fn finishRecord(
        self: *Session,
        record: *Record,
        terminal_state: protocol.OperationState,
        observed_utc: i64,
        observed_monotonic: u64,
    ) void {
        if ((record.flags & protocol.OperationFlags.domain_valid) != 0 and
            !protocol.isTerminal(record.state))
        {
            self.bank.removeDomain(record.domain_id);
        }
        record.state = terminal_state;
        record.flags |= protocol.OperationFlags.terminal;
        record.retry_at_monotonic_milliseconds = 0;
        record.terminal_at_monotonic_milliseconds = observed_monotonic;
        record.terminal_retire_at_monotonic_milliseconds = addSaturating(
            observed_monotonic,
            record.terminal_retention_milliseconds,
        );
        updateRecordObserved(record, observed_utc, observed_monotonic);
        clearPendingAction(record);
    }

    fn applyOutcomeHandles(
        self: *Session,
        record: *Record,
        result_handle: protocol.Handle128,
        error_handle: protocol.Handle128,
        valid_mask: u64,
    ) void {
        _ = self;
        if ((valid_mask & protocol.FeedbackValid.result) != 0) {
            record.result_handle = result_handle;
            record.flags |= protocol.OperationFlags.result_valid;
        }
        if ((valid_mask & protocol.FeedbackValid.error_handle) != 0) {
            record.error_handle = error_handle;
            record.flags |= protocol.OperationFlags.error_valid;
        }
    }

    pub fn pruneTerminalsLocked(self: *Session, now_monotonic: u64) bool {
        var terminal_count: u32 = 0;
        for (self.bank.records, 0..) |record, index| {
            if (!record.occupied or !protocol.isTerminal(record.state)) continue;
            self.bank.order_scratch[terminal_count] = @intCast(index);
            terminal_count += 1;
        }
        if (terminal_count == 0) return false;
        std.sort.heap(
            u32,
            self.bank.order_scratch[0..terminal_count],
            self,
            terminalBefore,
        );
        var changed = false;
        var ordinal: u32 = 0;
        while (ordinal < terminal_count) : (ordinal += 1) {
            const record_index = self.bank.order_scratch[ordinal];
            const record = &self.bank.records[record_index];
            const exceeds_count =
                terminal_count - ordinal > self.config.maximum_recent_terminal_count;
            const expired =
                record.terminal_retire_at_monotonic_milliseconds <= now_monotonic;
            if (exceeds_count or expired) {
                self.bank.releaseRecord(record_index);
                changed = true;
            }
        }
        return changed;
    }

    fn slotConsumingCount(self: *const Session) u32 {
        var count: u32 = 0;
        for (self.bank.records) |record| {
            if (!record.occupied) continue;
            switch (record.state) {
                .start_pending, .running, .cancel_pending, .recovery_pending => count += 1,
                else => {},
            }
        }
        return count;
    }

    fn countStates(self: *const Session) StateCounts {
        var counts: StateCounts = .{};
        for (self.bank.records) |record| {
            if (!record.occupied) continue;
            if (protocol.isTerminal(record.state)) {
                counts.terminal += 1;
            } else {
                counts.active += 1;
            }
            switch (record.state) {
                .start_pending, .running, .cancel_pending, .recovery_pending => counts.running += 1,
                else => {},
            }
        }
        return counts;
    }

    fn terminalCount(self: *const Session) u32 {
        var count: u32 = 0;
        for (self.bank.records) |record| {
            if (record.occupied and protocol.isTerminal(record.state)) count += 1;
        }
        return count;
    }

    pub fn recomputeNextWake(self: *Session) void {
        var next: u64 = 0;
        const start_slot_available =
            self.slotConsumingCount() < self.config.maximum_global_running_count;
        for (self.bank.records) |record| {
            if (!record.occupied) continue;
            const candidate = switch (record.state) {
                .retry_wait => record.retry_at_monotonic_milliseconds,
                .running => if ((record.flags & protocol.OperationFlags.cancel_requested) == 0)
                    addSaturating(
                        record.started_monotonic_milliseconds,
                        record.execution_timeout_milliseconds,
                    )
                else
                    self.last_observed_monotonic_milliseconds,
                .cancel_pending => record.pending_deadline_monotonic_milliseconds,
                .queued => if (start_slot_available)
                    self.last_observed_monotonic_milliseconds
                else
                    0,
                .recovery_pending => if (record.pending_action_id == 0)
                    self.last_observed_monotonic_milliseconds
                else
                    record.pending_deadline_monotonic_milliseconds,
                .succeeded, .failed, .canceled, .state_uncertain => record.terminal_retire_at_monotonic_milliseconds,
                .start_pending => record.pending_deadline_monotonic_milliseconds,
            };
            if (candidate != 0 and (next == 0 or candidate < next)) next = candidate;
        }
        self.next_wake_monotonic_milliseconds = next;
    }

    fn rerankSessionLocalQueue(self: *Session) void {
        var count: u32 = 0;
        for (self.bank.records, 0..) |record, index| {
            if (!record.occupied or !record.session_local) continue;
            self.bank.order_scratch[count] = @intCast(index);
            count += 1;
        }
        std.sort.heap(
            u32,
            self.bank.order_scratch[0..count],
            self,
            creationBefore,
        );
        for (self.bank.order_scratch[0..count], 0..) |record_index, ordinal| {
            self.bank.records[record_index].queue_order =
                self.imported_queue_order_high_water + ordinal + 1;
        }
    }

    fn sessionLocalQueueCount(self: *const Session) u64 {
        var count: u64 = 0;
        for (self.bank.records) |record| {
            if (record.occupied and record.session_local) count += 1;
        }
        return count;
    }

    fn canAdvanceRevision(self: *const Session) bool {
        return self.state_revision != std.math.maxInt(u64);
    }

    fn advanceRevisionAll(self: *Session) void {
        self.state_revision += 1;
        for (self.bank.records) |*record| {
            if (record.occupied) record.state_revision = self.state_revision;
        }
    }

    fn advanceRevisionRecord(self: *Session, record: *Record) void {
        self.state_revision += 1;
        record.state_revision = self.state_revision;
    }

    fn validGlobalObserved(
        self: *const Session,
        utc_milliseconds: i64,
        monotonic: u64,
    ) bool {
        return self.validAfter(
            utc_milliseconds,
            monotonic,
            self.last_observed_utc_milliseconds,
            self.last_observed_monotonic_milliseconds,
        );
    }

    fn validAfter(
        self: *const Session,
        utc_milliseconds: i64,
        monotonic: u64,
        previous_utc_milliseconds: i64,
        previous_monotonic: u64,
    ) bool {
        if (utc_milliseconds < 0 or monotonic == 0) return false;
        if (previous_monotonic == 0) return true;
        if (monotonic < previous_monotonic) return false;
        if (utc_milliseconds <= previous_utc_milliseconds) return true;
        const utc_delta: u64 = @intCast(
            utc_milliseconds - previous_utc_milliseconds,
        );
        const monotonic_delta = monotonic - previous_monotonic;
        return utc_delta <= addSaturating(
            monotonic_delta,
            self.config.maximum_future_skew_milliseconds,
        );
    }

    fn commitObserved(self: *Session, utc_milliseconds: i64, monotonic: u64) void {
        if (monotonic >= self.last_observed_monotonic_milliseconds) {
            self.last_observed_utc_milliseconds = utc_milliseconds;
            self.last_observed_monotonic_milliseconds = monotonic;
        }
    }
};

const StateCounts = struct {
    active: u32 = 0,
    running: u32 = 0,
    terminal: u32 = 0,
};

fn operationBefore(session: *Session, left_index: u32, right_index: u32) bool {
    const left = session.bank.records[left_index];
    const right = session.bank.records[right_index];
    if (left.priority != right.priority) return left.priority > right.priority;
    if (left.queue_order != right.queue_order) {
        return left.queue_order < right.queue_order;
    }
    return handleLess(left.operation_id, right.operation_id);
}

fn creationBefore(session: *Session, left_index: u32, right_index: u32) bool {
    const left = session.bank.records[left_index];
    const right = session.bank.records[right_index];
    if (left.created_monotonic_milliseconds != right.created_monotonic_milliseconds) {
        return left.created_monotonic_milliseconds < right.created_monotonic_milliseconds;
    }
    if (left.submit_sequence != right.submit_sequence) {
        return left.submit_sequence < right.submit_sequence;
    }
    return handleLess(left.operation_id, right.operation_id);
}

fn operationBySubmitSequence(session: *Session, left_index: u32, right_index: u32) bool {
    const left = session.bank.records[left_index];
    const right = session.bank.records[right_index];
    if (left.submit_sequence != right.submit_sequence) {
        return left.submit_sequence < right.submit_sequence;
    }
    return handleLess(left.operation_id, right.operation_id);
}

fn cancelBefore(session: *Session, left_index: u32, right_index: u32) bool {
    const left = session.bank.records[left_index];
    const right = session.bank.records[right_index];
    if (left.last_cancel_action_id != right.last_cancel_action_id) {
        return left.last_cancel_action_id < right.last_cancel_action_id;
    }
    return operationBySubmitSequence(session, left_index, right_index);
}

fn terminalBefore(session: *Session, left_index: u32, right_index: u32) bool {
    const left = session.bank.records[left_index];
    const right = session.bank.records[right_index];
    if (left.terminal_at_monotonic_milliseconds !=
        right.terminal_at_monotonic_milliseconds)
    {
        return left.terminal_at_monotonic_milliseconds <
            right.terminal_at_monotonic_milliseconds;
    }
    return left.submit_sequence < right.submit_sequence;
}

fn handleLess(left: protocol.Handle128, right: protocol.Handle128) bool {
    return left.high < right.high or (left.high == right.high and left.low < right.low);
}

fn clearPendingAction(record: *Record) void {
    record.pending_action_id = 0;
    record.pending_action_kind = 0;
    record.pending_plan_epoch = 0;
    record.pending_configuration_generation = 0;
    record.pending_action_token = .{ .high = 0, .low = 0 };
    record.pending_deadline_monotonic_milliseconds = 0;
}

fn updateRecordObserved(record: *Record, utc_milliseconds: i64, monotonic: u64) void {
    record.updated_utc_milliseconds = utc_milliseconds;
    record.updated_monotonic_milliseconds = monotonic;
}

fn resetAttemptTransient(record: *Record) void {
    record.result_handle = .{ .high = 0, .low = 0 };
    record.error_handle = .{ .high = 0, .low = 0 };
    record.started_monotonic_milliseconds = 0;
    record.progress_sequence = 0;
    record.progress_valid_mask = 0;
    record.percent_milli = 0;
    record.bytes_done = 0;
    record.bytes_total = 0;
    record.speed_bytes_per_second = 0;
    record.stage_handle = .{ .high = 0, .low = 0 };
    record.message_handle = .{ .high = 0, .low = 0 };
    record.checkpoint_handle = .{ .high = 0, .low = 0 };
    record.flags &= ~(protocol.OperationFlags.result_valid |
        protocol.OperationFlags.error_valid |
        protocol.OperationFlags.progress_valid);
}

fn compatibleFeedback(
    kind: protocol.ActionKind,
    outcome: protocol.ActionFeedbackOutcome,
) bool {
    return switch (kind) {
        .start => outcome == .started or
            outcome == .start_retryable_failure or
            outcome == .start_terminal_failure,
        .cancel => outcome == .cancel_completed or
            outcome == .cancel_retryable_failure,
        .recover => outcome == .recovered_queued or
            outcome == .recovered_succeeded or
            outcome == .recovered_failed or
            outcome == .recovered_canceled or
            outcome == .recovered_uncertain,
    };
}

fn deriveAttemptToken(
    operation_id: protocol.Handle128,
    attempt_number: u32,
    action_id: u64,
) protocol.Handle128 {
    const high = mix64(operation_id.high ^ action_id);
    var low = mix64(operation_id.low ^ (@as(u64, attempt_number) << 32) ^ action_id);
    if (high == 0 and low == 0) low = 1;
    return .{ .high = high, .low = low };
}

fn mix64(input: u64) u64 {
    var value = input;
    value ^= value >> 30;
    value *%= 0xbf58476d1ce4e5b9;
    value ^= value >> 27;
    value *%= 0x94d049bb133111eb;
    value ^= value >> 31;
    return value;
}

fn addSaturating(left: u64, right: anytype) u64 {
    const right_u64: u64 = @intCast(right);
    return std.math.add(u64, left, right_u64) catch std.math.maxInt(u64);
}

fn sameStaticCapacity(left: *const protocol.Config, right: *const protocol.Config) bool {
    return protocol.equalHandle(left.session_instance_id, right.session_instance_id) and
        protocol.equalHandle(left.clock_instance_id, right.clock_instance_id) and
        left.maximum_operation_count == right.maximum_operation_count and
        left.maximum_domain_count == right.maximum_domain_count and
        left.maximum_action_count == right.maximum_action_count and
        left.operation_index_capacity == right.operation_index_capacity and
        left.domain_index_capacity == right.domain_index_capacity and
        left.maximum_read_count == right.maximum_read_count;
}

fn sameSubmissionIdentity(record: *const Record, input: *const protocol.SubmitInput) bool {
    const record_valid_mask =
        (if ((record.flags & protocol.OperationFlags.domain_valid) != 0)
            protocol.SubmitValid.domain
        else
            0) |
        (if ((record.flags & protocol.OperationFlags.title_valid) != 0)
            protocol.SubmitValid.title
        else
            0) |
        (if ((record.flags & protocol.OperationFlags.request_valid) != 0)
            protocol.SubmitValid.request
        else
            0);
    return record_valid_mask == input.valid_mask and
        protocol.equalHandle(record.kind_handle, input.kind_handle) and
        protocol.equalHandle(record.domain_id, input.domain_id) and
        protocol.equalHandle(record.title_handle, input.title_handle) and
        protocol.equalHandle(record.request_handle, input.request_handle);
}

fn lockMutex(mutex: *windows.SRWLOCK) void {
    windows.ntdll.RtlAcquireSRWLockExclusive(mutex);
}

fn unlockMutex(mutex: *windows.SRWLOCK) void {
    windows.ntdll.RtlReleaseSRWLockExclusive(mutex);
}
