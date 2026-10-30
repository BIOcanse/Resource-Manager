const std = @import("std");
const residency = @import("residency.zig");
const Owner = residency.Owner;
const a = std.testing.allocator;

fn owner() !Owner {
    return Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 100, .bucket_ms = 50, .max_pairs = 32 });
}
fn seed(o: *Owner, pid: i32, tid: i32) !void {
    try o.seedProcess(pid, 100 + pid, " process ");
    try o.seedThread(pid, tid);
}
fn cs(o: *Owner, qpc: i64, lp: i32, old_pid: i32, old_tid: i32, pid: i32, tid: i32) !void {
    try o.observeContextSwitch(qpc, qpc + 10000, lp, old_pid, old_tid, pid, null, tid);
}

test "sparse closed intervals retain exact duration switches identity and opaque UTC ticks" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a, &.{0});
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(?i64, 10100), s.measured_through);
    try std.testing.expectEqual(@as(usize, 1), s.records.len);
    const r = s.records[0];
    try std.testing.expectEqual(@as(i64, 100), r.execution_time_qpc);
    try std.testing.expectEqual(@as(i32, 1), r.switch_count);
    try std.testing.expectEqual(@as(i64, 1), r.process_instance_id);
    try std.testing.expectEqual(@as(i64, 1), r.thread_instance_id);
    try std.testing.expectEqual(@as(?i64, 110), r.process_start_key);
    try std.testing.expectEqualStrings("process", r.process_name);
    try std.testing.expectEqual(@as(usize, 2), o.getDiagnostics().retained_cell_count);
}

test "partial first bucket scales duration and excludes initial switch" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 125, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a, &.{0});
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(i64, 25), s.window_start_qpc);
    // The oracle first clips append to retained start, then prorates the first
    // bucket at snapshot: floor(25 * 25 / 50) + 50 + 25 = 87.
    try std.testing.expectEqual(@as(i64, 87), s.records[0].execution_time_qpc);
    try std.testing.expectEqual(@as(i32, 0), s.records[0].switch_count);
}

test "lagging processor fixes common measurement boundary and clips fast processor" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try o.seedThread(10, 21);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 0, 1, 0, 0, 10, 21);
    try cs(&o, 100, 1, 10, 21, 0, 0);
    try cs(&o, 125, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a, &.{ 0, 1 });
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(i64, 100), s.measured_through_qpc);
    try std.testing.expectEqual(@as(usize, 2), s.records.len);
    for (s.records) |r| try std.testing.expectEqual(@as(i64, 100), r.execution_time_qpc);
}

test "unknown and idle threads preserve continuity without fabricating attribution" {
    var o = try owner();
    defer o.deinit();
    try cs(&o, 0, 0, 0, 0, 100, 999);
    try cs(&o, 50, 0, 100, 999, 0, 0);
    try cs(&o, 100, 0, 0, 0, 0, 0);
    var s = try o.snapshot(a, &.{0});
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(usize, 0), s.records.len);
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
    var s = try o.snapshot(a, &.{0});
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(usize, 2), s.records.len);
    for (s.records) |r| {
        try std.testing.expectEqual(@as(i64, 50), r.execution_time_qpc);
        if (r.process_instance_id == 1) {
            try std.testing.expectEqualStrings("old", r.process_name);
            try std.testing.expectEqual(@as(i64, 1), r.thread_instance_id);
        } else {
            try std.testing.expectEqual(@as(i64, 2), r.process_instance_id);
            try std.testing.expectEqualStrings("new", r.process_name);
            try std.testing.expectEqual(@as(i64, 2), r.thread_instance_id);
        }
    }
}

test "thread retirement does not erase the interval already running" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try o.observeThreadEnd(50, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a, &.{0});
    defer s.deinit(a);
    try std.testing.expectEqual(@as(i64, 100), s.records[0].execution_time_qpc);
}

test "mismatched old thread creates gap until an entire fresh window" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 999, 10, 20);
    var incomplete = try o.snapshot(a, &.{0});
    defer incomplete.deinit(a);
    try std.testing.expect(!incomplete.is_complete);
    try cs(&o, 200, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a, &.{0});
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
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
    var s = try o.snapshot(a, &.{0});
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
    var s = try o.snapshot(a, &.{0});
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

