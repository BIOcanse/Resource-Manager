//! Provisional opaque Native binding used only by the C2/B2 language probe.
//! This is not the production ABI and does not select the final owner layout.
const std = @import("std");
const options = @import("comparison_options");
const values = @import("ledger_values").Bound;
const structure = @import("ledger_structure");
const uid_module = @import("../uid.zig");
const c2_index = @import("index.zig");
const b2_index = @import("index_b2.zig");
const notifications = @import("notifications.zig");
const owner_module = @import("owner.zig");
const protocol = @import("../protocol.zig");
const lifecycle = @import("../memory_resource.zig");
const types = @import("types.zig");

pub const ContractDescriptor = structure.ContractDescriptor;
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
    internal_error = 255,
};

const CandidateFlavor = enum { c2, b2 };
const candidate: CandidateFlavor = switch (options.candidate) {
    .c2 => .c2,
    .b2 => .b2,
};
const Uid = uid_module.Identifier(options.uid_width_bits);
const Index = if (candidate == .c2)
    c2_index.Candidate(Uid, .c2_chain)
else
    b2_index.Candidate(Uid, .b2_ordered);
const IndexScratch = if (candidate == .c2) struct {
    nodes: [64]u32 = undefined,
    publications: [64]Index.Publication = undefined,
    removals: [64]Index.RemovePlan = undefined,
    inserts: [64]Index.InsertPlan = undefined,
} else struct {
    indices: [64]u32 = undefined,
    operations: [128]Index.Transactions.Operation = undefined,
    segments: [256]Index.Transactions.Segment = undefined,
    versions: [64]u64 = undefined,
    removals: [64]Index.RemovePlan = undefined,
    inserts: [64]Index.InsertPlan = undefined,
    simulation_nodes: [64]u32 = undefined,
    simulation_uids: [64]Uid = undefined,
    simulation_backlinks: [128]u32 = undefined,
    simulation_node_ids: [128]u32 = undefined,
};

const attrs = types.Attributes{
    .recoverability = .recoverable,
    .recovery_cost_coefficient = 1,
    .access_loss_impact = .unobservable_now,
};

comptime {
    if (values.uid_width_bits != options.uid_width_bits)
        @compileError("comparison UID width does not match the selected SDK38 ledger_values");
    if (values.activity_bitmap_width != 8 and values.activity_bitmap_width != 16 and
        values.activity_bitmap_width != 32 and values.activity_bitmap_width != 64)
        @compileError("selected SDK38 activity bitmap width is not supported by the owner");
}

fn policy(comptime table: usize) types.Policy {
    const prefix = std.fmt.comptimePrint("table_{d}_", .{table});
    return .{
        .score_epochs = @intCast(@field(values, prefix ++ "activity_score_epochs")),
        .idle_epochs = @intCast(@field(values, prefix ++ "idle_epochs")),
        .protection = switch (@field(values, prefix ++ "capacity_protection_method")) {
            1 => .concentrated,
            2 => .smooth,
            else => @compileError("selected SDK38 capacity protection method is unsupported"),
        },
        .concentrated_trigger_percent = @intCast(@field(values, prefix ++ "concentrated_trigger_percent")),
        .concentrated_target_percent = @intCast(@field(values, prefix ++ "concentrated_target_percent")),
        .smooth_trigger_percent = @intCast(@field(values, prefix ++ "smooth_trigger_percent")),
        .smooth_emergency_percent = @intCast(@field(values, prefix ++ "smooth_emergency_percent")),
        .smooth_interval_epochs = @intCast(@field(values, prefix ++ "smooth_interval_epochs")),
        .smooth_release_count = @intCast(@field(values, prefix ++ "smooth_release_count")),
        .tick_maximum_actions = @intCast(@field(values, prefix ++ "tick_maximum_actions")),
        .release_check = .{
            .enabled = @field(values, prefix ++ "release_check_enabled") == 2,
            .tolerance_parts_per_million = @intCast(@field(values, prefix ++ "release_tolerance_parts_per_million")),
            .tolerance_bytes = @field(values, prefix ++ "release_tolerance_bytes"),
        },
    };
}

fn key(value: u64) Uid {
    var result = Uid{ .bytes = @splat(0) };
    std.mem.writeInt(u64, result.bytes[result.bytes.len - 8 ..][0..8], value, .big);
    return result;
}

