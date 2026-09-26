const std = @import("std");
const types = @import("types.zig");
const wire = @import("layout.zig");
const state = @import("state.zig");
const resources = @import("resource_ops.zig");
const subscriptions = @import("subscription_ops.zig");
const tasks = @import("task_queue.zig");
const actions = @import("action_gate.zig");
const public_manager = @import("public_manager.zig");

const Fixture = struct {
    memory: []align(8) u8,
    context: *state.Context,

    fn create(resource_capacity: u32, subscription_capacity: u32, task_capacity: u32) !Fixture {
        var config = baseConfig(resource_capacity, subscription_capacity, task_capacity);
        return createConfigured(&config);
    }

    fn createConfigured(config: *types.LedgerConfig) !Fixture {
        const computed = try wire.calculate(config);
        const memory = try std.testing.allocator.alignedAlloc(u8, .of(u64), computed.required_size);
        errdefer std.testing.allocator.free(memory);
        const context = try state.Context.create(memory.ptr, memory.len, null, config);
        return .{ .memory = memory, .context = context };
    }

    fn deinit(self: *Fixture) void {
        self.context.destroy();
        std.testing.allocator.free(self.memory);
    }

    fn publish(self: *Fixture, max_parallel: u16) !types.ResourceRef {
        var publication = basePublication(max_parallel);
        var output = std.mem.zeroes(types.ResourceRef);
        try resources.publish(self.context, &publication, &output);
        return output;
    }
};

fn baseConfig(resource_capacity: u32, subscription_capacity: u32, task_capacity: u32) types.LedgerConfig {
    var config = std.mem.zeroes(types.LedgerConfig);
    config.abi_version = types.abi_version;
    config.struct_size = @sizeOf(types.LedgerConfig);
    config.ledger_instance_id = 1001;
    config.owner_application_key = 2002;
    config.owner_process_created_utc_ticks = 3003;
    config.subscription_coefficient = 0.25;
    config.resource_capacity = resource_capacity;
    config.subscription_capacity = subscription_capacity;
    config.task_capacity = task_capacity;
    config.owner_process_id = 44;
    config.synchronization_kind = @intFromEnum(types.SynchronizationKind.process_local);
    config.lease_clock_domain =
        @intFromEnum(types.LeaseClockDomain.windows_performance_counter);
    config.lease_clock_frequency_hz = state.leaseClockFrequency() catch unreachable;
    config.maximum_subscription_ttl = 10_000;
    config.maximum_queue_ttl = 10_000;
    config.maximum_grant_ttl = 10_000;
    return config;
}

fn basePublication(max_parallel: u16) types.ResourcePublication {
    var publication = std.mem.zeroes(types.ResourcePublication);
    publication.abi_version = types.abi_version;
    publication.struct_size = @sizeOf(types.ResourcePublication);
    publication.owner_application_key = 2002;
    publication.owner_instance_id = 3003;
    publication.owner_instance_id_high = 3004;
    publication.owner_context_generation = 1;
    publication.lease_generation = 2;
    publication.binding_generation = 3;
    publication.capability_generation = 4;
    publication.executor_id_low = 5005;
    publication.executor_id_high = 5006;
    publication.owner_process_id = 44;
    publication.resource_key = 55;
    publication.resource_id = 66;
    publication.size_bytes = 4096;
    publication.payload_mapping_id = 77;
    publication.payload_generation = 1;
    publication.last_updated_utc_ticks = 88;
    publication.max_parallel_grants = max_parallel;
    publication.tier = 1;
    publication.resource_kind = 3;
    publication.recovery_kind = 0;
    publication.granularity = 1;
    publication.action_route = 1;
    publication.availability = @intFromEnum(types.Availability.available);
    return publication;
}

fn useRequest(resource: types.ResourceRef, key: u64, score: f64, instance: u64) types.UseRequest {
    var request = std.mem.zeroes(types.UseRequest);
    request.abi_version = types.abi_version;
    request.struct_size = @sizeOf(types.UseRequest);
    request.resource = resource;
    request.request_key = key;
    request.requester_application_key = instance + 100;
    request.requester_instance_id = instance;
    request.requester_session_id = instance + 200;
    request.score_plan_generation = 1;
    request.queue_lease_duration = 10_000;
    request.base_score = score;
    request.requester_process_id = @intCast(instance);
    return request;
}

fn subscribeForUse(
    context: *state.Context,
    request: *const types.UseRequest,
    deadline_timestamp: u64,
) !types.SubscriptionReceipt {
    var subscription = std.mem.zeroes(types.SubscriptionRequest);
    subscription.abi_version = types.abi_version;
    subscription.struct_size = @sizeOf(types.SubscriptionRequest);
    subscription.resource = request.resource;
    subscription.subscriber_application_key = request.requester_application_key;
    subscription.subscriber_instance_id = request.requester_instance_id;
    subscription.subscriber_session_id = request.requester_session_id;
    subscription.lease_duration = deadline_timestamp;
    subscription.subscriber_process_id = request.requester_process_id;
    subscription.intent_mask = 0b0010;
    var receipt = std.mem.zeroes(types.SubscriptionReceipt);
    try subscriptions.subscribe(context, &subscription, &receipt);
    return receipt;
}

fn publicManagerRequest(sample_generation: u64, memory_shortage: bool) public_manager.PlanRequest {
    return .{
        .abi_version = types.abi_version,
        .struct_size = @sizeOf(public_manager.PlanRequest),
        .sample_generation = sample_generation,
        .memory_shortage = @intFromBool(memory_shortage),
        .video_memory_shortage = 0,
        .reserved0 = [_]u8{0} ** 6,
        .subscriber_weight = 400,
        .activity_weight = 600,
        .weight_scale = 1000,
        .subscriber_half_saturation = 4,
        .activity_half_saturation = 8,
        .reserved1 = 0,
    };
}

test "lease config requires the published clock domain frequency and bounded TTLs" {
    var config = baseConfig(2, 2, 2);
    try std.testing.expect(types.validConfig(&config));

    config.lease_clock_domain = 0;
    try std.testing.expect(!types.validConfig(&config));
    config = baseConfig(2, 2, 2);
    config.lease_clock_frequency_hz = 0;
    try std.testing.expect(!types.validConfig(&config));
    config = baseConfig(2, 2, 2);
    config.maximum_subscription_ttl = 0;
    try std.testing.expect(!types.validConfig(&config));
    config = baseConfig(2, 2, 2);
    config.maximum_queue_ttl = 0;
    try std.testing.expect(!types.validConfig(&config));
    config = baseConfig(2, 2, 2);
    config.maximum_grant_ttl = 0;
    try std.testing.expect(!types.validConfig(&config));
}

