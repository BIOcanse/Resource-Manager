//! Iterative tree updates and atomic close/force-release for one owned table.
//! The owner supplies all persistent state and per-call workspace.
const std = @import("std");
const types = @import("types.zig");
const release = @import("../release_check.zig");

pub fn For(comptime Owner: type) type {
    const Uid = Owner.uid_type;
    const Api = Owner.api;
    const Index = @TypeOf(@as(Owner, undefined).index);

    return struct {
        const Self = @This();
        pub const Range = struct { table: u32, start: usize, end: usize };
        const UpdateAttributes = types.AttributeUpdate;

        /// Applies an attribute update to a partition and every descendant.
        /// This method owns table synchronization because updateUid performs
        /// the initial UID lookup without it.
        pub fn update(owner: *Owner, uid: Uid, change: Api.Update) !Api.UpdateResult {
            while (true) {
                const observed = (try owner.index.find(uid)) orelse return error.Nonexistent;
                if (observed.kind == .resource) return error.RetryResourceUpdate;
                if (observed.kind != .partition) return error.InvalidArgument;
                if (@as(usize, observed.table) >= owner.tables.len) return error.InvalidState;

                owner.tables[observed.table].write.lock();
                const result = updateLocked(owner, uid, observed, change) catch |err| {
                    if (err == error.RetryTreeUpdate) continue;
                    return err;
                };
                return result;
            }
        }

        fn updateLocked(owner: *Owner, uid: Uid, observed: Index.Location, change: Api.Update) !Api.UpdateResult {
            const table = observed.table;
            defer owner.tables[table].write.unlock();

            const current = (try owner.index.find(uid)) orelse return error.Nonexistent;
            if (current.table != observed.table or current.slot != observed.slot) return error.RetryTreeUpdate;
            if (current.kind != .partition) return error.RetryTreeUpdate;
            if (change.allocation_bytes != null) return error.InvalidArgument;
            const attributes = change.attributes orelse return .{ .updated = null };
            if (!hasAttributeChange(attributes)) return .{ .updated = null };

            if (@as(usize, current.slot) >= owner.shells.len) return error.InvalidState;
            const root = &owner.shells[current.slot];
            if (root.uid == null or !root.uid.?.eql(uid) or root.table != table) return error.Nonexistent;
            const range = try resolveRange(Owner, owner, root.start, root.capacity);
            if (range.table != table) return error.InvalidState;
            try validateRootLink(owner, table, current.slot, root.parent);

            var row_locks: usize = 0;
            defer unlockRows(owner, range.start, &row_locks);
            while (row_locks < range.end - range.start) : (row_locks += 1) {
                owner.rows[range.start + row_locks].lock.lock();
            }

            const validation = WalkValidation{
                .table = table,
                .root = current.slot,
                .range_start = range.start,
                .range_end = range.end,
            };
            try walkSubtree(owner, table, current.slot, validation, validateWalkShell);

            for (owner.rows[range.start..range.end], 0..) |*row, offset| {
                if (row.table != table) return error.InvalidState;
                if (row.uid) |resource_uid| {
                    if (row.resource.registration == null) return error.InvalidState;
                    try validateResourceIndex(owner, table, @intCast(range.start + offset), resource_uid);
                    if (row.fixed_declaration != null) try validateFixedRow(
                        owner,
                        table,
                        @intCast(range.start + offset),
                        row,
                    );
                } else if (row.resource.registration != null or row.fixed_declaration != null or
                    row.resource.use_count != 0 or row.resource.loading)
                {
                    return error.InvalidState;
                }
            }
            for (owner.rows[range.start..range.end]) |*row| {
                if (row.uid != null) row.attributes = attributes.apply(row.attributes);
            }
            const update_context = UpdateWalk{ .attributes = attributes };
            walkSubtree(owner, table, current.slot, update_context, applyShellAttributes) catch unreachable;
            return .{ .updated = null };
        }

        /// Closes one current resource or partition. The owner holds the
        /// resolved table's write lock and flushes work.notes after unlocking.
        pub fn close(owner: *Owner, uid: Uid, forced: bool, work: *Owner.Workspace) !void {
            const position = (try owner.index.find(uid)) orelse return error.Nonexistent;
            if (@as(usize, position.table) >= owner.tables.len) return error.InvalidState;
            if (position.kind == .resource) {
                return closeResource(owner, position.table, position.slot, uid, forced, work);
            }
            if (@as(usize, position.slot) >= owner.shells.len) return error.InvalidState;
            const shell = &owner.shells[position.slot];
            if (shell.uid == null or !shell.uid.?.eql(uid) or shell.table != position.table)
                return error.Nonexistent;
            const roots = [_]u32{position.slot};
            return closeRange(owner, shell.start, shell.capacity, &roots, forced, shell.parent, work);
        }

        /// Closes one or more direct child shells within a caller-selected
        /// resource interval as one commit. `roots` may alias work.shells: the
        /// root values are copied before descendant slots are appended.
        /// The owner holds the table write lock and flushes notes afterward.
        pub fn closeRange(
            owner: *Owner,
            start: u32,
            capacity: u32,
            roots: []const u32,
            forced: bool,
            restore_parent: ?u32,
            work: *Owner.Workspace,
        ) !void {
            _ = try closeAndInstallRange(owner, start, capacity, roots, forced, restore_parent, work, null);
        }

        /// Atomically releases the selected child shells in the whole planner
        /// interval, then installs a new shell in its requested prefix. Any
        /// reclaimed tail remains empty under `parent`.
        pub fn installRange(
            owner: *Owner,
            start: u32,
            interval_capacity: u32,
            requested_capacity: u32,
            roots: []const u32,
            parent: ?u32,
            uid: Uid,
            attributes: types.Attributes,
            number: u64,
            work: *Owner.Workspace,
        ) !u32 {
            work.blocked_slot = null;
            if (number == 0) return error.InvalidArgument;
            const shell_slot = (try closeAndInstallRange(owner, start, interval_capacity, roots, false, parent, work, .{
                .requested_capacity = requested_capacity,
                .uid = uid,
                .attributes = attributes,
                .number = number,
            })) orelse return error.InvalidState;
            return shell_slot;
        }

        const InstallSpec = struct {
            requested_capacity: u32,
            uid: Uid,
            attributes: types.Attributes,
            number: u64,
        };

        const ShellChoice = struct {
            slot: u32,
            from_free_list: bool,
            next_free: u32,
        };

        fn closeAndInstallRange(
            owner: *Owner,
            start: u32,
            capacity: u32,
            roots: []const u32,
            forced: bool,
            restore_parent: ?u32,
            work: *Owner.Workspace,
            install: ?InstallSpec,
        ) !?u32 {
            const note_start = work.note_count;
            errdefer work.note_count = note_start;

            const range = try resolveRange(Owner, owner, start, capacity);
            if (install) |spec| {
                try validateInstallRange(owner, range, start, capacity, spec.requested_capacity, restore_parent);
                if (Owner.table_declarations[range.table].partition_capacity == 0) return error.SpecificationLimit;
            }
            if (work.homes.len < roots.len) return error.WorkspaceTooSmall;
            std.mem.copyForwards(u32, work.homes[0..roots.len], roots);
            std.sort.heap(u32, work.homes[0..roots.len], {}, lessThanU32);
            const root_ids = work.homes[0..roots.len];
            const closed_shells = try collectShellsInternal(
                owner,
                range.table,
                start,
                capacity,
                root_ids,
                restore_parent,
                work.shells,
                install != null,
            );
            std.sort.heap(u32, root_ids, RootSortContext{ .owner = owner }, rootBefore);
            if (root_ids.len > 1) {
                for (root_ids[1..], 1..) |root_slot, i| {
                    const previous = &owner.shells[root_ids[i - 1]];
                    const previous_end = @as(usize, previous.start) + @as(usize, previous.capacity);
                    if (@as(usize, owner.shells[root_slot].start) < previous_end) return error.InvalidState;
                }
            }

            const target_row_count = range.end - range.start;
            if (work.rows.len < target_row_count) return error.WorkspaceTooSmall;

            var candidate_count: usize = 0;
            for (range.start..range.end) |slot| {
                work.rows[candidate_count] = .{
                    .slot = @intCast(slot),
                    .decision_bytes = 0,
                    .snapshot = undefined,
                };
                candidate_count += 1;
            }
            if (candidate_count != target_row_count) return error.InvalidState;

            var row_locks: usize = 0;
            defer unlockCandidateRows(owner, work, &row_locks);
            while (row_locks < candidate_count) : (row_locks += 1) {
                owner.rows[work.rows[row_locks].slot].lock.lock();
            }

            const restored_attributes = try parentAttributes(owner, range.table, restore_parent);
            var removal_count: usize = closed_shells.len;
            var registered_sub: u64 = 0;
            var page_sub: u64 = 0;

            for (work.rows[0..candidate_count]) |*candidate| {
                const row = &owner.rows[candidate.slot];
                if (row.table != range.table) return error.InvalidState;
                const in_tree = if (row.parent) |parent| containsSorted(closed_shells, parent) else false;
                if (!in_tree and row.parent != restore_parent) return error.InvalidState;
                if (row.parent) |parent| {
                    const parent_shell = &owner.shells[parent];
                    const parent_start = @as(usize, parent_shell.start);
                    const parent_end = parent_start + @as(usize, parent_shell.capacity);
                    if (@as(usize, candidate.slot) < parent_start or @as(usize, candidate.slot) >= parent_end)
                        return error.InvalidState;
                }
                if (!in_tree and row.uid != null) return error.InvalidState;
                if (!in_tree and (row.resource.registration != null or row.fixed_declaration != null))
                    return error.InvalidState;
                if (row.uid) |resource_uid| {
                    if (!in_tree) return error.InvalidState;
                    const reg = row.resource.registration orelse return error.InvalidState;
                    try validateResourceIndex(owner, range.table, candidate.slot, resource_uid);
                    if (row.fixed_declaration != null) try validateFixedRow(owner, range.table, candidate.slot, row);
                    if (!forced and reg.region != null) {
                        if (row.resource.use_count != 0 or row.resource.loading) {
                            if (install != null) work.blocked_slot = candidate.slot;
                            return error.InUse;
                        }
                        if (!row.attributes.safe()) {
                            if (install != null) work.blocked_slot = candidate.slot;
                            return error.Blocked;
                        }
                        if (release.check(
                            Owner.table_declarations[range.table].policy.release_check,
                            reg.allocation_bytes,
                            reg.allocation_bytes,
                            false,
                        ) != .allowed) {
                            if (install != null) work.blocked_slot = candidate.slot;
                            return error.ChangedBeyondTolerance;
                        }
                    } else if (reg.region == null and
                        (row.resource.use_count != 0 or row.resource.loading)) return error.InvalidState;

                    removal_count = std.math.add(usize, removal_count, 1) catch return error.InvalidState;
                    registered_sub = try addBytes(registered_sub, reg.allocation_bytes);
                    page_sub = try addBytes(page_sub, @intCast(reg.allocatedPageBytes()));
                    candidate.decision_bytes = reg.allocation_bytes;
                    if (reg.region != null) try owner.saveNote(range.table, work, resource_uid, row.resource.generation);
                } else if (row.resource.registration != null or row.fixed_declaration != null or
                    row.resource.use_count != 0 or row.resource.loading)
                {
                    return error.InvalidState;
                }
                candidate.snapshot = owner.snapshotLocked(row);
            }

            for (closed_shells) |shell_slot| {
                const shell = &owner.shells[shell_slot];
                const shell_uid = shell.uid orelse return error.InvalidState;
                if (shell.allocation_number == 0) return error.InvalidState;
                try validatePartitionIndex(owner, range.table, shell_slot, shell_uid);
                try owner.saveNote(range.table, work, shell_uid, shell.allocation_number);
            }

            const table_state = &owner.tables[range.table];
            var next_registered = try subtractBytes(table_state.registered_bytes, registered_sub);
            const next_pages = try subtractBytes(table_state.system_page_bytes, page_sub);

            const Resource = @TypeOf(owner.rows[0].resource);
            var empty_resource = try Resource.dynamic(0);
            empty_resource.registration = null;
            var shell_choice: ?ShellChoice = null;
            var new_history: @TypeOf(owner.shells[0].history) = .{};
            var fixed_count: usize = 0;
            var insert_count: usize = 0;
            if (install) |spec| {
                shell_choice = try chooseInstallShell(owner, range.table, closed_shells);
                for (Owner.fixed_declarations, 0..) |declaration, declaration_index| {
                    if (declaration.table != range.table) continue;
                    const fixed_parent = declaration.parent orelse continue;
                    if (!fixed_parent.eql(spec.uid)) continue;
                    if (declaration.ordinal >= spec.requested_capacity) return error.InvalidArgument;
                    for (Owner.fixed_declarations[0..declaration_index]) |previous| {
                        const previous_parent = previous.parent orelse continue;
                        if (previous.table == range.table and previous_parent.eql(spec.uid) and
                            previous.ordinal == declaration.ordinal) return error.InvalidArgument;
                    }
                    _ = try Resource.fixed(declaration.allocation_bytes);
                    next_registered = try addBytes(next_registered, declaration.allocation_bytes);
                    fixed_count = std.math.add(usize, fixed_count, 1) catch return error.InvalidState;
                }
                insert_count = std.math.add(usize, fixed_count, 1) catch return error.InvalidState;
                var history: @TypeOf(owner.shells[0].history) = .{};
                history = try history.withAccess(owner.epoch.load(.acquire));
                new_history = history;
            }

            if (work.remove_ids.len < removal_count or work.puts.len < insert_count)
                return error.WorkspaceTooSmall;

            var remove_id_count: usize = 0;
            for (work.rows[0..candidate_count]) |candidate| {
                const row = &owner.rows[candidate.slot];
                if (row.uid != null) {
                    work.remove_ids[remove_id_count] = candidate.slot;
                    remove_id_count += 1;
                }
            }
            if (owner.rows.len > std.math.maxInt(u32)) return error.InvalidState;
            const shell_node_offset: u32 = @intCast(owner.rows.len);
            for (closed_shells) |shell_slot| {
                work.remove_ids[remove_id_count] = std.math.add(u32, shell_node_offset, shell_slot) catch return error.InvalidState;
                remove_id_count += 1;
            }

            var put_count: usize = 0;
            if (install) |spec| {
                const choice = shell_choice.?;
                work.puts[put_count] = .{
                    .node_id = std.math.add(u32, shell_node_offset, choice.slot) catch return error.InvalidState,
                    .uid = spec.uid,
                    .location = .{ .kind = .partition, .table = range.table, .slot = choice.slot },
                };
                put_count += 1;
                for (Owner.fixed_declarations) |declaration| {
                    if (declaration.table != range.table) continue;
                    const fixed_parent = declaration.parent orelse continue;
                    if (!fixed_parent.eql(spec.uid)) continue;
                    const slot = std.math.add(u32, start, declaration.ordinal) catch return error.InvalidState;
                    work.puts[put_count] = .{
                        .node_id = slot,
                        .uid = declaration.uid,
                        .location = .{ .kind = .resource, .table = range.table, .slot = slot },
                    };
                    put_count += 1;
                }
            }
            if (remove_id_count != removal_count or put_count != insert_count) return error.InvalidState;
            const Transactions = @TypeOf(owner.index).Transactions;
            var index_transaction = try Transactions.prepare(
                &owner.index,
                work.remove_ids[0..remove_id_count],
                work.puts[0..put_count],
                &work.index_workspace,
            );
            defer index_transaction.abort();

            for (work.rows[0..candidate_count]) |candidate| {
                const row = &owner.rows[candidate.slot];
                if (row.uid == null) continue;
                if (forced) {
                    row.resource.forceRelease() catch @panic("ledger OS release invariant failed");
                } else {
                    const result = row.resource.unload() catch @panic("ledger OS release invariant failed");
                    if (result != .closed) @panic("ledger close preflight invariant failed");
                }
            }

            for (work.rows[0..candidate_count]) |candidate|
                owner.recordAllocationRelease(&owner.rows[candidate.slot]);
            index_transaction.commit();

            unlinkRoots(owner, range.table, restore_parent, closed_shells);
            for (work.rows[0..candidate_count]) |candidate| {
                const row = &owner.rows[candidate.slot];
                if (row.fixed_declaration) |declaration| owner.bindings[declaration].slot.store(types.no_slot, .release);
                row.uid = null;
                row.fixed_declaration = null;
                row.fixed_parent_number = 0;
                row.resource = empty_resource;
                row.parent = restore_parent;
                row.attributes = restored_attributes;
            }
            table_state.registered_bytes = next_registered;
            table_state.system_page_bytes = next_pages;

            if (shell_choice) |choice| {
                if (choice.from_free_list) table_state.free_partition = choice.next_free;
            }

            for (closed_shells) |shell_slot| {
                if (shell_choice) |choice| {
                    if (!choice.from_free_list and shell_slot == choice.slot) continue;
                }
                const shell = &owner.shells[shell_slot];
                shell.uid = null;
                shell.parent = null;
                shell.start = 0;
                shell.capacity = 0;
                shell.allocation_number = 0;
                shell.attributes = Owner.table_declarations[range.table].attributes;
                shell.history = .{};
                shell.first_child = types.no_slot;
                shell.next_sibling = table_state.free_partition;
                table_state.free_partition = shell_slot;
            }

            if (install) |spec| {
                const shell_slot = shell_choice.?.slot;
                const shell = &owner.shells[shell_slot];
                const old_first = if (restore_parent) |parent_slot|
                    owner.shells[parent_slot].first_child
                else
                    table_state.first_partition;
                shell.uid = spec.uid;
                shell.table = range.table;
                shell.parent = restore_parent;
                shell.start = start;
                shell.capacity = spec.requested_capacity;
                shell.allocation_number = spec.number;
                shell.attributes = spec.attributes;
                shell.history = new_history;
                shell.first_child = types.no_slot;
                shell.next_sibling = old_first;
                if (restore_parent) |parent_slot| {
                    owner.shells[parent_slot].first_child = shell_slot;
                } else {
                    table_state.first_partition = shell_slot;
                }

                const requested_end = @as(usize, start) + @as(usize, spec.requested_capacity);
                for (owner.rows[@as(usize, start)..requested_end]) |*row| {
                    row.parent = shell_slot;
                    row.attributes = spec.attributes;
                }
                for (Owner.fixed_declarations, 0..) |declaration, declaration_index| {
                    if (declaration.table != range.table) continue;
                    const fixed_parent = declaration.parent orelse continue;
                    if (!fixed_parent.eql(spec.uid)) continue;
                    const slot = std.math.add(u32, start, declaration.ordinal) catch unreachable;
                    const row = &owner.rows[slot];
                    row.uid = declaration.uid;
                    row.fixed_declaration = @intCast(declaration_index);
                    row.parent = shell_slot;
                    row.fixed_parent_number = spec.number;
                    row.attributes = declaration.attributes orelse spec.attributes;
                    row.resource = Resource.fixed(declaration.allocation_bytes) catch unreachable;
                    owner.bindings[declaration_index].parent_number.store(spec.number, .release);
                    owner.bindings[declaration_index].slot.store(slot, .release);
                }
            }

            return if (shell_choice) |choice| choice.slot else null;
        }

        /// Collects and validates roots plus every linked descendant into the
        /// caller's scratch slice. The returned IDs are sorted for membership
        /// checks and deterministic index-node preparation.
        pub fn collectShells(
            owner: *Owner,
            table: u32,
            start: u32,
            capacity: u32,
            roots: []const u32,
            restore_parent: ?u32,
            scratch: []u32,
        ) ![]u32 {
            return collectShellsInternal(owner, table, start, capacity, roots, restore_parent, scratch, false);
        }

        fn collectShellsInternal(
            owner: *Owner,
            table: u32,
            start: u32,
            capacity: u32,
            roots: []const u32,
            restore_parent: ?u32,
            scratch: []u32,
            allow_empty_roots: bool,
        ) ![]u32 {
            if (@as(usize, table) >= Owner.table_declarations.len or (roots.len == 0 and !allow_empty_roots))
                return error.InvalidArgument;
            if (roots.len > scratch.len) return error.WorkspaceTooSmall;
            const declaration = Owner.table_declarations[table];
            const shell_start = partitionStart(Owner, table);
            const shell_end = shell_start + @as(usize, declaration.partition_capacity);
            if (declaration.partition_capacity == 0 or roots.len > declaration.partition_capacity)
                return error.InvalidArgument;
            const range_start = @as(u64, start);
            const range_end = range_start + @as(u64, capacity);
            if (capacity == 0 or range_end > @as(u64, @intCast(owner.rows.len))) return error.InvalidArgument;

            if (restore_parent) |parent_slot| {
                if (!inShellRange(parent_slot, shell_start, shell_end)) return error.InvalidState;
                const parent = &owner.shells[parent_slot];
                if (parent.table != table or parent.uid == null) return error.InvalidState;
                const parent_start = @as(u64, parent.start);
                const parent_end = parent_start + @as(u64, parent.capacity);
                if (range_start < parent_start or range_end > parent_end) return error.InvalidArgument;
            } else {
                const resource_start = @as(u64, @intCast(resourceStart(Owner, table)));
                const resource_end = resource_start + @as(u64, declaration.capacity);
                if (range_start < resource_start or range_end > resource_end) return error.InvalidArgument;
            }

            std.mem.copyForwards(u32, scratch[0..roots.len], roots);
            std.sort.heap(u32, scratch[0..roots.len], {}, lessThanU32);
            if (roots.len > 1) {
                for (scratch[1..roots.len], 1..) |root, i| {
                    if (root == scratch[i - 1]) return error.InvalidArgument;
                }
            }
            for (scratch[0..roots.len]) |root_slot| {
                if (!inShellRange(root_slot, shell_start, shell_end)) return error.InvalidState;
            }
            try validateRootSiblings(owner, table, restore_parent, scratch[0..roots.len]);
            std.sort.heap(u32, scratch[0..roots.len], RootSortContext{ .owner = owner }, rootBefore);
            for (scratch[0..roots.len], 0..) |root_slot, i| {
                const root = &owner.shells[root_slot];
                const root_start = @as(u64, root.start);
                const root_end = root_start + @as(u64, root.capacity);
                if (root.capacity == 0 or root_start < range_start or root_end > range_end)
                    return error.InvalidArgument;
                if (i != 0) {
                    const previous = &owner.shells[scratch[i - 1]];
                    const previous_end = @as(u64, previous.start) + @as(u64, previous.capacity);
                    if (root_start < previous_end) return error.InvalidState;
                }
            }

            var count = roots.len;
            var cursor: usize = 0;
            while (cursor < count) : (cursor += 1) {
                const shell_slot = scratch[cursor];
                if (!inShellRange(shell_slot, shell_start, shell_end)) return error.InvalidState;
                const shell = &owner.shells[shell_slot];
                if (shell.table != table or shell.uid == null or shell.allocation_number == 0)
                    return error.InvalidState;
                if (cursor < roots.len and shell.parent != restore_parent) return error.InvalidState;
                const shell_range_start = @as(u64, shell.start);
                const shell_range_end = shell_range_start + @as(u64, shell.capacity);
                if (shell.capacity == 0 or shell_range_start < range_start or shell_range_end > range_end)
                    return error.InvalidArgument;
                if (cursor >= roots.len) {
                    const parent_slot = shell.parent orelse return error.InvalidState;
                    if (!inShellRange(parent_slot, shell_start, shell_end)) return error.InvalidState;
                    const parent = &owner.shells[parent_slot];
                    const parent_end = @as(u64, parent.start) + @as(u64, parent.capacity);
                    if (parent.table != table or parent.uid == null or shell_range_start < @as(u64, parent.start) or shell_range_end > parent_end)
                        return error.InvalidState;
                }

                var child = shell.first_child;
                var sibling_steps: usize = 0;
                while (child != types.no_slot) {
                    if (sibling_steps >= declaration.partition_capacity or count >= declaration.partition_capacity)
                        return error.InvalidState;
                    if (!inShellRange(child, shell_start, shell_end)) return error.InvalidState;
                    const child_shell = &owner.shells[child];
                    if (child_shell.table != table or child_shell.uid == null or child_shell.allocation_number == 0 or
                        child_shell.parent != shell_slot) return error.InvalidState;
                    const child_start = @as(u64, child_shell.start);
                    const child_end = child_start + @as(u64, child_shell.capacity);
                    if (child_shell.capacity == 0 or child_start < shell_range_start or child_end > shell_range_end)
                        return error.InvalidState;
                    if (count >= scratch.len) return error.WorkspaceTooSmall;
                    scratch[count] = child;
                    count += 1;
                    sibling_steps += 1;
                    child = child_shell.next_sibling;
                }
            }

            std.sort.heap(u32, scratch[0..count], {}, lessThanU32);
            if (count > 1) {
                for (scratch[1..count], 1..) |shell_slot, i| {
                    if (shell_slot == scratch[i - 1]) return error.InvalidState;
                }
            }
            return scratch[0..count];
        }

        fn closeResource(owner: *Owner, table: u32, slot: u32, uid: Uid, forced: bool, work: *Owner.Workspace) !void {
            if (@as(usize, slot) >= owner.rows.len) return error.InvalidState;
            const row = &owner.rows[slot];
            row.lock.lock();
            defer row.lock.unlock();
            if (row.table != table or row.uid == null or !row.uid.?.eql(uid)) return error.Nonexistent;
            const registration = row.resource.registration orelse return error.InvalidState;
            if (row.fixed_declaration != null) try validateFixedRow(owner, table, slot, row);
            try validateResourceIndex(owner, table, slot, uid);
            const resident = registration.region != null;
            if (!forced and resident) {
                if (row.resource.use_count != 0 or row.resource.loading) return error.InUse;
                if (!row.attributes.safe()) return error.Blocked;
                if (release.check(
                    Owner.table_declarations[table].policy.release_check,
                    registration.allocation_bytes,
                    registration.allocation_bytes,
                    false,
                ) != .allowed) return error.ChangedBeyondTolerance;
            } else if (!resident and (row.resource.use_count != 0 or row.resource.loading)) return error.InvalidState;

            const remove_registration = row.fixed_declaration == null;
            const next_pages = try subtractBytes(owner.tables[table].system_page_bytes, @intCast(registration.allocatedPageBytes()));
            const next_registered = if (remove_registration)
                try subtractBytes(owner.tables[table].registered_bytes, registration.allocation_bytes)
            else
                owner.tables[table].registered_bytes;
            const note_start = work.note_count;
            errdefer work.note_count = note_start;
            if (resident) try owner.saveNote(table, work, uid, row.resource.generation);

            const Transactions = @TypeOf(owner.index).Transactions;
            var index_transaction: ?Transactions.Prepared = null;
            defer if (index_transaction) |*prepared| prepared.abort();
            if (remove_registration) {
                if (work.remove_ids.len == 0) return error.WorkspaceTooSmall;
                try validateResourceIndex(owner, table, slot, uid);
                work.remove_ids[0] = slot;
                index_transaction = try Transactions.prepare(
                    &owner.index,
                    work.remove_ids[0..1],
                    &.{},
                    &work.index_workspace,
                );
            }

            if (forced) {
                row.resource.forceRelease() catch @panic("ledger OS release invariant failed");
            } else {
                const result = row.resource.unload() catch @panic("ledger OS release invariant failed");
                if (result != .closed) @panic("ledger close preflight invariant failed");
            }
            owner.recordAllocationRelease(row);
            if (index_transaction) |*prepared| prepared.commit();
            owner.tables[table].registered_bytes = next_registered;
            owner.tables[table].system_page_bytes = next_pages;
            if (remove_registration) {
                row.uid = null;
                row.fixed_declaration = null;
                row.fixed_parent_number = 0;
                row.resource.registration = null;
            }
        }

        const WalkValidation = struct {
            table: u32,
            root: u32,
            range_start: usize,
            range_end: usize,
        };
        const RootSortContext = struct { owner: *Owner };
        const UpdateWalk = struct { attributes: UpdateAttributes };

        fn rootBefore(context: RootSortContext, left: u32, right: u32) bool {
            const left_shell = &context.owner.shells[left];
            const right_shell = &context.owner.shells[right];
            if (left_shell.start != right_shell.start) return left_shell.start < right_shell.start;
            return left < right;
        }

        fn validateWalkShell(owner: *Owner, context: WalkValidation, slot: u32) !void {
            const shell = &owner.shells[slot];
            if (shell.table != context.table or shell.uid == null or shell.allocation_number == 0)
                return error.InvalidState;
            const start = @as(usize, shell.start);
            const end = start + @as(usize, shell.capacity);
            if (shell.capacity == 0 or start < context.range_start or end > context.range_end)
                return error.InvalidState;
            if (slot == context.root) return;
            const parent_slot = shell.parent orelse return error.InvalidState;
            const shell_start = partitionStart(Owner, context.table);
            const shell_end = shell_start + @as(usize, Owner.table_declarations[context.table].partition_capacity);
            if (!inShellRange(parent_slot, shell_start, shell_end)) return error.InvalidState;
            const parent = &owner.shells[parent_slot];
            const parent_end = @as(usize, parent.start) + @as(usize, parent.capacity);
            if (parent.table != context.table or parent.uid == null or start < @as(usize, parent.start) or end > parent_end)
                return error.InvalidState;
        }

        fn applyShellAttributes(owner: *Owner, context: UpdateWalk, slot: u32) error{}!void {
            const shell = &owner.shells[slot];
            shell.attributes = context.attributes.apply(shell.attributes);
        }

        fn walkSubtree(owner: *Owner, table: u32, root: u32, context: anytype, comptime visit: anytype) !void {
            const shell_start = partitionStart(Owner, table);
            const shell_end = shell_start + @as(usize, Owner.table_declarations[table].partition_capacity);
            if (!inShellRange(root, shell_start, shell_end)) return error.InvalidState;
            if (owner.shells[root].table != table or owner.shells[root].uid == null) return error.InvalidState;
            if (owner.shells[root].parent) |parent_slot| {
                if (!inShellRange(parent_slot, shell_start, shell_end)) return error.InvalidState;
                const parent = &owner.shells[parent_slot];
                if (parent.table != table or parent.uid == null) return error.InvalidState;
            }

            var current = root;
            var visited: usize = 0;
            while (true) {
                visited += 1;
                if (visited > Owner.table_declarations[table].partition_capacity) return error.InvalidState;
                try visit(owner, context, current);
                const shell = &owner.shells[current];
                if (shell.first_child != types.no_slot) {
                    const child = shell.first_child;
                    if (!inShellRange(child, shell_start, shell_end)) return error.InvalidState;
                    const child_shell = &owner.shells[child];
                    if (child_shell.table != table or child_shell.uid == null or child_shell.parent != current)
                        return error.InvalidState;
                    current = child;
                    continue;
                }

                while (current != root) {
                    const finished = &owner.shells[current];
                    if (finished.next_sibling != types.no_slot) {
                        const parent_slot = finished.parent orelse return error.InvalidState;
                        if (!inShellRange(finished.next_sibling, shell_start, shell_end)) return error.InvalidState;
                        const sibling = &owner.shells[finished.next_sibling];
                        if (sibling.table != table or sibling.uid == null or sibling.parent != parent_slot)
                            return error.InvalidState;
                        current = finished.next_sibling;
                        break;
                    }
                    current = finished.parent orelse return error.InvalidState;
                    if (!inShellRange(current, shell_start, shell_end)) return error.InvalidState;
                }
                if (current == root) break;
            }
        }

        fn validateRootLink(owner: *Owner, table: u32, root: u32, parent: ?u32) !void {
            try validateRootSiblings(owner, table, parent, &.{root});
        }

        fn validateRootSiblings(owner: *Owner, table: u32, parent: ?u32, roots: []const u32) !void {
            const first = if (parent) |slot| blk: {
                const shell_start = partitionStart(Owner, table);
                const shell_end = shell_start + @as(usize, Owner.table_declarations[table].partition_capacity);
                if (!inShellRange(slot, shell_start, shell_end)) return error.InvalidState;
                const shell = &owner.shells[slot];
                if (shell.table != table or shell.uid == null) return error.InvalidState;
                break :blk shell.first_child;
            } else owner.tables[table].first_partition;
            const shell_start = partitionStart(Owner, table);
            const shell_end = shell_start + @as(usize, Owner.table_declarations[table].partition_capacity);
            for (roots) |root_slot| {
                if (!inShellRange(root_slot, shell_start, shell_end)) return error.InvalidState;
                const root = &owner.shells[root_slot];
                if (root.table != table or root.uid == null or root.parent != parent or root.capacity == 0)
                    return error.InvalidState;
                const root_start = @as(u64, root.start);
                const root_end = root_start + @as(u64, root.capacity);
                if (parent) |parent_slot| {
                    const parent_shell = &owner.shells[parent_slot];
                    const parent_start = @as(u64, parent_shell.start);
                    const parent_end = parent_start + @as(u64, parent_shell.capacity);
                    if (root_start < parent_start or root_end > parent_end) return error.InvalidState;
                } else {
                    const resource_start = @as(u64, @intCast(resourceStart(Owner, table)));
                    const resource_end = resource_start + @as(u64, Owner.table_declarations[table].capacity);
                    if (root_start < resource_start or root_end > resource_end) return error.InvalidState;
                }
            }
            var current = first;
            var steps: usize = 0;
            var found: usize = 0;
            while (current != types.no_slot) {
                if (steps >= Owner.table_declarations[table].partition_capacity or
                    !inShellRange(current, shell_start, shell_end)) return error.InvalidState;
                const shell = &owner.shells[current];
                if (shell.table != table or shell.uid == null or shell.parent != parent) return error.InvalidState;
                if (containsSorted(roots, current)) found += 1;
                current = shell.next_sibling;
                steps += 1;
            }
            if (found != roots.len) return error.InvalidState;
        }

        fn validateResourceIndex(owner: *Owner, table: u32, slot: u32, uid: Uid) !void {
            const position = (try owner.index.find(uid)) orelse return error.InvalidState;
            if (position.kind != .resource or position.table != table or position.slot != slot)
                return error.InvalidState;
        }

        fn validatePartitionIndex(owner: *Owner, table: u32, slot: u32, uid: Uid) !void {
            const position = (try owner.index.find(uid)) orelse return error.InvalidState;
            if (position.kind != .partition or position.table != table or position.slot != slot)
                return error.InvalidState;
        }

        fn validateFixedRow(owner: *Owner, table: u32, slot: u32, row: anytype) !void {
            const declaration_id = row.fixed_declaration orelse return error.InvalidState;
            if (@as(usize, declaration_id) >= Owner.fixed_declarations.len) return error.InvalidState;
            const declaration = Owner.fixed_declarations[declaration_id];
            if (declaration.table != table or row.uid == null or !row.uid.?.eql(declaration.uid))
                return error.InvalidState;
            if (declaration.parent) |parent_uid| {
                const parent_slot = row.parent orelse return error.InvalidState;
                if (@as(usize, parent_slot) >= owner.shells.len) return error.InvalidState;
                const parent = &owner.shells[parent_slot];
                if (parent.uid == null or !parent.uid.?.eql(parent_uid) or
                    row.fixed_parent_number != parent.allocation_number) return error.InvalidState;
            } else if (row.parent != null or row.fixed_parent_number != 0) {
                return error.InvalidState;
            }
            const binding = &owner.bindings[declaration_id];
            if (binding.slot.load(.acquire) != slot or binding.parent_number.load(.acquire) != row.fixed_parent_number)
                return error.InvalidState;
        }

        fn validateInstallRange(
            owner: *Owner,
            range: Range,
            start: u32,
            interval_capacity: u32,
            requested_capacity: u32,
            parent: ?u32,
        ) !void {
            if (requested_capacity == 0 or requested_capacity > interval_capacity) return error.InvalidArgument;
            const interval_start = @as(u64, start);
            const interval_end = interval_start + @as(u64, interval_capacity);
            if (parent) |parent_slot| {
                const shell_start = partitionStart(Owner, range.table);
                const shell_end = shell_start + @as(usize, Owner.table_declarations[range.table].partition_capacity);
                if (!inShellRange(parent_slot, shell_start, shell_end)) return error.InvalidState;
                const parent_shell = &owner.shells[parent_slot];
                if (parent_shell.uid == null or parent_shell.table != range.table) return error.InvalidState;
                try validateRootLink(owner, range.table, parent_slot, parent_shell.parent);
                const parent_start = @as(u64, parent_shell.start);
                const parent_end = parent_start + @as(u64, parent_shell.capacity);
                if (interval_start < parent_start or interval_end > parent_end) return error.InvalidArgument;
            } else {
                const declaration = Owner.table_declarations[range.table];
                const resource_base = @as(u64, @intCast(resourceStart(Owner, range.table)));
                const reserved_start = resource_base + @as(u64, declaration.reserved_start);
                const reserved_end = reserved_start + @as(u64, declaration.reserved_count);
                if (interval_start < reserved_start or interval_end > reserved_end) return error.InvalidArgument;
            }
        }

        fn chooseInstallShell(owner: *Owner, table: u32, closed_shells: []const u32) !ShellChoice {
            const state = &owner.tables[table];
            const shell_start = partitionStart(Owner, table);
            const shell_end = shell_start + @as(usize, Owner.table_declarations[table].partition_capacity);
            if (state.free_partition != types.no_slot) {
                const slot = state.free_partition;
                if (!inShellRange(slot, shell_start, shell_end)) return error.InvalidState;
                const shell = &owner.shells[slot];
                if (shell.table != table or shell.uid != null or shell.parent != null or shell.start != 0 or
                    shell.capacity != 0 or shell.allocation_number != 0 or shell.first_child != types.no_slot)
                    return error.InvalidState;
                const next = shell.next_sibling;
                if (next != types.no_slot and (next == slot or !inShellRange(next, shell_start, shell_end)))
                    return error.InvalidState;
                return .{ .slot = slot, .from_free_list = true, .next_free = next };
            }
            if (closed_shells.len == 0) return error.SpecificationLimit;
            return .{ .slot = closed_shells[0], .from_free_list = false, .next_free = types.no_slot };
        }

        fn parentAttributes(owner: *Owner, table: u32, parent: ?u32) !@TypeOf(Owner.table_declarations[0].attributes) {
            if (parent) |slot| {
                if (@as(usize, slot) >= owner.shells.len) return error.InvalidState;
                const shell = &owner.shells[slot];
                if (shell.table != table or shell.uid == null) return error.InvalidState;
                return shell.attributes;
            }
            return Owner.table_declarations[table].attributes;
        }

        fn unlinkRoots(owner: *Owner, table: u32, parent: ?u32, closure: []const u32) void {
            var link: *u32 = if (parent) |slot| &owner.shells[slot].first_child else &owner.tables[table].first_partition;
            var steps: usize = 0;
            while (link.* != types.no_slot) {
                const current = link.*;
                const shell = &owner.shells[current];
                if (shell.parent == parent and containsSorted(closure, current)) {
                    link.* = shell.next_sibling;
                } else {
                    link = &shell.next_sibling;
                }
                steps += 1;
                std.debug.assert(steps <= Owner.table_declarations[table].partition_capacity);
            }
        }
    };
}

