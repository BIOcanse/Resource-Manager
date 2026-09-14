const std = @import("std");
const abi = @import("abi.zig");
const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

test "policy priority is the only source selection authority" {
    var config = testConfig(sourceMask(&.{ 1, 2, 3 }), 0);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var policies = [_]protocol.SourcePolicyInput{
        policy(3, 10),
        policy(1, 20),
        policy(2, 30),
    };
    try std.testing.expectEqual(ResultCode.ok, applyPolicy(session, &config, 1, 1, &policies));

    var observations = [_]protocol.ObservationInput{
        matched(1, 101, 201, 1, 301, 1, 1),
        terminal(2, .no_match, 1),
        matched(3, 103, 203, 3, 303, 4, 1),
    };
    const result = resolveFrame(session, &config, 1, 1, 1_000, 1_000, &observations);
    try std.testing.expectEqual(ResultCode.ok, result.code);
    try std.testing.expectEqual(@intFromEnum(protocol.ResolutionStatus.matched), result.output.status);
    try std.testing.expectEqual(@as(u32, 3), result.output.selected_source_id);
    try std.testing.expectEqual(@as(u64, 103), result.output.identity_handle);
    try std.testing.expectEqual(sourceMask(&.{ 1, 2, 3 }), result.output.observed_source_mask);
    try std.testing.expectEqual(sourceMask(&.{ 1, 3 }), result.output.matched_source_mask);
    try std.testing.expectEqual(sourceMask(&.{2}), result.output.no_match_source_mask);
}

test "atomic policy replacement can reorder sources without managed branches" {
    var config = testConfig(sourceMask(&.{ 1, 2, 3 }), 0);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var initial = [_]protocol.SourcePolicyInput{
        policy(3, 10),
        policy(1, 20),
        policy(2, 30),
    };
    try std.testing.expectEqual(ResultCode.ok, applyPolicy(session, &config, 1, 1, &initial));

    var observations = [_]protocol.ObservationInput{
        matched(1, 101, 201, 1, 301, 1, 1),
        terminal(2, .no_match, 1),
        matched(3, 103, 203, 3, 303, 4, 1),
    };
    try std.testing.expectEqual(
        @as(u32, 3),
        resolveFrame(session, &config, 1, 1, 1_000, 1_000, &observations).output.selected_source_id,
    );

    var reordered = [_]protocol.SourcePolicyInput{
        policy(1, 10),
        policy(3, 20),
        policy(2, 30),
    };
    try std.testing.expectEqual(ResultCode.ok, applyPolicy(session, &config, 2, 2, &reordered));
    const reordered_result = resolveFrame(session, &config, 2, 2, 2_000, 2_000, &observations);
    try std.testing.expectEqual(ResultCode.ok, reordered_result.code);
    try std.testing.expectEqual(@as(u32, 1), reordered_result.output.selected_source_id);
    try std.testing.expectEqual(@as(u64, 101), reordered_result.output.identity_handle);
}

test "unavailable stop mask is explicit and hot reconfigurable" {
    const mask = sourceMask(&.{ 1, 2, 3 });
    var config = testConfig(mask, sourceMask(&.{3}));
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var policies = [_]protocol.SourcePolicyInput{
        policy(3, 10),
        policy(1, 20),
        policy(2, 30),
    };
    try std.testing.expectEqual(ResultCode.ok, applyPolicy(session, &config, 1, 1, &policies));
    var observations = [_]protocol.ObservationInput{
        matched(1, 101, 201, 1, 0, 1, 1),
        terminal(2, .no_match, 1),
        terminal(3, .unavailable, 1),
    };
    const stopped = resolveFrame(session, &config, 1, 1, 1_000, 1_000, &observations);
    try std.testing.expectEqual(ResultCode.ok, stopped.code);
    try std.testing.expectEqual(@intFromEnum(protocol.ResolutionStatus.unavailable), stopped.output.status);
    try std.testing.expectEqual(@as(u32, 3), stopped.output.decision_source_id);
    try std.testing.expectEqual(@as(u32, 3), stopped.output.next_required_source_id);

    var next_config = config;
    next_config.generation = 2;
    next_config.stop_on_unavailable_source_mask = 0;
    try std.testing.expectEqual(ResultCode.ok, session.reconfigure(&next_config));
    config = next_config;
    const continued = resolveFrame(session, &config, 1, 2, 2_000, 2_000, &observations);
    try std.testing.expectEqual(ResultCode.ok, continued.code);
    try std.testing.expectEqual(@intFromEnum(protocol.ResolutionStatus.matched), continued.output.status);
    try std.testing.expectEqual(@as(u32, 1), continued.output.selected_source_id);
    try std.testing.expectEqual(sourceMask(&.{3}), continued.output.unavailable_source_mask);
}