test "public queue orders base score descending and preserves FIFO ties" {
    var fixture = try Fixture.create(1, 4, 8);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    var requests = [_]types.UseRequest{
        useRequest(resource, 1, 10, 1),
        useRequest(resource, 2, 80, 2),
        useRequest(resource, 3, 80, 3),
        useRequest(resource, 4, 40, 4),
    };
    var receipts: [4]types.TaskReceipt = undefined;
    for (&requests, 0..) |*request, index| {
        _ = try subscribeForUse(fixture.context, request, 10_000);
        try tasks.enqueue(fixture.context, request, 1, &receipts[index]);
    }

    const expected = [_]u64{ 2, 3, 4, 1 };
    for (expected) |request_key| {
        var grant = std.mem.zeroes(types.GrantReceipt);
        try tasks.reserveNext(fixture.context, resource, 1, 100, &grant);
        try std.testing.expectEqual(request_key, grant.task.request_key);
        var active = std.mem.zeroes(types.GrantReceipt);
        try tasks.beginGrantedUse(fixture.context, &grant, 2, &active);
        try tasks.completeGrantedUse(fixture.context, &active);
    }
}

test "public use requires a matching live subscription and retains it through queued work" {
    var fixture = try Fixture.create(1, 1, 2);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    var request = useRequest(resource, 1, 10, 1);
    var task = std.mem.zeroes(types.TaskReceipt);
    try std.testing.expectError(
        error.AccessDenied,
        tasks.enqueue(fixture.context, &request, 1, &task),
    );

    const subscription = try subscribeForUse(fixture.context, &request, 100);
    try tasks.enqueue(fixture.context, &request, 1, &task);
    try std.testing.expectError(
        error.ResourceBusy,
        subscriptions.release(fixture.context, &subscription),
    );
    var expired_count: u32 = 0;
    try subscriptions.sweep(fixture.context, 100, &expired_count);
    try std.testing.expectEqual(@as(u32, 0), expired_count);

    try tasks.cancel(fixture.context, &task);
    try subscriptions.release(fixture.context, &subscription);
    var snapshot = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &snapshot);
    try std.testing.expectEqual(@as(u32, 0), snapshot.active_subscriber_count);
}

test "subscription scheduling facts invalidate stale destructive plans" {
    var fixture = try Fixture.create(1, 2, 2);
    defer fixture.deinit();
    const resource = try fixture.publish(1);

    var initial = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &initial);
    const initial_topology = fixture.context.header.topology_generation;
    var stale_expected = destructiveExpected(
        resource,
        initial.scheduling_revision,
        7001,
        7002,
    );

    var use = useRequest(resource, 1, 10, 1);
    var receipt = try subscribeForUse(fixture.context, &use, 100);
    var subscribed = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &subscribed);
    try std.testing.expectEqual(initial.scheduling_revision + 1, subscribed.scheduling_revision);
    try std.testing.expectEqual(initial_topology + 1, fixture.context.header.topology_generation);
    var token = std.mem.zeroes(types.DestructiveToken);
    try std.testing.expectError(
        error.StaleReference,
        actions.beginDestructiveExpected(fixture.context, &stale_expected, &token),
    );

    var same_intent = std.mem.zeroes(types.SubscriptionReceipt);
    try subscriptions.confirm(fixture.context, &receipt, 200, 0b0010, &same_intent);
    var confirmed = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &confirmed);
    try std.testing.expectEqual(subscribed.scheduling_revision, confirmed.scheduling_revision);
    try std.testing.expectEqual(initial_topology + 1, fixture.context.header.topology_generation);

    var changed_intent = std.mem.zeroes(types.SubscriptionReceipt);
    try subscriptions.confirm(fixture.context, &same_intent, 300, 0b0100, &changed_intent);
    var changed = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &changed);
    try std.testing.expectEqual(confirmed.scheduling_revision + 1, changed.scheduling_revision);
    try std.testing.expectEqual(initial_topology + 2, fixture.context.header.topology_generation);

    stale_expected = destructiveExpected(resource, changed.scheduling_revision, 8001, 8002);
    receipt = changed_intent;
    try subscriptions.release(fixture.context, &receipt);
    var released = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &released);
    try std.testing.expectEqual(changed.scheduling_revision + 1, released.scheduling_revision);
    try std.testing.expectEqual(initial_topology + 3, fixture.context.header.topology_generation);
    try std.testing.expectError(
        error.StaleReference,
        actions.beginDestructiveExpected(fixture.context, &stale_expected, &token),
    );
}

test "subscription sweep invalidates each affected resource once" {
    var fixture = try Fixture.create(2, 4, 2);
    defer fixture.deinit();
    const first = try fixture.publish(1);
    const second = try fixture.publish(1);
    var requests = [_]types.UseRequest{
        useRequest(first, 1, 10, 1),
        useRequest(first, 2, 10, 2),
        useRequest(second, 3, 10, 3),
        useRequest(second, 4, 10, 4),
    };
    for (&requests) |*request| {
        _ = try subscribeForUse(fixture.context, request, 100);
    }

    var first_before = std.mem.zeroes(types.ResourceSnapshot);
    var second_before = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, first, &first_before);
    try resources.snapshotOne(fixture.context, second, &second_before);
    const topology_before = fixture.context.header.topology_generation;

    var expired_count: u32 = 0;
    try subscriptions.sweep(fixture.context, 100, &expired_count);
    try std.testing.expectEqual(@as(u32, 4), expired_count);
    var first_after = std.mem.zeroes(types.ResourceSnapshot);
    var second_after = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, first, &first_after);
    try resources.snapshotOne(fixture.context, second, &second_after);
    try std.testing.expectEqual(first_before.scheduling_revision + 1, first_after.scheduling_revision);
    try std.testing.expectEqual(second_before.scheduling_revision + 1, second_after.scheduling_revision);
    try std.testing.expectEqual(topology_before + 1, fixture.context.header.topology_generation);
    try std.testing.expectEqual(@as(u32, 0), first_after.active_subscriber_count);
    try std.testing.expectEqual(@as(u32, 0), second_after.active_subscriber_count);
}

test "publisher cannot inject activity and successful active use records it" {
    var fixture = try Fixture.create(1, 1, 2);
    defer fixture.deinit();
    var invalid = basePublication(1);
    invalid.activity_score = 1;
    var resource = std.mem.zeroes(types.ResourceRef);
    try std.testing.expectError(
        error.InvalidArgument,
        resources.publish(fixture.context, &invalid, &resource),
    );

    resource = try fixture.publish(1);
    var request = useRequest(resource, 1, 10, 1);
    _ = try subscribeForUse(fixture.context, &request, 100);
    var task = std.mem.zeroes(types.TaskReceipt);
    try tasks.enqueue(fixture.context, &request, 1, &task);
    var grant = std.mem.zeroes(types.GrantReceipt);
    try tasks.reserveNext(fixture.context, resource, 1, 100, &grant);
    var before = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &before);
    try std.testing.expectEqual(@as(u8, 0), before.activity_score);

    var active = std.mem.zeroes(types.GrantReceipt);
    try tasks.beginGrantedUse(fixture.context, &grant, 2, &active);
    var after = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &after);
    try std.testing.expectEqual(@as(u8, 32), after.activity_score);
    try std.testing.expectEqual(before.scheduling_revision + 1, after.scheduling_revision);
}

