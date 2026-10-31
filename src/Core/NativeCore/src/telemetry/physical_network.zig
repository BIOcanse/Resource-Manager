const policy = @import("../configuration/NativePolicy.g.zig");
const std = @import("std");

// Windows MIB_IF_ROW2. Unused spans preserve the documented SDK layout;
// tests/Native/NetworkInterfaceLayoutTests.cpp checks it against netioapi.h.
pub const InterfaceRow = extern struct {
    luid: u64,
    before_flags: [1144]u8,
    flags: u8,
    oper_status: u32,
    before_links: [32]u8,
    transmit_link_speed: u64,
    receive_link_speed: u64,
    in_octets: u64,
    unused_in: [8]u64,
    out_octets: u64,
    unused_out: [8]u64,
};

const InterfaceTable = extern struct {
    count: u32,
    rows: [1]InterfaceRow,
};

extern "iphlpapi" fn GetIfTable2(table: *?*InterfaceTable) callconv(.winapi) u32;
extern "iphlpapi" fn FreeMibTable(table: *anyopaque) callconv(.winapi) void;

const maximum_interfaces = policy.maximum_network_interfaces;
const Counter = struct { luid: u64, receive: u64, send: u64 };
pub const Rates = struct { receive: f64 = 0, send: f64 = 0, bandwidth: f64 = 0 };

pub const Sampler = struct {
    previous: [maximum_interfaces]Counter = undefined,
    count: usize = 0,
    tick_ms: ?u64 = null,

    pub fn reset(self: *Sampler) void {
        self.count = 0;
        self.tick_ms = null;
    }

    pub fn read(self: *Sampler, now_ms: u64) ?Rates {
        var table: ?*InterfaceTable = null;
        const status = GetIfTable2(&table);
        defer if (table) |allocated| FreeMibTable(allocated);
        if (status != 0 or table == null or table.?.count > maximum_interfaces) {
            self.reset();
            return null;
        }
        const rows: [*]const InterfaceRow = @ptrCast(&table.?.rows);
        return self.collect(rows[0..table.?.count], now_ms);
    }

    fn collect(self: *Sampler, rows: []const InterfaceRow, now_ms: u64) ?Rates {
        if (rows.len > maximum_interfaces) {
            self.reset();
            return null;
        }
        const elapsed = if (self.tick_ms) |tick| now_ms -| tick else 0;
        var complete = elapsed > 0;
        var current: [maximum_interfaces]Counter = undefined;
        var count: usize = 0;
        var rates: Rates = .{};
        for (rows) |row| {
            // HardwareInterface bit 0, FilterInterface bit 1, EndPointInterface
            // bit 7; IfOperStatusUp == 1. No adapter-name heuristics.
            if ((row.flags & 0x83) != 1 or row.oper_status != 1) continue;
            const next: Counter = .{ .luid = row.luid, .receive = row.in_octets, .send = row.out_octets };
            current[count] = next;
            count += 1;
            const previous = self.find(row.luid) orelse {
                complete = false;
                continue;
            };
            if (elapsed == 0 or next.receive < previous.receive or next.send < previous.send) {
                complete = false;
                continue;
            }
            const seconds = @as(f64, @floatFromInt(elapsed)) / 1000;
            rates.receive += @as(f64, @floatFromInt(next.receive - previous.receive)) / seconds;
            rates.send += @as(f64, @floatFromInt(next.send - previous.send)) / seconds;
            // Retain the existing single-link capacity definition for utilization.
            rates.bandwidth += @floatFromInt(@max(row.receive_link_speed, row.transmit_link_speed));
        }
        @memcpy(self.previous[0..count], current[0..count]);
        self.count = count;
        self.tick_ms = now_ms;
        return if (complete) rates else null;
    }

    fn find(self: *const Sampler, luid: u64) ?Counter {
        for (self.previous[0..self.count]) |counter| {
            if (counter.luid == luid) return counter;
        }
        return null;
    }
};

fn fixtureRow(luid: u64, receive: u64, send: u64) InterfaceRow {
    var result = std.mem.zeroes(InterfaceRow);
    result.luid = luid;
    result.flags = 1;
    result.oper_status = 1;
    result.receive_link_speed = 1_000_000;
    result.transmit_link_speed = 1_000_000;
    result.in_octets = receive;
    result.out_octets = send;
    return result;
}

