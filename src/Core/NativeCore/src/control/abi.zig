const std = @import("std");
const control = @import("rm_control");
const backend_tree = @import("backend_tree");
const ResultCode = @import("../common/result_codes.zig").ResultCode;

pub const abi_version: u32 = 0x0001_0000;
pub const no_uid: u32 = std.math.maxInt(u32);

pub const Profile = enum(u32) {
    full = 1,
    windows_service = 2,
    normal_read_only = 3,
};

/// Fixed-width host configuration. The selected profile has one fixed tree;
/// `host_blocks` must exactly match the host's runnable block registrations.
pub const Config = extern struct {
    struct_size: u32,
    profile: u32,
    worker_count: u32,
    history_capacity: u32,
    demand_capacity: u32,
    demand_bytes: u32,
    completion_capacity: u32,
    reader_capacity: u32,
    reader_bytes: u32,
    worker_arena_bytes: u32,
    reason_text_capacity: u32,
    demand_item_uid: u32,
    shutdown_item_uid: u32,
    host_blocks: ?[*]const u32,
    host_block_count: u32,
};

/// C callback table from 5b. Log and reason lengths use native `usize` to
/// match `rm_control.types.Host` and `rm_control.types.ReasonView`.
pub const HostCallbacks = extern struct {
    context: ?*anyopaque,
    start: ?*const fn (?*anyopaque, u32, u64) callconv(.c) void,
    stop: ?*const fn (?*anyopaque, u32, u64) callconv(.c) void,
    complete: ?*const fn (?*anyopaque, u64) callconv(.c) void,
    log: ?*const fn (?*anyopaque, u32, [*]const u8, usize) callconv(.c) void,
    item_changed: ?*const fn (?*anyopaque, u64, control.types.ItemKeyView, *const control.types.ReasonView) callconv(.c) void,
};

pub const ItemKeyView = control.types.ItemKeyView;
pub const ReasonView = control.types.ReasonView;

/// Values cross the ABI as `u32`; invalid values are rejected before they
/// reach the controller's tagged unions.
pub const ReadView = enum(u32) {
    not_published = 0,
    owner_stopped = 1,
    read_failed = 2,
    false_value = 3,
    true_value = 4,
};

pub const PresenceView = enum(u32) {
    present = 0,
    not_published = 1,
    owner_stopped = 2,
    read_failed = 3,
};

pub const BlockStateView = enum(u32) {
    stopped = 0,
    starting = 1,
    running = 2,
    stopping = 3,
};

const Allocator = std.heap.page_allocator;
const Controller = union(enum) {
    full: *backend_tree.Engine,
    windows_service: *backend_tree.ServiceEngine,
    normal_readonly: *backend_tree.NormalReadOnlyEngine,
};
const Session = struct { controller: Controller };

comptime {
    if (@sizeOf(Config) != 72) @compileError("rm_control Config ABI size changed");
    if (@offsetOf(Config, "host_blocks") != 56) @compileError("rm_control Config ABI layout changed");
    if (@offsetOf(Config, "host_block_count") != 64) @compileError("rm_control Config ABI layout changed");
    if (@sizeOf(HostCallbacks) != 48) @compileError("rm_control HostCallbacks ABI size changed");
}

pub export fn rm_control_abi_version() callconv(.c) u32 {
    return abi_version;
}

/// Copies the profile's exact non-root host UIDs. Capacity and needed are UID
/// counts, not byte lengths. A short buffer is left untouched.
pub export fn rm_control_profile_host_blocks(
    profile: u32,
    buffer: ?[*]u32,
    capacity: usize,
    needed_pointer: ?*usize,
) callconv(.c) i32 {
    const needed = needed_pointer orelse return code(.invalid_argument);
    needed.* = 0;
    const blocks: []const u32 = switch (profile) {
        @intFromEnum(Profile.full) => &backend_tree.full_host_block_uids,
        @intFromEnum(Profile.windows_service) => &backend_tree.service_host_block_uids,
        @intFromEnum(Profile.normal_read_only) => &backend_tree.normal_readonly_host_block_uids,
        else => return code(.invalid_argument),
    };
    needed.* = blocks.len;
    if (capacity < blocks.len) return code(.buffer_too_small);
    const output = buffer orelse return code(.invalid_argument);
    @memcpy(output[0..blocks.len], blocks);
    return code(.ok);
}

