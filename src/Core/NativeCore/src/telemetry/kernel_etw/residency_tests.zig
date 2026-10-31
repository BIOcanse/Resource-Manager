const std = @import("std");
const residency = @import("residency.zig");
const Owner = residency.Owner;
const a = std.testing.allocator;

const one_processor = [_]i32{0};
const two_processors = [_]i32{ 0, 1 };
const record_chunk_bytes = residency.records_per_chunk * residency.pair_record_bytes;

/// W = 100 QPC (1000 QPC/s, 100 ms), finite window, processor 0.
fn owner() !Owner {
    return Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 100, .max_pairs = 32, .processors = &one_processor });
}
fn owner2() !Owner {
    return Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 100, .max_pairs = 32, .processors = &two_processors });
}
fn seed(o: *Owner, pid: i32, tid: i32) !void {
    try o.seedProcess(pid, 100 + pid, " process ");
    try o.seedThread(pid, tid);
}
/// Civil time is opaque to the owner: qpc + 10000 ticks.
fn cs(o: *Owner, qpc: i64, lp: i32, old_pid: i32, old_tid: i32, pid: i32, tid: i32) !void {
    try o.observeContextSwitch(qpc, qpc + 10000, lp, old_pid, old_tid, pid, null, tid);
}

test "finite window keeps exact duration switches identity and civil through" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(i64, 0), s.window_start_qpc);
    try std.testing.expectEqual(@as(i64, 100), s.measured_through_qpc);
    // time(S) + 100 QPC at 1000 QPC/s = 10000 + 1,000,000 ticks.
    try std.testing.expectEqual(@as(?i64, 1_010_000), s.measured_through);
    try std.testing.expectEqual(@as(usize, 1), s.records.len);
    const r = s.records[0];
    try std.testing.expectEqual(@as(i64, 100), r.execution_time_qpc);
    try std.testing.expectEqual(@as(i32, 1), r.switch_count);
    try std.testing.expectEqual(@as(i64, 1), r.process_instance_id);
    try std.testing.expectEqual(@as(i64, 1), r.thread_instance_id);
    try std.testing.expectEqual(@as(?i64, 110), r.process_start_key);
    try std.testing.expectEqualStrings("process", r.process_name);
    const d = o.getDiagnostics();
    try std.testing.expectEqual(@as(usize, 1), d.active_pair_count);
    try std.testing.expectEqual(@as(usize, record_chunk_bytes), d.window_storage_bytes);
}

test "runs spanning S and E are clipped, switches count only inside the window, and a late processor sets S" {
    var o = try owner2();
    defer o.deinit();
    try seed(&o, 10, 20);
    try o.seedThread(10, 21);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    var before = try o.snapshot(a);
    defer before.deinit(a);
    try std.testing.expect(!before.boundaries_ready);
    try std.testing.expectEqual(@as(?i64, null), before.measured_through);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().window_storage_bytes);
    // Processor 1 is covered late: S = 30, E = 130.
    try cs(&o, 30, 1, 0, 0, 10, 21);
    try cs(&o, 80, 0, 10, 20, 0, 0);
    try cs(&o, 150, 1, 10, 21, 0, 0);
    var partial = try o.snapshot(a);
    defer partial.deinit(a);
    try std.testing.expect(partial.boundaries_ready and !partial.is_complete);
    try std.testing.expectEqual(@as(i64, 30), partial.window_start_qpc);
    try std.testing.expectEqual(@as(i64, 80), partial.measured_through_qpc);
    try std.testing.expectEqual(@as(?i64, 10080), partial.measured_through);
    try std.testing.expectEqual(@as(usize, 0), partial.records.len);
    try cs(&o, 200, 0, 0, 0, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(i64, 30), s.window_start_qpc);
    try std.testing.expectEqual(@as(i64, 130), s.measured_through_qpc);
    try std.testing.expectEqual(@as(usize, 2), s.records.len);
    // Rows are ordered by (process instance, thread instance, processor).
    try std.testing.expectEqual(@as(i32, 20), s.records[0].thread_id);
    try std.testing.expectEqual(@as(i32, 0), s.records[0].logical_processor_id);
    try std.testing.expectEqual(@as(i64, 50), s.records[0].execution_time_qpc); // [0, 80) clamped to S.
    try std.testing.expectEqual(@as(i32, 0), s.records[0].switch_count); // Its switch preceded S.
    try std.testing.expectEqual(@as(i32, 21), s.records[1].thread_id);
    try std.testing.expectEqual(@as(i64, 100), s.records[1].execution_time_qpc); // [30, 150) clipped at E.
    try std.testing.expectEqual(@as(i32, 1), s.records[1].switch_count);
}