const table_declarations = [_]types.Table{
    .{
        .capacity = 8,
        .partition_capacity = 4,
        .reserved_start = 0,
        .reserved_count = 4,
        .attributes = attrs,
        .policy = policy(0),
    },
    .{
        .capacity = 8,
        .partition_capacity = 4,
        .reserved_start = 0,
        .reserved_count = 0,
        .attributes = attrs,
        .policy = policy(1),
    },
};

const fixed_declarations = [_]types.For(Uid).Fixed{
    .{
        .table = 0,
        .parent = null,
        .ordinal = 6,
        .uid = key(1),
        .allocation_bytes = 31,
        .attributes = null,
    },
    .{
        .table = 0,
        .parent = key(11),
        .ordinal = 2,
        .uid = key(12),
        .allocation_bytes = 19,
        .attributes = null,
    },
};

const Ledger = owner_module.Owner(
    .{
        .uid_width = options.uid_width_bits,
        .activity_width = @intCast(values.activity_bitmap_width),
        .index_buckets = 64,
    },
    &table_declarations,
    &fixed_declarations,
    Index,
);
const Notifier = notifications.Notifier(Uid);

pub const Callback = *const fn (
    context: ?*anyopaque,
    uid_bytes: [*]const u8,
    uid_length: usize,
    allocation_number: u64,
) callconv(.c) void;

const CallbackCell = struct {
    used: bool = false,
    callback: ?Callback = null,
    context: ?*anyopaque = null,
    table: u32 = 0,
    slot: u32 = 0,
    generation: u64 = 0,
};

const active_magic: u64 = 0x4f574e4544505242; // "OWNEDPRB"

const Storage = struct {
    magic: u64 = 0,
    initialized: bool = false,

    rows: [16]Ledger.Row = undefined,
    shells: [8]Ledger.Shell = undefined,
    tables: [2]Ledger.TableState = undefined,
    bindings: [2]Ledger.FixedBinding = undefined,
    nodes: [24]Index.Node = undefined,
    homes: [64]Index.Home = undefined,
    records: [2][64]Notifier.Record = undefined,
    callback_cells: [2][64]CallbackCell = undefined,

    candidates: [2][64]Ledger.Candidate = undefined,
    row_work: [2][64]Ledger.Candidate = undefined,
    shell_work: [2][8]u32 = undefined,
    owner_homes: [2][64]u32 = undefined,
    notes: [2][64]Ledger.api.Notification = undefined,
    remove_ids: [2][64]u32 = undefined,
    puts: [2][64]Index.Transactions.Put = undefined,
    index_scratch: [2]IndexScratch = .{ .{}, .{} },
    cells: [2][8]Ledger.PartitionPlanning.Cell = undefined,
    children: [2][8]Ledger.PartitionPlanning.Child = undefined,
    child_slots: [2][8]u32 = undefined,
    numbers: [2][64]u64 = undefined,
    merge_a: [2][64]u64 = undefined,
    merge_b: [2][64]u64 = undefined,
    best_numbers: [2][64]u64 = undefined,

    destroy_remove_ids: [24]u32 = undefined,
    destroy_notices: [24]Ledger.Destruction.Notice = undefined,
    destroy_views: [2]types.TableView = undefined,

    ledger: Ledger = undefined,
    work: [2]Ledger.Workspace = undefined,

    fn transactionWorkspace(self: *@This(), table: usize) Index.Transactions.Workspace {
        if (candidate == .c2) {
            return .{
                .homes = &self.owner_homes[table],
                .nodes = &self.index_scratch[table].nodes,
                .publications = &self.index_scratch[table].publications,
                .removals = &self.index_scratch[table].removals,
                .inserts = &self.index_scratch[table].inserts,
            };
        }
        return .{
            .indices = &self.index_scratch[table].indices,
            .operations = &self.index_scratch[table].operations,
            .segments = &self.index_scratch[table].segments,
            .versions = &self.index_scratch[table].versions,
            .removals = &self.index_scratch[table].removals,
            .inserts = &self.index_scratch[table].inserts,
            .simulation_nodes = &self.index_scratch[table].simulation_nodes,
            .simulation_uids = &self.index_scratch[table].simulation_uids,
            .simulation_backlinks = &self.index_scratch[table].simulation_backlinks,
            .simulation_node_ids = &self.index_scratch[table].simulation_node_ids,
        };
    }

    fn initialize(self: *@This()) !void {
        for (&self.callback_cells) |*table_cells| {
            for (table_cells) |*cell| cell.* = .{};
        }
        var subscribers = [_][]Notifier.Record{ &self.records[0], &self.records[1] };
        self.ledger = try Ledger.init(
            &self.rows,
            &self.shells,
            &self.tables,
            &self.bindings,
            &self.nodes,
            &self.homes,
            &subscribers,
        );
        for (&self.work, 0..) |*work, table| {
            work.* = .{
                .candidates = &self.candidates[table],
                .rows = &self.row_work[table],
                .shells = &self.shell_work[table],
                .homes = &self.owner_homes[table],
                .notes = &self.notes[table],
                .remove_ids = &self.remove_ids[table],
                .puts = &self.puts[table],
                .index_workspace = self.transactionWorkspace(table),
                .cells = &self.cells[table],
                .children = &self.children[table],
                .child_slots = &self.child_slots[table],
                .numbers = &self.numbers[table],
                .merge_a = &self.merge_a[table],
                .merge_b = &self.merge_b[table],
                .best_numbers = &self.best_numbers[table],
            };
        }
        self.initialized = true;
    }
};