pub export fn rm_control_create(
    config_pointer: ?*const Config,
    callbacks_pointer: ?*const HostCallbacks,
    output_handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = output_handle orelse return code(.invalid_argument);
    output.* = null;

    const config_view = config_pointer orelse return code(.invalid_argument);
    if (config_view.struct_size != @sizeOf(Config)) return code(.abi_mismatch);
    const callbacks_view = callbacks_pointer orelse return code(.invalid_argument);
    const callbacks = hostFrom(callbacks_view) orelse return code(.invalid_argument);
    const profile: Profile = switch (config_view.profile) {
        @intFromEnum(Profile.full) => .full,
        @intFromEnum(Profile.windows_service) => .windows_service,
        @intFromEnum(Profile.normal_read_only) => .normal_read_only,
        else => return code(.invalid_argument),
    };
    const config = configFrom(config_view) catch |err| return codeFor(err);

    const session = Allocator.create(Session) catch return code(.out_of_memory);
    const controller: Controller = switch (profile) {
        .full => blk: {
            const engine = backend_tree.Engine.create(Allocator, config, callbacks) catch |err| {
                Allocator.destroy(session);
                return codeFor(err);
            };
            break :blk .{ .full = engine };
        },
        .windows_service => blk: {
            const engine = backend_tree.ServiceEngine.create(Allocator, config, callbacks) catch |err| {
                Allocator.destroy(session);
                return codeFor(err);
            };
            break :blk .{ .windows_service = engine };
        },
        .normal_read_only => blk: {
            const engine = backend_tree.NormalReadOnlyEngine.create(Allocator, config, callbacks) catch |err| {
                Allocator.destroy(session);
                return codeFor(err);
            };
            break :blk .{ .normal_readonly = engine };
        },
    };
    session.* = .{ .controller = controller };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_control_activate(handle: ?*anyopaque) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    switch (session.controller) {
        .full => |controller| controller.activate(),
        .windows_service => |controller| controller.activate(),
        .normal_readonly => |controller| controller.activate(),
    }
    return code(.ok);
}

pub export fn rm_control_destroy(handle: ?*anyopaque) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    switch (session.controller) {
        .full => |controller| controller.destroy() catch |err| return codeFor(err),
        .windows_service => |controller| controller.destroy() catch |err| return codeFor(err),
        .normal_readonly => |controller| controller.destroy() catch |err| return codeFor(err),
    }
    Allocator.destroy(session);
    return code(.ok);
}

pub export fn rm_control_claim_write(
    handle: ?*anyopaque,
    key_pointer: ?*const ItemKeyView,
    output_claim: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = output_claim orelse return code(.invalid_argument);
    output.* = null;
    const session = castSession(handle) orelse return code(.invalid_argument);
    const key = key_pointer orelse return code(.invalid_argument);
    const claim = switch (session.controller) {
        .full => |controller| controller.claimWrite(key.toKey()) catch |err| return codeFor(err),
        .windows_service => |controller| controller.claimWrite(key.toKey()) catch |err| return codeFor(err),
        .normal_readonly => |controller| controller.claimWrite(key.toKey()) catch |err| return codeFor(err),
    };
    output.* = @ptrCast(claim);
    return code(.ok);
}

pub export fn rm_control_claim_cell(
    handle: ?*anyopaque,
    key_pointer: ?*const ItemKeyView,
    output_claim: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = output_claim orelse return code(.invalid_argument);
    output.* = null;
    const session = castSession(handle) orelse return code(.invalid_argument);
    const key = key_pointer orelse return code(.invalid_argument);
    const claim = switch (session.controller) {
        .full => |controller| controller.claimCell(key.toKey()) catch |err| return codeFor(err),
        .windows_service => |controller| controller.claimCell(key.toKey()) catch |err| return codeFor(err),
        .normal_readonly => |controller| controller.claimCell(key.toKey()) catch |err| return codeFor(err),
    };
    output.* = @ptrCast(claim);
    return code(.ok);
}

pub export fn rm_control_read(
    handle: ?*anyopaque,
    key_pointer: ?*const ItemKeyView,
    output_value: ?*u32,
) callconv(.c) i32 {
    const output = output_value orelse return code(.invalid_argument);
    const session = castSession(handle) orelse return code(.invalid_argument);
    const key = key_pointer orelse return code(.invalid_argument);
    const value = switch (session.controller) {
        .full => |controller| controller.read(key.toKey()) catch |err| return codeFor(err),
        .windows_service => |controller| controller.read(key.toKey()) catch |err| return codeFor(err),
        .normal_readonly => |controller| controller.read(key.toKey()) catch |err| return codeFor(err),
    };
    output.* = @intFromEnum(readToView(value));
    return code(.ok);
}

