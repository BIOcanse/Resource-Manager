const std = @import("std");
const protocol = @import("protocol.zig");
const bank_module = @import("bank.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const Bank = bank_module.Bank;

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.Config,
    active: Bank,
    staging: Bank,
    sort_indices: []u32,
    resident_byte_count: u64,
    import_generation: u64 = 0,
    state_revision: u64 = 1,
    last_operation_epoch: u64 = 0,
    last_plan_epoch: u64 = 0,
    last_feedback_epoch: u64 = 0,
    last_snapshot_epoch: u64 = 0,
    last_command_utc_ms: i64 = 0,
    next_mutation_version: u64 = 1,
    mutation_exhausted: bool = false,
    committed_mutation_version: u64 = 0,

    pub fn create(config: *const protocol.Config) !*Session {
        if (!protocol.validConfig(config)) return error.InvalidConfiguration;
        const resident_byte_count = residentByteCount(config) catch return error.InvalidConfiguration;
        if (resident_byte_count > config.resident_byte_budget) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);
        var active = try Bank.init(allocator, config);
        errdefer active.deinit(allocator);
        var staging = try Bank.init(allocator, config);
        errdefer staging.deinit(allocator);
        const sort_indices = try allocator.alloc(u32, config.maximum_path_count);
        errdefer allocator.free(sort_indices);
        @memset(sort_indices, 0);
        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .active = active,
            .staging = staging,
            .sort_indices = sort_indices,
            .resident_byte_count = resident_byte_count,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.sort_indices);
        self.staging.deinit(allocator);
        self.active.deinit(allocator);
        allocator.destroy(self);
    }

    pub fn reconfigure(self: *Session, config: *const protocol.Config) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validConfig(config)) return .abi_mismatch;
        if (config.generation <= self.config.generation) return .stale_frame;
        if (!sameCapacities(config, &self.config) or self.resident_byte_count > config.resident_byte_budget) {
            return .invalid_argument;
        }
        self.config = config.*;
        self.advanceRevision();
        return .ok;
    }

    pub fn capacity(self: *Session) protocol.Capacity {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .registration_capacity = self.config.maximum_registration_count,
            .path_capacity = self.config.maximum_path_count,
            .persistence_operation_capacity = self.config.maximum_persistence_operation_count,
            .registration_snapshot_capacity = self.config.maximum_registration_snapshot_count,
            .path_snapshot_capacity = self.config.maximum_path_snapshot_count,
            .executable_path_byte_capacity_per_path = self.config.maximum_executable_path_byte_count,
            .root_path_byte_capacity_per_path = self.config.maximum_root_path_byte_count,
            .registration_index_capacity = self.config.registration_index_capacity,
            .path_index_capacity = self.config.path_index_capacity,
            .reserved_u32 = 0,
            .resident_byte_count = self.resident_byte_count,
            .reserved = .{ 0, 0, 0, 0 },
        };
    }

    pub fn importPersisted(
        self: *Session,
        input: *const protocol.ImportInput,
        rows: []const protocol.PersistedPathInput,
        key_bytes: []const u8,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validImport(input, &self.config)) return .abi_mismatch;
        if (input.import_generation <= self.import_generation or input.operation_epoch <= self.last_operation_epoch or
            input.command_utc_ms < self.last_command_utc_ms)
        {
            return .stale_frame;
        }
        const result = self.staging.loadPersisted(&self.config, input, rows, key_bytes);
        if (result != .ok) return result;
        const previous = self.active;
        self.active = self.staging;
        self.staging = previous;
        self.import_generation = input.import_generation;
        self.last_operation_epoch = input.operation_epoch;
        self.last_command_utc_ms = input.command_utc_ms;
        self.committed_mutation_version = if (self.mutation_exhausted)
            std.math.maxInt(u64)
        else
            self.next_mutation_version - 1;
        self.last_plan_epoch = 0;
        self.last_feedback_epoch = 0;
        self.last_snapshot_epoch = 0;
        self.advanceRevision();
        return .ok;
    }

    pub fn observe(
        self: *Session,
        input: *const protocol.ObserveInput,
        key_bytes: []const u8,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validObserve(input, &self.config, key_bytes)) return .abi_mismatch;
        if (!packedObservationKeys(input, key_bytes)) return .invalid_argument;
        if (input.operation_epoch <= self.last_operation_epoch or input.command_utc_ms < self.last_command_utc_ms) {
            return .stale_frame;
        }
        const executable = protocol.keySlice(key_bytes, input.executable_path_offset, input.executable_path_length).?;
        const root = protocol.keySlice(key_bytes, input.root_path_offset, input.root_path_length).?;
        if (!protocol.isSameOrUnder(executable, root)) return .invalid_argument;
        if ((input.path_flags & protocol.PathFlags.root_confirmed) != 0 and
            (input.path_flags & protocol.PathFlags.identity_confirmed) == 0)
        {
            return .invalid_argument;
        }

        const existing_path_slot = self.active.findPath(input.path_handle);
        if (existing_path_slot) |path_slot| {
            const path = &self.active.paths[path_slot];
            const registration = &self.active.registrations[path.registration_index];
            if (registration.software_handle != input.software_handle or
                !std.mem.eql(u8, self.active.executablePath(path_slot), executable))
            {
                return .invalid_argument;
            }
        }
        if (!self.active.rootHandleCompatible(input.root_handle, root)) return .invalid_argument;

        var registration_slot = self.active.findRegistration(input.software_handle);
        if (registration_slot == null and self.active.registration_count == self.config.maximum_registration_count) {
            return .out_of_memory;
        }
        if (existing_path_slot == null and self.active.path_count == self.config.maximum_path_count) {
            return .out_of_memory;
        }
        const metadata_changed = if (registration_slot) |slot| blk: {
            const registration = &self.active.registrations[slot];
            break :blk registration.catalog_entry_handle != input.catalog_entry_handle or
                registration.display_name_handle != input.display_name_handle or
                registration.software_kind_handle != input.software_kind_handle;
        } else false;
        const observed_changed = if (existing_path_slot) |slot| blk: {
            const path = &self.active.paths[slot];
            break :blk path.delete_pending or input.observed_at_utc_ms < path.first_observed_utc_ms or
                ((input.path_flags & protocol.PathFlags.identity_confirmed) != 0 and !path.identity_confirmed) or
                (!path.root_confirmed and
                    (path.root_handle != input.root_handle or !std.mem.eql(u8, self.active.rootPath(slot), root))) or
                ((input.path_flags & protocol.PathFlags.root_confirmed) != 0 and !path.root_confirmed);
        } else true;
        var required_mutation_count: u32 = if (metadata_changed or observed_changed) 1 else 0;
        if (metadata_changed) {
            for (self.active.paths, 0..) |candidate, index| {
                if (!candidate.occupied or candidate.registration_index != registration_slot.? or
                    (existing_path_slot != null and index == existing_path_slot.?)) continue;
                required_mutation_count += 1;
            }
        }
        if (!self.canAllocateMutations(required_mutation_count)) return .out_of_memory;
        if (registration_slot == null) {
            registration_slot = self.active.createRegistration(
                input.software_handle,
                input.catalog_entry_handle,
                input.display_name_handle,
                input.software_kind_handle,
            ) orelse return .out_of_memory;
        }

        const registration = &self.active.registrations[registration_slot.?];
        if (metadata_changed) {
            registration.catalog_entry_handle = input.catalog_entry_handle;
            registration.display_name_handle = input.display_name_handle;
            registration.software_kind_handle = input.software_kind_handle;
        }

        var observed_path_slot = existing_path_slot;
        if (observed_path_slot == null) {
            observed_path_slot = self.active.createPath(
                registration_slot.?,
                input.path_handle,
                input.root_handle,
                executable,
                root,
                input.observed_at_utc_ms,
                (input.path_flags & protocol.PathFlags.identity_confirmed) != 0,
                (input.path_flags & protocol.PathFlags.root_confirmed) != 0,
            ) orelse return .out_of_memory;
        }

        const path = &self.active.paths[observed_path_slot.?];
        if (path.delete_pending) {
            path.delete_pending = false;
        }
        if (input.observed_at_utc_ms < path.first_observed_utc_ms) {
            path.first_observed_utc_ms = input.observed_at_utc_ms;
        }
        if ((input.path_flags & protocol.PathFlags.identity_confirmed) != 0 and !path.identity_confirmed) {
            path.identity_confirmed = true;
        }
        if (!path.root_confirmed and
            (path.root_handle != input.root_handle or !std.mem.eql(u8, self.active.rootPath(observed_path_slot.?), root)))
        {
            path.root_handle = input.root_handle;
            self.active.writeRootPath(observed_path_slot.?, root);
        }
        if ((input.path_flags & protocol.PathFlags.root_confirmed) != 0 and !path.root_confirmed) {
            path.root_confirmed = true;
            path.identity_confirmed = true;
        }

        if (metadata_changed) {
            for (self.active.paths, 0..) |candidate, index| {
                if (!candidate.occupied or candidate.registration_index != registration_slot.? or
                    index == observed_path_slot.?) continue;
                self.markDirty(@intCast(index));
            }
        }
        if (metadata_changed or observed_changed) self.markDirty(observed_path_slot.?);
        self.last_operation_epoch = input.operation_epoch;
        self.last_command_utc_ms = input.command_utc_ms;
        self.advanceRevision();
        return .ok;
    }

    pub fn confirmRoot(
        self: *Session,
        input: *const protocol.ConfirmRootInput,
        key_bytes: []const u8,
        confirmed_count: *u32,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (confirmed_count.* != 0) return .abi_mismatch;
        if (!protocol.validConfirmRoot(input, &self.config, key_bytes)) return .abi_mismatch;
        if (input.operation_epoch <= self.last_operation_epoch or input.command_utc_ms < self.last_command_utc_ms) {
            return .stale_frame;
        }
        const registration_slot = self.active.findRegistration(input.software_handle) orelse return .no_data;
        const root = key_bytes;
        if (!self.active.rootHandleCompatible(input.root_handle, root)) return .invalid_argument;
        var count: u32 = 0;
        var changed_count: u32 = 0;
        for (self.active.paths, 0..) |path, index| {
            if (!path.occupied or path.delete_pending or path.registration_index != registration_slot or
                !protocol.isSameOrUnder(self.active.executablePath(@intCast(index)), root)) continue;
            count += 1;
            if (!path.root_confirmed or !path.identity_confirmed or path.root_handle != input.root_handle or
                !std.mem.eql(u8, self.active.rootPath(@intCast(index)), root)) changed_count += 1;
        }
        if (count == 0) return .no_data;
        if (!self.canAllocateMutations(changed_count)) return .out_of_memory;
        for (self.active.paths, 0..) |*path, index| {
            if (!path.occupied or path.delete_pending or path.registration_index != registration_slot or
                !protocol.isSameOrUnder(self.active.executablePath(@intCast(index)), root)) continue;
            if (!path.root_confirmed or !path.identity_confirmed or path.root_handle != input.root_handle or
                !std.mem.eql(u8, self.active.rootPath(@intCast(index)), root))
            {
                path.root_confirmed = true;
                path.identity_confirmed = true;
                path.root_handle = input.root_handle;
                self.active.writeRootPath(@intCast(index), root);
                self.markDirty(@intCast(index));
            }
        }
        confirmed_count.* = count;
        self.last_operation_epoch = input.operation_epoch;
        self.last_command_utc_ms = input.command_utc_ms;
        self.advanceRevision();
        return .ok;
    }

    pub fn markMissing(self: *Session, input: *const protocol.MarkMissingInput) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validMarkMissing(input, &self.config)) return .abi_mismatch;
        if (input.operation_epoch <= self.last_operation_epoch or input.command_utc_ms < self.last_command_utc_ms) {
            return .stale_frame;
        }
        const path_slot = self.active.findPath(input.path_handle) orelse return .no_data;
        const path = &self.active.paths[path_slot];
        const registration = &self.active.registrations[path.registration_index];
        if (registration.software_handle != input.software_handle) return .invalid_argument;
        if (!path.delete_pending) {
            if (!self.canAllocateMutations(1)) return .out_of_memory;
            path.delete_pending = true;
            self.markDirty(path_slot);
        }
        self.last_operation_epoch = input.operation_epoch;
        self.last_command_utc_ms = input.command_utc_ms;
        self.advanceRevision();
        return .ok;
    }

    pub fn planPersistence(
        self: *Session,
        input: *const protocol.PlanPersistenceInput,
        operations: []protocol.PersistenceOperation,
        output: *protocol.PersistencePlanOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validPlan(input, &self.config)) return .abi_mismatch;
        if (input.plan_epoch <= self.last_plan_epoch) return .stale_frame;
        if (operations.len < input.maximum_operation_count) return .buffer_too_small;
        if (!protocol.emptyPlanOutput(output)) return .abi_mismatch;
        for (operations[0..input.maximum_operation_count]) |*operation| {
            if (!protocol.emptyPersistenceOperation(operation)) return .abi_mismatch;
        }

        const dirty_count = self.collectDirtyPaths();
        const output_count: u32 = @min(dirty_count, input.maximum_operation_count);
        for (self.sort_indices[0..output_count], 0..) |path_slot, output_index| {
            const path = &self.active.paths[path_slot];
            const registration = &self.active.registrations[path.registration_index];
            var flags: u32 = 0;
            if (path.delete_pending) flags |= protocol.PersistenceFlags.delete;
            if (path.identity_confirmed) flags |= protocol.PersistenceFlags.identity_confirmed;
            if (path.root_confirmed) flags |= protocol.PersistenceFlags.root_confirmed;
            operations[output_index] = .{
                .struct_size = @sizeOf(protocol.PersistenceOperation),
                .flags = flags,
                .mutation_version = path.mutation_version,
                .software_handle = registration.software_handle,
                .catalog_entry_handle = registration.catalog_entry_handle,
                .display_name_handle = registration.display_name_handle,
                .software_kind_handle = registration.software_kind_handle,
                .path_handle = path.path_handle,
                .root_handle = path.root_handle,
                .first_observed_utc_ms = path.first_observed_utc_ms,
                .reserved = .{ 0, 0 },
            };
        }
        const first_mutation = if (output_count == 0) 0 else operations[0].mutation_version;
        const last_mutation = if (output_count == 0) 0 else operations[output_count - 1].mutation_version;
        self.last_plan_epoch = input.plan_epoch;
        self.advanceRevision();
        output.* = .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.PersistencePlanOutput),
            .configuration_generation = self.config.generation,
            .state_revision = self.state_revision,
            .plan_epoch = input.plan_epoch,
            .operation_count = output_count,
            .total_dirty_count = dirty_count,
            .flags = (if (output_count < dirty_count) protocol.PlanOutputFlags.has_more else 0) |
                (if (self.mutation_exhausted) protocol.PlanOutputFlags.mutation_exhausted else 0),
            .reserved_u32 = 0,
            .first_mutation_version = first_mutation,
            .last_mutation_version = last_mutation,
            .next_mutation_version = self.next_mutation_version,
            .reserved = .{ 0, 0, 0 },
        };
        return .ok;
    }

    pub fn applyPersistenceFeedback(
        self: *Session,
        input: *const protocol.PersistenceFeedbackInput,
        feedbacks: []const protocol.PersistenceFeedback,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validFeedbackInput(input, &self.config)) return .abi_mismatch;
        if (feedbacks.len != input.feedback_count) return .invalid_argument;
        if (input.feedback_epoch <= self.last_feedback_epoch) return .stale_frame;
        var previous_mutation: u64 = 0;
        for (feedbacks) |*feedback| {
            if (!protocol.validFeedback(feedback) or feedback.mutation_version <= previous_mutation) {
                return .invalid_argument;
            }
            previous_mutation = feedback.mutation_version;
            const path_slot = self.active.findPath(feedback.path_handle) orelse return .stale_frame;
            const path = &self.active.paths[path_slot];
            const registration = &self.active.registrations[path.registration_index];
            if (registration.software_handle != feedback.software_handle or !path.dirty or
                path.mutation_version != feedback.mutation_version)
            {
                return .stale_frame;
            }
        }
        for (feedbacks) |feedback| {
            const path_slot = self.active.findPath(feedback.path_handle).?;
            if (self.active.paths[path_slot].delete_pending) {
                self.active.removePath(path_slot);
            } else {
                self.active.paths[path_slot].dirty = false;
            }
        }
        self.refreshCommittedMutationVersion();
        self.last_feedback_epoch = input.feedback_epoch;
        self.advanceRevision();
        return .ok;
    }

    pub fn snapshot(
        self: *Session,
        input: *const protocol.SnapshotInput,
        registrations: []protocol.RegistrationSnapshot,
        paths: []protocol.PathSnapshot,
        output: *protocol.SnapshotOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validSnapshot(input, &self.config)) return .abi_mismatch;
        if (input.snapshot_epoch <= self.last_snapshot_epoch) return .stale_frame;
        if (registrations.len < input.maximum_registration_count or paths.len < input.maximum_path_count) {
            return .buffer_too_small;
        }
        if (!protocol.emptySnapshotOutput(output)) return .abi_mismatch;
        for (registrations[0..input.maximum_registration_count]) |*registration| {
            if (!protocol.emptyRegistrationSnapshot(registration)) return .abi_mismatch;
        }
        for (paths[0..input.maximum_path_count]) |*path| {
            if (!protocol.emptyPathSnapshot(path)) return .abi_mismatch;
        }

        const registration_page = self.writeRegistrationPage(input, registrations);
        const path_page = self.writePathPage(input, paths);
        const totals = self.snapshotTotals();
        var flags: u32 = 0;
        if (registration_page.has_more) flags |= protocol.SnapshotOutputFlags.registration_has_more;
        if (path_page.has_more) flags |= protocol.SnapshotOutputFlags.path_has_more;
        if (self.mutation_exhausted) flags |= protocol.SnapshotOutputFlags.mutation_exhausted;
        self.last_snapshot_epoch = input.snapshot_epoch;
        self.advanceRevision();
        output.* = .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.SnapshotOutput),
            .configuration_generation = self.config.generation,
            .import_generation = self.import_generation,
            .state_revision = self.state_revision,
            .last_operation_epoch = self.last_operation_epoch,
            .last_plan_epoch = self.last_plan_epoch,
            .last_feedback_epoch = self.last_feedback_epoch,
            .last_snapshot_epoch = input.snapshot_epoch,
            .last_command_utc_ms = self.last_command_utc_ms,
            .next_mutation_version = self.next_mutation_version,
            .committed_mutation_version = self.committed_mutation_version,
            .registration_count = totals.registration_count,
            .path_count = totals.path_count,
            .dirty_path_count = totals.dirty_path_count,
            .registration_output_count = registration_page.output_count,
            .path_output_count = path_page.output_count,
            .next_registration_cursor = registration_page.next_cursor,
            .next_path_cursor = path_page.next_cursor,
            .flags = flags,
            .resident_byte_count = self.resident_byte_count,
            .reserved = .{ 0, 0 },
        };
        return .ok;
    }

    fn markDirty(self: *Session, path_slot: u32) void {
        std.debug.assert(!self.mutation_exhausted);
        const path = &self.active.paths[path_slot];
        path.dirty = true;
        path.mutation_version = self.next_mutation_version;
        if (self.next_mutation_version == std.math.maxInt(u64)) {
            self.mutation_exhausted = true;
        } else {
            self.next_mutation_version += 1;
        }
        self.refreshCommittedMutationVersion();
    }

    fn canAllocateMutations(self: *const Session, count: u32) bool {
        if (count == 0) return true;
        if (self.mutation_exhausted) return false;
        return count - 1 <= std.math.maxInt(u64) - self.next_mutation_version;
    }

    fn refreshCommittedMutationVersion(self: *Session) void {
        var minimum_dirty: ?u64 = null;
        for (self.active.paths) |path| {
            if (!path.occupied or !path.dirty) continue;
            minimum_dirty = if (minimum_dirty) |current| @min(current, path.mutation_version) else path.mutation_version;
        }
        const settled_through = if (minimum_dirty) |mutation|
            mutation - 1
        else if (self.mutation_exhausted)
            std.math.maxInt(u64)
        else
            self.next_mutation_version - 1;
        self.committed_mutation_version = @max(self.committed_mutation_version, settled_through);
    }

    fn collectDirtyPaths(self: *Session) u32 {
        var count: u32 = 0;
        for (self.active.paths, 0..) |path, index| {
            if (!path.occupied or !path.dirty) continue;
            self.sort_indices[count] = @intCast(index);
            count += 1;
        }
        var outer: usize = 1;
        while (outer < count) : (outer += 1) {
            const value = self.sort_indices[outer];
            const mutation = self.active.paths[value].mutation_version;
            var inner = outer;
            while (inner > 0 and self.active.paths[self.sort_indices[inner - 1]].mutation_version > mutation) {
                self.sort_indices[inner] = self.sort_indices[inner - 1];
                inner -= 1;
            }
            self.sort_indices[inner] = value;
        }
        return count;
    }

    fn writeRegistrationPage(
        self: *const Session,
        input: *const protocol.SnapshotInput,
        outputs: []protocol.RegistrationSnapshot,
    ) PageResult {
        var output_count: u32 = 0;
        var cursor = input.registration_cursor;
        while (cursor < self.active.registrations.len and output_count < input.maximum_registration_count) : (cursor += 1) {
            const registration = &self.active.registrations[cursor];
            if (!registration.occupied) continue;
            const aggregate = self.registrationAggregate(cursor);
            if (aggregate.active_path_count == 0) continue;
            var flags: u32 = 0;
            if (aggregate.identity_confirmed) flags |= protocol.SnapshotFlags.identity_confirmed;
            if (aggregate.requires_root_confirmation) flags |= protocol.SnapshotFlags.requires_root_confirmation;
            if (aggregate.dirty_path_count != 0) flags |= protocol.SnapshotFlags.dirty;
            outputs[output_count] = .{
                .struct_size = @sizeOf(protocol.RegistrationSnapshot),
                .flags = flags,
                .software_handle = registration.software_handle,
                .catalog_entry_handle = registration.catalog_entry_handle,
                .display_name_handle = registration.display_name_handle,
                .software_kind_handle = registration.software_kind_handle,
                .first_observed_utc_ms = aggregate.first_observed_utc_ms,
                .active_path_count = aggregate.active_path_count,
                .confirmed_root_count = aggregate.confirmed_root_count,
                .dirty_path_count = aggregate.dirty_path_count,
                .reserved_u32 = 0,
                .reserved = .{ 0, 0 },
            };
            output_count += 1;
        }
        const has_more = hasRegistrationAtOrAfter(self, cursor);
        return .{
            .output_count = output_count,
            .next_cursor = @intCast(cursor),
            .has_more = has_more,
        };
    }

    fn writePathPage(
        self: *const Session,
        input: *const protocol.SnapshotInput,
        outputs: []protocol.PathSnapshot,
    ) PageResult {
        var output_count: u32 = 0;
        var cursor = input.path_cursor;
        while (cursor < self.active.paths.len and output_count < input.maximum_path_count) : (cursor += 1) {
            const path = &self.active.paths[cursor];
            if (!path.occupied or path.delete_pending) continue;
            const registration = &self.active.registrations[path.registration_index];
            var flags: u32 = 0;
            if (path.identity_confirmed) flags |= protocol.SnapshotFlags.identity_confirmed;
            if (path.root_confirmed) flags |= protocol.SnapshotFlags.root_confirmed;
            if (path.dirty) flags |= protocol.SnapshotFlags.dirty;
            outputs[output_count] = .{
                .struct_size = @sizeOf(protocol.PathSnapshot),
                .flags = flags,
                .mutation_version = path.mutation_version,
                .software_handle = registration.software_handle,
                .path_handle = path.path_handle,
                .root_handle = path.root_handle,
                .first_observed_utc_ms = path.first_observed_utc_ms,
                .executable_path_length = path.executable_path_length,
                .root_path_length = path.root_path_length,
                .reserved = .{ 0, 0, 0 },
            };
            output_count += 1;
        }
        const has_more = hasPathAtOrAfter(self, cursor);
        return .{ .output_count = output_count, .next_cursor = @intCast(cursor), .has_more = has_more };
    }

    fn registrationAggregate(self: *const Session, registration_slot: u32) RegistrationAggregate {
        var result = RegistrationAggregate{};
        for (self.active.paths) |path| {
            if (!path.occupied or path.delete_pending or path.registration_index != registration_slot) continue;
            result.active_path_count += 1;
            if (path.root_confirmed) result.confirmed_root_count += 1 else result.requires_root_confirmation = true;
            if (path.identity_confirmed) result.identity_confirmed = true;
            if (path.dirty) result.dirty_path_count += 1;
            if (result.first_observed_utc_ms == 0 or path.first_observed_utc_ms < result.first_observed_utc_ms) {
                result.first_observed_utc_ms = path.first_observed_utc_ms;
            }
        }
        return result;
    }

    fn snapshotTotals(self: *const Session) SnapshotTotals {
        var result = SnapshotTotals{};
        for (self.active.registrations, 0..) |registration, index| {
            if (!registration.occupied) continue;
            if (self.registrationAggregate(@intCast(index)).active_path_count != 0) result.registration_count += 1;
        }
        for (self.active.paths) |path| {
            if (!path.occupied) continue;
            if (path.dirty) result.dirty_path_count += 1;
            if (!path.delete_pending) result.path_count += 1;
        }
        return result;
    }

    fn advanceRevision(self: *Session) void {
        self.state_revision +%= 1;
        if (self.state_revision == 0) self.state_revision = 1;
    }
};

