// Windows PDH facts projected from the existing collector's committed frame.
// No sampler, clock, thread, lock, retained address or independent baseline.
// All variable-sized storage is borrowed from the caller's Ledger workspace.
const std = @import("std");
const pdh = @import("../../../telemetry/pdh_collector.zig");

pub const Identity = extern struct { pid: i32, reserved: u32 = 0, expected: i64, current: i64 };
pub const Row = extern struct {
    luid: u64, start: u64 = 0, pid: u32 = 0, physical: u32 = 0,
    engine: u32 = 0, class: u32 = 0, kind: u32, reserved: u32 = 0, value: f64,
};
pub const Input = extern struct {
    size: u32 = @sizeOf(Input), version: u32 = 1,
    engines: ?[*]const pdh.GpuEngineRow, engine_count: usize,
    windows: ?[*]const pdh.GpuEngineWindow, window_count: usize,
    memory: ?[*]const pdh.GpuMemoryRow, memory_count: usize,
    identities: ?[*]const Identity, identity_count: usize,
    // Bits: scheduling, breakdown, device, dedicated memory, validated breakdown.
    flags: u32, reserved: u32 = 0,
};
pub const Output = extern struct {
    rows: ?[*]const Row = null, count: usize = 0, workspace_used: usize = 0,
};

const Engine = struct { luid: u64, pid: u32, physical: u32, engine: u32, class: u32, value: f64, order: usize };
const Memory = struct { luid: u64, pid: u32, value: f64 };
const Process = struct { pid: u32, start: u64 };
const Choice = struct { luid: u64, physical: u32, engine: u32, usage: f64 };
fn pidValid(pid: u32) bool { return pid > 0 and pid <= std.math.maxInt(i32); }
fn clean(value: f64) f64 { return if (std.math.isFinite(value) and value >= 0) value else 0; }
fn percent(value: f64) f64 { return @min(100, clean(value)); }
fn sameEngine(a: anytype, b: anytype) bool { return a.luid == b.luid and a.physical == b.physical and a.engine == b.engine; }
fn engineLess(_: void, a: Engine, b: Engine) bool {
    if (a.luid != b.luid) return a.luid < b.luid;
    if (a.physical != b.physical) return a.physical < b.physical;
    if (a.engine != b.engine) return a.engine < b.engine;
    return a.order < b.order;
}
fn processLess(_: void, a: Process, b: Process) bool { return a.pid < b.pid; }
fn memoryLess(_: void, a: Memory, b: Memory) bool { return if (a.luid != b.luid) a.luid < b.luid else a.pid < b.pid; }
fn rowLess(_: void, a: Row, b: Row) bool {
    if (a.kind != b.kind) return a.kind < b.kind;
    if (a.luid != b.luid) return a.luid < b.luid;
    if (a.pid != b.pid) return a.pid < b.pid;
    if (a.physical != b.physical) return a.physical < b.physical;
    return a.engine < b.engine;
}
fn startOf(processes: []const Process, pid: u32) ?u64 {
    var low: usize = 0;
    var high = processes.len;
    while (low < high) {
        const middle = low + (high - low) / 2;
        if (processes[middle].pid < pid) low = middle + 1 else high = middle;
    }
    return if (low < processes.len and processes[low].pid == pid) processes[low].start else null;
}
fn choiceOf(choices: []const Choice, luid: u64) ?Choice {
    var low: usize = 0;
    var high = choices.len;
    while (low < high) {
        const middle = low + (high - low) / 2;
        if (choices[middle].luid < luid) low = middle + 1 else high = middle;
    }
    return if (low < choices.len and choices[low].luid == luid) choices[low] else null;
}
fn windowValid(windows: []const pdh.GpuEngineWindow, row: Engine, start: u64) bool {
    var count: usize = 0;
    for (windows) |window| {
        if (window.adapter_luid == row.luid and window.process_id == row.pid and
            window.physical_index == row.physical and window.engine_index == row.engine and
            window.begin_filetime >= 0 and @as(u64, @intCast(window.begin_filetime)) >= start and
            window.end_filetime > window.begin_filetime and window.begin_counter > 0 and
            window.end_counter > window.begin_counter and window.counter_frequency > 0) count += 1;
    }
    return count == 1;
}

