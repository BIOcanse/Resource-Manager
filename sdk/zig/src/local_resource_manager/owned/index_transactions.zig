const std = @import("std");

/// C2 owner transaction adapter. The owner supplies every workspace slice and
/// keeps its row/table locks while the returned Prepared value is alive.
pub fn C2(comptime Index: type) type {
    return struct {
        const Self = @This();

        pub const Put = struct {
            node_id: u32,
            uid: Index.uid_type,
            location: Index.Location,
        };

        /// All scratch and plans are supplied by the owner. `homes` and `nodes`
        /// need capacity for removal_count + put_count; the remaining slices
        /// need capacity for their corresponding operation count.
        pub const Workspace = struct {
            homes: []u32 = &.{},
            nodes: []u32 = &.{},
            publications: []Index.Publication = &.{},
            removals: []Index.RemovePlan = &.{},
            inserts: []Index.InsertPlan = &.{},
        };

        pub const Prepared = struct {
            index: *Index,
            work: *Workspace,
            home_count: usize = 0,
            node_count: usize = 0,
            removal_count: usize = 0,
            insert_count: usize = 0,
            active: bool = false,

            /// Releases an uncommitted transaction without changing the index.
            /// Calling abort after commit is a no-op.
            pub fn abort(self: *Prepared) void {
                if (!self.active) return;
                var i = self.node_count;
                while (i != 0) {
                    i -= 1;
                    self.index.unlockNode(self.work.nodes[i]);
                }
                i = self.home_count;
                while (i != 0) {
                    i -= 1;
                    self.index.unlockHome(self.work.homes[i]);
                }
                self.active = false;
            }

            /// Publishes all removals and insertions without a fallible step.
            /// The owner may call this only after all external release checks
            /// have passed. Index locks are released before this method returns.
            pub fn commit(self: *Prepared) void {
                if (!self.active) return;

                for (self.work.publications[0..self.home_count]) |publication| {
                    self.index.beginPublishLocked(publication);
                }
                for (self.work.removals[0..self.removal_count]) |plan| {
                    self.index.commitRemoveRetainingNodeLocked(plan);
                }
                for (self.work.inserts[0..self.insert_count]) |plan| {
                    self.index.commitInsertRetainingNodeLocked(plan);
                }
                for (self.work.publications[0..self.home_count]) |publication| {
                    self.index.finishPublishLocked(publication);
                }
                self.abort();
            }
        };

        pub fn prepare(
            index: *Index,
            removal_ids: []const u32,
            puts: []const Put,
            work: *Workspace,
        ) !Prepared {
            const operation_count = std.math.add(usize, removal_ids.len, puts.len) catch
                return error.WorkspaceTooSmall;
            if (work.homes.len < operation_count or work.nodes.len < operation_count or
                work.publications.len < operation_count or work.removals.len < removal_ids.len or
                work.inserts.len < puts.len)
            {
                return error.WorkspaceTooSmall;
            }
            if (index.nodes.len > Index.max_indexed_nodes or index.homes.len == 0 or
                index.homes.len > Index.max_homes)
            {
                return error.InvalidCapacity;
            }

            var home_count: usize = 0;
            var node_count: usize = 0;
            var removal_count: usize = 0;
            var insert_count: usize = 0;

            for (removal_ids) |node_id| {
                if (@as(usize, node_id) >= index.nodes.len) return error.InvalidNode;
                const node = &index.nodes[node_id];
                if (!node.linked.load(.seq_cst)) return error.NodeNotIndexed;
                const home_id = node.home_index.load(.seq_cst);
                if (@as(usize, home_id) >= index.homes.len) return error.InvalidIndexState;
                work.removals[removal_count] = .{ .node_id = node_id, .home_id = home_id };
                removal_count += 1;
                work.homes[home_count] = home_id;
                home_count += 1;
                work.nodes[node_count] = node_id;
                node_count += 1;
            }

            for (puts) |put| {
                if (@as(usize, put.node_id) >= index.nodes.len) return error.InvalidNode;
                const home_id = try index.homeFor(put.uid);
                work.inserts[insert_count] = .{
                    .node_id = put.node_id,
                    .home_id = home_id,
                    .uid = put.uid,
                    .location = put.location,
                };
                insert_count += 1;
                work.homes[home_count] = home_id;
                home_count += 1;
                work.nodes[node_count] = put.node_id;
                node_count += 1;
            }

            std.mem.sortUnstable(Index.RemovePlan, work.removals[0..removal_count], {}, lessRemove);
            if (removal_count > 1) {
                for (1..removal_count) |i| {
                    if (work.removals[i - 1].node_id == work.removals[i].node_id)
                        return error.InvalidTransaction;
                }
            }
            std.mem.sortUnstable(Index.InsertPlan, work.inserts[0..insert_count], {}, lessInsert);
            if (insert_count > 1) {
                for (1..insert_count) |i| {
                    if (work.inserts[i - 1].node_id == work.inserts[i].node_id)
                        return error.InvalidTransaction;
                }
            }
            std.mem.sortUnstable(u32, work.homes[0..home_count], {}, lessU32);
            home_count = uniqueU32(work.homes[0..home_count]);
            std.mem.sortUnstable(u32, work.nodes[0..node_count], {}, lessU32);
            node_count = uniqueU32(work.nodes[0..node_count]);

            var prepared = Prepared{
                .index = index,
                .work = work,
                .home_count = 0,
                .node_count = 0,
                .removal_count = removal_count,
                .insert_count = insert_count,
                .active = true,
            };
            errdefer prepared.abort();

            for (work.homes[0..home_count]) |home_id| {
                try index.lockHome(home_id);
                prepared.home_count += 1;
            }
            for (work.nodes[0..node_count]) |node_id| {
                try index.lockNode(node_id);
                prepared.node_count += 1;
            }

            for (work.homes[0..home_count], 0..) |home_id, i| {
                work.publications[i] = try index.preflightPublishLocked(home_id);
            }
            for (work.inserts[0..insert_count]) |plan| {
                const home_position = homePosition(work.homes[0..home_count], plan.home_id) orelse
                    return error.InvalidIndexState;
                work.publications[home_position].has_inserts = true;
            }
            for (work.publications[0..home_count]) |publication| {
                if (publication.has_inserts) try index.preflightHomeLocked(publication.home_id);
            }

            for (work.removals[0..removal_count]) |*plan| {
                const node = &index.nodes[plan.node_id];
                if (!node.linked.load(.seq_cst) or node.home_index.load(.seq_cst) != plan.home_id)
                    return error.ClosureChanged;
                plan.* = try index.preflightRemoveNodeLocked(plan.node_id, plan.home_id);
            }

            for (work.inserts[0..insert_count]) |*plan| {
                const removed_home = removalHome(work.removals[0..removal_count], plan.node_id);
                if (plan.location.kind == .partition and (try index.find(plan.uid)) != null)
                    return error.RetryExisting;
                plan.* = try index.preflightInsertNodeLocked(
                    plan.node_id,
                    plan.uid,
                    plan.location,
                    plan.home_id,
                    removed_home,
                );
            }

            return prepared;
        }

        fn lessU32(_: void, left: u32, right: u32) bool {
            return left < right;
        }

        fn uniqueU32(values: []u32) usize {
            if (values.len < 2) return values.len;
            var used: usize = 1;
            for (values[1..]) |value| {
                if (value == values[used - 1]) continue;
                values[used] = value;
                used += 1;
            }
            return used;
        }

        fn lessRemove(_: void, left: Index.RemovePlan, right: Index.RemovePlan) bool {
            return left.node_id < right.node_id;
        }

        fn lessInsert(_: void, left: Index.InsertPlan, right: Index.InsertPlan) bool {
            return left.node_id < right.node_id;
        }

        fn homePosition(homes: []const u32, home_id: u32) ?usize {
            var low: usize = 0;
            var high = homes.len;
            while (low < high) {
                const middle = low + (high - low) / 2;
                if (homes[middle] < home_id) {
                    low = middle + 1;
                } else {
                    high = middle;
                }
            }
            if (low == homes.len or homes[low] != home_id) return null;
            return low;
        }

        fn removalHome(removals: []const Index.RemovePlan, node_id: u32) ?u32 {
            var low: usize = 0;
            var high = removals.len;
            while (low < high) {
                const middle = low + (high - low) / 2;
                if (removals[middle].node_id < node_id) {
                    low = middle + 1;
                } else {
                    high = middle;
                }
            }
            if (low == removals.len or removals[low].node_id != node_id) return null;
            return removals[low].home_id;
        }
    };
}

