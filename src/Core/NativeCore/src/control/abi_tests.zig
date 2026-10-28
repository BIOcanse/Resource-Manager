const std = @import("std");
const control = @import("rm_control");
const declarations = @import("backend_declarations");
const backend_tree = @import("backend_tree");
const abi = @import("abi.zig");
const ResultCode = @import("../common/result_codes.zig").ResultCode;
const B = declarations.BlockUids;

const owner_text = "abi-test";
const cause_text = "control-boundary";
const reason = abi.ReasonView{
    .kind = 1,
    .owner = owner_text.ptr,
    .owner_length = owner_text.len,
    .cause = cause_text.ptr,
    .cause_length = cause_text.len,
    .plan_version = 1,
};

test "C ABI layout and state tags are stable" {
    const profile_signature: *const fn (u32, ?[*]u32, usize, ?*usize) callconv(.c) i32 = &abi.rm_control_profile_host_blocks;
    _ = profile_signature;
    try std.testing.expectEqual(@as(usize, 56), @sizeOf(abi.Config));
    try std.testing.expectEqual(@as(usize, 40), @offsetOf(abi.Config, "host_blocks"));
    try std.testing.expectEqual(@as(usize, 48), @offsetOf(abi.Config, "host_block_count"));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(abi.HostCallbacks));
    try std.testing.expectEqual(@as(usize, 8), @sizeOf(abi.ItemKeyView));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(abi.ReasonView));
    try std.testing.expectEqual(@as(usize, 8), @sizeOf(abi.FunctionRead));
    try std.testing.expectEqual(@as(usize, 16), @sizeOf(abi.ReadFunction));

    try std.testing.expectEqual(@as(u32, 0), @intFromEnum(abi.ReadView.not_published));
    try std.testing.expectEqual(@as(u32, 1), @intFromEnum(abi.ReadView.owner_stopped));
    try std.testing.expectEqual(@as(u32, 2), @intFromEnum(abi.ReadView.read_failed));
    try std.testing.expectEqual(@as(u32, 3), @intFromEnum(abi.ReadView.false_value));
    try std.testing.expectEqual(@as(u32, 4), @intFromEnum(abi.ReadView.true_value));
    try std.testing.expectEqual(@as(u32, 0), @intFromEnum(abi.PresenceView.present));
    try std.testing.expectEqual(@as(u32, 1), @intFromEnum(abi.PresenceView.not_published));
    try std.testing.expectEqual(@as(u32, 2), @intFromEnum(abi.PresenceView.owner_stopped));
    try std.testing.expectEqual(@as(u32, 3), @intFromEnum(abi.PresenceView.read_failed));
    try std.testing.expectEqual(@as(u32, 0), @intFromEnum(abi.BlockStateView.stopped));
    try std.testing.expectEqual(@as(u32, 1), @intFromEnum(abi.BlockStateView.starting));
    try std.testing.expectEqual(@as(u32, 2), @intFromEnum(abi.BlockStateView.running));
    try std.testing.expectEqual(@as(u32, 3), @intFromEnum(abi.BlockStateView.stopping));
    try std.testing.expectEqual(@as(u32, 1), @intFromEnum(abi.Profile.full));
    try std.testing.expectEqual(@as(u32, 2), @intFromEnum(abi.Profile.windows_service));
    try std.testing.expectEqual(@as(u32, 3), @intFromEnum(abi.Profile.normal_read_only));
    try std.testing.expectEqual(@as(u32, 0x0002_0000), abi.rm_control_abi_version());
}

test "C read functions return current owner values outside controller locks and unbind suppresses reads" {
    const config = makeConfig(.full);
    const callbacks = testCallbacks();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_create(&config, &callbacks, &handle));
    defer closeHandle(&handle);
    var context = FunctionContext{ .handle = handle };
    const function = abi.ReadFunction{ .context = &context, .read = functionRead };
    const key = abi.ItemKeyView{ .item = declarations.ItemUids.PresentUnfrozen, .block = 0 };
    var claim: ?*anyopaque = null;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_claim_read_function(handle, &key, &function, &claim));
    var value: u32 = 99;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_read(handle, &key, &value));
    try std.testing.expectEqual(@intFromEnum(abi.ReadView.false_value), value);
    context.value = true;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_notify_changed(handle, claim, &reason, 0));
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_read(handle, &key, &value));
    try std.testing.expectEqual(@intFromEnum(abi.ReadView.true_value), value);
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), context.reentry_code);
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_unbind(handle, claim, &reason, 0));
    const unbound_calls = context.calls;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_read(handle, &key, &value));
    try std.testing.expectEqual(@intFromEnum(abi.ReadView.owner_stopped), value);
    try std.testing.expectEqual(unbound_calls, context.calls);
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_bind(handle, claim, &reason, 0));
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_read(handle, &key, &value));
    try std.testing.expectEqual(@intFromEnum(abi.ReadView.true_value), value);
}