test "same identity merges evidence while identity or payload drift conflicts" {
    var config = testConfig(sourceMask(&.{1}), 0);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var policies = [_]protocol.SourcePolicyInput{policy(1, 1)};
    try std.testing.expectEqual(ResultCode.ok, applyPolicy(session, &config, 1, 1, &policies));

    var merge = [_]protocol.ObservationInput{
        matched(1, 10, 20, 1, 30, 1, 7),
        matched(1, 10, 20, 1, 30, 2, 7),
    };
    const merged = resolveFrame(session, &config, 1, 1, 1_000, 1_000, &merge);
    try std.testing.expectEqual(ResultCode.ok, merged.code);
    try std.testing.expectEqual(@intFromEnum(protocol.ResolutionStatus.matched), merged.output.status);
    try std.testing.expectEqual(@as(u64, 3), merged.output.evidence_mask);

    var identities = [_]protocol.ObservationInput{
        matched(1, 10, 20, 1, 30, 1, 8),
        matched(1, 11, 21, 1, 31, 2, 8),
    };
    const identity_conflict = resolveFrame(session, &config, 1, 2, 2_000, 2_000, &identities);
    try std.testing.expectEqual(ResultCode.ok, identity_conflict.code);
    try std.testing.expectEqual(@intFromEnum(protocol.ResolutionStatus.conflict), identity_conflict.output.status);
    try std.testing.expectEqual(@as(u32, 2), identity_conflict.output.conflict_count);
    try std.testing.expectEqual(@as(u64, 0), identity_conflict.output.identity_handle);

    var payloads = [_]protocol.ObservationInput{
        matched(1, 10, 20, 1, 30, 1, 9),
        matched(1, 10, 99, 1, 30, 2, 9),
    };
    const payload_conflict = resolveFrame(session, &config, 1, 3, 3_000, 3_000, &payloads);
    try std.testing.expectEqual(ResultCode.ok, payload_conflict.code);
    try std.testing.expectEqual(@intFromEnum(protocol.ResolutionStatus.conflict), payload_conflict.output.status);
    try std.testing.expectEqual(@as(u32, 2), payload_conflict.output.conflict_count);

    var repeated_conflicting_evidence = [_]protocol.ObservationInput{
        matched(1, 10, 20, 1, 30, 1, 10),
        matched(1, 11, 21, 1, 31, 2, 10),
        matched(1, 11, 21, 1, 31, 4, 10),
    };
    const repeated_conflict = resolveFrame(
        session,
        &config,
        1,
        4,
        4_000,
        4_000,
        &repeated_conflicting_evidence,
    );
    try std.testing.expectEqual(ResultCode.ok, repeated_conflict.code);
    try std.testing.expectEqual(@intFromEnum(protocol.ResolutionStatus.conflict), repeated_conflict.output.status);
    try std.testing.expectEqual(@as(u32, 2), repeated_conflict.output.conflict_count);
}

test "policy staging rejects incomplete duplicate or unsorted authority" {
    var config = testConfig(sourceMask(&.{ 1, 2 }), 0);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var valid = [_]protocol.SourcePolicyInput{
        policy(1, 10),
        policy(2, 20),
    };
    try std.testing.expectEqual(ResultCode.ok, applyPolicy(session, &config, 1, 1, &valid));
    const before = snapshot(session);

    var duplicate_priority = [_]protocol.SourcePolicyInput{
        policy(1, 10),
        policy(2, 10),
    };
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        applyPolicy(session, &config, 2, 2, &duplicate_priority),
    );
    var duplicate_source = [_]protocol.SourcePolicyInput{
        policy(1, 10),
        policy(1, 20),
    };
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        applyPolicy(session, &config, 2, 2, &duplicate_source),
    );
    var missing = [_]protocol.SourcePolicyInput{policy(1, 10)};
    var missing_input = policyReplaceInput(&config, 2, 2, missing.len);
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        session.replacePolicy(&missing_input, &missing),
    );
    const after = snapshot(session);
    try std.testing.expectEqual(before.policy_generation, after.policy_generation);
    try std.testing.expectEqual(before.state_revision, after.state_revision);
    try std.testing.expectEqual(before.policy_fingerprint_low, after.policy_fingerprint_low);
}