test "physical network ABI matches the independently checked Windows SDK layout" {
    try std.testing.expectEqual(1352, @sizeOf(InterfaceRow));
    try std.testing.expectEqual(1152, @offsetOf(InterfaceRow, "flags"));
    try std.testing.expectEqual(1156, @offsetOf(InterfaceRow, "oper_status"));
    try std.testing.expectEqual(1192, @offsetOf(InterfaceRow, "transmit_link_speed"));
    try std.testing.expectEqual(1200, @offsetOf(InterfaceRow, "receive_link_speed"));
    try std.testing.expectEqual(1208, @offsetOf(InterfaceRow, "in_octets"));
    try std.testing.expectEqual(1280, @offsetOf(InterfaceRow, "out_octets"));
    try std.testing.expectEqual(8, @offsetOf(InterfaceTable, "rows"));
}

test "physical network excludes virtual duplicate traffic and aggregates hardware LUIDs" {
    var sampler: Sampler = .{};
    var rows = [_]InterfaceRow{ fixtureRow(1, 100, 200), fixtureRow(2, 500, 600), fixtureRow(3, 100, 200) };
    rows[2].flags = 0;
    try std.testing.expect(sampler.collect(&rows, 1000) == null);
    rows[0].in_octets += 100;
    rows[0].out_octets += 200;
    rows[1].in_octets += 300;
    rows[1].out_octets += 400;
    rows[2].in_octets += 100; // Same traffic, never counted a second time.
    rows[2].out_octets += 200;
    const rates = sampler.collect(&rows, 2000).?;
    try std.testing.expectEqual(400, rates.receive);
    try std.testing.expectEqual(600, rates.send);
    try std.testing.expectEqual(2_000_000, rates.bandwidth);
    std.mem.swap(InterfaceRow, &rows[0], &rows[1]);
    rows[0].in_octets += 100;
    try std.testing.expectEqual(100, sampler.collect(&rows, 3000).?.receive);
}

test "physical network excludes filter endpoint and down interfaces" {
    var sampler: Sampler = .{};
    var rows = [_]InterfaceRow{ fixtureRow(1, 0, 0), fixtureRow(2, 0, 0), fixtureRow(3, 0, 0) };
    rows[0].flags |= 2;
    rows[1].flags |= 128;
    rows[2].oper_status = 2;
    try std.testing.expect(sampler.collect(&rows, 1000) == null);
    for (&rows) |*item| item.in_octets = 1000;
    const rates = sampler.collect(&rows, 2000).?;
    try std.testing.expectEqual(0, rates.receive);
    try std.testing.expectEqual(0, rates.bandwidth);
}

test "physical network rebases counters and preserves valid rates across long gaps" {
    var sampler: Sampler = .{};
    var rows = [_]InterfaceRow{fixtureRow(1, 1000, 1000)};
    try std.testing.expect(sampler.collect(&rows, 1000) == null);
    rows[0].in_octets = 10;
    try std.testing.expect(sampler.collect(&rows, 2000) == null);
    rows[0].in_octets = 30;
    try std.testing.expectEqual(20, sampler.collect(&rows, 3000).?.receive);
    rows[0].luid = 2;
    try std.testing.expect(sampler.collect(&rows, 4000) == null);
    rows[0].in_octets = 40;
    try std.testing.expectEqual(10, sampler.collect(&rows, 5000).?.receive);
    try std.testing.expectEqual(0, sampler.collect(&rows, 11000).?.receive);
    try std.testing.expect(sampler.collect(&rows, 11000) == null);
    sampler.reset();
    try std.testing.expect(sampler.collect(&rows, 12000) == null);
    try std.testing.expectEqual(0, sampler.collect(&rows, 13000).?.receive);
}

test "physical network reads the real Windows interface table without configuration writes" {
    var sampler: Sampler = .{};
    try std.testing.expect(sampler.read(1000) == null);
    try std.testing.expect(sampler.tick_ms != null);
    const rates = sampler.read(2000).?;
    try std.testing.expect(std.math.isFinite(rates.receive));
    try std.testing.expect(std.math.isFinite(rates.send));
    try std.testing.expect(rates.bandwidth >= 0);
}
