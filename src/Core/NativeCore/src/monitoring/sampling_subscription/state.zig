const std = @import("std");
const protocol = @import("protocol.zig");
const alignment = @import("alignment.zig");
const scratch = @import("scratch.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const no_index: u32 = std.math.maxInt(u32);
const tombstone_index: u32 = no_index - 1;

pub const SourceSlot = struct {
    active: bool = false,
    persistent: bool = false,
    explicit_interval: bool = false,
    sampling_flags: u32 = 0,
    source_handle: u64 = 0,
    last_seen_milliseconds: u64 = 0,
    observed_interval_milliseconds: u64 = 0,
    explicit_interval_milliseconds: u64 = 0,
    item_count: u32 = 0,
};

pub const ItemSlot = struct {
    active: bool = false,
    item_handle: u64 = 0,
    last_sampled_milliseconds: u64 = 0,
    retry_at_milliseconds: u64 = 0,
    last_planned_epoch: u64 = 0,
    scheduled_due_milliseconds: u64 = 0,
    effective_interval_milliseconds: u64 = 0,
};

pub const MembershipSlot = struct {
    active: bool = false,
    source_index: u32 = 0,
    item_index: u32 = 0,
    sampling_flags: u32 = 0,
    period_milliseconds: u64 = 0,
    next_trigger_milliseconds: u64 = 0,
    trigger_sequence: u64 = 0,
    last_planned_epoch: u64 = 0,
};

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.Config,
    sources: []SourceSlot,
    items: []ItemSlot,
    memberships: []MembershipSlot,
    source_index: []u32,
    item_index: []u32,
    scratch_item_handles: []u64,
    scratch_effective_intervals: []u64,
    scratch_source_counts: []u32,
    scratch_sampling_counts: []u32,
    scratch_trigger_due_milliseconds: []u64,
    scratch_issued_triggers: []protocol.TriggerOutput,
    owns_scratch: bool,
    // Owned mode only: the one block behind the five columns, held for a
    // single operation (beginScratch .. endScratch).
    owned_scratch_words: []u64 = &.{},
    issued_trigger_count: u32 = 0,
    issued_trigger_plan_epoch: u64 = 0,
    last_operation_epoch: u64 = 0,
    last_command_at_milliseconds: u64 = 0,
    last_plan_epoch: u64 = 0,
    next_wake_milliseconds: u64 = 0,
    state_revision: u64 = 1,

    pub fn create(config: *const protocol.Config) !*Session {
        return createWithScratch(config, true);
    }

    pub fn createExternalScratch(config: *const protocol.Config) !*Session {
        return createWithScratch(config, false);
    }

    fn createWithScratch(config: *const protocol.Config, owns_scratch: bool) !*Session {
        if (!protocol.validConfig(config)) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);

        // Configured capacities are admission limits only. Sources, items,
        // memberships and both indexes start empty, grow by the rows a Track
        // really registers and shrink back to the live rows after each
        // operation (fitRows).
        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .sources = &.{},
            .items = &.{},
            .memberships = &.{},
            .source_index = &.{},
            .item_index = &.{},
            .scratch_item_handles = &.{},
            .scratch_effective_intervals = &.{},
            .scratch_source_counts = &.{},
            .scratch_sampling_counts = &.{},
            .scratch_trigger_due_milliseconds = &.{},
            .scratch_issued_triggers = &.{},
            .owns_scratch = owns_scratch,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        if (self.owns_scratch) allocator.free(self.owned_scratch_words);
        allocator.free(self.scratch_issued_triggers);
        allocator.free(self.item_index);
        allocator.free(self.source_index);
        allocator.free(self.memberships);
        allocator.free(self.items);
        allocator.free(self.sources);
        allocator.destroy(self);
    }

    /// External mode: binds bytes.len / 32 rows (at most the item admission
    /// limit). The caller lends the rows the next operation needs, read from
    /// workspaceCapacity().item_rows, and unbinds after that operation.
    pub fn bindScratch(self: *Session, bytes: []u8) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (self.owns_scratch or self.scratch_item_handles.len != 0) return .invalid_argument;
        const rows = @min(bytes.len / 32, @as(usize, self.config.maximum_item_count));
        const view = scratch.View.bind(bytes, rows) orelse return .invalid_argument;
        self.setScratchView(view);
        return .ok;
    }

    pub fn unbindScratch(self: *Session) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (self.owns_scratch) return .invalid_argument;
        self.clearScratchView();
        return .ok;
    }

    fn setScratchView(self: *Session, view: scratch.View) void {
        self.scratch_item_handles = view.handles;
        self.scratch_effective_intervals = view.intervals;
        self.scratch_source_counts = view.source_counts;
        self.scratch_sampling_counts = view.sampling_counts;
        self.scratch_trigger_due_milliseconds = view.trigger_due;
    }

    fn clearScratchView(self: *Session) void {
        self.scratch_item_handles = &.{};
        self.scratch_effective_intervals = &.{};
        self.scratch_source_counts = &.{};
        self.scratch_sampling_counts = &.{};
        self.scratch_trigger_due_milliseconds = &.{};
    }

    /// Scratch rows one operation needs: one per allocated item slot, plus the
    /// references a Track may add or a Completion names, never above the item
    /// admission limit.
    pub fn scratchRowsFor(self: *const Session, reference_rows: usize, adds_items: bool) usize {
        const limit: usize = self.config.maximum_item_count;
        const rows = if (adds_items)
            self.items.len +| reference_rows
        else
            @max(self.items.len, reference_rows);
        return @min(rows, limit);
    }

    /// Lends `rows` disposable rows to the operation under the session mutex.
    /// External scratch must already be bound with at least that many rows.
    /// Owned scratch is created at exactly `rows` and released by endScratch,
    /// so neither mode keeps scratch between operations.
    pub fn beginScratch(self: *Session, rows: usize) ResultCode {
        if (!self.owns_scratch)
            return if (self.scratch_item_handles.len >= rows) .ok else .invalid_argument;
        if (rows == 0) return .ok;
        const words = self.allocator.alloc(u64, rows * 4) catch return .out_of_memory;
        const view = scratch.View.bind(std.mem.sliceAsBytes(words), rows) orelse unreachable;
        self.owned_scratch_words = words;
        self.setScratchView(view);
        return .ok;
    }

    pub fn endScratch(self: *Session) void {
        if (!self.owns_scratch) return;
        self.allocator.free(self.owned_scratch_words);
        self.owned_scratch_words = &.{};
        self.clearScratchView();
    }

    pub fn workspaceCapacity(self: *const Session) protocol.WorkspaceCapacity {
        const bytes = @sizeOf(Session) +
            self.sources.len * @sizeOf(SourceSlot) +
            self.items.len * @sizeOf(ItemSlot) +
            self.memberships.len * @sizeOf(MembershipSlot) +
            (self.source_index.len + self.item_index.len) * @sizeOf(u32) +
            self.scratch_issued_triggers.len * @sizeOf(protocol.TriggerOutput) +
            self.owned_scratch_words.len * @sizeOf(u64);
        return .{
            .struct_size = @sizeOf(protocol.WorkspaceCapacity),
            .source_rows = @intCast(self.sources.len),
            .item_rows = @intCast(self.items.len),
            .membership_rows = @intCast(self.memberships.len),
            .active_source_count = self.activeSourceCount(),
            .known_item_count = self.knownItemCount(),
            .issued_trigger_rows = @intCast(self.scratch_issued_triggers.len),
            .scratch_rows = @intCast(self.scratch_item_handles.len),
            .resident_bytes = bytes,
        };
    }

    pub fn reconfigure(self: *Session, config: *const protocol.Config) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        const begun = self.beginScratch(self.scratchRowsFor(0, false));
        if (begun != .ok) return begun;
        defer self.endScratch();
        if (!protocol.validConfig(config)) return .abi_mismatch;
        if (config.generation <= self.config.generation) return .stale_frame;
        if (!sameCapacities(config, &self.config)) return .invalid_argument;
        self.config = config.*;
        for (self.memberships) |*membership| {
            if (!membership.active) continue;
            const source = &self.sources[membership.source_index];
            membership.period_milliseconds = self.effectiveSourceInterval(source);
        }
        _ = self.rebuildItemAggregates();
        self.refreshItemDeadlines(self.last_command_at_milliseconds);
        self.next_wake_milliseconds =
            self.nextWakeFromAggregates(self.last_command_at_milliseconds);
        self.advanceRevision();
        return .ok;
    }

    pub fn reset(self: *Session, input: *const protocol.ControlInput) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validControl(input, &self.config)) return .abi_mismatch;
        if (!self.operationAdvances(input.operation_epoch, input.command_at_milliseconds)) {
            return .stale_frame;
        }
        @memset(self.sources, .{});
        @memset(self.items, .{});
        self.replaceMemberships(&.{}, "reset");
        @memset(self.source_index, no_index);
        @memset(self.item_index, no_index);
        self.replaceIssuedTriggers(&.{}, "reset");
        self.issued_trigger_count = 0;
        self.issued_trigger_plan_epoch = 0;
        self.last_plan_epoch = 0;
        self.next_wake_milliseconds = 0;
        self.fitRows("reset");
        self.commitOperation(input.operation_epoch, input.command_at_milliseconds);
        return .ok;
    }

    pub fn queryWorkspaceCapacity(self: *Session) protocol.WorkspaceCapacity {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        return self.workspaceCapacity();
    }

    pub fn capacity(self: *const Session) protocol.Capacity {
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .source_capacity = self.config.maximum_source_count,
            .item_capacity = self.config.maximum_item_count,
            .membership_capacity = self.config.maximum_membership_count,
            .due_item_capacity = self.config.maximum_due_item_count,
            .source_view_capacity = self.config.maximum_source_view_count,
            .expired_source_capacity = self.config.maximum_expired_source_count,
            .snapshot_source_capacity = self.config.maximum_source_count,
            .snapshot_item_capacity = self.config.maximum_item_count,
            .trigger_capacity = self.config.maximum_membership_count,
            .reserved_u32 = 0,
            .reserved = .{ 0, 0, 0 },
        };
    }

    pub fn operationAdvances(self: *const Session, epoch: u64, command_at: u64) bool {
        return epoch > self.last_operation_epoch and command_at >= self.last_command_at_milliseconds;
    }

    pub fn observedTimeValid(self: *const Session, observed_at: u64, command_at: u64) bool {
        return observed_at <= saturatingAdd(command_at, self.config.maximum_future_skew_milliseconds);
    }

    pub fn commitOperation(self: *Session, epoch: u64, command_at: u64) void {
        self.last_operation_epoch = epoch;
        self.last_command_at_milliseconds = command_at;
        self.advanceRevision();
    }

    pub fn advanceRevision(self: *Session) void {
        self.state_revision +%= 1;
        if (self.state_revision == 0) self.state_revision = 1;
    }

    pub fn effectiveSourceInterval(self: *const Session, source: *const SourceSlot) u64 {
        const requested = if (source.explicit_interval)
            source.explicit_interval_milliseconds
        else if (source.observed_interval_milliseconds != 0)
            source.observed_interval_milliseconds
        else
            self.config.default_interval_milliseconds;
        return @max(requested, self.config.minimum_interval_milliseconds);
    }

    pub fn findSource(self: *const Session, source_handle: u64) ?u32 {
        return findIndex(self.source_index, self.sources, source_handle, sourceHandleAt);
    }

    pub fn findItem(self: *const Session, item_handle: u64) ?u32 {
        return findIndex(self.item_index, self.items, item_handle, itemHandleAt);
    }

    pub fn findMembership(self: *const Session, source_handle: u64, item_handle: u64) ?u32 {
        const source_index = self.findSource(source_handle) orelse return null;
        const item_index = self.findItem(item_handle) orelse return null;
        for (self.memberships, 0..) |membership, index| {
            if (membership.active and membership.source_index == source_index and
                membership.item_index == item_index)
            {
                return @intCast(index);
            }
        }
        return null;
    }

    fn releaseItemAt(self: *Session, index: usize) void {
        removeIndex(self.item_index, self.items, self.items[index].item_handle, itemHandleAt);
        self.items[index] = .{};
    }

    // Count slots the replacement will release before mutating anything, so a
    // rejected Track leaves the old source and every pending plan unchanged.
    fn replacementFreeItemCount(self: *Session, source_index: ?u32, handles: []const u64) usize {
        _ = self.rebuildItemAggregates();
        if (source_index) |replaced_source| {
            for (self.memberships) |membership| {
                if (membership.active and membership.source_index == replaced_source)
                    self.scratch_source_counts[membership.item_index] -= 1;
            }
        }
        var count: usize = 0;
        for (self.items, 0..) |item, index| {
            if (!item.active) {
                count += 1;
                continue;
            }
            if (item.last_planned_epoch != 0 or
                std.mem.indexOfScalar(u64, handles, item.item_handle) != null) continue;
            if (self.scratch_source_counts[index] == 0) count += 1;
        }
        return count;
    }

    pub fn removeSourceAt(self: *Session, source_slot_index: u32) void {
        self.deactivateSourceAt(source_slot_index);
        for (self.memberships) |*membership| {
            if (membership.active and membership.source_index == source_slot_index) {
                membership.* = .{};
            }
        }
    }

    pub fn sourceItemCount(self: *const Session, source_slot_index: u32) u32 {
        return self.sources[source_slot_index].item_count;
    }

    pub fn deactivateSourceAt(self: *Session, source_slot_index: u32) void {
        const handle = self.sources[source_slot_index].source_handle;
        removeIndex(self.source_index, self.sources, handle, sourceHandleAt);
        self.sources[source_slot_index] = .{};
    }

    pub fn clearInactiveSourceMemberships(self: *Session) void {
        for (self.memberships) |*membership| {
            if (membership.active and !self.sources[membership.source_index].active) {
                membership.* = .{};
            }
        }
    }

    pub fn activeSourceCount(self: *const Session) u32 {
        var count: u32 = 0;
        for (self.sources) |source| if (source.active) {
            count += 1;
        };
        return count;
    }

    pub fn knownItemCount(self: *const Session) u32 {
        var count: u32 = 0;
        for (self.items) |item| if (item.active) {
            count += 1;
        };
        return count;
    }

    pub fn membershipCount(self: *const Session) u32 {
        var count: u32 = 0;
        for (self.memberships) |membership| if (membership.active) {
            count += 1;
        };
        return count;
    }

    pub fn rebuildItemAggregates(self: *Session) u32 {
        @memset(self.scratch_effective_intervals, std.math.maxInt(u64));
        @memset(self.scratch_source_counts, 0);
        @memset(self.scratch_sampling_counts, 0);
        for (self.memberships) |membership| {
            if (!membership.active) continue;
            const source = &self.sources[membership.source_index];
            const item = &self.items[membership.item_index];
            if (!source.active or !item.active) continue;
            const interval = self.effectiveSourceInterval(source);
            const item_index: usize = @intCast(membership.item_index);
            self.scratch_source_counts[item_index] += 1;
            if (membership.sampling_flags & protocol.SourceFlags.fresh_on_trigger != 0) continue;
            if (interval < self.scratch_effective_intervals[item_index]) {
                self.scratch_effective_intervals[item_index] = interval;
            }
            self.scratch_sampling_counts[item_index] += 1;
        }

        var active_items: u32 = 0;
        for (self.scratch_source_counts) |source_count| {
            if (source_count != 0) active_items += 1;
        }
        return active_items;
    }

    pub fn nextWakeFromAggregates(self: *const Session, now: u64) u64 {
        var next_wake: u64 = 0;
        for (self.items, 0..) |item, item_index| {
            if (!item.active or self.scratch_source_counts[item_index] == 0) continue;
            if (self.scratch_sampling_counts[item_index] != 0) {
                const interval = self.scratch_effective_intervals[item_index];
                const due_at = alignment.dueAt(
                    item.last_sampled_milliseconds,
                    item.scheduled_due_milliseconds,
                    item.retry_at_milliseconds,
                    interval,
                    now,
                );
                if (next_wake == 0 or due_at < next_wake) next_wake = due_at;
            }
        }
        for (self.memberships) |membership| {
            if (!membership.active or
                membership.sampling_flags & protocol.SourceFlags.periodic_triggers == 0)
            {
                continue;
            }
            const source = &self.sources[membership.source_index];
            const item = &self.items[membership.item_index];
            if (!source.active or !item.active) continue;
            const due_at = membership.next_trigger_milliseconds;
            if (due_at != 0 and (next_wake == 0 or due_at < next_wake)) next_wake = due_at;
        }
        return next_wake;
    }

    pub fn refreshItemDeadlines(self: *Session, command_at_milliseconds: u64) void {
        for (self.items, 0..) |*item, item_index| {
            if (!item.active) continue;
            if (self.scratch_source_counts[item_index] == 0) {
                if (item.last_planned_epoch == 0) {
                    self.releaseItemAt(item_index);
                    continue;
                }
                item.retry_at_milliseconds = 0;
                item.scheduled_due_milliseconds = 0;
                item.effective_interval_milliseconds = 0;
                continue;
            }

            if (self.scratch_sampling_counts[item_index] == 0) {
                item.retry_at_milliseconds = 0;
                item.scheduled_due_milliseconds = 0;
                item.effective_interval_milliseconds = 0;
                continue;
            }

            const interval = self.scratch_effective_intervals[item_index];
            const interval_changed = item.effective_interval_milliseconds != interval;
            if (interval_changed) {
                item.retry_at_milliseconds = if (item.effective_interval_milliseconds != 0 and
                    item.retry_at_milliseconds != 0)
                    alignment.nextDue(command_at_milliseconds, interval)
                else
                    0;
                item.effective_interval_milliseconds = interval;
            }
            if (item.last_sampled_milliseconds == 0) {
                if (item.last_planned_epoch == 0) item.scheduled_due_milliseconds = 0;
                continue;
            }
            // The item lost its sampling demand while its plan was in flight and has demand again.
            // That plan's completion is the item's next own completion; leaving the deadline unset
            // lets completion schedule from it instead of from the sample it is replacing.
            if (item.scheduled_due_milliseconds == 0 and item.last_planned_epoch != 0) continue;
            const next_due = alignment.nextDue(
                item.last_sampled_milliseconds,
                interval,
            );
            if (item.scheduled_due_milliseconds == 0 or
                (interval_changed and item.scheduled_due_milliseconds > command_at_milliseconds))
            {
                item.scheduled_due_milliseconds = next_due;
            }
        }
    }

    pub fn track(
        self: *Session,
        input: *const protocol.TrackInput,
        references: []const protocol.ItemReference,
    ) ResultCode {
        if (!protocol.validTrackHeader(input, &self.config)) return .abi_mismatch;
        if (references.len != input.item_count) return .invalid_argument;
        if (references.len > self.config.maximum_item_count) return .sampling_item_capacity_exceeded;
        if (references.len > self.config.maximum_membership_count) return .sampling_membership_capacity_exceeded;
        if (!self.operationAdvances(input.operation_epoch, input.command_at_milliseconds)) {
            return .stale_frame;
        }
        if (!self.observedTimeValid(input.observed_at_milliseconds, input.command_at_milliseconds)) {
            return .stale_frame;
        }

        for (references, 0..) |reference, index| {
            if (!protocol.validItemReference(&reference)) return .abi_mismatch;
            self.scratch_item_handles[index] = reference.item_handle;
        }
        const handles = self.scratch_item_handles[0..references.len];
        std.sort.pdq(u64, handles, {}, lessThanU64);
        for (handles, 0..) |handle, index| {
            if (index != 0 and handles[index - 1] == handle) return .invalid_argument;
        }

        const existing_source_index = self.findSource(input.source_handle);
        if (existing_source_index) |source_index| {
            if (input.observed_at_milliseconds < self.sources[source_index].last_seen_milliseconds) {
                return .stale_frame;
            }
        }

        var new_item_count: usize = 0;
        for (handles) |handle| if (self.findItem(handle) == null) {
            new_item_count += 1;
        };
        // Free item rows are the slots this replacement releases plus the
        // rows still below the admission limit; only the shortfall is grown.
        const reusable_item_rows = self.replacementFreeItemCount(existing_source_index, handles);
        const item_headroom = @as(usize, self.config.maximum_item_count) - self.items.len;
        if (new_item_count > reusable_item_rows + item_headroom) return .sampling_item_capacity_exceeded;
        const free_source_rows = self.freeSourceCount();
        const source_headroom = @as(usize, self.config.maximum_source_count) - self.sources.len;
        if (existing_source_index == null and free_source_rows + source_headroom == 0) return .sampling_source_capacity_exceeded;
        const grown_item_rows = self.items.len + (new_item_count -| reusable_item_rows);
        const grown_source_rows = self.sources.len +
            @intFromBool(existing_source_index == null and free_source_rows == 0);
        const reusable_memberships = if (existing_source_index) |source_index|
            self.sourceItemCount(source_index)
        else
            0;
        const required_memberships = self.membershipCount() - reusable_memberships + references.len;
        if (required_memberships > self.config.maximum_membership_count) return .sampling_membership_capacity_exceeded;
        // Prepare the replacement source and exact membership rows before any
        // authoritative state changes. Membership indices never leave this module.
        var updated_source = if (existing_source_index) |index| self.sources[index] else SourceSlot{};
        const source = &updated_source;
        const previous_last_seen = source.last_seen_milliseconds;
        const previous_sampling_flags = source.sampling_flags;
        const previous_effective_interval = if (source.active)
            self.effectiveSourceInterval(source)
        else
            0;
        const elapsed = input.observed_at_milliseconds - previous_last_seen;
        if (previous_last_seen != 0 and elapsed != 0 and elapsed < self.config.active_ttl_milliseconds) {
            source.observed_interval_milliseconds = if (source.observed_interval_milliseconds == 0)
                elapsed
            else
                smoothedInterval(source.observed_interval_milliseconds, elapsed);
        }
        source.active = true;
        source.persistent = input.flags & protocol.SourceFlags.persistent != 0;
        source.explicit_interval = input.flags & protocol.SourceFlags.explicit_interval != 0;
        source.sampling_flags = input.flags & protocol.SourceFlags.trigger_modes;
        source.source_handle = input.source_handle;
        source.last_seen_milliseconds = input.observed_at_milliseconds;
        source.explicit_interval_milliseconds = input.explicit_interval_milliseconds;
        source.item_count = @intCast(handles.len);
        const period = self.effectiveSourceInterval(source);
        const sampling_flags_changed = previous_sampling_flags != source.sampling_flags or
            (previous_effective_interval != 0 and previous_effective_interval != period);

        var membership_candidate: ?[]MembershipSlot = null;
        if (required_memberships != self.memberships.len) {
            const candidate = self.allocator.alloc(MembershipSlot, required_memberships) catch return .out_of_memory;
            @memset(candidate, .{});
            var retained_count: usize = 0;
            for (self.memberships) |membership| {
                if (!membership.active) continue;
                if (existing_source_index != null and membership.source_index == existing_source_index.?) {
                    const handle = self.items[membership.item_index].item_handle;
                    if (sampling_flags_changed or membership.period_milliseconds != period or
                        membership.sampling_flags != source.sampling_flags or
                        std.mem.indexOfScalar(u64, handles, handle) == null) continue;
                }
                candidate[retained_count] = membership;
                retained_count += 1;
            }
            membership_candidate = candidate;
        }
        // Growth keeps every slot position, so the candidate rows stay valid.
        if (!self.growRows(grown_source_rows, grown_item_rows)) {
            if (membership_candidate) |candidate| self.allocator.free(candidate);
            return .out_of_memory;
        }
        if (membership_candidate) |candidate| self.replaceMemberships(candidate, "track-demand");
        const source_index = existing_source_index orelse self.allocateSource(input.source_handle) orelse unreachable;
        self.sources[source_index] = updated_source;

        for (self.memberships) |*membership| {
            if (!membership.active or membership.source_index != source_index) continue;
            const old_item_handle = self.items[membership.item_index].item_handle;
            const retained = std.mem.indexOfScalar(u64, handles, old_item_handle) != null;
            if (!retained or sampling_flags_changed or
                membership.period_milliseconds != period or
                membership.sampling_flags != source.sampling_flags)
            {
                membership.* = .{};
            }
        }

        _ = self.rebuildItemAggregates();
        for (self.items, 0..) |item, index| {
            if (item.active and self.scratch_source_counts[index] == 0 and
                item.last_planned_epoch == 0 and
                std.mem.indexOfScalar(u64, handles, item.item_handle) == null)
            {
                self.releaseItemAt(index);
            }
        }

        for (handles) |handle| {
            const item_index = self.findItem(handle) orelse
                self.allocateItem(handle) orelse unreachable;
            var existing_membership: ?*MembershipSlot = null;
            for (self.memberships) |*membership| {
                if (membership.active and membership.source_index == source_index and
                    membership.item_index == item_index)
                {
                    existing_membership = membership;
                    break;
                }
            }
            if (existing_membership != null) continue;
            const membership = self.allocateMembership() orelse unreachable;
            // Resolve the existing phase before writing the new slot: evaluating
            // this during the struct assignment can expose this slot to the scan.
            const next_trigger = if (source.sampling_flags & protocol.SourceFlags.periodic_triggers != 0)
                self.initialTriggerDue(item_index, source.sampling_flags, period, input.command_at_milliseconds)
            else
                0;
            membership.* = .{
                .active = true,
                .source_index = source_index,
                .item_index = item_index,
                .sampling_flags = source.sampling_flags,
                .period_milliseconds = period,
                .next_trigger_milliseconds = next_trigger,
                .trigger_sequence = if (source.sampling_flags & protocol.SourceFlags.periodic_triggers != 0) 1 else 0,
            };
        }

        _ = self.rebuildItemAggregates();
        self.refreshItemDeadlines(input.command_at_milliseconds);
        self.next_wake_milliseconds = self.nextWakeFromAggregates(input.command_at_milliseconds);
        self.fitRows("track");
        self.commitOperation(input.operation_epoch, input.command_at_milliseconds);
        return .ok;
    }

    fn initialTriggerDue(
        self: *const Session,
        item_index: u32,
        sampling_flags: u32,
        period_milliseconds: u64,
        registered_at_milliseconds: u64,
    ) u64 {
        if (sampling_flags & protocol.SourceFlags.align_same_item_phase != 0) {
            var earliest_boundary: u64 = 0;
            for (self.memberships) |membership| {
                if (!membership.active or membership.item_index != item_index or
                    membership.sampling_flags & protocol.SourceFlags.periodic_triggers == 0 or
                    membership.period_milliseconds == 0)
                {
                    continue;
                }
                const boundary = boundaryAfter(
                    membership.next_trigger_milliseconds,
                    membership.period_milliseconds,
                    registered_at_milliseconds,
                );
                if (earliest_boundary == 0 or boundary < earliest_boundary) {
                    earliest_boundary = boundary;
                }
            }
            if (earliest_boundary != 0) return earliest_boundary;
        }
        return saturatingAdd(registered_at_milliseconds, period_milliseconds);
    }

    fn boundaryAfter(boundary: u64, period: u64, timestamp: u64) u64 {
        if (boundary > timestamp) return boundary;
        const steps: u128 = @as(u128, (timestamp - boundary) / period) + 1;
        const next: u128 = @as(u128, boundary) + steps * period;
        return if (next > std.math.maxInt(u64)) std.math.maxInt(u64) else @intCast(next);
    }

    pub fn remove(self: *Session, input: *const protocol.RemoveInput) ResultCode {
        if (!protocol.validRemove(input, &self.config)) return .abi_mismatch;
        if (!self.operationAdvances(input.operation_epoch, input.command_at_milliseconds)) {
            return .stale_frame;
        }
        const source_index = self.findSource(input.source_handle) orelse return .no_data;
        const required = self.memberships.len - self.sourceItemCount(source_index);
        const candidate = self.allocator.alloc(MembershipSlot, required) catch return .out_of_memory;
        var retained: usize = 0;
        for (self.memberships) |membership| {
            if (!membership.active or membership.source_index == source_index) continue;
            candidate[retained] = membership;
            retained += 1;
        }
        self.replaceMemberships(candidate, "source-remove");
        self.removeSourceAt(source_index);
        _ = self.rebuildItemAggregates();
        self.refreshItemDeadlines(input.command_at_milliseconds);
        self.next_wake_milliseconds = self.nextWakeFromAggregates(input.command_at_milliseconds);
        self.fitRows("source-remove");
        self.commitOperation(input.operation_epoch, input.command_at_milliseconds);
        return .ok;
    }

    fn freeSourceCount(self: *const Session) usize {
        var count: usize = 0;
        for (self.sources) |source| if (!source.active) {
            count += 1;
        };
        return count;
    }

    pub fn replaceMemberships(self: *Session, candidate: []MembershipSlot, reason: []const u8) void {
        const previous = self.memberships.len;
        self.allocator.free(self.memberships);
        self.memberships = candidate;
        if (previous != candidate.len) std.log.defaultLog(.info, .sampling_subscription, "workspace resize column=memberships old_rows={d} new_rows={d} old_bytes={d} new_bytes={d} reason={s}", .{ previous, candidate.len, previous * @sizeOf(MembershipSlot), candidate.len * @sizeOf(MembershipSlot), reason });
    }

    pub fn replaceIssuedTriggers(self: *Session, candidate: []protocol.TriggerOutput, reason: []const u8) void {
        const previous = self.scratch_issued_triggers.len;
        self.allocator.free(self.scratch_issued_triggers);
        self.scratch_issued_triggers = candidate;
        if (previous != candidate.len) std.log.defaultLog(.info, .sampling_subscription, "workspace resize column=issued-triggers old_rows={d} new_rows={d} old_bytes={d} new_bytes={d} reason={s}", .{ previous, candidate.len, previous * @sizeOf(protocol.TriggerOutput), candidate.len * @sizeOf(protocol.TriggerOutput), reason });
    }

    /// Grows sources and items to at least these rows. Every slot keeps its
    /// position, so membership slot numbers stay valid. All storage is
    /// allocated before anything is published; failure changes nothing.
    fn growRows(self: *Session, source_rows: usize, item_rows: usize) bool {
        if (source_rows <= self.sources.len and item_rows <= self.items.len) return true;
        return self.replaceRows(
            @max(source_rows, self.sources.len),
            @max(item_rows, self.items.len),
            false,
            "track-demand",
        );
    }

    /// Shrinks sources, items and both indexes to the live rows at the end of
    /// an operation. Live slots keep their relative order and memberships
    /// follow them; every output is ordered by handle, never by slot.
    /// Allocation failure keeps the current rows, which stay valid.
    pub fn fitRows(self: *Session, reason: []const u8) void {
        const live_sources: usize = self.activeSourceCount();
        const live_items: usize = self.knownItemCount();
        if (live_sources == self.sources.len and live_items == self.items.len) return;
        _ = self.replaceRows(live_sources, live_items, true, reason);
    }

    fn replaceRows(
        self: *Session,
        source_rows: usize,
        item_rows: usize,
        compact: bool,
        reason: []const u8,
    ) bool {
        const allocator = self.allocator;
        const sources = allocator.alloc(SourceSlot, source_rows) catch return false;
        const items = allocator.alloc(ItemSlot, item_rows) catch {
            allocator.free(sources);
            return false;
        };
        const source_index = allocator.alloc(u32, indexRows(source_rows)) catch {
            allocator.free(items);
            allocator.free(sources);
            return false;
        };
        const item_index = allocator.alloc(u32, indexRows(item_rows)) catch {
            allocator.free(source_index);
            allocator.free(items);
            allocator.free(sources);
            return false;
        };
        @memset(sources, .{});
        @memset(items, .{});
        @memset(source_index, no_index);
        @memset(item_index, no_index);

        var next_source: usize = 0;
        for (self.sources, 0..) |source, old| {
            if (!source.active) continue;
            const slot = if (compact) next_source else old;
            sources[slot] = source;
            if (!insertIndex(source_index, sources, source.source_handle, @intCast(slot), sourceHandleAt)) unreachable;
            next_source += 1;
        }
        var next_item: usize = 0;
        for (self.items, 0..) |item, old| {
            if (!item.active) continue;
            const slot = if (compact) next_item else old;
            items[slot] = item;
            if (!insertIndex(item_index, items, item.item_handle, @intCast(slot), itemHandleAt)) unreachable;
            next_item += 1;
        }
        if (compact) {
            // The new indexes translate each membership's old slots by handle.
            for (self.memberships) |*membership| {
                if (!membership.active) continue;
                const old_source = &self.sources[membership.source_index];
                const old_item = &self.items[membership.item_index];
                // A membership whose slot was released cannot follow it; the
                // slot would otherwise be reused by another handle.
                if (!old_source.active or !old_item.active) {
                    membership.* = .{};
                    continue;
                }
                membership.source_index = findIndex(source_index, sources, old_source.source_handle, sourceHandleAt) orelse unreachable;
                membership.item_index = findIndex(item_index, items, old_item.item_handle, itemHandleAt) orelse unreachable;
            }
        }

        const previous_sources = self.sources.len;
        const previous_items = self.items.len;
        allocator.free(self.item_index);
        allocator.free(self.source_index);
        allocator.free(self.items);
        allocator.free(self.sources);
        self.sources = sources;
        self.items = items;
        self.source_index = source_index;
        self.item_index = item_index;
        if (previous_sources != source_rows) std.log.defaultLog(.info, .sampling_subscription, "workspace resize column=sources old_rows={d} new_rows={d} old_bytes={d} new_bytes={d} reason={s}", .{ previous_sources, source_rows, previous_sources * @sizeOf(SourceSlot), source_rows * @sizeOf(SourceSlot), reason });
        if (previous_items != item_rows) std.log.defaultLog(.info, .sampling_subscription, "workspace resize column=items old_rows={d} new_rows={d} old_bytes={d} new_bytes={d} reason={s}", .{ previous_items, item_rows, previous_items * @sizeOf(ItemSlot), item_rows * @sizeOf(ItemSlot), reason });
        return true;
    }

    fn allocateSource(self: *Session, handle: u64) ?u32 {
        for (self.sources, 0..) |*source, index| {
            if (source.active) continue;
            source.* = .{ .active = true, .source_handle = handle };
            if (!insertIndex(self.source_index, self.sources, handle, @intCast(index), sourceHandleAt)) {
                source.* = .{};
                return null;
            }
            return @intCast(index);
        }
        return null;
    }

    fn allocateItem(self: *Session, handle: u64) ?u32 {
        for (self.items, 0..) |*item, index| {
            if (item.active) continue;
            item.* = .{ .active = true, .item_handle = handle };
            if (!insertIndex(self.item_index, self.items, handle, @intCast(index), itemHandleAt)) {
                item.* = .{};
                return null;
            }
            return @intCast(index);
        }
        return null;
    }

    fn allocateMembership(self: *Session) ?*MembershipSlot {
        for (self.memberships) |*membership| {
            if (!membership.active) return membership;
        }
        return null;
    }
};

