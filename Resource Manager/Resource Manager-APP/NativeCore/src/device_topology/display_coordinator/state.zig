const std = @import("std");
const windows = std.os.windows;
const protocol = @import("protocol.zig");
const bank_module = @import("bank.zig");
const identity = @import("identity.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const Bank = bank_module.Bank;
const FactSlot = bank_module.FactSlot;
const TextSlot = bank_module.TextSlot;

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: windows.SRWLOCK = windows.SRWLOCK_INIT,
    config: protocol.Config,
    resident_byte_count: u64,
    active: Bank,
    working: Bank,
    next: Bank,
    batch_text_map: []u32,
    normalization_scratch: []u8,
    identity_index: []IdentityIndexSlot,
    union_parent: []u32,
    union_rank: []u8,
    oem_candidate_used: []u8,
    component_states: []ComponentState,
    text_order_scratch: []TextOrderSlot,
    text_remap: []u32,
    phase: protocol.Phase = .warming,
    state_revision: u64 = 1,
    content_generation: u64 = 0,
    last_refresh_epoch: u64 = 0,
    last_success_utc_ms: i64 = 0,
    last_attempt_utc_ms: i64 = 0,
    last_monotonic_ms: u64 = 0,
    last_finalize_content_changed: bool = false,
    last_persistence_epoch: u64 = 0,
    current_source_mask: u64 = 0,
    retained_source_mask: u64 = 0,
    failed_source_mask: u64 = 0,
    unsupported_source_mask: u64 = 0,
    submitted_source_mask: u64 = 0,
    round_current_source_mask: u64 = 0,
    round_retained_source_mask: u64 = 0,
    round_failed_source_mask: u64 = 0,
    round_unsupported_source_mask: u64 = 0,
    round_requested_source_mask: u64 = 0,
    round_refresh_epoch: u64 = 0,
    round_captured_utc_ms: i64 = 0,
    round_monotonic_ms: u64 = 0,

    pub fn create(config: *const protocol.Config) !*Session {
        if (!protocol.validConfig(config)) return error.InvalidConfiguration;
        const resident = residentByteCount(config) catch
            return error.InvalidConfiguration;
        if (resident > config.resident_byte_budget) return error.InvalidConfiguration;

        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);
        var active = try Bank.create(allocator, config);
        errdefer active.destroy();
        var working = try Bank.create(allocator, config);
        errdefer working.destroy();
        var next = try Bank.create(allocator, config);
        errdefer next.destroy();
        const batch_text_map = try allocator.alloc(u32, config.maximum_text_binding_count);
        errdefer allocator.free(batch_text_map);
        const normalization_scratch = try allocator.alloc(u8, config.maximum_text_byte_count);
        errdefer allocator.free(normalization_scratch);
        const identity_index = try allocator.alloc(IdentityIndexSlot, config.identity_index_capacity);
        errdefer allocator.free(identity_index);
        const union_parent = try allocator.alloc(u32, config.maximum_observation_count);
        errdefer allocator.free(union_parent);
        const union_rank = try allocator.alloc(u8, config.maximum_observation_count);
        errdefer allocator.free(union_rank);
        const oem_candidate_used = try allocator.alloc(u8, config.maximum_observation_count);
        errdefer allocator.free(oem_candidate_used);
        const component_states = try allocator.alloc(
            ComponentState,
            config.maximum_observation_count,
        );
        errdefer allocator.free(component_states);
        const text_order_scratch = try allocator.alloc(
            TextOrderSlot,
            config.maximum_text_binding_count,
        );
        errdefer allocator.free(text_order_scratch);
        const text_remap = try allocator.alloc(u32, config.maximum_text_binding_count);
        errdefer allocator.free(text_remap);
        @memset(batch_text_map, protocol.no_text_index);
        @memset(normalization_scratch, 0);
        @memset(identity_index, .{});
        @memset(union_parent, protocol.no_text_index);
        @memset(union_rank, 0);
        @memset(oem_candidate_used, 0);
        @memset(component_states, .{});
        @memset(text_order_scratch, .{});
        @memset(text_remap, protocol.no_text_index);
        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .resident_byte_count = resident,
            .active = active,
            .working = working,
            .next = next,
            .batch_text_map = batch_text_map,
            .normalization_scratch = normalization_scratch,
            .identity_index = identity_index,
            .union_parent = union_parent,
            .union_rank = union_rank,
            .oem_candidate_used = oem_candidate_used,
            .component_states = component_states,
            .text_order_scratch = text_order_scratch,
            .text_remap = text_remap,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.text_remap);
        allocator.free(self.text_order_scratch);
        allocator.free(self.component_states);
        allocator.free(self.oem_candidate_used);
        allocator.free(self.union_rank);
        allocator.free(self.union_parent);
        allocator.free(self.identity_index);
        allocator.free(self.normalization_scratch);
        allocator.free(self.batch_text_map);
        self.next.destroy();
        self.working.destroy();
        self.active.destroy();
        allocator.destroy(self);
    }

    pub fn capacity(self: *Session) protocol.Capacity {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .maximum_source_count = self.config.maximum_source_count,
            .maximum_observation_count = self.config.maximum_observation_count,
            .maximum_node_count = self.config.maximum_node_count,
            .maximum_edge_count = self.config.maximum_edge_count,
            .maximum_capability_count = self.config.maximum_capability_count,
            .maximum_diff_entry_count = self.config.maximum_diff_entry_count,
            .maximum_text_binding_count = self.config.maximum_text_binding_count,
            .maximum_text_byte_count = self.config.maximum_text_byte_count,
            .maximum_unresolved_count = self.config.maximum_unresolved_count,
            .maximum_source_batch_count = self.config.maximum_source_batch_count,
            .identity_index_capacity = self.config.identity_index_capacity,
            .text_index_capacity = self.config.text_index_capacity,
            .reserved_u32 = 0,
            .resident_byte_count = self.resident_byte_count,
            .reserved = [_]u64{0} ** 8,
        };
    }

    pub fn beginRefresh(
        self: *Session,
        input: *const protocol.RefreshInput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (!protocol.validRefresh(input, &self.config)) return .abi_mismatch;
        if (self.phase == .collecting) return .invalid_argument;
        if (input.refresh_epoch <= self.last_refresh_epoch) return .stale_frame;
        if (input.monotonic_ms <= self.last_monotonic_ms or
            input.captured_utc_ms < self.last_attempt_utc_ms)
        {
            return .stale_frame;
        }
        if (!self.canAdvanceRevision()) return .out_of_memory;
        self.working.reset();
        copyFactsAndTexts(&self.active, &self.working) catch return .out_of_memory;
        self.working.source_generations = self.active.source_generations;
        self.submitted_source_mask = 0;
        self.round_current_source_mask = 0;
        self.round_retained_source_mask =
            (self.current_source_mask | self.retained_source_mask) &
            ~input.requested_source_mask;
        self.round_failed_source_mask = 0;
        self.round_unsupported_source_mask = 0;
        self.round_requested_source_mask = input.requested_source_mask;
        self.round_refresh_epoch = input.refresh_epoch;
        self.round_captured_utc_ms = input.captured_utc_ms;
        self.round_monotonic_ms = input.monotonic_ms;
        self.last_attempt_utc_ms = input.captured_utc_ms;
        self.last_monotonic_ms = input.monotonic_ms;
        self.phase = .collecting;
        self.advanceRevision();
        return .ok;
    }

    pub fn submitSource(
        self: *Session,
        header: *const protocol.SourceBatchHeader,
        facts: []const protocol.DisplayFact,
        texts: []const protocol.TextInput,
        bytes: []const u8,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (self.phase != .collecting) return .invalid_argument;
        if (!protocol.validBatch(
            header,
            &self.config,
            self.round_refresh_epoch,
            facts,
            texts,
            bytes,
        )) return .abi_mismatch;
        const source = protocol.sourceFromInt(header.source_id).?;
        const status = protocol.statusFromInt(header.status).?;
        const source_bit = protocol.sourceBit(source);
        if ((self.round_requested_source_mask & source_bit) == 0) return .invalid_argument;
        if ((self.submitted_source_mask & source_bit) != 0) return .stale_frame;
        const source_index: usize = @intFromEnum(source) - 1;
        const active_generation = self.active.source_generations[source_index];
        switch (status) {
            .complete, .unsupported => {
                if (header.source_generation <= active_generation) return .stale_frame;
            },
            .retained => {
                if (active_generation == 0 or header.source_generation != active_generation) {
                    return .stale_frame;
                }
            },
            .unavailable => {
                if (active_generation != 0 and
                    header.source_generation != active_generation)
                {
                    return .stale_frame;
                }
                if (active_generation == 0 and header.source_generation == 0) {
                    return .stale_frame;
                }
            },
        }
        const maximum_captured = std.math.add(
            i64,
            self.round_captured_utc_ms,
            self.config.maximum_future_skew_ms,
        ) catch return .invalid_argument;
        if (header.captured_utc_ms > maximum_captured) return .invalid_argument;

        if (status == .complete) {
            const result = self.replaceSource(source, header.source_generation, facts, texts, bytes);
            if (result != .ok) return result;
            self.round_current_source_mask |= source_bit;
        } else if (status == .unsupported) {
            self.working.removeSourceFacts(source);
            self.working.source_generations[source_index] = header.source_generation;
            self.round_unsupported_source_mask |= source_bit;
        } else if (status == .retained) {
            self.round_retained_source_mask |= source_bit;
        } else {
            self.round_failed_source_mask |= source_bit;
            if (active_generation != 0) {
                self.round_retained_source_mask |= source_bit;
            } else {
                self.working.source_generations[source_index] = header.source_generation;
            }
        }
        self.submitted_source_mask |= source_bit;
        return .ok;
    }

    pub fn finalize(
        self: *Session,
        input: *const protocol.FinalizeInput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (self.phase != .collecting) return .invalid_argument;
        if (!protocol.validFinalize(
            input,
            &self.config,
            self.round_refresh_epoch,
            self.round_requested_source_mask,
        )) {
            return .abi_mismatch;
        }
        if (self.submitted_source_mask != self.round_requested_source_mask) {
            return .invalid_argument;
        }
        const required_complete =
            (self.round_current_source_mask & self.config.required_source_mask) ==
            self.config.required_source_mask;
        if (!required_complete) {
            if (!self.canAdvanceRevision()) return .out_of_memory;
            const retained_payload =
                self.current_source_mask | self.retained_source_mask;
            const unsupported_without_payload =
                self.round_unsupported_source_mask & ~retained_payload;
            self.last_refresh_epoch = self.round_refresh_epoch;
            self.current_source_mask = 0;
            self.retained_source_mask = retained_payload;
            self.failed_source_mask =
                ((self.config.required_source_mask & ~self.round_current_source_mask) |
                    self.round_failed_source_mask) & ~unsupported_without_payload;
            self.unsupported_source_mask = unsupported_without_payload;
            self.last_finalize_content_changed = false;
            self.phase = if (self.content_generation == 0)
                .failed_no_data
            else
                .failed_retained;
            self.advanceRevision();
            return .unavailable;
        }

        self.next.reset();
        copyFactsAndTexts(&self.working, &self.next) catch return .out_of_memory;
        self.next.source_generations = self.working.source_generations;
        const result = self.buildCanonical();
        if (result != .ok) return result;

        const changed = self.content_generation == 0 or
            !sameCanonical(&self.active, &self.next);
        const next_generation = if (changed)
            std.math.add(u64, self.content_generation, 1) catch return .out_of_memory
        else
            self.content_generation;
        if (changed) {
            const diff_result = self.buildDiff(next_generation);
            if (diff_result != .ok) return diff_result;
        } else {
            self.next.diff_count = 0;
        }
        for (self.next.nodes[0..self.next.node_count]) |*node| {
            node.generation = next_generation;
        }
        for (self.next.capabilities[0..self.next.capability_count]) |*capability| {
            capability.generation = next_generation;
        }

        if (!self.canAdvanceRevision()) return .out_of_memory;
        const old_active = self.active;
        self.active = self.next;
        self.next = old_active;
        self.content_generation = next_generation;
        self.last_refresh_epoch = self.round_refresh_epoch;
        self.last_success_utc_ms = self.round_captured_utc_ms;
        self.current_source_mask = self.round_current_source_mask;
        self.retained_source_mask = self.round_retained_source_mask;
        self.failed_source_mask = self.round_failed_source_mask;
        self.unsupported_source_mask = self.round_unsupported_source_mask;
        self.last_finalize_content_changed = changed;
        self.phase = .ready;
        self.advanceRevision();
        return .ok;
    }

    pub fn abortRefresh(
        self: *Session,
        input: *const protocol.AbortInput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (self.phase != .collecting) return .invalid_argument;
        if (!protocol.validAbort(input, &self.config, self.round_refresh_epoch)) {
            return .abi_mismatch;
        }
        if (!self.canAdvanceRevision()) return .out_of_memory;
        self.working.reset();
        self.last_refresh_epoch = self.round_refresh_epoch;
        self.last_finalize_content_changed = false;
        self.phase = if (self.content_generation == 0)
            .warming
        else if ((self.current_source_mask & self.config.required_source_mask) ==
            self.config.required_source_mask)
            .ready
        else
            .failed_retained;
        self.advanceRevision();
        return .ok;
    }

    pub fn snapshot(
        self: *Session,
        output: *protocol.SnapshotOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (!protocol.emptyBytes(protocol.SnapshotOutput, output)) return .abi_mismatch;
        var flags: u32 = 0;
        if (self.content_generation != 0) flags |= protocol.SnapshotFlags.has_last_good;
        if (self.phase == .failed_retained or self.phase == .failed_no_data) {
            flags |= protocol.SnapshotFlags.required_source_incomplete;
        }
        if (self.active.unresolved_count != 0) flags |= protocol.SnapshotFlags.has_unresolved;
        if (self.last_finalize_content_changed) {
            flags |= protocol.SnapshotFlags.content_changed;
        }
        output.* = .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.SnapshotOutput),
            .configuration_generation = self.config.generation,
            .state_revision = self.state_revision,
            .refresh_epoch = self.last_refresh_epoch,
            .content_generation = self.content_generation,
            .last_success_utc_ms = self.last_success_utc_ms,
            .last_attempt_utc_ms = self.last_attempt_utc_ms,
            .phase = @intFromEnum(self.phase),
            .flags = flags,
            .current_source_mask = self.current_source_mask,
            .retained_source_mask = self.retained_source_mask,
            .failed_source_mask = self.failed_source_mask,
            .unsupported_source_mask = self.unsupported_source_mask,
            .node_count = self.active.node_count,
            .edge_count = self.active.edge_count,
            .capability_count = self.active.capability_count,
            .text_count = self.active.text_count,
            .unresolved_count = self.active.unresolved_count,
            .diff_count = self.active.diff_count,
            .resident_byte_count = self.resident_byte_count,
        };
        return .ok;
    }

    pub fn readNodes(
        self: *Session,
        input: *const protocol.ReadInput,
        outputs: []protocol.NodeOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        const validation = self.validateRead(input);
        if (validation != .ok) return validation;
        return copyOutputs(protocol.NodeOutput, self.active.nodes[0..self.active.node_count], outputs);
    }

    pub fn readEdges(
        self: *Session,
        input: *const protocol.ReadInput,
        outputs: []protocol.EdgeOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        const validation = self.validateRead(input);
        if (validation != .ok) return validation;
        return copyOutputs(protocol.EdgeOutput, self.active.edges[0..self.active.edge_count], outputs);
    }

    pub fn readCapabilities(
        self: *Session,
        input: *const protocol.ReadInput,
        outputs: []protocol.DisplayCapabilityOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        const validation = self.validateRead(input);
        if (validation != .ok) return validation;
        return copyOutputs(
            protocol.DisplayCapabilityOutput,
            self.active.capabilities[0..self.active.capability_count],
            outputs,
        );
    }

    pub fn readTexts(
        self: *Session,
        input: *const protocol.ReadInput,
        outputs: []protocol.TextOutput,
        bytes: []u8,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        const validation = self.validateRead(input);
        if (validation != .ok) return validation;
        if (outputs.len < self.active.text_count or bytes.len < self.active.text_byte_count) {
            return .buffer_too_small;
        }
        for (outputs[0..self.active.text_count]) |*output| {
            if (!protocol.emptyBytes(protocol.TextOutput, output)) return .abi_mismatch;
        }
        if (!allBytesZero(bytes[0..self.active.text_byte_count])) return .abi_mismatch;
        @memcpy(bytes[0..self.active.text_byte_count], self.active.text_bytes[0..self.active.text_byte_count]);
        for (self.active.texts[0..self.active.text_count], 0..) |slot, index| {
            outputs[index] = .{
                .struct_size = @sizeOf(protocol.TextOutput),
                .normalization_kind = @intFromEnum(slot.kind),
                .handle = slot.handle,
                .byte_offset = slot.offset,
                .byte_length = slot.length,
                .flags = 0,
                .reserved = .{ 0, 0, 0 },
            };
        }
        return .ok;
    }

    pub fn readDiff(
        self: *Session,
        input: *const protocol.ReadInput,
        outputs: []protocol.DiffEntry,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        const validation = self.validateRead(input);
        if (validation != .ok) return validation;
        return copyOutputs(protocol.DiffEntry, self.active.diffs[0..self.active.diff_count], outputs);
    }

    pub fn readUnresolved(
        self: *Session,
        input: *const protocol.ReadInput,
        outputs: []protocol.UnresolvedOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        const validation = self.validateRead(input);
        if (validation != .ok) return validation;
        return copyOutputs(
            protocol.UnresolvedOutput,
            self.active.unresolved[0..self.active.unresolved_count],
            outputs,
        );
    }

    fn validateRead(
        self: *const Session,
        input: *const protocol.ReadInput,
    ) ResultCode {
        if (!protocol.validRead(input, &self.config)) return .abi_mismatch;
        if (input.state_revision != self.state_revision or
            input.content_generation != self.content_generation)
        {
            return .stale_frame;
        }
        return .ok;
    }

    pub fn exportPersistence(
        self: *Session,
        input: *const protocol.PersistenceInput,
        header: *protocol.PersistenceHeader,
        facts: []protocol.DisplayFact,
        texts: []protocol.TextInput,
        bytes: []u8,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (!protocol.validPersistenceInput(input, &self.config)) return .abi_mismatch;
        if (input.operation_epoch <= self.last_persistence_epoch) return .stale_frame;
        if (self.content_generation == 0) return .no_data;
        if (!protocol.emptyBytes(protocol.PersistenceHeader, header)) return .abi_mismatch;
        if (facts.len < self.active.fact_count or texts.len < self.active.text_count or
            bytes.len < self.active.text_byte_count) return .buffer_too_small;
        for (facts[0..self.active.fact_count]) |*fact| {
            if (!protocol.emptyBytes(protocol.DisplayFact, fact)) return .abi_mismatch;
        }
        for (texts[0..self.active.text_count]) |*text| {
            if (!protocol.emptyBytes(protocol.TextInput, text)) return .abi_mismatch;
        }
        if (!allBytesZero(bytes[0..self.active.text_byte_count])) return .abi_mismatch;

        @memcpy(
            bytes[0..self.active.text_byte_count],
            self.active.text_bytes[0..self.active.text_byte_count],
        );
        for (self.active.texts[0..self.active.text_count], 0..) |slot, index| {
            texts[index] = .{
                .struct_size = @sizeOf(protocol.TextInput),
                .normalization_kind = @intFromEnum(slot.kind),
                .byte_offset = slot.offset,
                .byte_length = slot.length,
                .valid_mask = protocol.TextValid.required,
                .flags = 0,
                .reserved = .{ 0, 0 },
            };
        }
        for (self.active.facts[0..self.active.fact_count], 0..) |fact, index| {
            facts[index] = persistedFact(&fact);
        }
        header.* = .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.PersistenceHeader),
            .configuration_generation = self.config.generation,
            .operation_epoch = input.operation_epoch,
            .content_generation = self.content_generation,
            .state_revision = self.state_revision,
            .last_refresh_epoch = self.last_refresh_epoch,
            .last_success_utc_ms = self.last_success_utc_ms,
            .last_attempt_utc_ms = self.last_attempt_utc_ms,
            .current_source_mask = self.current_source_mask,
            .retained_source_mask = self.retained_source_mask,
            .failed_source_mask = self.failed_source_mask,
            .unsupported_source_mask = self.unsupported_source_mask,
            .source_generations = self.active.source_generations,
            .fact_count = self.active.fact_count,
            .text_count = self.active.text_count,
            .text_byte_count = self.active.text_byte_count,
            .phase = @intFromEnum(self.phase),
            .checksum = identity.zeroHandle(),
            .last_monotonic_ms = self.last_monotonic_ms,
            .reserved = .{0},
        };
        header.checksum = persistenceChecksum(
            header,
            facts[0..self.active.fact_count],
            texts[0..self.active.text_count],
            bytes[0..self.active.text_byte_count],
        );
        self.last_persistence_epoch = input.operation_epoch;
        return .ok;
    }

    pub fn importPersistence(
        self: *Session,
        input: *const protocol.PersistenceInput,
        header: *const protocol.PersistenceHeader,
        facts: []const protocol.DisplayFact,
        texts: []const protocol.TextInput,
        bytes: []const u8,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer unlockMutex(&self.mutex);
        if (!protocol.validPersistenceInput(input, &self.config)) return .abi_mismatch;
        if (input.operation_epoch <= self.last_persistence_epoch) return .stale_frame;
        if (self.phase != .warming or self.content_generation != 0 or self.last_refresh_epoch != 0) {
            return .invalid_argument;
        }
        if (!protocol.validPersistenceHeader(
            header,
            &self.config,
            input,
            facts,
            texts,
            bytes,
        )) return .abi_mismatch;
        const expected_checksum = persistenceChecksum(header, facts, texts, bytes);
        if (!bank_module.equalHandle(expected_checksum, header.checksum)) return .invalid_argument;

        self.next.reset();
        @memset(self.batch_text_map, protocol.no_text_index);
        for (texts, 0..) |text, index| {
            const kind = protocol.textKindFromInt(text.normalization_kind).?;
            const raw = protocol.byteSlice(bytes, text.byte_offset, text.byte_length).?;
            const normalized = identity.normalize(kind, raw, self.normalization_scratch) orelse
                return .invalid_argument;
            if (!std.mem.eql(u8, normalized, raw)) return .invalid_argument;
            const handle = identity.textHandle(kind, normalized);
            const slot = self.next.appendText(kind, handle, normalized) catch |err|
                return if (err == error.HashCollision) .invalid_argument else .out_of_memory;
            self.batch_text_map[index] = slot;
        }
        for (facts) |fact| {
            if (!factTextKindsMatch(&fact, texts)) return .invalid_argument;
            if (self.next.fact_count >= self.next.facts.len) return .out_of_memory;
            self.next.facts[self.next.fact_count] = translateFact(
                &fact,
                protocol.sourceFromInt(fact.source_id).?,
                self.batch_text_map,
            );
            self.next.fact_count += 1;
        }
        self.next.source_generations = header.source_generations;
        const build_result = self.buildCanonical();
        if (build_result != .ok) return build_result;
        for (self.next.nodes[0..self.next.node_count]) |*node| {
            node.generation = header.content_generation;
        }
        for (self.next.capabilities[0..self.next.capability_count]) |*capability| {
            capability.generation = header.content_generation;
        }
        self.next.diff_count = 0;
        const old_active = self.active;
        self.active = self.next;
        self.next = old_active;
        self.content_generation = header.content_generation;
        self.state_revision = header.state_revision;
        self.last_refresh_epoch = header.last_refresh_epoch;
        self.last_success_utc_ms = header.last_success_utc_ms;
        self.last_attempt_utc_ms = header.last_attempt_utc_ms;
        self.last_monotonic_ms = header.last_monotonic_ms;
        self.current_source_mask = header.current_source_mask;
        self.retained_source_mask = header.retained_source_mask;
        self.failed_source_mask = header.failed_source_mask;
        self.unsupported_source_mask = header.unsupported_source_mask;
        self.last_persistence_epoch = input.operation_epoch;
        self.phase = protocol.phaseFromInt(header.phase).?;
        self.last_finalize_content_changed = false;
        return .ok;
    }

    fn replaceSource(
        self: *Session,
        source: protocol.SourceId,
        source_generation: u64,
        facts: []const protocol.DisplayFact,
        texts: []const protocol.TextInput,
        bytes: []const u8,
    ) ResultCode {
        var retained_fact_count: u32 = 0;
        for (self.working.facts[0..self.working.fact_count]) |fact| {
            if (fact.source != source) retained_fact_count += 1;
        }
        if (retained_fact_count + facts.len > self.working.facts.len) return .out_of_memory;
        var normalized_total: u64 = 0;
        for (texts) |text| {
            const value = protocol.byteSlice(bytes, text.byte_offset, text.byte_length).?;
            const length = identity.normalizedLength(
                protocol.textKindFromInt(text.normalization_kind).?,
                value,
            ) orelse return .invalid_argument;
            normalized_total = std.math.add(u64, normalized_total, length) catch
                return .out_of_memory;
        }
        if (texts.len > self.config.maximum_text_binding_count or
            normalized_total > self.config.maximum_text_byte_count)
        {
            return .out_of_memory;
        }
        for (facts) |fact| {
            if (!factTextKindsMatch(&fact, texts)) return .invalid_argument;
            if ((fact.valid_mask & protocol.FactValid.observed_at_utc_ms) != 0) {
                const maximum = std.math.add(
                    i64,
                    self.round_captured_utc_ms,
                    self.config.maximum_future_skew_ms,
                ) catch return .invalid_argument;
                if (fact.observed_at_utc_ms > maximum) return .invalid_argument;
            }
        }
        if (!self.sourceOrdinalsUnique(source, facts)) return .invalid_argument;

        self.next.reset();
        copyFactsAndTextsExcept(&self.working, &self.next, source) catch
            return .out_of_memory;
        self.next.source_generations = self.working.source_generations;
        @memset(self.batch_text_map, protocol.no_text_index);
        for (texts, 0..) |text, index| {
            const kind = protocol.textKindFromInt(text.normalization_kind).?;
            const raw = protocol.byteSlice(bytes, text.byte_offset, text.byte_length).?;
            const normalized = identity.normalize(kind, raw, self.normalization_scratch) orelse
                return .invalid_argument;
            const handle = identity.textHandle(kind, normalized);
            const slot = self.next.appendText(kind, handle, normalized) catch |err|
                return if (err == error.HashCollision) .invalid_argument else .out_of_memory;
            self.batch_text_map[index] = slot;
        }
        for (facts) |fact| {
            self.next.facts[self.next.fact_count] = translateFact(
                &fact,
                source,
                self.batch_text_map,
            );
            self.next.fact_count += 1;
        }
        self.next.source_generations[@intFromEnum(source) - 1] = source_generation;
        const old_working = self.working;
        self.working = self.next;
        self.next = old_working;
        return .ok;
    }

    fn buildCanonical(self: *Session) ResultCode {
        sortFacts(&self.next);
        self.canonicalizeTexts(&self.next) catch return .out_of_memory;
        const fact_count = self.next.fact_count;
        @memset(self.union_parent, protocol.no_text_index);
        @memset(self.union_rank, 0);
        @memset(self.component_states, .{});
        @memset(self.identity_index, .{});
        for (0..fact_count) |index| self.union_parent[index] = @intCast(index);
        for (self.next.facts[0..fact_count], 0..) |fact, index| {
            const index_result = self.indexFactIdentities(&self.next, &fact, @intCast(index));
            if (index_result != .ok) return index_result;
        }
        const oem_result = self.matchOemConnectorProfiles(&self.next);
        if (oem_result != .ok) return oem_result;
        for (self.next.facts[0..fact_count], 0..) |fact, index| {
            const root = findRoot(self.union_parent, @intCast(index));
            accumulateComponent(
                &self.component_states[root],
                &self.next,
                &fact,
                &self.config,
            );
        }

        for (self.component_states[0..fact_count]) |*component| {
            if (!component.present) continue;
            if (component.conflict_reason != 0) {
                if (self.next.unresolved_count >= self.next.unresolved.len) return .out_of_memory;
                self.next.unresolved[self.next.unresolved_count] = unresolvedConflict(
                    protocol.unresolvedReasonFromInt(component.conflict_reason).?,
                    component.first_ordinal,
                    component.source_mask,
                    component.conflict_first,
                    component.conflict_second,
                    component.conflict_kind,
                );
                self.next.unresolved_count += 1;
                continue;
            }
            const canonical = chooseComponentCanonical(
                component,
                self.config.identity_source_priority_order,
            ) orelse {
                if (self.next.unresolved_count >= self.next.unresolved.len) return .out_of_memory;
                self.next.unresolved[self.next.unresolved_count] = unresolvedMissingComponent(
                    component,
                );
                self.next.unresolved_count += 1;
                continue;
            };
            if (self.next.node_count + 2 > self.next.nodes.len or
                self.next.edge_count >= self.next.edges.len or
                self.next.capability_count >= self.next.capabilities.len)
            {
                return .out_of_memory;
            }
            var aggregate = component.aggregate;
            if (aggregate.display_name.isZero()) {
                aggregate.display_name = aggregate.source_device;
            }
            const monitor_handle = identity.nodeHandle(.monitor, canonical);
            const connector_identity = if (!aggregate.target_identity.isZero())
                aggregate.target_identity
            else
                canonical;
            const connector_handle = identity.nodeHandle(.connector, connector_identity);
            const source_mask = component.source_mask;
            self.next.nodes[self.next.node_count] = .{
                .struct_size = @sizeOf(protocol.NodeOutput),
                .kind = @intFromEnum(protocol.NodeKind.connector),
                .node_handle = connector_handle,
                .generation = 0,
                .canonical_identity_handle = connector_identity,
                .display_name_handle = aggregate.display_name,
                .connector_kind = aggregate.output_technology,
                .connector_instance = aggregate.connector_instance,
                .status_flags = aggregate.status_flags,
                .source_mask = source_mask,
                .evidence_mask = aggregate.evidence_mask,
                .valid_mask = aggregate.valid_mask,
                .identity_mask = aggregate.identity_mask,
                .primary_source_id = aggregate.primary_source_id,
                .reserved_u32 = 0,
                .primary_source_record_ordinal = aggregate.primary_source_record_ordinal,
                .primary_payload_handle = aggregate.primary_payload_handle,
                .oem_profile_payload_handle = aggregate.oem_profile_payload_handle,
            };
            self.next.node_count += 1;
            self.next.nodes[self.next.node_count] = .{
                .struct_size = @sizeOf(protocol.NodeOutput),
                .kind = @intFromEnum(protocol.NodeKind.monitor),
                .node_handle = monitor_handle,
                .generation = 0,
                .canonical_identity_handle = canonical,
                .display_name_handle = aggregate.display_name,
                .connector_kind = aggregate.output_technology,
                .connector_instance = aggregate.connector_instance,
                .status_flags = aggregate.status_flags,
                .source_mask = source_mask,
                .evidence_mask = aggregate.evidence_mask,
                .valid_mask = aggregate.valid_mask,
                .identity_mask = aggregate.identity_mask,
                .primary_source_id = aggregate.primary_source_id,
                .reserved_u32 = 0,
                .primary_source_record_ordinal = aggregate.primary_source_record_ordinal,
                .primary_payload_handle = aggregate.primary_payload_handle,
                .oem_profile_payload_handle = aggregate.oem_profile_payload_handle,
            };
            self.next.node_count += 1;
            self.next.edges[self.next.edge_count] = .{
                .struct_size = @sizeOf(protocol.EdgeOutput),
                .kind = @intFromEnum(protocol.EdgeKind.connector_to_monitor),
                .parent_handle = connector_handle,
                .child_handle = monitor_handle,
                .source_mask = source_mask,
                .flags = 0,
                .reserved = .{0},
            };
            self.next.edge_count += 1;
            self.next.capabilities[self.next.capability_count] = aggregate.capability(
                monitor_handle,
                canonical,
            );
            self.next.capability_count += 1;
        }
        sortCanonical(&self.next);
        if (!canonicalHandlesUnique(&self.next)) return .invalid_argument;
        return .ok;
    }

    fn buildDiff(self: *Session, next_generation: u64) ResultCode {
        self.next.diff_count = 0;
        var result = appendEntityDiff(
            protocol.NodeOutput,
            .node,
            self.active.nodes[0..self.active.node_count],
            self.next.nodes[0..self.next.node_count],
            self.content_generation,
            next_generation,
            self.next.diffs,
            &self.next.diff_count,
            nodeHandleOf,
            sameNode,
        );
        if (result != .ok) return result;
        result = appendEntityDiff(
            protocol.EdgeOutput,
            .edge,
            self.active.edges[0..self.active.edge_count],
            self.next.edges[0..self.next.edge_count],
            self.content_generation,
            next_generation,
            self.next.diffs,
            &self.next.diff_count,
            edgeHandleOf,
            sameEdge,
        );
        if (result != .ok) return result;
        result = appendEntityDiff(
            protocol.DisplayCapabilityOutput,
            .capability,
            self.active.capabilities[0..self.active.capability_count],
            self.next.capabilities[0..self.next.capability_count],
            self.content_generation,
            next_generation,
            self.next.diffs,
            &self.next.diff_count,
            capabilityHandleOf,
            sameCapability,
        );
        if (result != .ok) return result;
        result = appendEntityDiff(
            TextSlot,
            .text,
            self.active.texts[0..self.active.text_count],
            self.next.texts[0..self.next.text_count],
            self.content_generation,
            next_generation,
            self.next.diffs,
            &self.next.diff_count,
            textHandleOf,
            sameText,
        );
        if (result != .ok) return result;
        return appendEntityDiff(
            protocol.UnresolvedOutput,
            .unresolved,
            self.active.unresolved[0..self.active.unresolved_count],
            self.next.unresolved[0..self.next.unresolved_count],
            self.content_generation,
            next_generation,
            self.next.diffs,
            &self.next.diff_count,
            unresolvedHandleOf,
            sameUnresolved,
        );
    }

    fn canAdvanceRevision(self: *const Session) bool {
        return self.state_revision != std.math.maxInt(u64);
    }

    fn advanceRevision(self: *Session) void {
        self.state_revision += 1;
    }

    fn indexFactIdentities(
        self: *Session,
        bank: *const Bank,
        fact: *const FactSlot,
        fact_index: u32,
    ) ResultCode {
        if ((fact.identity_mask & protocol.IdentityMask.target) != 0) {
            const result = self.indexIdentity(
                @intCast(protocol.IdentityMask.target),
                .{ .high = fact.adapter_luid, .low = fact.target_id },
                fact_index,
            );
            if (result != .ok) return result;
        }
        if ((fact.identity_mask & protocol.IdentityMask.monitor_path) != 0) {
            const result = self.indexIdentity(
                @intCast(protocol.IdentityMask.monitor_path),
                bank.texts[fact.monitor_path_text_index].handle,
                fact_index,
            );
            if (result != .ok) return result;
        }
        if ((fact.identity_mask & protocol.IdentityMask.source_device_name) != 0) {
            const result = self.indexIdentity(
                @intCast(protocol.IdentityMask.source_device_name),
                bank.texts[fact.source_device_text_index].handle,
                fact_index,
            );
            if (result != .ok) return result;
        }
        if ((fact.identity_mask & protocol.IdentityMask.edid_serial) != 0) {
            const result = self.indexIdentity(
                @intCast(protocol.IdentityMask.edid_serial),
                bank.texts[fact.edid_serial_text_index].handle,
                fact_index,
            );
            if (result != .ok) return result;
        }
        if ((fact.identity_mask & protocol.IdentityMask.source_object_key) != 0) {
            return self.indexIdentity(
                @intCast(protocol.IdentityMask.source_object_key),
                .{
                    .high = @intFromEnum(fact.source),
                    .low = fact.source_object_key,
                },
                fact_index,
            );
        }
        return .ok;
    }

    fn matchOemConnectorProfiles(
        self: *Session,
        bank: *const Bank,
    ) ResultCode {
        @memset(self.oem_candidate_used, 0);
        for (bank.facts[0..bank.fact_count], 0..) |profile, profile_index| {
            if (profile.source != .oem_connector_profile or
                (profile.valid_mask & protocol.FactValid.oem_match_rule) == 0)
            {
                continue;
            }

            const preferred = if ((profile.valid_mask &
                protocol.FactValid.preferred_adapter_token) != 0)
                bank.text(profile.match_text_index)
            else
                null;
            var preferred_count: u32 = 0;
            var all_count: u32 = 0;
            var preferred_best: ?u32 = null;
            var all_best: ?u32 = null;
            for (bank.facts[0..bank.fact_count], 0..) |candidate, candidate_index| {
                if (candidate.source != .display_config or
                    self.oem_candidate_used[candidate_index] != 0 or
                    (candidate.valid_mask & protocol.FactValid.output_technology) == 0 or
                    candidate.output_technology != profile.output_technology)
                {
                    continue;
                }

                all_count += 1;
                all_best = betterOemCandidate(
                    bank,
                    all_best,
                    @intCast(candidate_index),
                );
                if (preferred) |token| {
                    if ((candidate.valid_mask &
                        protocol.FactValid.adapter_device_path) == 0 or
                        std.mem.indexOf(
                            u8,
                            bank.text(candidate.match_text_index),
                            token,
                        ) == null)
                    {
                        continue;
                    }
                    preferred_count += 1;
                    preferred_best = betterOemCandidate(
                        bank,
                        preferred_best,
                        @intCast(candidate_index),
                    );
                }
            }

            const selected = if (preferred_count != 0)
                preferred_best
            else if ((profile.match_flags &
                protocol.OemMatchFlags.allow_any_active_adapter) != 0 and
                all_count == 1)
                all_best
            else
                null;
            if (selected) |candidate_index| {
                unionSets(
                    self.union_parent,
                    self.union_rank,
                    @intCast(profile_index),
                    candidate_index,
                );
                self.oem_candidate_used[candidate_index] = 1;
            }
        }
        return .ok;
    }

    fn indexIdentity(
        self: *Session,
        kind: u32,
        handle: protocol.Handle128,
        fact_index: u32,
    ) ResultCode {
        const mask = self.identity_index.len - 1;
        var probe = bank_module.hashHandle(handle, kind) & mask;
        var remaining = self.identity_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const slot = &self.identity_index[probe];
            if (!slot.occupied) {
                slot.* = .{
                    .occupied = true,
                    .kind = kind,
                    .handle = handle,
                    .fact_index = fact_index,
                };
                return .ok;
            }
            if (slot.kind == kind and bank_module.equalHandle(slot.handle, handle)) {
                unionSets(self.union_parent, self.union_rank, slot.fact_index, fact_index);
                return .ok;
            }
            probe = (probe + 1) & mask;
        }
        return .out_of_memory;
    }

    fn sourceOrdinalsUnique(
        self: *Session,
        source: protocol.SourceId,
        facts: []const protocol.DisplayFact,
    ) bool {
        @memset(self.identity_index, .{});
        const mask = self.identity_index.len - 1;
        for (facts) |fact| {
            const handle = protocol.Handle128{
                .high = @intFromEnum(source),
                .low = fact.source_record_ordinal,
            };
            var probe = bank_module.hashHandle(handle, 0) & mask;
            var remaining = self.identity_index.len;
            var inserted = false;
            while (remaining != 0) : (remaining -= 1) {
                const slot = &self.identity_index[probe];
                if (!slot.occupied) {
                    slot.* = .{
                        .occupied = true,
                        .kind = 0,
                        .handle = handle,
                        .fact_index = 0,
                    };
                    inserted = true;
                    break;
                }
                if (bank_module.equalHandle(slot.handle, handle)) return false;
                probe = (probe + 1) & mask;
            }
            if (!inserted) return false;
        }
        return true;
    }

    fn canonicalizeTexts(self: *Session, bank: *Bank) !void {
        @memset(self.text_remap, protocol.no_text_index);
        @memcpy(
            self.normalization_scratch[0..bank.text_byte_count],
            bank.text_bytes[0..bank.text_byte_count],
        );
        for (bank.texts[0..bank.text_count], 0..) |slot, index| {
            self.text_order_scratch[index] = .{
                .slot = slot,
                .old_index = @intCast(index),
            };
        }
        std.mem.sort(
            TextOrderSlot,
            self.text_order_scratch[0..bank.text_count],
            self.normalization_scratch[0..bank.text_byte_count],
            lessTextOrder,
        );
        var byte_count: u32 = 0;
        for (self.text_order_scratch[0..bank.text_count], 0..) |entry, new_index| {
            const old_start = entry.slot.offset;
            const old_end = old_start + entry.slot.length;
            const new_end = byte_count + entry.slot.length;
            @memcpy(
                bank.text_bytes[byte_count..new_end],
                self.normalization_scratch[old_start..old_end],
            );
            bank.texts[new_index] = .{
                .kind = entry.slot.kind,
                .handle = entry.slot.handle,
                .offset = byte_count,
                .length = entry.slot.length,
            };
            self.text_remap[entry.old_index] = @intCast(new_index);
            byte_count = new_end;
        }
        @memset(bank.text_bytes[byte_count..], 0);
        @memset(bank.texts[bank.text_count..], .{});
        bank.text_byte_count = byte_count;
        for (bank.facts[0..bank.fact_count]) |*fact| remapFactTexts(fact, self.text_remap);
        bank.rebuildTextIndex();
    }
};