pub export fn rm_control_read_presence(
    handle: ?*anyopaque,
    key_pointer: ?*const ItemKeyView,
    output_value: ?*u32,
) callconv(.c) i32 {
    const output = output_value orelse return code(.invalid_argument);
    const session = castSession(handle) orelse return code(.invalid_argument);
    const key = key_pointer orelse return code(.invalid_argument);
    const value = switch (session.controller) {
        .full => |controller| controller.readPresence(key.toKey()) catch |err| return codeFor(err),
        .windows_service => |controller| controller.readPresence(key.toKey()) catch |err| return codeFor(err),
        .normal_readonly => |controller| controller.readPresence(key.toKey()) catch |err| return codeFor(err),
    };
    output.* = @intFromEnum(presenceToView(value));
    return code(.ok);
}

pub export fn rm_control_read_state(
    handle: ?*anyopaque,
    block_uid: u32,
    output_state: ?*u32,
) callconv(.c) i32 {
    const output = output_state orelse return code(.invalid_argument);
    const session = castSession(handle) orelse return code(.invalid_argument);
    const value = switch (session.controller) {
        .full => |controller| controller.readState(block_uid) catch |err| return codeFor(err),
        .windows_service => |controller| controller.readState(block_uid) catch |err| return codeFor(err),
        .normal_readonly => |controller| controller.readState(block_uid) catch |err| return codeFor(err),
    };
    output.* = @intFromEnum(value);
    return code(.ok);
}

pub export fn rm_control_publish(
    handle: ?*anyopaque,
    claim_pointer: ?*anyopaque,
    value: u32,
    reason_pointer: ?*const ReasonView,
    completion_token: u64,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    const claim = castClaim(claim_pointer) orelse return code(.invalid_argument);
    const reason = reason_pointer orelse return code(.invalid_argument);
    const publication = readFromView(value) catch return code(.invalid_argument);
    const token = optionalToken(completion_token);
    return switch (session.controller) {
        .full => |controller| finish(controller.publish(claim, publication, reason.toReason(), token)),
        .windows_service => |controller| finish(controller.publish(claim, publication, reason.toReason(), token)),
        .normal_readonly => |controller| finish(controller.publish(claim, publication, reason.toReason(), token)),
    };
}

pub export fn rm_control_publish_presence(
    handle: ?*anyopaque,
    claim_pointer: ?*anyopaque,
    value: u32,
    reason_pointer: ?*const ReasonView,
    completion_token: u64,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    const claim = castClaim(claim_pointer) orelse return code(.invalid_argument);
    const reason = reason_pointer orelse return code(.invalid_argument);
    const publication = presenceFromView(value) catch return code(.invalid_argument);
    const token = optionalToken(completion_token);
    return switch (session.controller) {
        .full => |controller| finish(controller.publishPresence(claim, publication, reason.toReason(), token)),
        .windows_service => |controller| finish(controller.publishPresence(claim, publication, reason.toReason(), token)),
        .normal_readonly => |controller| finish(controller.publishPresence(claim, publication, reason.toReason(), token)),
    };
}

pub export fn rm_control_bind(
    handle: ?*anyopaque,
    claim_pointer: ?*anyopaque,
    reason_pointer: ?*const ReasonView,
    completion_token: u64,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    const claim = castClaim(claim_pointer) orelse return code(.invalid_argument);
    const reason = reason_pointer orelse return code(.invalid_argument);
    const token = optionalToken(completion_token);
    return switch (session.controller) {
        .full => |controller| finish(controller.bind(claim, reason.toReason(), token)),
        .windows_service => |controller| finish(controller.bind(claim, reason.toReason(), token)),
        .normal_readonly => |controller| finish(controller.bind(claim, reason.toReason(), token)),
    };
}

pub export fn rm_control_unbind(
    handle: ?*anyopaque,
    claim_pointer: ?*anyopaque,
    reason_pointer: ?*const ReasonView,
    completion_token: u64,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    const claim = castClaim(claim_pointer) orelse return code(.invalid_argument);
    const reason = reason_pointer orelse return code(.invalid_argument);
    const token = optionalToken(completion_token);
    return switch (session.controller) {
        .full => |controller| finish(controller.unbind(claim, reason.toReason(), token)),
        .windows_service => |controller| finish(controller.unbind(claim, reason.toReason(), token)),
        .normal_readonly => |controller| finish(controller.unbind(claim, reason.toReason(), token)),
    };
}