test "frame coverage ordering generations time and output shapes fail closed" {
    var config = testConfig(sourceMask(&.{ 1, 2 }), 0);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var policies = [_]protocol.SourcePolicyInput{
        policy(1, 10),
        policy(2, 20),
    };
    try std.testing.expectEqual(ResultCode.ok, applyPolicy(session, &config, 1, 1, &policies));

    const empty = [_]protocol.ObservationInput{};
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        resolveFrame(session, &config, 1, 1, 1_000, 1_000, &empty).code,
    );
    var missing = [_]protocol.ObservationInput{terminal(1, .no_match, 1)};
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        resolveFrame(session, &config, 1, 1, 1_000, 1_000, &missing).code,
    );
    var mixed = [_]protocol.ObservationInput{
        terminal(1, .no_match, 1),
        matched(1, 10, 20, 1, 0, 1, 1),
        terminal(2, .no_match, 1),
    };
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        resolveFrame(session, &config, 1, 1, 1_000, 1_000, &mixed).code,
    );
    var valid = [_]protocol.ObservationInput{
        terminal(1, .no_match, 1),
        terminal(2, .no_match, 1),
    };
    var dirty_output = emptyOutput();
    dirty_output.flags = 1;
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        resolveWithOutput(session, &config, 1, 1, 1_000, 1_000, &valid, &dirty_output),
    );
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        resolveFrame(session, &config, 2, 1, 1_000, 1_000, &valid).code,
    );
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        resolveFrame(session, &config, 1, 1, 1_000, 1_006, &valid).code,
    );
    const accepted = resolveFrame(session, &config, 1, 1, 1_000, 1_005, &valid);
    try std.testing.expectEqual(ResultCode.ok, accepted.code);
    try std.testing.expectEqual(@intFromEnum(protocol.ResolutionStatus.unattributed), accepted.output.status);
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        resolveFrame(session, &config, 1, 1, 1_000, 1_000, &valid).code,
    );
}

test "explicit empty policy and observation frame resolves unattributed" {
    var config = testConfig(0, 0);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var input = policyReplaceInput(&config, 1, 1, 0);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.replacePolicy(session, &input, null, 0),
    );
    var resolve_input = resolveInput(&config, 1, 1, 1_000, 1_000, 0);
    var output = emptyOutput();
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.resolve(session, &resolve_input, null, 0, &output),
    );
    try std.testing.expectEqual(@intFromEnum(protocol.ResolutionStatus.unattributed), output.status);
    try std.testing.expectEqual(@as(u64, 0), output.observed_source_mask);
}

test "module-local C ABI enforces resident budget and executes lifecycle" {
    var invalid_config = testConfig(sourceMask(&.{1}), 0);
    invalid_config.resident_byte_budget = 1;
    var invalid_handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.abi_mismatch)),
        abi.rm_software_identity_resolution_create(&invalid_config, &invalid_handle),
    );
    try std.testing.expectEqual(@as(?*anyopaque, null), invalid_handle);

    var config = testConfig(sourceMask(&.{1}), 0);
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_software_identity_resolution_create(&config, &handle),
    );
    defer abi.rm_software_identity_resolution_destroy(handle);
    try std.testing.expectEqual(protocol.abi_version, abi.rm_software_identity_resolution_abi_version());

    var capacity = std.mem.zeroes(protocol.Capacity);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_software_identity_resolution_query_capacity(handle, &capacity, @sizeOf(protocol.Capacity)),
    );
    try std.testing.expect(capacity.resident_byte_count > 0);
    try std.testing.expect(capacity.resident_byte_count <= config.resident_byte_budget);

    var policies = [_]protocol.SourcePolicyInput{policy(1, 1)};
    var policy_input = policyReplaceInput(&config, 1, 1, policies.len);
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_software_identity_resolution_replace_policy(
            handle,
            &policy_input,
            policies[0..].ptr,
            policies.len,
        ),
    );
    var observations = [_]protocol.ObservationInput{matched(1, 10, 20, 1, 30, 1, 1)};
    var resolve_input = resolveInput(&config, 1, 1, 1_000, 1_000, observations.len);
    var output = emptyOutput();
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_software_identity_resolution_resolve(
            handle,
            &resolve_input,
            observations[0..].ptr,
            observations.len,
            &output,
        ),
    );
    try std.testing.expectEqual(@as(u64, 10), output.identity_handle);
    var summary = emptySummary();
    try std.testing.expectEqual(
        @as(i32, @intFromEnum(ResultCode.ok)),
        abi.rm_software_identity_resolution_snapshot(handle, &summary),
    );
    try std.testing.expectEqual(@as(u64, 1), summary.last_frame_epoch);
}

const ResolveCall = struct {
    code: ResultCode,
    output: protocol.ResolutionOutput,
};

fn applyPolicy(
    session: *session_module.Session,
    config: *const protocol.Config,
    policy_generation: u64,
    operation_epoch: u64,
    policies: []const protocol.SourcePolicyInput,
) ResultCode {
    var input = policyReplaceInput(config, policy_generation, operation_epoch, policies.len);
    return session.replacePolicy(&input, policies);
}

fn resolveFrame(
    session: *session_module.Session,
    config: *const protocol.Config,
    policy_generation: u64,
    frame_epoch: u64,
    command_utc_ms: i64,
    observed_at_utc_ms: i64,
    observations: []const protocol.ObservationInput,
) ResolveCall {
    var output = emptyOutput();
    const code = resolveWithOutput(
        session,
        config,
        policy_generation,
        frame_epoch,
        command_utc_ms,
        observed_at_utc_ms,
        observations,
        &output,
    );
    return .{ .code = code, .output = output };
}