test "public manager ranks only by subscriptions and automatic activity" {
    var fixture = try Fixture.create(3, 2, 4);
    defer fixture.deinit();
    const zero = try fixture.publish(1);
    const subscribed = try fixture.publish(1);
    const active_resource = try fixture.publish(1);

    var subscribed_use = useRequest(subscribed, 1, 10, 1);
    _ = try subscribeForUse(fixture.context, &subscribed_use, 10_000);
    var active_use = useRequest(active_resource, 2, 10, 2);
    _ = try subscribeForUse(fixture.context, &active_use, 10_000);
    var task = std.mem.zeroes(types.TaskReceipt);
    try tasks.enqueue(fixture.context, &active_use, 1, &task);
    var grant = std.mem.zeroes(types.GrantReceipt);
    try tasks.reserveNext(fixture.context, active_resource, 1, 100, &grant);
    var active = std.mem.zeroes(types.GrantReceipt);
    try tasks.beginGrantedUse(fixture.context, &grant, 2, &active);
    try tasks.completeGrantedUse(fixture.context, &active);

    var candidates: [3]public_manager.Candidate = undefined;
    var summary = std.mem.zeroes(public_manager.PlanSummary);
    var request = publicManagerRequest(1, false);
    try public_manager.plan(fixture.context, &request, &candidates, candidates.len, &summary);
    try std.testing.expectEqual(@as(u32, 1), summary.candidate_count);
    try std.testing.expectEqual(zero.public_resource_id, candidates[0].resource.public_resource_id);
    try std.testing.expectEqual(
        @intFromEnum(public_manager.UnloadReason.zero_subscribers),
        candidates[0].reason,
    );

    request = publicManagerRequest(2, true);
    try public_manager.plan(fixture.context, &request, &candidates, candidates.len, &summary);
    try std.testing.expectEqual(@as(u32, 3), summary.candidate_count);
    try std.testing.expectEqual(zero.public_resource_id, candidates[0].resource.public_resource_id);
    try std.testing.expectEqual(subscribed.public_resource_id, candidates[1].resource.public_resource_id);
    try std.testing.expectEqual(active_resource.public_resource_id, candidates[2].resource.public_resource_id);
    try std.testing.expectEqual(@as(u8, 28), candidates[2].activity_score);
    try std.testing.expect(candidates[1].retention_score_q16 < candidates[2].retention_score_q16);

    var protected_task = std.mem.zeroes(types.TaskReceipt);
    try tasks.enqueue(fixture.context, &subscribed_use, 2, &protected_task);
    request = publicManagerRequest(3, true);
    try public_manager.plan(fixture.context, &request, &candidates, candidates.len, &summary);
    try std.testing.expectEqual(@as(u32, 1), summary.protected_resource_count);
    try std.testing.expectEqual(@as(u32, 2), summary.candidate_count);
    try std.testing.expectEqual(zero.public_resource_id, candidates[0].resource.public_resource_id);
    try std.testing.expectEqual(active_resource.public_resource_id, candidates[1].resource.public_resource_id);
}

test "public manager keeps memory and video-memory shortage domains separate" {
    var fixture = try Fixture.create(2, 2, 2);
    defer fixture.deinit();
    var memory_publication = basePublication(1);
    memory_publication.resource_key = 101;
    memory_publication.resource_id = 11;
    var memory_resource = std.mem.zeroes(types.ResourceRef);
    try resources.publish(fixture.context, &memory_publication, &memory_resource);
    var gpu_publication = basePublication(1);
    gpu_publication.resource_key = 102;
    gpu_publication.resource_id = 12;
    gpu_publication.adapter_key = 202;
    gpu_publication.flags = types.gpu_backed_flag;
    var gpu_resource = std.mem.zeroes(types.ResourceRef);
    try resources.publish(fixture.context, &gpu_publication, &gpu_resource);
    const memory_use = useRequest(memory_resource, 1, 10, 1);
    _ = try subscribeForUse(fixture.context, &memory_use, 10_000);
    const gpu_use = useRequest(gpu_resource, 2, 10, 2);
    _ = try subscribeForUse(fixture.context, &gpu_use, 10_000);

    var candidates: [2]public_manager.Candidate = undefined;
    var summary = std.mem.zeroes(public_manager.PlanSummary);
    var request = publicManagerRequest(1, true);
    try public_manager.plan(fixture.context, &request, &candidates, candidates.len, &summary);
    try std.testing.expectEqual(@as(u32, 1), summary.candidate_count);
    try std.testing.expectEqual(memory_resource.public_resource_id, candidates[0].resource.public_resource_id);

    request = publicManagerRequest(2, false);
    request.video_memory_shortage = 1;
    try public_manager.plan(fixture.context, &request, &candidates, candidates.len, &summary);
    try std.testing.expectEqual(@as(u32, 1), summary.candidate_count);
    try std.testing.expectEqual(gpu_resource.public_resource_id, candidates[0].resource.public_resource_id);
}

test "gpu-backed publication requires an exact adapter identity" {
    var publication = basePublication(1);
    publication.flags = types.gpu_backed_flag;
    try std.testing.expect(!types.validPublication(&publication));

    publication.adapter_key = 202;
    try std.testing.expect(types.validPublication(&publication));

    publication.flags = 0;
    try std.testing.expect(!types.validPublication(&publication));
}

test "public manager quarantines zero subscriber resources with protected work" {
    var fixture = try Fixture.create(1, 1, 1);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    var request = useRequest(resource, 1, 10, 1);
    _ = try subscribeForUse(fixture.context, &request, 10_000);
    var task = std.mem.zeroes(types.TaskReceipt);
    try tasks.enqueue(fixture.context, &request, 1, &task);
    fixture.context.column(
        u32,
        fixture.context.layout.resources.active_subscriber_count,
    )[resource.resource_slot] = 0;

    var candidates: [1]public_manager.Candidate = undefined;
    var summary = std.mem.zeroes(public_manager.PlanSummary);
    const plan_request = publicManagerRequest(1, false);
    try std.testing.expectError(
        error.InconsistentState,
        public_manager.plan(
            fixture.context,
            &plan_request,
            &candidates,
            candidates.len,
            &summary,
        ),
    );
    try std.testing.expectEqual(
        types.ConsistencyState.quarantined,
        fixture.context.consistencyState(),
    );
}