test "capacity error invalidates residency and returns explicit failure" {
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 100, .bucket_ms = 50, .max_cells = 1 });
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try std.testing.expectError(error.CapacityExceeded, cs(&o, 100, 0, 10, 20, 0, 0));
    var s = try o.snapshot(a, &.{0});
    defer s.deinit(a);
    try std.testing.expect(!s.is_complete);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().retained_cell_count);
    try std.testing.expectEqual(@as(i64, 1), o.getDiagnostics().capacity_reset_count);
    try std.testing.expectEqual(@intFromEnum(residency.CapacityReason.sparse_cells), o.last_capacity_failure.reason);
    try std.testing.expectEqual(@intFromEnum(residency.CapacityResetReason.none), o.getDiagnostics().last_capacity_reset.reason);
}

test "retention reclaims retired lifetime and sparse old cells" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    try o.observeProcessEnd(100, 0, 10, 0);
    try cs(&o, 250, 0, 0, 0, 0, 0);
    var s = try o.snapshot(a, &.{0});
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(usize, 0), s.records.len);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().retained_process_identity_count);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().retained_thread_identity_count);
}

test "duplicate or missing expected processors remain incomplete" {
    var o = try owner();
    defer o.deinit();
    try cs(&o, 0, 0, 0, 0, 0, 0);
    try cs(&o, 100, 0, 0, 0, 0, 0);
    var d = try o.snapshot(a, &.{ 0, 0 });
    defer d.deinit(a);
    try std.testing.expect(!d.is_complete);
    var m = try o.snapshot(a, &.{ 0, 1 });
    defer m.deinit(a);
    try std.testing.expect(!m.is_complete);
}

test "Unicode process whitespace uses the managed trim semantics" {
    var o = try owner();
    defer o.deinit();
    try o.seedProcess(10, 100, "\u{3000}\u{a0}name\u{2009}");
    try o.seedThread(10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a, &.{0});
    defer s.deinit(a);
    try std.testing.expectEqualStrings("name", s.records[0].process_name);
}

test "all allocation failures unwind owner and snapshot without leaks" {
    try std.testing.checkAllAllocationFailures(a, allocationFailureCase, .{});
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
    var s = try o.snapshot(a, &.{0});
    defer s.deinit(a);
    try std.testing.expectEqual(@as(usize, 2), s.records.len);
    try std.testing.expect(s.records[0].thread_instance_id != s.records[1].thread_instance_id);
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
    var s = try o.snapshot(a, &.{0});
    defer s.deinit(a);
    try std.testing.expectEqual(@as(usize, 1), s.records.len);
    try std.testing.expectEqualStrings("new", s.records[0].process_name);
    try std.testing.expectEqual(@as(?i64, 100), s.records[0].process_start_key);
    try std.testing.expectEqual(@as(i32, 2), s.records[0].switch_count);
}

test "clear restarts identity IDs and QPC floor but invalidates inventory handles" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    const inventory = o.captureInventorySeed();
    try cs(&o, 1000, 0, 0, 0, 10, 20);
    try o.clear();
    try std.testing.expect((try o.seedInventoryProcess(inventory, 20, 200, "stale")) == null);
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a, &.{0});
    defer s.deinit(a);
    try std.testing.expect(s.is_complete);
    try std.testing.expectEqual(@as(i64, 1), s.records[0].process_instance_id);
    try std.testing.expectEqual(@as(i64, 1), s.records[0].thread_instance_id);
}

test "long running rolling sparse cells remain bounded and retired IDs reclaim" {
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 100, .bucket_ms = 50, .max_pairs = 2, .max_cells = 4, .max_processes = 1, .max_threads = 1 });
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    var qpc: i64 = 50;
    while (qpc <= 5000) : (qpc += 50) {
        try cs(&o, qpc, 0, 10, 20, 10, 20);
        var s = try o.snapshot(a, &.{0});
        defer s.deinit(a);
        if (qpc >= 100) {
            try std.testing.expect(s.is_complete);
            try std.testing.expectEqual(@as(i64, 100), s.records[0].execution_time_qpc);
        }
        try std.testing.expect(o.getDiagnostics().retained_cell_count <= 3);
    }
    try cs(&o, 5050, 0, 10, 20, 0, 0);
    try o.observeProcessEnd(5050, 0, 10, 0);
    try cs(&o, 5200, 0, 0, 0, 0, 0);
    var final_snapshot = try o.snapshot(a, &.{0});
    defer final_snapshot.deinit(a);
    try seed(&o, 11, 21); // Explicit identity cap can be reused after retention.
    try std.testing.expectEqual(@as(usize, 1), o.getDiagnostics().retained_thread_identity_count);
}