fn resolveWithOutput(
    session: *session_module.Session,
    config: *const protocol.Config,
    policy_generation: u64,
    frame_epoch: u64,
    command_utc_ms: i64,
    observed_at_utc_ms: i64,
    observations: []const protocol.ObservationInput,
    output: *protocol.ResolutionOutput,
) ResultCode {
    var input = resolveInput(
        config,
        policy_generation,
        frame_epoch,
        command_utc_ms,
        observed_at_utc_ms,
        observations.len,
    );
    return session.resolve(&input, observations, output);
}

fn testConfig(required_source_mask: u64, stop_mask: u64) protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = 1,
        .maximum_policy_count = 16,
        .maximum_observation_count = 64,
        .policy_index_capacity = 32,
        .reserved_u32 = 0,
        .required_source_mask = required_source_mask,
        .stop_on_unavailable_source_mask = stop_mask,
        .maximum_future_skew_ms = 5,
        .resident_byte_budget = 1 << 20,
        .flags = 0,
        .reserved = .{ 0, 0, 0, 0 },
    };
}

fn policy(source_id: u32, priority: u32) protocol.SourcePolicyInput {
    return .{
        .struct_size = @sizeOf(protocol.SourcePolicyInput),
        .source_id = source_id,
        .priority = priority,
        .flags = 0,
        .reserved = .{ 0, 0, 0, 0 },
    };
}

fn policyReplaceInput(
    config: *const protocol.Config,
    policy_generation: u64,
    operation_epoch: u64,
    policy_count: usize,
) protocol.PolicyReplaceInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PolicyReplaceInput),
        .configuration_generation = config.generation,
        .policy_generation = policy_generation,
        .operation_epoch = operation_epoch,
        .policy_count = @intCast(policy_count),
        .reserved_u32 = 0,
        .valid_mask = protocol.PolicyReplaceValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn resolveInput(
    config: *const protocol.Config,
    policy_generation: u64,
    frame_epoch: u64,
    command_utc_ms: i64,
    observed_at_utc_ms: i64,
    observation_count: usize,
) protocol.ResolveInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ResolveInput),
        .configuration_generation = config.generation,
        .policy_generation = policy_generation,
        .frame_epoch = frame_epoch,
        .command_utc_ms = command_utc_ms,
        .observed_at_utc_ms = observed_at_utc_ms,
        .observation_count = @intCast(observation_count),
        .reserved_u32 = 0,
        .valid_mask = protocol.ResolveValid.required,
        .flags = 0,
        .reserved = .{ 0, 0, 0 },
    };
}

fn terminal(
    source_id: u32,
    status: protocol.ObservationStatus,
    generation: u64,
) protocol.ObservationInput {
    return .{
        .struct_size = @sizeOf(protocol.ObservationInput),
        .source_id = source_id,
        .status = @intFromEnum(status),
        .flags = 0,
        .identity_handle = 0,
        .display_name_handle = 0,
        .software_kind = 0,
        .reserved_u32 = 0,
        .root_handle = 0,
        .evidence_mask = 0,
        .observation_generation = generation,
        .reserved = .{ 0, 0 },
    };
}

fn matched(
    source_id: u32,
    identity_handle: u64,
    display_name_handle: u64,
    software_kind: u32,
    root_handle: u64,
    evidence_mask: u64,
    generation: u64,
) protocol.ObservationInput {
    var result = terminal(source_id, .matched, generation);
    result.identity_handle = identity_handle;
    result.display_name_handle = display_name_handle;
    result.software_kind = software_kind;
    result.root_handle = root_handle;
    result.evidence_mask = evidence_mask;
    return result;
}

fn emptyOutput() protocol.ResolutionOutput {
    var output = std.mem.zeroes(protocol.ResolutionOutput);
    output.abi_version = protocol.abi_version;
    output.struct_size = @sizeOf(protocol.ResolutionOutput);
    return output;
}

fn emptySummary() protocol.ResolutionSummary {
    var output = std.mem.zeroes(protocol.ResolutionSummary);
    output.abi_version = protocol.abi_version;
    output.struct_size = @sizeOf(protocol.ResolutionSummary);
    return output;
}

fn snapshot(session: *session_module.Session) protocol.ResolutionSummary {
    var output = emptySummary();
    const result = session.snapshot(&output);
    std.debug.assert(result == .ok);
    return output;
}

fn sourceMask(source_ids: []const u32) u64 {
    var result: u64 = 0;
    for (source_ids) |source_id| result |= protocol.sourceBit(source_id).?;
    return result;
}