fn sameCapacities(left: *const protocol.Config, right: *const protocol.Config) bool {
    return left.maximum_source_count == right.maximum_source_count and
        left.maximum_item_count == right.maximum_item_count and
        left.maximum_membership_count == right.maximum_membership_count and
        left.maximum_due_item_count == right.maximum_due_item_count and
        left.maximum_source_view_count == right.maximum_source_view_count and
        left.maximum_expired_source_count == right.maximum_expired_source_count;
}

fn sourceHandleAt(slots: []const SourceSlot, index: u32) u64 {
    return slots[index].source_handle;
}

fn itemHandleAt(slots: []const ItemSlot, index: u32) u64 {
    return slots[index].item_handle;
}

fn findIndex(
    index: []const u32,
    slots: anytype,
    handle: u64,
    comptime handleAt: anytype,
) ?u32 {
    if (index.len == 0) return null;
    const mask = index.len - 1;
    var probe = hashHandle(handle) & mask;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        const slot_index = index[probe];
        if (slot_index == no_index) return null;
        if (slot_index != tombstone_index and handleAt(slots, slot_index) == handle) return slot_index;
        probe = (probe + 1) & mask;
    }
    return null;
}

fn insertIndex(
    index: []u32,
    slots: anytype,
    handle: u64,
    slot_index: u32,
    comptime handleAt: anytype,
) bool {
    if (index.len == 0) return false;
    const mask = index.len - 1;
    var probe = hashHandle(handle) & mask;
    var first_tombstone: ?usize = null;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        const existing = index[probe];
        if (existing == tombstone_index) {
            if (first_tombstone == null) first_tombstone = probe;
        } else if (existing == no_index) {
            index[first_tombstone orelse probe] = slot_index;
            return true;
        } else if (handleAt(slots, existing) == handle) {
            return false;
        }
        probe = (probe + 1) & mask;
    }
    if (first_tombstone) |tombstone| {
        index[tombstone] = slot_index;
        return true;
    }
    return false;
}