test "unknown and idle threads preserve continuity without fabricating attribution" {
    var o = try owner();
    defer o.deinit();
    try cs(&o, 0, 0, 0, 0, 100, 999);
    try cs(&o, 50, 0, 100, 999, 0, 0);
    try cs(&o, 100, 0, 0, 0, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(usize, 0), s.records.len);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().window_storage_bytes);
}

test "switches on processors outside the expected set are ignored" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 3, 0, 0, 10, 20);
    try cs(&o, 100, 3, 10, 20, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expect(!s.boundaries_ready);
    try std.testing.expectEqual(@as(i64, 100), o.latest_event_qpc);
}

test "PID TID reuse produces distinct retained lifetime rows" {
    var o = try owner();
    defer o.deinit();
    try o.observeProcessStart(0, 0, 10, 1, 100, "old");
    try o.observeThreadStart(0, 0, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 50, 0, 10, 20, 0, 0);
    try o.observeProcessStart(50, 0, 10, 2, 200, "new");
    try o.observeThreadStart(50, 0, 10, 20);
    try cs(&o, 50, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(usize, 2), s.records.len);
    try std.testing.expectEqualStrings("old", s.records[0].process_name);
    try std.testing.expectEqual(@as(i64, 1), s.records[0].thread_instance_id);
    try std.testing.expectEqual(@as(i64, 2), s.records[1].process_instance_id);
    try std.testing.expectEqualStrings("new", s.records[1].process_name);
    try std.testing.expectEqual(@as(i64, 2), s.records[1].thread_instance_id);
    for (s.records) |r| try std.testing.expectEqual(@as(i64, 50), r.execution_time_qpc);
}

test "thread retirement does not erase the interval already running" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try o.observeThreadEnd(50, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expectEqual(@as(i64, 100), s.records[0].execution_time_qpc);
    try o.releaseWindowStorage();
    // The ended thread was kept only by the window; releasing it returns the identity.
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().retained_thread_identity_count);
}

test "mismatched old thread before E restarts the finite window at that switch" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 60, 0, 10, 20, 10, 20);
    const epoch = o.continuity_epoch;
    try cs(&o, 100, 0, 10, 999, 10, 20);
    try std.testing.expectEqual(epoch, o.continuity_epoch);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().active_pair_count);
    var incomplete = try o.snapshot(a);
    defer incomplete.deinit(a);
    try std.testing.expect(!incomplete.is_complete);
    try std.testing.expectEqual(@as(i64, 100), incomplete.window_start_qpc);
    try cs(&o, 200, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(i64, 100), s.window_start_qpc);
    try std.testing.expectEqual(@as(i64, 100), s.records[0].execution_time_qpc);
    try std.testing.expectEqual(@as(i32, 1), s.records[0].switch_count);
}

test "a finite window ignores a discontinuity after its processor covered E" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    try cs(&o, 150, 0, 10, 999, 10, 20);
    try cs(&o, 300, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(i64, 0), s.window_start_qpc);
    try std.testing.expectEqual(@as(usize, 1), s.records.len);
    try std.testing.expectEqual(@as(i64, 100), s.records[0].execution_time_qpc);
}

test "backward events reset continuity preserve time floor and identity" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 100, 0, 0, 0, 10, 20);
    try cs(&o, 200, 0, 10, 20, 0, 0);
    try cs(&o, 150, 0, 0, 0, 10, 20);
    try std.testing.expectEqual(@as(i64, 200), o.latest_event_qpc);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().active_pair_count);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expect(!s.is_complete);
    try std.testing.expectEqual(@as(usize, 1), o.getDiagnostics().current_thread_count);
}