// A conservative exact upper bound from supplied cardinalities, not a policy cap.
pub fn required(input: *const Input) ?usize {
    const engine_bytes = std.math.mul(usize, input.engine_count, @sizeOf(Engine) + @sizeOf(Choice) + 12 * @sizeOf(Row)) catch return null;
    const memory_bytes = std.math.mul(usize, input.memory_count, @sizeOf(Memory) + 4 * @sizeOf(Row)) catch return null;
    const identity_bytes = std.math.mul(usize, input.identity_count, @sizeOf(Process)) catch return null;
    const sum = std.math.add(usize, engine_bytes, memory_bytes) catch return null;
    return std.math.add(usize, std.math.add(usize, sum, identity_bytes) catch return null, 6 * @alignOf(Row)) catch null;
}

pub fn project(input: *const Input, workspace: []u8, output: *Output) u32 {
    output.* = .{};
    if (input.size != @sizeOf(Input) or input.version != 1 or input.flags & ~@as(u32, 31) != 0 or
        (input.engines == null and input.engine_count != 0) or (input.windows == null and input.window_count != 0) or
        (input.memory == null and input.memory_count != 0) or (input.identities == null and input.identity_count != 0)) return 1;
    const needed = required(input) orelse return 1;
    if (workspace.len < needed) return 5;
    var region = std.heap.FixedBufferAllocator.init(workspace);
    const allocator = region.allocator();
    const engines = allocator.alloc(Engine, input.engine_count) catch return 5;
    const memories = allocator.alloc(Memory, input.memory_count) catch return 5;
    const processes = allocator.alloc(Process, input.identity_count) catch return 5;
    const choices = allocator.alloc(Choice, input.engine_count) catch return 5;
    const capacity = std.math.add(usize, std.math.mul(usize, input.engine_count, 6) catch return 1,
        std.math.mul(usize, input.memory_count, 2) catch return 1) catch return 1;
    const rows = allocator.alloc(Row, capacity) catch return 5;
    const selected_storage = allocator.alloc(Row, capacity) catch return 5;
    var count: usize = 0;
    var process_count: usize = 0;
    if (input.identities) |identities| for (identities[0..input.identity_count]) |identity| {
        // Invalid entries do not make a valid expected instance ambiguous.
        if (identity.pid <= 0 or identity.expected <= 0) continue;
        processes[process_count] = .{ .pid = @intCast(identity.pid),
            .start = if (identity.current == identity.expected) @intCast(identity.expected) else 0 };
        process_count += 1;
    };
    std.sort.heap(Process, processes[0..process_count], {}, processLess);
    var unique: usize = 0;
    var i: usize = 0;
    while (i < process_count) {
        var end = i + 1;
        while (end < process_count and processes[end].pid == processes[i].pid) : (end += 1) {}
        if (end == i + 1 and processes[i].start != 0) { processes[unique] = processes[i]; unique += 1; }
        i = end;
    }
    const stable = processes[0..unique];
    const windows: []const pdh.GpuEngineWindow = if (input.windows) |p| p[0..input.window_count] else &.{};
    if (input.engines) |source| for (source[0..input.engine_count], 0..) |row, index| {
        engines[index] = .{ .luid = row.adapter_luid, .pid = row.process_id, .physical = row.physical_index,
            .engine = row.engine_index, .class = row.engine_class, .value = row.usage_percent, .order = index };
    };
    std.sort.heap(Engine, engines, {}, engineLess);
    // Engine order is LUID / physical / engine. Keep the first engine on a
    // capped tie, without rescanning the entire output for every process row.
    var choice_count: usize = 0;
    i = 0;
    while (i < engines.len) {
        const first = engines[i];
        var end = i + 1;
        while (end < engines.len and sameEngine(first, engines[end])) : (end += 1) {}
        var total: f64 = 0;
        for (engines[i..end]) |row| total += percent(row.value);
        total = @min(100, total);
        const choice = Choice{ .luid = first.luid, .physical = first.physical, .engine = first.engine, .usage = total };
        if (choice_count == 0 or choices[choice_count - 1].luid != first.luid) {
            choices[choice_count] = choice; choice_count += 1;
        } else if (total > choices[choice_count - 1].usage) choices[choice_count - 1] = choice;
        i = end;
    }
    i = 0;
    while (i < engines.len) {
        const first = engines[i];
        const selected_engine = choiceOf(choices[0..choice_count], first.luid).?;
        const selected = selected_engine.physical == first.physical and selected_engine.engine == first.engine;
        var end = i + 1;
        while (end < engines.len and sameEngine(first, engines[end])) : (end += 1) {}
        var total: f64 = 0;
        var raw_total: f64 = 0;
        for (engines[i..end]) |row| { total += percent(row.value); raw_total += clean(row.value); }
        total = @min(100, total);
        if (input.flags & 6 != 0 and selected) {
            rows[count] = .{ .luid = first.luid, .physical = first.physical, .engine = first.engine, .kind = 5, .value = total };
            count += 1;
            rows[count] = .{ .luid = first.luid, .physical = first.physical, .engine = first.engine, .kind = 10, .value = @min(100, raw_total) };
            count += 1;
        }
        if (input.flags & 4 != 0) {
            rows[count] = .{ .luid = first.luid, .kind = switch (first.class) { 1 => 7, 2 => 8, else => 9 }, .value = @min(100, raw_total) };
            count += 1;
            // Exact class aggregates exclude non-finite rows, matching PDH's
            // existing distinct exact-engine contract.
            std.sort.heap(Engine, engines[i..end], {}, struct {
                fn less(_: void, a: Engine, b: Engine) bool { return if (a.class != b.class) a.class < b.class else a.order < b.order; }
            }.less);
            var begin = i;
            while (begin < end) {
                const row = engines[begin];
                var stop = begin + 1;
                while (stop < end and engines[stop].class == row.class) : (stop += 1) {}
                var sum: f64 = 0;
                var finite = false;
                for (engines[begin..stop]) |part| if (std.math.isFinite(part.value)) { finite = true; sum += percent(part.value); };
                if (finite) {
                    rows[count] = .{ .luid = row.luid, .physical = row.physical, .engine = row.engine,
                        .class = row.class, .kind = 6, .value = @min(100, sum) }; count += 1;
                }
                begin = stop;
            }
        }
        // Sort just this engine by PID while preserving the engine's first-row
        // classification above. Heap sort has an explicit bound and no recursion.
        std.sort.heap(Engine, engines[i..end], {}, struct {
            fn less(_: void, a: Engine, b: Engine) bool { return if (a.pid != b.pid) a.pid < b.pid else a.order < b.order; }
        }.less);
        var begin = i;
        while (begin < end) {
            const row = engines[begin];
            var stop = begin + 1;
            while (stop < end and engines[stop].pid == row.pid) : (stop += 1) {}
            var sum: f64 = 0;
            var raw_sum: f64 = 0;
            for (engines[begin..stop]) |part| { sum += percent(part.value); raw_sum += clean(part.value); }
            const start = startOf(stable, row.pid);
            const accepted = start != null and windowValid(windows, row, start.?);
            if (input.flags & 1 != 0 and accepted) {
                rows[count] = .{ .luid = row.luid, .pid = row.pid, .start = start.?, .kind = 1, .value = @min(100, sum) };
                count += 1;
            }
            if (input.flags & 2 != 0 and selected and pidValid(row.pid) and (input.flags & 16 == 0 or accepted) and raw_sum > 0) {
                rows[count] = .{ .luid = row.luid, .pid = row.pid, .physical = row.physical, .engine = row.engine,
                    .kind = 3, .value = @min(100, raw_sum) };
                count += 1;
            }
            begin = stop;
        }
        i = end;
    }
    if (input.memory) |source| for (source[0..input.memory_count], 0..) |row, index| {
        memories[index] = .{ .luid = row.adapter_luid, .pid = row.process_id, .value = row.dedicated_bytes };
    };
    std.sort.heap(Memory, memories, {}, memoryLess);
    i = 0;
    while (i < memories.len) {
        const first = memories[i];
        var end = i + 1;
        while (end < memories.len and memories[end].luid == first.luid and memories[end].pid == first.pid) : (end += 1) {}
        var sum: f64 = 0;
        for (memories[i..end]) |row| sum += clean(row.value);
        if (pidValid(first.pid)) {
            if (input.flags & 9 == 9) if (startOf(stable, first.pid)) |start| {
                rows[count] = .{ .luid = first.luid, .pid = first.pid, .start = start, .kind = 2, .value = sum };
                count += 1;
            };
            if (input.flags & 2 != 0 and sum > 0) {
                rows[count] = .{ .luid = first.luid, .pid = first.pid, .kind = 4, .value = @min(std.math.floatMax(f64), sum) };
                count += 1;
            }
        }
        i = end;
    }
    @memcpy(selected_storage[0..count], rows[0..count]);
    const selected_rows = selected_storage[0..count];
    std.sort.heap(Row, selected_rows, {}, rowLess);
    // Consolidate per-process max across engines and specialized maxima.
    var out_count: usize = 0;
    for (selected_rows) |value| {
        if (out_count > 0) {
            const previous = &selected_rows[out_count - 1];
            if (value.kind == previous.kind and value.luid == previous.luid and value.pid == previous.pid and
                (value.kind == 1 or (value.kind >= 7 and value.kind <= 9))) {
                previous.value = @max(previous.value, value.value);
                continue;
            }
        }
        selected_rows[out_count] = value;
        out_count += 1;
    }
    output.* = .{ .rows = selected_rows.ptr, .count = out_count, .workspace_used = region.end_index };
    return 0;
}