test "receipts cannot cross ledger incarnation or public resource identity" {
    var config_a = baseConfig(1, 1, 2);
    config_a.ledger_instance_id = 0xA001;
    var config_b = config_a;
    config_b.ledger_instance_id = 0xB001;
    var ledger_a = try Fixture.createConfigured(&config_a);
    defer ledger_a.deinit();
    var ledger_b = try Fixture.createConfigured(&config_b);
    defer ledger_b.deinit();

    var publication_a = basePublication(2);
    publication_a.public_resource_id = 0xA101;
    var publication_b = publication_a;
    publication_b.public_resource_id = 0xB101;
    var resource_a = std.mem.zeroes(types.ResourceRef);
    var resource_b = std.mem.zeroes(types.ResourceRef);
    try resources.publish(ledger_a.context, &publication_a, &resource_a);
    try resources.publish(ledger_b.context, &publication_b, &resource_b);

    var request_a = std.mem.zeroes(types.SubscriptionRequest);
    request_a.abi_version = types.abi_version;
    request_a.struct_size = @sizeOf(types.SubscriptionRequest);
    request_a.resource = resource_a;
    request_a.subscriber_application_key = 122;
    request_a.subscriber_instance_id = 22;
    request_a.subscriber_session_id = 222;
    request_a.lease_duration = 100;
    request_a.subscriber_process_id = 22;
    request_a.intent_mask = 0b0010;
    var request_b = request_a;
    request_b.resource = resource_b;
    var subscription_a = std.mem.zeroes(types.SubscriptionReceipt);
    var subscription_b = std.mem.zeroes(types.SubscriptionReceipt);
    try subscriptions.subscribe(ledger_a.context, &request_a, &subscription_a);
    try subscriptions.subscribe(ledger_b.context, &request_b, &subscription_b);
    var before = try std.testing.allocator.dupe(u8, ledger_b.memory);
    defer std.testing.allocator.free(before);
    try std.testing.expectError(
        error.StaleReference,
        subscriptions.release(ledger_b.context, &subscription_a),
    );
    try std.testing.expectEqualSlices(u8, before, ledger_b.memory);

    var use_a = useRequest(resource_a, 21, 50, 22);
    var use_b = use_a;
    use_b.resource = resource_b;
    var task_a = std.mem.zeroes(types.TaskReceipt);
    var task_b = std.mem.zeroes(types.TaskReceipt);
    try tasks.enqueue(ledger_a.context, &use_a, 1, &task_a);
    try tasks.enqueue(ledger_b.context, &use_b, 1, &task_b);
    std.testing.allocator.free(before);
    before = try std.testing.allocator.dupe(u8, ledger_b.memory);
    try std.testing.expectError(error.StaleReference, tasks.cancel(ledger_b.context, &task_a));
    try std.testing.expectEqualSlices(u8, before, ledger_b.memory);
}

test "subscription capacity failure preserves the complete mapping and output" {
    var fixture = try Fixture.create(1, 1, 1);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    var first = std.mem.zeroes(types.SubscriptionRequest);
    first.abi_version = types.abi_version;
    first.struct_size = @sizeOf(types.SubscriptionRequest);
    first.resource = resource;
    first.subscriber_application_key = 1;
    first.subscriber_instance_id = 2;
    first.subscriber_session_id = 3;
    first.lease_duration = 100;
    first.subscriber_process_id = 4;
    first.intent_mask = 0b0010;
    var receipt = std.mem.zeroes(types.SubscriptionReceipt);
    try subscriptions.subscribe(fixture.context, &first, &receipt);

    var second = first;
    second.subscriber_instance_id = 5;
    second.subscriber_session_id = 6;
    var output: types.SubscriptionReceipt = undefined;
    @memset(std.mem.asBytes(&output), 0xA5);
    const expected_output = output;
    const before = try std.testing.allocator.dupe(u8, fixture.memory);
    defer std.testing.allocator.free(before);
    try std.testing.expectError(
        error.CapacityExhausted,
        subscriptions.subscribe(fixture.context, &second, &output),
    );
    try std.testing.expectEqualSlices(u8, before, fixture.memory);
    try std.testing.expectEqualSlices(
        u8,
        std.mem.asBytes(&expected_output),
        std.mem.asBytes(&output),
    );
}

test "queue count inconsistency is rejected before cancel or reserve mutation" {
    var cancel_fixture = try Fixture.create(1, 1, 1);
    defer cancel_fixture.deinit();
    const cancel_resource = try cancel_fixture.publish(1);
    var cancel_request = useRequest(cancel_resource, 1, 10, 1);
    _ = try subscribeForUse(cancel_fixture.context, &cancel_request, 10_000);
    var cancel_receipt = std.mem.zeroes(types.TaskReceipt);
    try tasks.enqueue(cancel_fixture.context, &cancel_request, 1, &cancel_receipt);
    cancel_fixture.context.column(
        u32,
        cancel_fixture.context.layout.resources.queued_request_count,
    )[cancel_resource.resource_slot] = 0;
    const cancel_before = try std.testing.allocator.dupe(u8, cancel_fixture.memory);
    defer std.testing.allocator.free(cancel_before);
    try std.testing.expectError(
        error.InconsistentState,
        tasks.cancel(cancel_fixture.context, &cancel_receipt),
    );
    try std.testing.expectEqualSlices(u8, cancel_before, cancel_fixture.memory);

    var reserve_fixture = try Fixture.create(1, 1, 1);
    defer reserve_fixture.deinit();
    const reserve_resource = try reserve_fixture.publish(1);
    var reserve_request = useRequest(reserve_resource, 2, 10, 2);
    _ = try subscribeForUse(reserve_fixture.context, &reserve_request, 10_000);
    var reserve_receipt = std.mem.zeroes(types.TaskReceipt);
    try tasks.enqueue(reserve_fixture.context, &reserve_request, 1, &reserve_receipt);
    reserve_fixture.context.column(
        u32,
        reserve_fixture.context.layout.resources.queued_request_count,
    )[reserve_resource.resource_slot] = 0;
    const reserve_before = try std.testing.allocator.dupe(u8, reserve_fixture.memory);
    defer std.testing.allocator.free(reserve_before);
    var grant = std.mem.zeroes(types.GrantReceipt);
    try std.testing.expectError(
        error.InconsistentState,
        tasks.reserveNext(reserve_fixture.context, reserve_resource, 1, 100, &grant),
    );
    try std.testing.expectEqualSlices(u8, reserve_before, reserve_fixture.memory);
}

test "queued reserved and active transitions never open the destructive gate" {
    var fixture = try Fixture.create(1, 1, 4);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    var request = useRequest(resource, 1, 10, 1);
    _ = try subscribeForUse(fixture.context, &request, 10_000);
    var task = std.mem.zeroes(types.TaskReceipt);
    try tasks.enqueue(fixture.context, &request, 1, &task);
    var token = std.mem.zeroes(types.DestructiveToken);
    var expected = destructiveExpected(resource, 2, 7001, 7002);
    try std.testing.expectError(error.ResourceBusy, actions.beginDestructiveExpected(fixture.context, &expected, &token));

    var grant = std.mem.zeroes(types.GrantReceipt);
    try tasks.reserveNext(fixture.context, resource, 1, 100, &grant);
    try std.testing.expectError(error.ResourceBusy, actions.beginDestructiveExpected(fixture.context, &expected, &token));
    var active = std.mem.zeroes(types.GrantReceipt);
    try tasks.beginGrantedUse(fixture.context, &grant, 2, &active);
    expected.scheduling_revision = 3;
    try std.testing.expectError(error.ResourceBusy, actions.beginDestructiveExpected(fixture.context, &expected, &token));
    try tasks.completeGrantedUse(fixture.context, &active);
    try actions.beginDestructiveExpected(fixture.context, &expected, &token);
    var no_effect = destructiveNoEffect(token, 8001, 8002);
    try actions.abortDestructive(fixture.context, &token, &no_effect);
}