const FunctionContext = struct {
    handle: ?*anyopaque,
    value: bool = false,
    calls: usize = 0,
    reentry_code: i32 = -1,
};

fn functionRead(raw: ?*anyopaque, output: *abi.FunctionRead) callconv(.c) void {
    const context: *FunctionContext = @ptrCast(@alignCast(raw orelse unreachable));
    var state: u32 = 99;
    context.reentry_code = abi.rm_control_read_state(context.handle, B.Backend, &state);
    context.calls += 1;
    output.* = .{ .kind = 3, .value = @intFromBool(context.value) };
}

test "profile host UID export reports exact lists and validates caller buffers" {
    const profiles = [_]struct { profile: abi.Profile, blocks: []const u32 }{
        .{ .profile = .full, .blocks = &backend_tree.full_host_block_uids },
        .{ .profile = .windows_service, .blocks = &backend_tree.service_host_block_uids },
        .{ .profile = .normal_read_only, .blocks = &backend_tree.normal_readonly_host_block_uids },
    };
    for (profiles) |profile| {
        var needed: usize = 999;
        const tag = @intFromEnum(profile.profile);
        try std.testing.expectEqual(@intFromEnum(ResultCode.buffer_too_small), abi.rm_control_profile_host_blocks(tag, null, 0, &needed));
        try std.testing.expectEqual(profile.blocks.len, needed);
        var output: [backend_tree.service_host_block_uids.len + 1]u32 = @splat(0xaaaa_aaaa);
        try std.testing.expectEqual(@intFromEnum(ResultCode.buffer_too_small), abi.rm_control_profile_host_blocks(tag, &output, needed - 1, &needed));
        for (output) |uid| try std.testing.expectEqual(@as(u32, 0xaaaa_aaaa), uid);
        try std.testing.expectEqual(@intFromEnum(ResultCode.invalid_argument), abi.rm_control_profile_host_blocks(tag, null, needed, &needed));
        try std.testing.expectEqual(profile.blocks.len, needed);
        try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_profile_host_blocks(tag, &output, output.len, &needed));
        try std.testing.expectEqualSlices(u32, profile.blocks, output[0..needed]);
        for (output[needed..]) |uid| try std.testing.expectEqual(@as(u32, 0xaaaa_aaaa), uid);
        try std.testing.expect(std.mem.indexOfScalar(u32, output[0..needed], B.Program) == null);
    }
    var needed: usize = 999;
    try std.testing.expectEqual(@intFromEnum(ResultCode.invalid_argument), abi.rm_control_profile_host_blocks(99, null, 0, &needed));
    try std.testing.expectEqual(@as(usize, 0), needed);
    try std.testing.expectEqual(@intFromEnum(ResultCode.invalid_argument), abi.rm_control_profile_host_blocks(1, null, 0, null));
}

test "production profiles have one backend root and preserve domain ownership and data edges" {
    const profiles = [_]struct { topology: control.tree.Tree, blocks: usize, inputs: usize, backend_children: usize }{
        .{ .topology = backend_tree.topology, .blocks = backend_tree.full_host_block_uids.len + 1, .inputs = 42, .backend_children = 10 },
        .{ .topology = backend_tree.service_topology, .blocks = backend_tree.service_host_block_uids.len + 1, .inputs = 43, .backend_children = 10 },
        .{ .topology = backend_tree.normal_readonly_topology, .blocks = backend_tree.normal_readonly_host_block_uids.len + 1, .inputs = 20, .backend_children = 5 },
    };
    for (profiles) |profile| {
        try std.testing.expectEqual(profile.blocks, profile.topology.blocks.len);
        var root_children: usize = 0;
        var backend_children: usize = 0;
        var input_edges: usize = 0;
        for (profile.topology.blocks) |block| {
            input_edges += block.inputs.len;
            if (block.parent) |parent| {
                if (parent == B.Program) {
                    root_children += 1;
                    try std.testing.expectEqual(B.Backend, block.uid);
                }
                if (parent == B.Backend) backend_children += 1;
                if (block.uid == B.FanControlCoreLifetimeService) try std.testing.expectEqual(B.HardwareControl, parent);
                if (block.uid == B.FanCurveTemperatureFeed) try std.testing.expectEqual(B.FanControlCoreLifetimeService, parent);
                if (block.uid == B.HostMemoryProtection) try std.testing.expectEqual(B.Backend, parent);
                if (block.uid == B.KernelLeakObservation) {
                    try std.testing.expectEqual(B.Reports, parent);
                    try std.testing.expectEqualSlices(u32, &.{B.WindowsResourceBreakdownSampler}, block.inputs);
                }
            } else try std.testing.expectEqual(B.Program, block.uid);
        }
        try std.testing.expectEqual(@as(usize, 1), root_children);
        try std.testing.expectEqual(profile.backend_children, backend_children);
        try std.testing.expectEqual(profile.inputs, input_edges);
    }
}

