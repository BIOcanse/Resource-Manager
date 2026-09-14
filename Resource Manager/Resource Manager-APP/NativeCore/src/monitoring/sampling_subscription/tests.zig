const std = @import("std");
const abi = @import("abi.zig");
const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const output_capacity = 8;

const PlanResult = struct {
    header: protocol.PlanHeader,
    due: [output_capacity]protocol.DueItemOutput,
    sources: [output_capacity]protocol.SourceViewOutput,
    expired: [output_capacity]protocol.ExpiredSourceOutput,
};

test "module-local C ABI creates queries and destroys an opaque session" {
    var config = testConfig();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_sampling_subscription_create(&config, &handle),
    );
    defer abi.rm_sampling_subscription_destroy(handle);
    try std.testing.expect(handle != null);

    var capacity = std.mem.zeroes(protocol.Capacity);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_sampling_subscription_query_capacity(handle, &capacity, @sizeOf(protocol.Capacity)),
    );
    try std.testing.expectEqual(@as(u32, @sizeOf(protocol.Capacity)), capacity.struct_size);
    try std.testing.expectEqual(config.maximum_source_count, capacity.source_capacity);
    try std.testing.expectEqual(config.maximum_item_count, capacity.item_capacity);
    try std.testing.expectEqual(config.maximum_membership_count, capacity.membership_capacity);
    try std.testing.expectEqual(protocol.abi_version, abi.rm_sampling_subscription_abi_version());
}

test "module-local C ABI rejects invalid create and capacity query shapes" {
    var config = testConfig();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.invalid_argument)),
        abi.rm_sampling_subscription_create(null, &handle),
    );
    try std.testing.expectEqual(@as(?*anyopaque, null), handle);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.invalid_argument)),
        abi.rm_sampling_subscription_create(&config, null),
    );

    config.flags = 1;
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.abi_mismatch)),
        abi.rm_sampling_subscription_create(&config, &handle),
    );
    try std.testing.expectEqual(@as(?*anyopaque, null), handle);

    config.flags = 0;
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_sampling_subscription_create(&config, &handle),
    );
    defer abi.rm_sampling_subscription_destroy(handle);

    var capacity = std.mem.zeroes(protocol.Capacity);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.abi_mismatch)),
        abi.rm_sampling_subscription_query_capacity(handle, &capacity, @sizeOf(protocol.Capacity) - 1),
    );
    try std.testing.expectEqual(std.mem.zeroes(protocol.Capacity), capacity);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.invalid_argument)),
        abi.rm_sampling_subscription_query_capacity(null, &capacity, @sizeOf(protocol.Capacity)),
    );
}

test "module-local C ABI executes the complete stateful data path" {
    var config = testConfig();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_sampling_subscription_create(&config, &handle),
    );
    defer abi.rm_sampling_subscription_destroy(handle);

    var references = [_]protocol.ItemReference{ itemReference(10), itemReference(20) };
    var track_input = trackInput(1, 1_000, 1_000, 100, 0, 0, references.len);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_sampling_subscription_track(handle, &track_input, references[0..].ptr, references.len),
    );

    var header = planHeader(2, 1, 1_000, output_capacity, output_capacity, output_capacity);
    var due = std.mem.zeroes([output_capacity]protocol.DueItemOutput);
    var sources = std.mem.zeroes([output_capacity]protocol.SourceViewOutput);
    var expired = std.mem.zeroes([output_capacity]protocol.ExpiredSourceOutput);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_sampling_subscription_plan(
            handle,
            &header,
            due[0..].ptr,
            due.len,
            sources[0..].ptr,
            sources.len,
            expired[0..].ptr,
            expired.len,
        ),
    );
    try std.testing.expectEqual(@as(u32, 2), header.due_item_count);
    try std.testing.expectEqual(@as(u32, 1), header.source_view_count);
    try std.testing.expectEqual(@as(u64, 10), due[0].item_handle);
    try std.testing.expectEqual(@as(u64, 20), due[1].item_handle);

    var completion_input = protocol.CompletionInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CompletionInput),
        .configuration_generation = config.generation,
        .operation_epoch = 3,
        .plan_epoch = header.plan_epoch,
        .command_at_milliseconds = 1_050,
        .completed_at_milliseconds = 1_050,
        .valid_mask = protocol.CompletionValid.required,
        .item_count = 2,
        .status = @intFromEnum(protocol.CompletionStatus.sampled),
        .flags = 0,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
    var completion_output = std.mem.zeroes(protocol.CompletionOutput);
    completion_output.abi_version = protocol.abi_version;
    completion_output.struct_size = @sizeOf(protocol.CompletionOutput);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_sampling_subscription_complete(
            handle,
            &completion_input,
            references[0..].ptr,
            2,
            &completion_output,
        ),
    );
    try std.testing.expectEqual(@as(u32, 2), completion_output.active_item_count);

    header = planHeader(4, 2, 1_050, output_capacity, output_capacity, output_capacity);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_sampling_subscription_plan(
            handle,
            &header,
            due[0..].ptr,
            due.len,
            sources[0..].ptr,
            sources.len,
            expired[0..].ptr,
            expired.len,
        ),
    );
    try std.testing.expectEqual(@as(u32, 0), header.due_item_count);

    var snapshot_header = std.mem.zeroes(protocol.SnapshotHeader);
    snapshot_header.abi_version = protocol.abi_version;
    snapshot_header.struct_size = @sizeOf(protocol.SnapshotHeader);
    var snapshot_sources = std.mem.zeroes([output_capacity]protocol.SourceStateOutput);
    var snapshot_items = std.mem.zeroes([output_capacity]protocol.ItemStateOutput);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_sampling_subscription_snapshot(
            handle,
            &snapshot_header,
            snapshot_sources[0..].ptr,
            snapshot_sources.len,
            snapshot_items[0..].ptr,
            snapshot_items.len,
        ),
    );
    try std.testing.expectEqual(@as(u32, 1), snapshot_header.active_source_count);
    try std.testing.expectEqual(@as(u32, 2), snapshot_header.known_item_count);
    try std.testing.expectEqual(@as(u64, 1_050), snapshot_items[0].last_sampled_milliseconds);
    try std.testing.expectEqual(@as(u64, 1_050), snapshot_items[1].last_sampled_milliseconds);

    var remove_input = protocol.RemoveInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.RemoveInput),
        .configuration_generation = config.generation,
        .operation_epoch = 6,
        .command_at_milliseconds = 1_100,
        .source_handle = 100,
        .valid_mask = protocol.RemoveValid.required,
        .flags = 0,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_sampling_subscription_remove(handle, &remove_input),
    );

    config.generation = 2;
    config.default_interval_milliseconds = 2_000;
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_sampling_subscription_reconfigure(handle, &config),
    );
    var reset_input = controlInput(7, 2_000, config.generation);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_sampling_subscription_reset(handle, &reset_input),
    );

    snapshot_header = std.mem.zeroes(protocol.SnapshotHeader);
    snapshot_header.abi_version = protocol.abi_version;
    snapshot_header.struct_size = @sizeOf(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_sampling_subscription_snapshot(
            handle,
            &snapshot_header,
            snapshot_sources[0..].ptr,
            snapshot_sources.len,
            snapshot_items[0..].ptr,
            snapshot_items.len,
        ),
    );
    try std.testing.expectEqual(config.generation, snapshot_header.configuration_generation);
    try std.testing.expectEqual(@as(u32, 0), snapshot_header.active_source_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot_header.known_item_count);
}