test "lagging processor capacity rewarms CPU and preserves pre-reset span facts" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 0, 1, 0, 0, 0, 0);
    try cs(&o, 500, 0, 10, 20, 0, 0);
    var s = try o.snapshot(a, &.{ 0, 1 });
    defer s.deinit(a);
    try std.testing.expect(!s.is_complete);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().active_pair_count);
    const reset = o.getDiagnostics().last_capacity_reset;
    try std.testing.expectEqual(@intFromEnum(residency.CapacityResetReason.bucket_retention_span), reset.reason);
    try std.testing.expectEqual(@as(i64, 500), reset.event_qpc);
    try std.testing.expectEqual(std.math.minInt(i64), reset.retained_window_start_qpc);
    try std.testing.expectEqual(@as(u64, 1), reset.active_pair_count);
    try std.testing.expectEqual(@as(u64, 32), reset.maximum_active_pairs);
    try std.testing.expectEqual(@as(u64, 4), reset.retained_bucket_slots);
    try std.testing.expectEqual(@as(u64, 128), reset.maximum_bucket_slots);
    try std.testing.expectEqual(@as(u64, 10), reset.required_bucket_count);
    try std.testing.expectEqual(@as(u64, 8), reset.maximum_bucket_count);
    try std.testing.expectEqual(@as(u64, 1), reset.current_process_count);
    try std.testing.expectEqual(@as(u64, 1), reset.current_thread_count);
}

test "configured identity processor and name capacities return errors" {
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .max_processes = 1, .max_threads = 1, .max_processors = 1, .max_name_bytes = 4 });
    defer o.deinit();
    try o.seedProcess(10, 100, "name");
    try std.testing.expectError(error.CapacityExceeded, o.seedProcess(11, 100, "x"));
    try o.seedThread(10, 20);
    try std.testing.expectError(error.CapacityExceeded, o.seedThread(10, 21));
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try std.testing.expectError(error.CapacityExceeded, cs(&o, 0, 1, 0, 0, 0, 0));
    try std.testing.expectError(error.CapacityExceeded, o.seedProcess(10, 100, "bigger"));
}

test "missing processor incomplete snapshot has no partial boundary timestamp" {
    var o = try owner();
    defer o.deinit();
    try cs(&o, 100, 0, 0, 0, 0, 0);
    var s = try o.snapshot(a, &.{ 0, 1 });
    defer s.deinit(a);
    try std.testing.expectEqual(@as(?i64, null), s.measured_through);
    try std.testing.expectEqual(@as(i64, 0), s.measured_through_qpc);
}

test "seed capacity failure invalidates a previously complete publication window" {
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 100, .bucket_ms = 50, .max_processes = 1 });
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var before = try o.snapshot(a, &.{0});
    defer before.deinit(a);
    try std.testing.expect(before.is_complete);
    try std.testing.expectError(error.CapacityExceeded, o.seedProcess(11, 200, "over cap"));
    var after = try o.snapshot(a, &.{0});
    defer after.deinit(a);
    try std.testing.expect(!after.is_complete);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().active_pair_count);
}

test "handled pair capacity retains failing switch boundary and rewarms the next interval" {
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 1000, .bucket_ms = 10, .max_pairs = 1 });
    defer o.deinit();
    try seed(&o, 100, 10);
    try seed(&o, 200, 20);
    try cs(&o, 0, 0, 0, 0, 100, 10);
    try cs(&o, 500, 0, 100, 10, 200, 20);
    try cs(&o, 1000, 0, 200, 20, 100, 10);
    var incomplete = try o.snapshot(a, &.{0});
    defer incomplete.deinit(a);
    try std.testing.expect(!incomplete.is_complete);
    try std.testing.expectEqual(@as(i64, 0), incomplete.window_start_qpc);
    try std.testing.expectEqual(@as(i64, 1000), incomplete.measured_through_qpc);
    try std.testing.expectEqual(@as(?i64, 11000), incomplete.measured_through);
    try std.testing.expectEqual(@as(usize, 0), incomplete.records.len);
    try std.testing.expectEqual(@as(i64, 1), o.getDiagnostics().continuity_epoch);
    try std.testing.expectEqual(@as(i64, 1), o.getDiagnostics().capacity_reset_count);
    const reset = o.getDiagnostics().last_capacity_reset;
    try std.testing.expectEqual(@intFromEnum(residency.CapacityResetReason.active_pair_limit), reset.reason);
    try std.testing.expectEqual(@as(i64, 1000), reset.event_qpc);
    try std.testing.expectEqual(@as(i64, 0), reset.retained_window_start_qpc);
    try std.testing.expectEqual(@as(u64, 1), reset.active_pair_count);
    try std.testing.expectEqual(@as(u64, 1), reset.maximum_active_pairs);
    try std.testing.expectEqual(@as(u64, 102), reset.retained_bucket_slots);
    try std.testing.expectEqual(@as(u64, 102), reset.maximum_bucket_slots);
    try std.testing.expectEqual(@as(u64, 102), reset.required_bucket_count);
    try std.testing.expectEqual(@as(u64, 204), reset.maximum_bucket_count);
    try std.testing.expectEqual(@as(u64, 2), reset.current_process_count);
    try std.testing.expectEqual(@as(u64, 2), reset.current_thread_count);
    try cs(&o, 2000, 0, 100, 10, 0, 0);
    var complete = try o.snapshot(a, &.{0});
    defer complete.deinit(a);
    try std.testing.expect(complete.is_complete);
    try std.testing.expectEqual(@as(usize, 1), complete.records.len);
    try std.testing.expectEqual(@as(i32, 100), complete.records[0].process_id);
    try std.testing.expectEqual(@as(i64, 1000), complete.records[0].execution_time_qpc);
    try std.testing.expectEqual(@as(i32, 1), complete.records[0].switch_count);
    try std.testing.expectEqual(reset, o.getDiagnostics().last_capacity_reset);
}