test "subscription is independent from use protection" {
    var fixture = try Fixture.create(1, 2, 2);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    var request = std.mem.zeroes(types.SubscriptionRequest);
    request.abi_version = types.abi_version;
    request.struct_size = @sizeOf(types.SubscriptionRequest);
    request.resource = resource;
    request.subscriber_application_key = 9;
    request.subscriber_instance_id = 10;
    request.subscriber_session_id = 11;
    request.lease_duration = 100;
    request.subscriber_process_id = 12;
    request.intent_mask = 0b0010;
    var receipt = std.mem.zeroes(types.SubscriptionReceipt);
    try subscriptions.subscribe(fixture.context, &request, &receipt);

    var snapshot = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &snapshot);
    try std.testing.expectEqual(@as(u32, 1), snapshot.active_subscriber_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.active_use_count);
    try std.testing.expectEqual(@as(u8, types.all_action_bits), snapshot.allowed_actions);
}

test "destructive commit requires the exact active token and explicit post state" {
    var fixture = try Fixture.create(1, 0, 2);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    var expected = destructiveExpected(resource, 1, 7001, 7002);
    var token = std.mem.zeroes(types.DestructiveToken);
    try actions.beginDestructiveExpected(fixture.context, &expected, &token);

    var active_snapshot = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &active_snapshot);
    try std.testing.expectEqual(@as(u8, 1), active_snapshot.destructive_active);
    try std.testing.expectEqual(@as(u8, 0), active_snapshot.allowed_actions & types.destructive_action_bits);

    var wrong_attempt = token;
    wrong_attempt.action_attempt_id_low += 1;
    var post_state = basePublication(1);
    post_state.size_bytes = 2048;
    try std.testing.expectError(
        error.StaleReference,
        actions.commitDestructive(fixture.context, &wrong_attempt, &post_state),
    );
    var still_active = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &still_active);
    try std.testing.expectEqual(@as(u8, 1), still_active.destructive_active);

    var wrong_action = token;
    wrong_action.action_mask = 2;
    try std.testing.expectError(
        error.StaleReference,
        actions.commitDestructive(fixture.context, &wrong_action, &post_state),
    );
    try actions.commitDestructive(fixture.context, &token, &post_state);

    var committed = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &committed);
    try std.testing.expectEqual(@as(u64, 2), committed.scheduling_revision);
    try std.testing.expectEqual(@as(u64, 2048), committed.size_bytes);
    try std.testing.expectEqual(@as(u8, 0), committed.destructive_active);
    try std.testing.expectError(
        error.StaleReference,
        actions.commitDestructive(fixture.context, &token, &post_state),
    );
}

test "destructive abort requires typed no-effect proof and preserves revision" {
    var fixture = try Fixture.create(1, 0, 2);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    var stale_expected = destructiveExpected(resource, 2, 7001, 7002);
    var token = std.mem.zeroes(types.DestructiveToken);
    try std.testing.expectError(
        error.StaleReference,
        actions.beginDestructiveExpected(fixture.context, &stale_expected, &token),
    );

    var expected = destructiveExpected(resource, 1, 7001, 7002);
    try actions.beginDestructiveExpected(fixture.context, &expected, &token);
    var wrong_receipt = destructiveNoEffect(token, 8001, 8002);
    wrong_receipt.action_attempt_id_high += 1;
    try std.testing.expectError(
        error.InvalidArgument,
        actions.abortDestructive(fixture.context, &token, &wrong_receipt),
    );
    var still_active = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &still_active);
    try std.testing.expectEqual(@as(u8, 1), still_active.destructive_active);

    var receipt = destructiveNoEffect(token, 8001, 8002);
    try actions.abortDestructive(fixture.context, &token, &receipt);
    var aborted = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &aborted);
    try std.testing.expectEqual(@as(u64, 1), aborted.scheduling_revision);
    try std.testing.expectEqual(@as(u8, 0), aborted.destructive_active);
    try std.testing.expectError(
        error.StaleReference,
        actions.abortDestructive(fixture.context, &token, &receipt),
    );
}

test "destructive gate capacity is preflighted before invoking an effect" {
    var fixture = try Fixture.create(1, 0, 2);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    const epochs = fixture.context.column(u64, fixture.context.layout.resources.gate_epoch);
    epochs[resource.resource_slot] = std.math.maxInt(u64) - 1;

    var expected = destructiveExpected(resource, 1, 7001, 7002);
    var token = std.mem.zeroes(types.DestructiveToken);
    var before = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &before);
    try std.testing.expectError(
        error.CapacityExhausted,
        actions.beginDestructiveExpected(fixture.context, &expected, &token),
    );
    var after = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &after);
    try std.testing.expectEqualDeep(before, after);
}

test "recall transaction preserves identity and rejects stale subscriber facts" {
    var fixture = try Fixture.create(1, 2, 2);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    var first_use = useRequest(resource, 1, 10, 1);
    _ = try subscribeForUse(fixture.context, &first_use, 10_000);

    var unload_expected = destructiveExpected(resource, 2, 7001, 7002);
    var unload_token = std.mem.zeroes(types.DestructiveToken);
    try actions.beginDestructiveExpected(fixture.context, &unload_expected, &unload_token);
    var unavailable = basePublication(1);
    unavailable.public_resource_id = resource.public_resource_id;
    unavailable.size_bytes = 0;
    unavailable.payload_mapping_id = 0;
    unavailable.payload_generation = 2;
    unavailable.availability = @intFromEnum(types.Availability.unavailable);
    try actions.commitDestructive(fixture.context, &unload_token, &unavailable);

    var unloaded = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &unloaded);
    var expected = recallExpected(unloaded, 8001, 8002);
    var second_use = useRequest(resource, 2, 10, 2);
    _ = try subscribeForUse(fixture.context, &second_use, 10_000);
    var token = std.mem.zeroes(types.RecallToken);
    try std.testing.expectError(
        error.StaleReference,
        actions.beginRecallExpected(fixture.context, &expected, &token),
    );

    try resources.snapshotOne(fixture.context, resource, &unloaded);
    expected = recallExpected(unloaded, 8001, 8002);
    try actions.beginRecallExpected(fixture.context, &expected, &token);
    var preparing = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &preparing);
    try std.testing.expectEqual(resource, preparing.id);
    try std.testing.expectEqual(@intFromEnum(types.Availability.preparing), preparing.availability);
    try std.testing.expectEqual(@as(u8, 1), preparing.destructive_active);
    var task = std.mem.zeroes(types.TaskReceipt);
    try std.testing.expectError(
        error.ResourceBusy,
        tasks.enqueue(fixture.context, &first_use, 1, &task),
    );

    var restored = basePublication(1);
    restored.public_resource_id = resource.public_resource_id;
    restored.payload_mapping_id = 99;
    restored.payload_generation = 4;
    try std.testing.expectError(
        error.InvalidArgument,
        actions.commitRecall(fixture.context, &token, &restored),
    );
    restored.payload_generation = 3;
    try actions.commitRecall(fixture.context, &token, &restored);

    var available = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &available);
    try std.testing.expectEqual(resource, available.id);
    try std.testing.expectEqual(@intFromEnum(types.Availability.available), available.availability);
    try std.testing.expectEqual(@as(u64, 4096), available.size_bytes);
    try std.testing.expectEqual(@as(u64, 3), available.payload_generation);
    try std.testing.expectEqual(@as(u8, 0), available.destructive_active);
    try std.testing.expectError(
        error.StaleReference,
        actions.commitRecall(fixture.context, &token, &restored),
    );
}