test "per-item fastest interval and independent due selection are authoritative" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 1, 0, 0, &.{ 10, 20 }));
    try std.testing.expectEqual(ResultCode.ok, track(session, 2, 2_000, 2_000, 1, 0, 0, &.{ 10, 20 }));
    try std.testing.expectEqual(ResultCode.ok, track(session, 3, 3_000, 3_000, 2, 0, 0, &.{10}));
    try std.testing.expectEqual(ResultCode.ok, track(session, 4, 3_500, 3_500, 2, 0, 0, &.{10}));

    const initial = runPlan(session, 5, 1, 3_500);
    try std.testing.expectEqual(ResultCode.ok, initial.code);
    try std.testing.expectEqual(@as(u32, 2), initial.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 10), initial.result.due[0].item_handle);
    try std.testing.expectEqual(@as(u64, 500), initial.result.due[0].effective_interval_milliseconds);
    try std.testing.expectEqual(@as(u64, 20), initial.result.due[1].item_handle);
    try std.testing.expectEqual(@as(u64, 1_000), initial.result.due[1].effective_interval_milliseconds);

    const first_completion = complete(session, 6, 1, 3_500, 3_500, .sampled, &.{ 10, 20 });
    try std.testing.expectEqual(ResultCode.ok, first_completion.code);
    try std.testing.expectEqual(@as(u64, 4_000), first_completion.output.next_wake_milliseconds);

    const next = runPlan(session, 7, 2, 4_000);
    try std.testing.expectEqual(ResultCode.ok, next.code);
    try std.testing.expectEqual(@as(u32, 2), next.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 10), next.result.due[0].item_handle);
    try std.testing.expectEqual(@as(u64, 20), next.result.due[1].item_handle);
    try std.testing.expectEqual(@as(u64, 4_000), next.result.header.next_wake_milliseconds);
}

test "persistent and explicit interval sources remain explicit until remove" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    const flags = protocol.SourceFlags.persistent | protocol.SourceFlags.explicit_interval;
    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 11, flags, 250, &.{42}));
    const first = runPlan(session, 2, 1, 1_000);
    try std.testing.expectEqual(ResultCode.ok, first.code);
    try std.testing.expectEqual(@as(u64, 250), first.result.due[0].effective_interval_milliseconds);
    try std.testing.expectEqual(flags, first.result.sources[0].flags);

    const sampled = complete(session, 3, 1, 1_000, 1_000, .sampled, &.{42});
    try std.testing.expectEqual(ResultCode.ok, sampled.code);
    const retained = runPlan(session, 4, 2, 10_000);
    try std.testing.expectEqual(ResultCode.ok, retained.code);
    try std.testing.expectEqual(@as(u32, 1), retained.result.header.active_source_count);
    try std.testing.expectEqual(@as(u32, 0), retained.result.header.expired_source_count);

    try std.testing.expectEqual(ResultCode.ok, remove(session, 5, 10_000, 11));
    const idle = runPlan(session, 6, 3, 10_000);
    try std.testing.expectEqual(ResultCode.ok, idle.code);
    try std.testing.expectEqual(@as(u32, 0), idle.result.header.active_source_count);
    try std.testing.expectEqual(@as(u64, 0), idle.result.header.next_wake_milliseconds);
}