const IdentityIndexSlot = struct {
    occupied: bool = false,
    kind: u32 = 0,
    handle: protocol.Handle128 = .{ .high = 0, .low = 0 },
    fact_index: u32 = 0,
};

const TextOrderSlot = struct {
    slot: TextSlot = .{},
    old_index: u32 = 0,
};

const Aggregate = struct {
    display_name: protocol.Handle128 = .{ .high = 0, .low = 0 },
    source_device: protocol.Handle128 = .{ .high = 0, .low = 0 },
    target_identity: protocol.Handle128 = .{ .high = 0, .low = 0 },
    valid_mask: u64 = 0,
    identity_mask: u64 = 0,
    status_flags: u64 = 0,
    evidence_mask: u64 = 0,
    capability_flags: u64 = 0,
    left: i32 = 0,
    top: i32 = 0,
    right: i32 = 0,
    bottom: i32 = 0,
    bits_per_color_channel: u32 = 0,
    minimum_luminance_milli_nits: u32 = 0,
    maximum_luminance_milli_nits: u32 = 0,
    maximum_full_frame_luminance_milli_nits: u32 = 0,
    output_technology: u32 = 0,
    connector_instance: u32 = 0,
    capability_source_mask: u32 = 0,
    capability_flags_rank: u32 = std.math.maxInt(u32),
    bounds_rank: u32 = std.math.maxInt(u32),
    bits_rank: u32 = std.math.maxInt(u32),
    minimum_rank: u32 = std.math.maxInt(u32),
    maximum_rank: u32 = std.math.maxInt(u32),
    full_frame_rank: u32 = std.math.maxInt(u32),
    output_technology_rank: u32 = std.math.maxInt(u32),
    connector_instance_rank: u32 = std.math.maxInt(u32),
    refresh_rank: u32 = std.math.maxInt(u32),
    friendly_rank: u32 = std.math.maxInt(u32),
    source_device_rank: u32 = std.math.maxInt(u32),
    primary_source_rank: u32 = std.math.maxInt(u32),
    primary_source_id: u32 = 0,
    primary_source_record_ordinal: u64 = 0,
    primary_payload_handle: protocol.Handle128 = .{ .high = 0, .low = 0 },
    oem_profile_payload_handle: protocol.Handle128 = .{ .high = 0, .low = 0 },
    refresh_numerator: u32 = 0,
    refresh_denominator: u32 = 0,

    fn capability(
        self: Aggregate,
        node_handle: protocol.Handle128,
        identity_handle: protocol.Handle128,
    ) protocol.DisplayCapabilityOutput {
        var output_flags = self.capability_flags;
        const complete_luminance =
            (self.valid_mask & (protocol.FactValid.minimum_luminance |
                protocol.FactValid.maximum_luminance |
                protocol.FactValid.full_frame_luminance)) ==
            (protocol.FactValid.minimum_luminance |
                protocol.FactValid.maximum_luminance |
                protocol.FactValid.full_frame_luminance) and
            self.maximum_luminance_milli_nits > self.minimum_luminance_milli_nits and
            self.maximum_full_frame_luminance_milli_nits >
                self.minimum_luminance_milli_nits and
            self.maximum_full_frame_luminance_milli_nits <=
                self.maximum_luminance_milli_nits;
        if ((output_flags & protocol.CapabilityFlags.high_dynamic_range_active) != 0 and
            complete_luminance)
        {
            output_flags |= protocol.CapabilityFlags.absolute_luminance_available;
        }
        return .{
            .struct_size = @sizeOf(protocol.DisplayCapabilityOutput),
            .flags = @intCast(output_flags),
            .node_handle = node_handle,
            .generation = 0,
            .display_identity_handle = identity_handle,
            .source_device_handle = self.source_device,
            .friendly_name_handle = self.display_name,
            .left = self.left,
            .top = self.top,
            .right = self.right,
            .bottom = self.bottom,
            .bits_per_color_channel = self.bits_per_color_channel,
            .minimum_luminance_milli_nits = self.minimum_luminance_milli_nits,
            .maximum_luminance_milli_nits = self.maximum_luminance_milli_nits,
            .maximum_full_frame_luminance_milli_nits = self.maximum_full_frame_luminance_milli_nits,
            .output_technology = self.output_technology,
            .capability_source_mask = self.capability_source_mask,
            .refresh_numerator = self.refresh_numerator,
            .refresh_denominator = self.refresh_denominator,
            .valid_mask = self.valid_mask,
        };
    }
};

