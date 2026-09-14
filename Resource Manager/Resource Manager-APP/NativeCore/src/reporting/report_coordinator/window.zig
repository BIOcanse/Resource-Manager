const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const WindowValues = state.RollingValues;

pub fn update(
    session: *state.Session,
    input: *const protocol.SourceSnapshotInput,
    fact: *const protocol.FactInput,
) ResultCode {
    const bucket_start = alignDown(
        input.observed_at_utc_milliseconds,
        session.config.bucket_width_milliseconds,
    );
    const existing = session.findBucket(
        input.source_handle,
        fact.rule_handle,
        fact.target_handle,
        bucket_start,
    );
    const slot_index = existing orelse state.firstFreeFrom(
        state.BucketSlot,
        session.buckets,
        session.next_bucket_slot,
    ) orelse return .buffer_too_small;
    const mutation = session.allocateMutation() orelse return .out_of_memory;
    session.snapshotBucket(slot_index);

    if (existing == null) {
        const generation = state.nextSlotGeneration(session.buckets[slot_index].slot_generation) orelse
            return .out_of_memory;
        const handle = session.allocateBucketHandle() orelse return .out_of_memory;
        session.buckets[slot_index] = .{
            .occupied = true,
            .slot_generation = generation,
            .bucket_handle = handle,
            .source_handle = input.source_handle,
            .rule_handle = fact.rule_handle,
            .rule_generation = fact.rule_generation,
            .target_handle = fact.target_handle,
            .bucket_start_utc_ms = bucket_start,
            .mutation_version = mutation,
        };
        if (!session.insertBucketIndex(slot_index)) {
            session.buckets[slot_index].occupied = false;
            return .buffer_too_small;
        }
        state.advanceFreeCursor(
            &session.next_bucket_slot,
            session.buckets.len,
            slot_index,
        );
    }

    var bucket = &session.buckets[slot_index];
    if (bucket.delete_pending) return .stale_frame;
    if ((fact.flags & protocol.FactFlags.delta_valid) != 0) {
        const next = bucket.delta_value + fact.delta_value;
        if (!std.math.isFinite(next)) return .invalid_argument;
        bucket.delta_value = next;
    }
    if ((fact.flags & protocol.FactFlags.peak_valid) != 0) {
        bucket.peak_value = @max(bucket.peak_value, fact.peak_value);
    }
    if ((fact.flags & protocol.FactFlags.event_count_valid) != 0) {
        bucket.event_count = addSaturating(bucket.event_count, fact.event_count);
    }
    if ((fact.flags & protocol.FactFlags.request_event_count_valid) != 0) {
        bucket.request_event_count = addSaturating(
            bucket.request_event_count,
            fact.request_event_count,
        );
    }
    if ((fact.flags & protocol.FactFlags.connection_event_count_valid) != 0) {
        bucket.connection_event_count = addSaturating(
            bucket.connection_event_count,
            fact.connection_event_count,
        );
    }
    if ((fact.flags & protocol.FactFlags.sample_hit_count_valid) != 0) {
        bucket.sample_hit_count = addSaturating(bucket.sample_hit_count, fact.sample_hit_count);
    }
    bucket.sample_duration_milliseconds = addSaturating(
        bucket.sample_duration_milliseconds,
        fact.sample_duration_milliseconds,
    );
    bucket.mutation_version = mutation;
    const rule_index = session.findRule(fact.rule_handle) orelse return .invalid_argument;
    const observation_index = session.findObservation(
        input.source_handle,
        session.rules[rule_index].value.coverage_scope_handle,
        fact.rule_handle,
        fact.target_handle,
    ) orelse return .invalid_argument;
    var observation = &session.observations[observation_index];
    if (observation.window_revision == std.math.maxInt(u64)) return .out_of_memory;
    observation.window_revision += 1;
    return .ok;
}

pub fn aggregate(session: *state.Session, now_utc_ms: i64) ResultCode {
    return aggregateInternal(session, now_utc_ms, true);
}

pub fn rebuildAggregate(session: *state.Session, now_utc_ms: i64) ResultCode {
    return aggregateInternal(session, now_utc_ms, false);
}