fn removeIndex(
    index: []u32,
    slots: anytype,
    handle: u64,
    comptime handleAt: anytype,
) void {
    if (index.len == 0) return;
    const mask = index.len - 1;
    var probe = hashHandle(handle) & mask;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        const slot_index = index[probe];
        if (slot_index == no_index) return;
        if (slot_index != tombstone_index and handleAt(slots, slot_index) == handle) {
            backwardShiftDelete(index, slots, probe, handleAt);
            return;
        }
        probe = (probe + 1) & mask;
    }
}

fn backwardShiftDelete(
    index: []u32,
    slots: anytype,
    deleted_index: usize,
    comptime handleAt: anytype,
) void {
    const mask = index.len - 1;
    var hole = deleted_index;
    index[hole] = no_index;
    var scan = (hole + 1) & mask;
    while (index[scan] != no_index) : (scan = (scan + 1) & mask) {
        if (index[scan] == tombstone_index) continue;
        const home = hashHandle(handleAt(slots, index[scan])) & mask;
        if (probeDistance(home, hole, mask) >= probeDistance(home, scan, mask)) continue;
        index[hole] = index[scan];
        index[scan] = no_index;
        hole = scan;
    }
}

fn probeDistance(home: usize, position: usize, mask: usize) usize {
    return (position -% home) & mask;
}