test "event loss invalidates inventory tokens and preserves monotonic IDs and time floor" {
    var o = try owner();
    defer o.deinit();
    const inventory = o.captureInventorySeed();
    const process = (try o.seedInventoryProcess(inventory, 10, 100, "old")).?;
    try o.seedInventoryThread(process, 20);
    try cs(&o, 100, 0, 0, 0, 10, 20);
    try o.resetAfterEventLoss();
    try o.seedInventoryThread(process, 21);
    try std.testing.expect((try o.seedInventoryProcess(inventory, 11, 200, "late")) == null);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().current_process_count);
    try o.observeProcessStart(99, 0, 10, 2, 200, "delayed");
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().current_process_count);
    try seed(&o, 10, 20);
    try cs(&o, 100, 0, 0, 0, 10, 20);
    try cs(&o, 200, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expectEqual(@as(i64, 2), s.records[0].process_instance_id);
    try std.testing.expectEqual(@as(i64, 2), s.records[0].thread_instance_id);
}

test "inventory cannot attach unverified birth to event process or seed stale process threads" {
    var o = try owner();
    defer o.deinit();
    try o.observeProcessStart(0, 0, 10, 1, null, "event");
    const inventory = o.captureInventorySeed();
    try std.testing.expect((try o.seedInventoryProcess(inventory, 10, 100, "inventory")) == null);
    try o.seedProcess(10, 100, "inventory");
    try std.testing.expectEqual(@as(?i64, null), o.current_processes.get(10).?.start_key);
    try o.observeProcessStart(0, 0, 10, 2, 200, "replacement");
    const process = (try o.seedInventoryProcess(inventory, 10, 200, null)).?;
    try o.observeProcessStart(0, 0, 10, 3, 300, "new");
    try o.seedInventoryThread(process, 20);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().current_thread_count);
}

test "handled pair capacity keeps pre-reset facts and the failing switch starts a fresh window" {
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 100, .max_pairs = 1, .processors = &one_processor });
    defer o.deinit();
    try seed(&o, 10, 20);
    try o.seedThread(10, 21);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 50, 0, 10, 20, 10, 21);
    try cs(&o, 70, 0, 10, 21, 0, 0);
    const d = o.getDiagnostics();
    try std.testing.expectEqual(@as(i64, 1), d.capacity_reset_count);
    try std.testing.expectEqual(@as(usize, 0), d.active_pair_count);
    try std.testing.expectEqual(@as(usize, 0), d.window_storage_bytes);
    try std.testing.expectEqual(@intFromEnum(residency.CapacityResetReason.active_pair_limit), d.last_capacity_reset.reason);
    try std.testing.expectEqual(@as(i64, 70), d.last_capacity_reset.event_qpc);
    try std.testing.expectEqual(@as(i64, 0), d.last_capacity_reset.window_start_qpc);
    try std.testing.expectEqual(@as(u64, 1), d.last_capacity_reset.active_pair_count);
    try std.testing.expectEqual(@as(u64, 0), d.last_capacity_reset.required_slot_span);
    try std.testing.expectEqual(@as(u64, 0), d.last_capacity_reset.ring_slots);
    try std.testing.expectEqual(@as(u64, 2), d.last_capacity_reset.current_thread_count);
    try std.testing.expectEqual(@intFromEnum(residency.CapacityReason.active_pairs), o.last_capacity_failure.reason);
    try std.testing.expectEqual(@as(u64, 2), o.last_capacity_failure.observed);
    try std.testing.expectEqual(@as(u64, 1), o.last_capacity_failure.limit);
    // The failing switch is processor 0's first boundary: S = 70.
    try cs(&o, 170, 0, 0, 0, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(i64, 70), s.window_start_qpc);
    try std.testing.expectEqual(@as(usize, 0), s.records.len);
}