/// B2 transaction support is defined alongside the C2 adapter so both
/// candidates share the same owner-facing Put/Prepared lifecycle.
pub fn B2(comptime Index: type) type {
    return struct {
        pub const Put = struct {
            node_id: u32,
            uid: Index.uid_type,
            location: Index.Location,
        };

        pub const Operation = struct {
            start: u32,
            offset: usize = 0,
            node_id: u32,
            ordinal: u32,
            is_put: bool,
            group_root: u32,
            coverage_count: u32 = 0,
            expanded: bool = false,
        };

        /// A linear, non-wrapping physical bucket interval.
        pub const Segment = struct {
            start: u32,
            count: u32,
        };

        /// Explicit work counters are used by correctness/profile runs only.
        /// The regular prepare entry point compiles all counter updates out.
        pub const PrepareStats = struct {
            sampled_prefix_buckets: usize = 0,
            operation_segments: usize = 0,
            merged_segments: usize = 0,
            closure_buckets_enumerated: usize = 0,
            closure_validation_passes: usize = 0,
            closure_validation_visits: usize = 0,
            publication_version_visits: usize = 0,
            removal_shift_prefix_visits: usize = 0,
            simulation_bucket_visits: usize = 0,
            closure_expansion_rounds: usize = 0,
            closure_expansion_bucket_visits: usize = 0,
            closure_rebuild_bucket_visits: usize = 0,
        };

        /// B2 batch scratch follows the number of operations and their actual
        /// merged bucket closure. No slice must scale with global B or N.
        pub const Workspace = struct {
            operations: []Operation = &.{},
            segments: []Segment = &.{},
            indices: []u32 = &.{},
            versions: []u64 = &.{},
            removals: []Index.RemovePlan = &.{},
            inserts: []Index.InsertPlan = &.{},
            simulation_nodes: []u32 = &.{},
            simulation_uids: []Index.uid_type = &.{},
            simulation_node_ids: []u32 = &.{},
            simulation_backlinks: []u32 = &.{},
        };

        pub const Prepared = struct {
            index: *Index,
            work: *Workspace,
            indices: []const u32,
            versions: []const u64,
            removal_count: usize = 0,
            insert_count: usize = 0,
            active: bool = true,
            locks_held: bool = false,

            pub fn abort(self: *Prepared) void {
                if (!self.active) return;
                if (self.locks_held) self.index.unlockClosurePrepared(self.indices);
                self.locks_held = false;
                self.active = false;
            }

            /// The caller performs any external no-fail action only after
            /// prepare succeeds. Every index write below is preflighted and
            /// uses no allocator or fallible operation.
            pub fn commit(self: *Prepared) void {
                if (!self.active) return;
                if (self.indices.len == 0) {
                    self.abort();
                    return;
                }
                self.index.beginPublishLocked(self.indices, self.versions);
                self.index.waitReadersLocked(self.indices);
                for (self.work.removals[0..self.removal_count]) |plan| self.index.commitRemoveLocked(plan);
                for (self.work.inserts[0..self.insert_count]) |plan| {
                    self.index.commitInsertRecomputedLocked(plan, self.indices);
                }
                self.index.finishPublishLocked(self.indices, self.versions);
                self.abort();
            }
        };

        const Relative = struct {
            phase: u8,
            offset: usize,
            key: Index.uid_type,
        };

        const MissingProbe = struct {
            node_id: u32,
            bucket_id: u32,
        };

        pub fn prepare(
            index: *Index,
            removal_ids: []const u32,
            puts: []const Put,
            work: *Workspace,
        ) !Prepared {
            return prepareImpl(false, index, removal_ids, puts, work, {});
        }

        /// Same preflight as `prepare`, with counters written to caller-owned
        /// storage for non-timed correctness runs.
        pub fn prepareWithStats(
            index: *Index,
            removal_ids: []const u32,
            puts: []const Put,
            work: *Workspace,
            stats: *PrepareStats,
        ) !Prepared {
            stats.* = .{};
            return prepareImpl(true, index, removal_ids, puts, work, stats);
        }

        fn prepareImpl(
            comptime track_stats: bool,
            index: *Index,
            removal_ids: []const u32,
            puts: []const Put,
            work: *Workspace,
            stats: if (track_stats) *PrepareStats else void,
        ) !Prepared {
            if (index.nodes.len > Index.max_indexed_nodes or index.buckets.len < 2 or
                index.buckets.len > Index.max_buckets or index.nodes.len >= index.buckets.len)
            {
                return error.InvalidCapacity;
            }
            const operation_count = std.math.add(usize, removal_ids.len, puts.len) catch
                return error.WorkspaceTooSmall;
            const segment_capacity = std.math.mul(usize, operation_count, 2) catch
                return error.WorkspaceTooSmall;
            if (work.removals.len < removal_ids.len or work.inserts.len < puts.len)
                return error.WorkspaceTooSmall;
            if (operation_count == 0) {
                return .{
                    .index = index,
                    .work = work,
                    .indices = &.{},
                    .versions = &.{},
                    .removal_count = 0,
                    .insert_count = 0,
                    .active = true,
                };
            }
            if (work.operations.len < operation_count or work.segments.len < segment_capacity)
                return error.WorkspaceTooSmall;

            for (removal_ids, 0..) |node_id, i| {
                work.removals[i] = .{ .node_id = node_id };
            }
            std.mem.sortUnstable(Index.RemovePlan, work.removals[0..removal_ids.len], {}, lessRemove);
            if (removal_ids.len > 1) {
                for (1..removal_ids.len) |i| {
                    if (work.removals[i - 1].node_id == work.removals[i].node_id)
                        return error.InvalidTransaction;
                }
            }

            for (puts, 0..) |put, i| {
                if (@as(usize, put.node_id) >= index.nodes.len) return error.InvalidNode;
                _ = try index.homeFor(put.uid);
                work.inserts[i] = .{
                    .node_id = put.node_id,
                    .at = 0,
                    .empty = 0,
                    .uid = put.uid,
                    .location = put.location,
                };
            }
            std.mem.sortUnstable(Index.InsertPlan, work.inserts[0..puts.len], {}, lessInsert);
            if (puts.len > 1) {
                for (1..puts.len) |i| {
                    if (work.inserts[i - 1].node_id == work.inserts[i].node_id)
                        return error.InvalidTransaction;
                }
            }

            var operation_used: usize = 0;
            for (work.removals[0..removal_ids.len]) |plan| {
                work.operations[operation_used] = .{
                    .start = try index.positionForNode(plan.node_id),
                    .node_id = plan.node_id,
                    .ordinal = @intCast(operation_used),
                    .is_put = false,
                    .group_root = @intCast(operation_used),
                };
                operation_used += 1;
            }
            for (work.inserts[0..puts.len]) |plan| {
                work.operations[operation_used] = .{
                    .start = try index.homeFor(plan.uid),
                    .node_id = plan.node_id,
                    .ordinal = @intCast(operation_used),
                    .is_put = true,
                    .group_root = @intCast(operation_used),
                };
                operation_used += 1;
            }

            const initial_operation = work.operations[0];
            const initial_prefix = try sampleOperationPrefix(index, initial_operation);
            if (!initial_operation.is_put) {
                work.operations[0].start = initial_prefix.start;
            }
            if (comptime track_stats) stats.sampled_prefix_buckets += initial_prefix.count;

            const anchor = advanceBucket(index, initial_prefix.start, initial_prefix.count - 1);
            for (work.operations[0..operation_count]) |*operation| {
                operation.offset = distance(index.buckets.len, anchor, operation.start);
            }
            std.mem.sortUnstable(Operation, work.operations[0..operation_count], {}, lessOperation);

            var segment_count: usize = 0;
            var operation_index: usize = 0;
            while (operation_index < operation_count) {
                const operation = work.operations[operation_index];
                const prefix = if (operation.ordinal == 0 and operation.start == initial_prefix.start)
                    initial_prefix
                else
                    try sampleOperationPrefix(index, operation);

                if (!operation.is_put and prefix.start != operation.start)
                    return error.ClosureChanged;
                if (comptime track_stats) {
                    if (operation.ordinal != 0 or operation.start != initial_prefix.start)
                        stats.sampled_prefix_buckets += prefix.count;
                }

                const empty_endpoint = advanceBucket(index, prefix.start, prefix.count - 1);
                const end_offset = distance(index.buckets.len, anchor, empty_endpoint);
                const group_end_offset = if (end_offset < operation.offset)
                    index.buckets.len - 1
                else
                    end_offset;
                var group_end = operation_index;
                const group_root = operation.ordinal;
                while (group_end < operation_count and work.operations[group_end].offset <= group_end_offset) : (group_end += 1) {
                    work.operations[group_end].group_root = group_root;
                }
                if (group_end == operation_index) return error.InvalidIndexState;

                work.operations[operation_index].coverage_count = @intCast(prefix.count);
                segment_count += 1;
                operation_index = group_end;
            }
            if (comptime track_stats) stats.operation_segments = segment_count;

            std.mem.sortUnstable(Operation, work.operations[0..operation_count], {}, lessOperationOrdinal);
            var merged_segment_count = try rebuildSegments(index, work, operation_count);
            if (comptime track_stats) stats.merged_segments = merged_segment_count;

            var expanded_puts: usize = 0;
            var rebuilding = false;
            while (true) {
                const closed_len = try enumerateSegments(index, work, merged_segment_count);
                const closed = work.indices[0..closed_len];
                if (comptime track_stats) {
                    stats.closure_buckets_enumerated = closed.len;
                    if (rebuilding) stats.closure_rebuild_bucket_visits += closed.len;
                }
                if (closed.len == 0) return error.InvalidTransaction;
                const simulation_node_capacity = std.math.add(usize, closed.len, puts.len) catch
                    return error.WorkspaceTooSmall;
                if (work.versions.len < closed.len or
                    work.simulation_nodes.len < closed.len or
                    work.simulation_uids.len < closed.len or
                    work.simulation_node_ids.len < simulation_node_capacity or
                    work.simulation_backlinks.len < simulation_node_capacity)
                    return error.WorkspaceTooSmall;

                try index.lockClosure(closed);
                var prepared = Prepared{
                    .index = index,
                    .work = work,
                    .indices = closed,
                    .versions = work.versions[0..closed.len],
                    .removal_count = removal_ids.len,
                    .insert_count = puts.len,
                    .active = true,
                    .locks_held = true,
                };
                errdefer prepared.abort();

                if (comptime track_stats) {
                    stats.closure_validation_passes += 1;
                    stats.closure_validation_visits += closed.len;
                }
                try index.preflightPublishLocked(closed, work.versions[0..closed.len]);
                if (comptime track_stats) {
                    stats.closure_validation_passes += 1;
                    stats.closure_validation_visits += closed.len;
                    stats.publication_version_visits += closed.len;
                }
                if (removal_ids.len != 0) {
                    for (work.removals[0..removal_ids.len], 0..) |plan, i| {
                        if (index.nodes[plan.node_id].bucket_index.load(.seq_cst) != work.operations[i].start)
                            return error.ClosureChanged;
                    }
                    if (comptime track_stats) {
                        var batch_stats: Index.BatchPreflightStats = .{};
                        try index.prepareRemoveBatchWithStatsLocked(
                            work.removals[0..removal_ids.len],
                            closed,
                            &batch_stats,
                        );
                        stats.closure_validation_passes += 1;
                        stats.closure_validation_visits += batch_stats.closure_validation_visits;
                        stats.removal_shift_prefix_visits += batch_stats.removal_shift_prefix_visits;
                    } else {
                        try index.prepareRemoveBatchLocked(work.removals[0..removal_ids.len], closed);
                    }
                }
                for (work.inserts[0..puts.len]) |plan| {
                    const removed = removalIndex(work.removals[0..removal_ids.len], plan.node_id) != null;
                    const node = &index.nodes[plan.node_id];
                    if (removed) {
                        if (!node.linked.load(.seq_cst) or node.bucket_index.load(.seq_cst) == Index.no_bucket)
                            return error.NodeNotIndexed;
                    } else if (node.linked.load(.seq_cst) or node.bucket_index.load(.seq_cst) != Index.no_bucket) {
                        return error.NodeAlreadyIndexed;
                    }
                }

                const simulation_node_count = try initializeSimulation(index, closed, work, puts.len);
                if (comptime track_stats) stats.simulation_bucket_visits += closed.len;
                for (work.removals[0..removal_ids.len]) |plan| {
                    if (comptime track_stats) {
                        try simulateRemove(true, index, plan.node_id, closed, work, simulation_node_count, stats);
                    } else {
                        try simulateRemove(false, index, plan.node_id, closed, work, simulation_node_count, {});
                    }
                }

                var missing: ?MissingProbe = null;
                for (work.inserts[0..puts.len]) |plan| {
                    const result = if (comptime track_stats)
                        try simulateInsert(true, index, plan, closed, work, simulation_node_count, stats)
                    else
                        try simulateInsert(false, index, plan, closed, work, simulation_node_count, {});
                    if (result) |needed| {
                        missing = needed;
                        break;
                    }
                }
                if (missing == null) return prepared;

                prepared.abort();
                if (expanded_puts >= puts.len) return error.ClosureChanged;
                const needed = missing.?;
                const put_position = insertIndex(work.inserts[0..puts.len], needed.node_id) orelse
                    return error.InvalidIndexState;
                const put_operation_index = removal_ids.len + put_position;
                const put_operation = &work.operations[put_operation_index];
                if (!put_operation.is_put or put_operation.expanded) return error.ClosureChanged;
                const root_ordinal = put_operation.group_root;
                if (@as(usize, root_ordinal) >= operation_count) return error.InvalidIndexState;
                const root_operation = &work.operations[root_ordinal];
                if (root_operation.ordinal != root_ordinal or root_operation.group_root != root_ordinal)
                    return error.InvalidIndexState;

                const sample = try index.samplePostSimulationPrefix(
                    needed.bucket_id,
                    closed,
                    work.simulation_nodes[0..closed.len],
                );
                if (comptime track_stats) {
                    stats.closure_expansion_rounds += 1;
                    stats.closure_expansion_bucket_visits += sample.bucket_visits;
                }
                const expanded_endpoint = advanceBucket(
                    index,
                    sample.prefix.start,
                    sample.prefix.count - 1,
                );
                const expanded_count = distance(index.buckets.len, root_operation.start, expanded_endpoint) + 1;
                if (expanded_count <= root_operation.coverage_count or expanded_count > index.buckets.len)
                    return error.ClosureChanged;
                root_operation.coverage_count = @intCast(expanded_count);
                put_operation.expanded = true;
                expanded_puts += 1;
                merged_segment_count = try rebuildSegments(index, work, operation_count);
                if (comptime track_stats) stats.merged_segments = merged_segment_count;
                rebuilding = true;
            }
        }

        fn sampleOperationPrefix(index: *const Index, operation: Operation) !Index.Prefix {
            if (operation.is_put) return index.samplePrefix(operation.start, null);
            return index.sampleRemovePrefix(operation.node_id);
        }

        fn appendCircularSegments(
            segments: []Segment,
            used: usize,
            start: u32,
            count: usize,
            bucket_count: usize,
        ) !usize {
            if (count == 0 or count > bucket_count or @as(usize, start) >= bucket_count)
                return error.InvalidClosure;
            if (used == segments.len) return error.WorkspaceTooSmall;

            const first_count = @min(count, bucket_count - @as(usize, start));
            segments[used] = .{ .start = start, .count = @intCast(first_count) };
            var next_used = used + 1;
            if (first_count < count) {
                if (next_used == segments.len) return error.WorkspaceTooSmall;
                segments[next_used] = .{ .start = 0, .count = @intCast(count - first_count) };
                next_used += 1;
            }
            return next_used;
        }

        fn rebuildSegments(index: *const Index, work: *Workspace, operation_count: usize) !usize {
            var segment_count: usize = 0;
            for (work.operations[0..operation_count]) |operation| {
                if (operation.ordinal != operation.group_root) continue;
                if (operation.coverage_count == 0) return error.InvalidIndexState;
                segment_count = try appendCircularSegments(
                    work.segments,
                    segment_count,
                    operation.start,
                    operation.coverage_count,
                    index.buckets.len,
                );
            }
            std.mem.sortUnstable(Segment, work.segments[0..segment_count], {}, lessSegment);
            return mergeSegments(work.segments[0..segment_count]);
        }

        fn enumerateSegments(index: *const Index, work: *Workspace, segment_count: usize) !usize {
            var required: usize = 0;
            for (work.segments[0..segment_count]) |segment| {
                required = std.math.add(usize, required, segment.count) catch return error.InvalidClosure;
            }
            if (required > index.buckets.len) return error.InvalidClosure;
            if (work.indices.len < required) return error.WorkspaceTooSmall;

            var used: usize = 0;
            for (work.segments[0..segment_count]) |segment| {
                for (0..segment.count) |offset| {
                    work.indices[used] = segment.start + @as(u32, @intCast(offset));
                    used += 1;
                }
            }
            return used;
        }

        fn mergeSegments(segments: []Segment) usize {
            var merged: usize = 0;
            for (segments) |segment| {
                if (merged != 0) {
                    const previous = &segments[merged - 1];
                    const previous_end = @as(usize, previous.start) + previous.count;
                    const segment_end = @as(usize, segment.start) + segment.count;
                    if (@as(usize, segment.start) <= previous_end) {
                        if (segment_end > previous_end) previous.count = @intCast(segment_end - previous.start);
                        continue;
                    }
                }
                segments[merged] = segment;
                merged += 1;
            }
            return merged;
        }

        fn advanceBucket(index: *const Index, start: u32, amount: usize) u32 {
            const bucket_count = index.buckets.len;
            return @intCast((@as(usize, start) + amount) % bucket_count);
        }

        fn lessOperation(_: void, left: Operation, right: Operation) bool {
            if (left.offset != right.offset) return left.offset < right.offset;
            return left.ordinal < right.ordinal;
        }

        fn lessOperationOrdinal(_: void, left: Operation, right: Operation) bool {
            return left.ordinal < right.ordinal;
        }

        fn lessSegment(_: void, left: Segment, right: Segment) bool {
            if (left.start != right.start) return left.start < right.start;
            return left.count < right.count;
        }

        fn initializeSimulation(
            index: *const Index,
            indices: []const u32,
            work: *Workspace,
            insert_count: usize,
        ) !usize {
            var node_count: usize = 0;
            for (indices, 0..) |bucket_id, position| {
                const node_id = index.buckets[bucket_id].node_id.load(.seq_cst);
                work.simulation_nodes[position] = node_id;
                if (node_id == Index.no_bucket) continue;
                if (@as(usize, node_id) >= index.nodes.len) return error.InvalidIndexState;
                const uid = (try index.uidAt(node_id)) orelse return error.InvalidIndexState;
                if (!index.nodes[node_id].linked.load(.seq_cst) or
                    index.nodes[node_id].bucket_index.load(.seq_cst) != bucket_id)
                {
                    return error.InvalidIndexState;
                }
                work.simulation_uids[position] = uid;
                work.simulation_node_ids[node_count] = node_id;
                node_count += 1;
            }

            for (work.inserts[0..insert_count]) |plan| {
                work.simulation_node_ids[node_count] = plan.node_id;
                node_count += 1;
            }
            std.mem.sortUnstable(u32, work.simulation_node_ids[0..node_count], {}, lessU32);
            node_count = uniqueU32(work.simulation_node_ids[0..node_count]);
            @memset(work.simulation_backlinks[0..node_count], Index.no_bucket);

            for (indices, 0..) |bucket_id, position| {
                const node_id = work.simulation_nodes[position];
                if (node_id == Index.no_bucket) continue;
                const slot = findSimulationNode(work, node_count, node_id) orelse
                    return error.InvalidIndexState;
                if (work.simulation_backlinks[slot] != Index.no_bucket)
                    return error.InvalidIndexState;
                work.simulation_backlinks[slot] = bucket_id;
            }
            return node_count;
        }

        fn simulateRemove(
            comptime track_stats: bool,
            index: *const Index,
            node_id: u32,
            indices: []const u32,
            work: *Workspace,
            simulation_node_count: usize,
            stats: if (track_stats) *PrepareStats else void,
        ) !void {
            if (@as(usize, node_id) >= index.nodes.len) return error.InvalidNode;
            var hole = try simulationBacklink(work, simulation_node_count, node_id);
            if (hole == Index.no_bucket) return error.ClosureChanged;
            var source = nextBucket(index, hole);
            var reached_stop = false;
            for (0..index.buckets.len) |_| {
                if (comptime track_stats) stats.simulation_bucket_visits += 1;
                const source_position = findIndex(indices, source) orelse return error.ClosureChanged;
                const moved = work.simulation_nodes[source_position];
                if (moved == Index.no_bucket) {
                    reached_stop = true;
                    break;
                }
                const moved_uid = work.simulation_uids[source_position];
                if (try index.homeFor(moved_uid) == source) {
                    reached_stop = true;
                    break;
                }
                const hole_position = findIndex(indices, hole) orelse return error.ClosureChanged;
                work.simulation_nodes[hole_position] = moved;
                work.simulation_uids[hole_position] = moved_uid;
                try setSimulationBacklink(work, simulation_node_count, moved, hole);
                hole = source;
                source = nextBucket(index, source);
            }
            if (!reached_stop) return error.InvalidIndexState;
            const hole_position = findIndex(indices, hole) orelse return error.ClosureChanged;
            work.simulation_nodes[hole_position] = Index.no_bucket;
            try setSimulationBacklink(work, simulation_node_count, node_id, Index.no_bucket);
        }

        fn simulateInsert(
            comptime track_stats: bool,
            index: *const Index,
            plan: Index.InsertPlan,
            indices: []const u32,
            work: *Workspace,
            simulation_node_count: usize,
            stats: if (track_stats) *PrepareStats else void,
        ) !?MissingProbe {
            const home = try index.homeFor(plan.uid);
            const target = Relative{ .phase = 1, .offset = 0, .key = plan.uid };
            var cursor = home;
            var previous: ?Relative = null;
            var insertion_at: ?u32 = null;
            var empty: ?u32 = null;
            for (0..index.buckets.len) |_| {
                if (comptime track_stats) stats.simulation_bucket_visits += 1;
                const position = findIndex(indices, cursor) orelse return .{
                    .node_id = plan.node_id,
                    .bucket_id = cursor,
                };
                const current_node_id = work.simulation_nodes[position];
                if (current_node_id == Index.no_bucket) {
                    empty = cursor;
                    break;
                }
                if (@as(usize, current_node_id) >= index.nodes.len) return error.InvalidIndexState;
                const current_uid = work.simulation_uids[position];
                if (current_uid.eql(plan.uid)) {
                    if (plan.location.kind == .partition) return error.RetryExisting;
                    return error.DuplicateUid;
                }
                const current = relative(index, home, cursor, current_uid) catch return error.InvalidIndexState;
                if (previous) |prior| {
                    if (compareRelative(prior, current) != .lt) return error.InvalidIndexState;
                }
                if (insertion_at == null and compareRelative(current, target) == .gt) insertion_at = cursor;
                previous = current;
                cursor = nextBucket(index, cursor);
            }
            const end = empty orelse return error.IndexFull;
            const at = insertion_at orelse end;
            var destination = end;
            while (destination != at) {
                if (comptime track_stats) stats.simulation_bucket_visits += 1;
                const source = previousBucket(index, destination);
                const source_position = findIndex(indices, source) orelse return error.ClosureChanged;
                const destination_position = findIndex(indices, destination) orelse return error.ClosureChanged;
                const moved = work.simulation_nodes[source_position];
                if (moved == Index.no_bucket) return error.InvalidIndexState;
                work.simulation_nodes[destination_position] = moved;
                work.simulation_uids[destination_position] = work.simulation_uids[source_position];
                try setSimulationBacklink(work, simulation_node_count, moved, destination);
                destination = source;
            }
            const at_position = findIndex(indices, at) orelse return error.ClosureChanged;
            work.simulation_nodes[at_position] = plan.node_id;
            work.simulation_uids[at_position] = plan.uid;
            try setSimulationBacklink(work, simulation_node_count, plan.node_id, at);
            return null;
        }

        fn findSimulationNode(work: *const Workspace, node_count: usize, node_id: u32) ?usize {
            var low: usize = 0;
            var high = node_count;
            while (low < high) {
                const middle = low + (high - low) / 2;
                if (work.simulation_node_ids[middle] < node_id) {
                    low = middle + 1;
                } else {
                    high = middle;
                }
            }
            if (low == node_count or work.simulation_node_ids[low] != node_id) return null;
            return low;
        }

        fn simulationBacklink(work: *const Workspace, node_count: usize, node_id: u32) !u32 {
            const slot = findSimulationNode(work, node_count, node_id) orelse
                return error.InvalidIndexState;
            return work.simulation_backlinks[slot];
        }

        fn setSimulationBacklink(
            work: *Workspace,
            node_count: usize,
            node_id: u32,
            bucket_id: u32,
        ) !void {
            const slot = findSimulationNode(work, node_count, node_id) orelse
                return error.InvalidIndexState;
            work.simulation_backlinks[slot] = bucket_id;
        }

        fn relative(index: *const Index, start: u32, position: u32, key: Index.uid_type) !Relative {
            const home = try index.homeFor(key);
            const home_distance = distance(index.buckets.len, home, position);
            const start_distance = distance(index.buckets.len, start, position);
            return .{
                .phase = if (home_distance > start_distance) 0 else if (home_distance == start_distance) 1 else 2,
                .offset = distance(index.buckets.len, start, home),
                .key = key,
            };
        }

        fn compareRelative(left: Relative, right: Relative) std.math.Order {
            if (left.phase != right.phase) return std.math.order(left.phase, right.phase);
            if (left.offset != right.offset) return std.math.order(left.offset, right.offset);
            return left.key.order(right.key);
        }

        fn lessU32(_: void, left: u32, right: u32) bool {
            return left < right;
        }

        fn lessRemove(_: void, left: Index.RemovePlan, right: Index.RemovePlan) bool {
            return left.node_id < right.node_id;
        }

        fn lessInsert(_: void, left: Index.InsertPlan, right: Index.InsertPlan) bool {
            return left.node_id < right.node_id;
        }

        fn insertIndex(inserts: []const Index.InsertPlan, node_id: u32) ?usize {
            var low: usize = 0;
            var high = inserts.len;
            while (low < high) {
                const middle = low + (high - low) / 2;
                if (inserts[middle].node_id < node_id) {
                    low = middle + 1;
                } else {
                    high = middle;
                }
            }
            if (low == inserts.len or inserts[low].node_id != node_id) return null;
            return low;
        }

        fn uniqueU32(values: []u32) usize {
            if (values.len < 2) return values.len;
            var used: usize = 1;
            for (values[1..]) |value| {
                if (value == values[used - 1]) continue;
                values[used] = value;
                used += 1;
            }
            return used;
        }

        fn removalIndex(removals: []const Index.RemovePlan, node_id: u32) ?usize {
            var low: usize = 0;
            var high = removals.len;
            while (low < high) {
                const middle = low + (high - low) / 2;
                if (removals[middle].node_id < node_id) {
                    low = middle + 1;
                } else {
                    high = middle;
                }
            }
            if (low == removals.len or removals[low].node_id != node_id) return null;
            return low;
        }

        fn findIndex(indices: []const u32, bucket_id: u32) ?usize {
            var low: usize = 0;
            var high = indices.len;
            while (low < high) {
                const middle = low + (high - low) / 2;
                if (indices[middle] < bucket_id) {
                    low = middle + 1;
                } else {
                    high = middle;
                }
            }
            if (low == indices.len or indices[low] != bucket_id) return null;
            return low;
        }

        fn nextBucket(index: *const Index, bucket_id: u32) u32 {
            return if (@as(usize, bucket_id) + 1 == index.buckets.len) 0 else bucket_id + 1;
        }

        fn previousBucket(index: *const Index, bucket_id: u32) u32 {
            return if (bucket_id == 0) @intCast(index.buckets.len - 1) else bucket_id - 1;
        }

        fn distance(size: usize, from: u32, to: u32) usize {
            if (to >= from) return @as(usize, to - from);
            return size - @as(usize, from) + @as(usize, to);
        }
    };
}