test "bucket storage and resize resets have distinct managed reasons and pre-reset facts" {
    var storage = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 100, .bucket_ms = 50, .max_pairs = 2 });
    defer storage.deinit();
    try seed(&storage, 10, 20);
    try storage.seedThread(10, 21);
    try cs(&storage, 0, 0, 0, 0, 10, 20);
    try cs(&storage, 0, 1, 0, 0, 0, 0);
    try cs(&storage, 250, 0, 10, 20, 10, 21);
    try cs(&storage, 300, 0, 10, 21, 0, 0);
    const stored = storage.getDiagnostics().last_capacity_reset;
    try std.testing.expectEqual(@intFromEnum(residency.CapacityResetReason.bucket_storage_limit), stored.reason);
    try std.testing.expectEqual(@as(i64, 300), stored.event_qpc);
    try std.testing.expectEqual(@as(u64, 1), stored.active_pair_count);
    try std.testing.expectEqual(@as(u64, 5), stored.retained_bucket_slots);
    try std.testing.expectEqual(@as(u64, 8), stored.maximum_bucket_slots);
    try std.testing.expectEqual(@as(u64, 4), stored.required_bucket_count);
    try std.testing.expectEqual(@as(u64, 2), stored.current_thread_count);
    try std.testing.expectEqual(@as(i64, 1), storage.getDiagnostics().capacity_reset_count);

    var resize = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 100, .bucket_ms = 50, .max_pairs = 1 });
    defer resize.deinit();
    try seed(&resize, 10, 20);
    try cs(&resize, 0, 0, 0, 0, 10, 20);
    try cs(&resize, 0, 1, 0, 0, 0, 0);
    try cs(&resize, 250, 0, 10, 20, 0, 0);
    const grown = resize.getDiagnostics().last_capacity_reset;
    try std.testing.expectEqual(@intFromEnum(residency.CapacityResetReason.bucket_resize_limit), grown.reason);
    try std.testing.expectEqual(@as(i64, 250), grown.event_qpc);
    try std.testing.expectEqual(@as(u64, 4), grown.retained_bucket_slots);
    try std.testing.expectEqual(@as(u64, 4), grown.maximum_bucket_slots);
    try std.testing.expectEqual(@as(u64, 5), grown.required_bucket_count);
    try std.testing.expectEqual(@as(i64, 1), resize.getDiagnostics().capacity_reset_count);
}

test "capacity-reset current identity counts exclude retired identities still retained by totals" {
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 1000, .bucket_ms = 10, .max_pairs = 1 });
    defer o.deinit();
    try seed(&o, 100, 10);
    try cs(&o, 0, 0, 0, 0, 100, 10);
    try cs(&o, 500, 0, 100, 10, 0, 0);
    try o.observeProcessEnd(500, 0, 100, 0);
    try seed(&o, 200, 20);
    try cs(&o, 500, 0, 0, 0, 200, 20);
    try cs(&o, 1000, 0, 200, 20, 0, 0);
    const reset = o.getDiagnostics().last_capacity_reset;
    try std.testing.expectEqual(@intFromEnum(residency.CapacityResetReason.active_pair_limit), reset.reason);
    try std.testing.expectEqual(@as(u64, 1), reset.current_process_count);
    try std.testing.expectEqual(@as(u64, 1), reset.current_thread_count);
    try std.testing.expectEqual(@as(u64, 2), o.last_capacity_failure.processes);
    try std.testing.expectEqual(@as(u64, 2), o.last_capacity_failure.threads);
}