const ComponentState = struct {
    present: bool = false,
    first_ordinal: u64 = 0,
    source_mask: u64 = 0,
    conflict_reason: u32 = 0,
    conflict_kind: u64 = 0,
    conflict_first: protocol.Handle128 = .{ .high = 0, .low = 0 },
    conflict_second: protocol.Handle128 = .{ .high = 0, .low = 0 },
    monitor_identity: protocol.Handle128 = .{ .high = 0, .low = 0 },
    target_identity: protocol.Handle128 = .{ .high = 0, .low = 0 },
    source_device_identity: protocol.Handle128 = .{ .high = 0, .low = 0 },
    edid_identity: protocol.Handle128 = .{ .high = 0, .low = 0 },
    source_object_identity: [protocol.source_count]protocol.Handle128 =
        [_]protocol.Handle128{.{ .high = 0, .low = 0 }} ** protocol.source_count,
    canonical_by_source: [protocol.source_count]protocol.Handle128 =
        [_]protocol.Handle128{.{ .high = 0, .low = 0 }} ** protocol.source_count,
    aggregate: Aggregate = .{},
};

fn translateFact(
    input: *const protocol.DisplayFact,
    source: protocol.SourceId,
    text_map: []const u32,
) FactSlot {
    return .{
        .source = source,
        .valid_mask = input.valid_mask,
        .identity_mask = input.identity_mask,
        .source_record_ordinal = input.source_record_ordinal,
        .adapter_luid = input.adapter_luid,
        .target_id = input.target_id,
        .connector_instance = input.connector_instance,
        .output_technology = input.output_technology,
        .status_flags = input.status_flags,
        .monitor_path_text_index = translatedTextIndex(
            input.valid_mask,
            protocol.FactValid.monitor_path,
            input.monitor_path_text_index,
            text_map,
        ),
        .source_device_text_index = translatedTextIndex(
            input.valid_mask,
            protocol.FactValid.source_device_name,
            input.source_device_text_index,
            text_map,
        ),
        .friendly_name_text_index = translatedTextIndex(
            input.valid_mask,
            protocol.FactValid.friendly_name,
            input.friendly_name_text_index,
            text_map,
        ),
        .edid_serial_text_index = translatedTextIndex(
            input.valid_mask,
            protocol.FactValid.edid_serial,
            input.edid_serial_text_index,
            text_map,
        ),
        .evidence_text_index = translatedTextIndex(
            input.valid_mask,
            protocol.FactValid.evidence,
            input.evidence_text_index,
            text_map,
        ),
        .match_text_index = translatedMatchTextIndex(input, text_map),
        .match_flags = input.match_flags,
        .position_x = input.position_x,
        .position_y = input.position_y,
        .width = input.width,
        .height = input.height,
        .refresh_numerator = input.refresh_numerator,
        .refresh_denominator = input.refresh_denominator,
        .bits_per_color_channel = input.bits_per_color_channel,
        .minimum_luminance_milli_nits = input.minimum_luminance_milli_nits,
        .maximum_luminance_milli_nits = input.maximum_luminance_milli_nits,
        .maximum_full_frame_luminance_milli_nits = input.maximum_full_frame_luminance_milli_nits,
        .capability_flags = input.capability_flags,
        .observed_at_utc_ms = input.observed_at_utc_ms,
        .source_object_key = input.source_object_key,
        .payload_handle = input.payload_handle,
    };
}