test "transient expiry returns exact source and removes memberships" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 9, 0, 0, &.{77}));
    const expired = runPlan(session, 2, 1, 6_001);
    try std.testing.expectEqual(ResultCode.ok, expired.code);
    try std.testing.expectEqual(@as(u32, 1), expired.result.header.expired_source_count);
    try std.testing.expectEqual(@as(u64, 9), expired.result.expired[0].source_handle);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ExpiryReason.ttl_elapsed),
        expired.result.expired[0].reason,
    );
    try std.testing.expectEqual(@as(u32, 0), expired.result.header.active_source_count);
    try std.testing.expectEqual(@as(u32, 0), expired.result.header.active_item_count);
}

test "failed completion preserves last sampled and retries at effective interval" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 1, 0, 0, &.{10}));
    const first = runPlan(session, 2, 1, 1_000);
    try std.testing.expectEqual(ResultCode.ok, first.code);
    const failed = complete(session, 3, 1, 1_000, 1_000, .failed, &.{10});
    try std.testing.expectEqual(ResultCode.ok, failed.code);
    try std.testing.expectEqual(@as(u64, 2_000), failed.output.next_wake_milliseconds);

    const early = runPlan(session, 4, 2, 1_999);
    try std.testing.expectEqual(ResultCode.ok, early.code);
    try std.testing.expectEqual(@as(u32, 0), early.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 2_000), early.result.header.next_wake_milliseconds);

    const retry = runPlan(session, 5, 3, 2_000);
    try std.testing.expectEqual(ResultCode.ok, retry.code);
    try std.testing.expectEqual(@as(u32, 1), retry.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 0), retry.result.due[0].last_sampled_milliseconds);
}

test "skipped completion keeps the short contention retry gate" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 1, 0, 0, &.{10}));
    const first = runPlan(session, 2, 1, 1_000);
    try std.testing.expectEqual(ResultCode.ok, first.code);
    const skipped = complete(session, 3, 1, 1_000, 1_000, .skipped, &.{10});
    try std.testing.expectEqual(ResultCode.ok, skipped.code);
    try std.testing.expectEqual(@as(u64, 1_100), skipped.output.next_wake_milliseconds);
}

test "source update atomically replaces its item membership" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 1, 0, 0, &.{ 10, 20 }));
    try std.testing.expectEqual(ResultCode.ok, track(session, 2, 1_100, 1_100, 1, 0, 0, &.{30}));
    const plan = runPlan(session, 3, 1, 1_100);
    try std.testing.expectEqual(ResultCode.ok, plan.code);
    try std.testing.expectEqual(@as(u32, 1), plan.result.header.active_item_count);
    try std.testing.expectEqual(@as(u32, 1), plan.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 30), plan.result.due[0].item_handle);
    try std.testing.expectEqual(@as(u32, 1), plan.result.sources[0].item_count);

    const snapshot_result = runSnapshot(session);
    try std.testing.expectEqual(ResultCode.ok, snapshot_result.code);
    try std.testing.expectEqual(@as(u32, 3), snapshot_result.header.known_item_count);
    try std.testing.expectEqual(@as(u32, 1), snapshot_result.header.membership_count);
}

test "duplicate handles, time regression, future skew, and capacity fail closed" {
    var config = testConfig();
    config.maximum_source_count = 1;
    config.maximum_item_count = 2;
    config.maximum_membership_count = 2;
    config.maximum_due_item_count = 2;
    config.maximum_source_view_count = 1;
    config.maximum_expired_source_count = 1;
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(ResultCode.invalid_argument, track(session, 1, 1_000, 1_000, 1, 0, 0, &.{ 10, 10 }));
    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 1, 0, 0, &.{10}));
    try std.testing.expectEqual(ResultCode.stale_frame, track(session, 1, 1_001, 1_001, 1, 0, 0, &.{10}));
    try std.testing.expectEqual(ResultCode.stale_frame, track(session, 2, 999, 999, 1, 0, 0, &.{10}));
    try std.testing.expectEqual(ResultCode.stale_frame, track(session, 2, 1_100, 1_700, 1, 0, 0, &.{10}));
    try std.testing.expectEqual(ResultCode.out_of_memory, track(session, 2, 1_100, 1_100, 2, 0, 0, &.{20}));

    const snapshot_result = runSnapshotWithCapacities(session, 0, 2);
    try std.testing.expectEqual(ResultCode.buffer_too_small, snapshot_result.code);
}