fn aggregateInternal(
    session: *state.Session,
    now_utc_ms: i64,
    track_boundary_changes: bool,
) ResultCode {
    @memset(session.staging_rolling_values, .{});
    const cutoff_24 = subtractFloor(now_utc_ms, session.config.window_24h_milliseconds);
    const cutoff_7d = subtractFloor(now_utc_ms, session.config.window_7d_milliseconds);
    for (session.buckets) |bucket| {
        if (!bucket.occupied or bucket.delete_pending) continue;
        if (bucket.bucket_start_utc_ms > now_utc_ms) continue;
        const bucket_end = addSaturatingI64(
            bucket.bucket_start_utc_ms,
            session.config.bucket_width_milliseconds,
        );
        if (bucket_end <= cutoff_7d) continue;

        const rule_index = session.findRule(bucket.rule_handle) orelse return .invalid_argument;
        const rule = session.rules[rule_index].value;
        const observation_index = session.findObservation(
            bucket.source_handle,
            rule.coverage_scope_handle,
            bucket.rule_handle,
            bucket.target_handle,
        ) orelse continue;
        const observation = session.observations[observation_index];
        if (!observation.occupied or observation.delete_pending) continue;
        var output = &session.staging_rolling_values[observation_index];

        output.sum_7d += bucket.delta_value;
        if (!std.math.isFinite(output.sum_7d)) return .invalid_argument;
        output.events_7d = addSaturating(output.events_7d, bucket.event_count);
        output.request_events_7d = addSaturating(
            output.request_events_7d,
            bucket.request_event_count,
        );
        output.connection_events_7d = addSaturating(
            output.connection_events_7d,
            bucket.connection_event_count,
        );
        output.sample_hits_7d = addSaturating(
            output.sample_hits_7d,
            bucket.sample_hit_count,
        );
        if (bucket_end <= cutoff_24) continue;
        output.sum_24h += bucket.delta_value;
        if (!std.math.isFinite(output.sum_24h)) return .invalid_argument;
        output.events_24h = addSaturating(output.events_24h, bucket.event_count);
        output.request_events_24h = addSaturating(
            output.request_events_24h,
            bucket.request_event_count,
        );
        output.connection_events_24h = addSaturating(
            output.connection_events_24h,
            bucket.connection_event_count,
        );
        output.sample_hits_24h = addSaturating(
            output.sample_hits_24h,
            bucket.sample_hit_count,
        );
    }
    if (track_boundary_changes) {
        for (
            session.observations,
            session.rolling_values,
            session.staging_rolling_values,
            0..,
        ) |observation, previous, current, observation_index| {
            if (!observation.occupied or observation.delete_pending or
                std.meta.eql(previous, current)) continue;
            session.snapshotObservation(@intCast(observation_index));
            if (session.observations[observation_index].window_revision ==
                std.math.maxInt(u64)) return .out_of_memory;
            session.observations[observation_index].window_revision += 1;
        }
    }
    std.mem.swap(
        []state.RollingValues,
        &session.rolling_values,
        &session.staging_rolling_values,
    );
    return .ok;
}

pub fn values(session: *const state.Session, observation_index: u32) WindowValues {
    if (observation_index >= session.rolling_values.len) return .{};
    return session.rolling_values[observation_index];
}

pub fn prune(session: *state.Session, now_utc_ms: i64) ResultCode {
    const cutoff = subtractFloor(now_utc_ms, session.config.window_7d_milliseconds);
    for (session.buckets, 0..) |*bucket, bucket_index| {
        if (!bucket.occupied or bucket.delete_pending) continue;
        const bucket_end = addSaturatingI64(
            bucket.bucket_start_utc_ms,
            session.config.bucket_width_milliseconds,
        );
        if (bucket_end > cutoff) continue;
        session.snapshotBucket(@intCast(bucket_index));
        bucket.delete_pending = true;
        bucket.mutation_version = session.allocateMutation() orelse return .out_of_memory;
    }
    return .ok;
}

pub fn nextBoundary(session: *const state.Session, now_utc_ms: i64) i64 {
    var next: i64 = 0;
    for (session.buckets) |bucket| {
        if (!bucket.occupied or bucket.delete_pending) continue;
        const bucket_end = addSaturatingI64(
            bucket.bucket_start_utc_ms,
            session.config.bucket_width_milliseconds,
        );
        const boundary_24 = addSaturatingI64(bucket_end, session.config.window_24h_milliseconds);
        const boundary_7d = addSaturatingI64(bucket_end, session.config.window_7d_milliseconds);
        if (boundary_24 > now_utc_ms and (next == 0 or boundary_24 < next)) next = boundary_24;
        if (boundary_7d > now_utc_ms and (next == 0 or boundary_7d < next)) next = boundary_7d;
    }
    return next;
}

fn alignDown(value: i64, width: u64) i64 {
    const width_i64: i64 = @intCast(width);
    return @divFloor(value, width_i64) * width_i64;
}

fn subtractFloor(value: i64, decrement: u64) i64 {
    if (decrement > std.math.maxInt(i64)) return 0;
    return std.math.sub(i64, value, @intCast(decrement)) catch 0;
}

fn addSaturating(value: u64, increment: u64) u64 {
    return std.math.add(u64, value, increment) catch std.math.maxInt(u64);
}

fn addSaturatingI64(value: i64, increment: u64) i64 {
    if (increment > std.math.maxInt(i64)) return std.math.maxInt(i64);
    return std.math.add(i64, value, @intCast(increment)) catch std.math.maxInt(i64);
}