fn persistedFact(input: *const FactSlot) protocol.DisplayFact {
    return .{
        .struct_size = @sizeOf(protocol.DisplayFact),
        .source_id = @intFromEnum(input.source),
        .valid_mask = input.valid_mask,
        .identity_mask = input.identity_mask,
        .source_record_ordinal = input.source_record_ordinal,
        .adapter_luid = input.adapter_luid,
        .target_id = input.target_id,
        .connector_instance = input.connector_instance,
        .output_technology = input.output_technology,
        .status_flags = input.status_flags,
        .monitor_path_text_index = persistedTextIndex(
            input.valid_mask,
            protocol.FactValid.monitor_path,
            input.monitor_path_text_index,
        ),
        .source_device_text_index = persistedTextIndex(
            input.valid_mask,
            protocol.FactValid.source_device_name,
            input.source_device_text_index,
        ),
        .friendly_name_text_index = persistedTextIndex(
            input.valid_mask,
            protocol.FactValid.friendly_name,
            input.friendly_name_text_index,
        ),
        .edid_serial_text_index = persistedTextIndex(
            input.valid_mask,
            protocol.FactValid.edid_serial,
            input.edid_serial_text_index,
        ),
        .evidence_text_index = persistedTextIndex(
            input.valid_mask,
            protocol.FactValid.evidence,
            input.evidence_text_index,
        ),
        .match_text_index = persistedMatchTextIndex(input),
        .match_flags = input.match_flags,
        .reserved_u32 = 0,
        .position_x = input.position_x,
        .position_y = input.position_y,
        .width = input.width,
        .height = input.height,
        .refresh_numerator = input.refresh_numerator,
        .refresh_denominator = input.refresh_denominator,
        .bits_per_color_channel = input.bits_per_color_channel,
        .minimum_luminance_milli_nits = input.minimum_luminance_milli_nits,
        .maximum_luminance_milli_nits = input.maximum_luminance_milli_nits,
        .maximum_full_frame_luminance_milli_nits = input.maximum_full_frame_luminance_milli_nits,
        .capability_flags = input.capability_flags,
        .observed_at_utc_ms = input.observed_at_utc_ms,
        .source_object_key = input.source_object_key,
        .payload_handle = input.payload_handle,
    };
}