pub const Name = extern struct { text: ?[*]align(1) const u16, length: usize };
pub const Formatted = extern struct { name: Name, status: u32, reserved: u32 = 0, value: i64 };
pub const SharedRow = extern struct { luid: u64, pid: u32, failed: u32 = 0, bytes: u64 = 0 };
pub const SharedResult = extern struct {
    rows: ?[*]const SharedRow = null, count: usize = 0, failures: u32 = 0, native_status: u32 = 0,
    // Product result codes: Ok=0, NoData=4, PdhError=8.
    result: u32 = 4, flags: u32 = 0, // bit 0: no adapter requested
};
fn equalAscii(a: []align(1) const u16, b: []const u8) bool {
    if (a.len != b.len) return false;
    for (a, b) |wide, narrow| if (wide > 127 or std.ascii.toLower(@intCast(wide)) != std.ascii.toLower(narrow)) return false;
    return true;
}
fn equalName(a: Name, b: Name) bool {
    if (a.length != b.length) return false;
    const aa = (a.text orelse return false)[0..a.length];
    const bb = (b.text orelse return false)[0..b.length];
    for (aa, bb) |x, y| if (x != y and (x > 127 or y > 127 or std.ascii.toLower(@as(u8, @intCast(x))) != std.ascii.toLower(@as(u8, @intCast(y))))) return false;
    return true;
}
fn number(text: []align(1) const u16, base: u8) ?u32 {
    if (text.len == 0) return null;
    var result: u32 = 0;
    for (text) |character| {
        if (character > 127) return null;
        const digit = std.fmt.charToDigit(@intCast(character), base) catch return null;
        result = std.math.add(u32, std.math.mul(u32, result, base) catch return null, digit) catch return null;
    }
    return result;
}
pub fn parse(name: Name, process: bool, pid: *u32, luid: *u64) bool {
    pid.* = 0; luid.* = 0;
    var text = (name.text orelse return false)[0..name.length];
    var parsed_pid: u32 = 0;
    if (process) {
        if (text.len < 4 or !equalAscii(text[0..4], "pid_")) return false;
        text = text[4..];
        var index: usize = 0;
        while (index < text.len and text[index] != '_') : (index += 1) {}
        if (index == text.len) return false;
        parsed_pid = number(text[0..index], 10) orelse return false;
        if (!pidValid(parsed_pid)) return false;
        text = text[index + 1 ..];
    }
    if (text.len < 33 or !equalAscii(text[0..7], "luid_0x") or !equalAscii(text[15..18], "_0x") or !equalAscii(text[26..32], "_phys_")) return false;
    const high = number(text[7..15], 16) orelse return false;
    const low = number(text[18..26], 16) orelse return false;
    _ = number(text[32..], 10) orelse return false;
    const packed_luid = (@as(u64, high) << 32) | low;
    if (packed_luid == 0) return false;
    pid.* = parsed_pid; luid.* = packed_luid; return true;
}