test "window storage release returns its allocator bytes while run identities and published copy survive" {
    const Budget = @import("budget.zig").Budget;
    var metadata: Budget = .{ .backing = a, .cap = 1024 * 1024 };
    var window: Budget = .{ .backing = a, .cap = 1024 * 1024 };
    var next_window: Budget = .{ .backing = a, .cap = 1024 * 1024 };
    {
        var o = try Owner.initWithWindowAllocator(metadata.allocator(), window.allocator(), .{ .qpc_frequency = 1000, .window_ms = 100, .bucket_ms = 50 });
        defer o.deinit();
        try seed(&o, 10, 20);
        const inventory = o.captureInventorySeed();
        try cs(&o, 0, 0, 0, 0, 10, 20);
        try cs(&o, 100, 0, 10, 20, 0, 0);
        var published = try o.snapshot(a, &.{0});
        defer published.deinit(a);
        try std.testing.expect(published.is_complete);
        try std.testing.expect(o.getDiagnostics().retained_bucket_bytes > 0);
        try std.testing.expect(window.current > o.getDiagnostics().retained_bucket_bytes);
        const identities = metadata.current;
        try std.testing.expectError(error.WindowStorageInUse, o.setWindowAllocator(next_window.allocator()));
        try o.releaseWindowStorage();
        try std.testing.expectEqual(@as(usize, 0), window.current);
        try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().active_pair_count);
        try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().retained_bucket_bytes);
        try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().retained_cell_count);
        try std.testing.expectEqual(identities, metadata.current);
        try std.testing.expectEqual(@as(usize, 1), o.getDiagnostics().current_process_count);
        try std.testing.expectEqual(@as(usize, 1), o.getDiagnostics().current_thread_count);
        try std.testing.expectEqual(inventory, o.captureInventorySeed());
        try std.testing.expectEqualStrings("process", published.records[0].process_name);
        try std.testing.expectEqual(@as(i64, 100), published.records[0].execution_time_qpc);
        try o.setWindowAllocator(next_window.allocator());
        try o.beginWindow();
        try cs(&o, 100, 0, 0, 0, 10, 20);
        try cs(&o, 200, 0, 10, 20, 0, 0);
        var next = try o.snapshot(a, &.{0});
        defer next.deinit(a);
        try std.testing.expect(next.is_complete);
        try std.testing.expectEqual(published.records[0].thread_instance_id, next.records[0].thread_instance_id);
        try std.testing.expect(next_window.current > 0);
        try o.releaseWindowStorage();
        try std.testing.expectEqual(@as(usize, 0), next_window.current);
    }
    try std.testing.expectEqual(@as(usize, 0), metadata.current);
    try std.testing.expectEqual(@as(usize, 0), window.current);
    try std.testing.expectEqual(@as(usize, 0), next_window.current);
}

test "boundary readiness is distinct from complete coverage for the same selected CPUs" {
    var o = try owner();
    defer o.deinit();
    try std.testing.expect(!o.snapshotMetadata(&.{ 0, 1 }).boundaries_ready);
    try cs(&o, 0, 0, 0, 0, 0, 0);
    try std.testing.expect(!o.snapshotMetadata(&.{ 0, 1 }).boundaries_ready);
    try cs(&o, 0, 1, 0, 0, 0, 0);
    const started = o.snapshotMetadata(&.{ 0, 1 });
    try std.testing.expect(started.boundaries_ready);
    try std.testing.expect(!started.is_complete);
    try std.testing.expect(!o.snapshotMetadata(&.{ 0, 0 }).boundaries_ready);
    try cs(&o, 100, 0, 0, 0, 0, 0);
    try cs(&o, 100, 1, 0, 0, 0, 0);
    const complete = o.snapshotMetadata(&.{ 0, 1 });
    try std.testing.expect(complete.boundaries_ready);
    try std.testing.expect(complete.is_complete);
    try o.releaseWindowStorage();
    try std.testing.expect(!o.snapshotMetadata(&.{ 0, 1 }).boundaries_ready);
}

test "capacity-reset wire facts have stable eighty-eight byte layout" {
    try std.testing.expectEqual(@as(usize, 88), @sizeOf(residency.CapacityReset));
    try std.testing.expectEqual(@as(usize, 8), @offsetOf(residency.CapacityReset, "event_qpc"));
    try std.testing.expectEqual(@as(usize, 80), @offsetOf(residency.CapacityReset, "current_thread_count"));
}