const PageResult = struct {
    output_count: u32,
    next_cursor: u32,
    has_more: bool,
};

const RegistrationAggregate = struct {
    first_observed_utc_ms: i64 = 0,
    active_path_count: u32 = 0,
    confirmed_root_count: u32 = 0,
    dirty_path_count: u32 = 0,
    identity_confirmed: bool = false,
    requires_root_confirmation: bool = false,
};

const SnapshotTotals = struct {
    registration_count: u32 = 0,
    path_count: u32 = 0,
    dirty_path_count: u32 = 0,
};

fn packedObservationKeys(input: *const protocol.ObserveInput, key_bytes: []const u8) bool {
    if (input.executable_path_offset != 0) return false;
    const root_offset = std.math.add(u32, input.executable_path_offset, input.executable_path_length) catch return false;
    const end = std.math.add(u32, input.root_path_offset, input.root_path_length) catch return false;
    return input.root_path_offset == root_offset and end == key_bytes.len;
}

fn hasRegistrationAtOrAfter(session: *const Session, start: usize) bool {
    var index = start;
    while (index < session.active.registrations.len) : (index += 1) {
        if (session.active.registrations[index].occupied and
            session.registrationAggregate(@intCast(index)).active_path_count != 0) return true;
    }
    return false;
}

fn hasPathAtOrAfter(session: *const Session, start: usize) bool {
    for (session.active.paths[start..]) |path| if (path.occupied and !path.delete_pending) return true;
    return false;
}