test "full profile exposes claims, typed reads, read functions, readers and bounded snapshots" {
    const config = makeConfig(.full);
    const callbacks = testCallbacks();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_create(&config, &callbacks, &handle));
    defer closeHandle(&handle);

    const bool_item = itemByUid(backend_tree.declarations, declarations.ItemUids.PresentNativeSessionAvailable) orelse return error.TestUnexpectedResult;
    const bool_key = keyFor(bool_item);
    var claim: ?*anyopaque = null;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_claim_cell(handle, &bool_key, &claim));
    try std.testing.expect(claim != null);
    var duplicate_claim: ?*anyopaque = null;
    try std.testing.expectEqual(@intFromEnum(ResultCode.control_duplicate_claim), abi.rm_control_claim_cell(handle, &bool_key, &duplicate_claim));
    try std.testing.expect(duplicate_claim == null);

    var value: u32 = 99;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_read(handle, &bool_key, &value));
    try std.testing.expectEqual(@intFromEnum(abi.ReadView.not_published), value);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_control_publish(handle, claim, @intFromEnum(abi.ReadView.false_value), &reason, 0),
    );
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_read(handle, &bool_key, &value));
    try std.testing.expectEqual(@intFromEnum(abi.ReadView.false_value), value);

    const host_item = firstItem(backend_tree.declarations, .host, .own) orelse return error.TestUnexpectedResult;
    const host_key = keyFor(host_item);
    try std.testing.expectEqual(@intFromEnum(ResultCode.control_type_mismatch), abi.rm_control_read(handle, &host_key, &value));
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_read_presence(handle, &host_key, &value));
    try std.testing.expectEqual(@intFromEnum(abi.PresenceView.not_published), value);

    const holder = "backend-settings-reader";
    var reader_id: u64 = 0;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_control_register_reader(handle, &host_key, holder.ptr, holder.len, &reader_id),
    );
    try std.testing.expect(reader_id > 0);
    var duplicate_reader_id: u64 = 0;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.control_duplicate_reader),
        abi.rm_control_register_reader(handle, &host_key, holder.ptr, holder.len, &duplicate_reader_id),
    );
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_unregister_reader(handle, reader_id));
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_unregister_reader(handle, reader_id));

    var host_claim: ?*anyopaque = null;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_claim_write(handle, &host_key, &host_claim));
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_control_publish_presence(handle, host_claim, @intFromEnum(abi.PresenceView.present), &reason, 0),
    );
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_read_presence(handle, &host_key, &value));
    try std.testing.expectEqual(@intFromEnum(abi.PresenceView.present), value);

    var required_length: usize = 0;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.buffer_too_small),
        abi.rm_control_snapshot(handle, null, 0, &required_length),
    );
    try std.testing.expect(required_length > 0);
    const snapshot = try std.testing.allocator.alloc(u8, required_length);
    defer std.testing.allocator.free(snapshot);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_control_snapshot(handle, snapshot.ptr, snapshot.len, &required_length),
    );
    try std.testing.expect(std.mem.indexOf(u8, snapshot, "\"registryVersion\"") != null);
    try std.testing.expect(std.mem.indexOf(u8, snapshot, "\"parent\"") != null);

    const unknown_key = abi.ItemKeyView{ .item = std.math.maxInt(u32), .block = 0 };
    var unknown_claim: ?*anyopaque = null;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.control_undeclared_item),
        abi.rm_control_claim_write(handle, &unknown_key, &unknown_claim),
    );
}