fn hashHandle(handle: u64) usize {
    var value = handle;
    value ^= value >> 33;
    value *%= 0xff51_afd7_ed55_8ccd;
    value ^= value >> 33;
    value *%= 0xc4ce_b9fe_1a85_ec53;
    value ^= value >> 33;
    return @truncate(value);
}

// Open addressing at most half full, sized by the rows really held; no rows
// hold no index at all.
fn indexRows(rows: usize) usize {
    if (rows == 0) return 0;
    return std.math.ceilPowerOfTwo(usize, rows * 2) catch unreachable;
}

fn smoothedInterval(previous: u64, elapsed: u64) u64 {
    const numerator: u128 = @as(u128, previous) * 3 + elapsed;
    return @intCast(numerator / 4);
}

pub fn saturatingAdd(left: u64, right: u64) u64 {
    return std.math.add(u64, left, right) catch std.math.maxInt(u64);
}

fn lessThanU64(_: void, left: u64, right: u64) bool {
    return left < right;
}

pub fn lockMutex(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}

test "sampling source index deletion compacts probes without historical tombstones" {
    var index = @as([64]u32, @splat(no_index));
    var sources = @as([1]SourceSlot, @splat(.{}));
    var ordinal: u64 = 1;
    while (ordinal <= 4_096) : (ordinal += 1) {
        sources[0] = .{ .active = true, .source_handle = ordinal };
        try std.testing.expect(insertIndex(&index, &sources, ordinal, 0, sourceHandleAt));
        try std.testing.expectEqual(@as(?u32, 0), findIndex(&index, &sources, ordinal, sourceHandleAt));
        removeIndex(&index, &sources, ordinal, sourceHandleAt);
        try std.testing.expectEqual(@as(?u32, null), findIndex(&index, &sources, ordinal, sourceHandleAt));
        sources[0] = .{};
    }

    for (index) |slot_index| try std.testing.expectEqual(no_index, slot_index);
}