test "recall abort is typed no-effect and generation exhaustion is preflighted" {
    var fixture = try Fixture.create(1, 1, 2);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    var use = useRequest(resource, 1, 10, 1);
    _ = try subscribeForUse(fixture.context, &use, 10_000);

    var unload_expected = destructiveExpected(resource, 2, 7001, 7002);
    var unload_token = std.mem.zeroes(types.DestructiveToken);
    try actions.beginDestructiveExpected(fixture.context, &unload_expected, &unload_token);
    var unavailable = basePublication(1);
    unavailable.public_resource_id = resource.public_resource_id;
    unavailable.size_bytes = 0;
    unavailable.payload_mapping_id = 0;
    unavailable.payload_generation = 2;
    unavailable.availability = @intFromEnum(types.Availability.unavailable);
    try actions.commitDestructive(fixture.context, &unload_token, &unavailable);

    var snapshot = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &snapshot);
    var expected = recallExpected(snapshot, 8001, 8002);
    var token = std.mem.zeroes(types.RecallToken);
    try actions.beginRecallExpected(fixture.context, &expected, &token);
    var receipt = recallNoEffect(token, 9001, 9002);
    try actions.abortRecall(fixture.context, &token, &receipt);
    try resources.snapshotOne(fixture.context, resource, &snapshot);
    try std.testing.expectEqual(@intFromEnum(types.Availability.unavailable), snapshot.availability);
    try std.testing.expectEqual(@as(u64, 2), snapshot.payload_generation);
    try std.testing.expectEqual(@as(u8, 0), snapshot.destructive_active);

    fixture.context.column(
        u64,
        fixture.context.layout.resources.payload_generation,
    )[resource.resource_slot] = std.math.maxInt(u64);
    try resources.snapshotOne(fixture.context, resource, &snapshot);
    expected = recallExpected(snapshot, 8003, 8004);
    const before = try std.testing.allocator.dupe(u8, fixture.memory);
    defer std.testing.allocator.free(before);
    try std.testing.expectError(
        error.CapacityExhausted,
        actions.beginRecallExpected(fixture.context, &expected, &token),
    );
    try std.testing.expectEqualSlices(u8, before, fixture.memory);
}

test "task gate exhaustion rejects before queue facts change" {
    var fixture = try Fixture.create(1, 1, 2);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    fixture.context.column(
        u64,
        fixture.context.layout.resources.gate_epoch,
    )[resource.resource_slot] = std.math.maxInt(u64);
    var request = useRequest(resource, 1, 10, 1);
    _ = try subscribeForUse(fixture.context, &request, 10_000);
    var receipt = std.mem.zeroes(types.TaskReceipt);
    try std.testing.expectError(
        error.CapacityExhausted,
        tasks.enqueue(fixture.context, &request, 1, &receipt),
    );
    var snapshot = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &snapshot);
    try std.testing.expectEqual(@as(u32, 0), snapshot.queued_request_count);
    try std.testing.expectEqual(std.math.maxInt(u64), snapshot.gate_epoch);
    try std.testing.expectEqualDeep(std.mem.zeroes(types.TaskReceipt), receipt);
}

test "topology generation permits its last value then rejects without publishing" {
    var fixture = try Fixture.create(3, 0, 2);
    defer fixture.deinit();
    _ = try fixture.publish(1);
    fixture.context.header.topology_generation = std.math.maxInt(u64) - 1;
    _ = try fixture.publish(1);
    try std.testing.expectEqual(
        std.math.maxInt(u64),
        fixture.context.header.topology_generation,
    );

    var publication = basePublication(1);
    publication.resource_key += 1;
    publication.resource_id += 1;
    var output = std.mem.zeroes(types.ResourceRef);
    try std.testing.expectError(
        error.CapacityExhausted,
        resources.publish(fixture.context, &publication, &output),
    );
    var snapshots: [3]types.ResourceSnapshot = undefined;
    var count: u32 = 0;
    var topology_generation: u64 = 0;
    try resources.snapshotBatch(
        fixture.context,
        snapshots[0..].ptr,
        snapshots.len,
        &count,
        &topology_generation,
    );
    try std.testing.expectEqual(@as(u32, 2), count);
    try std.testing.expectEqual(std.math.maxInt(u64), topology_generation);
}

test "resource fact update advances topology and exhaustion preserves prior snapshot" {
    var fixture = try Fixture.create(1, 0, 2);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    const initial_topology = fixture.context.header.topology_generation;

    var publication = basePublication(1);
    publication.size_bytes = 8192;
    try resources.update(fixture.context, resource, &publication);
    try std.testing.expectEqual(
        initial_topology + 1,
        fixture.context.header.topology_generation,
    );
    var updated = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &updated);
    try std.testing.expectEqual(@as(u64, 8192), updated.size_bytes);
    try std.testing.expectEqual(@as(u64, 2), updated.scheduling_revision);

    fixture.context.header.topology_generation = std.math.maxInt(u64);
    const header_before = fixture.context.header.*;
    const snapshot_before = updated;
    publication.size_bytes = 16384;
    try std.testing.expectError(
        error.CapacityExhausted,
        resources.update(fixture.context, resource, &publication),
    );
    var after = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &after);
    try std.testing.expectEqualDeep(header_before, fixture.context.header.*);
    try std.testing.expectEqualDeep(snapshot_before, after);
}

test "ordinary resource update cannot rebind GPU adapter identity" {
    var fixture = try Fixture.create(1, 0, 2);
    defer fixture.deinit();
    var publication = basePublication(1);
    publication.adapter_key = 101;
    publication.flags = types.gpu_backed_flag;
    var resource = std.mem.zeroes(types.ResourceRef);
    try resources.publish(fixture.context, &publication, &resource);

    var rebound = publication;
    rebound.adapter_key = 202;
    try std.testing.expectError(
        error.Conflict,
        resources.update(fixture.context, resource, &rebound),
    );

    var crossed_domain = publication;
    crossed_domain.adapter_key = 0;
    crossed_domain.flags = 0;
    try std.testing.expectError(
        error.Conflict,
        resources.update(fixture.context, resource, &crossed_domain),
    );

    var snapshot = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &snapshot);
    try std.testing.expectEqual(@as(u64, 101), snapshot.adapter_key);
    try std.testing.expect(snapshot.flags & types.gpu_backed_flag != 0);
}

