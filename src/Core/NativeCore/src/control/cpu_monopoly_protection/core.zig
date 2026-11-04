const std = @import("std");

// No allocation, threads, timer, process API or restoration. The host lends one
// bounded Ledger region for the block's run and supplies subscribed deliveries.
pub const Row = extern struct {
    key: u64 = 0,
    since: i64 = 0,
    last: i64 = 0,
    cpu: f64 = 0,
    pid: u32 = 0,
    state: u32 = 0, // 0 empty, 1 observed, 2 pending, 3 due, 4 acted, 5 explicit, 6 tombstone
    seen: u32 = 0,
    reserved: u32 = 0,
};
pub const Config = extern struct { threshold: f64, duration_ms: i64, maximum_gap_ms: i64 };
pub const Input = extern struct { key: u64, at: i64, cpu: f64, pid: u32, eligible: u32 };
pub const Result = extern struct { key: u64 = 0, elapsed_ms: i64 = 0, cpu: f64 = 0, pid: u32 = 0, flags: u32 = 0 };
pub const reached: u32 = 1;
pub const below: u32 = 2;
pub const due: u32 = 4;
pub const ended: u32 = 8;
pub const capacity: u32 = 16;

fn find(rows: []Row, pid: u32, key: u64, create: bool) ?*Row {
    if (rows.len == 0 or pid == 0 or key == 0) return null;
    const start = (key ^ (key >> 32) ^ pid) % rows.len;
    var offset: usize = 0;
    var free: ?*Row = null;
    while (offset < rows.len) : (offset += 1) {
        const row = &rows[(start + offset) % rows.len];
        if (row.state != 0 and row.state != 6 and row.pid == pid and row.key == key) return row;
        if (row.state == 6 and free == null) free = row;
        if (row.state == 0) { if (free == null) free = row; break; }
    }
    if (!create) return null;
    const row = free orelse return null;
    row.* = .{ .pid = pid, .key = key, .state = 1 };
    return row;
}
pub fn begin(rows: []Row) void {
    var i: usize = 0;
    while (i < rows.len) : (i += 1) rows[i].seen = 0;
}
pub fn unavailable(rows: []Row) void {
    var i: usize = 0;
    while (i < rows.len) : (i += 1) { if (rows[i].state == 2 or rows[i].state == 3) rows[i].state = 1; }
}
pub fn observe(rows: []Row, config: Config, input: Input) Result {
    const row = find(rows, input.pid, input.key, true) orelse return .{ .flags = capacity };
    row.seen = 1;
    if (input.at <= row.last) return .{};
    const gap = row.last != 0 and input.at - row.last > config.maximum_gap_ms;
    row.last = input.at;
    if (row.state == 4 or row.state == 5) return .{};
    const hit = input.eligible != 0 and std.math.isFinite(input.cpu) and input.cpu >= config.threshold and input.cpu <= 100;
    if (!hit) {
        const was_pending = row.state == 2 or row.state == 3;
        row.state = 1;
        return .{ .pid = input.pid, .key = input.key, .flags = if (was_pending) below else 0 };
    }
    var flags: u32 = 0;
    if (row.state == 1 or gap) { row.since = input.at; row.state = 2; flags |= reached; }
    row.cpu = input.cpu;
    if (input.at - row.since >= config.duration_ms) { row.state = 3; flags |= due; }
    return .{ .pid = input.pid, .key = input.key, .cpu = input.cpu, .elapsed_ms = input.at - row.since, .flags = flags };
}
pub fn select(rows: []Row) Result {
    var selected: ?*Row = null;
    var i: usize = 0;
    while (i < rows.len) : (i += 1) {
        const row = &rows[i];
        if (row.seen == 0 or row.state != 3) continue;
        if (selected == null or row.cpu > selected.?.cpu or (row.cpu == selected.?.cpu and row.pid < selected.?.pid)) selected = row;
    }
    const row = selected orelse return .{};
    return .{ .pid = row.pid, .key = row.key, .cpu = row.cpu, .elapsed_ms = row.last - row.since, .flags = due };
}
pub fn finish(rows: []Row, pid: u32, key: u64, success: bool) void {
    if (find(rows, pid, key, false)) |row| {
        if (row.state != 5) row.state = if (success) 4 else 1;
    }
    // Another candidate must sustain a full fresh window after this action.
    unavailable(rows);
}
pub fn explicit(rows: []Row, pid: u32, key: u64) bool {
    const row = find(rows, pid, key, true) orelse return false;
    const was_active = row.state == 4;
    row.state = 5;
    return was_active;
}
pub fn eligible(rows: []Row, pid: u32, key: u64) bool {
    const row = find(rows, pid, key, false) orelse return false;
    return row.state == 3;
}
pub fn canAutomaticWrite(rows: []Row, pid: u32, key: u64) bool {
    const row = find(rows, pid, key, false) orelse return true;
    return row.state != 3 and row.state != 4 and row.state != 5;
}
pub fn endReason(rows: []Row, pid: u32, key: u64) u32 {
    const row = find(rows, pid, key, false) orelse return 2;
    return if (row.state == 5) 1 else 0;
}
pub fn restore(rows: []Row, pid: u32, key: u64) bool {
    const row = find(rows, pid, key, true) orelse return false;
    row.state = 4;
    return true;
}
pub fn migrate(source: []Row, destination: []Row) bool {
    @memset(destination, .{});
    var i: usize = 0;
    while (i < source.len) : (i += 1) {
        const old = source[i];
        if (old.state == 0 or old.state == 6) continue;
        const row = find(destination, old.pid, old.key, true) orelse return false;
        row.* = old;
    }
    return true;
}
// One ended action per call; the caller repeats at most rows.len times.
pub fn sweep(rows: []Row) Result {
    var i: usize = 0;
    while (i < rows.len) : (i += 1) {
        const row = &rows[i];
        if (row.seen != 0 or row.state == 0 or row.state == 6) continue;
        const active = row.state == 4;
        const result: Result = .{ .pid = row.pid, .key = row.key, .flags = if (active) ended else 0 };
        row.state = 6;
        if (active) return result;
    }
    return .{};
}
