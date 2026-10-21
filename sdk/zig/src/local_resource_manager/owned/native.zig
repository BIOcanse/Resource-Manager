//! Independent ledger Native C ABI candidate.
//!
//! One generated consumer root instantiates Binding once with its static shape,
//! C2 index, and immutable build fingerprints. The paired source remains in the
//! registered local task as a review mirror of the SDK integration.
const std = @import("std");

// These imports resolve from the intended `owned/native.zig` location.
const uid_module = @import("../uid.zig");
const lifecycle = @import("../memory_resource.zig");
const types = @import("types.zig");
const c2_index = @import("index.zig");
const notifications = @import("notifications.zig");
const owner_module = @import("owner.zig");
const Lock = @import("sync.zig").Lock;

pub const BuildIdentity = struct {
    structure_hash: [32]u8,
    policy_hash: [32]u8,
};

pub const Status = enum(i32) {
    ok = 0,
    in_memory = 1,
    newly_allocated = 2,
    not_in_memory = 3,
    loading = 4,
    completed = 5,
    force_released = 6,
    updated = 7,
    registration_only = 8,
    in_use = 9,
    blocked = 10,
    nonexistent = 11,
    input_error = 12,
    allocation_failed = 13,
    specification_limit = 14,
    changed_beyond_tolerance = 15,
    unsupported_platform = 16,
    invalid_state = 17,
    partition_created = 18,
    partition_exists = 19,
    internal_error = 255,
};

pub const Lease = extern struct {
    slot: u32,
    reserved: u32,
    allocation_number: u64,
    use_generation: u64,
};

pub const Borrow = extern struct {
    lease: Lease,
    data: ?[*]u8,
    length: u64,
};

pub const Attributes = extern struct {
    recoverability: u32,
    recovery_cost_coefficient: u32,
    access_loss_impact: u32,
};

pub const Update = extern struct {
    present_mask: u32,
    recoverability: u32,
    recovery_cost_coefficient: u32,
    access_loss_impact: u32,
    allocation_bytes: u64,
};

pub const Target = extern struct {
    kind: u32,
    table: u32,
    parent_uid: ?[*]const u8,
    parent_uid_length: u64,
};

pub const PartitionResult = extern struct { allocation_number: u64 };

pub const Subscription = extern struct {
    table: u32,
    slot: u32,
    generation: u64,
};

pub const TableView = extern struct {
    registered_bytes: u64,
    system_page_bytes: u64,
};

pub const DevelopmentStatistics = @import("allocation_statistics.zig").Counters;

pub const TickResult = extern struct {
    released: u64,
    scanned: u64,
};

pub const TypeLayout = extern struct {
    size: u32,
    alignment: u32,
    field_count: u32,
    reserved: u32,
    field_offsets: [8]u32,
};

pub const Descriptor = extern struct {
    descriptor_size: u32,
    abi_version: u32,
    uid_width_bits: u32,
    candidate_id: u32,
    table_count: u32,
    fixed_count: u32,
    max_subscribers_per_table: u32,
    resource_capacity: u32,
    partition_capacity: u32,
    index_node_count: u32,
    index_home_count: u32,
    reserved: u32,
    native_page_size: u64,
    instance_size: u64,
    instance_alignment: u64,
    operation_workspace_size: u64,
    operation_workspace_alignment: u64,
    tick_workspace_size: u64,
    tick_workspace_alignment: u64,
    destroy_workspace_size: u64,
    destroy_workspace_alignment: u64,
    structure_hash: [32]u8,
    policy_hash: [32]u8,
    lease_layout: TypeLayout,
    borrow_layout: TypeLayout,
    attributes_layout: TypeLayout,
    update_layout: TypeLayout,
    target_layout: TypeLayout,
    partition_result_layout: TypeLayout,
    subscription_layout: TypeLayout,
    table_view_layout: TypeLayout,
    tick_result_layout: TypeLayout,
};

pub const Callback = *const fn (
    context: ?*anyopaque,
    uid_bytes: [*]const u8,
    uid_length: u64,
    allocation_number: u64,
) callconv(.c) void;

const active_magic: u64 = 0x4c45444745524e31; // "LEDGERN1"
const callback_free: u8 = 0;
const callback_reserved: u8 = 1;
const callback_active: u8 = 2;
const callback_retiring: u8 = 3;