test "ordinary resource update cannot cross unload or recall lifecycle states" {
    var fixture = try Fixture.create(1, 0, 2);
    defer fixture.deinit();
    const resource = try fixture.publish(1);

    var before = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &before);
    var unavailable = basePublication(1);
    unavailable.public_resource_id = resource.public_resource_id;
    unavailable.size_bytes = 0;
    unavailable.payload_mapping_id = 0;
    unavailable.payload_generation = 2;
    unavailable.availability = @intFromEnum(types.Availability.unavailable);
    try std.testing.expectError(
        error.Conflict,
        resources.update(fixture.context, resource, &unavailable),
    );
    var after_rejected_unload = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &after_rejected_unload);
    try std.testing.expectEqualDeep(before, after_rejected_unload);

    var expected = destructiveExpected(resource, 1, 7001, 7002);
    var token = std.mem.zeroes(types.DestructiveToken);
    try actions.beginDestructiveExpected(fixture.context, &expected, &token);
    try actions.commitDestructive(fixture.context, &token, &unavailable);
    var tombstone = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &tombstone);

    var restored = basePublication(1);
    restored.public_resource_id = resource.public_resource_id;
    restored.payload_generation = 3;
    try std.testing.expectError(
        error.Conflict,
        resources.update(fixture.context, resource, &restored),
    );
    var after_rejected_recall = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &after_rejected_recall);
    try std.testing.expectEqualDeep(tombstone, after_rejected_recall);
}

test "full-capacity auto-id publish leaves header free list and output unchanged" {
    var fixture = try Fixture.create(1, 0, 2);
    defer fixture.deinit();
    _ = try fixture.publish(1);
    const header_before = fixture.context.header.*;
    const free_head_before = fixture.context.header.resource_free_head;
    var publication = basePublication(1);
    publication.public_resource_id = 0;
    var output = types.ResourceRef{
        .ledger_instance_id = 11,
        .resource_generation = 12,
        .public_resource_id = 13,
        .resource_slot = 14,
        .reserved = 15,
    };
    const output_before = output;

    try std.testing.expectError(
        error.CapacityExhausted,
        resources.publish(fixture.context, &publication, &output),
    );
    try std.testing.expectEqualDeep(header_before, fixture.context.header.*);
    try std.testing.expectEqual(free_head_before, fixture.context.header.resource_free_head);
    try std.testing.expectEqualDeep(output_before, output);
}

test "mapping and mutation sequences never wrap" {
    var fixture = try Fixture.create(1, 0, 2);
    defer fixture.deinit();
    _ = try fixture.publish(1);

    fixture.context.header.mapping_epoch = std.math.maxInt(u64) - 1;
    try std.testing.expect(fixture.context.recoverAbandonedLocked());
    try std.testing.expectEqual(
        std.math.maxInt(u64),
        fixture.context.header.mapping_epoch,
    );
    try std.testing.expect(!fixture.context.recoverAbandonedLocked());
    try std.testing.expectEqual(
        std.math.maxInt(u64),
        fixture.context.header.mapping_epoch,
    );
    try std.testing.expectEqual(
        types.ConsistencyState.quarantined,
        fixture.context.consistencyState(),
    );

    fixture.context.header.consistency_state =
        @intFromEnum(types.ConsistencyState.stable);
    fixture.context.header.mutation_sequence = std.math.maxInt(u64) - 3;
    var last = try fixture.context.beginMutation();
    last.finish();
    try std.testing.expectEqual(
        std.math.maxInt(u64) - 1,
        fixture.context.header.mutation_sequence,
    );
    try std.testing.expectError(
        error.CapacityExhausted,
        fixture.context.beginMutation(),
    );
    try std.testing.expectEqual(
        std.math.maxInt(u64) - 1,
        fixture.context.header.mutation_sequence,
    );
}

test "zero change maintenance does not publish a ledger mutation" {
    var fixture = try Fixture.create(1, 1, 2);
    defer fixture.deinit();
    const resource = try fixture.publish(1);

    var subscription_request = std.mem.zeroes(types.SubscriptionRequest);
    subscription_request.abi_version = types.abi_version;
    subscription_request.struct_size = @sizeOf(types.SubscriptionRequest);
    subscription_request.resource = resource;
    subscription_request.subscriber_application_key = 9;
    subscription_request.subscriber_instance_id = 10;
    subscription_request.subscriber_session_id = 11;
    subscription_request.lease_duration = 10_000;
    subscription_request.subscriber_process_id = 12;
    subscription_request.intent_mask = 2;
    var subscription = std.mem.zeroes(types.SubscriptionReceipt);
    try subscriptions.subscribe(fixture.context, &subscription_request, &subscription);

    var task_request = useRequest(resource, 1, 10, 1);
    task_request.requester_application_key = subscription_request.subscriber_application_key;
    task_request.requester_instance_id = subscription_request.subscriber_instance_id;
    task_request.requester_session_id = subscription_request.subscriber_session_id;
    task_request.requester_process_id = subscription_request.subscriber_process_id;
    var task = std.mem.zeroes(types.TaskReceipt);
    try tasks.enqueue(fixture.context, &task_request, 1, &task);

    const sequence_before = fixture.context.header.mutation_sequence;
    var expired_count: u32 = std.math.maxInt(u32);
    try subscriptions.sweep(fixture.context, 100, &expired_count);
    try std.testing.expectEqual(@as(u32, 0), expired_count);
    try std.testing.expectEqual(sequence_before, fixture.context.header.mutation_sequence);

    var removed_count: u32 = std.math.maxInt(u32);
    try tasks.sweep(fixture.context, 100, &removed_count);
    try std.testing.expectEqual(@as(u32, 0), removed_count);
    try std.testing.expectEqual(sequence_before, fixture.context.header.mutation_sequence);
}

test "task sweep batches gate advancement once per affected resource" {
    var fixture = try Fixture.create(2, 4, 4);
    defer fixture.deinit();
    const first = try fixture.publish(1);
    const second = try fixture.publish(1);

    var requests = [_]types.UseRequest{
        useRequest(first, 1, 10, 1),
        useRequest(first, 2, 9, 2),
        useRequest(second, 3, 8, 3),
        useRequest(second, 4, 7, 4),
    };
    for (&requests) |*request| {
        _ = try subscribeForUse(fixture.context, request, 20_000);
        var receipt = std.mem.zeroes(types.TaskReceipt);
        try tasks.enqueue(fixture.context, request, 1, &receipt);
    }

    const gate_epochs = fixture.context.column(u64, fixture.context.layout.resources.gate_epoch);
    const first_gate_before = gate_epochs[first.resource_slot];
    const second_gate_before = gate_epochs[second.resource_slot];
    var removed_count: u32 = 0;
    try tasks.sweep(fixture.context, 10_000, &removed_count);

    try std.testing.expectEqual(@as(u32, 4), removed_count);
    try std.testing.expectEqual(first_gate_before + 1, gate_epochs[first.resource_slot]);
    try std.testing.expectEqual(second_gate_before + 1, gate_epochs[second.resource_slot]);
    const queued_counts =
        fixture.context.column(u32, fixture.context.layout.resources.queued_request_count);
    try std.testing.expectEqual(@as(u32, 0), queued_counts[first.resource_slot]);
    try std.testing.expectEqual(@as(u32, 0), queued_counts[second.resource_slot]);
}

fn destructiveExpected(
    resource: types.ResourceRef,
    scheduling_revision: u64,
    attempt_low: u64,
    attempt_high: u64,
) types.DestructiveExpectedRequest {
    var request = std.mem.zeroes(types.DestructiveExpectedRequest);
    request.abi_version = types.abi_version;
    request.struct_size = @sizeOf(types.DestructiveExpectedRequest);
    request.resource = resource;
    request.scheduling_revision = scheduling_revision;
    request.action_attempt_id_low = attempt_low;
    request.action_attempt_id_high = attempt_high;
    request.action_mask = 1;
    return request;
}