test "the expected processor set is validated and stored for the capture" {
    try std.testing.expectError(error.InvalidConfig, Owner.init(a, .{ .qpc_frequency = 1000, .processors = &.{ 0, 0 } }));
    try std.testing.expectError(error.InvalidConfig, Owner.init(a, .{ .qpc_frequency = 1000, .processors = &.{-1} }));
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 100, .max_processors = 2 });
    defer o.deinit();
    try std.testing.expectError(error.InvalidConfig, o.setProcessors(&.{}));
    try std.testing.expectError(error.CapacityExceeded, o.setProcessors(&.{ 0, 1, 2 }));
    try std.testing.expectEqual(@intFromEnum(residency.CapacityReason.processors), o.last_capacity_failure.reason);
    // No processor set yet: switches never establish S.
    try cs(&o, 0, 0, 0, 0, 0, 0);
    var none = try o.snapshot(a);
    defer none.deinit(a);
    try std.testing.expect(!none.boundaries_ready);
    try o.setProcessors(&.{ 1, 0 });
    try std.testing.expectEqualSlices(i32, &.{ 1, 0 }, o.expected);
    try cs(&o, 10, 0, 0, 0, 0, 0);
    var missing = try o.snapshot(a);
    defer missing.deinit(a);
    try std.testing.expectEqual(@as(?i64, null), missing.measured_through);
    try std.testing.expectEqual(@as(i64, 0), missing.measured_through_qpc);
    try cs(&o, 20, 1, 0, 0, 0, 0);
    var ready = try o.snapshot(a);
    defer ready.deinit(a);
    try std.testing.expect(ready.boundaries_ready and !ready.is_complete);
    try std.testing.expectEqual(@as(i64, 20), ready.window_start_qpc);
}

test "Unicode process whitespace uses the managed trim semantics" {
    var o = try owner();
    defer o.deinit();
    try o.seedProcess(10, 100, "\u{3000}\u{a0}name\u{2009}");
    try o.seedThread(10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expectEqualStrings("name", s.records[0].process_name);
}

test "same process TID reuse keeps old running lifetime and new thread distinct" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try o.observeThreadEnd(25, 0, 10, 20);
    try o.observeThreadStart(25, 0, 10, 20);
    try cs(&o, 50, 0, 10, 20, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expectEqual(@as(usize, 2), s.records.len);
    try std.testing.expect(s.records[0].thread_instance_id < s.records[1].thread_instance_id);
    for (s.records) |r| {
        try std.testing.expectEqual(@as(i64, 1), r.process_instance_id);
        try std.testing.expectEqual(@as(i64, 50), r.execution_time_qpc);
    }
}

test "same unique key updates shared lifetime name but mismatched process end is ignored" {
    var o = try owner();
    defer o.deinit();
    try o.observeProcessStart(0, 0, 10, 111, 100, "old");
    try o.seedThread(10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 50, 0, 10, 20, 10, 20);
    try o.observeProcessStart(50, 0, 10, 111, 200, "new");
    try o.observeProcessEnd(50, 0, 10, 222);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expectEqual(@as(usize, 1), s.records.len);
    try std.testing.expectEqualStrings("new", s.records[0].process_name);
    try std.testing.expectEqual(@as(?i64, 100), s.records[0].process_start_key);
    try std.testing.expectEqual(@as(i32, 2), s.records[0].switch_count);
}

test "clear restarts identity IDs QPC floor and window mode but invalidates inventory handles" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    const inventory = o.captureInventorySeed();
    try o.beginWindowAt(0, 4);
    try cs(&o, 1000, 0, 0, 0, 10, 20);
    try o.clear();
    try std.testing.expectEqual(@as(u32, 0), o.slots);
    try std.testing.expect((try o.seedInventoryProcess(inventory, 20, 200, "stale")) == null);
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(i64, 1), s.records[0].process_instance_id);
    try std.testing.expectEqual(@as(i64, 1), s.records[0].thread_instance_id);
}

test "configured identity processor and name capacities return errors" {
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .max_processes = 1, .max_threads = 1, .max_processors = 1, .max_name_bytes = 4, .processors = &one_processor });
    defer o.deinit();
    try o.seedProcess(10, 100, "name");
    try std.testing.expectError(error.CapacityExceeded, o.seedProcess(11, 100, "x"));
    try o.seedThread(10, 20);
    try std.testing.expectError(error.CapacityExceeded, o.seedThread(10, 21));
    try std.testing.expectError(error.CapacityExceeded, o.setProcessors(&.{ 0, 1 }));
    try std.testing.expectError(error.CapacityExceeded, o.seedProcess(10, 100, "bigger"));
}

