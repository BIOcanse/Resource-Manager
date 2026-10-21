//! Explicit full-owner consistency audit. The caller supplies every scratch
//! array; this module performs no allocation and is never used on hot paths.
const std = @import("std");
const types = @import("types.zig");
const memory = @import("../memory_region.zig");
const index_limits = @import("index.zig");

pub fn For(comptime Owner: type) type {
    const Uid = Owner.uid_type;

    return struct {
        pub const Event = struct {
            position: usize,
            shell: u32,
            depth: u32,
            entering: bool,
        };

        /// All slices are caller-owned and must remain exclusive for the call.
        /// Required lengths are checked before any owner lock is acquired.
        pub const Workspace = struct {
            shell_queue: []u32,
            shell_marks: []u8,
            depth: []u32,
            events: []Event,
            stack: []u32,
            table_views: []types.TableView,
            fixed_marks: []u8,
            allocations: []u64,
        };

        pub const Stats = struct {
            resources: usize,
            partitions: usize,
            fixed_declarations: usize,
            tables: usize,
            active_resources: usize = 0,
            active_partitions: usize = 0,
            free_partitions: usize = 0,
            fixed_rows: usize = 0,
            fixed_identity_lookups: usize = 0,
            partition_links: usize = 0,
            interval_events: usize = 0,
            event_sort_comparisons: usize = 0,
            allocation_numbers: usize = 0,
            allocation_sort_comparisons: usize = 0,
            registered_bytes: u128 = 0,
            system_page_bytes: u128 = 0,
            index_home_body: usize = 0,
            index_node_body: usize = 0,
            index_neighbor_payload_loads: usize = 0,
        };

        pub fn run(owner: *Owner, workspace: *Workspace) !Stats {
            const resource_count = Owner.resource_capacity;
            const partition_count = Owner.partition_capacity;
            const table_count = Owner.table_count;
            const fixed_count = Owner.fixed_declarations.len;
            const node_count = std.math.add(usize, resource_count, partition_count) catch return error.InvalidState;
            const event_capacity = std.math.mul(usize, partition_count, 2) catch return error.InvalidState;

            if (owner.rows.len != resource_count or owner.shells.len != partition_count or
                owner.tables.len != table_count or owner.bindings.len != fixed_count or
                owner.index.nodes.len != node_count or owner.index.nodes.len > index_limits.max_indexed_nodes or
                owner.index.homes.len == 0 or owner.index.homes.len > index_limits.max_homes)
            {
                return error.InvalidState;
            }
            if (workspace.shell_queue.len < partition_count or workspace.shell_marks.len < partition_count or
                workspace.depth.len < partition_count or workspace.events.len < event_capacity or
                workspace.stack.len < partition_count or workspace.table_views.len < table_count or
                workspace.fixed_marks.len < fixed_count or workspace.allocations.len < node_count)
            {
                return error.WorkspaceTooSmall;
            }

            var stats = Stats{
                .resources = resource_count,
                .partitions = partition_count,
                .fixed_declarations = fixed_count,
                .tables = table_count,
            };

            var table_locks: usize = 0;
            var row_locks: usize = 0;
            var home_locks: usize = 0;
            defer unlockAll(owner, &table_locks, &row_locks, &home_locks);

            while (table_locks < table_count) : (table_locks += 1) owner.tables[table_locks].write.lock();
            while (row_locks < resource_count) : (row_locks += 1) owner.rows[row_locks].lock.lock();
            while (home_locks < owner.index.homes.len) : (home_locks += 1) {
                owner.index.lockHome(@intCast(home_locks)) catch return error.InvalidState;
            }

            const epoch = owner.epoch.load(.acquire);
            if (epoch == 0) return error.InvalidState;
            @memset(workspace.shell_marks[0..partition_count], 0);
            @memset(workspace.fixed_marks[0..fixed_count], 0);
            @memset(workspace.allocations[0..node_count], 0);
            for (workspace.table_views[0..table_count]) |*view| view.* = .{ .registered_bytes = 0, .system_page_bytes = 0 };

            // This catches odd publications before fixed-parent lookups and
            // leaves every index writer excluded until the audit completes.
            const index_stats = owner.index.audit() catch return error.InvalidState;
            stats.index_home_body = index_stats.home_body;
            stats.index_node_body = index_stats.node_body;
            stats.index_neighbor_payload_loads = index_stats.neighbor_payload_loads;

            try auditShells(owner, workspace, &stats, epoch);
            try auditRows(owner, workspace, &stats, epoch, node_count);
            try auditFixedBindings(owner, workspace, &stats);
            try auditAllocationNumbers(owner, workspace, &stats, node_count);

            for (workspace.table_views[0..table_count], owner.tables[0..table_count]) |actual, table| {
                if (actual.registered_bytes != table.registered_bytes or
                    actual.system_page_bytes != table.system_page_bytes)
                {
                    return error.InvalidState;
                }
                stats.registered_bytes += actual.registered_bytes;
                stats.system_page_bytes += actual.system_page_bytes;
            }
            return stats;
        }

        fn auditShells(owner: *Owner, workspace: *Workspace, stats: *Stats, epoch: u64) !void {
            var resource_start: usize = 0;
            var shell_start: usize = 0;

            for (Owner.table_declarations, 0..) |declaration, table_index| {
                const table: u32 = @intCast(table_index);
                const resource_end = std.math.add(usize, resource_start, @as(usize, declaration.capacity)) catch return error.InvalidState;
                const shell_end = std.math.add(usize, shell_start, @as(usize, declaration.partition_capacity)) catch return error.InvalidState;
                if (resource_end > owner.rows.len or shell_end > owner.shells.len) return error.InvalidState;

                var queue_count: usize = 0;
                var queue_read: usize = 0;
                var root = owner.tables[table_index].first_partition;
                var root_steps: usize = 0;
                while (root != types.no_slot) {
                    if (root_steps >= @as(usize, declaration.partition_capacity)) return error.InvalidState;
                    root_steps += 1;
                    const slot = try shellIndex(root, shell_start, shell_end);
                    const shell = &owner.shells[slot];
                    if (shell.table != table or shell.parent != null) return error.InvalidState;
                    try validateActiveShell(owner, shell, slot, resource_start, resource_end, shell_start, shell_end, null, epoch);
                    if (workspace.shell_marks[slot] != 0 or queue_count >= @as(usize, declaration.partition_capacity)) return error.InvalidState;
                    workspace.shell_marks[slot] = 1;
                    workspace.depth[slot] = 0;
                    workspace.shell_queue[queue_count] = root;
                    queue_count += 1;
                    stats.partition_links += 1;
                    root = shell.next_sibling;
                }

                while (queue_read < queue_count) : (queue_read += 1) {
                    const parent_slot_u32 = workspace.shell_queue[queue_read];
                    const parent_slot = @as(usize, parent_slot_u32);
                    const parent = &owner.shells[parent_slot];
                    var child = parent.first_child;
                    var child_steps: usize = 0;
                    while (child != types.no_slot) {
                        if (child_steps >= @as(usize, declaration.partition_capacity) or queue_count >= @as(usize, declaration.partition_capacity))
                            return error.InvalidState;
                        child_steps += 1;
                        const child_slot = try shellIndex(child, shell_start, shell_end);
                        const record = &owner.shells[child_slot];
                        if (record.table != table or record.parent != parent_slot_u32) return error.InvalidState;
                        try validateActiveShell(owner, record, child_slot, resource_start, resource_end, shell_start, shell_end, parent_slot_u32, epoch);
                        if (workspace.shell_marks[child_slot] != 0) return error.InvalidState;
                        workspace.shell_marks[child_slot] = 1;
                        workspace.depth[child_slot] = std.math.add(u32, workspace.depth[parent_slot], 1) catch return error.InvalidState;
                        workspace.shell_queue[queue_count] = child;
                        queue_count += 1;
                        stats.partition_links += 1;
                        child = record.next_sibling;
                    }
                }

                var free = owner.tables[table_index].free_partition;
                var free_steps: usize = 0;
                while (free != types.no_slot) {
                    if (free_steps >= @as(usize, declaration.partition_capacity)) return error.InvalidState;
                    free_steps += 1;
                    const slot = try shellIndex(free, shell_start, shell_end);
                    const shell = &owner.shells[slot];
                    if (workspace.shell_marks[slot] != 0 or shell.table != table or shell.uid != null or
                        shell.parent != null or shell.start != 0 or shell.capacity != 0 or
                        shell.allocation_number != 0 or shell.first_child != types.no_slot or
                        !std.meta.eql(shell.attributes, declaration.attributes) or
                        !std.meta.eql(shell.history, @TypeOf(shell.history){}))
                    {
                        return error.InvalidState;
                    }
                    workspace.shell_marks[slot] = 2;
                    stats.free_partitions += 1;
                    free = shell.next_sibling;
                }

                var event_count: usize = 0;
                for (shell_start..shell_end) |slot| {
                    if (owner.shells[slot].table != table) return error.InvalidState;
                    if (workspace.shell_marks[slot] == 1) {
                        if (owner.shells[slot].uid == null) return error.InvalidState;
                        stats.active_partitions += 1;
                        try appendShellEvents(workspace, slot, owner.shells[slot], workspace.depth[slot], &event_count);
                    } else if (workspace.shell_marks[slot] == 2) {
                        if (owner.shells[slot].uid != null) return error.InvalidState;
                    } else return error.InvalidState;
                }

                stats.interval_events = std.math.add(usize, stats.interval_events, event_count) catch return error.InvalidState;
                try sweepTableRows(owner, workspace, stats, table, resource_start, resource_end, event_count);
                resource_start = resource_end;
                shell_start = shell_end;
            }
            if (resource_start != owner.rows.len or shell_start != owner.shells.len) return error.InvalidState;
        }

        fn validateActiveShell(
            owner: *Owner,
            shell: anytype,
            slot: usize,
            resource_start: usize,
            resource_end: usize,
            shell_start: usize,
            shell_end: usize,
            parent_slot: ?u32,
            epoch: u64,
        ) !void {
            if (@as(usize, shell.table) >= Owner.table_count or shell.uid == null or
                shell.capacity == 0 or shell.allocation_number == 0)
            {
                return error.InvalidState;
            }
            const start = @as(usize, shell.start);
            const end = std.math.add(usize, start, @as(usize, shell.capacity)) catch return error.InvalidState;
            if (start < resource_start or end > resource_end or end <= start) return error.InvalidState;
            if (parent_slot) |parent_id| {
                if (@as(usize, parent_id) >= owner.shells.len) return error.InvalidState;
                const parent_shell = &owner.shells[parent_id];
                const parent_start = @as(usize, parent_shell.start);
                const parent_end = std.math.add(usize, parent_start, @as(usize, parent_shell.capacity)) catch return error.InvalidState;
                if (start < parent_start or end > parent_end) return error.InvalidState;
            } else {
                const declaration = Owner.table_declarations[@as(usize, shell.table)];
                const reserved_start = std.math.add(usize, resource_start, @as(usize, declaration.reserved_start)) catch return error.InvalidState;
                const reserved_end = std.math.add(usize, reserved_start, @as(usize, declaration.reserved_count)) catch return error.InvalidState;
                if (start < reserved_start or end > reserved_end) return error.InvalidState;
            }
            if (slot < shell_start or slot >= shell_end) return error.InvalidState;
            if (shell.history.last_activity_epoch > epoch or
                (shell.history.last_activity_epoch == 0 and shell.history.bits != 0) or
                (shell.history.last_activity_epoch != 0 and shell.history.bits & 1 == 0))
            {
                return error.InvalidState;
            }
            const next_number = owner.numbers.next.load(.acquire);
            if (shell.allocation_number >= next_number) return error.InvalidState;
        }

        fn appendShellEvents(workspace: *Workspace, slot: usize, shell: anytype, depth: u32, event_count: *usize) !void {
            if (event_count.* > workspace.events.len or workspace.events.len - event_count.* < 2)
                return error.WorkspaceTooSmall;
            const start = @as(usize, shell.start);
            const end = std.math.add(usize, start, @as(usize, shell.capacity)) catch return error.InvalidState;
            const index: u32 = @intCast(slot);
            workspace.events[event_count.*] = .{ .position = start, .shell = index, .depth = depth, .entering = true };
            workspace.events[event_count.* + 1] = .{ .position = end, .shell = index, .depth = depth, .entering = false };
            event_count.* += 2;
        }

        fn sweepTableRows(owner: *Owner, workspace: *Workspace, stats: *Stats, table: u32, start: usize, end: usize, event_count: usize) !void {
            if (event_count > workspace.events.len) return error.InvalidState;
            var sort_context = SortContext{ .comparisons = &stats.event_sort_comparisons };
            std.sort.heap(Event, workspace.events[0..event_count], &sort_context, eventLess);

            var stack_count: usize = 0;
            var next_event: usize = 0;
            var position = start;
            while (position <= end) : (position += 1) {
                while (next_event < event_count and workspace.events[next_event].position == position) : (next_event += 1) {
                    const event = workspace.events[next_event];
                    const shell = &owner.shells[event.shell];
                    if (event.entering) {
                        if (stack_count >= workspace.stack.len) return error.WorkspaceTooSmall;
                        const expected_parent: ?u32 = if (stack_count == 0) null else workspace.stack[stack_count - 1];
                        if (shell.parent != expected_parent or event.depth != @as(u32, @intCast(stack_count))) return error.InvalidState;
                        workspace.stack[stack_count] = event.shell;
                        stack_count += 1;
                    } else {
                        if (stack_count == 0 or workspace.stack[stack_count - 1] != event.shell or
                            @as(usize, event.depth) + 1 != stack_count)
                        {
                            return error.InvalidState;
                        }
                        stack_count -= 1;
                    }
                }
                if (position == end) break;
                const row = &owner.rows[position];
                const expected_parent: ?u32 = if (stack_count == 0) null else workspace.stack[stack_count - 1];
                if (row.table != table or row.parent != expected_parent) return error.InvalidState;
                if (expected_parent == null and row.uid != null) {
                    const declaration = Owner.table_declarations[@as(usize, table)];
                    const reserved_start = start + @as(usize, declaration.reserved_start);
                    const reserved_end = reserved_start + @as(usize, declaration.reserved_count);
                    if (position >= reserved_start and position < reserved_end) return error.InvalidState;
                }
            }
            if (next_event != event_count or stack_count != 0) return error.InvalidState;
        }

        const SortContext = struct { comparisons: *usize };
        fn eventLess(context: *SortContext, left: Event, right: Event) bool {
            context.comparisons.* += 1;
            if (left.position != right.position) return left.position < right.position;
            if (left.entering != right.entering) return !left.entering;
            if (left.depth != right.depth) {
                return if (left.entering) left.depth < right.depth else left.depth > right.depth;
            }
            return left.shell < right.shell;
        }

        fn auditRows(owner: *Owner, workspace: *Workspace, stats: *Stats, epoch: u64, node_count: usize) !void {
            var base: usize = 0;
            for (Owner.table_declarations, 0..) |declaration, table_index| {
                const stop = std.math.add(usize, base, @as(usize, declaration.capacity)) catch return error.InvalidState;
                if (stop > owner.rows.len) return error.InvalidState;
                for (base..stop) |slot| {
                    const row = &owner.rows[slot];
                    if (row.table != @as(u32, @intCast(table_index))) return error.InvalidState;
                    const entry = owner.index.entryAt(@intCast(slot)) catch return error.InvalidState;
                    if (row.uid) |uid| {
                        const registration = row.resource.registration orelse return error.InvalidState;
                        if ((row.fixed_declaration == null and row.resource.retention != .dynamic) or
                            (row.fixed_declaration != null and row.resource.retention != .fixed))
                        {
                            return error.InvalidState;
                        }
                        if (row.resource.loading and (row.resource.use_count != 1 or
                            row.resource.next_use_generation <= 1 or
                            row.resource.active_use_xor != row.resource.next_use_generation - 1))
                        {
                            return error.InvalidState;
                        }
                        if (row.resource.next_use_generation == 0 or
                            row.resource.forced_through_use_generation >= row.resource.next_use_generation or
                            row.resource.use_count > row.resource.next_use_generation - 1 - row.resource.forced_through_use_generation)
                        {
                            return error.InvalidState;
                        }
                        if ((row.resource.use_count == 0 and (row.resource.active_use_xor != 0 or row.resource.loading)) or
                            (row.resource.use_count != 0 and registration.region == null))
                        {
                            return error.InvalidState;
                        }
                        if (row.resource.use_count == 1 and (row.resource.active_use_xor == 0 or
                            row.resource.active_use_xor >= row.resource.next_use_generation or
                            row.resource.active_use_xor <= row.resource.forced_through_use_generation))
                        {
                            return error.InvalidState;
                        }
                        if (row.resource.loading and row.resource.generation == 0) return error.InvalidState;
                        if (registration.region == null and row.resource.retention == .dynamic) return error.InvalidState;
                        if (row.resource.generation == 0 and registration.region != null) return error.InvalidState;
                        if (row.resource.activity_history.last_activity_epoch > epoch or
                            (row.resource.activity_history.last_activity_epoch == 0 and row.resource.activity_history.bits != 0) or
                            (row.resource.activity_history.last_activity_epoch != 0 and row.resource.activity_history.bits & 1 == 0))
                        {
                            return error.InvalidState;
                        }
                        if (entry == null or !entry.?.uid.eql(uid) or entry.?.location.kind != .resource or
                            entry.?.location.table != @as(u32, @intCast(table_index)) or entry.?.location.slot != @as(u32, @intCast(slot)))
                        {
                            return error.InvalidState;
                        }
                        stats.active_resources += 1;
                        const allocation_bytes = registration.allocation_bytes;
                        const required_pages = memory.requiredBytes(allocation_bytes) catch return error.InvalidState;
                        if (registration.region) |region| {
                            const expected_length = std.math.cast(usize, allocation_bytes) orelse return error.InvalidState;
                            if (region.allocation_bytes != expected_length or region.page_bytes != required_pages or
                                region.bytes().len != expected_length or
                                (region.page_bytes == 0 and region.base != null) or
                                (region.page_bytes != 0 and region.base == null))
                            {
                                return error.InvalidState;
                            }
                            const pages_u64 = std.math.cast(u64, region.page_bytes) orelse return error.InvalidState;
                            workspace.table_views[table_index].system_page_bytes = std.math.add(
                                u64,
                                workspace.table_views[table_index].system_page_bytes,
                                pages_u64,
                            ) catch return error.InvalidState;
                        }
                        workspace.table_views[table_index].registered_bytes = std.math.add(
                            u64,
                            workspace.table_views[table_index].registered_bytes,
                            allocation_bytes,
                        ) catch return error.InvalidState;
                        if (row.fixed_declaration) |fixed_id| {
                            try auditFixedRow(owner, workspace, slot, fixed_id, uid);
                        } else if (row.fixed_parent_number != 0) return error.InvalidState;
                        if (row.resource.generation != 0) {
                            try appendAllocation(workspace, &stats.allocation_numbers, row.resource.generation, owner.numbers.next.load(.acquire), node_count);
                        }
                    } else {
                        if (entry != null or row.fixed_declaration != null or row.fixed_parent_number != 0 or
                            row.resource.registration != null or row.resource.use_count != 0 or row.resource.loading or
                            row.resource.active_use_xor != 0)
                        {
                            return error.InvalidState;
                        }
                    }
                }
                base = stop;
            }
            if (base != owner.rows.len) return error.InvalidState;

            for (owner.shells, 0..) |shell, slot| {
                const entry = owner.index.entryAt(@intCast(node_count - owner.shells.len + slot)) catch return error.InvalidState;
                if (shell.uid) |uid| {
                    if (entry == null or !entry.?.uid.eql(uid) or entry.?.location.kind != .partition or
                        entry.?.location.table != shell.table or entry.?.location.slot != @as(u32, @intCast(slot)))
                    {
                        return error.InvalidState;
                    }
                    try appendAllocation(workspace, &stats.allocation_numbers, shell.allocation_number, owner.numbers.next.load(.acquire), node_count);
                } else if (entry != null) return error.InvalidState;
            }
        }

        fn auditFixedRow(owner: *Owner, workspace: *Workspace, slot: usize, fixed_id: u32, uid: Uid) !void {
            const fixed_index = @as(usize, fixed_id);
            if (fixed_index >= Owner.fixed_declarations.len) return error.InvalidState;
            const declaration = Owner.fixed_declarations[fixed_index];
            const row = &owner.rows[slot];
            if (declaration.table != row.table or !declaration.uid.eql(uid) or
                workspace.fixed_marks[fixed_index] != 0)
            {
                return error.InvalidState;
            }
            if (declaration.parent) |parent_uid| {
                const parent_slot = row.parent orelse return error.InvalidState;
                if (@as(usize, parent_slot) >= owner.shells.len) return error.InvalidState;
                const parent = &owner.shells[parent_slot];
                const expected_slot = std.math.add(u32, parent.start, declaration.ordinal) catch return error.InvalidState;
                if (parent.uid == null or !parent.uid.?.eql(parent_uid) or parent.table != declaration.table or
                    declaration.ordinal >= parent.capacity or expected_slot != @as(u32, @intCast(slot)) or
                    row.fixed_parent_number != parent.allocation_number)
                {
                    return error.InvalidState;
                }
            } else {
                const expected_slot = std.math.add(usize, Owner.resourceStart(@intCast(declaration.table)), @as(usize, declaration.ordinal)) catch return error.InvalidState;
                if (row.parent != null or row.fixed_parent_number != 0 or expected_slot != @as(usize, slot)) return error.InvalidState;
            }
            const binding = &owner.bindings[fixed_index];
            if (binding.slot.load(.acquire) != @as(u32, @intCast(slot)) or binding.parent_number.load(.acquire) != row.fixed_parent_number)
                return error.InvalidState;
            workspace.fixed_marks[fixed_index] = 1;
        }

        fn auditFixedBindings(owner: *Owner, workspace: *Workspace, stats: *Stats) !void {
            for (Owner.fixed_declarations, 0..) |declaration, fixed_id| {
                const binding = &owner.bindings[fixed_id];
                if (declaration.parent == null) {
                    if (workspace.fixed_marks[fixed_id] != 1) return error.InvalidState;
                    stats.fixed_rows += 1;
                    continue;
                }
                const parent_uid = declaration.parent.?;
                stats.fixed_identity_lookups += 1;
                const location = (owner.index.find(parent_uid) catch return error.InvalidState) orelse {
                    if (workspace.fixed_marks[fixed_id] != 0 or binding.slot.load(.acquire) != types.no_slot)
                        return error.InvalidState;
                    continue;
                };
                if (location.kind != .partition or location.table != declaration.table or
                    @as(usize, location.slot) >= owner.shells.len)
                {
                    return error.InvalidState;
                }
                const parent = &owner.shells[location.slot];
                const expected_slot = std.math.add(u32, parent.start, declaration.ordinal) catch return error.InvalidState;
                if (parent.uid == null or !parent.uid.?.eql(parent_uid) or declaration.ordinal >= parent.capacity or
                    workspace.fixed_marks[fixed_id] != 1 or binding.slot.load(.acquire) != expected_slot or
                    binding.parent_number.load(.acquire) != parent.allocation_number)
                {
                    return error.InvalidState;
                }
                stats.fixed_rows += 1;
            }
            for (workspace.fixed_marks[0..Owner.fixed_declarations.len]) |mark| {
                if (mark > 1) return error.InvalidState;
            }
        }

        fn auditAllocationNumbers(owner: *Owner, workspace: *Workspace, stats: *Stats, node_count: usize) !void {
            var sort_context = SortContext{ .comparisons = &stats.allocation_sort_comparisons };
            std.sort.heap(u64, workspace.allocations[0..stats.allocation_numbers], &sort_context, allocationLess);
            if (stats.allocation_numbers > 1) {
                for (workspace.allocations[1..stats.allocation_numbers], 1..) |number, index| {
                    if (number == workspace.allocations[index - 1]) return error.InvalidState;
                }
            }
            if (stats.allocation_numbers > node_count) return error.InvalidState;
            _ = owner;
        }

        fn allocationLess(context: *SortContext, left: u64, right: u64) bool {
            context.comparisons.* += 1;
            return left < right;
        }

        fn appendAllocation(workspace: *Workspace, count: *usize, number: u64, next_number: u64, capacity: usize) !void {
            if (number == 0 or number >= next_number or count.* >= capacity) return error.InvalidState;
            workspace.allocations[count.*] = number;
            count.* += 1;
        }

        fn shellIndex(slot: u32, start: usize, end: usize) !usize {
            const index = @as(usize, slot);
            if (index < start or index >= end) return error.InvalidState;
            return index;
        }
    };
}

fn unlockAll(owner: anytype, table_count: *usize, row_count: *usize, home_count: *usize) void {
    while (home_count.* != 0) {
        home_count.* -= 1;
        owner.index.unlockHome(@intCast(home_count.*));
    }
    while (row_count.* != 0) {
        row_count.* -= 1;
        owner.rows[row_count.*].lock.unlock();
    }
    while (table_count.* != 0) {
        table_count.* -= 1;
        owner.tables[table_count.*].write.unlock();
    }
}