fn destructiveNoEffect(
    token: types.DestructiveToken,
    receipt_low: u64,
    receipt_high: u64,
) types.DestructiveNoEffectReceipt {
    var receipt = std.mem.zeroes(types.DestructiveNoEffectReceipt);
    receipt.abi_version = types.abi_version;
    receipt.struct_size = @sizeOf(types.DestructiveNoEffectReceipt);
    receipt.action_attempt_id_low = token.action_attempt_id_low;
    receipt.action_attempt_id_high = token.action_attempt_id_high;
    receipt.receipt_id_low = receipt_low;
    receipt.receipt_id_high = receipt_high;
    receipt.observed_monotonic_timestamp = 1;
    receipt.proof_mask = types.no_effect_proof_not_invoked;
    receipt.reason = @intFromEnum(types.NoEffectReason.executor_rejected_before_effect);
    return receipt;
}

fn recallExpected(
    snapshot: types.ResourceSnapshot,
    attempt_low: u64,
    attempt_high: u64,
) types.RecallExpectedRequest {
    var request = std.mem.zeroes(types.RecallExpectedRequest);
    request.abi_version = types.abi_version;
    request.struct_size = @sizeOf(types.RecallExpectedRequest);
    request.resource = snapshot.id;
    request.scheduling_revision = snapshot.scheduling_revision;
    request.gate_epoch = snapshot.gate_epoch;
    request.action_attempt_id_low = attempt_low;
    request.action_attempt_id_high = attempt_high;
    request.payload_generation = snapshot.payload_generation;
    request.subscriber_count = snapshot.active_subscriber_count;
    request.activity_score = snapshot.activity_score;
    request.flags = snapshot.flags;
    return request;
}

fn recallNoEffect(
    token: types.RecallToken,
    receipt_low: u64,
    receipt_high: u64,
) types.DestructiveNoEffectReceipt {
    var receipt = std.mem.zeroes(types.DestructiveNoEffectReceipt);
    receipt.abi_version = types.abi_version;
    receipt.struct_size = @sizeOf(types.DestructiveNoEffectReceipt);
    receipt.action_attempt_id_low = token.action_attempt_id_low;
    receipt.action_attempt_id_high = token.action_attempt_id_high;
    receipt.receipt_id_low = receipt_low;
    receipt.receipt_id_high = receipt_high;
    receipt.observed_monotonic_timestamp = 1;
    receipt.proof_mask = types.no_effect_proof_not_invoked;
    receipt.reason = @intFromEnum(types.NoEffectReason.executor_rejected_before_effect);
    return receipt;
}

test "abandoned mutex recovery accepts only a complete committed ledger" {
    var fixture = try Fixture.create(2, 2, 4);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    var subscription_request = std.mem.zeroes(types.SubscriptionRequest);
    subscription_request.abi_version = types.abi_version;
    subscription_request.struct_size = @sizeOf(types.SubscriptionRequest);
    subscription_request.resource = resource;
    subscription_request.subscriber_application_key = 9;
    subscription_request.subscriber_instance_id = 10;
    subscription_request.subscriber_session_id = 11;
    subscription_request.lease_duration = 100;
    subscription_request.subscriber_process_id = 12;
    subscription_request.intent_mask = 0b0010;
    var subscription = std.mem.zeroes(types.SubscriptionReceipt);
    try subscriptions.subscribe(fixture.context, &subscription_request, &subscription);
    var request = useRequest(resource, 1, 10, 1);
    request.requester_application_key = subscription_request.subscriber_application_key;
    request.requester_instance_id = subscription_request.subscriber_instance_id;
    request.requester_session_id = subscription_request.subscriber_session_id;
    request.requester_process_id = subscription_request.subscriber_process_id;
    var task = std.mem.zeroes(types.TaskReceipt);
    try tasks.enqueue(fixture.context, &request, 1, &task);

    const previous_epoch = fixture.context.header.mapping_epoch;
    try std.testing.expect(fixture.context.recoverAbandonedLocked());
    try std.testing.expectEqual(types.ConsistencyState.stable, fixture.context.consistencyState());
    try std.testing.expectEqual(previous_epoch + 1, fixture.context.header.mapping_epoch);
}

test "abandoned mutex recovery quarantines an interrupted mutation" {
    var fixture = try Fixture.create(1, 0, 2);
    defer fixture.deinit();
    _ = try fixture.publish(1);
    fixture.context.header.mutation_sequence |= 1;

    try std.testing.expect(!fixture.context.recoverAbandonedLocked());
    try std.testing.expectEqual(types.ConsistencyState.quarantined, fixture.context.consistencyState());
}

test "abandoned mutex recovery quarantines corrupt aggregate and queue facts" {
    var fixture = try Fixture.create(1, 1, 2);
    defer fixture.deinit();
    const resource = try fixture.publish(1);
    var request = useRequest(resource, 1, 10, 1);
    _ = try subscribeForUse(fixture.context, &request, 10_000);
    var task = std.mem.zeroes(types.TaskReceipt);
    try tasks.enqueue(fixture.context, &request, 1, &task);
    fixture.context.column(u32, fixture.context.layout.resources.queued_request_count)[resource.resource_slot] = 0;

    try std.testing.expect(!fixture.context.recoverAbandonedLocked());
    try std.testing.expectEqual(types.ConsistencyState.quarantined, fixture.context.consistencyState());
}

test "resource and task slot reuse rejects stale generations" {
    var fixture = try Fixture.create(1, 0, 2);
    defer fixture.deinit();
    const first = try fixture.publish(1);
    try resources.revoke(fixture.context, first);
    const second = try fixture.publish(1);
    try std.testing.expectEqual(first.resource_slot, second.resource_slot);
    try std.testing.expect(first.resource_generation != second.resource_generation);
    var snapshot = std.mem.zeroes(types.ResourceSnapshot);
    try std.testing.expectError(error.StaleReference, resources.snapshotOne(fixture.context, first, &snapshot));
}

test "authority identities accept a nonzero high half with a zero low half" {
    var fixture = try Fixture.create(1, 0, 2);
    defer fixture.deinit();
    var publication = basePublication(1);
    publication.owner_instance_id = 0;
    publication.owner_instance_id_high = 0xA001;
    publication.executor_id_low = 0;
    publication.executor_id_high = 0xB001;
    var resource = std.mem.zeroes(types.ResourceRef);
    try resources.publish(fixture.context, &publication, &resource);
    var snapshot = std.mem.zeroes(types.ResourceSnapshot);
    try resources.snapshotOne(fixture.context, resource, &snapshot);
    try std.testing.expectEqual(@as(u64, 0), snapshot.owner_instance_id);
    try std.testing.expectEqual(@as(u64, 0xA001), snapshot.owner_instance_id_high);
    try std.testing.expectEqual(@as(u64, 0), snapshot.executor_id_low);
    try std.testing.expectEqual(@as(u64, 0xB001), snapshot.executor_id_high);
}
