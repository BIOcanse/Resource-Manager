//! Composed owned-memory ledger. This is a D1/D3 candidate, not a published ABI.
//! All metadata and per-call workspace are explicit, stable caller-owned arrays.
const std = @import("std");
const uid_module = @import("../uid.zig");
const lifecycle = @import("../memory_resource.zig");
const window = @import("../activity_window.zig");
const region = @import("../memory_region.zig");
const release = @import("../release_check.zig");
const types = @import("types.zig");
const synchronization = @import("sync.zig");
const notifications = @import("notifications.zig");
const tree_operations = @import("tree.zig");
const partition_planning = @import("partition_planning.zig");
const auditing = @import("audit.zig");
const destroying = @import("destruction.zig");
const allocation_statistics = @import("allocation_statistics.zig");
const lease_analysis_module = @import("lease_analysis.zig");
const use_cache = @import("window_use_cache.zig");
const builtin = @import("builtin");

pub fn Owner(comptime config: types.Config, comptime declarations: []const types.Table, comptime fixed: []const types.For(uid_module.Identifier(config.uid_width)).Fixed, comptime Index: type) type {
    return WithRegion(config, declarations, fixed, Index, region.Region);
}

/// Compile-time descriptor selection; no runtime allocator or failure policy.
pub fn WithRegion(comptime config: types.Config, comptime declarations: []const types.Table, comptime fixed: []const types.For(uid_module.Identifier(config.uid_width)).Fixed, comptime Index: type, comptime Region: type) type {
    if (config.use_window_cache and (config.use_window_cache_slots == 0 or config.use_window_cache_slots > 65536))
        @compileError("window cache slots must be 1..65536");
    // A compiler-rt 16-byte atomic fallback uses a process-global lock table.
    // Never silently turn the shared hit path into that fallback. Baseline and
    // other targets keep the unchanged false-cache path; primary owns target flags.
    if (config.use_window_cache and (builtin.cpu.arch != .x86_64 or !builtin.cpu.has(.x86, .cx16)))
        @compileError("window cache requires native 128-bit atomics: x86_64 + cx16");
    if (config.development_lease_analysis_capacity == 0 or config.development_lease_analysis_capacity > 65536)
        @compileError("lease analysis capacity must be 1..65536");
    const Uid = uid_module.Identifier(config.uid_width);
    const T = types.For(Uid);
    const Activity = window.Window(config.activity_width);
    const Resource = lifecycle.WithRegion(.{ .width = config.activity_width, .score_epochs = config.activity_width, .idle_epochs = config.activity_width, .development_write_watch = config.development_lease_analysis }, Region);
    const Notifier = notifications.Notifier(Uid);
    const Planning = partition_planning.For(Activity.Bitmap);
    const counts = comptime blk: {
        if (declarations.len == 0 or declarations.len > 64) @compileError("declare 1..64 tables");
        var r: usize = 0;
        var p: usize = 0;
        for (declarations) |decl| {
            if (decl.capacity == 0 or !decl.policy.valid(config.activity_width) or
                @as(u64, decl.reserved_start) + decl.reserved_count > decl.capacity)
                @compileError("invalid table declaration");
            r += decl.capacity;
            p += decl.partition_capacity;
        }
        for (fixed) |f| {
            if (f.table >= declarations.len) @compileError("fixed table out of range");
            if (f.parent == null) {
                const table = declarations[f.table];
                if (f.ordinal >= table.capacity or (f.ordinal >= table.reserved_start and
                    f.ordinal < @as(u64, table.reserved_start) + table.reserved_count))
                    @compileError("table fixed slot overlaps reserved range or exceeds capacity");
            }
        }
        break :blk .{ .resources = r, .partitions = p };
    };
    return struct {
        const Self = @This();
        pub const uid_type = Uid;
        pub const api = T;
        pub const resource_capacity = counts.resources;
        pub const partition_capacity = counts.partitions;
        pub const table_count = declarations.len;
        pub const development_statistics_enabled = config.development_allocation_statistics;
        pub const development_lease_analysis_enabled = config.development_lease_analysis;
        pub const LeaseAnalysisRecord = types.LeaseAnalysisRecord;
        pub const LeaseAnalysisSummary = types.LeaseAnalysisSummary;
        pub const table_declarations = declarations;
        pub const fixed_declarations = fixed;
        pub const window_cache_enabled = config.use_window_cache;
        pub const PartitionPlanning = Planning;
        pub const Audit = auditing.For(Self);
        pub const Destruction = destroying.For(Self);
        pub const FixedBinding = struct {
            slot: std.atomic.Value(u32) = std.atomic.Value(u32).init(types.no_slot),
            parent_number: std.atomic.Value(u64) = std.atomic.Value(u64).init(0),
        };
        pub const Row = struct {
            lock: if (config.use_window_cache) synchronization.SharedLock else synchronization.Lock = .{},
            cache: if (config.use_window_cache) use_cache.Entry else void = if (config.use_window_cache) .{} else {},
            uid: ?Uid = null,
            table: u32,
            parent: ?u32 = null,
            fixed_declaration: ?u32 = null,
            fixed_parent_number: u64 = 0,
            attributes: types.Attributes,
            resource: Resource,
            allocation_birth: if (config.development_allocation_statistics) allocation_statistics.Birth else void = if (config.development_allocation_statistics) .{} else {},
        };
        pub const Shell = struct {
            uid: ?Uid = null,
            table: u32,
            parent: ?u32 = null,
            start: u32 = 0,
            capacity: u32 = 0,
            allocation_number: u64 = 0,
            attributes: types.Attributes,
            history: Activity.History = .{},
            first_child: u32 = types.no_slot,
            next_sibling: u32 = types.no_slot,
        };
        pub const TableState = struct {
            write: synchronization.Lock = .{},
            cache_head: if (config.use_window_cache) u32 else void = if (config.use_window_cache) types.no_slot else {},
            mode: types.Mode = .normal,
            concentrated: bool = false,
            last_smooth_epoch: u64 = 0,
            registered_bytes: u64 = 0,
            system_page_bytes: u64 = 0,
            pending_discards: u32 = 0,
            /// Table-relative row where Tick resumes pending-discard cleanup:
            /// just after the row it tried last, so rows that keep failing
            /// cannot take the whole budget of every Tick.
            pending_cursor: u32 = 0,
            first_partition: u32 = types.no_slot,
            free_partition: u32 = types.no_slot,
            notifier: Notifier,
            allocation_statistics: if (config.development_allocation_statistics) allocation_statistics.Counters else void = if (config.development_allocation_statistics) .{} else {},
        };
        pub const Candidate = struct { slot: u32, decision_bytes: u64, snapshot: Snapshot };
        pub const Workspace = struct {
            candidates: []Candidate,
            rows: []Candidate,
            shells: []u32,
            homes: []u32,
            notes: []T.Notification,
            remove_ids: []u32 = &.{},
            puts: []Index.Transactions.Put = &.{},
            index_workspace: Index.Transactions.Workspace = .{},
            cells: []Planning.Cell = &.{},
            children: []Planning.Child = &.{},
            child_slots: []u32 = &.{},
            numbers: []u64 = &.{},
            merge_a: []u64 = &.{},
            merge_b: []u64 = &.{},
            best_numbers: []u64 = &.{},
            blocked_slot: ?u32 = null,
            note_count: usize = 0,
            pub fn reset(self: *@This()) void {
                self.note_count = 0;
            }
        };

        rows: []Row,
        shells: []Shell,
        tables: []TableState,
        bindings: []FixedBinding,
        index: Index,
        numbers: synchronization.Counter = .{},
        cache_hints: if (config.use_window_cache) use_cache.Hints(config.use_window_cache_slots) else void = if (config.use_window_cache) .{} else {},
        cache_diagnostics: if (config.use_window_cache and builtin.is_test) CacheCounters else void = if (config.use_window_cache and builtin.is_test) .{} else {},
        epoch: std.atomic.Value(u64) = std.atomic.Value(u64).init(1),
        lease_analysis: if (config.development_lease_analysis) lease_analysis_module.StorageWithWriteWatch(config.development_lease_analysis_capacity, config.development_lease_analysis_write_watch_pages) else void = if (config.development_lease_analysis) .{} else {},

        /// Initializes the owner in its final caller-owned block. Analysis records,
        /// write-watch scratch and cache hints can reach many MiB at their allowed
        /// maxima, so the owner is never built as a stack value and returned.
        pub fn init(self: *Self, rows: []Row, shells: []Shell, tables: []TableState, bindings: []FixedBinding, nodes: []Index.Node, homes: []Index.Home, subscribers: []const []Notifier.Record) !void {
            if (rows.len != counts.resources or shells.len != counts.partitions or
                tables.len != declarations.len or bindings.len != fixed.len or
                nodes.len != rows.len + shells.len or homes.len != config.index_buckets or
                subscribers.len != tables.len) return error.InvalidArgument;
            self.rows = rows;
            self.shells = shells;
            self.tables = tables;
            self.bindings = bindings;
            self.index = Index.init(nodes, homes);
            self.numbers = .{};
            if (comptime config.use_window_cache) {
                for (&self.cache_hints.slots) |*hint| hint.* = std.atomic.Value(u32).init(types.no_slot);
            }
            if (comptime config.use_window_cache and builtin.is_test) self.cache_diagnostics = .{};
            self.epoch = std.atomic.Value(u64).init(1);
            if (comptime config.development_lease_analysis) self.lease_analysis.init();
            for (bindings) |*binding| binding.* = .{};
            for (declarations, 0..) |decl, t| {
                tables[t] = .{ .notifier = try Notifier.init(subscribers[t]), .free_partition = if (decl.partition_capacity == 0) types.no_slot else @intCast(partitionStart(t)) };
                for (rows[resourceStart(t)..][0..decl.capacity]) |*row| {
                    row.* = .{ .table = @intCast(t), .attributes = decl.attributes, .resource = try Resource.dynamic(0) };
                    row.resource.registration = null;
                }
                for (shells[partitionStart(t)..][0..decl.partition_capacity], partitionStart(t)..) |*shell, p|
                    shell.* = .{ .table = @intCast(t), .attributes = decl.attributes, .next_sibling = if (p + 1 == partitionStart(t) + decl.partition_capacity) types.no_slot else @intCast(p + 1) };
            }
            for (fixed, 0..) |f, i| if (f.parent == null)
                try self.registerFixed(@intCast(i), @intCast(resourceStart(f.table) + f.ordinal), null);
        }

        pub fn resourceStart(table: usize) usize {
            var start: usize = 0;
            for (declarations[0..table]) |decl| start += decl.capacity;
            return start;
        }
        pub fn audit(self: *Self, work: *Audit.Workspace) !Audit.Stats {
            return Audit.run(self, work);
        }
        pub fn destroy(self: *Self, forced: bool, work: *Destruction.Workspace) !void {
            return Destruction.run(self, forced, work);
        }
        pub fn partitionStart(table: usize) usize {
            var start: usize = 0;
            for (declarations[0..table]) |decl| start += decl.partition_capacity;
            return start;
        }
        pub fn registerFixed(self: *Self, declaration: u32, slot: u32, parent: ?u32) !void {
            const f = fixed[declaration];
            const row = &self.rows[slot];
            const attrs = f.attributes orelse if (parent) |p| self.shells[p].attributes else declarations[f.table].attributes;
            const next_total = try addBytes(self.tables[f.table].registered_bytes, f.allocation_bytes);
            const value = try Resource.fixed(f.allocation_bytes);
            try self.index.insert(slot, f.uid, .{ .kind = .resource, .table = f.table, .slot = slot });
            row.uid = f.uid;
            row.fixed_declaration = declaration;
            row.parent = parent;
            row.fixed_parent_number = if (parent) |p| self.shells[p].allocation_number else 0;
            row.attributes = attrs;
            row.resource = value;
            self.tables[f.table].registered_bytes = next_total;
            self.bindings[declaration].parent_number.store(row.fixed_parent_number, .release);
            self.bindings[declaration].slot.store(slot, .release);
        }

        pub fn use(self: *Self, uid: Uid) !T.UseResult {
            if (comptime config.use_window_cache) {
                const hint = &self.cache_hints.slots[use_cache.Hints(config.use_window_cache_slots).position(uid)];
                const slot = hint.load(.acquire);
                if (slot < self.rows.len) {
                    const row = &self.rows[slot];
                    row.lock.lockShared();
                    defer row.lock.unlockShared();
                    if (row.uid != null and row.uid.?.eql(uid) and cacheIdentity(row)) {
                        if (row.resource.loading) return .loading;
                        self.cacheHit();
                        return .{ .in_memory = try self.cacheBorrow(slot) };
                    }
                }
                return self.useCacheMiss(uid);
            }
            while (try self.index.find(uid)) |position| {
                if (position.kind != .resource) return .not_in_memory;
                const row = &self.rows[position.slot];
                row.lock.lock();
                defer row.lock.unlock();
                if (row.uid == null or !row.uid.?.eql(uid)) continue;
                return self.useLocked(position.slot);
            }
            return .not_in_memory;
        }
        /// Compile-time fixed declaration indexes its live binding directly.
        pub fn useFixed(self: *Self, comptime declaration: usize) !T.UseResult {
            if (declaration >= fixed.len) @compileError("fixed declaration out of range");
            return self.useFixedBody(declaration);
        }
        /// Runtime numeric fixed declaration indexes its live binding directly.
        pub fn useFixedId(self: *Self, declaration: u32) !T.UseResult {
            if (declaration >= fixed.len) return error.InvalidArgument;
            return self.useFixedBody(@intCast(declaration));
        }
        fn useFixedBody(self: *Self, declaration: usize) !T.UseResult {
            if (comptime config.use_window_cache) return self.useFixedCached(declaration);
            while (true) {
                const parent_number = self.bindings[declaration].parent_number.load(.acquire);
                const slot = self.bindings[declaration].slot.load(.acquire);
                if (slot == types.no_slot) return .not_in_memory;
                const row = &self.rows[slot];
                row.lock.lock();
                defer row.lock.unlock();
                if (self.bindings[declaration].slot.load(.acquire) != slot) continue;
                if (self.bindings[declaration].parent_number.load(.acquire) != parent_number or
                    row.fixed_parent_number != parent_number) continue;
                if (row.fixed_declaration != @as(u32, @intCast(declaration)) or row.uid == null or !row.uid.?.eql(fixed[declaration].uid))
                    return .not_in_memory;
                return self.useLocked(slot);
            }
        }
        fn useLocked(self: *Self, slot: u32) !T.UseResult {
            if (comptime config.use_window_cache) {
                const row = &self.rows[slot];
                if (row.cache.physical != null) {
                    if (row.resource.loading) return .loading;
                    return .{ .in_memory = try self.cacheBorrow(slot) };
                }
                const epoch = self.epoch.load(.acquire);
                if (row.resource.registration) |registration| {
                    if (registration.region != null and !row.resource.loading) try cacheStampAvailable(row);
                }
                return switch (try row.resource.use(epoch)) {
                    .in_memory => |borrow| .{ .in_memory = try self.cacheStart(slot, borrow) },
                    .loading => .loading,
                    .not_in_memory => .not_in_memory,
                };
            }
            const epoch = self.epoch.load(.acquire);
            return switch (try self.rows[slot].resource.use(epoch)) {
                .in_memory => |borrow| .{ .in_memory = try self.wrap(slot, borrow, epoch) },
                .loading => .loading,
                .not_in_memory => .not_in_memory,
            };
        }
        fn wrap(self: *Self, slot: u32, borrow: lifecycle.Borrow, epoch: u64) !T.Borrow {
            if (comptime config.use_window_cache) return self.cacheStart(slot, borrow);
            const result = T.Borrow{ .lease = .{ .slot = slot, .allocation_number = borrow.lease.resource_generation, .use_generation = borrow.lease.use_generation }, .region = borrow.region, .allocation_number = borrow.lease.resource_generation };
            self.recordLeaseBegin(slot, result.lease, epoch);
            return result;
        }
        fn rowLease(lease: T.Lease) lifecycle.Lease {
            return .{ .resource_generation = lease.allocation_number, .use_generation = lease.use_generation };
        }

        const CacheCounters = struct {
            hits: std.atomic.Value(u64) = std.atomic.Value(u64).init(0),
            index_lookups: std.atomic.Value(u64) = std.atomic.Value(u64).init(0),
            exclusive_entries: std.atomic.Value(u64) = std.atomic.Value(u64).init(0),
        };
        pub fn cacheDiagnostics(self: *Self) struct { hits: u64, index_lookups: u64, exclusive_entries: u64 } {
            if (comptime config.use_window_cache and builtin.is_test) return .{
                .hits = self.cache_diagnostics.hits.load(.monotonic),
                .index_lookups = self.cache_diagnostics.index_lookups.load(.monotonic),
                .exclusive_entries = self.cache_diagnostics.exclusive_entries.load(.monotonic),
            };
            return .{ .hits = 0, .index_lookups = 0, .exclusive_entries = 0 };
        }
        fn cacheHit(self: *Self) void {
            if (comptime builtin.is_test) _ = self.cache_diagnostics.hits.fetchAdd(1, .monotonic);
        }
        fn cacheStampAvailable(row: *Row) !void {
            if (row.cache.stamps.next.load(.monotonic) == std.math.maxInt(u64)) return error.SpecificationLimit;
        }
        fn cacheIdentity(row: *const Row) bool {
            const physical = row.cache.physical orelse return false;
            const registration = row.resource.registration orelse return false;
            return physical.lease.resource_generation == row.resource.generation and registration.region != null;
        }
        fn cacheBorrow(self: *Self, slot: u32) !T.Borrow {
            const row = &self.rows[slot];
            std.debug.assert(cacheIdentity(row));
            const physical = row.cache.physical.?;
            const stamp = try row.cache.acquire();
            const result = T.Borrow{ .lease = .{ .slot = slot, .allocation_number = physical.lease.resource_generation, .use_generation = stamp }, .region = physical.region, .allocation_number = physical.lease.resource_generation };
            self.recordLeaseBegin(slot, result.lease, self.epoch.load(.acquire));
            return result;
        }
        fn cacheStart(self: *Self, slot: u32, physical: lifecycle.Borrow) !T.Borrow {
            const row = &self.rows[slot];
            std.debug.assert(row.cache.physical == null and row.cache.count() == 0);
            row.cache.physical = physical;
            self.linkCache(slot);
            self.cache_hints.slots[use_cache.Hints(config.use_window_cache_slots).position(row.uid.?)].store(slot, .release);
            return self.cacheBorrow(slot);
        }
        fn useCacheMiss(self: *Self, uid: Uid) !T.UseResult {
            while (true) {
                if (comptime builtin.is_test) _ = self.cache_diagnostics.index_lookups.fetchAdd(1, .monotonic);
                const position = (try self.index.find(uid)) orelse return .not_in_memory;
                if (position.kind != .resource) return .not_in_memory;
                // Only cache creation/retirement needs structure ownership. Hits
                // never acquire this table lock or consult the UID index.
                self.tables[position.table].write.lock();
                defer self.tables[position.table].write.unlock();
                const row = &self.rows[position.slot];
                row.lock.lock();
                defer row.lock.unlock();
                if (comptime builtin.is_test) _ = self.cache_diagnostics.exclusive_entries.fetchAdd(1, .monotonic);
                if (row.uid == null or !row.uid.?.eql(uid)) continue;
                return self.useLocked(position.slot);
            }
        }
        fn fixedMatches(self: *Self, declaration: usize, slot: u32, parent_number: u64) bool {
            const row = &self.rows[slot];
            return self.bindings[declaration].slot.load(.acquire) == slot and
                self.bindings[declaration].parent_number.load(.acquire) == parent_number and
                row.fixed_parent_number == parent_number and row.fixed_declaration == @as(u32, @intCast(declaration)) and
                row.uid != null and row.uid.?.eql(fixed[declaration].uid);
        }
        fn useFixedCached(self: *Self, declaration: usize) !T.UseResult {
            while (true) {
                const parent_number = self.bindings[declaration].parent_number.load(.acquire);
                const slot = self.bindings[declaration].slot.load(.acquire);
                if (slot == types.no_slot) return .not_in_memory;
                const row = &self.rows[slot];
                {
                    row.lock.lockShared();
                    defer row.lock.unlockShared();
                    if (!self.fixedMatches(declaration, slot, parent_number)) continue;
                    if (cacheIdentity(row)) {
                        if (row.resource.loading) return .loading;
                        self.cacheHit();
                        return .{ .in_memory = try self.cacheBorrow(slot) };
                    }
                }
                self.tables[fixed[declaration].table].write.lock();
                defer self.tables[fixed[declaration].table].write.unlock();
                row.lock.lock();
                defer row.lock.unlock();
                if (comptime builtin.is_test) _ = self.cache_diagnostics.exclusive_entries.fetchAdd(1, .monotonic);
                if (!self.fixedMatches(declaration, slot, parent_number)) continue;
                return self.useLocked(slot);
            }
        }
        // These links are mutated only under table.write and the row EXCLUSIVE
        // gate. Tick also owns table.write, so no detached/reused list nodes exist.
        fn linkCache(self: *Self, slot: u32) void {
            const row = &self.rows[slot];
            std.debug.assert(!row.cache.linked);
            const head = &self.tables[row.table].cache_head;
            row.cache.previous = types.no_slot;
            row.cache.next = head.*;
            if (head.* != types.no_slot) self.rows[head.*].cache.previous = slot;
            head.* = slot;
            row.cache.linked = true;
        }
        fn unlinkCache(self: *Self, slot: u32) void {
            const row = &self.rows[slot];
            if (!row.cache.linked) return;
            if (row.cache.previous == types.no_slot) self.tables[row.table].cache_head = row.cache.next else self.rows[row.cache.previous].cache.next = row.cache.next;
            if (row.cache.next != types.no_slot) self.rows[row.cache.next].cache.previous = row.cache.previous;
            row.cache.next = types.no_slot;
            row.cache.previous = types.no_slot;
            row.cache.linked = false;
        }
        /// Caller owns table.write + exclusive row gate. An idle pin is internal
        /// bookkeeping, never a payload release or notification.
        pub fn flushWindowCache(self: *Self, row: *Row) void {
            if (comptime config.use_window_cache) {
                if (row.cache.physical == null or row.cache.count() != 0) return;
                const physical = row.cache.physical.?;
                _ = row.resource.endUse(physical.lease, .complete, row.cache.last_end_epoch.load(.acquire)) catch
                    @panic("window pin retirement invariant failed");
                self.unlinkCache(@intCast((@intFromPtr(row) - @intFromPtr(self.rows.ptr)) / @sizeOf(Row)));
                row.cache.invalidate();
            }
        }
        fn endCached(self: *Self, lease: T.Lease, disposition: lifecycle.EndDisposition) !lifecycle.EndResult {
            if (lease.slot >= self.rows.len) return error.InvalidArgument;
            if (lease.allocation_number == 0 or lease.use_generation == 0) return error.InvalidLease;
            const row = &self.rows[lease.slot];
            {
                row.lock.lockShared();
                defer row.lock.unlockShared();
                if (row.uid == null or row.resource.generation != lease.allocation_number or
                    lease.use_generation <= row.cache.retired_through) return .force_released;
                if (!row.resource.loading) {
                    const holders = row.cache.count();
                    try row.cache.end(lease.use_generation);
                    row.cache.endedAt(self.epoch.load(.acquire));
                    if (comptime config.development_lease_analysis) self.lease_analysis.collectResource(row.resource, self.epoch.load(.acquire), holders);
                    const result: lifecycle.EndResult = if (disposition == .abandon) .abandoned else .completed;
                    self.recordLeaseEnd(lease, result, self.epoch.load(.acquire));
                    return result;
                }
            }
            self.tables[row.table].write.lock();
            defer self.tables[row.table].write.unlock();
            row.lock.lock();
            defer row.lock.unlock();
            if (row.uid == null or row.resource.generation != lease.allocation_number or
                lease.use_generation <= row.cache.retired_through) return .force_released;
            if (!row.cache.valid(lease.use_generation) or row.cache.count() != 1 or !row.resource.loading) return error.InvalidLease;
            self.collectLeaseWrites(row);
            const result = try row.resource.endUse(row.cache.physical.?.lease, if (disposition == .discard) .abandon else disposition, self.epoch.load(.acquire));
            self.recordLeaseEnd(lease, result, self.epoch.load(.acquire));
            self.unlinkCache(lease.slot);
            row.cache.invalidate();
            if (row.resource.discardPending()) self.tables[row.table].pending_discards += 1;
            if (disposition == .discard) {
                self.finishDiscardLocked(lease.slot) catch return .cleanup_failed;
                return .completed;
            }
            return result;
        }

        fn advanceCacheEpoch(self: *Self, next_epoch: u64) void {
            // Never freeze unrelated rows through a global epoch publication.
            // A late zero-ref entry retires at its recorded logical end epoch,
            // so it cannot invent activity even if it survives this pass.
            for (self.tables) |*table| {
                table.write.lock();
                var slot = table.cache_head;
                var visited: usize = 0;
                while (slot != types.no_slot) {
                    if (visited >= self.rows.len or slot >= self.rows.len) @panic("window cache list invariant failed");
                    const row = &self.rows[slot];
                    const next = row.cache.next;
                    row.lock.lock();
                    self.collectLeaseWrites(row);
                    self.flushWindowCache(row);
                    row.lock.unlock();
                    slot = next;
                    visited += 1;
                }
                table.write.unlock();
            }
            self.epoch.store(next_epoch, .release);
        }
        pub fn push(self: *Self, target: T.Target, uid: Uid, allocation_bytes: ?u64, work: *Workspace) !T.PushResult {
            while (true) {
                // Hits share exactly the passive use path, and ignore new inputs.
                switch (try self.use(uid)) {
                    .in_memory => |borrow| return .{ .in_memory = borrow },
                    .loading => return .loading,
                    .not_in_memory => {},
                }
                const known = try self.index.find(uid);
                const table = if (known) |position| position.table else try self.targetTable(target);
                self.tables[table].write.lock();
                work.reset();
                var locked = true;
                defer {
                    if (locked) self.tables[table].write.unlock();
                    self.flush(table, work);
                }
                const target_range = if (known != null) Range{ .table = table, .parent = null, .start = resourceStart(table), .capacity = declarations[table].capacity, .attributes = declarations[table].attributes } else try self.resolveTarget(target);
                if (target_range.table != table) return error.Nonexistent;
                // A miss is observed again under the structure owner.
                if (try self.index.find(uid)) |position| {
                    if (position.table != table) continue;
                    if (position.kind != .resource) return error.InvalidArgument;
                    const slot = position.slot;
                    const row = &self.rows[slot];
                    row.lock.lock();
                    defer row.lock.unlock();
                    if (row.uid == null or !row.uid.?.eql(uid)) return error.Nonexistent;
                    if (row.resource.discardPending()) return error.CleanupPending;
                    if (row.resource.registration.?.region != null) {
                        return switch (try self.useLocked(slot)) {
                            .in_memory => |borrow| .{ .in_memory = borrow },
                            .loading => .loading,
                            .not_in_memory => unreachable,
                        };
                    }
                    const size = allocation_bytes orelse row.resource.registration.?.allocation_bytes;
                    const old_size = row.resource.registration.?.allocation_bytes;
                    const next_total = try replaceBytes(self.tables[table].registered_bytes, old_size, size);
                    const next_pages = try addBytes(self.tables[table].system_page_bytes, try region.requiredBytes(size));
                    const number = try self.numbers.take();
                    // Resolve a candidate registration first; allocation failure preserves the old one.
                    var replacement = try Resource.fixed(size);
                    replacement.generation = row.resource.generation;
                    replacement.next_use_generation = row.resource.next_use_generation;
                    replacement.forced_through_use_generation = row.resource.forced_through_use_generation;
                    replacement.activity_history = row.resource.activity_history;
                    const epoch = self.epoch.load(.acquire);
                    if (comptime config.use_window_cache) try cacheStampAvailable(row);
                    const fresh = (try replacement.push(epoch, number)).fresh;
                    row.resource = replacement;
                    self.tables[table].registered_bytes = next_total;
                    self.tables[table].system_page_bytes = next_pages;
                    self.recordAllocation(row, size);
                    return .{ .newly_allocated = try self.wrap(slot, fresh, epoch) };
                }
                if (known != null) continue;
                const size = allocation_bytes orelse return error.InvalidArgument;
                _ = try region.requiredBytes(size);
                const number = try self.numbers.take(); // Exhaustion is checked before any eviction.
                var available: usize = 0;
                var slot: ?u32 = null;
                for (self.rows[target_range.start..][0..target_range.capacity], target_range.start..) |*row, i| {
                    if (!self.belongs(i, target_range.parent) or row.fixed_declaration != null or
                        !self.allowedAt(i, target_range)) continue;
                    available += 1;
                    if (row.uid == null and slot == null) slot = @intCast(i);
                }
                if (available == 0) return error.InvalidArgument;
                if (slot == null) {
                    if (target_range.parent == null) return error.Blocked;
                    slot = try self.rotate(target_range, size, work);
                }
                const row = &self.rows[slot.?];
                row.lock.lock();
                var row_locked = true;
                defer if (row_locked) row.lock.unlock();
                const next_total = try addBytes(self.tables[table].registered_bytes, size);
                const next_pages = try addBytes(self.tables[table].system_page_bytes, try region.requiredBytes(size));
                var value = try Resource.dynamic(size);
                const epoch = self.epoch.load(.acquire);
                if (comptime config.use_window_cache) try cacheStampAvailable(row);
                const fresh = (try value.push(epoch, number)).fresh;
                self.index.insert(slot.?, uid, .{ .kind = .resource, .table = table, .slot = slot.? }) catch |err| {
                    value.forceRelease() catch @panic("ledger allocation rollback failed");
                    if (err == error.DuplicateUid) continue;
                    return err;
                };
                row.uid = uid;
                row.parent = target_range.parent;
                row.attributes = target_range.attributes;
                row.resource = value;
                self.tables[table].registered_bytes = next_total;
                self.tables[table].system_page_bytes = next_pages;
                self.recordAllocation(row, size);
                const borrow = try self.wrap(slot.?, fresh, epoch);
                row.lock.unlock();
                row_locked = false;
                self.tables[table].write.unlock();
                locked = false;
                self.flush(table, work);
                return .{ .newly_allocated = borrow };
            }
        }

        pub fn endUse(self: *Self, lease: T.Lease, disposition: lifecycle.EndDisposition) !lifecycle.EndResult {
            if (comptime config.use_window_cache) return self.endCached(lease, disposition);
            if (lease.slot >= self.rows.len) return error.InvalidArgument;
            const row = &self.rows[lease.slot];
            row.lock.lock();
            if (row.uid == null or row.resource.generation != lease.allocation_number) {
                row.lock.unlock();
                return .force_released;
            }
            if (disposition == .complete or !row.resource.loading) {
                defer row.lock.unlock();
                const epoch = self.epoch.load(.acquire);
                const holders = row.resource.use_count;
                const result = try row.resource.endUse(rowLease(lease), disposition, epoch);
                if (comptime config.development_lease_analysis) if (result == .completed) self.lease_analysis.collectResource(row.resource, epoch, holders);
                self.recordLeaseEnd(lease, result, epoch);
                return result;
            }
            const table = row.table;
            row.lock.unlock();
            self.tables[table].write.lock();
            defer self.tables[table].write.unlock();
            row.lock.lock();
            defer row.lock.unlock();
            return self.endLoadingLocked(lease, disposition);
        }

        /// Revalidate after upgrading from the row lock to table.write + row.
        /// A fixed row can survive forceRelease with its allocation number intact.
        pub fn endLoadingLocked(self: *Self, lease: T.Lease, disposition: lifecycle.EndDisposition) !lifecycle.EndResult {
            const row = &self.rows[lease.slot];
            const table = row.table;
            if (lease.allocation_number == 0 or lease.use_generation == 0) return error.InvalidLease;
            if (row.uid == null or row.resource.generation != lease.allocation_number) return .force_released;
            if (lease.use_generation <= row.resource.forced_through_use_generation) return .force_released;
            if (row.resource.use_count != 1 or lease.use_generation != row.resource.active_use_xor)
                return error.InvalidLease;
            const epoch = self.epoch.load(.acquire);
            self.collectLeaseWrites(row);
            // Consume the valid use before any fallible cleanup. The no-cleanup
            // abandon primitive also gives callers a guaranteed terminal exit.
            const result = try row.resource.endUse(rowLease(lease), .abandon, epoch);
            self.recordLeaseEnd(lease, result, epoch);
            self.tables[table].pending_discards += 1;
            if (disposition == .abandon) return .abandoned;
            self.finishDiscardLocked(lease.slot) catch return .cleanup_failed;
            return .completed;
        }

        /// Caller owns table.write and the row. There is no outstanding public
        /// or physical use. No notice is sent for an unpublished discard.
        pub fn finishDiscardLocked(self: *Self, slot: u32) !void {
            const row = &self.rows[slot];
            if (!row.resource.discardPending()) return error.InvalidState;
            const reg = row.resource.registration orelse return error.InvalidState;
            const old_pages = reg.allocatedPageBytes();
            if (row.resource.retention == .dynamic) {
                try self.index.removeAfter(slot, &row.resource, Resource.cleanupDiscard);
            } else try row.resource.cleanupDiscard();
            self.recordAllocationRelease(row);
            self.tables[row.table].system_page_bytes -= old_pages;
            self.tables[row.table].pending_discards -= 1;
            if (row.resource.registration == null) {
                self.tables[row.table].registered_bytes -= reg.allocation_bytes;
                row.uid = null;
            }
        }

        /// Establish one shell, replacing complete direct children only after
        /// every affected row, index publication and new declaration preflights.
        pub fn pushPartition(self: *Self, target: T.Target, uid: Uid, capacity: u32, attributes: types.Attributes, work: *Workspace) !T.PartitionResult {
            retry_push: while (true) {
                const observed = try self.index.find(uid);
                const table = if (observed) |position| position.table else try self.targetTable(target);
                self.tables[table].write.lock();
                work.reset();
                defer {
                    self.tables[table].write.unlock();
                    self.flush(table, work);
                }
                if (try self.index.find(uid)) |position| {
                    if (position.table != table) continue;
                    if (position.kind != .partition) return error.InvalidArgument;
                    const shell = &self.shells[position.slot];
                    shell.history = try shell.history.withAccess(self.epoch.load(.acquire));
                    return .{ .created = false, .allocation_number = shell.allocation_number };
                }
                var target_range = try self.resolveTarget(target);
                if (target_range.table != table) continue;
                if (target_range.parent == null) {
                    const declaration = declarations[table];
                    target_range.start += declaration.reserved_start;
                    target_range.capacity = declaration.reserved_count;
                }
                if (capacity == 0) return error.InvalidArgument;
                for (fixed) |declaration| {
                    if (declaration.table == table and declaration.parent != null and
                        declaration.parent.?.eql(uid) and declaration.ordinal >= capacity)
                        return error.InvalidArgument;
                }
                var longest: usize = 0;
                var run: usize = 0;
                for (self.rows[target_range.start..][0..target_range.capacity]) |*row| {
                    if (row.parent == target_range.parent and row.fixed_declaration != null) {
                        run = 0;
                    } else {
                        run += 1;
                        longest = @max(longest, run);
                    }
                }
                if (capacity > longest) return error.InvalidArgument;
                const prepared = try self.preparePartitionPlan(target_range, work);
                var attempts: usize = 0;
                while (attempts <= prepared.child_count) : (attempts += 1) {
                    const plan = (try Planning.plan(@intCast(target_range.start), @intCast(target_range.capacity), work.cells[0..prepared.cell_count], work.children[0..prepared.child_count], capacity, .{ .candidate_a = work.merge_a, .candidate_b = work.merge_b, .best_numbers = work.best_numbers })) orelse return error.Blocked;
                    var root_count: usize = 0;
                    for (work.children[0..prepared.child_count], work.child_slots[0..prepared.child_count]) |child, shell_slot| {
                        if (child.start >= plan.start and child.start < plan.end) {
                            if (root_count == work.shells.len) return error.WorkspaceTooSmall;
                            work.shells[root_count] = shell_slot;
                            root_count += 1;
                        }
                    }
                    const number = try self.numbers.take();
                    work.blocked_slot = null;
                    const shell_slot = tree_operations.For(Self).installRange(self, plan.start, plan.end - plan.start, capacity, work.shells[0..root_count], target_range.parent, uid, attributes, number, work) catch |err| {
                        if (err == error.RetryExisting) continue :retry_push;
                        if (work.blocked_slot) |slot| {
                            var excluded = false;
                            for (work.children[0..prepared.child_count]) |*child| {
                                if (slot >= child.start and slot < @as(u64, child.start) + child.capacity and child.safe) {
                                    child.safe = false;
                                    excluded = true;
                                    break;
                                }
                            }
                            if (excluded) continue;
                        }
                        return err;
                    };
                    return .{ .created = true, .allocation_number = self.shells[shell_slot].allocation_number };
                }
                return error.Blocked;
            }
        }

        const PreparedPartition = struct { cell_count: usize, child_count: usize };
        fn preparePartitionPlan(self: *Self, target: Range, work: *Workspace) !PreparedPartition {
            var child_count: usize = 0;
            var child = if (target.parent) |p| self.shells[p].first_child else self.tables[target.table].first_partition;
            while (child != types.no_slot) {
                if (child_count >= declarations[target.table].partition_capacity) return error.InvalidState;
                if (child_count >= work.child_slots.len or child_count >= work.children.len) return error.WorkspaceTooSmall;
                const shell = &self.shells[child];
                if (shell.uid == null or shell.table != target.table or shell.parent != target.parent) return error.InvalidState;
                work.child_slots[child_count] = child;
                child_count += 1;
                child = shell.next_sibling;
            }
            std.sort.heap(u32, work.child_slots[0..child_count], self, shellStartBefore);
            var number_count: usize = 0;
            for (work.child_slots[0..child_count], 0..) |shell_slot, i| {
                const shell = &self.shells[shell_slot];
                const roots = [_]u32{shell_slot};
                const descendants = try tree_operations.For(Self).collectShells(self, target.table, shell.start, shell.capacity, &roots, target.parent, work.shells);
                const number_start = number_count;
                var summary = Planning.Child{ .start = shell.start, .capacity = shell.capacity, .safe = true, .score = 0, .aligned_bits = 0, .latest_access = 0, .allocation_numbers = undefined };
                const epoch = self.epoch.load(.acquire);
                const activity_policy = Activity.Policy{ .score_epochs = declarations[target.table].policy.score_epochs, .idle_epochs = declarations[target.table].policy.idle_epochs };
                for (descendants) |descendant| {
                    if (number_count == work.numbers.len) return error.WorkspaceTooSmall;
                    const record = &self.shells[descendant];
                    work.numbers[number_count] = record.allocation_number;
                    number_count += 1;
                    summary.score = @max(summary.score, try record.history.score(epoch, activity_policy));
                    summary.aligned_bits |= try record.history.aligned(epoch);
                    summary.latest_access = @max(summary.latest_access, record.history.last_activity_epoch);
                }
                for (self.rows[shell.start..][0..shell.capacity]) |*row| {
                    row.lock.lock();
                    defer row.lock.unlock();
                    self.flushWindowCache(row);
                    if (row.uid == null) continue;
                    const history = try row.resource.historyAt(epoch);
                    summary.score = @max(summary.score, try history.score(epoch, activity_policy));
                    summary.aligned_bits |= try history.aligned(epoch);
                    summary.latest_access = @max(summary.latest_access, history.last_activity_epoch);
                    if (row.resource.registration) |registration| {
                        if (registration.region != null and (row.resource.use_count != 0 or row.resource.loading or !row.attributes.safe()))
                            summary.safe = false;
                    }
                }
                summary.allocation_numbers = work.numbers[number_start..number_count];
                std.sort.heap(u64, work.numbers[number_start..number_count], {}, numberDescending);
                work.children[i] = summary;
            }
            var cell_count: usize = 0;
            var slot = target.start;
            var child_index: usize = 0;
            while (slot < target.start + target.capacity) {
                if (cell_count == work.cells.len) return error.WorkspaceTooSmall;
                if (child_index < child_count and work.children[child_index].start == slot) {
                    const descriptor = work.children[child_index];
                    work.cells[cell_count] = .{ .start = @intCast(slot), .kind = .child, .child_index = @intCast(child_index) };
                    slot += descriptor.capacity;
                    child_index += 1;
                } else {
                    const row = &self.rows[slot];
                    if (row.parent != target.parent) return error.InvalidState;
                    work.cells[cell_count] = .{ .start = @intCast(slot), .kind = if (row.uid == null) .empty else .barrier };
                    slot += 1;
                }
                cell_count += 1;
            }
            if (child_index != child_count) return error.InvalidState;
            return .{ .cell_count = cell_count, .child_count = child_count };
        }
        fn shellStartBefore(self: *Self, left: u32, right: u32) bool {
            return self.shells[left].start < self.shells[right].start;
        }
        fn numberDescending(_: void, left: u64, right: u64) bool {
            return left > right;
        }

        pub fn updateUid(self: *Self, uid: Uid, change: T.Update) !T.UpdateResult {
            while (try self.index.find(uid)) |position| {
                if (position.kind == .partition) {
                    return self.updatePartition(uid, change) catch |err| {
                        if (err == error.RetryResourceUpdate) continue;
                        return err;
                    };
                }
                if (change.allocation_bytes != null) self.tables[position.table].write.lock();
                defer if (change.allocation_bytes != null) self.tables[position.table].write.unlock();
                const row = &self.rows[position.slot];
                row.lock.lock();
                defer row.lock.unlock();
                if (row.uid == null or !row.uid.?.eql(uid)) continue;
                return self.updateLocked(position.slot, null, change);
            }
            return error.Nonexistent;
        }
        pub fn updateLease(self: *Self, lease: T.Lease, change: T.Update) !T.UpdateResult {
            if (lease.slot >= self.rows.len) return error.InvalidArgument;
            const row = &self.rows[lease.slot];
            const table = row.table;
            if (change.allocation_bytes != null) self.tables[table].write.lock();
            defer if (change.allocation_bytes != null) self.tables[table].write.unlock();
            row.lock.lock();
            defer row.lock.unlock();
            if (row.uid == null or row.resource.generation != lease.allocation_number) return .force_released;
            if (comptime config.use_window_cache) return self.updateCachedLocked(lease.slot, lease, change);
            return self.updateLocked(lease.slot, rowLease(lease), change);
        }
        fn updateLocked(self: *Self, slot: u32, caller: ?lifecycle.Lease, change: T.Update) !T.UpdateResult {
            if (comptime config.use_window_cache) {
                std.debug.assert(caller == null);
                return self.updateCachedLocked(slot, null, change);
            }
            const row = &self.rows[slot];
            if (caller) |lease| {
                if (lease.resource_generation != row.resource.generation or
                    lease.use_generation <= row.resource.forced_through_use_generation) return .force_released;
                if (row.resource.use_count == 0 or lease.use_generation == 0 or
                    lease.use_generation >= row.resource.next_use_generation) return error.InvalidLease;
                if (row.resource.use_count == 1 and lease.use_generation != row.resource.active_use_xor)
                    return error.InvalidLease;
            }
            var result: T.UpdateResult = .{ .updated = null };
            if (change.allocation_bytes) |size| {
                const previous = row.resource.registration.?.allocation_bytes;
                const previous_pages = row.resource.registration.?.allocatedPageBytes();
                const next_total = try replaceBytes(self.tables[row.table].registered_bytes, previous, size);
                const next_pages = try replaceBytes(self.tables[row.table].system_page_bytes, previous_pages, if (row.resource.registration.?.region != null) try region.requiredBytes(size) else 0);
                const prior_base = if (row.resource.registration.?.region) |r| r.base else null;
                self.collectLeaseWrites(row);
                switch (try row.resource.update(size, caller)) {
                    .in_use => return .in_use,
                    .force_released => return .force_released,
                    .nonexistent => return error.Nonexistent,
                    .registration_only => result = .registration_only,
                    .resident => |bytes| result = .{ .updated = if (caller != null) bytes else null },
                }
                if (comptime config.development_lease_analysis) {
                    const after_base = if (row.resource.registration.?.region) |r| r.base else null;
                    if (prior_base != after_base) self.lease_analysis.resetResource(row.resource, self.epoch.load(.acquire));
                }
                self.tables[row.table].registered_bytes = next_total;
                self.tables[row.table].system_page_bytes = next_pages;
            }
            if (change.attributes) |attrs| row.attributes = attrs.apply(row.attributes);
            return result;
        }

        fn updateCachedLocked(self: *Self, slot: u32, caller: ?T.Lease, change: T.Update) !T.UpdateResult {
            const row = &self.rows[slot];
            if (caller) |lease| {
                if (lease.allocation_number != row.resource.generation or lease.use_generation <= row.cache.retired_through) return .force_released;
                if (!row.cache.valid(lease.use_generation)) return error.InvalidLease;
            }
            var result: T.UpdateResult = .{ .updated = null };
            if (change.allocation_bytes) |size| {
                self.flushWindowCache(row);
                const registration = row.resource.registration orelse return error.Nonexistent;
                const next_total = try replaceBytes(self.tables[row.table].registered_bytes, registration.allocation_bytes, size);
                const next_pages = try replaceBytes(self.tables[row.table].system_page_bytes, registration.allocatedPageBytes(), if (registration.region != null) try region.requiredBytes(size) else 0);
                // One physical pin can represent many logical users. Only the
                // sole validated public caller may authorize pointer movement.
                const physical_caller = if (caller != null and row.cache.count() == 1) row.cache.physical.?.lease else null;
                const prior_base = if (row.resource.registration.?.region) |r| r.base else null;
                self.collectLeaseWrites(row);
                switch (try row.resource.update(size, physical_caller)) {
                    .in_use => return .in_use,
                    .force_released => return .force_released,
                    .nonexistent => return error.Nonexistent,
                    .registration_only => result = .registration_only,
                    .resident => |bytes| {
                        if (row.cache.physical) |*physical| physical.region = bytes;
                        result = .{ .updated = if (caller != null) bytes else null };
                    },
                }
                if (comptime config.development_lease_analysis) {
                    const after_base = if (row.resource.registration.?.region) |r| r.base else null;
                    if (prior_base != after_base) self.lease_analysis.resetResource(row.resource, self.epoch.load(.acquire));
                }
                self.tables[row.table].registered_bytes = next_total;
                self.tables[row.table].system_page_bytes = next_pages;
            }
            if (change.attributes) |attrs| row.attributes = attrs.apply(row.attributes);
            return result;
        }

        pub fn close(self: *Self, uid: Uid, work: *Workspace) !void {
            return self.closeUid(uid, false, work);
        }
        pub fn forceRelease(self: *Self, uid: Uid, work: *Workspace) !void {
            return self.closeUid(uid, true, work);
        }
        fn closeUid(self: *Self, uid: Uid, forced: bool, work: *Workspace) !void {
            while (true) {
                const observed = (try self.index.find(uid)) orelse return error.Nonexistent;
                self.tables[observed.table].write.lock();
                work.reset();
                var locked = true;
                defer if (locked) self.tables[observed.table].write.unlock();
                const position = (try self.index.find(uid)) orelse return error.Nonexistent;
                if (position.table != observed.table) continue;
                if (position.kind == .partition) {
                    try self.closePartition(uid, forced, work);
                } else {
                    const row = &self.rows[position.slot];
                    row.lock.lock();
                    defer row.lock.unlock();
                    if (row.uid == null or !row.uid.?.eql(uid)) return error.Nonexistent;
                    if (row.resource.discardPending()) {
                        // Only a failed OS page release leaves a retryable pending
                        // discard; an index or state error is returned as itself.
                        self.finishDiscardLocked(position.slot) catch |err|
                            return if (err == error.ReleaseFailed) error.CleanupPending else err;
                        return;
                    }
                    try self.releaseLocked(position.slot, row.resource.registration.?.allocation_bytes, forced, false, work);
                }
                self.tables[position.table].write.unlock();
                locked = false;
                self.flush(position.table, work);
                return;
            }
        }
        fn releaseLocked(self: *Self, slot: u32, decision_bytes: u64, forced: bool, remove_fixed: bool, work: *Workspace) !void {
            const row = &self.rows[slot];
            self.flushWindowCache(row);
            const reg = row.resource.registration orelse return;
            const resident = reg.region != null;
            if (!forced and resident) {
                if (row.resource.use_count != 0 or row.resource.loading) return error.InUse;
                if (!row.attributes.safe()) return error.Blocked;
                if (release.check(declarations[row.table].policy.release_check, decision_bytes, reg.allocation_bytes, false) != .allowed) return error.ChangedBeyondTolerance;
            }
            const remove_registration = row.fixed_declaration == null or remove_fixed;
            const previous_note_count = work.note_count;
            if (resident) try self.saveNote(row.table, work, row.uid.?, row.resource.generation);
            errdefer work.note_count = previous_note_count;
            if (remove_registration) try self.index.remove(slot);
            const old_pages = reg.allocatedPageBytes();
            const final_history = row.resource.historyAt(self.epoch.load(.acquire)) catch unreachable;
            self.collectLeaseWrites(row);
            if (forced) row.resource.forceRelease() catch @panic("ledger OS release invariant failed") else {
                const result = row.resource.unload() catch @panic("ledger OS release invariant failed");
                if (result != .closed) unreachable;
            }
            row.resource.activity_history = final_history;
            self.tables[row.table].system_page_bytes -= old_pages;
            self.recordAllocationRelease(row);
            if (remove_registration) {
                self.tables[row.table].registered_bytes -= reg.allocation_bytes;
                if (row.fixed_declaration) |f| self.bindings[f].slot.store(types.no_slot, .release);
                row.uid = null;
                row.fixed_declaration = null;
                row.resource.registration = null;
            }
        }

        // Only called under the existing table write lock. Comptime false
        // removes the calls and both zero-sized storage fields entirely.
        inline fn recordAllocation(self: *Self, row: *Row, bytes: u64) void {
            if (comptime config.development_lease_analysis) self.lease_analysis.resetResource(row.resource, self.epoch.load(.acquire));
            if (comptime config.development_allocation_statistics) {
                row.allocation_birth = .{ .epoch = self.epoch.load(.acquire), .allocation_bytes = bytes };
                const counters = &self.tables[row.table].allocation_statistics;
                counters.new_allocations +|= 1;
                if (bytes < std.heap.pageSize()) {
                    counters.subpage_allocations +|= 1;
                    counters.subpage_allocation_bytes +|= bytes;
                }
            }
        }

        pub inline fn recordAllocationRelease(self: *Self, row: *Row) void {
            if (comptime config.use_window_cache) {
                self.unlinkCache(@intCast((@intFromPtr(row) - @intFromPtr(self.rows.ptr)) / @sizeOf(Row)));
                row.cache.forceInvalidate();
            }
            // Every committed owner/tree/destruction release reaches this hook
            // before row identity is cleared, with the row lock already held.
            if (comptime config.development_lease_analysis) {
                self.lease_analysis.lock.lock();
                defer self.lease_analysis.lock.unlock();
                self.lease_analysis.tracker.invalidate(row.resource.generation, self.epoch.load(.acquire), lease_analysis_module.monotonicMilliseconds());
            }
            if (comptime config.development_allocation_statistics) {
                const birth = row.allocation_birth;
                if (birth.epoch == 0) return;
                if (birth.epoch == self.epoch.load(.acquire)) {
                    const counters = &self.tables[row.table].allocation_statistics;
                    counters.same_epoch_releases +|= 1;
                    counters.same_epoch_allocation_bytes +|= birth.allocation_bytes;
                }
                row.allocation_birth = .{};
            }
        }

        inline fn recordLeaseEnd(self: *Self, lease: T.Lease, result: lifecycle.EndResult, epoch: u64) void {
            if (comptime config.development_lease_analysis) {
                if (result == .force_released) return;
                self.lease_analysis.lock.lock();
                defer self.lease_analysis.lock.unlock();
                self.lease_analysis.tracker.finish(lease, epoch, lease_analysis_module.monotonicMilliseconds());
            }
        }

        pub inline fn collectLeaseWrites(self: *Self, row: *Row) void {
            if (comptime config.development_lease_analysis) {
                const holders = if (comptime config.use_window_cache) row.cache.count() else row.resource.use_count;
                self.lease_analysis.collectResource(row.resource, self.epoch.load(.acquire), holders);
            }
        }

        fn recordLeaseBegin(self: *Self, slot: u32, lease: T.Lease, epoch: u64) void {
            if (comptime config.development_lease_analysis) {
                self.collectLeaseWrites(&self.rows[slot]);
                self.lease_analysis.lock.lock();
                defer self.lease_analysis.lock.unlock();
                self.lease_analysis.tracker.begin(&self.rows[slot].uid.?.bytes, lease, epoch, lease_analysis_module.monotonicMilliseconds());
            }
        }
        /// One coherent, read-only physical-order snapshot; [] is summary-only.
        /// Active duration is summary.snapshot_ms - started_ms (ended_ms stays 0).
        pub fn getLeaseAnalysis(self: *Self, records: []LeaseAnalysisRecord) !LeaseAnalysisSummary {
            if (comptime config.development_lease_analysis) {
                self.lease_analysis.lock.lock();
                defer self.lease_analysis.lock.unlock();
                return self.lease_analysis.tracker.snapshot(records, lease_analysis_module.monotonicMilliseconds());
            } else return error.LeaseAnalysisDisabled;
        }

        pub const DevelopmentStatistics = if (config.development_allocation_statistics) struct {
            pub fn get(owner: *Self, table: u32) !allocation_statistics.Counters {
                if (table >= owner.tables.len) return error.InvalidArgument;
                owner.tables[table].write.lock();
                defer owner.tables[table].write.unlock();
                return owner.tables[table].allocation_statistics;
            }
        } else struct {};

        pub fn setMode(self: *Self, table: u32, mode: types.Mode) !void {
            if (table >= self.tables.len) return error.InvalidArgument;
            self.tables[table].write.lock();
            defer self.tables[table].write.unlock();
            self.tables[table].mode = mode;
        }
        pub fn view(self: *Self, table: u32) !types.TableView {
            if (table >= self.tables.len) return error.InvalidArgument;
            self.tables[table].write.lock();
            defer self.tables[table].write.unlock();
            return .{ .registered_bytes = self.tables[table].registered_bytes, .system_page_bytes = self.tables[table].system_page_bytes };
        }
        pub fn tick(self: *Self, workspaces: []Workspace) !types.TickResult {
            if (workspaces.len != self.tables.len) return error.InvalidArgument;
            const epoch = self.epoch.load(.acquire);
            if (epoch == std.math.maxInt(u64)) return error.SpecificationLimit;
            var result = types.TickResult{ .released = 0, .scanned = 0 };
            for (declarations, 0..) |decl, t| {
                const state = &self.tables[t];
                const work = &workspaces[t];
                work.reset();
                state.write.lock();
                var locked = true;
                defer {
                    if (locked) state.write.unlock();
                    self.flush(@intCast(t), work);
                }
                var budget = decl.policy.tick_maximum_actions;
                // Finishing a prior discard is not a new safe-release decision.
                // Count failed attempts as well, so a persistent OS failure is
                // bounded and cannot stall another table or force content out.
                // Resume after the row tried last and stop once every pending
                // row has been tried, so unfinished cleanup continues next Tick.
                var pending_left = state.pending_discards;
                var offset = state.pending_cursor;
                var visited: u32 = 0;
                const row_start = resourceStart(t);
                while (budget != 0 and pending_left != 0 and visited < decl.capacity) : (visited += 1) {
                    const slot: u32 = @intCast(row_start + offset);
                    offset = if (offset + 1 == decl.capacity) 0 else offset + 1;
                    result.scanned += 1;
                    const row = &self.rows[slot];
                    row.lock.lock();
                    if (row.resource.discardPending()) {
                        budget -= 1;
                        pending_left -= 1;
                        state.pending_cursor = offset;
                        if (self.finishDiscardLocked(slot)) |_| {
                            result.released += 1;
                        } else |_| {}
                    }
                    row.lock.unlock();
                }
                var free = self.freeCapacity(t);
                var capacity_target: usize = 0;
                var wanted: u32 = 0;
                const p = decl.policy;
                switch (p.protection) {
                    .concentrated => {
                        if (p.concentrated_trigger_percent == 0 or free.total == 0) state.concentrated = false else {
                            if (free.empty * 100 < free.total * @as(u64, p.concentrated_trigger_percent)) state.concentrated = true;
                            if (free.empty * 100 >= free.total * @as(u64, p.concentrated_target_percent)) state.concentrated = false;
                            if (state.concentrated) {
                                capacity_target = @intCast((free.total * p.concentrated_target_percent + 99) / 100);
                                wanted = @intCast(@min(budget, capacity_target - free.empty));
                            }
                        }
                    },
                    .smooth => if (free.total != 0 and free.empty * 100 < free.total * @as(u64, p.smooth_trigger_percent)) {
                        capacity_target = @intCast((free.total * p.smooth_trigger_percent + 99) / 100);
                        if (free.empty * 100 <= free.total * @as(u64, p.smooth_emergency_percent)) wanted = budget else if (epoch - state.last_smooth_epoch >= p.smooth_interval_epochs) {
                            wanted = @min(budget, p.smooth_release_count);
                            state.last_smooth_epoch = epoch;
                        }
                        wanted = @intCast(@min(wanted, capacity_target - free.empty));
                    },
                }
                if (wanted != 0) {
                    const count = try self.clean(t, .capacity, wanted, work, &result.scanned);
                    budget -= @intCast(count);
                    result.released += count;
                    free = self.freeCapacity(t);
                    if (p.protection == .concentrated and free.empty * 100 >= free.total * @as(u64, p.concentrated_target_percent))
                        state.concentrated = false;
                }
                if (state.mode == .optimize and budget != 0) {
                    const count = try self.clean(t, .mode, budget, work, &result.scanned);
                    budget -= @intCast(count);
                    result.released += count;
                }
                if (state.mode != .unrestricted and p.idle_epochs != 0 and budget != 0) {
                    const count = try self.clean(t, .idle, budget, work, &result.scanned);
                    result.released += count;
                }
                state.write.unlock();
                locked = false;
                self.flush(@intCast(t), work);
            }
            if (comptime config.use_window_cache) self.advanceCacheEpoch(epoch + 1) else {
                if (comptime config.development_lease_analysis) {
                    for (self.rows) |*row| {
                        row.lock.lock();
                        self.collectLeaseWrites(row);
                        row.lock.unlock();
                    }
                }
                self.epoch.store(epoch + 1, .release);
            }
            return result;
        }

        const Stage = enum { capacity, mode, idle, rotation };
        fn clean(self: *Self, table: usize, stage: Stage, maximum: u32, work: *Workspace, scanned: *usize) !usize {
            const candidate_limit = @min(maximum, declarations[table].capacity);
            if (work.candidates.len < candidate_limit) return error.WorkspaceTooSmall;
            var count: usize = 0;
            const start = resourceStart(table);
            for (self.rows[start..][0..declarations[table].capacity], start..) |*row, slot| {
                scanned.* += 1;
                row.lock.lock();
                defer row.lock.unlock();
                if (!self.safe(row)) continue;
                if (stage == .capacity and (!self.capacityEligible(table, slot) or row.fixed_declaration != null)) continue;
                if (stage == .idle and !try row.resource.activity_history.idle(self.epoch.load(.acquire), .{ .score_epochs = declarations[table].policy.score_epochs, .idle_epochs = declarations[table].policy.idle_epochs })) continue;
                const item = Candidate{ .slot = @intCast(slot), .decision_bytes = row.resource.registration.?.allocation_bytes, .snapshot = self.snapshotLocked(row) };
                self.retain(work.candidates[0..candidate_limit], &count, item, stage);
            }
            std.sort.heap(Candidate, work.candidates[0..count], SortContext{ .owner = self, .stage = stage }, candidateBefore);
            // Only retained candidates are reread; execution rechecks protection.
            var done: usize = 0;
            var i: usize = 0;
            while (i < count) : (i += 1) {
                const item = work.candidates[i];
                const row = &self.rows[item.slot];
                row.lock.lock();
                defer row.lock.unlock();
                if (stage == .idle and !try row.resource.activity_history.idle(self.epoch.load(.acquire), .{ .score_epochs = declarations[table].policy.score_epochs, .idle_epochs = declarations[table].policy.idle_epochs })) continue;
                self.releaseLocked(item.slot, item.decision_bytes, false, false, work) catch |err| {
                    switch (err) {
                        error.InUse, error.Blocked, error.ChangedBeyondTolerance => continue,
                        else => return err,
                    }
                };
                done += 1;
            }
            return done;
        }
        fn safe(self: *Self, row: *Row) bool {
            self.flushWindowCache(row);
            const registration = row.resource.registration orelse return false;
            return row.uid != null and registration.region != null and row.resource.use_count == 0 and
                !row.resource.loading and row.attributes.safe();
        }
        fn retain(self: *Self, list: []Candidate, count: *usize, candidate: Candidate, stage: Stage) void {
            if (count.* < list.len) {
                var item = count.*;
                list[item] = candidate;
                count.* += 1;
                while (item != 0) {
                    const parent = (item - 1) / 2;
                    if (!self.before(list[parent], list[item], stage)) break;
                    std.mem.swap(Candidate, &list[parent], &list[item]);
                    item = parent;
                }
                return;
            }
            if (!self.before(candidate, list[0], stage)) return;
            list[0] = candidate;
            var parent: usize = 0;
            while (true) {
                const left = parent * 2 + 1;
                if (left >= list.len) break;
                const right = left + 1;
                const worst = if (right < list.len and self.before(list[left], list[right], stage)) right else left;
                if (!self.before(list[parent], list[worst], stage)) break;
                std.mem.swap(Candidate, &list[parent], &list[worst]);
                parent = worst;
            }
        }
        const SortContext = struct { owner: *Self, stage: Stage };
        fn candidateBefore(ctx: SortContext, left: Candidate, right: Candidate) bool {
            return ctx.owner.before(left, right, ctx.stage);
        }
        fn before(self: *Self, left: Candidate, right: Candidate, stage: Stage) bool {
            const l = left.snapshot;
            const r = right.snapshot;
            if (stage != .rotation) {
                if (l.attributes.access_loss_impact != r.attributes.access_loss_impact)
                    return @intFromEnum(l.attributes.access_loss_impact) > @intFromEnum(r.attributes.access_loss_impact);
            }
            if (l.score != r.score) return l.score < r.score;
            if (stage != .rotation) {
                const lb = @as(u128, l.attributes.recovery_cost_coefficient) * l.size;
                const rb = @as(u128, r.attributes.recovery_cost_coefficient) * r.size;
                if (lb != rb) return lb < rb;
                if (stage != .capacity and l.size != r.size) return l.size > r.size;
            }
            return Activity.tieBefore(l.history, r.history, self.epoch.load(.acquire), l.number, r.number) catch unreachable;
        }
        const Snapshot = struct { attributes: types.Attributes, score: u8, size: u64, history: Activity.History, number: u64 };
        pub fn snapshotLocked(self: *Self, row: *const Row) Snapshot {
            const epoch = self.epoch.load(.acquire);
            const history = row.resource.historyAt(epoch) catch unreachable;
            return .{ .attributes = row.attributes, .score = history.score(epoch, .{ .score_epochs = declarations[row.table].policy.score_epochs, .idle_epochs = declarations[row.table].policy.idle_epochs }) catch unreachable, .size = if (row.resource.registration) |reg| reg.allocation_bytes else 0, .history = history, .number = row.resource.generation };
        }
        fn capacityEligible(_: *Self, table: usize, slot: usize) bool {
            const d = declarations[table];
            if (d.reserved_count == d.capacity) return true;
            const ordinal = slot - resourceStart(table);
            return ordinal < d.reserved_start or ordinal >= @as(u64, d.reserved_start) + d.reserved_count;
        }
        const CapacityCount = struct { total: u64 = 0, empty: u64 = 0 };
        fn freeCapacity(self: *Self, table: usize) CapacityCount {
            var count: CapacityCount = .{};
            const start = resourceStart(table);
            for (self.rows[start..][0..declarations[table].capacity], start..) |*row, slot| {
                if (!self.capacityEligible(table, slot) or row.fixed_declaration != null) continue;
                count.total += 1;
                if (row.uid == null) count.empty += 1;
            }
            return count;
        }
        pub fn saveNote(self: *Self, table: u32, work: *Workspace, uid: Uid, number: u64) !void {
            if (!self.tables[table].notifier.hasSubscribers()) return;
            if (work.note_count == work.notes.len) return error.WorkspaceTooSmall;
            work.notes[work.note_count] = .{ .uid = uid, .allocation_number = number };
            work.note_count += 1;
        }
        fn flush(self: *Self, table: u32, work: *Workspace) void {
            for (work.notes[0..work.note_count]) |note| self.tables[table].notifier.dispatch(note.uid, note.allocation_number);
            work.note_count = 0;
        }
        fn addBytes(total: u64, bytes: u64) !u64 {
            return std.math.add(u64, total, bytes) catch error.SizeOverflow;
        }
        fn replaceBytes(total: u64, old: u64, new: u64) !u64 {
            return addBytes(total - old, new);
        }

        const Range = struct { table: u32, parent: ?u32, start: usize, capacity: usize, attributes: types.Attributes };
        fn targetTable(self: *Self, target: T.Target) !u32 {
            switch (target) {
                .table => |t| {
                    if (t >= self.tables.len) return error.InvalidArgument;
                    return t;
                },
                .partition => |value| {
                    const position = (try self.index.find(value)) orelse return error.Nonexistent;
                    if (position.kind != .partition) return error.InvalidArgument;
                    return position.table;
                },
            }
        }
        fn resolveTarget(self: *Self, target: T.Target) !Range {
            switch (target) {
                .table => |t| {
                    if (t >= declarations.len) return error.InvalidArgument;
                    return .{ .table = t, .parent = null, .start = resourceStart(t), .capacity = declarations[t].capacity, .attributes = declarations[t].attributes };
                },
                .partition => |uid| {
                    const position = (try self.index.find(uid)) orelse return error.Nonexistent;
                    if (position.kind != .partition) return error.InvalidArgument;
                    const shell = &self.shells[position.slot];
                    return .{ .table = position.table, .parent = position.slot, .start = shell.start, .capacity = shell.capacity, .attributes = shell.attributes };
                },
            }
        }
        fn belongs(self: *Self, slot: usize, parent: ?u32) bool {
            return self.rows[slot].parent == parent;
        }
        fn allowedAt(self: *Self, slot: usize, target: Range) bool {
            if (target.parent != null) return true;
            const decl = declarations[target.table];
            const ordinal = slot - resourceStart(target.table);
            _ = self;
            return ordinal < decl.reserved_start or ordinal >= @as(u64, decl.reserved_start) + decl.reserved_count;
        }
        fn rotate(self: *Self, target: Range, allocation_bytes: u64, work: *Workspace) !u32 {
            if (work.candidates.len < target.capacity) return error.WorkspaceTooSmall;
            var count: usize = 0;
            for (self.rows[target.start..][0..target.capacity], target.start..) |*row, slot| {
                row.lock.lock();
                defer row.lock.unlock();
                self.flushWindowCache(row);
                if (row.parent != target.parent or row.fixed_declaration != null or row.uid == null) continue;
                work.candidates[count] = .{ .slot = @intCast(slot), .decision_bytes = row.resource.registration.?.allocation_bytes, .snapshot = self.snapshotLocked(row) };
                count += 1;
            }
            std.sort.heap(Candidate, work.candidates[0..count], SortContext{ .owner = self, .stage = .rotation }, candidateBefore);
            var i: usize = 0;
            while (i < count) : (i += 1) {
                const item = work.candidates[i];
                const row = &self.rows[item.slot];
                row.lock.lock();
                defer row.lock.unlock();
                if (!self.safe(row)) continue;
                if (comptime config.use_window_cache) try cacheStampAvailable(row);
                const registration = row.resource.registration.?;
                _ = try replaceBytes(self.tables[target.table].registered_bytes, registration.allocation_bytes, allocation_bytes);
                _ = try replaceBytes(self.tables[target.table].system_page_bytes, registration.allocatedPageBytes(), try region.requiredBytes(allocation_bytes));
                self.releaseLocked(item.slot, item.decision_bytes, false, false, work) catch |err| {
                    switch (err) {
                        error.InUse, error.Blocked, error.ChangedBeyondTolerance => continue,
                        else => return err,
                    }
                };
                return item.slot;
            }
            return error.Blocked;
        }

        pub const Subscription = struct { table: u32, handle: Notifier.Handle };
        pub fn subscribe(self: *Self, table: u32, callback: Notifier.Callback, context: ?*anyopaque) !Subscription {
            if (table >= self.tables.len) return error.InvalidArgument;
            self.tables[table].write.lock();
            defer self.tables[table].write.unlock();
            return .{ .table = table, .handle = try self.tables[table].notifier.subscribe(callback, context) };
        }
        pub fn unsubscribe(self: *Self, subscription: Subscription) !void {
            if (subscription.table >= self.tables.len) return error.Nonexistent;
            try self.tables[subscription.table].notifier.unsubscribe(subscription.handle);
        }
        fn updatePartition(self: *Self, uid: Uid, change: T.Update) !T.UpdateResult {
            return tree_operations.For(Self).update(self, uid, change);
        }
        fn closePartition(self: *Self, uid: Uid, forced: bool, work: *Workspace) !void {
            return tree_operations.For(Self).close(self, uid, forced, work);
        }
    };
}