fn persistedTextIndex(valid_mask: u64, bit: u64, index: u32) u32 {
    return if ((valid_mask & bit) == 0) 0 else index;
}

fn persistedMatchTextIndex(input: *const FactSlot) u32 {
    const mask = protocol.FactValid.adapter_device_path |
        protocol.FactValid.preferred_adapter_token;
    return if ((input.valid_mask & mask) == 0) 0 else input.match_text_index;
}

fn translatedMatchTextIndex(
    input: *const protocol.DisplayFact,
    text_map: []const u32,
) u32 {
    const mask = protocol.FactValid.adapter_device_path |
        protocol.FactValid.preferred_adapter_token;
    return if ((input.valid_mask & mask) == 0)
        protocol.no_text_index
    else
        text_map[input.match_text_index];
}

fn translatedTextIndex(
    valid_mask: u64,
    bit: u64,
    local_index: u32,
    text_map: []const u32,
) u32 {
    return if ((valid_mask & bit) == 0) protocol.no_text_index else text_map[local_index];
}

fn remapFactTexts(fact: *FactSlot, remap: []const u32) void {
    fact.monitor_path_text_index = remapTextIndex(fact.monitor_path_text_index, remap);
    fact.source_device_text_index = remapTextIndex(fact.source_device_text_index, remap);
    fact.friendly_name_text_index = remapTextIndex(fact.friendly_name_text_index, remap);
    fact.edid_serial_text_index = remapTextIndex(fact.edid_serial_text_index, remap);
    fact.evidence_text_index = remapTextIndex(fact.evidence_text_index, remap);
    fact.match_text_index = remapTextIndex(fact.match_text_index, remap);
}