fn residentByteCount(config: *const protocol.Config) !u64 {
    var total: u64 = @sizeOf(Session);
    total = try addAllocation(total, config.maximum_path_count, @sizeOf(u32));
    var bank_total: u64 = 0;
    bank_total = try addAllocation(bank_total, config.maximum_registration_count, @sizeOf(bank_module.RegistrationSlot));
    bank_total = try addAllocation(bank_total, config.maximum_path_count, @sizeOf(bank_module.PathSlot));
    bank_total = try addAllocation(bank_total, config.registration_index_capacity, @sizeOf(u32));
    bank_total = try addAllocation(bank_total, config.path_index_capacity, @sizeOf(u32));
    bank_total = try addAllocation(
        bank_total,
        config.maximum_path_count,
        config.maximum_executable_path_byte_count,
    );
    bank_total = try addAllocation(bank_total, config.maximum_path_count, config.maximum_root_path_byte_count);
    return std.math.add(u64, total, try std.math.mul(u64, bank_total, 2));
}

fn addAllocation(total: u64, count: u32, item_size: usize) !u64 {
    return std.math.add(u64, total, try std.math.mul(u64, count, item_size));
}

fn sameCapacities(left: *const protocol.Config, right: *const protocol.Config) bool {
    return left.maximum_registration_count == right.maximum_registration_count and
        left.maximum_path_count == right.maximum_path_count and
        left.maximum_persistence_operation_count == right.maximum_persistence_operation_count and
        left.maximum_registration_snapshot_count == right.maximum_registration_snapshot_count and
        left.maximum_path_snapshot_count == right.maximum_path_snapshot_count and
        left.maximum_executable_path_byte_count == right.maximum_executable_path_byte_count and
        left.maximum_root_path_byte_count == right.maximum_root_path_byte_count and
        left.registration_index_capacity == right.registration_index_capacity and
        left.path_index_capacity == right.path_index_capacity;
}

pub fn lockMutex(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}
