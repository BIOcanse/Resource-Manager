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

test "lagging processor capacity is an explicit continuity error" {
    var o = try owner();
    defer o.deinit();
    try seed(&o, 10, 20);
    try cs(&o, 0, 0, 0, 0, 10, 20);
    try cs(&o, 0, 1, 0, 0, 0, 0);
    try std.testing.expectError(error.CapacityExceeded, cs(&o, 500, 0, 10, 20, 0, 0));
    var s = try o.snapshot(a, &.{ 0, 1 });
    defer s.deinit(a);
    try std.testing.expect(!s.is_complete);
    try std.testing.expectEqual(@as(usize, 0), o.getDiagnostics().active_pair_count);
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

test "pair capacity error retains failing switch boundary and rewarms the next interval" {
    var o = try Owner.init(a, .{ .qpc_frequency = 1000, .window_ms = 1000, .bucket_ms = 10, .max_pairs = 1 });
    defer o.deinit();
    try seed(&o, 100, 10);
    try seed(&o, 200, 20);
    try cs(&o, 0, 0, 0, 0, 100, 10);
    try cs(&o, 500, 0, 100, 10, 200, 20);
    try std.testing.expectError(error.CapacityExceeded, cs(&o, 1000, 0, 200, 20, 100, 10));
    var incomplete = try o.snapshot(a, &.{0});
    defer incomplete.deinit(a);
    try std.testing.expect(!incomplete.is_complete);
    try std.testing.expectEqual(@as(i64, 0), incomplete.window_start_qpc);
    try std.testing.expectEqual(@as(i64, 1000), incomplete.measured_through_qpc);
    try std.testing.expectEqual(@as(?i64, 11000), incomplete.measured_through);
    try std.testing.expectEqual(@as(usize, 0), incomplete.records.len);
    try std.testing.expectEqual(@as(i64, 1), o.getDiagnostics().continuity_epoch);
    try std.testing.expectEqual(@as(i64, 1), o.getDiagnostics().capacity_reset_count);
    try cs(&o, 2000, 0, 100, 10, 0, 0);
    var complete = try o.snapshot(a, &.{0});
    defer complete.deinit(a);
    try std.testing.expect(complete.is_complete);
    try std.testing.expectEqual(@as(usize, 1), complete.records.len);
    try std.testing.expectEqual(@as(i32, 100), complete.records[0].process_id);
    try std.testing.expectEqual(@as(i64, 1000), complete.records[0].execution_time_qpc);
    try std.testing.expectEqual(@as(i32, 1), complete.records[0].switch_count);
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