pub export fn rm_control_set_demand(
    handle: ?*anyopaque,
    key_pointer: ?[*]const u8,
    key_length: u32,
    block_uid: u32,
    reason_pointer: ?*const ReasonView,
    completion_token: u64,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    const key = bytesFrom(key_pointer, key_length) catch return code(.invalid_argument);
    const reason = reason_pointer orelse return code(.invalid_argument);
    const token = optionalToken(completion_token);
    return switch (session.controller) {
        .full => |controller| finish(controller.setDemand(key, block_uid, reason.toReason(), token)),
        .windows_service => |controller| finish(controller.setDemand(key, block_uid, reason.toReason(), token)),
        .normal_readonly => |controller| finish(controller.setDemand(key, block_uid, reason.toReason(), token)),
    };
}

pub export fn rm_control_clear_demand(
    handle: ?*anyopaque,
    key_pointer: ?[*]const u8,
    key_length: u32,
    reason_pointer: ?*const ReasonView,
    completion_token: u64,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    const key = bytesFrom(key_pointer, key_length) catch return code(.invalid_argument);
    const reason = reason_pointer orelse return code(.invalid_argument);
    const token = optionalToken(completion_token);
    return switch (session.controller) {
        .full => |controller| finish(controller.clearDemand(key, reason.toReason(), token)),
        .windows_service => |controller| finish(controller.clearDemand(key, reason.toReason(), token)),
        .normal_readonly => |controller| finish(controller.clearDemand(key, reason.toReason(), token)),
    };
}

pub export fn rm_control_register_reader(
    handle: ?*anyopaque,
    key_pointer: ?*const ItemKeyView,
    holder_pointer: ?[*]const u8,
    holder_length: u32,
    output_registration_id: ?*u64,
) callconv(.c) i32 {
    const output = output_registration_id orelse return code(.invalid_argument);
    output.* = 0;
    const session = castSession(handle) orelse return code(.invalid_argument);
    const key = key_pointer orelse return code(.invalid_argument);
    const holder = bytesFrom(holder_pointer, holder_length) catch return code(.invalid_argument);
    const registration_id = switch (session.controller) {
        .full => |controller| controller.registerReader(key.toKey(), holder) catch |err| return codeFor(err),
        .windows_service => |controller| controller.registerReader(key.toKey(), holder) catch |err| return codeFor(err),
        .normal_readonly => |controller| controller.registerReader(key.toKey(), holder) catch |err| return codeFor(err),
    };
    output.* = registration_id;
    return code(.ok);
}

pub export fn rm_control_unregister_reader(handle: ?*anyopaque, registration_id: u64) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return switch (session.controller) {
        .full => |controller| finish(controller.unregisterReader(registration_id)),
        .windows_service => |controller| finish(controller.unregisterReader(registration_id)),
        .normal_readonly => |controller| finish(controller.unregisterReader(registration_id)),
    };
}

pub export fn rm_control_run_running(handle: ?*anyopaque, block_uid: u32, run: u64) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return switch (session.controller) {
        .full => |controller| finish(controller.reportRunning(block_uid, run)),
        .windows_service => |controller| finish(controller.reportRunning(block_uid, run)),
        .normal_readonly => |controller| finish(controller.reportRunning(block_uid, run)),
    };
}

pub export fn rm_control_run_returned(handle: ?*anyopaque, block_uid: u32, run: u64) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return switch (session.controller) {
        .full => |controller| finish(controller.reportReturned(block_uid, run)),
        .windows_service => |controller| finish(controller.reportReturned(block_uid, run)),
        .normal_readonly => |controller| finish(controller.reportReturned(block_uid, run)),
    };
}

pub export fn rm_control_shutdown(
    handle: ?*anyopaque,
    reason_pointer: ?*const ReasonView,
    completion_token: u64,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    const reason = reason_pointer orelse return code(.invalid_argument);
    const token = optionalToken(completion_token);
    return switch (session.controller) {
        .full => |controller| finish(controller.shutdown(reason.toReason(), token)),
        .windows_service => |controller| finish(controller.shutdown(reason.toReason(), token)),
        .normal_readonly => |controller| finish(controller.shutdown(reason.toReason(), token)),
    };
}