// Aggregates one original formatted array. Selection is injected; no PDH query
// is opened by this pure transformation. Adapter ambiguity is resolved by the
// caller before presenting the requested LUID set.
pub fn shared(values: []const Formatted, requested: []const u64, process: bool, rows: []SharedRow, output: *SharedResult) u32 {
    output.* = .{};
    if (rows.len < values.len) return 5;
    var count: usize = 0;
    var failures: u32 = 0;
    var status: u32 = 0;
    for (values, 0..) |item, index| {
        var pid: u32 = 0; var luid: u64 = 0;
        if (!parse(item.name, process, &pid, &luid) or std.mem.indexOfScalar(u64, requested, luid) == null) continue;
        var key: usize = 0;
        while (key < count and !(rows[key].luid == luid and rows[key].pid == pid)) : (key += 1) {}
        if (key == count) { rows[count] = .{ .luid = luid, .pid = pid }; count += 1; }
        var duplicate = false;
        for (values[0..index]) |prior| if (equalName(prior.name, item.name)) { duplicate = true; break; };
        const invalid_status = item.status != 0 and item.status != 1;
        if (duplicate or invalid_status or item.value < 0) {
            rows[key].failed = 1; failures += 1;
            if ((process and invalid_status) or (!process and !duplicate)) status = item.status;
            continue;
        }
        const sum = @addWithOverflow(rows[key].bytes, @as(u64, @intCast(item.value)));
        if (sum[1] != 0) { rows[key].failed = 1; failures += 1; } else rows[key].bytes = sum[0];
    }
    var written: usize = 0;
    for (rows[0..count]) |row| if (row.failed == 0) { rows[written] = row; written += 1; };
    output.* = .{ .rows = rows.ptr, .count = written, .failures = failures, .native_status = status,
        .result = if (written != 0) 0 else if (failures != 0) 8 else 4 };
    return 0;
}