test "plan output capacity is validated before expiry mutation" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 1, 0, 0, &.{10}));
    var header = planHeader(2, 1, 6_001, 1, output_capacity, output_capacity);
    var due = std.mem.zeroes([output_capacity]protocol.DueItemOutput);
    var sources = std.mem.zeroes([output_capacity]protocol.SourceViewOutput);
    var expired = std.mem.zeroes([output_capacity]protocol.ExpiredSourceOutput);
    const result = session_module.plan(
        session,
        &header,
        &due,
        1,
        &sources,
        output_capacity,
        &expired,
        output_capacity,
    );
    try std.testing.expectEqual(ResultCode.buffer_too_small, result);

    const snapshot_result = runSnapshot(session);
    try std.testing.expectEqual(ResultCode.ok, snapshot_result.code);
    try std.testing.expectEqual(@as(u32, 1), snapshot_result.header.active_source_count);
}

test "reset preserves anti-replay epoch and reconfigure rejects capacity drift" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 1, 0, 0, &.{10}));
    var control = controlInput(2, 1_100, config.generation);
    try std.testing.expectEqual(ResultCode.ok, session.reset(&control));
    try std.testing.expectEqual(ResultCode.stale_frame, track(session, 2, 1_100, 1_100, 1, 0, 0, &.{10}));

    var next_config = config;
    next_config.generation += 1;
    next_config.maximum_item_count += 1;
    next_config.maximum_due_item_count += 1;
    try std.testing.expectEqual(ResultCode.invalid_argument, session.reconfigure(&next_config));
    next_config.maximum_item_count = config.maximum_item_count;
    next_config.maximum_due_item_count = config.maximum_due_item_count;
    next_config.default_interval_milliseconds = 2_000;
    try std.testing.expectEqual(ResultCode.ok, session.reconfigure(&next_config));
}

test "unknown fields, empty items, and malformed item rows fail without mutation" {
    var invalid_config = testConfig();
    invalid_config.flags = 1;
    try std.testing.expectError(error.InvalidConfiguration, session_module.Session.create(&invalid_config));

    const config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(ResultCode.invalid_argument, track(session, 1, 1_000, 1_000, 1, 0, 0, &.{}));
    try std.testing.expectEqual(ResultCode.abi_mismatch, track(session, 1, 1_000, 1_000, 1, 1 << 7, 0, &.{10}));
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        track(session, 1, 1_000, 1_000, 1, protocol.SourceFlags.explicit_interval, 99, &.{10}),
    );

    var input = trackInput(1, 1_000, 1_000, 1, 0, 0, 1);
    var reference = itemReference(10);
    reference.reserved[0] = 1;
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        session_module.track(session, &input, @ptrCast(&reference), 1),
    );

    const snapshot_result = runSnapshot(session);
    try std.testing.expectEqual(ResultCode.ok, snapshot_result.code);
    try std.testing.expectEqual(@as(u32, 0), snapshot_result.header.active_source_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot_result.header.known_item_count);
}

test "completion binds to the latest exact plan and rejects duplicate item handles" {
    const config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 1, 0, 0, &.{10}));
    const first = runPlan(session, 2, 1, 1_000);
    try std.testing.expectEqual(ResultCode.ok, first.code);
    const second = runPlan(session, 3, 2, 1_000);
    try std.testing.expectEqual(ResultCode.ok, second.code);
    try std.testing.expectEqual(ResultCode.stale_frame, complete(session, 4, 1, 1_000, 1_000, .sampled, &.{10}).code);
    try std.testing.expectEqual(ResultCode.invalid_argument, complete(session, 4, 2, 1_000, 1_000, .sampled, &.{ 10, 10 }).code);
    try std.testing.expectEqual(ResultCode.ok, complete(session, 4, 2, 1_000, 1_000, .sampled, &.{10}).code);
}

test "timing reconfigure recalculates native next wake" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 1, 0, 0, &.{10}));
    const plan = runPlan(session, 2, 1, 1_000);
    try std.testing.expectEqual(ResultCode.ok, plan.code);
    try std.testing.expectEqual(ResultCode.ok, complete(session, 3, 1, 1_000, 1_000, .sampled, &.{10}).code);

    config.generation = 2;
    config.default_interval_milliseconds = 2_000;
    try std.testing.expectEqual(ResultCode.ok, session.reconfigure(&config));
    const snapshot_result = runSnapshot(session);
    try std.testing.expectEqual(ResultCode.ok, snapshot_result.code);
    try std.testing.expectEqual(@as(u64, 2), snapshot_result.header.configuration_generation);
    try std.testing.expectEqual(@as(u64, 2_000), snapshot_result.header.next_wake_milliseconds);
}