fn remapTextIndex(index: u32, remap: []const u32) u32 {
    return if (index == protocol.no_text_index) protocol.no_text_index else remap[index];
}

fn factTextKindsMatch(
    fact: *const protocol.DisplayFact,
    texts: []const protocol.TextInput,
) bool {
    return textKindMatches(fact, protocol.FactValid.monitor_path, fact.monitor_path_text_index, texts, .monitor_device_path) and
        textKindMatches(fact, protocol.FactValid.source_device_name, fact.source_device_text_index, texts, .source_device_name) and
        textKindMatches(fact, protocol.FactValid.friendly_name, fact.friendly_name_text_index, texts, .friendly_name) and
        textKindMatches(fact, protocol.FactValid.edid_serial, fact.edid_serial_text_index, texts, .identity_token) and
        textKindMatches(fact, protocol.FactValid.evidence, fact.evidence_text_index, texts, .evidence) and
        matchTextKindMatches(fact, texts);
}

fn matchTextKindMatches(
    fact: *const protocol.DisplayFact,
    texts: []const protocol.TextInput,
) bool {
    if ((fact.valid_mask & protocol.FactValid.adapter_device_path) != 0) {
        return protocol.textKindFromInt(
            texts[fact.match_text_index].normalization_kind,
        ).? == .adapter_device_path;
    }
    if ((fact.valid_mask & protocol.FactValid.preferred_adapter_token) != 0) {
        return protocol.textKindFromInt(
            texts[fact.match_text_index].normalization_kind,
        ).? == .adapter_match_token;
    }
    return true;
}

fn textKindMatches(
    fact: *const protocol.DisplayFact,
    bit: u64,
    index: u32,
    texts: []const protocol.TextInput,
    expected: protocol.TextKind,
) bool {
    if ((fact.valid_mask & bit) == 0) return true;
    return protocol.textKindFromInt(texts[index].normalization_kind).? == expected;
}

fn copyFactsAndTexts(source: *const Bank, destination: *Bank) !void {
    return copyFactsAndTextsExcept(source, destination, null);
}

fn copyFactsAndTextsExcept(
    source: *const Bank,
    destination: *Bank,
    excluded_source: ?protocol.SourceId,
) !void {
    destination.reset();
    for (source.facts[0..source.fact_count]) |fact| {
        if (excluded_source != null and fact.source == excluded_source.?) continue;
        var copied = fact;
        copied.monitor_path_text_index = try copyTextIndex(
            source,
            destination,
            fact.monitor_path_text_index,
        );
        copied.source_device_text_index = try copyTextIndex(
            source,
            destination,
            fact.source_device_text_index,
        );
        copied.friendly_name_text_index = try copyTextIndex(
            source,
            destination,
            fact.friendly_name_text_index,
        );
        copied.edid_serial_text_index = try copyTextIndex(
            source,
            destination,
            fact.edid_serial_text_index,
        );
        copied.evidence_text_index = try copyTextIndex(
            source,
            destination,
            fact.evidence_text_index,
        );
        copied.match_text_index = try copyTextIndex(
            source,
            destination,
            fact.match_text_index,
        );
        if (destination.fact_count >= destination.facts.len) return error.FactCapacity;
        destination.facts[destination.fact_count] = copied;
        destination.fact_count += 1;
    }
    destination.source_generations = source.source_generations;
}

fn copyTextIndex(source: *const Bank, destination: *Bank, index: u32) !u32 {
    if (index == protocol.no_text_index) return protocol.no_text_index;
    const slot: TextSlot = source.texts[index];
    return destination.appendText(slot.kind, slot.handle, source.text(index));
}

fn sortFacts(bank: *Bank) void {
    std.mem.sort(FactSlot, bank.facts[0..bank.fact_count], {}, lessFact);
}

fn lessFact(_: void, left: FactSlot, right: FactSlot) bool {
    if (left.source != right.source) {
        return @intFromEnum(left.source) < @intFromEnum(right.source);
    }
    return left.source_record_ordinal < right.source_record_ordinal;
}

fn lessTextOrder(bytes: []const u8, left: TextOrderSlot, right: TextOrderSlot) bool {
    if (!bank_module.equalHandle(left.slot.handle, right.slot.handle)) {
        return bank_module.lessHandle({}, left.slot.handle, right.slot.handle);
    }
    if (left.slot.kind != right.slot.kind) {
        return @intFromEnum(left.slot.kind) < @intFromEnum(right.slot.kind);
    }
    const left_bytes = bytes[left.slot.offset .. left.slot.offset + left.slot.length];
    const right_bytes = bytes[right.slot.offset .. right.slot.offset + right.slot.length];
    return std.mem.order(u8, left_bytes, right_bytes) == .lt;
}

fn findRoot(parents: []u32, value: u32) u32 {
    var current = value;
    while (parents[current] != current) current = parents[current];
    return current;
}

fn unionSets(parents: []u32, ranks: []u8, left: u32, right: u32) void {
    var left_root = findRoot(parents, left);
    var right_root = findRoot(parents, right);
    if (left_root == right_root) return;
    if (ranks[left_root] < ranks[right_root]) {
        const temp = left_root;
        left_root = right_root;
        right_root = temp;
    }
    parents[right_root] = left_root;
    if (ranks[left_root] == ranks[right_root]) ranks[left_root] +|= 1;
}

fn betterOemCandidate(
    bank: *const Bank,
    current: ?u32,
    candidate_index: u32,
) u32 {
    const current_index = current orelse return candidate_index;
    const left = bank.facts[candidate_index];
    const right = bank.facts[current_index];
    const left_active =
        (left.status_flags & protocol.DisplayStatusFlags.active) != 0;
    const right_active =
        (right.status_flags & protocol.DisplayStatusFlags.active) != 0;
    if (left_active != right_active) return if (left_active) candidate_index else current_index;
    const left_connected =
        (left.status_flags & protocol.DisplayStatusFlags.connected) != 0;
    const right_connected =
        (right.status_flags & protocol.DisplayStatusFlags.connected) != 0;
    if (left_connected != right_connected) {
        return if (left_connected) candidate_index else current_index;
    }
    if (left.connector_instance != right.connector_instance) {
        return if (left.connector_instance < right.connector_instance)
            candidate_index
        else
            current_index;
    }
    if (left.target_id != right.target_id) {
        return if (left.target_id < right.target_id) candidate_index else current_index;
    }
    return if (left.source_record_ordinal < right.source_record_ordinal)
        candidate_index
    else
        current_index;
}

fn chooseComponentCanonical(
    component: *const ComponentState,
    priority_order: u64,
) ?protocol.Handle128 {
    var rank: u32 = 0;
    while (rank < protocol.source_count) : (rank += 1) {
        const source = protocol.sourceFromInt(
            @as(u8, @truncate(priority_order >> @intCast(rank * 8))),
        ).?;
        const candidate = component.canonical_by_source[@intFromEnum(source) - 1];
        if (!candidate.isZero()) return candidate;
    }
    return null;
}

fn accumulateComponent(
    component: *ComponentState,
    bank: *const Bank,
    fact: *const FactSlot,
    config: *const protocol.Config,
) void {
    component.present = true;
    if (component.first_ordinal == 0 or
        fact.source_record_ordinal < component.first_ordinal)
    {
        component.first_ordinal = fact.source_record_ordinal;
    }
    const source_bit = protocol.sourceBit(fact.source);
    component.source_mask |= source_bit;
    const source_index: usize = @intFromEnum(fact.source) - 1;
    if (component.canonical_by_source[source_index].isZero()) {
        component.canonical_by_source[source_index] = factCanonicalCandidate(bank, fact);
    }
    if ((fact.identity_mask & protocol.IdentityMask.monitor_path) != 0) {
        noteIdentity(
            component,
            &component.monitor_identity,
            bank.texts[fact.monitor_path_text_index].handle,
            .conflicting_monitor_path,
            protocol.IdentityMask.monitor_path,
        );
    }
    if ((fact.identity_mask & protocol.IdentityMask.target) != 0) {
        noteIdentity(
            component,
            &component.target_identity,
            identity.targetHandle(fact.adapter_luid, fact.target_id),
            .conflicting_target,
            protocol.IdentityMask.target,
        );
    }
    if ((fact.identity_mask & protocol.IdentityMask.source_device_name) != 0) {
        noteIdentity(
            component,
            &component.source_device_identity,
            bank.texts[fact.source_device_text_index].handle,
            .conflicting_source_device,
            protocol.IdentityMask.source_device_name,
        );
    }
    if ((fact.identity_mask & protocol.IdentityMask.edid_serial) != 0) {
        noteIdentity(
            component,
            &component.edid_identity,
            bank.texts[fact.edid_serial_text_index].handle,
            .conflicting_edid_serial,
            protocol.IdentityMask.edid_serial,
        );
    }
    if ((fact.identity_mask & protocol.IdentityMask.source_object_key) != 0) {
        noteIdentity(
            component,
            &component.source_object_identity[source_index],
            identity.sourceObjectHandle(fact.source, fact.source_object_key),
            .conflicting_source_object,
            protocol.IdentityMask.source_object_key,
        );
    }
    accumulateAggregate(&component.aggregate, bank, fact, config);
}