test "positive prior-window switch does not mutate new window continuity identities or boundary" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try o.beginWindowAt(100);
    try cs(&o, 100, 0, 0, 0, 10, 20);
    try cs(&o, 200, 0, 10, 20, 0, 0);
    var closed = try o.snapshot(a, &.{0});
    defer closed.deinit(a);
    try std.testing.expect(closed.is_complete);
    try o.releaseWindowStorage();
    try o.beginWindowAt(300);
    const before = o.getDiagnostics();
    try std.testing.expectEqual(@as(usize, 1), before.current_process_count);
    try std.testing.expectEqual(@as(usize, 1), before.current_thread_count);
    try std.testing.expect(!o.contextSwitchBelongsToWindow(199));
    try cs(&o, 199, 0, 10, 20, 10, 20); // Valid, positive, inside old [100,200] window.
    const after = o.getDiagnostics();
    try std.testing.expectEqual(before.continuity_epoch, after.continuity_epoch);
    try std.testing.expectEqual(before.current_process_count, after.current_process_count);
    try std.testing.expectEqual(before.current_thread_count, after.current_thread_count);
    try std.testing.expectEqual(@as(i64, 200), o.latest_event_qpc);
    try std.testing.expectEqual(@as(usize, 0), after.active_pair_count);
    try std.testing.expect(!o.snapshotMetadata(&.{0}).boundaries_ready);
    try std.testing.expect(!o.snapshotMetadata(&.{0}).is_complete);
    try cs(&o, 300, 0, 0, 0, 10, 20);
    try std.testing.expect(o.snapshotMetadata(&.{0}).boundaries_ready);
    try std.testing.expect(!o.snapshotMetadata(&.{0}).is_complete);
}

test "native default admits thirteen thousand thread two CPU migrations while old bound rewarms" {
    const thread_count = 13165;
    {
        var config: residency.Config = .{ .qpc_frequency = 10_000_000 };
        config.max_pairs = 16384;
        var old = try Owner.init(a, config);
        defer old.deinit();
        try feedTwoCpuMigrations(&old, thread_count);
        var incomplete = try old.snapshot(a, &.{ 0, 1 });
        defer incomplete.deinit(a);
        try std.testing.expect(!incomplete.is_complete);
        try std.testing.expect(old.getDiagnostics().capacity_reset_count > 0);
        try std.testing.expectEqual(@intFromEnum(residency.CapacityResetReason.active_pair_limit), old.getDiagnostics().last_capacity_reset.reason);
        try std.testing.expectEqual(@as(u64, 16384), old.getDiagnostics().last_capacity_reset.maximum_active_pairs);
    }
    {
        var current = try Owner.init(a, .{ .qpc_frequency = 10_000_000 });
        defer current.deinit();
        try std.testing.expectEqual(@as(usize, 65536), current.getDiagnostics().maximum_active_pairs);
        try feedTwoCpuMigrations(&current, thread_count);
        var complete = try current.snapshot(a, &.{ 0, 1 });
        defer complete.deinit(a);
        try std.testing.expect(complete.is_complete);
        try std.testing.expectEqual(@as(i64, 0), current.getDiagnostics().capacity_reset_count);
        try std.testing.expectEqual(@as(usize, thread_count * 2), current.getDiagnostics().active_pair_count);
        try std.testing.expectEqual(@as(usize, thread_count * 2), complete.records.len);
        try std.testing.expectEqual(@as(usize, thread_count), current.getDiagnostics().current_thread_count);
    }
}

fn feedTwoCpuMigrations(o: *Owner, thread_count: usize) !void {
    try o.seedProcess(10, 100, "two-cpu");
    for (0..thread_count) |i| try o.seedThread(10, @intCast(i + 100));
    var previous: i32 = 0;
    for (0..thread_count) |i| {
        const next: i32 = @intCast(i + 100);
        const tick: i64 = @intCast(i * 2);
        try cs(o, tick, 0, if (previous == 0) 0 else 10, previous, 10, next);
        try cs(o, tick + 1, 1, if (previous == 0) 0 else 10, previous, 10, next);
        previous = next;
    }
    const end = o.window_qpc + 1;
    try cs(o, end, 0, 10, previous, 0, 0);
    try cs(o, end, 1, 10, previous, 0, 0);
}

fn windowAllocationFailureCase(window_allocator: std.mem.Allocator) !void {
    var o = try Owner.initWithWindowAllocator(a, window_allocator, .{ .qpc_frequency = 1000, .window_ms = 100, .bucket_ms = 50 });
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    var published = try o.snapshot(a, &.{0});
    defer published.deinit(a);
    try o.releaseWindowStorage();
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().retained_bucket_bytes);
    try std.testing.expectEqual(@as(usize, 1), o.getDiagnostics().current_thread_count);
}