test "fixed slot indices reuse tombstones at full source and membership capacity" {
    const stress_capacity = 64;
    var config = testConfig();
    config.maximum_source_count = stress_capacity;
    config.maximum_item_count = stress_capacity;
    config.maximum_membership_count = stress_capacity * 4;
    config.maximum_due_item_count = stress_capacity;
    config.maximum_source_view_count = stress_capacity;
    config.maximum_expired_source_count = stress_capacity;
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    var operation_epoch: u64 = 1;
    var source_index: u64 = 0;
    while (source_index < stress_capacity) : (source_index += 1) {
        const item_base = source_index % stress_capacity;
        try std.testing.expectEqual(
            ResultCode.ok,
            track(
                session,
                operation_epoch,
                1_000,
                1_000,
                1_000 + source_index,
                0,
                0,
                &.{
                    1 + item_base,
                    1 + ((item_base + 1) % stress_capacity),
                    1 + ((item_base + 2) % stress_capacity),
                    1 + ((item_base + 3) % stress_capacity),
                },
            ),
        );
        operation_epoch += 1;
    }

    var due = std.mem.zeroes([stress_capacity]protocol.DueItemOutput);
    var sources = std.mem.zeroes([stress_capacity]protocol.SourceViewOutput);
    var expired = std.mem.zeroes([stress_capacity]protocol.ExpiredSourceOutput);
    var header = planHeader(
        operation_epoch,
        1,
        1_000,
        stress_capacity,
        stress_capacity,
        stress_capacity,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(
            session,
            &header,
            &due,
            stress_capacity,
            &sources,
            stress_capacity,
            &expired,
            stress_capacity,
        ),
    );
    try std.testing.expectEqual(@as(u32, stress_capacity), header.due_item_count);
    try std.testing.expectEqual(@as(u32, stress_capacity), header.active_source_count);
    operation_epoch += 1;

    source_index = 0;
    while (source_index < stress_capacity) : (source_index += 2) {
        try std.testing.expectEqual(
            ResultCode.ok,
            remove(session, operation_epoch, 1_100, 1_000 + source_index),
        );
        operation_epoch += 1;
    }
    source_index = 0;
    while (source_index < stress_capacity) : (source_index += 2) {
        const item_base = source_index % stress_capacity;
        try std.testing.expectEqual(
            ResultCode.ok,
            track(
                session,
                operation_epoch,
                1_100,
                1_100,
                2_000 + source_index,
                0,
                0,
                &.{
                    1 + item_base,
                    1 + ((item_base + 1) % stress_capacity),
                    1 + ((item_base + 2) % stress_capacity),
                    1 + ((item_base + 3) % stress_capacity),
                },
            ),
        );
        operation_epoch += 1;
    }

    var snapshot_header = std.mem.zeroes(protocol.SnapshotHeader);
    snapshot_header.abi_version = protocol.abi_version;
    snapshot_header.struct_size = @sizeOf(protocol.SnapshotHeader);
    var snapshot_sources = std.mem.zeroes([stress_capacity]protocol.SourceStateOutput);
    var snapshot_items = std.mem.zeroes([stress_capacity]protocol.ItemStateOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.snapshot(
            session,
            &snapshot_header,
            &snapshot_sources,
            stress_capacity,
            &snapshot_items,
            stress_capacity,
        ),
    );
    try std.testing.expectEqual(@as(u32, stress_capacity), snapshot_header.active_source_count);
    try std.testing.expectEqual(@as(u32, stress_capacity), snapshot_header.known_item_count);
    try std.testing.expectEqual(@as(u32, stress_capacity * 4), snapshot_header.membership_count);

    header = planHeader(
        operation_epoch,
        2,
        7_001,
        stress_capacity,
        stress_capacity,
        stress_capacity,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(
            session,
            &header,
            &due,
            stress_capacity,
            &sources,
            stress_capacity,
            &expired,
            stress_capacity,
        ),
    );
    try std.testing.expectEqual(@as(u32, stress_capacity), header.expired_source_count);
    try std.testing.expectEqual(@as(u32, 0), header.active_source_count);

    snapshot_header = std.mem.zeroes(protocol.SnapshotHeader);
    snapshot_header.abi_version = protocol.abi_version;
    snapshot_header.struct_size = @sizeOf(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.snapshot(
            session,
            &snapshot_header,
            &snapshot_sources,
            stress_capacity,
            &snapshot_items,
            stress_capacity,
        ),
    );
    try std.testing.expectEqual(@as(u32, 0), snapshot_header.membership_count);
}

test "cold items share the current boundary while retaining independent handles" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(
        ResultCode.ok,
        track(session, 1, 1_000, 1_000, 100, 0, 0, &.{ 10, 20, 30, 40 }),
    );
    const plan = runPlan(session, 2, 1, 1_000);
    try std.testing.expectEqual(ResultCode.ok, plan.code);
    try std.testing.expectEqual(@as(u32, 4), plan.result.header.due_item_count);
    for (plan.result.due[0..4], [_]u64{ 10, 20, 30, 40 }) |due, expected_handle| {
        try std.testing.expectEqual(expected_handle, due.item_handle);
        try std.testing.expectEqual(@as(u32, 0), due.flags);
        try std.testing.expectEqual(@as(u64, 1_000), due.scheduled_due_milliseconds);
    }
    try std.testing.expectEqual(
        ResultCode.ok,
        complete(session, 3, 1, 1_000, 1_000, .sampled, &.{ 10, 20, 30, 40 }).code,
    );
}