fn code(status: Status) i32 {
    return @intFromEnum(status);
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

fn storageFromRaw(raw: ?*anyopaque) ?*Storage {
    const pointer = raw orelse return null;
    if (@intFromPtr(pointer) % @alignOf(Storage) != 0) return null;
    const storage: *Storage = @ptrCast(@alignCast(pointer));
    return storage;
}

fn instance(raw: ?*anyopaque) ?*Storage {
    const storage = storageFromRaw(raw) orelse return null;
    if (storage.magic != active_magic or !storage.initialized) return null;
    return storage;
}

fn uidFromBytes(bytes: ?[*]const u8, length: usize) ?Uid {
    if (length != Uid.width_bits / 8) return null;
    const source = bytes orelse return null;
    var uid: Uid = undefined;
    @memcpy(uid.bytes[0..], source[0..length]);
    return uid;
}

fn targetFromInputs(
    target_kind: u8,
    target_table: u32,
    parent_bytes: ?[*]const u8,
    parent_length: usize,
) ?Ledger.api.Target {
    return switch (target_kind) {
        0 => if (target_table < 2 and parent_length == 0 and parent_bytes == null)
            .{ .table = target_table }
        else
            null,
        1 => blk: {
            const parent_uid = uidFromBytes(parent_bytes, parent_length) orelse break :blk null;
            break :blk .{ .partition = parent_uid };
        },
        else => null,
    };
}

fn initializeBorrowOutputs(
    out_slot: ?*u32,
    out_allocation_number: ?*u64,
    out_use_generation: ?*u64,
    out_region: ?*?[*]u8,
    out_region_length: ?*usize,
) bool {
    const slot = out_slot orelse return false;
    const allocation_number = out_allocation_number orelse return false;
    const use_generation = out_use_generation orelse return false;
    const region = out_region orelse return false;
    const region_length = out_region_length orelse return false;
    slot.* = 0;
    allocation_number.* = 0;
    use_generation.* = 0;
    region.* = null;
    region_length.* = 0;
    return true;
}

fn writeBorrow(
    borrow: Ledger.api.Borrow,
    out_slot: *u32,
    out_allocation_number: *u64,
    out_use_generation: *u64,
    out_region: *?[*]u8,
    out_region_length: *usize,
) void {
    out_slot.* = borrow.lease.slot;
    out_allocation_number.* = borrow.lease.allocation_number;
    out_use_generation.* = borrow.lease.use_generation;
    out_region.* = borrow.region.ptr;
    out_region_length.* = borrow.region.len;
}

fn attributesFromInputs(
    recoverability_raw: u8,
    recovery_cost_coefficient: u32,
    access_loss_impact_raw: u8,
) ?types.Attributes {
    const recoverability: protocol.Recoverability = switch (recoverability_raw) {
        0 => .not_recoverable,
        1 => .recoverable,
        else => return null,
    };
    const impact: protocol.AccessLossImpact = switch (access_loss_impact_raw) {
        0 => .fatal,
        1 => .observable_now,
        2 => .unobservable_now,
        else => return null,
    };
    return .{
        .recoverability = recoverability,
        .recovery_cost_coefficient = recovery_cost_coefficient,
        .access_loss_impact = impact,
    };
}

fn attributeUpdateFromInputs(
    mask: u8,
    recoverability_raw: u8,
    recovery_cost_coefficient: u32,
    access_loss_impact_raw: u8,
) ?types.AttributeUpdate {
    if (mask & ~@as(u8, 7) != 0) return null;
    var result = types.AttributeUpdate{};
    if (mask & 1 != 0) result.recoverability = switch (recoverability_raw) {
        0 => .not_recoverable,
        1 => .recoverable,
        else => return null,
    };
    if (mask & 2 != 0) result.recovery_cost_coefficient = recovery_cost_coefficient;
    if (mask & 4 != 0) result.access_loss_impact = switch (access_loss_impact_raw) {
        0 => .fatal,
        1 => .observable_now,
        2 => .unobservable_now,
        else => return null,
    };
    return result;
}

fn updateFromInputs(
    has_allocation_bytes: u8,
    allocation_bytes: u64,
    attribute_mask: u8,
    recoverability_raw: u8,
    recovery_cost_coefficient: u32,
    access_loss_impact_raw: u8,
) ?Ledger.api.Update {
    if (has_allocation_bytes > 1) return null;
    const attribute_update = attributeUpdateFromInputs(
        attribute_mask,
        recoverability_raw,
        recovery_cost_coefficient,
        access_loss_impact_raw,
    ) orelse return null;
    return .{
        .allocation_bytes = if (has_allocation_bytes == 1) allocation_bytes else null,
        .attributes = if (attribute_mask == 0) null else attribute_update,
    };
}

fn notifierBridge(context: ?*anyopaque, uid: Uid, allocation_number: u64) callconv(.c) void {
    const raw_cell = context orelse return;
    const cell: *CallbackCell = @ptrCast(@alignCast(raw_cell));
    const callback = cell.callback orelse return;
    callback(cell.context, uid.bytes[0..].ptr, uid.bytes.len, allocation_number);
}

pub fn storageSize() usize {
    return @sizeOf(Storage);
}

pub fn storageAlignment() usize {
    return @alignOf(Storage);
}

pub fn uidWidthBits() u32 {
    return options.uid_width_bits;
}

pub fn candidateId() u32 {
    return if (candidate == .c2) 0 else 1;
}

pub fn create(raw_storage: ?*anyopaque, storage_bytes: usize, out_instance: ?*?*anyopaque) i32 {
    const output = out_instance orelse return code(.input_error);
    output.* = null;
    const storage = storageFromRaw(raw_storage) orelse return code(.input_error);
    if (storage_bytes < @sizeOf(Storage)) return code(.input_error);
    if (storage.magic != 0) return code(.invalid_state);
    storage.* = .{};
    storage.initialize() catch |err| {
        storage.* = .{};
        return code(errorStatus(err));
    };
    storage.magic = active_magic;
    output.* = raw_storage.?;
    return code(.ok);
}

pub fn destroy(raw_instance: ?*anyopaque, forced: u8) i32 {
    if (forced > 1) return code(.input_error);
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    var work = Ledger.Destruction.Workspace{
        .remove_ids = &storage.destroy_remove_ids,
        .notices = &storage.destroy_notices,
        .table_views = &storage.destroy_views,
        .index_workspace = storage.transactionWorkspace(0),
    };
    storage.ledger.destroy(forced == 1, &work) catch |err| return code(errorStatus(err));
    for (&storage.callback_cells) |*table_cells| {
        for (table_cells) |*cell| cell.* = .{};
    }
    storage.initialized = false;
    storage.magic = 0;
    return code(.ok);
}

pub fn push(
    raw_instance: ?*anyopaque,
    target_kind: u8,
    target_table: u32,
    target_parent_uid: ?[*]const u8,
    target_parent_uid_length: usize,
    uid_bytes: ?[*]const u8,
    uid_length: usize,
    has_allocation_bytes: u8,
    allocation_bytes: u64,
    out_slot: ?*u32,
    out_allocation_number: ?*u64,
    out_use_generation: ?*u64,
    out_region: ?*?[*]u8,
    out_region_length: ?*usize,
) i32 {
    if (!initializeBorrowOutputs(out_slot, out_allocation_number, out_use_generation, out_region, out_region_length))
        return code(.input_error);
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    const target = targetFromInputs(target_kind, target_table, target_parent_uid, target_parent_uid_length) orelse
        return code(.input_error);
    const uid = uidFromBytes(uid_bytes, uid_length) orelse return code(.input_error);
    if (has_allocation_bytes > 1) return code(.input_error);
    const outputs = .{
        .slot = out_slot.?,
        .allocation_number = out_allocation_number.?,
        .use_generation = out_use_generation.?,
        .region = out_region.?,
        .region_length = out_region_length.?,
    };
    const result = storage.ledger.push(
        target,
        uid,
        if (has_allocation_bytes == 1) allocation_bytes else null,
        &storage.work[0],
    ) catch |err| return code(errorStatus(err));
    return switch (result) {
        .in_memory => |borrow| blk: {
            writeBorrow(borrow, outputs.slot, outputs.allocation_number, outputs.use_generation, outputs.region, outputs.region_length);
            break :blk code(.in_memory);
        },
        .newly_allocated => |borrow| blk: {
            writeBorrow(borrow, outputs.slot, outputs.allocation_number, outputs.use_generation, outputs.region, outputs.region_length);
            break :blk code(.newly_allocated);
        },
        .loading => code(.loading),
    };
}

pub fn use(
    raw_instance: ?*anyopaque,
    uid_bytes: ?[*]const u8,
    uid_length: usize,
    out_slot: ?*u32,
    out_allocation_number: ?*u64,
    out_use_generation: ?*u64,
    out_region: ?*?[*]u8,
    out_region_length: ?*usize,
) i32 {
    if (!initializeBorrowOutputs(out_slot, out_allocation_number, out_use_generation, out_region, out_region_length))
        return code(.input_error);
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    const uid = uidFromBytes(uid_bytes, uid_length) orelse return code(.input_error);
    const outputs = .{
        .slot = out_slot.?,
        .allocation_number = out_allocation_number.?,
        .use_generation = out_use_generation.?,
        .region = out_region.?,
        .region_length = out_region_length.?,
    };
    const result = storage.ledger.use(uid) catch |err| return code(errorStatus(err));
    return switch (result) {
        .in_memory => |borrow| blk: {
            writeBorrow(borrow, outputs.slot, outputs.allocation_number, outputs.use_generation, outputs.region, outputs.region_length);
            break :blk code(.in_memory);
        },
        .not_in_memory => code(.not_in_memory),
        .loading => code(.loading),
    };
}

pub fn useFixed(
    raw_instance: ?*anyopaque,
    declaration: u32,
    out_slot: ?*u32,
    out_allocation_number: ?*u64,
    out_use_generation: ?*u64,
    out_region: ?*?[*]u8,
    out_region_length: ?*usize,
) i32 {
    if (!initializeBorrowOutputs(out_slot, out_allocation_number, out_use_generation, out_region, out_region_length))
        return code(.input_error);
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    const result = switch (declaration) {
        0 => storage.ledger.useFixed(0) catch |err| return code(errorStatus(err)),
        1 => storage.ledger.useFixed(1) catch |err| return code(errorStatus(err)),
        else => return code(.input_error),
    };
    const outputs = .{
        .slot = out_slot.?,
        .allocation_number = out_allocation_number.?,
        .use_generation = out_use_generation.?,
        .region = out_region.?,
        .region_length = out_region_length.?,
    };
    return switch (result) {
        .in_memory => |borrow| blk: {
            writeBorrow(borrow, outputs.slot, outputs.allocation_number, outputs.use_generation, outputs.region, outputs.region_length);
            break :blk code(.in_memory);
        },
        .not_in_memory => code(.not_in_memory),
        .loading => code(.loading),
    };
}

pub fn pushPartition(
    raw_instance: ?*anyopaque,
    target_kind: u8,
    target_table: u32,
    target_parent_uid: ?[*]const u8,
    target_parent_uid_length: usize,
    uid_bytes: ?[*]const u8,
    uid_length: usize,
    capacity: u32,
    recoverability_raw: u8,
    recovery_cost_coefficient: u32,
    access_loss_impact_raw: u8,
    out_created: ?*u8,
    out_allocation_number: ?*u64,
) i32 {
    const created = out_created orelse return code(.input_error);
    const allocation_number = out_allocation_number orelse return code(.input_error);
    created.* = 0;
    allocation_number.* = 0;
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    const target = targetFromInputs(target_kind, target_table, target_parent_uid, target_parent_uid_length) orelse
        return code(.input_error);
    const uid = uidFromBytes(uid_bytes, uid_length) orelse return code(.input_error);
    const attrs_value = attributesFromInputs(recoverability_raw, recovery_cost_coefficient, access_loss_impact_raw) orelse
        return code(.input_error);
    const result = storage.ledger.pushPartition(target, uid, capacity, attrs_value, &storage.work[0]) catch |err|
        return code(errorStatus(err));
    created.* = @intFromBool(result.created);
    allocation_number.* = result.allocation_number;
    return code(.ok);
}

pub fn endUse(
    raw_instance: ?*anyopaque,
    slot: u32,
    allocation_number: u64,
    use_generation: u64,
    disposition: u8,
) i32 {
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    const end_disposition: lifecycle.EndDisposition = switch (disposition) {
        0 => .complete,
        1 => .discard,
        else => return code(.input_error),
    };
    const result = storage.ledger.endUse(.{
        .slot = slot,
        .allocation_number = allocation_number,
        .use_generation = use_generation,
    }, end_disposition) catch |err| return code(errorStatus(err));
    return switch (result) {
        .completed => code(.completed),
        .force_released => code(.force_released),
    };
}

pub fn updateUid(
    raw_instance: ?*anyopaque,
    uid_bytes: ?[*]const u8,
    uid_length: usize,
    has_allocation_bytes: u8,
    allocation_bytes: u64,
    attribute_mask: u8,
    recoverability_raw: u8,
    recovery_cost_coefficient: u32,
    access_loss_impact_raw: u8,
) i32 {
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    const uid = uidFromBytes(uid_bytes, uid_length) orelse return code(.input_error);
    const change = updateFromInputs(
        has_allocation_bytes,
        allocation_bytes,
        attribute_mask,
        recoverability_raw,
        recovery_cost_coefficient,
        access_loss_impact_raw,
    ) orelse return code(.input_error);
    const result = storage.ledger.updateUid(uid, change) catch |err| return code(errorStatus(err));
    return switch (result) {
        .updated => code(.updated),
        .registration_only => code(.registration_only),
        .in_use => code(.in_use),
        .force_released => code(.force_released),
    };
}

pub fn updateLease(
    raw_instance: ?*anyopaque,
    slot: u32,
    allocation_number: u64,
    use_generation: u64,
    has_allocation_bytes: u8,
    allocation_bytes: u64,
    attribute_mask: u8,
    recoverability_raw: u8,
    recovery_cost_coefficient: u32,
    access_loss_impact_raw: u8,
    out_region: ?*?[*]u8,
    out_region_length: ?*usize,
) i32 {
    const region = out_region orelse return code(.input_error);
    const region_length = out_region_length orelse return code(.input_error);
    region.* = null;
    region_length.* = 0;
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    const change = updateFromInputs(
        has_allocation_bytes,
        allocation_bytes,
        attribute_mask,
        recoverability_raw,
        recovery_cost_coefficient,
        access_loss_impact_raw,
    ) orelse return code(.input_error);
    const result = storage.ledger.updateLease(.{
        .slot = slot,
        .allocation_number = allocation_number,
        .use_generation = use_generation,
    }, change) catch |err| return code(errorStatus(err));
    return switch (result) {
        .updated => |maybe_bytes| blk: {
            if (maybe_bytes) |bytes| {
                region.* = bytes.ptr;
                region_length.* = bytes.len;
            }
            break :blk code(.updated);
        },
        .registration_only => code(.registration_only),
        .in_use => code(.in_use),
        .force_released => code(.force_released),
    };
}

pub fn close(raw_instance: ?*anyopaque, uid_bytes: ?[*]const u8, uid_length: usize) i32 {
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    const uid = uidFromBytes(uid_bytes, uid_length) orelse return code(.input_error);
    storage.ledger.close(uid, &storage.work[0]) catch |err| return code(errorStatus(err));
    return code(.ok);
}

pub fn forceRelease(raw_instance: ?*anyopaque, uid_bytes: ?[*]const u8, uid_length: usize) i32 {
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    const uid = uidFromBytes(uid_bytes, uid_length) orelse return code(.input_error);
    storage.ledger.forceRelease(uid, &storage.work[0]) catch |err| return code(errorStatus(err));
    return code(.ok);
}

pub fn setMode(raw_instance: ?*anyopaque, table: u32, mode_raw: u8) i32 {
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    const mode: types.Mode = switch (mode_raw) {
        0 => .unrestricted,
        1 => .normal,
        2 => .optimize,
        else => return code(.input_error),
    };
    storage.ledger.setMode(table, mode) catch |err| return code(errorStatus(err));
    return code(.ok);
}

pub fn tick(raw_instance: ?*anyopaque, out_released: ?*usize, out_scanned: ?*usize) i32 {
    const released = out_released orelse return code(.input_error);
    const scanned = out_scanned orelse return code(.input_error);
    released.* = 0;
    scanned.* = 0;
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    const result = storage.ledger.tick(&storage.work) catch |err| return code(errorStatus(err));
    released.* = result.released;
    scanned.* = result.scanned;
    return code(.ok);
}

pub fn view(
    raw_instance: ?*anyopaque,
    table: u32,
    out_registered_bytes: ?*u64,
    out_system_page_bytes: ?*u64,
) i32 {
    const registered = out_registered_bytes orelse return code(.input_error);
    const system_pages = out_system_page_bytes orelse return code(.input_error);
    registered.* = 0;
    system_pages.* = 0;
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    const result = storage.ledger.view(table) catch |err| return code(errorStatus(err));
    registered.* = result.registered_bytes;
    system_pages.* = result.system_page_bytes;
    return code(.ok);
}

pub fn subscribe(
    raw_instance: ?*anyopaque,
    table: u32,
    callback: ?Callback,
    context: ?*anyopaque,
    out_table: ?*u32,
    out_slot: ?*u32,
    out_generation: ?*u64,
) i32 {
    const table_output = out_table orelse return code(.input_error);
    const slot_output = out_slot orelse return code(.input_error);
    const generation_output = out_generation orelse return code(.input_error);
    table_output.* = 0;
    slot_output.* = 0;
    generation_output.* = 0;
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    if (table >= 2) return code(.input_error);
    const actual_callback = callback orelse return code(.input_error);
    var cell: ?*CallbackCell = null;
    for (&storage.callback_cells[table]) |*candidate_cell| {
        if (!candidate_cell.used) {
            cell = candidate_cell;
            break;
        }
    }
    const selected_cell = cell orelse return code(.specification_limit);
    selected_cell.* = .{ .callback = actual_callback, .context = context };
    const result = storage.ledger.subscribe(table, notifierBridge, selected_cell) catch |err| {
        selected_cell.* = .{};
        return code(errorStatus(err));
    };
    selected_cell.used = true;
    selected_cell.table = result.table;
    selected_cell.slot = result.handle.slot;
    selected_cell.generation = result.handle.generation;
    table_output.* = result.table;
    slot_output.* = result.handle.slot;
    generation_output.* = result.handle.generation;
    return code(.ok);
}

pub fn unsubscribe(
    raw_instance: ?*anyopaque,
    table: u32,
    slot: u32,
    generation: u64,
) i32 {
    const storage = instance(raw_instance) orelse return code(.invalid_state);
    if (table >= 2) return code(.nonexistent);
    var cell: ?*CallbackCell = null;
    for (&storage.callback_cells[table]) |*candidate_cell| {
        if (candidate_cell.used and candidate_cell.slot == slot and candidate_cell.generation == generation) {
            cell = candidate_cell;
            break;
        }
    }
    const selected_cell = cell orelse return code(.nonexistent);
    storage.ledger.unsubscribe(.{
        .table = table,
        .handle = .{ .slot = slot, .generation = generation },
    }) catch |err| return code(errorStatus(err));
    selected_cell.* = .{};
    return code(.ok);
}