fn resolveRange(comptime Owner: type, owner: anytype, start: u32, capacity: u32) !For(Owner).Range {
    if (capacity == 0 or @as(usize, start) >= owner.rows.len) return error.InvalidArgument;
    const end_u64 = @as(u64, start) + @as(u64, capacity);
    if (end_u64 > @as(u64, @intCast(owner.rows.len))) return error.InvalidArgument;
    const table = owner.rows[start].table;
    if (@as(usize, table) >= Owner.table_declarations.len) return error.InvalidState;
    const declaration = Owner.table_declarations[table];
    const table_start = resourceStart(Owner, table);
    const table_end = table_start + @as(usize, declaration.capacity);
    const start_usize: usize = @intCast(start);
    const end: usize = @intCast(end_u64);
    if (start_usize < table_start or end > table_end) return error.InvalidArgument;
    return .{ .table = table, .start = start_usize, .end = end };
}

fn resourceStart(comptime Owner: type, table: u32) usize {
    var start: usize = 0;
    for (Owner.table_declarations[0..@as(usize, table)]) |declaration| start += @as(usize, declaration.capacity);
    return start;
}

fn partitionStart(comptime Owner: type, table: u32) usize {
    var start: usize = 0;
    for (Owner.table_declarations[0..@as(usize, table)]) |declaration| start += @as(usize, declaration.partition_capacity);
    return start;
}