test "separate window allocator failures return all window and metadata allocations" {
    try std.testing.checkAllAllocationFailures(a, windowAllocationFailureCase, .{});
}
fn allocationFailureCase(allocator: std.mem.Allocator) !void {
    var o = try Owner.init(allocator, .{ .qpc_frequency = 1000, .window_ms = 100, .bucket_ms = 50 });
    defer o.deinit();
    try o.seedProcess(10, 100, "p");
    try o.seedThread(10, 20);
    try o.observeContextSwitch(0, 0, 0, 0, 0, 10, null, 20);
    try o.observeContextSwitch(100, 0, 0, 10, 20, 0, null, 0);
    var s = try o.snapshot(allocator, &.{0});
    defer s.deinit(allocator);
}

test "retention CPU detail observes actual snapshot pair removal only after a new bucket" {
    const Profile = @import("publication_profile.zig").Profile;
    var profile: Profile = .{};
    var o = try owner();
    defer o.deinit();
    o.profile = &profile;
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    // Even a bound disabled profiler must perform no detail clock reads.
    try std.testing.expectEqual(@as(u64, 0), profile.cpu_detail.detail_clock_reads);
    try std.testing.expectEqual(@as(u64, 0), profile.cpu_detail.retention_event.calls);
    try profile.configure(true);
    try cs(&o, 250, 0, 0, 0, 10, 20);
    var snapshot = try o.snapshot(a, &.{0});
    defer snapshot.deinit(a);
    try std.testing.expect(snapshot.is_complete and snapshot.records.len == 0);
    const detail = profile.cpu_detail.retention_snapshot;
    try std.testing.expectEqual(@as(u64, 1), detail.calls);
    try std.testing.expectEqual(@as(u64, 1), detail.pairs_removed);
    try std.testing.expectEqual(@as(u64, 2), detail.cells_removed);
    try std.testing.expectEqual(@as(u64, 0), detail.pair_restarts);
    try std.testing.expectEqual(@as(u64, 1), detail.pair_entry_visits);
    try std.testing.expectEqual(@as(u64, 0), detail.cell_entry_visits);
    try std.testing.expectEqual(@as(u64, 0), profile.cpu_detail.retention_event.calls);
    try std.testing.expectEqual(@as(u64, 2), profile.cpu_detail.detail_clock_reads);
    var repeated = try o.snapshot(a, &.{0});
    defer repeated.deinit(a);
    try std.testing.expectEqual(@as(u64, 2), profile.cpu_detail.detail_clock_reads);
    try std.testing.expectEqual(@as(u64, 1), profile.cpu_detail.retention_snapshot.calls);
    try std.testing.expectEqual(@as(usize, 0), o.cell_count);
}

test "retention CPU detail separates event cell pruning and preserves exact output" {
    const Profile = @import("publication_profile.zig").Profile;
    var profile: Profile = .{};
    try profile.configure(true);
    var o = try owner();
    defer o.deinit();
    o.profile = &profile;
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 50, 0, 10, 20, 10, 20);
    try cs(&o, 100, 0, 10, 20, 0, 0);
    try cs(&o, 125, 0, 0, 0, 10, 20);
    var partial = try o.snapshot(a, &.{0});
    defer partial.deinit(a);
    const before = profile.cpu_detail.retention_event;
    const reads = profile.cpu_detail.detail_clock_reads;
    try cs(&o, 175, 0, 10, 20, 0, 0);
    const after = profile.cpu_detail.retention_event;
    try std.testing.expectEqual(before.calls + 1, after.calls);
    try std.testing.expectEqual(before.cells_removed + 1, after.cells_removed);
    try std.testing.expectEqual(before.cell_restarts, after.cell_restarts);
    try std.testing.expectEqual(before.pairs_removed, after.pairs_removed);
    try std.testing.expect(after.pair_entry_visits >= before.pair_entry_visits + 2);
    try std.testing.expect(after.cell_entry_visits >= before.cell_entry_visits + 2);
    try std.testing.expectEqual(reads + 2, profile.cpu_detail.detail_clock_reads);
    var snapshot = try o.snapshot(a, &.{0});
    defer snapshot.deinit(a);
    try std.testing.expect(snapshot.is_complete);
    try std.testing.expectEqual(@as(usize, 1), snapshot.records.len);
    try std.testing.expectEqual(@as(i64, 75), snapshot.records[0].execution_time_qpc);
    try std.testing.expectEqual(@as(i32, 1), snapshot.records[0].switch_count);
    try std.testing.expectEqual(@as(u64, 0), profile.cpu_detail.retention_snapshot.calls);
    try std.testing.expectEqual(reads + 2, profile.cpu_detail.detail_clock_reads);
}