fn noteIdentity(
    component: *ComponentState,
    current: *protocol.Handle128,
    value: protocol.Handle128,
    reason: protocol.UnresolvedReason,
    kind: u64,
) void {
    if (current.isZero()) {
        current.* = value;
    } else if (!bank_module.equalHandle(current.*, value) and
        component.conflict_reason == 0)
    {
        component.conflict_reason = @intFromEnum(reason);
        component.conflict_first = current.*;
        component.conflict_second = value;
        component.conflict_kind = kind;
    }
}

fn factCanonicalCandidate(
    bank: *const Bank,
    fact: *const FactSlot,
) protocol.Handle128 {
    if ((fact.identity_mask & protocol.IdentityMask.monitor_path) != 0) {
        return bank.texts[fact.monitor_path_text_index].handle;
    }
    if ((fact.identity_mask & protocol.IdentityMask.target) != 0) {
        return identity.targetHandle(fact.adapter_luid, fact.target_id);
    }
    if ((fact.identity_mask & protocol.IdentityMask.source_device_name) != 0) {
        return bank.texts[fact.source_device_text_index].handle;
    }
    if ((fact.identity_mask & protocol.IdentityMask.source_object_key) != 0) {
        return identity.sourceObjectHandle(fact.source, fact.source_object_key);
    }
    if ((fact.identity_mask & protocol.IdentityMask.edid_serial) != 0) {
        return bank.texts[fact.edid_serial_text_index].handle;
    }
    return identity.zeroHandle();
}

fn accumulateAggregate(
    result: *Aggregate,
    bank: *const Bank,
    fact: *const FactSlot,
    config: *const protocol.Config,
) void {
    result.identity_mask |= fact.identity_mask;
    result.status_flags |= fact.status_flags;
    result.valid_mask |= fact.valid_mask;
    const source_bit = protocol.sourceBit(fact.source);
    const current_projection_rank = protocol.priorityRank(
        config.projection_source_priority_order,
        fact.source,
    );
    if (current_projection_rank < result.primary_source_rank or
        (current_projection_rank == result.primary_source_rank and
            fact.source_record_ordinal < result.primary_source_record_ordinal))
    {
        result.primary_source_rank = current_projection_rank;
        result.primary_source_id = @intFromEnum(fact.source);
        result.primary_source_record_ordinal = fact.source_record_ordinal;
        result.primary_payload_handle = fact.payload_handle;
    }
    if (fact.source == .oem_connector_profile) {
        result.oem_profile_payload_handle = fact.payload_handle;
    }
    if (fact.evidence_text_index != protocol.no_text_index) {
        result.evidence_mask |= source_bit;
    }
    if ((fact.identity_mask & protocol.IdentityMask.target) != 0 and
        result.target_identity.isZero())
    {
        result.target_identity = identity.targetHandle(fact.adapter_luid, fact.target_id);
    }
    const current_friendly_rank = protocol.priorityRank(
        config.friendly_name_source_priority_order,
        fact.source,
    );
    if (fact.friendly_name_text_index != protocol.no_text_index and
        current_friendly_rank < result.friendly_rank)
    {
        result.display_name = bank.texts[fact.friendly_name_text_index].handle;
        result.friendly_rank = current_friendly_rank;
    }
    const current_identity_rank = protocol.priorityRank(
        config.identity_source_priority_order,
        fact.source,
    );
    if (fact.source_device_text_index != protocol.no_text_index and
        current_identity_rank < result.source_device_rank)
    {
        result.source_device = bank.texts[fact.source_device_text_index].handle;
        result.source_device_rank = current_identity_rank;
    }
    const current_capability_rank = protocol.priorityRank(
        config.capability_source_priority_order,
        fact.source,
    );
    applyCapability(result, fact, source_bit, current_capability_rank);
}

fn applyCapability(result: *Aggregate, fact: *const FactSlot, source_bit: u64, rank: u32) void {
    if ((fact.valid_mask & protocol.FactValid.capability_flags) != 0 and
        rank < result.capability_flags_rank)
    {
        result.capability_flags = fact.capability_flags;
        result.capability_flags_rank = rank;
        result.capability_source_mask |= @intCast(source_bit);
    }
    if ((fact.valid_mask & protocol.FactValid.bounds) != 0 and rank < result.bounds_rank) {
        result.left = fact.position_x;
        result.top = fact.position_y;
        result.right = fact.position_x + fact.width;
        result.bottom = fact.position_y + fact.height;
        result.bounds_rank = rank;
        result.capability_source_mask |= @intCast(source_bit);
    }
    if ((fact.valid_mask & protocol.FactValid.bits_per_color_channel) != 0 and
        rank < result.bits_rank)
    {
        result.bits_per_color_channel = fact.bits_per_color_channel;
        result.bits_rank = rank;
        result.capability_source_mask |= @intCast(source_bit);
    }
    if ((fact.valid_mask & protocol.FactValid.minimum_luminance) != 0 and
        rank < result.minimum_rank)
    {
        result.minimum_luminance_milli_nits = fact.minimum_luminance_milli_nits;
        result.minimum_rank = rank;
        result.capability_source_mask |= @intCast(source_bit);
    }
    if ((fact.valid_mask & protocol.FactValid.maximum_luminance) != 0 and
        rank < result.maximum_rank)
    {
        result.maximum_luminance_milli_nits = fact.maximum_luminance_milli_nits;
        result.maximum_rank = rank;
        result.capability_source_mask |= @intCast(source_bit);
    }
    if ((fact.valid_mask & protocol.FactValid.full_frame_luminance) != 0 and
        rank < result.full_frame_rank)
    {
        result.maximum_full_frame_luminance_milli_nits =
            fact.maximum_full_frame_luminance_milli_nits;
        result.full_frame_rank = rank;
        result.capability_source_mask |= @intCast(source_bit);
    }
    if ((fact.valid_mask & protocol.FactValid.output_technology) != 0 and
        rank < result.output_technology_rank)
    {
        result.output_technology = fact.output_technology;
        result.output_technology_rank = rank;
        result.capability_source_mask |= @intCast(source_bit);
    }
    if ((fact.valid_mask & protocol.FactValid.connector_instance) != 0 and
        rank < result.connector_instance_rank)
    {
        result.connector_instance = fact.connector_instance;
        result.connector_instance_rank = rank;
        result.capability_source_mask |= @intCast(source_bit);
    }
    if ((fact.valid_mask & protocol.FactValid.refresh_rate) != 0 and
        rank < result.refresh_rank)
    {
        result.refresh_numerator = fact.refresh_numerator;
        result.refresh_denominator = fact.refresh_denominator;
        result.refresh_rank = rank;
        result.capability_source_mask |= @intCast(source_bit);
    }
}