test "seed capacity failure invalidates a previously complete publication window" {
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 100, .max_processes = 1, .processors = &one_processor });
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var before = try o.snapshot(a);
    defer before.deinit(a);
    try std.testing.expect(before.is_complete);
    try std.testing.expectError(error.CapacityExceeded, o.seedProcess(11, 200, "over cap"));
    var after = try o.snapshot(a);
    defer after.deinit(a);
    try std.testing.expect(!after.is_complete);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().active_pair_count);
}

test "a window begin floors delayed switches and keeps identities" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 10, 20);
    const epoch = o.continuity_epoch;
    try o.beginWindowAt(500, 0);
    try std.testing.expectEqual(epoch + 1, o.continuity_epoch);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().window_storage_bytes);
    try cs(&o, 400, 0, 10, 20, 0, 0);
    try std.testing.expectEqual(@as(i64, 100), o.latest_event_qpc);
    var floored = try o.snapshot(a);
    defer floored.deinit(a);
    try std.testing.expect(!floored.boundaries_ready);
    try cs(&o, 500, 0, 0, 0, 10, 20);
    try cs(&o, 600, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(i64, 500), s.window_start_qpc);
    try std.testing.expectEqual(@as(i64, 1), s.records[0].thread_instance_id);
    try std.testing.expectError(error.InvalidInput, o.beginWindowAt(0, residency.maximum_slots_per_window + 1));
}

test "continuous snapshots report the last n complete slots and slide one slot at a time" {
    var o = try owner();
    defer o.deinit();
    // n = 4: b(k) = 25k, R = 10.
    try o.beginWindowAt(0, 4);
    try seed(&o, 10, 20);
    try o.seedThread(10, 21);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 30, 0, 10, 20, 0, 0);
    try cs(&o, 60, 0, 0, 0, 10, 21);
    try cs(&o, 100, 0, 10, 21, 10, 20);
    var first = try o.snapshot(a);
    defer first.deinit(a);
    try std.testing.expect(first.is_complete);
    try std.testing.expectEqual(@as(i64, 0), first.window_start_qpc);
    try std.testing.expectEqual(@as(i64, 100), first.measured_through_qpc);
    try std.testing.expectEqual(@as(usize, 2), first.records.len);
    try std.testing.expectEqual(@as(i64, 30), first.records[0].execution_time_qpc);
    try std.testing.expectEqual(@as(i32, 1), first.records[0].switch_count);
    try std.testing.expectEqual(@as(i64, 40), first.records[1].execution_time_qpc);
    try std.testing.expectEqual(@as(i32, 1), first.records[1].switch_count);
    try cs(&o, 125, 0, 10, 20, 0, 0);
    var second = try o.snapshot(a);
    defer second.deinit(a);
    try std.testing.expect(second.is_complete);
    try std.testing.expectEqual(@as(i64, 25), second.window_start_qpc);
    try std.testing.expectEqual(@as(i64, 125), second.measured_through_qpc);
    try std.testing.expectEqual(@as(?i64, 10000 + 1_250_000), second.measured_through);
    try std.testing.expectEqual(@as(usize, 2), second.records.len);
    // Thread 20: [25, 30) + [100, 125); its switch at 100 is in slot 4.
    try std.testing.expectEqual(@as(i64, 30), second.records[0].execution_time_qpc);
    try std.testing.expectEqual(@as(i32, 1), second.records[0].switch_count);
    try std.testing.expectEqual(@as(i64, 40), second.records[1].execution_time_qpc);
    const d = o.getDiagnostics();
    try std.testing.expectEqual(@as(u32, 4), d.slots_per_window);
    try std.testing.expectEqual(@as(u32, 10), d.ring_slots);
    try std.testing.expectEqual(Owner.windowStorageBytes(1, 10), d.window_storage_bytes);
}