/// Writes a complete JSON snapshot when there is room. On a short buffer it
/// still returns the full byte count through `required_length`.
pub export fn rm_control_snapshot(
    handle: ?*anyopaque,
    buffer_pointer: ?[*]u8,
    buffer_length: usize,
    required_length: ?*usize,
) callconv(.c) i32 {
    const required = required_length orelse return code(.invalid_argument);
    required.* = 0;
    const session = castSession(handle) orelse return code(.invalid_argument);
    if (buffer_length > 0 and buffer_pointer == null) return code(.invalid_argument);
    var empty_buffer: [0]u8 = .{};
    const buffer: []u8 = if (buffer_length == 0) empty_buffer[0..] else buffer_pointer.?[0..buffer_length];
    const length = switch (session.controller) {
        .full => |controller| controller.snapshot(buffer) catch |err| return codeFor(err),
        .windows_service => |controller| controller.snapshot(buffer) catch |err| return codeFor(err),
        .normal_readonly => |controller| controller.snapshot(buffer) catch |err| return codeFor(err),
    };
    required.* = length;
    return code(if (length > buffer_length) .buffer_too_small else .ok);
}

fn configFrom(view: *const Config) !control.Config {
    if (view.host_block_count > 0 and view.host_blocks == null) return error.InvalidArgument;
    const host_block_count: usize = @intCast(view.host_block_count);
    const host_blocks = if (host_block_count == 0) &.{} else view.host_blocks.?[0..host_block_count];
    return .{
        .worker_count = view.worker_count,
        .history_capacity = view.history_capacity,
        .demand_capacity = view.demand_capacity,
        .demand_bytes = view.demand_bytes,
        .completion_capacity = view.completion_capacity,
        .reader_capacity = view.reader_capacity,
        .reader_bytes = view.reader_bytes,
        .worker_arena_bytes = view.worker_arena_bytes,
        .reason_text_capacity = view.reason_text_capacity,
        .demand_item = if (view.demand_item_uid == no_uid) null else view.demand_item_uid,
        .shutdown_item = view.shutdown_item_uid,
        .host_blocks = host_blocks,
    };
}

fn hostFrom(view: *const HostCallbacks) ?control.Host {
    return .{
        .context = view.context,
        .start = view.start orelse return null,
        .stop = view.stop orelse return null,
        .complete = view.complete orelse return null,
        .log = view.log orelse return null,
        .item_changed = view.item_changed orelse return null,
    };
}

fn bytesFrom(pointer: ?[*]const u8, length: u32) error{InvalidArgument}![]const u8 {
    if (length == 0) return &.{};
    const data = pointer orelse return error.InvalidArgument;
    return data[0..length];
}

fn readFromView(value: u32) error{InvalidArgument}!control.Read {
    return switch (value) {
        @intFromEnum(ReadView.not_published) => .not_published,
        @intFromEnum(ReadView.owner_stopped) => .owner_stopped,
        @intFromEnum(ReadView.read_failed) => .read_failed,
        @intFromEnum(ReadView.false_value) => .{ .value = false },
        @intFromEnum(ReadView.true_value) => .{ .value = true },
        else => error.InvalidArgument,
    };
}

fn presenceFromView(value: u32) error{InvalidArgument}!control.Presence {
    return switch (value) {
        @intFromEnum(PresenceView.present) => .present,
        @intFromEnum(PresenceView.not_published) => .not_published,
        @intFromEnum(PresenceView.owner_stopped) => .owner_stopped,
        @intFromEnum(PresenceView.read_failed) => .read_failed,
        else => error.InvalidArgument,
    };
}

fn readToView(value: control.Read) ReadView {
    return switch (value) {
        .not_published => .not_published,
        .owner_stopped => .owner_stopped,
        .read_failed => .read_failed,
        .value => |boolean| if (boolean) .true_value else .false_value,
    };
}

fn presenceToView(value: control.Presence) PresenceView {
    return switch (value) {
        .present => .present,
        .not_published => .not_published,
        .owner_stopped => .owner_stopped,
        .read_failed => .read_failed,
    };
}

fn optionalToken(value: u64) ?u64 {
    return if (value == 0) null else value;
}

fn castSession(handle: ?*anyopaque) ?*Session {
    return @ptrCast(@alignCast(handle orelse return null));
}

fn castClaim(claim: ?*anyopaque) ?control.state.Claim {
    return @ptrCast(claim orelse return null);
}

fn finish(result: anyerror!void) i32 {
    result catch |err| return codeFor(err);
    return code(.ok);
}