fn inShellRange(slot: u32, start: usize, end: usize) bool {
    return @as(usize, slot) >= start and @as(usize, slot) < end;
}

fn hasAttributeChange(attributes: anytype) bool {
    return attributes.recoverability != null or attributes.recovery_cost_coefficient != null or
        attributes.access_loss_impact != null;
}

fn containsSorted(values: []const u32, value: u32) bool {
    var low: usize = 0;
    var high = values.len;
    while (low < high) {
        const middle = low + (high - low) / 2;
        if (values[middle] < value) {
            low = middle + 1;
        } else {
            high = middle;
        }
    }
    return low < values.len and values[low] == value;
}

fn lessThanU32(_: void, left: u32, right: u32) bool {
    return left < right;
}

fn addBytes(total: u64, bytes: u64) !u64 {
    return std.math.add(u64, total, bytes) catch error.SizeOverflow;
}

fn subtractBytes(total: u64, bytes: u64) !u64 {
    return std.math.sub(u64, total, bytes) catch error.InvalidState;
}

fn unlockRows(owner: anytype, start: usize, count: *usize) void {
    while (count.* != 0) {
        count.* -= 1;
        owner.rows[start + count.*].lock.unlock();
    }
}

fn unlockCandidateRows(owner: anytype, work: anytype, count: *usize) void {
    while (count.* != 0) {
        count.* -= 1;
        owner.rows[work.rows[count.*].slot].lock.unlock();
    }
}