test "retention pair entry traversal is bounded when old and live pairs coexist" {
    const pair_count = 256;
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 10000, .bucket_ms = 50, .max_pairs = 512, .max_cells = 4096, .max_threads = 512 });
    defer o.deinit();
    try o.seedProcess(10, 110, "process");
    for (0..pair_count) |i| try o.seedThread(10, @intCast(i + 1));
    // A lagging idle processor prevents event-path pruning during construction.
    try cs(&o, 0, 1, 0, 0, 0, 0);
    try cs(&o, 0, 0, 0, 0, 10, 1);
    for (0..pair_count) |i| try cs(&o, @intCast(i + 1), 0, 10, @intCast(i + 1), if (i + 1 < pair_count) 10 else 0, if (i + 1 < pair_count) @intCast(i + 2) else 0);
    // Make the first half of the actual hash traversal survive, using genuine
    // switches on existing pairs. The map itself does not grow or reorder.
    var live_threads: [pair_count / 2]i32 = undefined;
    var pairs = o.pairs.valueIterator();
    for (&live_threads) |*tid| tid.* = pairs.next().?.thread.thread_id;
    try cs(&o, 11000, 0, 0, 0, 10, live_threads[0]);
    for (live_threads, 0..) |tid, i| try cs(&o, @intCast(11001 + i), 0, 10, tid, if (i + 1 < live_threads.len) 10 else 0, if (i + 1 < live_threads.len) live_threads[i + 1] else 0);
    try cs(&o, 11128, 1, 0, 0, 0, 0);
    var profile: @import("publication_profile.zig").Profile = .{};
    try profile.configure(true);
    o.profile = &profile;
    var snapshot = try o.snapshot(a, &.{ 0, 1 });
    defer snapshot.deinit(a);
    try std.testing.expect(snapshot.is_complete);
    try std.testing.expectEqual(@as(usize, pair_count / 2), snapshot.records.len);
    for (snapshot.records) |row| {
        try std.testing.expectEqual(@as(i64, 1), row.execution_time_qpc);
        try std.testing.expectEqual(@as(i32, 1), row.switch_count);
    }
    const detail = profile.cpu_detail.retention_snapshot;
    try std.testing.expectEqual(@as(u64, pair_count / 2), detail.pairs_removed);
    try std.testing.expectEqual(@as(usize, pair_count / 2), o.pairs.count());
    try std.testing.expectEqual(@as(usize, pair_count / 2), o.cell_count);
    try std.testing.expect(detail.pair_entry_visits <= 4 * pair_count);
}

test "retention workspace allocation failure leaves cutoff and cells retryable" {
    for (0..2) |failure_offset| {
        var failing = std.testing.FailingAllocator.init(a, .{});
        var o = try Owner.init(failing.allocator(), .{ .qpc_frequency = 1000, .window_ms = 100, .bucket_ms = 50, .max_pairs = 32 });
        defer o.deinit();
        try seed(&o, 10, 20);
        // The idle second processor holds retention back during construction.
        try cs(&o, 0, 1, 0, 0, 0, 0);
        try cs(&o, 0, 0, 0, 0, 10, 20);
        try cs(&o, 100, 0, 10, 20, 0, 0);
        try cs(&o, 125, 0, 0, 0, 10, 20);
        try cs(&o, 175, 0, 10, 20, 0, 0);
        try cs(&o, 175, 1, 0, 0, 0, 0);
        const previous_start = o.retained_window_start_qpc;
        const previous_bucket = o.last_pruned_bucket;
        const previous_cells = o.cell_count;
        const previous_slots = o.retained_bucket_slots;
        failing.fail_index = failing.alloc_index + failure_offset;
        try std.testing.expectError(error.OutOfMemory, o.snapshot(a, &.{ 0, 1 }));
        try std.testing.expect(failing.has_induced_failure);
        try std.testing.expectEqual(previous_start, o.retained_window_start_qpc);
        try std.testing.expectEqual(previous_bucket, o.last_pruned_bucket);
        try std.testing.expectEqual(previous_cells, o.cell_count);
        try std.testing.expectEqual(previous_slots, o.retained_bucket_slots);
        try std.testing.expectEqual(@as(usize, 1), o.pairs.count());
        failing.fail_index = std.math.maxInt(usize);
        var retry = try o.snapshot(a, &.{ 0, 1 });
        defer retry.deinit(a);
        try std.testing.expect(retry.is_complete);
        try std.testing.expectEqual(@as(usize, 1), retry.records.len);
        try std.testing.expectEqual(@as(i64, 75), retry.records[0].execution_time_qpc);
        try std.testing.expectEqual(@as(i32, 1), retry.records[0].switch_count);
        try std.testing.expectEqual(@as(usize, 3), o.cell_count);
    }
}