test "Windows Service profile selects the additional update-service block" {
    var config = makeConfig(.windows_service);
    const callbacks = testCallbacks();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_create(&config, &callbacks, &handle));
    defer closeHandle(&handle);

    var state: u32 = 99;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_control_read_state(handle, declarations.BlockUids.AutomaticProductUpdateService, &state),
    );
    try std.testing.expectEqual(@intFromEnum(abi.BlockStateView.stopped), state);

    config.profile = @intFromEnum(abi.Profile.full);
    var wrong_profile_handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.control_host_blocks_mismatch),
        abi.rm_control_create(&config, &callbacks, &wrong_profile_handle),
    );
    try std.testing.expect(wrong_profile_handle == null);
}

test "NormalReadOnly profile selects its exact filtered host block set" {
    const expected_host_blocks = backend_tree.normal_readonly_host_block_uids;
    try std.testing.expectEqualSlices(
        u32,
        expected_host_blocks[0..],
        backend_tree.normal_readonly_host_block_uids[0..],
    );
    try std.testing.expectEqual(expected_host_blocks.len + 1, backend_tree.normal_readonly_registry.blocks.len);
    var normal_readonly_input_edges: usize = 0;
    for (backend_tree.normal_readonly_topology.blocks) |block| {
        normal_readonly_input_edges += block.inputs.len;
    }
    try std.testing.expectEqual(@as(usize, 20), normal_readonly_input_edges);

    var config = makeConfig(.normal_read_only);
    const callbacks = testCallbacks();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_create(&config, &callbacks, &handle));
    defer closeHandle(&handle);

    for (expected_host_blocks) |block_uid| {
        var state: u32 = 99;
        try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_read_state(handle, block_uid, &state));
        try std.testing.expectEqual(@intFromEnum(abi.BlockStateView.stopped), state);
    }

    config.host_blocks = backend_tree.full_host_block_uids[0..].ptr;
    config.host_block_count = @intCast(backend_tree.full_host_block_uids.len);
    var wrong_full_handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.control_host_blocks_mismatch),
        abi.rm_control_create(&config, &callbacks, &wrong_full_handle),
    );
    try std.testing.expect(wrong_full_handle == null);

    config.host_blocks = backend_tree.service_host_block_uids[0..].ptr;
    config.host_block_count = @intCast(backend_tree.service_host_block_uids.len);
    var wrong_service_handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.control_host_blocks_mismatch),
        abi.rm_control_create(&config, &callbacks, &wrong_service_handle),
    );
    try std.testing.expect(wrong_service_handle == null);
}

test "runtime and completion callbacks reenter the C ABI, and destroy refuses active runs" {
    var context = CallbackContext{};
    const config = makeConfig(.full);
    const callbacks = callbacksFor(&context);
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_create(&config, &callbacks, &handle));
    defer cleanupCallbackHandle(&handle, &context);
    context.handle = handle;

    const block_uid = declarations.BlockUids.TrustedAccessInvocationHostedService;
    context.permit_key = .{ .item = declarations.ItemUids.HostStartPermitted, .block = block_uid };
    const holder = "abi-reentrant-reader";
    var reader_id: u64 = 0;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_control_register_reader(handle, &context.permit_key, holder.ptr, holder.len, &reader_id),
    );
    for (backend_tree.full_host_block_uids) |uid| {
        if (uid == block_uid) continue;
        const permit = abi.ItemKeyView{ .item = declarations.ItemUids.HostStartPermitted, .block = uid };
        var parent_claim: ?*anyopaque = null;
        try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_claim_cell(handle, &permit, &parent_claim));
        try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_publish(handle, parent_claim, @intFromEnum(abi.ReadView.true_value), &reason, 0));
    }
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_activate(handle));

    // The parent branch is running before this round-completion check.
    try std.testing.expect(waitForBlockState(handle, B.TrustedAccess, .running));

    var claim: ?*anyopaque = null;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_claim_cell(handle, &context.permit_key, &claim));
    const completion_token: u64 = 91;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok),
        abi.rm_control_publish(handle, claim, @intFromEnum(abi.ReadView.true_value), &reason, completion_token));
    try std.testing.expect(waitForU64(&context.completed_token, completion_token));
    try std.testing.expect(waitForI32(&context.start_report_code, @intFromEnum(ResultCode.ok)));
    try std.testing.expect(context.item_changed_calls.load(.acquire) > 0);
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), context.item_changed_code.load(.acquire));
    try std.testing.expectEqual(@intFromEnum(abi.ReadView.true_value), context.item_changed_value.load(.acquire));
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), context.start_report_code.load(.acquire));
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), context.completion_read_code.load(.acquire));
    try std.testing.expectEqual(@intFromEnum(abi.BlockStateView.running), context.completion_block_state.load(.acquire));
    try std.testing.expect(waitForBlockState(handle, block_uid, .running));
    try std.testing.expect(context.started_run.load(.acquire) > 0);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.control_runs_active),
        abi.rm_control_destroy(handle),
    );

    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_shutdown(handle, &reason, 0));
    try std.testing.expect(waitForBlockState(handle, block_uid, .stopped));
    try std.testing.expect(waitForBlockState(handle, B.Backend, .stopped));
    try std.testing.expect(waitForI32(&context.stop_report_code, @intFromEnum(ResultCode.ok)));
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), context.stop_report_code.load(.acquire));
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_unregister_reader(handle, reader_id));
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_control_destroy(handle));
    handle = null;
    context.handle = null;
}