test "equal frequency subscriptions converge on one shared time boundary" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 100, 0, 0, &.{10}));
    const first = runPlan(session, 2, 1, 1_000);
    try std.testing.expectEqual(ResultCode.ok, first.code);
    try std.testing.expectEqual(ResultCode.ok, complete(session, 3, 1, 1_000, 1_101, .sampled, &.{10}).code);

    try std.testing.expectEqual(ResultCode.ok, track(session, 4, 1_500, 1_500, 200, 0, 0, &.{20}));
    const second = runPlan(session, 5, 2, 1_500);
    try std.testing.expectEqual(ResultCode.ok, second.code);
    try std.testing.expectEqual(@as(u32, 1), second.result.header.due_item_count);
    try std.testing.expectEqual(ResultCode.ok, complete(session, 6, 2, 1_500, 1_701, .sampled, &.{20}).code);

    const early = runPlan(session, 7, 3, 1_999);
    try std.testing.expectEqual(ResultCode.ok, early.code);
    try std.testing.expectEqual(@as(u32, 0), early.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 2_000), early.result.header.next_wake_milliseconds);
    const aligned = runPlan(session, 8, 4, 2_000);
    try std.testing.expectEqual(ResultCode.ok, aligned.code);
    try std.testing.expectEqual(@as(u32, 2), aligned.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 2_000), aligned.result.due[0].scheduled_due_milliseconds);
    try std.testing.expectEqual(@as(u64, 10), aligned.result.due[0].item_handle);
    try std.testing.expectEqual(@as(u64, 2_000), aligned.result.due[1].scheduled_due_milliseconds);
    try std.testing.expectEqual(@as(u64, 20), aligned.result.due[1].item_handle);
    try std.testing.expectEqual(
        ResultCode.ok,
        complete(session, 9, 4, 2_000, 2_000, .sampled, &.{ 10, 20 }).code,
    );
}

test "a new faster subscription advances the item without delaying an overdue item" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 100, 0, 0, &.{10}));
    _ = runPlan(session, 2, 1, 1_000);
    try std.testing.expectEqual(ResultCode.ok, complete(session, 3, 1, 1_000, 1_000, .sampled, &.{10}).code);
    const explicit = protocol.SourceFlags.persistent | protocol.SourceFlags.explicit_interval;
    try std.testing.expectEqual(ResultCode.ok, track(session, 4, 1_200, 1_200, 200, explicit, 250, &.{10}));

    const early = runPlan(session, 5, 2, 1_249);
    try std.testing.expectEqual(ResultCode.ok, early.code);
    try std.testing.expectEqual(@as(u32, 0), early.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 1_250), early.result.header.next_wake_milliseconds);
    const due = runPlan(session, 6, 3, 1_250);
    try std.testing.expectEqual(ResultCode.ok, due.code);
    try std.testing.expectEqual(@as(u32, 1), due.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 250), due.result.due[0].effective_interval_milliseconds);
}

test "removing a faster source never postpones an already due item" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    const explicit = protocol.SourceFlags.persistent | protocol.SourceFlags.explicit_interval;
    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 100, explicit, 1_000, &.{10}));
    try std.testing.expectEqual(ResultCode.ok, track(session, 2, 1_000, 1_000, 200, explicit, 250, &.{10}));
    const initial = runPlan(session, 3, 1, 1_000);
    try std.testing.expectEqual(@as(u32, 1), initial.result.header.due_item_count);
    try std.testing.expectEqual(ResultCode.ok, complete(session, 4, 1, 1_000, 1_000, .sampled, &.{10}).code);

    try std.testing.expectEqual(ResultCode.ok, remove(session, 5, 1_300, 200));
    const overdue = runPlan(session, 6, 2, 1_300);
    try std.testing.expectEqual(ResultCode.ok, overdue.code);
    try std.testing.expectEqual(@as(u32, 1), overdue.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 1_250), overdue.result.due[0].scheduled_due_milliseconds);
    try std.testing.expectEqual(@as(u64, 1_000), overdue.result.due[0].effective_interval_milliseconds);
}

test "removing a faster source realigns future work to the remaining absolute boundary" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    const explicit = protocol.SourceFlags.persistent | protocol.SourceFlags.explicit_interval;
    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 100, explicit, 1_000, &.{10}));
    try std.testing.expectEqual(ResultCode.ok, track(session, 2, 1_000, 1_000, 200, explicit, 250, &.{10}));
    _ = runPlan(session, 3, 1, 1_000);
    try std.testing.expectEqual(ResultCode.ok, complete(session, 4, 1, 1_000, 1_000, .sampled, &.{10}).code);

    try std.testing.expectEqual(ResultCode.ok, remove(session, 5, 1_100, 200));
    const early = runPlan(session, 6, 2, 1_999);
    try std.testing.expectEqual(ResultCode.ok, early.code);
    try std.testing.expectEqual(@as(u32, 0), early.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 2_000), early.result.header.next_wake_milliseconds);
    const due = runPlan(session, 7, 3, 2_000);
    try std.testing.expectEqual(@as(u32, 1), due.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 2_000), due.result.due[0].scheduled_due_milliseconds);
}

test "failed retry rebases when a faster source joins" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    const explicit = protocol.SourceFlags.persistent | protocol.SourceFlags.explicit_interval;
    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 100, explicit, 5_000, &.{10}));
    _ = runPlan(session, 2, 1, 1_000);
    try std.testing.expectEqual(ResultCode.ok, complete(session, 3, 1, 1_000, 1_100, .failed, &.{10}).code);
    try std.testing.expectEqual(ResultCode.ok, track(session, 4, 1_200, 1_200, 200, explicit, 1_000, &.{10}));

    const early = runPlan(session, 5, 2, 1_999);
    try std.testing.expectEqual(@as(u32, 0), early.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 2_000), early.result.header.next_wake_milliseconds);
    const due = runPlan(session, 6, 3, 2_000);
    try std.testing.expectEqual(@as(u32, 1), due.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 2_000), due.result.due[0].retry_at_milliseconds);
}