pub const DecoderLimits = extern struct { bytes: u32, items: u32, name_characters: u32, reserved: u32 = 0 };
// Name pointers remain borrowed references into the supplied original buffer.
pub fn decode(buffer: ?[*]const u8, allocated: u32, returned: u32, count: u32, limits: DecoderLimits, rows: []Formatted) bool {
    const source = buffer orelse return false;
    if (allocated > limits.bytes or returned > allocated or count > limits.items or rows.len < count or limits.name_characters == 0) return false;
    const array_bytes = std.math.mul(u32, count, 24) catch return false;
    if (array_bytes > returned) return false;
    const address = @intFromPtr(source);
    _ = std.math.add(usize, address, returned) catch return false;
    for (rows[0..count], 0..) |*row, index| {
        const item = source[index * 24 ..][0..24];
        const name_address = std.mem.readInt(u64, item[0..8], .little);
        if (name_address < address or name_address - address < array_bytes or name_address - address >= returned) return false;
        const offset: usize = @intCast(name_address - address);
        const maximum = @min((returned - offset) / 2, limits.name_characters);
        var length: usize = 0;
        while (length < maximum and std.mem.readInt(u16, source[offset + length * 2 ..][0..2], .little) != 0) : (length += 1) {}
        if (length == 0 or length == maximum) return false;
        row.* = .{ .name = .{ .text = @ptrCast(source + offset), .length = length },
            .status = std.mem.readInt(u32, item[8..12], .little), .value = std.mem.readInt(i64, item[16..24], .little) };
    }
    return true;
}