test "C ABI rejects invalid layout, profiles, tags, and null handles" {
    var config = makeConfig(.full);
    const callbacks = testCallbacks();
    var handle: ?*anyopaque = null;
    config.struct_size -= 4;
    try std.testing.expectEqual(@intFromEnum(ResultCode.abi_mismatch), abi.rm_control_create(&config, &callbacks, &handle));
    config = makeConfig(.full);
    config.profile = 99;
    try std.testing.expectEqual(@intFromEnum(ResultCode.invalid_argument), abi.rm_control_create(&config, &callbacks, &handle));

    const key = abi.ItemKeyView{ .item = declarations.ItemUids.HostStartPermitted, .block = backend_tree.full_host_block_uids[0] };
    try std.testing.expectEqual(@intFromEnum(ResultCode.invalid_argument), abi.rm_control_read(null, &key, null));
    try std.testing.expectEqual(@intFromEnum(ResultCode.invalid_argument), abi.rm_control_publish(null, null, 99, null, 0));
}

fn makeConfig(profile: abi.Profile) abi.Config {
    const host_blocks = switch (profile) {
        .full => backend_tree.full_host_block_uids[0..].ptr,
        .windows_service => backend_tree.service_host_block_uids[0..].ptr,
        .normal_read_only => backend_tree.normal_readonly_host_block_uids[0..].ptr,
    };
    const host_block_count = switch (profile) {
        .full => backend_tree.full_host_block_uids.len,
        .windows_service => backend_tree.service_host_block_uids.len,
        .normal_read_only => backend_tree.normal_readonly_host_block_uids.len,
    };
    return .{
        .struct_size = @sizeOf(abi.Config),
        .profile = @intFromEnum(profile),
        .worker_count = 1,
        .history_capacity = 4,
        .completion_capacity = 8,
        .reader_capacity = 8,
        .reader_bytes = 1024,
        .worker_arena_bytes = 1_048_576,
        .reason_text_capacity = 256,
        .shutdown_item_uid = declarations.ItemUids.ProgramShuttingDown,
        .host_blocks = host_blocks,
        .host_block_count = @intCast(host_block_count),
    };
}

fn testCallbacks() abi.HostCallbacks {
    return .{
        .context = null,
        .start = noStart,
        .stop = noStop,
        .complete = noComplete,
        .log = noLog,
        .item_changed = noItemChanged,
    };
}

const CallbackContext = struct {
    handle: ?*anyopaque = null,
    permit_key: abi.ItemKeyView = .{ .item = 0, .block = 0 },
    item_changed_calls: std.atomic.Value(u32) = .init(0),
    item_changed_code: std.atomic.Value(i32) = .init(-1),
    item_changed_value: std.atomic.Value(u32) = .init(99),
    completed_token: std.atomic.Value(u64) = .init(0),
    completion_read_code: std.atomic.Value(i32) = .init(-1),
    completion_block_state: std.atomic.Value(u32) = .init(99),
    started_run: std.atomic.Value(u64) = .init(0),
    start_report_code: std.atomic.Value(i32) = .init(-1),
    stop_report_code: std.atomic.Value(i32) = .init(-1),
};

fn callbacksFor(context: *CallbackContext) abi.HostCallbacks {
    return .{
        .context = @ptrCast(context),
        .start = reentrantStart,
        .stop = reentrantStop,
        .complete = reentrantComplete,
        .log = noLog,
        .item_changed = reentrantItemChanged,
    };
}