const FakeTreeApi = struct {
    pub const Update = struct { attributes: ?types.AttributeUpdate = null };
    pub const UpdateResult = void;
};

const FakeTreeIndex = struct {
    pub const LocationKind = enum { resource, partition };
    pub const Location = struct { kind: LocationKind, table: u32, slot: u32 };
    pub const RemovePlan = struct {};
    pub const Publication = struct {};
};

const FakeTreeOwner = struct {
    pub const uid_type = u32;
    pub const api = FakeTreeApi;
    pub const Declaration = struct {
        capacity: u32,
        partition_capacity: u32,
        attributes: types.Attributes,
    };
    pub const Shell = struct {
        uid: ?u32 = null,
        table: u32 = 0,
        parent: ?u32 = null,
        start: u32 = 0,
        capacity: u32 = 0,
        allocation_number: u64 = 0,
        first_child: u32 = types.no_slot,
        next_sibling: u32 = types.no_slot,
    };
    pub const Table = struct { first_partition: u32 = types.no_slot };
    pub const Row = struct { table: u32 = 0 };
    pub const Workspace = struct {};

    pub const table_declarations = [_]Declaration{
        .{
            .capacity = 12,
            .partition_capacity = 6,
            .attributes = .{
                .recoverability = .recoverable,
                .recovery_cost_coefficient = 1,
                .access_loss_impact = .unobservable_now,
            },
        },
    };

    rows: []Row,
    shells: []Shell,
    tables: []Table,
    index: FakeTreeIndex = .{},
};