fn codeFor(err: anyerror) i32 {
    return code(switch (err) {
        error.OutOfMemory => .out_of_memory,
        error.InvalidArgument => .invalid_argument,
        error.AbiMismatch => .abi_mismatch,
        error.ThreadQuotaExceeded,
        error.SystemResources,
        error.LockedMemoryLimitExceeded,
        error.Unexpected,
        => .unavailable,
        error.InvalidConfiguration => .control_invalid_configuration,
        error.HostBlocksMismatch => .control_host_blocks_mismatch,
        error.UnregisteredItem => .control_undeclared_item,
        error.UnregisteredBlock => .control_unregistered_block,
        error.StorageMismatch => .control_storage_mismatch,
        error.AlreadyClaimed => .control_duplicate_claim,
        error.InvalidClaim => .control_invalid_claim,
        error.TypeMismatch => .control_type_mismatch,
        error.NotActive => .control_not_active,
        error.CompletionFull => .control_completion_full,
        error.InvalidReason => .invalid_argument,
        error.ReasonTooLong => .control_reason_too_long,
        error.InvalidDemand => .control_invalid_demand,
        error.NoDemandItem => .control_no_demand_item,
        error.DemandFull => .control_demand_full,
        error.InvalidHolder => .control_invalid_holder,
        error.DuplicateHolder => .control_duplicate_reader,
        error.ReaderFull => .control_reader_full,
        error.NotShuttingDown => .control_not_shutting_down,
        error.RunsActive => .control_runs_active,
        else => @panic("unmapped rm_control ABI error; fail fast rather than hide a controller defect"),
    });
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}

test "every reachable controller error has an explicit result code" {
    try std.testing.expectEqual(code(.out_of_memory), codeFor(error.OutOfMemory));
    try std.testing.expectEqual(code(.invalid_argument), codeFor(error.InvalidArgument));
    try std.testing.expectEqual(code(.abi_mismatch), codeFor(error.AbiMismatch));
    try std.testing.expectEqual(code(.unavailable), codeFor(error.ThreadQuotaExceeded));
    try std.testing.expectEqual(code(.unavailable), codeFor(error.SystemResources));
    try std.testing.expectEqual(code(.unavailable), codeFor(error.LockedMemoryLimitExceeded));
    try std.testing.expectEqual(code(.unavailable), codeFor(error.Unexpected));
    try std.testing.expectEqual(code(.control_invalid_configuration), codeFor(error.InvalidConfiguration));
    try std.testing.expectEqual(code(.control_host_blocks_mismatch), codeFor(error.HostBlocksMismatch));
    try std.testing.expectEqual(code(.control_undeclared_item), codeFor(error.UnregisteredItem));
    try std.testing.expectEqual(code(.control_unregistered_block), codeFor(error.UnregisteredBlock));
    try std.testing.expectEqual(code(.control_storage_mismatch), codeFor(error.StorageMismatch));
    try std.testing.expectEqual(code(.control_duplicate_claim), codeFor(error.AlreadyClaimed));
    try std.testing.expectEqual(code(.control_invalid_claim), codeFor(error.InvalidClaim));
    try std.testing.expectEqual(code(.control_type_mismatch), codeFor(error.TypeMismatch));
    try std.testing.expectEqual(code(.control_not_active), codeFor(error.NotActive));
    try std.testing.expectEqual(code(.control_completion_full), codeFor(error.CompletionFull));
    try std.testing.expectEqual(code(.invalid_argument), codeFor(error.InvalidReason));
    try std.testing.expectEqual(code(.control_reason_too_long), codeFor(error.ReasonTooLong));
    try std.testing.expectEqual(code(.control_invalid_demand), codeFor(error.InvalidDemand));
    try std.testing.expectEqual(code(.control_no_demand_item), codeFor(error.NoDemandItem));
    try std.testing.expectEqual(code(.control_demand_full), codeFor(error.DemandFull));
    try std.testing.expectEqual(code(.control_invalid_holder), codeFor(error.InvalidHolder));
    try std.testing.expectEqual(code(.control_duplicate_reader), codeFor(error.DuplicateHolder));
    try std.testing.expectEqual(code(.control_reader_full), codeFor(error.ReaderFull));
    try std.testing.expectEqual(code(.control_not_shutting_down), codeFor(error.NotShuttingDown));
    try std.testing.expectEqual(code(.control_runs_active), codeFor(error.RunsActive));
}