test "failed completion after final source removal does not poison resubscription" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();

    const explicit = protocol.SourceFlags.persistent | protocol.SourceFlags.explicit_interval;
    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 100, explicit, 1_000, &.{10}));
    _ = runPlan(session, 2, 1, 1_000);
    try std.testing.expectEqual(ResultCode.ok, remove(session, 3, 1_100, 100));
    try std.testing.expectEqual(ResultCode.ok, complete(session, 4, 1, 1_100, 1_100, .failed, &.{10}).code);
    try std.testing.expectEqual(ResultCode.ok, track(session, 5, 1_200, 1_200, 200, explicit, 1_000, &.{10}));

    const due = runPlan(session, 6, 2, 1_200);
    try std.testing.expectEqual(ResultCode.ok, due.code);
    try std.testing.expectEqual(@as(u32, 1), due.result.header.due_item_count);
    try std.testing.expectEqual(@as(u64, 0), due.result.due[0].retry_at_milliseconds);
}

test "retired dispatch fields fail closed" {
    var config = testConfig();
    config.flags = 1;
    try std.testing.expectError(error.InvalidConfiguration, session_module.Session.create(&config));
    config = testConfig();
    config.reserved_u64_0 = 1;
    try std.testing.expectError(error.InvalidConfiguration, session_module.Session.create(&config));

    config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 100, 0, 0, &.{10}));
    var header = planHeader(2, 1, 1_000, output_capacity, output_capacity, output_capacity);
    header.request_flags = 1;
    var due = std.mem.zeroes([output_capacity]protocol.DueItemOutput);
    var sources = std.mem.zeroes([output_capacity]protocol.SourceViewOutput);
    var expired = std.mem.zeroes([output_capacity]protocol.ExpiredSourceOutput);
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        session_module.plan(session, &header, &due, output_capacity, &sources, output_capacity, &expired, output_capacity),
    );
}

test "reconfigure realigns future work to the new default interval" {
    var config = testConfig();
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    try std.testing.expectEqual(ResultCode.ok, track(session, 1, 1_000, 1_000, 100, 0, 0, &.{10}));
    _ = runPlan(session, 2, 1, 1_000);
    try std.testing.expectEqual(ResultCode.ok, complete(session, 3, 1, 1_000, 1_000, .sampled, &.{10}).code);

    config.generation = 2;
    config.default_interval_milliseconds = 500;
    try std.testing.expectEqual(ResultCode.ok, session.reconfigure(&config));
    const snapshot = runSnapshot(session);
    try std.testing.expectEqual(ResultCode.ok, snapshot.code);
    try std.testing.expectEqual(@as(u64, 1_500), snapshot.items[0].scheduled_due_milliseconds);
}

const PlanCall = struct {
    code: ResultCode,
    result: PlanResult,
};

fn runPlan(session: *session_module.Session, operation_epoch: u64, plan_epoch: u64, now: u64) PlanCall {
    var result = PlanResult{
        .header = planHeader(operation_epoch, plan_epoch, now, output_capacity, output_capacity, output_capacity),
        .due = std.mem.zeroes([output_capacity]protocol.DueItemOutput),
        .sources = std.mem.zeroes([output_capacity]protocol.SourceViewOutput),
        .expired = std.mem.zeroes([output_capacity]protocol.ExpiredSourceOutput),
    };
    const code = session_module.plan(
        session,
        &result.header,
        &result.due,
        output_capacity,
        &result.sources,
        output_capacity,
        &result.expired,
        output_capacity,
    );
    return .{ .code = code, .result = result };
}

fn planHeader(
    operation_epoch: u64,
    plan_epoch: u64,
    now: u64,
    due_capacity: u32,
    source_capacity: u32,
    expired_capacity: u32,
) protocol.PlanHeader {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PlanHeader),
        .configuration_generation = 1,
        .operation_epoch = operation_epoch,
        .plan_epoch = plan_epoch,
        .command_at_milliseconds = now,
        .valid_mask = protocol.PlanValid.required,
        .due_item_capacity = due_capacity,
        .source_view_capacity = source_capacity,
        .expired_source_capacity = expired_capacity,
        .due_item_count = 0,
        .source_view_count = 0,
        .expired_source_count = 0,
        .active_source_count = 0,
        .active_item_count = 0,
        .next_wake_milliseconds = 0,
        .state_revision = 0,
        .flags = 0,
        .request_flags = 0,
        .reserved = 0,
    };
}

const CompleteCall = struct {
    code: ResultCode,
    output: protocol.CompletionOutput,
};