test "continuous storage stays bounded over a long run with thread churn" {
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 100, .max_pairs = 64, .max_threads = 256, .processors = &one_processor });
    defer o.deinit();
    try o.beginWindowAt(0, 4);
    try o.seedProcess(10, 110, "churn");
    var previous: i32 = 0;
    var tid: i32 = 1;
    while (tid <= 5000) : (tid += 1) {
        const qpc: i64 = @as(i64, tid) * 10;
        try o.observeThreadStart(qpc, 0, 10, tid);
        try cs(&o, qpc, 0, if (previous == 0) 0 else 10, previous, 10, tid);
        if (previous != 0) try o.observeThreadEnd(qpc, 0, 10, previous);
        previous = tid;
        const d = o.getDiagnostics();
        // A pair lives at most about W + 2 slots: 150 QPC of 10 QPC runs.
        try std.testing.expect(d.active_pair_count <= 16);
        try std.testing.expect(d.retained_thread_identity_count <= 18);
    }
    try std.testing.expect(o.getDiagnostics().capacity_reset_count == 0);
    try std.testing.expectEqual(Owner.windowStorageBytes(16, 10), o.getDiagnostics().window_storage_bytes);
    var s = try o.snapshot(a);
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    var total: i64 = 0;
    for (s.records) |r| total += r.execution_time_qpc;
    try std.testing.expectEqual(@as(i64, 100), total);
}

test "pair records are 24 bytes and N pairs occupy exact chunk bytes" {
    try std.testing.expectEqual(@as(usize, 24), residency.pair_record_bytes);
    try std.testing.expectEqual(@as(usize, 65_520), record_chunk_bytes);
    // Copy 011 (26,237 pairs) and candidate .5 (13.8k pairs), finite windows.
    try std.testing.expectEqual(@as(usize, 655_200), Owner.windowStorageBytes(26_237, 0));
    try std.testing.expectEqual(@as(usize, 393_120), Owner.windowStorageBytes(13_800, 0));
    // n = 5: R = 12 cells of 16 B, 341 rings per 64 KiB chunk.
    try std.testing.expectEqual(@as(usize, 65_520 + 65_472), Owner.windowStorageBytes(1, 12));
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 100, .max_pairs = 4096, .max_threads = 4096, .processors = &one_processor });
    defer o.deinit();
    try o.seedProcess(10, 110, "many");
    var tid: i32 = 1;
    while (tid <= 3000) : (tid += 1) try o.seedThread(10, tid);
    try cs(&o, 0, 0, 0, 0, 10, 1);
    tid = 2;
    while (tid <= 3000) : (tid += 1) try o.observeContextSwitch(@divTrunc(@as(i64, tid), 100), 0, 0, 10, tid - 1, 10, null, tid);
    try std.testing.expectEqual(@as(usize, 2999), o.getDiagnostics().active_pair_count);
    try std.testing.expectEqual(@as(usize, 2 * 65_520), o.getDiagnostics().window_storage_bytes);
    try o.releaseWindowStorage();
    const d = o.getDiagnostics();
    try std.testing.expectEqual(@as(usize, 0), d.window_storage_bytes);
    try std.testing.expectEqual(@as(usize, 2 * 65_520), d.peak_window_storage_bytes);
    try std.testing.expectEqual(@as(usize, 3000), d.current_thread_count);
}

test "all allocation failures unwind owner and snapshot without leaks" {
    try std.testing.checkAllAllocationFailures(a, allocationFailureCase, .{});
}
fn allocationFailureCase(allocator: std.mem.Allocator) !void {
    var o = try Owner.init(allocator, .{ .qpc_frequency = 1000, .window_ms = 100, .processors = &one_processor });
    defer o.deinit();
    try o.seedProcess(10, 100, "p");
    try o.seedThread(10, 20);
    try o.observeContextSwitch(0, 0, 0, 0, 0, 10, null, 20);
    try o.observeContextSwitch(100, 0, 0, 10, 20, 0, null, 0);
    var s = try o.snapshot(allocator);
    defer s.deinit(allocator);
}

test "separate window allocator failures return all window and metadata allocations" {
    try std.testing.checkAllAllocationFailures(a, windowAllocationFailureCase, .{});
}
fn windowAllocationFailureCase(window_allocator: std.mem.Allocator) !void {
    var o = try Owner.initWithWindowAllocator(a, window_allocator, .{ .qpc_frequency = 1000, .window_ms = 100, .processors = &one_processor });
    defer o.deinit();
    try o.beginWindowAt(0, 2);
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var published = try o.snapshot(a);
    defer published.deinit(a);
    try o.releaseWindowStorage();
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().window_storage_bytes);
    try std.testing.expectEqual(@as(usize, 1), o.getDiagnostics().current_thread_count);
}