fn unresolvedConflict(
    reason: protocol.UnresolvedReason,
    ordinal: u64,
    source_mask: u64,
    first: protocol.Handle128,
    second: protocol.Handle128,
    identity_kind: u64,
) protocol.UnresolvedOutput {
    return .{
        .struct_size = @sizeOf(protocol.UnresolvedOutput),
        .reason = @intFromEnum(reason),
        .source_record_ordinal = ordinal,
        .source_mask = source_mask,
        .first_identity = first,
        .second_identity = second,
        .identity_kind = @intCast(identity_kind),
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
}

fn unresolvedMissingComponent(
    component: *const ComponentState,
) protocol.UnresolvedOutput {
    return .{
        .struct_size = @sizeOf(protocol.UnresolvedOutput),
        .reason = @intFromEnum(protocol.UnresolvedReason.missing_strong_identity),
        .source_record_ordinal = component.first_ordinal,
        .source_mask = component.source_mask,
        .first_identity = identity.zeroHandle(),
        .second_identity = identity.zeroHandle(),
        .identity_kind = 0,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
}

fn sortCanonical(bank: *Bank) void {
    std.mem.sort(protocol.NodeOutput, bank.nodes[0..bank.node_count], {}, lessNode);
    std.mem.sort(protocol.EdgeOutput, bank.edges[0..bank.edge_count], {}, lessEdge);
    std.mem.sort(
        protocol.DisplayCapabilityOutput,
        bank.capabilities[0..bank.capability_count],
        {},
        lessCapability,
    );
    std.mem.sort(
        protocol.UnresolvedOutput,
        bank.unresolved[0..bank.unresolved_count],
        {},
        lessUnresolved,
    );
}

fn canonicalHandlesUnique(bank: *const Bank) bool {
    var index: usize = 1;
    while (index < bank.node_count) : (index += 1) {
        if (bank_module.equalHandle(
            bank.nodes[index - 1].node_handle,
            bank.nodes[index].node_handle,
        )) return false;
    }
    index = 1;
    while (index < bank.capability_count) : (index += 1) {
        if (bank_module.equalHandle(
            bank.capabilities[index - 1].node_handle,
            bank.capabilities[index].node_handle,
        )) return false;
    }
    index = 1;
    while (index < bank.edge_count) : (index += 1) {
        if (bank_module.equalHandle(
            bank.edges[index - 1].parent_handle,
            bank.edges[index].parent_handle,
        ) and bank_module.equalHandle(
            bank.edges[index - 1].child_handle,
            bank.edges[index].child_handle,
        )) return false;
    }
    return true;
}

fn lessNode(_: void, left: protocol.NodeOutput, right: protocol.NodeOutput) bool {
    return bank_module.lessHandle({}, left.node_handle, right.node_handle);
}

fn lessEdge(_: void, left: protocol.EdgeOutput, right: protocol.EdgeOutput) bool {
    if (bank_module.equalHandle(left.parent_handle, right.parent_handle)) {
        return bank_module.lessHandle({}, left.child_handle, right.child_handle);
    }
    return bank_module.lessHandle({}, left.parent_handle, right.parent_handle);
}

fn lessCapability(
    _: void,
    left: protocol.DisplayCapabilityOutput,
    right: protocol.DisplayCapabilityOutput,
) bool {
    return bank_module.lessHandle({}, left.node_handle, right.node_handle);
}

fn lessUnresolved(
    _: void,
    left: protocol.UnresolvedOutput,
    right: protocol.UnresolvedOutput,
) bool {
    return bank_module.lessHandle(
        {},
        identity.unresolvedHandle(&left),
        identity.unresolvedHandle(&right),
    );
}

fn sameCanonical(left: *const Bank, right: *const Bank) bool {
    if (left.node_count != right.node_count or left.edge_count != right.edge_count or
        left.capability_count != right.capability_count or
        left.unresolved_count != right.unresolved_count) return false;
    for (left.nodes[0..left.node_count], right.nodes[0..right.node_count]) |a, b| {
        if (!sameNode(a, b)) return false;
    }
    for (left.edges[0..left.edge_count], right.edges[0..right.edge_count]) |a, b| {
        if (!sameEdge(a, b)) return false;
    }
    for (
        left.capabilities[0..left.capability_count],
        right.capabilities[0..right.capability_count],
    ) |a, b| {
        if (!sameCapability(a, b)) return false;
    }
    if (left.text_count != right.text_count or
        left.text_byte_count != right.text_byte_count)
    {
        return false;
    }
    for (left.texts[0..left.text_count], right.texts[0..right.text_count]) |a, b| {
        if (!sameText(a, b)) return false;
    }
    if (!std.mem.eql(
        u8,
        left.text_bytes[0..left.text_byte_count],
        right.text_bytes[0..right.text_byte_count],
    )) return false;
    return std.mem.eql(
        u8,
        std.mem.sliceAsBytes(left.unresolved[0..left.unresolved_count]),
        std.mem.sliceAsBytes(right.unresolved[0..right.unresolved_count]),
    );
}

fn sameNode(left: protocol.NodeOutput, right: protocol.NodeOutput) bool {
    var a = left;
    var b = right;
    a.generation = 0;
    b.generation = 0;
    return std.meta.eql(a, b);
}

fn sameEdge(left: protocol.EdgeOutput, right: protocol.EdgeOutput) bool {
    return std.meta.eql(left, right);
}

fn sameCapability(
    left: protocol.DisplayCapabilityOutput,
    right: protocol.DisplayCapabilityOutput,
) bool {
    var a = left;
    var b = right;
    a.generation = 0;
    b.generation = 0;
    return std.meta.eql(a, b);
}

fn nodeHandleOf(value: protocol.NodeOutput) protocol.Handle128 {
    return value.node_handle;
}

fn edgeHandleOf(value: protocol.EdgeOutput) protocol.Handle128 {
    return identity.edgeHandle(value.parent_handle, value.child_handle);
}

fn capabilityHandleOf(value: protocol.DisplayCapabilityOutput) protocol.Handle128 {
    return value.node_handle;
}

fn textHandleOf(value: TextSlot) protocol.Handle128 {
    return value.handle;
}

fn sameText(left: TextSlot, right: TextSlot) bool {
    return left.kind == right.kind and
        bank_module.equalHandle(left.handle, right.handle);
}

fn unresolvedHandleOf(value: protocol.UnresolvedOutput) protocol.Handle128 {
    return identity.unresolvedHandle(&value);
}

fn sameUnresolved(
    left: protocol.UnresolvedOutput,
    right: protocol.UnresolvedOutput,
) bool {
    return std.meta.eql(left, right);
}

fn appendEntityDiff(
    comptime T: type,
    entity_kind: protocol.EntityKind,
    old_values: []const T,
    new_values: []const T,
    old_generation: u64,
    new_generation: u64,
    outputs: []protocol.DiffEntry,
    output_count: *u32,
    comptime handleOf: fn (T) protocol.Handle128,
    comptime equal: fn (T, T) bool,
) ResultCode {
    var old_index: usize = 0;
    var new_index: usize = 0;
    while (old_index < old_values.len or new_index < new_values.len) {
        if (output_count.* >= outputs.len) return .out_of_memory;
        if (old_index >= old_values.len) {
            appendDiff(outputs, output_count, entity_kind, .added, handleOf(new_values[new_index]), 0, new_generation, 0);
            new_index += 1;
            continue;
        }
        if (new_index >= new_values.len) {
            appendDiff(outputs, output_count, entity_kind, .removed, handleOf(old_values[old_index]), old_generation, 0, 0);
            old_index += 1;
            continue;
        }
        const old_handle = handleOf(old_values[old_index]);
        const new_handle = handleOf(new_values[new_index]);
        if (bank_module.equalHandle(old_handle, new_handle)) {
            if (!equal(old_values[old_index], new_values[new_index])) {
                appendDiff(outputs, output_count, entity_kind, .changed, new_handle, old_generation, new_generation, std.math.maxInt(u64));
            }
            old_index += 1;
            new_index += 1;
        } else if (bank_module.lessHandle({}, old_handle, new_handle)) {
            appendDiff(outputs, output_count, entity_kind, .removed, old_handle, old_generation, 0, 0);
            old_index += 1;
        } else {
            appendDiff(outputs, output_count, entity_kind, .added, new_handle, 0, new_generation, 0);
            new_index += 1;
        }
    }
    return .ok;
}

fn appendDiff(
    outputs: []protocol.DiffEntry,
    count: *u32,
    entity_kind: protocol.EntityKind,
    change_kind: protocol.ChangeKind,
    handle: protocol.Handle128,
    old_generation: u64,
    new_generation: u64,
    changed_field_mask: u64,
) void {
    outputs[count.*] = .{
        .struct_size = @sizeOf(protocol.DiffEntry),
        .entity_kind = @intFromEnum(entity_kind),
        .change_kind = @intFromEnum(change_kind),
        .reserved_u32 = 0,
        .entity_handle = handle,
        .old_generation = old_generation,
        .new_generation = new_generation,
        .changed_field_mask = changed_field_mask,
    };
    count.* += 1;
}

fn copyOutputs(comptime T: type, source: []const T, outputs: []T) ResultCode {
    if (outputs.len < source.len) return .buffer_too_small;
    for (outputs[0..source.len]) |*output| {
        if (!protocol.emptyBytes(T, output)) return .abi_mismatch;
    }
    @memcpy(outputs[0..source.len], source);
    return .ok;
}

fn allBytesZero(bytes: []const u8) bool {
    for (bytes) |byte| if (byte != 0) return false;
    return true;
}

fn persistenceChecksum(
    header: *const protocol.PersistenceHeader,
    facts: []const protocol.DisplayFact,
    texts: []const protocol.TextInput,
    bytes: []const u8,
) protocol.Handle128 {
    var canonical_header = header.*;
    canonical_header.checksum = identity.zeroHandle();
    var hasher = std.crypto.hash.sha2.Sha256.init(.{});
    hasher.update("rm-display-persistence-v1");
    hasher.update(std.mem.asBytes(&canonical_header));
    hasher.update(std.mem.sliceAsBytes(facts));
    hasher.update(std.mem.sliceAsBytes(texts));
    hasher.update(bytes);
    var digest: [32]u8 = undefined;
    hasher.final(&digest);
    var result = protocol.Handle128{
        .high = std.mem.readInt(u64, digest[0..8], .little),
        .low = std.mem.readInt(u64, digest[8..16], .little),
    };
    if (result.isZero()) result.low = 1;
    return result;
}

fn residentByteCount(config: *const protocol.Config) !u64 {
    var total: u64 = @sizeOf(Session);
    const bank_bytes = try bankResidentByteCount(config);
    total = try std.math.add(u64, total, try std.math.mul(u64, bank_bytes, 3));
    total = try addAllocation(total, config.maximum_text_binding_count, @sizeOf(u32));
    total = try addAllocation(total, config.maximum_text_byte_count, @sizeOf(u8));
    total = try addAllocation(total, config.identity_index_capacity, @sizeOf(IdentityIndexSlot));
    total = try addAllocation(total, config.maximum_observation_count, @sizeOf(u32));
    total = try addAllocation(total, config.maximum_observation_count, @sizeOf(u8));
    total = try addAllocation(total, config.maximum_observation_count, @sizeOf(u8));
    total = try addAllocation(
        total,
        config.maximum_observation_count,
        @sizeOf(ComponentState),
    );
    total = try addAllocation(total, config.maximum_text_binding_count, @sizeOf(TextOrderSlot));
    total = try addAllocation(total, config.maximum_text_binding_count, @sizeOf(u32));
    return total;
}

fn bankResidentByteCount(config: *const protocol.Config) !u64 {
    var total: u64 = 0;
    total = try addAllocation(total, config.maximum_observation_count, @sizeOf(FactSlot));
    total = try addAllocation(total, config.maximum_text_binding_count, @sizeOf(TextSlot));
    total = try addAllocation(total, config.maximum_text_byte_count, @sizeOf(u8));
    total = try addAllocation(total, config.text_index_capacity, @sizeOf(u32));
    total = try addAllocation(total, config.maximum_node_count, @sizeOf(protocol.NodeOutput));
    total = try addAllocation(total, config.maximum_edge_count, @sizeOf(protocol.EdgeOutput));
    total = try addAllocation(
        total,
        config.maximum_capability_count,
        @sizeOf(protocol.DisplayCapabilityOutput),
    );
    total = try addAllocation(total, config.maximum_unresolved_count, @sizeOf(protocol.UnresolvedOutput));
    total = try addAllocation(total, config.maximum_diff_entry_count, @sizeOf(protocol.DiffEntry));
    return total;
}

fn addAllocation(total: u64, count: u32, item_size: usize) !u64 {
    return std.math.add(u64, total, try std.math.mul(u64, count, item_size));
}

fn lockMutex(mutex: *windows.SRWLOCK) void {
    windows.ntdll.RtlAcquireSRWLockExclusive(mutex);
}

fn unlockMutex(mutex: *windows.SRWLOCK) void {
    windows.ntdll.RtlReleaseSRWLockExclusive(mutex);
}