test "collectShells handles equal-capacity nesting and roots aliasing its scratch" {
    var rows: [12]FakeTreeOwner.Row = undefined;
    for (&rows) |*row| row.* = .{ .table = 0 };
    var shells = [_]FakeTreeOwner.Shell{
        .{ .uid = 10, .table = 0, .start = 0, .capacity = 8, .allocation_number = 1, .first_child = 1, .next_sibling = 3 },
        .{ .uid = 11, .table = 0, .parent = 0, .start = 0, .capacity = 8, .allocation_number = 2, .first_child = 2 },
        .{ .uid = 12, .table = 0, .parent = 1, .start = 2, .capacity = 2, .allocation_number = 3 },
        .{ .uid = 13, .table = 0, .start = 8, .capacity = 4, .allocation_number = 4 },
        .{},
        .{},
    };
    var tables = [_]FakeTreeOwner.Table{.{ .first_partition = 0 }};
    var owner = FakeTreeOwner{
        .rows = &rows,
        .shells = &shells,
        .tables = &tables,
    };
    var scratch: [6]u32 = .{ 3, 0, 0, 0, 0, 0 };
    const closed = try For(FakeTreeOwner).collectShells(
        &owner,
        0,
        0,
        12,
        scratch[0..2],
        null,
        &scratch,
    );
    try std.testing.expectEqualSlices(u32, &.{ 0, 1, 2, 3 }, closed);
}

test "collectShells rejects overlapping sibling resource intervals" {
    var rows: [12]FakeTreeOwner.Row = undefined;
    for (&rows) |*row| row.* = .{ .table = 0 };
    var shells = [_]FakeTreeOwner.Shell{
        .{ .uid = 10, .table = 0, .start = 0, .capacity = 8, .allocation_number = 1, .next_sibling = 3 },
        .{},
        .{},
        .{ .uid = 13, .table = 0, .start = 7, .capacity = 4, .allocation_number = 2 },
        .{},
        .{},
    };
    var tables = [_]FakeTreeOwner.Table{.{ .first_partition = 0 }};
    var owner = FakeTreeOwner{
        .rows = &rows,
        .shells = &shells,
        .tables = &tables,
    };
    var scratch: [6]u32 = .{ 3, 0, 0, 0, 0, 0 };
    try std.testing.expectError(
        error.InvalidState,
        For(FakeTreeOwner).collectShells(&owner, 0, 0, 12, scratch[0..2], null, &scratch),
    );
}
