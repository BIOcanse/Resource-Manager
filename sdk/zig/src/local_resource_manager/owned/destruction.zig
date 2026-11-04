//! Whole-instance close/force-release. Payload release starts only after all
//! resources and the complete index transaction have passed preflight.
const std = @import("std");
const types = @import("types.zig");
const release_check = @import("../release_check.zig");

pub fn For(comptime Owner: type) type {
    const Index = @TypeOf(@as(Owner, undefined).index);
    const Transactions = Index.Transactions;
    const Uid = Owner.uid_type;
    return struct {
        pub const Notice = struct { table: u32, uid: Uid, allocation_number: u64 };
        pub const Workspace = struct {
            remove_ids: []u32,
            notices: []Notice,
            table_views: []types.TableView,
            index_workspace: Transactions.Workspace,
        };

        pub fn run(owner: *Owner, forced: bool, work: *Workspace) !void {
            if (work.table_views.len < owner.tables.len) return error.WorkspaceTooSmall;
            var table_locks: usize = 0;
            var row_locks: usize = 0;
            var notice_count: usize = 0;
            var committed = false;
            defer {
                while (row_locks != 0) {
                    row_locks -= 1;
                    owner.rows[row_locks].lock.unlock();
                }
                while (table_locks != 0) {
                    table_locks -= 1;
                    owner.tables[table_locks].write.unlock();
                }
                if (committed) {
                    for (work.notices[0..notice_count]) |notice|
                        owner.tables[notice.table].notifier.dispatch(notice.uid, notice.allocation_number);
                }
            }
            while (table_locks < owner.tables.len) : (table_locks += 1) owner.tables[table_locks].write.lock();
            while (row_locks < owner.rows.len) : (row_locks += 1) owner.rows[row_locks].lock.lock();
            for (work.table_views[0..owner.tables.len]) |*view|
                view.* = .{ .registered_bytes = 0, .system_page_bytes = 0 };

            var remove_count: usize = 0;
            for (Owner.table_declarations, 0..) |declaration, table| {
                const start = Owner.resourceStart(table);
                const subscribed = owner.tables[table].notifier.hasSubscribers();
                for (owner.rows[start..][0..declaration.capacity], start..) |*row, slot| {
                    owner.flushWindowCache(row);
                    if (row.table != table) return error.InvalidState;
                    const entry = try owner.index.entryAt(@intCast(slot));
                    if (row.uid) |uid| {
                        const registered = row.resource.registration orelse return error.InvalidState;
                        if (!forced and row.resource.discardPending()) return error.CleanupPending;
                        const indexed = entry orelse return error.InvalidState;
                        if (!indexed.uid.eql(uid) or indexed.location.kind != .resource or
                            indexed.location.table != table or indexed.location.slot != slot)
                            return error.InvalidState;
                        if (registered.region != null) {
                            if (row.resource.generation == 0) return error.InvalidState;
                            if (!forced) {
                                if (row.resource.use_count != 0 or row.resource.loading) return error.InUse;
                                if (!row.attributes.safe()) return error.Blocked;
                                if (release_check.check(declaration.policy.release_check, registered.allocation_bytes, registered.allocation_bytes, false) != .allowed)
                                    return error.ChangedBeyondTolerance;
                            }
                            if (subscribed and !row.resource.discardPending()) try saveNotice(work, &notice_count, .{
                                .table = @intCast(table),
                                .uid = uid,
                                .allocation_number = row.resource.generation,
                            });
                        } else if (row.resource.use_count != 0 or row.resource.loading) return error.InvalidState;
                        const view = &work.table_views[table];
                        view.registered_bytes = std.math.add(u64, view.registered_bytes, registered.allocation_bytes) catch return error.InvalidState;
                        view.system_page_bytes = std.math.add(u64, view.system_page_bytes, registered.allocatedPageBytes()) catch return error.InvalidState;
                        try saveRemoval(work, &remove_count, @intCast(slot));
                    } else if (entry != null or row.resource.registration != null or row.resource.use_count != 0 or
                        row.resource.loading or row.fixed_declaration != null) return error.InvalidState;
                }
                const shell_start = Owner.partitionStart(table);
                for (owner.shells[shell_start..][0..declaration.partition_capacity], shell_start..) |*shell, slot| {
                    if (shell.table != table) return error.InvalidState;
                    const node_id = std.math.cast(u32, owner.rows.len + slot) orelse return error.InvalidState;
                    const entry = try owner.index.entryAt(node_id);
                    if (shell.uid) |uid| {
                        const indexed = entry orelse return error.InvalidState;
                        if (shell.allocation_number == 0 or !indexed.uid.eql(uid) or
                            indexed.location.kind != .partition or indexed.location.table != table or
                            indexed.location.slot != slot) return error.InvalidState;
                        try saveRemoval(work, &remove_count, node_id);
                        if (subscribed) try saveNotice(work, &notice_count, .{
                            .table = @intCast(table),
                            .uid = uid,
                            .allocation_number = shell.allocation_number,
                        });
                    } else if (entry != null) return error.InvalidState;
                }
                const view = work.table_views[table];
                if (view.registered_bytes != owner.tables[table].registered_bytes or
                    view.system_page_bytes != owner.tables[table].system_page_bytes) return error.InvalidState;
            }
            const Resource = @TypeOf(owner.rows[0].resource);
            var empty = try Resource.dynamic(0);
            empty.registration = null;
            var transaction = try Transactions.prepare(&owner.index, work.remove_ids[0..remove_count], &.{}, &work.index_workspace);
            defer transaction.abort();
            // Owned OS release failures are fatal invariants; no ordinary
            // failure remains after the first payload is returned to the OS.
            for (owner.rows) |*row| {
                if (row.uid == null) continue;
                owner.collectLeaseWrites(row);
                if (forced) row.resource.forceRelease() catch @panic("ledger OS release invariant failed") else {
                    const result = row.resource.unload() catch @panic("ledger OS release invariant failed");
                    if (result != .closed) @panic("ledger destruction preflight invariant failed");
                }
            }
            for (owner.rows) |*row| owner.recordAllocationRelease(row);
            transaction.commit();
            for (owner.bindings) |*binding| {
                binding.slot.store(types.no_slot, .release);
                binding.parent_number.store(0, .release);
            }
            for (Owner.table_declarations, 0..) |declaration, table| {
                const start = Owner.resourceStart(table);
                for (owner.rows[start..][0..declaration.capacity]) |*row| {
                    row.uid = null;
                    row.parent = null;
                    row.fixed_declaration = null;
                    row.fixed_parent_number = 0;
                    row.attributes = declaration.attributes;
                    row.resource = empty;
                }
                const shell_start = Owner.partitionStart(table);
                for (owner.shells[shell_start..][0..declaration.partition_capacity], shell_start..) |*shell, slot| {
                    shell.uid = null;
                    shell.parent = null;
                    shell.start = 0;
                    shell.capacity = 0;
                    shell.allocation_number = 0;
                    shell.attributes = declaration.attributes;
                    shell.history = .{};
                    shell.first_child = types.no_slot;
                    shell.next_sibling = if (slot + 1 == shell_start + declaration.partition_capacity)
                        types.no_slot
                    else
                        @intCast(slot + 1);
                }
                owner.tables[table].registered_bytes = 0;
                owner.tables[table].system_page_bytes = 0;
                owner.tables[table].pending_discards = 0;
                owner.tables[table].first_partition = types.no_slot;
                owner.tables[table].free_partition = if (declaration.partition_capacity == 0)
                    types.no_slot
                else
                    @intCast(shell_start);
            }
            committed = true;
        }

        fn saveRemoval(work: *Workspace, count: *usize, node_id: u32) !void {
            if (count.* == work.remove_ids.len) return error.WorkspaceTooSmall;
            work.remove_ids[count.*] = node_id;
            count.* += 1;
        }
        fn saveNotice(work: *Workspace, count: *usize, notice: Notice) !void {
            if (count.* == work.notices.len) return error.WorkspaceTooSmall;
            work.notices[count.*] = notice;
            count.* += 1;
        }
    };
}