fn complete(
    session: *session_module.Session,
    operation_epoch: u64,
    plan_epoch: u64,
    command_at: u64,
    completed_at: u64,
    status: protocol.CompletionStatus,
    handles: []const u64,
) CompleteCall {
    var references = std.mem.zeroes([output_capacity]protocol.ItemReference);
    for (handles, 0..) |handle, index| references[index] = itemReference(handle);
    var input = protocol.CompletionInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CompletionInput),
        .configuration_generation = 1,
        .operation_epoch = operation_epoch,
        .plan_epoch = plan_epoch,
        .command_at_milliseconds = command_at,
        .completed_at_milliseconds = completed_at,
        .valid_mask = protocol.CompletionValid.required,
        .item_count = @intCast(handles.len),
        .status = @intFromEnum(status),
        .flags = 0,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
    var output = std.mem.zeroes(protocol.CompletionOutput);
    output.abi_version = protocol.abi_version;
    output.struct_size = @sizeOf(protocol.CompletionOutput);
    const code = session_module.complete(session, &input, &references, @intCast(handles.len), &output);
    return .{ .code = code, .output = output };
}

fn track(
    session: *session_module.Session,
    operation_epoch: u64,
    command_at: u64,
    observed_at: u64,
    source_handle: u64,
    flags: u32,
    explicit_interval: u64,
    handles: []const u64,
) ResultCode {
    var references = std.mem.zeroes([output_capacity]protocol.ItemReference);
    for (handles, 0..) |handle, index| references[index] = itemReference(handle);
    var input = trackInput(
        operation_epoch,
        command_at,
        observed_at,
        source_handle,
        flags,
        explicit_interval,
        @intCast(handles.len),
    );
    return session_module.track(session, &input, &references, @intCast(handles.len));
}

fn trackInput(
    operation_epoch: u64,
    command_at: u64,
    observed_at: u64,
    source_handle: u64,
    flags: u32,
    explicit_interval: u64,
    item_count: u32,
) protocol.TrackInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.TrackInput),
        .configuration_generation = 1,
        .operation_epoch = operation_epoch,
        .command_at_milliseconds = command_at,
        .observed_at_milliseconds = observed_at,
        .source_handle = source_handle,
        .explicit_interval_milliseconds = explicit_interval,
        .valid_mask = protocol.TrackValid.required |
            (if (flags & protocol.SourceFlags.explicit_interval != 0)
                protocol.TrackValid.explicit_interval
            else
                0),
        .item_count = item_count,
        .flags = flags,
        .reserved = .{ 0, 0, 0 },
    };
}

fn remove(session: *session_module.Session, operation_epoch: u64, command_at: u64, source_handle: u64) ResultCode {
    var input = protocol.RemoveInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.RemoveInput),
        .configuration_generation = 1,
        .operation_epoch = operation_epoch,
        .command_at_milliseconds = command_at,
        .source_handle = source_handle,
        .valid_mask = protocol.RemoveValid.required,
        .flags = 0,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
    return session_module.remove(session, &input);
}

fn itemReference(handle: u64) protocol.ItemReference {
    return .{
        .struct_size = @sizeOf(protocol.ItemReference),
        .flags = 0,
        .item_handle = handle,
        .reserved = .{ 0, 0 },
    };
}

fn controlInput(operation_epoch: u64, command_at: u64, generation: u64) protocol.ControlInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ControlInput),
        .configuration_generation = generation,
        .operation_epoch = operation_epoch,
        .command_at_milliseconds = command_at,
        .valid_mask = protocol.ControlValid.required,
        .flags = 0,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
}

const SnapshotCall = struct {
    code: ResultCode,
    header: protocol.SnapshotHeader,
    sources: [output_capacity]protocol.SourceStateOutput,
    items: [output_capacity]protocol.ItemStateOutput,
};

fn runSnapshot(session: *session_module.Session) SnapshotCall {
    return runSnapshotWithCapacities(session, output_capacity, output_capacity);
}

fn runSnapshotWithCapacities(session: *session_module.Session, source_capacity: u32, item_capacity: u32) SnapshotCall {
    var result = SnapshotCall{
        .code = .ok,
        .header = std.mem.zeroes(protocol.SnapshotHeader),
        .sources = std.mem.zeroes([output_capacity]protocol.SourceStateOutput),
        .items = std.mem.zeroes([output_capacity]protocol.ItemStateOutput),
    };
    result.header.abi_version = protocol.abi_version;
    result.header.struct_size = @sizeOf(protocol.SnapshotHeader);
    result.code = session_module.snapshot(
        session,
        &result.header,
        &result.sources,
        source_capacity,
        &result.items,
        item_capacity,
    );
    return result;
}

fn testConfig() protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = 1,
        .maximum_source_count = output_capacity,
        .maximum_item_count = output_capacity,
        .maximum_membership_count = output_capacity * 2,
        .maximum_due_item_count = output_capacity,
        .maximum_source_view_count = output_capacity,
        .maximum_expired_source_count = output_capacity,
        .default_interval_milliseconds = 1_000,
        .minimum_interval_milliseconds = 100,
        .active_ttl_milliseconds = 5_000,
        .maximum_future_skew_milliseconds = 500,
        .flags = 0,
        .reserved_u64_0 = 0,
        .reserved_u64_1 = 0,
        .reserved_u64_2 = 0,
        .reserved_u32_0 = 0,
        .reserved_u32_1 = 0,
    };
}

fn lessThanU64(_: void, left: u64, right: u64) bool {
    return left < right;
}