/// Compose one static C2 owner and the generic `rm_ledger_*` export set.
/// `identity` is an immutable build-generation result; it is copied verbatim to
/// the descriptor and is never recomputed or interpreted by this module.
pub fn Binding(
    comptime config: types.Config,
    comptime declarations: []const types.Table,
    comptime fixed: []const types.For(uid_module.Identifier(config.uid_width)).Fixed,
    comptime identity: BuildIdentity,
) type {
    const Uid = uid_module.Identifier(config.uid_width);
    const Index = c2_index.Candidate(Uid, .c2_chain);
    const Ledger = owner_module.Owner(config, declarations, fixed, Index);
    const Notifier = notifications.Notifier(Uid);

    const dimensions = comptime blk: {
        var max_resources: usize = 0;
        var max_partitions: usize = 0;
        var max_removals: usize = 0;
        var max_puts: usize = 0;
        var max_transaction_operations: usize = 0;
        var tick_cells: usize = 0;
        for (declarations, 0..) |declaration, table| {
            const resources: usize = declaration.capacity;
            const partitions: usize = declaration.partition_capacity;
            var fixed_for_table: usize = 0;
            for (fixed) |item| {
                if (item.table == table) fixed_for_table += 1;
            }
            const removals = resources + partitions;
            // A valid installed shell can bind at most one fixed declaration
            // per resource ordinal, even if the static declaration list has
            // many alternative parent groups.
            const puts = @as(usize, @min(fixed_for_table, resources)) + 1;
            max_resources = @max(max_resources, resources);
            max_partitions = @max(max_partitions, partitions);
            max_removals = @max(max_removals, removals);
            max_puts = @max(max_puts, puts);
            max_transaction_operations = @max(max_transaction_operations, removals + puts);
            tick_cells += @min(@as(usize, declaration.policy.tick_maximum_actions), resources);
        }
        break :blk .{
            .max_resources = max_resources,
            .max_partitions = max_partitions,
            .max_nodes_per_table = max_removals,
            .max_puts_per_table = max_puts,
            .max_transaction_operations = max_transaction_operations,
            .tick_cells = tick_cells,
        };
    };

    const total_nodes = Ledger.resource_capacity + Ledger.partition_capacity;
    const table_count = Ledger.table_count;

    if (total_nodes > Index.max_indexed_nodes)
        @compileError("compiled resource and partition rows exceed the C2 node limit");
    if (config.index_buckets == 0 or config.index_buckets > Index.max_homes)
        @compileError("compiled index bucket count exceeds the C2 home limit");
    if (fixed.len > std.math.maxInt(u32))
        @compileError("fixed declaration IDs must fit the Native u32 field");

    const CallbackCell = struct {
        lock: Lock = .{},
        state: u8 = callback_free,
        callback: ?Callback = null,
        context: ?*anyopaque = null,
        table: u32 = 0,
        slot: u32 = 0,
        generation: u64 = 0,
    };

    // Persistent owner state only. Call scratch is never embedded here.
    const Storage = struct {
        magic: u64 = 0,
        initialized: bool = false,
        rows: [Ledger.resource_capacity]Ledger.Row = undefined,
        shells: [Ledger.partition_capacity]Ledger.Shell = undefined,
        tables: [table_count]Ledger.TableState = undefined,
        bindings: [fixed.len]Ledger.FixedBinding = undefined,
        nodes: [total_nodes]Index.Node = undefined,
        homes: [config.index_buckets]Index.Home = undefined,
        records: [table_count][notifications.max_subscriptions]Notifier.Record = undefined,
        callback_cells: [table_count][notifications.max_subscriptions]CallbackCell = undefined,
        ledger: Ledger = undefined,
    };

    // One-table scratch, sized for the largest declared table, not the sum of
    // all tables. Callers keep it exclusive until synchronous notification
    // flush returns.
    const OperationWorkspace = struct {
        candidates: [dimensions.max_resources]Ledger.Candidate = undefined,
        rows: [dimensions.max_resources]Ledger.Candidate = undefined,
        shells: [dimensions.max_partitions]u32 = undefined,
        owner_homes: [dimensions.max_partitions]u32 = undefined,
        notes: [dimensions.max_nodes_per_table]Ledger.api.Notification = undefined,
        remove_ids: [dimensions.max_nodes_per_table]u32 = undefined,
        puts: [dimensions.max_puts_per_table]Index.Transactions.Put = undefined,
        cells: [dimensions.max_resources]Ledger.PartitionPlanning.Cell = undefined,
        children: [dimensions.max_partitions]Ledger.PartitionPlanning.Child = undefined,
        child_slots: [dimensions.max_partitions]u32 = undefined,
        numbers: [dimensions.max_partitions]u64 = undefined,
        merge_a: [dimensions.max_partitions]u64 = undefined,
        merge_b: [dimensions.max_partitions]u64 = undefined,
        best_numbers: [dimensions.max_partitions]u64 = undefined,
        transaction_homes: [dimensions.max_transaction_operations]u32 = undefined,
        transaction_nodes: [dimensions.max_transaction_operations]u32 = undefined,
        transaction_publications: [dimensions.max_transaction_operations]Index.Publication = undefined,
        transaction_removals: [dimensions.max_nodes_per_table]Index.RemovePlan = undefined,
        transaction_inserts: [dimensions.max_puts_per_table]Index.InsertPlan = undefined,

        fn ownerWorkspace(self: *@This()) Ledger.Workspace {
            return .{
                .candidates = self.candidates[0..],
                .rows = self.rows[0..],
                .shells = self.shells[0..],
                .homes = self.owner_homes[0..],
                .notes = self.notes[0..],
                .remove_ids = self.remove_ids[0..],
                .puts = self.puts[0..],
                .index_workspace = .{
                    .homes = self.transaction_homes[0..],
                    .nodes = self.transaction_nodes[0..],
                    .publications = self.transaction_publications[0..],
                    .removals = self.transaction_removals[0..],
                    .inserts = self.transaction_inserts[0..],
                },
                .cells = self.cells[0..],
                .children = self.children[0..],
                .child_slots = self.child_slots[0..],
                .numbers = self.numbers[0..],
                .merge_a = self.merge_a[0..],
                .merge_b = self.merge_b[0..],
                .best_numbers = self.best_numbers[0..],
            };
        }
    };

    // Tick scratch is a packed per-table set. Owner.tick processes each table
    // sequentially and flushes its notes before advancing.
    const TickWorkspace = struct {
        candidates: [dimensions.tick_cells]Ledger.Candidate = undefined,
        notes: [dimensions.tick_cells]Ledger.api.Notification = undefined,
        workspaces: [table_count]Ledger.Workspace = undefined,
    };

    const DestroyWorkspace = struct {
        remove_ids: [total_nodes]u32 = undefined,
        notices: [total_nodes]Ledger.Destruction.Notice = undefined,
        table_views: [table_count]types.TableView = undefined,
        transaction_homes: [total_nodes]u32 = undefined,
        transaction_nodes: [total_nodes]u32 = undefined,
        transaction_publications: [total_nodes]Index.Publication = undefined,
        transaction_removals: [total_nodes]Index.RemovePlan = undefined,
    };

    comptime {
        if (@sizeOf(usize) != 8) @compileError("the ledger Native C ABI draft targets 64-bit Windows");
        if (@sizeOf(Descriptor) != 616 or @alignOf(Descriptor) != 8)
            @compileError("rm_ledger_descriptor no longer matches the paired C header");
        if (@sizeOf(Lease) != 24 or @alignOf(Lease) != 8 or
            @sizeOf(Borrow) != 40 or @alignOf(Borrow) != 8 or
            @sizeOf(Attributes) != 12 or @alignOf(Attributes) != 4 or
            @sizeOf(Update) != 24 or @alignOf(Update) != 8 or
            @sizeOf(Target) != 24 or @alignOf(Target) != 8 or
            @sizeOf(PartitionResult) != 8 or @alignOf(PartitionResult) != 8 or
            @sizeOf(Subscription) != 16 or @alignOf(Subscription) != 8 or
            @sizeOf(TableView) != 16 or @alignOf(TableView) != 8 or
            @sizeOf(TickResult) != 16 or @alignOf(TickResult) != 8)
            @compileError("a public rm_ledger record no longer matches the paired C header");
    }

    return struct {
        const Self = @This();

        // Compile-time aliases let Zig composition/tests bind caller-owned
        // memory while the public C ABI remains opaque and descriptor-sized.
        pub const StateType = Storage;
        pub const OperationWorkspaceType = OperationWorkspace;
        pub const TickWorkspaceType = TickWorkspace;
        pub const DestroyWorkspaceType = DestroyWorkspace;

        /// Referencing this function from the generated composition root makes
        /// every generic export part of that consumer build.
        pub fn exportAll() void {
            _ = &Self.rm_ledger_query_descriptor;
            _ = &Self.rm_ledger_create;
            _ = &Self.rm_ledger_destroy;
            _ = &Self.rm_ledger_use;
            _ = &Self.rm_ledger_use_fixed;
            _ = &Self.rm_ledger_push;
            _ = &Self.rm_ledger_push_partition;
            _ = &Self.rm_ledger_end_use;
            _ = &Self.rm_ledger_update_uid;
            _ = &Self.rm_ledger_update_lease;
            _ = &Self.rm_ledger_close;
            _ = &Self.rm_ledger_force_release;
            _ = &Self.rm_ledger_set_mode;
            _ = &Self.rm_ledger_get_table_view;
            _ = &Self.rm_ledger_tick;
            _ = &Self.rm_ledger_subscribe;
            _ = &Self.rm_ledger_unsubscribe;
            if (config.development_allocation_statistics)
                _ = &Self.DevelopmentStatisticsApi.rm_ledger_get_development_statistics;
        }

        pub const DevelopmentStatisticsApi = if (config.development_allocation_statistics) struct {
            pub export fn rm_ledger_get_development_statistics(
                raw_instance: ?*anyopaque,
                table: u32,
                out_statistics: ?*DevelopmentStatistics,
            ) callconv(.c) i32 {
                const out = validOutput(DevelopmentStatistics, out_statistics, raw_instance) catch return code(.input_error);
                out.* = .{};
                const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
                out.* = Ledger.DevelopmentStatistics.get(&storage.ledger, table) catch |err| return code(errorStatus(err));
                return code(.ok);
            }
        } else struct {};

        pub export fn rm_ledger_query_descriptor(out_descriptor: ?*Descriptor) callconv(.c) i32 {
            const out = validOutput(Descriptor, out_descriptor, null) catch return code(.input_error);
            out.* = descriptorValue();
            return code(.ok);
        }

        pub export fn rm_ledger_create(
            raw_storage: ?*anyopaque,
            storage_bytes: u64,
            out_instance: ?*?*anyopaque,
        ) callconv(.c) i32 {
            const out = validOutput(?*anyopaque, out_instance, raw_storage) catch return code(.input_error);
            out.* = null;
            const raw = raw_storage orelse return code(.input_error);
            if (storage_bytes < @sizeOf(Storage)) return code(.input_error);
            if (@intFromPtr(raw) % @alignOf(Storage) != 0) return code(.input_error);
            if (!validAddressRange(@intFromPtr(raw), @sizeOf(Storage))) return code(.input_error);

            const storage: *Storage = @ptrCast(@alignCast(raw));
            if (storage.magic == active_magic and storage.initialized) return code(.invalid_state);
            storage.magic = 0;
            storage.initialized = false;
            for (&storage.callback_cells) |*table_cells| {
                for (table_cells) |*cell| cell.* = .{};
            }
            var subscriber_slices: [table_count][]Notifier.Record = undefined;
            for (0..table_count) |table| subscriber_slices[table] = storage.records[table][0..];
            storage.ledger = Ledger.init(
                &storage.rows,
                &storage.shells,
                &storage.tables,
                &storage.bindings,
                &storage.nodes,
                &storage.homes,
                &subscriber_slices,
            ) catch |err| return code(errorStatus(err));
            storage.initialized = true;
            storage.magic = active_magic;
            out.* = raw;
            return code(.ok);
        }

        pub export fn rm_ledger_destroy(
            raw_instance: ?*anyopaque,
            forced: u32,
            raw_workspace: ?*anyopaque,
            workspace_bytes: u64,
        ) callconv(.c) i32 {
            if (forced > 1) return code(.input_error);
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            const scratch = typedBuffer(DestroyWorkspace, storage, raw_workspace, workspace_bytes) catch |err|
                return code(errorStatus(err));
            var work = Ledger.Destruction.Workspace{
                .remove_ids = scratch.remove_ids[0..],
                .notices = scratch.notices[0..],
                .table_views = scratch.table_views[0..],
                .index_workspace = .{
                    .homes = scratch.transaction_homes[0..],
                    .nodes = scratch.transaction_nodes[0..],
                    .publications = scratch.transaction_publications[0..],
                    .removals = scratch.transaction_removals[0..],
                    .inserts = &.{},
                },
            };
            storage.ledger.destroy(forced != 0, &work) catch |err| return code(errorStatus(err));
            storage.initialized = false;
            storage.magic = 0;
            return code(.ok);
        }

        pub export fn rm_ledger_use(
            raw_instance: ?*anyopaque,
            uid_bytes: ?[*]const u8,
            uid_length: u64,
            out_borrow: ?*Borrow,
        ) callconv(.c) i32 {
            const out = validOutput(Borrow, out_borrow, raw_instance) catch return code(.input_error);
            if (outputOverlapsBytes(out, uid_bytes, uid_length) or
                inputBytesOverlapStorage(raw_instance, uid_bytes, uid_length)) return code(.input_error);
            out.* = std.mem.zeroes(Borrow);
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            const uid = readUid(uid_bytes, uid_length) catch |err| return code(errorStatus(err));
            const result = storage.ledger.use(uid) catch |err| return code(errorStatus(err));
            return switch (result) {
                .in_memory => |borrow| blk: {
                    out.* = nativeBorrow(borrow);
                    break :blk code(.in_memory);
                },
                .not_in_memory => code(.not_in_memory),
                .loading => code(.loading),
            };
        }

        pub export fn rm_ledger_use_fixed(
            raw_instance: ?*anyopaque,
            declaration: u32,
            out_borrow: ?*Borrow,
        ) callconv(.c) i32 {
            const out = validOutput(Borrow, out_borrow, raw_instance) catch return code(.input_error);
            out.* = std.mem.zeroes(Borrow);
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            const result = storage.ledger.useFixedId(declaration) catch |err| return code(errorStatus(err));
            return switch (result) {
                .in_memory => |borrow| blk: {
                    out.* = nativeBorrow(borrow);
                    break :blk code(.in_memory);
                },
                .not_in_memory => code(.not_in_memory),
                .loading => code(.loading),
            };
        }

        pub export fn rm_ledger_push(
            raw_instance: ?*anyopaque,
            raw_target: ?*const Target,
            uid_bytes: ?[*]const u8,
            uid_length: u64,
            has_allocation_bytes: u32,
            allocation_bytes: u64,
            raw_workspace: ?*anyopaque,
            workspace_bytes: u64,
            out_borrow: ?*Borrow,
        ) callconv(.c) i32 {
            const out = validOutput(Borrow, out_borrow, raw_instance) catch return code(.input_error);
            const target_ptr = raw_target orelse return code(.input_error);
            if (@intFromPtr(target_ptr) % @alignOf(Target) != 0 or
                !validAddressRange(@intFromPtr(target_ptr), @sizeOf(Target))) return code(.input_error);
            if (outputOverlapsRaw(out, target_ptr, @sizeOf(Target)) or
                inputOverlapsStorage(raw_instance, @intFromPtr(target_ptr), @sizeOf(Target)) or
                outputOverlapsBytes(out, uid_bytes, uid_length) or
                inputBytesOverlapStorage(raw_instance, uid_bytes, uid_length) or
                outputOverlapsAddress(out, raw_workspace, @sizeOf(OperationWorkspace))) return code(.input_error);
            const target_value = target_ptr.*;
            if (outputOverlapsBytes(out, target_value.parent_uid, target_value.parent_uid_length) or
                inputBytesOverlapStorage(raw_instance, target_value.parent_uid, target_value.parent_uid_length))
                return code(.input_error);
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            const scratch = typedBuffer(OperationWorkspace, storage, raw_workspace, workspace_bytes) catch |err|
                return code(errorStatus(err));
            if (outputOverlapsBuffer(out, scratch, @sizeOf(OperationWorkspace)) or
                inputBytesOverlapBuffer(uid_bytes, uid_length, scratch, @sizeOf(OperationWorkspace)) or
                overlaps(@intFromPtr(target_ptr), @sizeOf(Target), @intFromPtr(scratch), @sizeOf(OperationWorkspace)) or
                inputBytesOverlapBuffer(target_value.parent_uid, target_value.parent_uid_length, scratch, @sizeOf(OperationWorkspace)))
                return code(.input_error);
            out.* = std.mem.zeroes(Borrow);
            if (has_allocation_bytes > 1) return code(.input_error);
            const uid = readUid(uid_bytes, uid_length) catch |err| return code(errorStatus(err));
            const target = targetFromC(target_value) catch |err| return code(errorStatus(err));
            var work = scratch.ownerWorkspace();
            const size: ?u64 = if (has_allocation_bytes == 1) allocation_bytes else null;
            const result = storage.ledger.push(target, uid, size, &work) catch |err| return code(errorStatus(err));
            return switch (result) {
                .in_memory => |borrow| blk: {
                    out.* = nativeBorrow(borrow);
                    break :blk code(.in_memory);
                },
                .newly_allocated => |borrow| blk: {
                    out.* = nativeBorrow(borrow);
                    break :blk code(.newly_allocated);
                },
                .loading => code(.loading),
            };
        }

        pub export fn rm_ledger_push_partition(
            raw_instance: ?*anyopaque,
            raw_target: ?*const Target,
            uid_bytes: ?[*]const u8,
            uid_length: u64,
            capacity: u32,
            raw_attributes: ?*const Attributes,
            raw_workspace: ?*anyopaque,
            workspace_bytes: u64,
            out_result: ?*PartitionResult,
        ) callconv(.c) i32 {
            const out = validOutput(PartitionResult, out_result, raw_instance) catch return code(.input_error);
            const target_ptr = raw_target orelse return code(.input_error);
            const attributes_ptr = raw_attributes orelse return code(.input_error);
            if (@intFromPtr(target_ptr) % @alignOf(Target) != 0 or
                !validAddressRange(@intFromPtr(target_ptr), @sizeOf(Target)) or
                @intFromPtr(attributes_ptr) % @alignOf(Attributes) != 0 or
                !validAddressRange(@intFromPtr(attributes_ptr), @sizeOf(Attributes))) return code(.input_error);
            if (outputOverlapsRaw(out, target_ptr, @sizeOf(Target)) or
                inputOverlapsStorage(raw_instance, @intFromPtr(target_ptr), @sizeOf(Target)) or
                outputOverlapsRaw(out, attributes_ptr, @sizeOf(Attributes)) or
                inputOverlapsStorage(raw_instance, @intFromPtr(attributes_ptr), @sizeOf(Attributes)) or
                outputOverlapsBytes(out, uid_bytes, uid_length) or
                inputBytesOverlapStorage(raw_instance, uid_bytes, uid_length) or
                outputOverlapsAddress(out, raw_workspace, @sizeOf(OperationWorkspace))) return code(.input_error);
            const target_value = target_ptr.*;
            const attributes_value = attributes_ptr.*;
            if (outputOverlapsBytes(out, target_value.parent_uid, target_value.parent_uid_length) or
                inputBytesOverlapStorage(raw_instance, target_value.parent_uid, target_value.parent_uid_length))
                return code(.input_error);
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            const scratch = typedBuffer(OperationWorkspace, storage, raw_workspace, workspace_bytes) catch |err|
                return code(errorStatus(err));
            if (outputOverlapsBuffer(out, scratch, @sizeOf(OperationWorkspace)) or
                inputBytesOverlapBuffer(uid_bytes, uid_length, scratch, @sizeOf(OperationWorkspace)) or
                overlaps(@intFromPtr(target_ptr), @sizeOf(Target), @intFromPtr(scratch), @sizeOf(OperationWorkspace)) or
                overlaps(@intFromPtr(attributes_ptr), @sizeOf(Attributes), @intFromPtr(scratch), @sizeOf(OperationWorkspace)) or
                inputBytesOverlapBuffer(target_value.parent_uid, target_value.parent_uid_length, scratch, @sizeOf(OperationWorkspace)))
                return code(.input_error);
            out.* = std.mem.zeroes(PartitionResult);
            const uid = readUid(uid_bytes, uid_length) catch |err| return code(errorStatus(err));
            const target = targetFromC(target_value) catch |err| return code(errorStatus(err));
            const attributes = attributesFromC(attributes_value) catch |err| return code(errorStatus(err));
            var work = scratch.ownerWorkspace();
            const result = storage.ledger.pushPartition(target, uid, capacity, attributes, &work) catch |err|
                return code(errorStatus(err));
            out.allocation_number = result.allocation_number;
            return code(if (result.created) .partition_created else .partition_exists);
        }

        pub export fn rm_ledger_end_use(
            raw_instance: ?*anyopaque,
            raw_lease: ?*const Lease,
            disposition: u32,
        ) callconv(.c) i32 {
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            const lease_ptr = raw_lease orelse return code(.input_error);
            if (@intFromPtr(lease_ptr) % @alignOf(Lease) != 0 or
                !validAddressRange(@intFromPtr(lease_ptr), @sizeOf(Lease)) or
                inputOverlapsStorage(raw_instance, @intFromPtr(lease_ptr), @sizeOf(Lease))) return code(.input_error);
            const lease = ownerLease(lease_ptr.*) catch |err| return code(errorStatus(err));
            const end_disposition: lifecycle.EndDisposition = switch (disposition) {
                0 => .complete,
                1 => .discard,
                else => return code(.input_error),
            };
            return switch (storage.ledger.endUse(lease, end_disposition) catch |err| return code(errorStatus(err))) {
                .completed => code(.completed),
                .force_released => code(.force_released),
            };
        }

        pub export fn rm_ledger_update_uid(
            raw_instance: ?*anyopaque,
            uid_bytes: ?[*]const u8,
            uid_length: u64,
            raw_update: ?*const Update,
        ) callconv(.c) i32 {
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            const update_ptr = raw_update orelse return code(.input_error);
            if (@intFromPtr(update_ptr) % @alignOf(Update) != 0 or
                !validAddressRange(@intFromPtr(update_ptr), @sizeOf(Update)) or
                inputOverlapsStorage(raw_instance, @intFromPtr(update_ptr), @sizeOf(Update)) or
                inputBytesOverlapStorage(raw_instance, uid_bytes, uid_length)) return code(.input_error);
            if (inputBytesOverlapBuffer(uid_bytes, uid_length, update_ptr, @sizeOf(Update)))
                return code(.input_error);
            const uid = readUid(uid_bytes, uid_length) catch |err| return code(errorStatus(err));
            const update = updateFromC(update_ptr.*) catch |err| return code(errorStatus(err));
            return updateStatus(storage.ledger.updateUid(uid, update) catch |err| return code(errorStatus(err)));
        }

        pub export fn rm_ledger_update_lease(
            raw_instance: ?*anyopaque,
            raw_lease: ?*const Lease,
            raw_update: ?*const Update,
            out_borrow: ?*Borrow,
        ) callconv(.c) i32 {
            const out = validOutput(Borrow, out_borrow, raw_instance) catch return code(.input_error);
            const lease_ptr = raw_lease orelse return code(.input_error);
            const update_ptr = raw_update orelse return code(.input_error);
            if (@intFromPtr(lease_ptr) % @alignOf(Lease) != 0 or
                @intFromPtr(update_ptr) % @alignOf(Update) != 0 or
                !validAddressRange(@intFromPtr(lease_ptr), @sizeOf(Lease)) or
                !validAddressRange(@intFromPtr(update_ptr), @sizeOf(Update)) or
                inputOverlapsStorage(raw_instance, @intFromPtr(lease_ptr), @sizeOf(Lease)) or
                inputOverlapsStorage(raw_instance, @intFromPtr(update_ptr), @sizeOf(Update))) return code(.input_error);
            if (outputOverlapsRaw(out, lease_ptr, @sizeOf(Lease)) or
                outputOverlapsRaw(out, update_ptr, @sizeOf(Update)) or
                overlaps(@intFromPtr(lease_ptr), @sizeOf(Lease), @intFromPtr(update_ptr), @sizeOf(Update)))
                return code(.input_error);
            out.* = std.mem.zeroes(Borrow);
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            const input_lease = lease_ptr.*;
            const lease = ownerLease(input_lease) catch |err| return code(errorStatus(err));
            const update = updateFromC(update_ptr.*) catch |err| return code(errorStatus(err));
            const result = storage.ledger.updateLease(lease, update) catch |err| return code(errorStatus(err));
            switch (result) {
                .updated => |region_bytes| {
                    if (update.allocation_bytes != null) {
                        const bytes = region_bytes orelse return code(.invalid_state);
                        out.* = .{
                            .lease = input_lease,
                            .data = if (bytes.len == 0) null else bytes.ptr,
                            .length = @intCast(bytes.len),
                        };
                    }
                    return code(.updated);
                },
                .registration_only => return code(.registration_only),
                .in_use => return code(.in_use),
                .force_released => return code(.force_released),
            }
        }

        pub export fn rm_ledger_close(
            raw_instance: ?*anyopaque,
            uid_bytes: ?[*]const u8,
            uid_length: u64,
            raw_workspace: ?*anyopaque,
            workspace_bytes: u64,
        ) callconv(.c) i32 {
            return closeByUid(raw_instance, uid_bytes, uid_length, raw_workspace, workspace_bytes, false);
        }

        pub export fn rm_ledger_force_release(
            raw_instance: ?*anyopaque,
            uid_bytes: ?[*]const u8,
            uid_length: u64,
            raw_workspace: ?*anyopaque,
            workspace_bytes: u64,
        ) callconv(.c) i32 {
            return closeByUid(raw_instance, uid_bytes, uid_length, raw_workspace, workspace_bytes, true);
        }

        pub export fn rm_ledger_set_mode(raw_instance: ?*anyopaque, table: u32, mode: u32) callconv(.c) i32 {
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            const selected: types.Mode = switch (mode) {
                0 => .unrestricted,
                1 => .normal,
                2 => .optimize,
                else => return code(.input_error),
            };
            storage.ledger.setMode(table, selected) catch |err| return code(errorStatus(err));
            return code(.ok);
        }

        pub export fn rm_ledger_get_table_view(
            raw_instance: ?*anyopaque,
            table: u32,
            out_view: ?*TableView,
        ) callconv(.c) i32 {
            const out = validOutput(TableView, out_view, raw_instance) catch return code(.input_error);
            out.* = std.mem.zeroes(TableView);
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            const view = storage.ledger.view(table) catch |err| return code(errorStatus(err));
            out.* = .{ .registered_bytes = view.registered_bytes, .system_page_bytes = view.system_page_bytes };
            return code(.ok);
        }

        pub export fn rm_ledger_tick(
            raw_instance: ?*anyopaque,
            raw_workspace: ?*anyopaque,
            workspace_bytes: u64,
            out_result: ?*TickResult,
        ) callconv(.c) i32 {
            const out = validOutput(TickResult, out_result, raw_instance) catch return code(.input_error);
            if (outputOverlapsAddress(out, raw_workspace, @sizeOf(TickWorkspace))) return code(.input_error);
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            const scratch = typedBuffer(TickWorkspace, storage, raw_workspace, workspace_bytes) catch |err|
                return code(errorStatus(err));
            if (outputOverlapsBuffer(out, scratch, @sizeOf(TickWorkspace))) return code(.input_error);
            out.* = std.mem.zeroes(TickResult);
            var candidate_offset: usize = 0;
            for (declarations, 0..) |declaration, table| {
                const capacity: usize = @min(@as(usize, declaration.policy.tick_maximum_actions), declaration.capacity);
                const next_offset = candidate_offset + capacity;
                scratch.workspaces[table] = .{
                    .candidates = scratch.candidates[candidate_offset..next_offset],
                    .rows = &.{},
                    .shells = &.{},
                    .homes = &.{},
                    .notes = scratch.notes[candidate_offset..next_offset],
                };
                candidate_offset = next_offset;
            }
            const result = storage.ledger.tick(&scratch.workspaces) catch |err| return code(errorStatus(err));
            out.* = .{ .released = @intCast(result.released), .scanned = @intCast(result.scanned) };
            return code(.ok);
        }

        pub export fn rm_ledger_subscribe(
            raw_instance: ?*anyopaque,
            table: u32,
            callback: ?Callback,
            context: ?*anyopaque,
            out_subscription: ?*Subscription,
        ) callconv(.c) i32 {
            const out = validOutput(Subscription, out_subscription, raw_instance) catch return code(.input_error);
            if (context) |context_pointer| {
                if (outputOverlapsRaw(out, context_pointer, 1) or
                    inputOverlapsStorage(raw_instance, @intFromPtr(context_pointer), 1)) return code(.input_error);
            }
            out.* = std.mem.zeroes(Subscription);
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            if (table >= table_count) return code(.input_error);
            const cb = callback orelse return code(.input_error);
            const cell = reserveCallbackCell(storage, table) catch |err| return code(errorStatus(err));
            cell.lock.lock();
            cell.callback = cb;
            cell.context = context;
            cell.table = table;
            cell.lock.unlock();
            const subscription = storage.ledger.subscribe(table, invokeCallback, cell) catch |err| {
                cell.lock.lock();
                cell.callback = null;
                cell.context = null;
                cell.table = 0;
                cell.state = callback_free;
                cell.lock.unlock();
                return code(errorStatus(err));
            };
            const output_subscription = Subscription{
                .table = table,
                .slot = subscription.handle.slot,
                .generation = subscription.handle.generation,
            };
            cell.lock.lock();
            cell.slot = output_subscription.slot;
            cell.generation = output_subscription.generation;
            cell.state = callback_active;
            cell.lock.unlock();
            out.* = output_subscription;
            return code(.ok);
        }

        pub export fn rm_ledger_unsubscribe(
            raw_instance: ?*anyopaque,
            raw_subscription: ?*const Subscription,
        ) callconv(.c) i32 {
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            const input = raw_subscription orelse return code(.input_error);
            if (@intFromPtr(input) % @alignOf(Subscription) != 0 or
                !validAddressRange(@intFromPtr(input), @sizeOf(Subscription)) or
                inputOverlapsStorage(raw_instance, @intFromPtr(input), @sizeOf(Subscription))) return code(.input_error);
            const subscription = input.*;
            if (subscription.table >= table_count or subscription.slot >= notifications.max_subscriptions)
                return code(.nonexistent);
            const cell = findCallbackCell(storage, subscription) orelse return code(.nonexistent);
            const owner_subscription = Ledger.Subscription{
                .table = subscription.table,
                .handle = .{ .slot = subscription.slot, .generation = subscription.generation },
            };
            storage.ledger.unsubscribe(owner_subscription) catch |err| {
                cell.lock.lock();
                cell.state = callback_active;
                cell.lock.unlock();
                return code(errorStatus(err));
            };
            cell.lock.lock();
            cell.callback = null;
            cell.context = null;
            cell.table = 0;
            cell.slot = 0;
            cell.generation = 0;
            cell.state = callback_free;
            cell.lock.unlock();
            return code(.ok);
        }

        fn closeByUid(
            raw_instance: ?*anyopaque,
            uid_bytes: ?[*]const u8,
            uid_length: u64,
            raw_workspace: ?*anyopaque,
            workspace_bytes: u64,
            forced: bool,
        ) i32 {
            const storage = instance(raw_instance) catch |err| return code(errorStatus(err));
            const scratch = typedBuffer(OperationWorkspace, storage, raw_workspace, workspace_bytes) catch |err|
                return code(errorStatus(err));
            if (inputBytesOverlapStorage(raw_instance, uid_bytes, uid_length) or
                inputBytesOverlapBuffer(uid_bytes, uid_length, scratch, @sizeOf(OperationWorkspace)))
                return code(.input_error);
            const uid = readUid(uid_bytes, uid_length) catch |err| return code(errorStatus(err));
            var work = scratch.ownerWorkspace();
            if (forced) {
                storage.ledger.forceRelease(uid, &work) catch |err| return code(errorStatus(err));
            } else {
                storage.ledger.close(uid, &work) catch |err| return code(errorStatus(err));
            }
            return code(.ok);
        }

        fn reserveCallbackCell(storage: *Storage, table: u32) error{SpecificationLimit}!*CallbackCell {
            for (&storage.callback_cells[table]) |*cell| {
                cell.lock.lock();
                const available = cell.state == callback_free;
                if (available) cell.state = callback_reserved;
                cell.lock.unlock();
                if (available)
                    return cell;
            }
            return error.SpecificationLimit;
        }

        fn findCallbackCell(storage: *Storage, subscription: Subscription) ?*CallbackCell {
            for (&storage.callback_cells[subscription.table]) |*cell| {
                cell.lock.lock();
                const matches = cell.state == callback_active and cell.slot == subscription.slot and
                    cell.generation == subscription.generation;
                if (matches) cell.state = callback_retiring;
                cell.lock.unlock();
                if (matches) return cell;
            }
            return null;
        }

        fn invokeCallback(context: ?*anyopaque, uid: Uid, allocation_number: u64) callconv(.c) void {
            const cell: *CallbackCell = @ptrCast(@alignCast(context orelse unreachable));
            const callback = cell.callback orelse unreachable;
            callback(cell.context, uid.bytes[0..].ptr, @intCast(Uid.width_bits / 8), allocation_number);
        }

        fn descriptorValue() Descriptor {
            var descriptor = std.mem.zeroes(Descriptor);
            descriptor.descriptor_size = @sizeOf(Descriptor);
            descriptor.abi_version = 1;
            descriptor.uid_width_bits = @intCast(Uid.width_bits);
            descriptor.candidate_id = 0;
            descriptor.table_count = @intCast(table_count);
            descriptor.fixed_count = @intCast(fixed.len);
            descriptor.max_subscribers_per_table = @intCast(notifications.max_subscriptions);
            descriptor.resource_capacity = @intCast(Ledger.resource_capacity);
            descriptor.partition_capacity = @intCast(Ledger.partition_capacity);
            descriptor.index_node_count = @intCast(total_nodes);
            descriptor.index_home_count = config.index_buckets;
            descriptor.native_page_size = @intCast(std.heap.pageSize());
            descriptor.instance_size = @sizeOf(Storage);
            descriptor.instance_alignment = @alignOf(Storage);
            descriptor.operation_workspace_size = @sizeOf(OperationWorkspace);
            descriptor.operation_workspace_alignment = @alignOf(OperationWorkspace);
            descriptor.tick_workspace_size = @sizeOf(TickWorkspace);
            descriptor.tick_workspace_alignment = @alignOf(TickWorkspace);
            descriptor.destroy_workspace_size = @sizeOf(DestroyWorkspace);
            descriptor.destroy_workspace_alignment = @alignOf(DestroyWorkspace);
            descriptor.structure_hash = identity.structure_hash;
            descriptor.policy_hash = identity.policy_hash;
            descriptor.lease_layout = makeLayout(Lease, &.{
                @offsetOf(Lease, "slot"),
                @offsetOf(Lease, "reserved"),
                @offsetOf(Lease, "allocation_number"),
                @offsetOf(Lease, "use_generation"),
            });
            descriptor.borrow_layout = makeLayout(Borrow, &.{
                @offsetOf(Borrow, "lease"),
                @offsetOf(Borrow, "data"),
                @offsetOf(Borrow, "length"),
            });
            descriptor.attributes_layout = makeLayout(Attributes, &.{
                @offsetOf(Attributes, "recoverability"),
                @offsetOf(Attributes, "recovery_cost_coefficient"),
                @offsetOf(Attributes, "access_loss_impact"),
            });
            descriptor.update_layout = makeLayout(Update, &.{
                @offsetOf(Update, "present_mask"),
                @offsetOf(Update, "recoverability"),
                @offsetOf(Update, "recovery_cost_coefficient"),
                @offsetOf(Update, "access_loss_impact"),
                @offsetOf(Update, "allocation_bytes"),
            });
            descriptor.target_layout = makeLayout(Target, &.{
                @offsetOf(Target, "kind"),
                @offsetOf(Target, "table"),
                @offsetOf(Target, "parent_uid"),
                @offsetOf(Target, "parent_uid_length"),
            });
            descriptor.partition_result_layout = makeLayout(PartitionResult, &.{
                @offsetOf(PartitionResult, "allocation_number"),
            });
            descriptor.subscription_layout = makeLayout(Subscription, &.{
                @offsetOf(Subscription, "table"),
                @offsetOf(Subscription, "slot"),
                @offsetOf(Subscription, "generation"),
            });
            descriptor.table_view_layout = makeLayout(TableView, &.{
                @offsetOf(TableView, "registered_bytes"),
                @offsetOf(TableView, "system_page_bytes"),
            });
            descriptor.tick_result_layout = makeLayout(TickResult, &.{
                @offsetOf(TickResult, "released"),
                @offsetOf(TickResult, "scanned"),
            });
            return descriptor;
        }

        fn makeLayout(comptime Record: type, comptime offsets: []const usize) TypeLayout {
            var result = TypeLayout{
                .size = @sizeOf(Record),
                .alignment = @alignOf(Record),
                .field_count = @intCast(offsets.len),
                .reserved = 0,
                .field_offsets = @splat(0),
            };
            inline for (offsets, 0..) |offset, index| result.field_offsets[index] = @intCast(offset);
            return result;
        }

        fn instance(raw: ?*anyopaque) error{ InvalidArgument, InvalidState }!*Storage {
            const pointer = raw orelse return error.InvalidArgument;
            if (@intFromPtr(pointer) % @alignOf(Storage) != 0) return error.InvalidArgument;
            if (!validAddressRange(@intFromPtr(pointer), @sizeOf(Storage))) return error.InvalidArgument;
            const storage: *Storage = @ptrCast(@alignCast(pointer));
            if (!storage.initialized or storage.magic != active_magic) return error.InvalidState;
            return storage;
        }

        fn typedBuffer(comptime Buffer: type, storage: *Storage, raw: ?*anyopaque, bytes: u64) !*Buffer {
            const pointer = raw orelse return error.InvalidArgument;
            if (bytes < @sizeOf(Buffer) or @intFromPtr(pointer) % @alignOf(Buffer) != 0)
                return error.InvalidArgument;
            if (!validAddressRange(@intFromPtr(pointer), @sizeOf(Buffer))) return error.InvalidArgument;
            if (overlaps(@intFromPtr(pointer), @sizeOf(Buffer), @intFromPtr(storage), @sizeOf(Storage)))
                return error.InvalidArgument;
            return @ptrCast(@alignCast(pointer));
        }

        fn validOutput(comptime Value: type, raw: ?*Value, raw_instance: ?*anyopaque) !*Value {
            const pointer = raw orelse return error.InvalidArgument;
            if (@intFromPtr(pointer) % @alignOf(Value) != 0) return error.InvalidArgument;
            if (!validAddressRange(@intFromPtr(pointer), @sizeOf(Value))) return error.InvalidArgument;
            if (raw_instance) |instance_pointer| {
                if (overlaps(@intFromPtr(pointer), @sizeOf(Value), @intFromPtr(instance_pointer), @sizeOf(Storage)))
                    return error.InvalidArgument;
            }
            return pointer;
        }

        fn outputOverlapsBuffer(output: anytype, buffer: anytype, buffer_size: usize) bool {
            return overlaps(@intFromPtr(output), @sizeOf(@TypeOf(output.*)), @intFromPtr(buffer), buffer_size);
        }

        fn outputOverlapsRaw(output: anytype, raw: anytype, input_size: usize) bool {
            return overlaps(@intFromPtr(output), @sizeOf(@TypeOf(output.*)), @intFromPtr(raw), input_size);
        }

        fn outputOverlapsAddress(output: anytype, raw: ?*anyopaque, input_size: usize) bool {
            const pointer = raw orelse return false;
            return overlaps(@intFromPtr(output), @sizeOf(@TypeOf(output.*)), @intFromPtr(pointer), input_size);
        }

        fn outputOverlapsBytes(output: anytype, raw: ?[*]const u8, length: u64) bool {
            if (length > @as(u64, @intCast(std.math.maxInt(usize)))) return true;
            const pointer = raw orelse return false;
            return overlaps(
                @intFromPtr(output),
                @sizeOf(@TypeOf(output.*)),
                @intFromPtr(pointer),
                @intCast(length),
            );
        }

        fn inputOverlapsStorage(raw_instance: ?*anyopaque, address: usize, length: usize) bool {
            const pointer = raw_instance orelse return false;
            return overlaps(@intFromPtr(pointer), @sizeOf(Storage), address, length);
        }

        fn inputBytesOverlapStorage(raw_instance: ?*anyopaque, raw: ?[*]const u8, length: u64) bool {
            if (length > @as(u64, @intCast(std.math.maxInt(usize)))) return true;
            const pointer = raw orelse return false;
            return inputOverlapsStorage(raw_instance, @intFromPtr(pointer), @intCast(length));
        }

        fn inputBytesOverlapBuffer(raw: ?[*]const u8, length: u64, buffer: anytype, buffer_size: usize) bool {
            if (length > @as(u64, @intCast(std.math.maxInt(usize)))) return true;
            const pointer = raw orelse return false;
            return overlaps(@intFromPtr(pointer), @intCast(length), @intFromPtr(buffer), buffer_size);
        }

        fn validAddressRange(start: usize, length: usize) bool {
            _ = std.math.add(usize, start, length) catch return false;
            return true;
        }

        fn overlaps(left: usize, left_length: usize, right: usize, right_length: usize) bool {
            const left_end = std.math.add(usize, left, left_length) catch return true;
            const right_end = std.math.add(usize, right, right_length) catch return true;
            return left < right_end and right < left_end;
        }

        fn readUid(raw: ?[*]const u8, length: u64) error{InvalidArgument}!Uid {
            if (length != @sizeOf(Uid)) return error.InvalidArgument;
            const bytes = raw orelse return error.InvalidArgument;
            if (!validAddressRange(@intFromPtr(bytes), @sizeOf(Uid))) return error.InvalidArgument;
            var uid: Uid = undefined;
            @memcpy(&uid.bytes, bytes[0..@sizeOf(Uid)]);
            return uid;
        }

        fn targetFromC(value: Target) !Ledger.api.Target {
            return switch (value.kind) {
                0 => blk: {
                    if (value.parent_uid != null or value.parent_uid_length != 0) return error.InvalidArgument;
                    break :blk .{ .table = value.table };
                },
                1 => blk: {
                    if (value.table != 0) return error.InvalidArgument;
                    break :blk .{ .partition = try readUid(value.parent_uid, value.parent_uid_length) };
                },
                else => error.InvalidArgument,
            };
        }

        fn attributesFromC(value: Attributes) error{InvalidArgument}!types.Attributes {
            return .{
                .recoverability = switch (value.recoverability) {
                    0 => .not_recoverable,
                    1 => .recoverable,
                    else => return error.InvalidArgument,
                },
                .recovery_cost_coefficient = value.recovery_cost_coefficient,
                .access_loss_impact = switch (value.access_loss_impact) {
                    0 => .fatal,
                    1 => .observable_now,
                    2 => .unobservable_now,
                    else => return error.InvalidArgument,
                },
            };
        }

        fn updateFromC(value: Update) error{InvalidArgument}!Ledger.api.Update {
            const allowed_mask: u32 = 0x0f;
            if ((value.present_mask & ~allowed_mask) != 0) return error.InvalidArgument;
            var result: Ledger.api.Update = .{};
            if ((value.present_mask & 0x01) != 0) result.allocation_bytes = value.allocation_bytes;
            if ((value.present_mask & 0x0e) != 0) {
                var attributes_change: types.AttributeUpdate = .{};
                if ((value.present_mask & 0x02) != 0) {
                    attributes_change.recoverability = switch (value.recoverability) {
                        0 => .not_recoverable,
                        1 => .recoverable,
                        else => return error.InvalidArgument,
                    };
                }
                if ((value.present_mask & 0x04) != 0)
                    attributes_change.recovery_cost_coefficient = value.recovery_cost_coefficient;
                if ((value.present_mask & 0x08) != 0) {
                    attributes_change.access_loss_impact = switch (value.access_loss_impact) {
                        0 => .fatal,
                        1 => .observable_now,
                        2 => .unobservable_now,
                        else => return error.InvalidArgument,
                    };
                }
                result.attributes = attributes_change;
            }
            return result;
        }

        fn ownerLease(value: Lease) error{InvalidArgument}!Ledger.api.Lease {
            if (value.reserved != 0) return error.InvalidArgument;
            return .{ .slot = value.slot, .allocation_number = value.allocation_number, .use_generation = value.use_generation };
        }

        fn nativeBorrow(value: Ledger.api.Borrow) Borrow {
            return .{
                .lease = .{
                    .slot = value.lease.slot,
                    .reserved = 0,
                    .allocation_number = value.lease.allocation_number,
                    .use_generation = value.lease.use_generation,
                },
                .data = if (value.region.len == 0) null else value.region.ptr,
                .length = @intCast(value.region.len),
            };
        }

        fn updateStatus(result: Ledger.api.UpdateResult) i32 {
            return switch (result) {
                .updated => code(.updated),
                .registration_only => code(.registration_only),
                .in_use => code(.in_use),
                .force_released => code(.force_released),
            };
        }

        fn errorStatus(err: anyerror) Status {
            if (err == error.InvalidArgument or err == error.InvalidLease or err == error.NotRegistered or
                err == error.SizeOverflow or err == error.AllocationTooSmall)
                return .input_error;
            if (err == error.AllocationFailed) return .allocation_failed;
            if (err == error.InUse) return .in_use;
            if (err == error.Blocked) return .blocked;
            if (err == error.Nonexistent or err == error.NonexistentHandle) return .nonexistent;
            if (err == error.SpecificationLimit or err == error.GenerationExhausted) return .specification_limit;
            if (err == error.ChangedBeyondTolerance) return .changed_beyond_tolerance;
            if (err == error.UnsupportedPlatform) return .unsupported_platform;
            if (err == error.InvalidState or err == error.WorkspaceTooSmall or err == error.InvalidIndexState)
                return .invalid_state;
            return .internal_error;
        }

        fn code(status: Status) i32 {
            return @intFromEnum(status);
        }
    };
}