fn contextFrom(raw: ?*anyopaque) *CallbackContext {
    return @ptrCast(@alignCast(raw orelse unreachable));
}

fn reentrantStart(raw: ?*anyopaque, block_uid: u32, run: u64) callconv(.c) void {
    const context = contextFrom(raw);
    context.started_run.store(run, .release);
    context.start_report_code.store(abi.rm_control_run_running(context.handle, block_uid, run), .release);
}

fn reentrantStop(raw: ?*anyopaque, block_uid: u32, run: u64) callconv(.c) void {
    const context = contextFrom(raw);
    context.stop_report_code.store(abi.rm_control_run_returned(context.handle, block_uid, run), .release);
}

fn reentrantComplete(raw: ?*anyopaque, token: u64) callconv(.c) void {
    const context = contextFrom(raw);
    var state: u32 = 99;
    const result = abi.rm_control_read_state(context.handle, context.permit_key.block, &state);
    context.completion_block_state.store(state, .release);
    context.completion_read_code.store(result, .release);
    context.completed_token.store(token, .release);
}

fn reentrantItemChanged(raw: ?*anyopaque, _: u64, key: control.types.ItemKeyView, _: *const control.types.ReasonView) callconv(.c) void {
    const context = contextFrom(raw);
    var value: u32 = 99;
    const result = abi.rm_control_read(context.handle, &key, &value);
    context.item_changed_value.store(value, .release);
    context.item_changed_code.store(result, .release);
    _ = context.item_changed_calls.fetchAdd(1, .acq_rel);
}

fn waitForU64(value: *const std.atomic.Value(u64), expected: u64) bool {
    var attempts: usize = 0;
    while (attempts < 5_000_000) : (attempts += 1) {
        if (value.load(.acquire) == expected) return true;
        std.Thread.yield() catch {};
    }
    return false;
}

fn waitForI32(value: *const std.atomic.Value(i32), expected: i32) bool {
    var attempts: usize = 0;
    while (attempts < 5_000_000) : (attempts += 1) {
        if (value.load(.acquire) == expected) return true;
        std.Thread.yield() catch {};
    }
    return false;
}

fn waitForBlockState(handle: ?*anyopaque, block_uid: u32, expected: abi.BlockStateView) bool {
    var attempts: usize = 0;
    while (attempts < 5_000_000) : (attempts += 1) {
        var state: u32 = 99;
        if (abi.rm_control_read_state(handle, block_uid, &state) == @intFromEnum(ResultCode.ok) and state == @intFromEnum(expected)) return true;
        std.Thread.yield() catch {};
    }
    return false;
}

fn cleanupCallbackHandle(handle: *?*anyopaque, context: *CallbackContext) void {
    const current = handle.* orelse return;
    _ = abi.rm_control_shutdown(current, &reason, 0);
    _ = waitForBlockState(current, B.Backend, .stopped);
    _ = abi.rm_control_destroy(current);
    handle.* = null;
    context.handle = null;
}

fn noStart(_: ?*anyopaque, _: u32, _: u64) callconv(.c) void {}
fn noStop(_: ?*anyopaque, _: u32, _: u64) callconv(.c) void {}
fn noComplete(_: ?*anyopaque, _: u64) callconv(.c) void {}
fn noLog(_: ?*anyopaque, _: u32, _: [*]const u8, _: usize) callconv(.c) void {}
fn noItemChanged(_: ?*anyopaque, _: u64, _: control.types.ItemKeyView, _: *const control.types.ReasonView) callconv(.c) void {}

fn firstItem(registry: control.declarations.Registry, value_type: control.declarations.ValueType, storage: control.declarations.Storage) ?control.declarations.Item {
    for (registry.items) |item| {
        if (item.value_type == value_type and item.storage == storage) return item;
    }
    return null;
}

fn itemByUid(registry: control.declarations.Registry, uid: u32) ?control.declarations.Item {
    for (registry.items) |item| if (item.uid == uid) return item;
    return null;
}

fn keyFor(item: control.declarations.Item) abi.ItemKeyView {
    return .{
        .item = item.uid,
        .block = if (item.per_block) backend_tree.full_host_block_uids[0] else 0,
    };
}

fn closeHandle(handle: *?*anyopaque) void {
    const current = handle.* orelse return;
    std.debug.assert(abi.rm_control_shutdown(current, &reason, 0) == @intFromEnum(ResultCode.ok));
    std.debug.assert(abi.rm_control_destroy(current) == @intFromEnum(ResultCode.ok));
    handle.* = null;
}
