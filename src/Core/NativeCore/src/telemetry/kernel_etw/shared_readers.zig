const std = @import("std");
const capture = @import("abi.zig");
const p = @import("protocol.zig");
const irq = @import("interrupt_abi.zig");
const interrupts = @import("interrupts.zig");
pub const diagnostics = @import("thread_diagnostics.zig");

/// Mutable reader configuration belongs to the ProcessTrace consumer. The
/// shared owner's cold command queue is the only entry from other threads.
/// Publication storage belongs to the shared owner's leased publication bank.
pub const State = struct {
    allocator: std.mem.Allocator,
    capture: *capture.Session,
    interrupts: ?*irq.Session,
    processor_ids: []i32,
    thread_diagnostics: diagnostics.Owner,
    residency_enabled: bool = true,

    pub fn init(allocator: std.mem.Allocator, owner: *capture.Session, interrupt_owner: *irq.Session, ids: []const i32) !State {
        var self: State = .{ .allocator = allocator, .capture = owner, .interrupts = interrupt_owner, .processor_ids = &.{}, .thread_diagnostics = try diagnostics.Owner.init(allocator, diagnostics.windows, @intCast(owner.residency.config.max_threads), @intCast(owner.residency.config.max_processes), interrupt_owner.owner.config.max_path_units, interrupt_owner.owner.config.max_modules, owner.budget.cap) };
        errdefer self.thread_diagnostics.deinit();
        try self.configureProcessors(ids);
        return self;
    }

    /// CPU diagnostics own no IRQ state. The IRQ output is independently held.
    pub fn initCpu(allocator: std.mem.Allocator, owner: *capture.Session, irq_config: irq.Config, ids: []const i32) !State {
        var self: State = .{ .allocator = allocator, .capture = owner, .interrupts = null, .processor_ids = &.{}, .thread_diagnostics = try diagnostics.Owner.init(allocator, diagnostics.windows, @intCast(owner.residency.config.max_threads), @intCast(owner.residency.config.max_processes), irq_config.max_path_units, irq_config.max_modules, owner.budget.cap) };
        errdefer self.thread_diagnostics.deinit();
        try self.configureProcessors(ids);
        return self;
    }

    pub fn deinit(self: *State) void {
        self.allocator.free(self.processor_ids);
        self.thread_diagnostics.deinit();
        self.* = undefined;
    }

    /// The residency owner validates and stores the expected set (resetting
    /// continuity); this copy resolves interrupt processor indexes.
    pub fn configureProcessors(self: *State, ids: []const i32) !void {
        if (ids.len > self.capture.residency.config.max_processors) return error.InvalidConfig;
        const replacement = try self.allocator.dupe(i32, ids);
        errdefer self.allocator.free(replacement);
        try self.capture.residency.setProcessors(ids);
        self.allocator.free(self.processor_ids);
        self.processor_ids = replacement;
    }

    pub fn resolveProcessor(self: *const State, index: i32) ?i32 {
        if (index < 0 or @as(usize, @intCast(index)) >= self.processor_ids.len) return null;
        return self.processor_ids[@intCast(index)];
    }

    pub fn apply(self: *State, input: *const p.Input) !void {
        if (input.reserved != 0 or input.reserved2 != 0 or input.flags & ~@as(u32, 1) != 0) return error.InvalidInput;
        if (input.kind == 9) {
            if (input.qpc < 0) return error.InvalidInput;
            try self.capture.residency.beginWindowAt(input.qpc, input.unique);
            return;
        }
        if (input.kind == 13) {
            if (input.pid <= 0) return error.InvalidInput;
            try self.thread_diagnostics.register(input.unique, @intCast(input.pid), input.start, input.time_ticks);
            self.bindDiagnosticInstances();
            return;
        }
        if (input.kind == 14) {
            self.thread_diagnostics.remove(input.unique);
            return;
        }
        if (input.kind == 19) {
            _ = try self.thread_diagnostics.sample(input.unique, input.time_ticks, input.qpc);
            self.bindDiagnosticInstances();
            return;
        }
        if (input.kind == 15) {
            if (input.qpc < 0) return error.InvalidInput;
            if (self.interrupts) |target| target.owner.reset(input.qpc, self.capture.events_lost);
            return;
        }
        if (input.kind == 16) {
            if (input.time_ticks != 0) return error.InvalidInput;
            try self.capture.residency.configureWindow(input.qpc, std.math.cast(usize, input.unique) orelse return error.InvalidInput);
            return;
        }
        if (input.kind == 11 or input.kind == 12) {
            if (input.pid <= 0 or input.start <= 0 or input.qpc < 0 or (input.kind == 12 and input.tid <= 0)) return error.InvalidInput;
            if (@as(u64, @intCast(input.qpc)) != self.capture.events_lost) return;
            const name: ?[]const u8 = if (input.name_len == 0) null else (input.name orelse return error.InvalidInput)[0..input.name_len];
            const seed = self.capture.residency.captureInventorySeed();
            const process = (try self.capture.residency.seedInventoryProcess(seed, input.pid, input.start, name)) orelse return;
            if (input.kind == 12) try self.capture.residency.seedInventoryThread(process, input.tid);
            return;
        }
        try self.capture.apply(input);
    }

    pub fn observeThread(self: *State, pid: u32, tid: u32, ended: bool, event_filetime: i64) !void {
        const thread = self.capture.residency.current_threads.get(@bitCast(tid));
        if (!ended) if (thread) |value| {
            value.observed_filetime = event_filetime;
        };
        try self.thread_diagnostics.observeThread(pid, tid, if (thread) |value| value.instance_id else 0, ended, event_filetime);
    }
    fn bindDiagnosticInstances(self: *State) void {
        for (self.thread_diagnostics.requests.items, 0..) |*request, request_index| {
            var index: usize = 0;
            while (index < request.entries.items.len) {
                const entry = &request.entries.items[index];
                if (entry.instance != 0 or entry.ended) { index += 1; continue; }
                const thread = self.capture.residency.current_threads.get(@bitCast(entry.tid)) orelse { index += 1; continue; };
                // A failed open can use the inventory birth only as a matching hint.
                // No verified creation/start/counter fact is produced from that hint.
                const birth_hint = if (entry.birth > 0) entry.birth else entry.expected_birth;
                if (birth_hint <= 0 or thread.process.process_id != @as(i32, @bitCast(request.pid)) or thread.observed_filetime < birth_hint) {
                    index += 1; continue;
                }
                var already_bound: ?usize = null;
                for (request.entries.items, 0..) |other, other_index| if (other.instance == thread.instance_id) {
                    already_bound = other_index; break;
                };
                if (already_bound) |other_index| {
                    if (self.thread_diagnostics.mergeErrorRetry(request_index, other_index, index)) {
                        index = 0; // swapRemove can move a still-unbound entry into an earlier position.
                        continue;
                    }
                } else entry.instance = thread.instance_id;
                index += 1;
            }
        }
    }
    pub fn publishDiagnostics(self: *State, output: std.mem.Allocator) !diagnostics.View {
        self.bindDiagnosticInstances();
        return try self.thread_diagnostics.publish(output);
    }

    pub fn setResidencyEnabled(self: *State, enabled: bool) !void {
        if (enabled == self.residency_enabled) return;
        if (enabled) try self.capture.residency.resetContinuity();
        self.residency_enabled = enabled;
    }

    pub fn publishResidency(self: *State, output: std.mem.Allocator, temporary: std.mem.Allocator) !p.View {
        const profiling = self.capture.residency.profile;
        var snapshot = build_snapshot: {
            const snapshot_started = if (profiling) |profile| profile.detailStart() else null;
            defer if (profiling) |profile| profile.finishCpuPart(snapshot_started, &profile.cpu_detail.native_snapshot);
            break :build_snapshot try self.capture.residency.snapshotBorrowedNames(temporary);
        };
        const started = if (profiling) |profile| profile.detailStart() else null;
        defer if (profiling) |profile| profile.finishCpuPart(started, &profile.cpu_detail.abi_conversion);
        defer snapshot.deinit(temporary);
        const rows = try output.alloc(p.Row, snapshot.records.len);
        errdefer output.free(rows);
        var names: std.AutoHashMapUnmanaged(i64, []const u8) = .empty;
        defer names.deinit(temporary);
        errdefer {
            var values = names.valueIterator();
            while (values.next()) |value| output.free(value.*);
        }
        for (snapshot.records, rows) |row, *out| {
            const name = names.get(row.process_instance_id) orelse copy_name: {
                const copied = try output.dupe(u8, row.process_name);
                names.put(temporary, row.process_instance_id, copied) catch |err| {
                    output.free(copied);
                    return err;
                };
                break :copy_name copied;
            };
            out.* = .{
                .process_instance = row.process_instance_id,
                .pid = row.process_id,
                .flags = if (row.process_start_key != null) 1 else 0,
                .start = row.process_start_key orelse 0,
                .thread_instance = row.thread_instance_id,
                .tid = row.thread_id,
                .processor = row.logical_processor_id,
                .duration = row.execution_time_qpc,
                .switches = row.switch_count,
                .name_len = @intCast(name.len),
                .name = name.ptr,
            };
        }
        return .{ .frequency = snapshot.qpc_frequency, .start_qpc = snapshot.window_start_qpc, .through_qpc = snapshot.measured_through_qpc, .time_ticks = snapshot.measured_through orelse 0, .flags = @as(u32, if (snapshot.is_complete) 1 else 0) | @as(u32, if (snapshot.measured_through != null) 2 else 0) | @as(u32, if (snapshot.boundaries_ready) 4 else 0), .count = @intCast(rows.len), .rows = rows.ptr, .current_bytes = self.capture.budget.current, .peak_bytes = self.capture.budget.peak };
    }

    pub fn publishInterrupts(self: *State, output: std.mem.Allocator, now_tick: i64, processor_count: u32) !irq.View {
        return publishInterruptOwner(self.interrupts orelse return error.NoPublication, output, now_tick, processor_count);
    }
    pub fn publishInterruptOwner(owner: *irq.Session, output: std.mem.Allocator, now_tick: i64, processor_count: u32) !irq.View {
        // This is also called by the owner's timer when no events arrive, so
        // complete ten-second buckets age out independently of IRQ traffic.
        const snapshot = try owner.owner.publish(output, now_tick, processor_count);
        const drivers = try output.alloc(irq.Driver, snapshot.drivers.len);
        const buckets = try output.alloc(irq.Bucket, snapshot.bucket_count);
        for (snapshot.drivers, drivers) |d, *row| row.* = .{ .module = identity(d.module), .total_duration_ms = d.total_duration_ms, .maximum_single_duration_ms = d.maximum_single_duration_ms, .cpu_capacity_percent = d.cpu_capacity_percent, .event_count = d.event_count, .long_event_count = d.long_event_count };
        for (snapshot.buckets[0..snapshot.bucket_count], buckets) |b, *row| row.* = .{ .started_tick_ms = b.started_tick_ms, .ended_tick_ms = b.ended_tick_ms, .total_duration_ms = b.total_duration_ms, .cpu_capacity_percent = b.cpu_capacity_percent, .event_count = b.event_count };
        return .{ .generation = snapshot.generation, .events_lost = snapshot.events_lost, .started_tick_ms = snapshot.started_tick_ms, .ended_tick_ms = snapshot.ended_tick_ms, .window_ms = snapshot.window_ms, .processor_count = snapshot.processor_count, .flags = if (snapshot.available) 1 else 0, .total_duration_ms = snapshot.total_duration_ms, .maximum_single_duration_ms = snapshot.maximum_single_duration_ms, .cpu_capacity_percent = snapshot.cpu_capacity_percent, .event_count = snapshot.event_count, .long_event_count = snapshot.long_event_count, .maximum_kind = if (snapshot.maximum_kind) |kind| @intFromEnum(kind) else 0, .maximum_module = identity(snapshot.maximum_module), .driver_count = @intCast(drivers.len), .bucket_count = @intCast(buckets.len), .drivers = drivers.ptr, .buckets = buckets.ptr, .current_bytes = owner.budget.current, .peak_bytes = owner.budget.peak };
    }
};

fn identity(value: interrupts.Identity) irq.Identity {
    return .{ .path = if (value.path) |path| path.ptr else null, .path_len = if (value.path) |path| @intCast(path.len) else 0, .name = if (value.name) |name| name.ptr else null, .name_len = if (value.name) |name| @intCast(name.len) else 0 };
}

test "shared reader publications retain rows and age idle interrupt windows" {
    const cfg: p.Config = .{ .qpc_frequency = 1000, .window_ms = 1000, .max_pairs = 64, .max_processes = 64, .max_threads = 128, .max_processors = 8, .max_name_bytes = 4096, .byte_cap = 1024 * 1024 };
    const owner = try capture.Session.create(std.testing.allocator, &cfg);
    defer owner.destroy();
    const irq_cfg: irq.Config = .{ .max_modules = 8, .max_path_units = 128, .max_identities = 16, .max_slices = 64, .max_slice_drivers = 128, .byte_cap = 1024 * 1024, .generation = 7, .started_tick_ms = 0, .events_lost = 0 };
    const interrupt_owner = try irq.Session.create(std.testing.allocator, &irq_cfg);
    defer interrupt_owner.destroy();
    var state = try State.init(std.testing.allocator, owner, interrupt_owner, &.{4});
    defer state.deinit();
    try std.testing.expectEqual(@as(?i32, 4), state.resolveProcessor(0));
    try std.testing.expectEqual(@as(?i32, null), state.resolveProcessor(1));
    try std.testing.expectError(error.InvalidConfig, state.configureProcessors(&.{ 4, 4 }));
    try std.testing.expectEqual(@as(?i32, 4), state.resolveProcessor(0));
    try state.apply(&.{ .kind = 1, .pid = 1, .flags = 1, .start = 10, .name = "p".ptr, .name_len = 1 });
    try state.apply(&.{ .kind = 2, .pid = 1, .tid = 2 });
    try state.apply(&.{ .kind = 7, .pid = 1, .tid = 2, .processor = 4 });
    try state.apply(&.{ .kind = 7, .old_pid = 1, .old_tid = 2, .processor = 4, .qpc = 1000, .time_ticks = 1000 });
    var bank = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer bank.deinit();
    var temporary = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer temporary.deinit();
    const residency = try state.publishResidency(bank.allocator(), temporary.allocator());
    _ = temporary.reset(.free_all);
    try std.testing.expectEqual(@as(u32, 1), residency.count);
    try std.testing.expectEqual(@as(i64, 1000), residency.rows.?[0].duration);
    try state.setResidencyEnabled(false);
    try state.setResidencyEnabled(true);
    try std.testing.expectEqual(@as(i64, 1000), residency.rows.?[0].duration);
    try std.testing.expectEqualStrings("p", residency.rows.?[0].name.?[0..residency.rows.?[0].name_len]);
    try std.testing.expectError(error.InvalidInput, state.apply(&.{ .kind = 16, .qpc = 2000, .time_ticks = 100, .unique = 32 }));
    try state.apply(&.{ .kind = 16, .qpc = 2000, .unique = 32 });
    try std.testing.expectEqual(@as(i64, 2000), owner.residency.window_qpc);
    try std.testing.expectEqual(@as(usize, 1), owner.residency.current_threads.count());
    try std.testing.expectEqual(@as(i64, 1000), residency.rows.?[0].duration);
    const path = std.unicode.utf8ToUtf16LeStringLiteral("driver.sys");
    try interrupt_owner.owner.upsertImage(100, 10, path, 0);
    try interrupt_owner.owner.observe(.isr, 1, 100, 0);
    const first = try state.publishInterrupts(bank.allocator(), 10000, 1);
    try std.testing.expectEqual(@as(u64, 1), first.event_count);
    const idle = try state.publishInterrupts(bank.allocator(), 70000, 1);
    try std.testing.expectEqual(@as(u64, 0), idle.event_count);
    try std.testing.expectEqual(@as(u64, 1), first.event_count);
    try state.apply(&.{ .kind = 15, .qpc = 70000 });
    try std.testing.expectEqual(@as(i64, 70000), interrupt_owner.owner.started_tick_ms);
    try std.testing.expectEqual(@as(?interrupts.Identity, null), interrupt_owner.owner.modules.resolve(100));
    try interrupt_owner.owner.upsertImage(100, 10, path, 70000);
    try std.testing.expect(interrupt_owner.owner.modules.resolve(100) != null);
    _ = interrupt_owner.owner.refreshLoss(70000, 1);
    try std.testing.expectEqual(@as(?interrupts.Identity, null), interrupt_owner.owner.modules.resolve(100));
}
test "residency bank stores only final rows and one name per process instance" {
    const Budget = @import("budget.zig").Budget;
    const cfg: p.Config = .{ .qpc_frequency = 1000, .window_ms = 1000, .max_pairs = 512, .max_processes = 8, .max_threads = 512, .max_processors = 1, .max_name_bytes = 4096, .byte_cap = 2 * 1024 * 1024 };
    const owner = try capture.Session.create(std.testing.allocator, &cfg);
    defer owner.destroy();
    const irq_cfg: irq.Config = .{ .max_modules = 8, .max_path_units = 128, .max_identities = 16, .max_slices = 8, .max_slice_drivers = 16, .byte_cap = 1024 * 1024, .generation = 1, .started_tick_ms = 0, .events_lost = 0 };
    const interrupt_owner = try irq.Session.create(std.testing.allocator, &irq_cfg);
    defer interrupt_owner.destroy();
    var state = try State.init(std.testing.allocator, owner, interrupt_owner, &.{0});
    defer state.deinit();
    const name = @as([512]u8, @splat('p'));
    try state.apply(&.{ .kind = 1, .pid = 1, .flags = 1, .start = 10, .name = &name, .name_len = name.len });
    for (0..256) |index| try state.apply(&.{ .kind = 2, .pid = 1, .tid = @intCast(index + 1) });
    for (0..256) |index| try state.apply(&.{ .kind = 7, .pid = 1, .tid = @intCast(index + 1), .old_pid = if (index == 0) 0 else 1, .old_tid = @intCast(index), .processor = 0, .qpc = @intCast(index * 3), .time_ticks = @intCast(1000000 + index * 3) });
    try state.apply(&.{ .kind = 7, .old_pid = 1, .old_tid = 256, .processor = 0, .qpc = 768, .time_ticks = 1000768 });
    try state.apply(&.{ .kind = 7, .processor = 0, .qpc = 1000, .time_ticks = 1001000 });
    var output_budget: Budget = .{ .backing = std.testing.allocator, .cap = 64 * 1024 };
    var bank = std.heap.ArenaAllocator.init(output_budget.allocator());
    defer bank.deinit();
    // Final p.Rows occupy 16 KiB and the one shared name 512 bytes. A native
    // intermediate snapshot and 256 name copies do not fit this output cap.
    // One 256-row Record span fits; 256 duplicate 512-byte names and
    // abandoned geometric Record buffers do not belong in this scratch cap.
    var temporary_budget: Budget = .{ .backing = std.testing.allocator, .cap = 64 * 1024 };
    var temporary = std.heap.ArenaAllocator.init(temporary_budget.allocator());
    defer temporary.deinit();
    const view = try state.publishResidency(bank.allocator(), temporary.allocator());
    _ = temporary.reset(.free_all);
    try std.testing.expectEqual(@as(u32, 256), view.count);
    try std.testing.expect(view.flags & 1 != 0);
    for (view.rows.?[0..view.count]) |row| {
        try std.testing.expectEqual(@as(i64, 3), row.duration);
        try std.testing.expectEqualStrings(&name, row.name.?[0..row.name_len]);
        try std.testing.expectEqual(view.rows.?[0].name, row.name);
    }
    try std.testing.expectEqual(@as(u64, 0), output_budget.failures);
    try std.testing.expect(output_budget.current < 64 * 1024);
    try state.apply(&.{ .kind = 10 });
    try std.testing.expectEqualStrings(&name, view.rows.?[0].name.?[0..view.rows.?[0].name_len]);
}

test "residency publication reuses fragmented region islands with retained banks" {
    const cfg: p.Config = .{ .qpc_frequency = 1000, .window_ms = 1000, .max_pairs = 512, .max_processes = 8, .max_threads = 512, .max_processors = 1, .max_name_bytes = 4096, .byte_cap = 2 * 1024 * 1024 };
    const owner = try capture.Session.create(std.testing.allocator, &cfg);
    defer owner.destroy();
    const irq_cfg: irq.Config = .{ .max_modules = 8, .max_path_units = 128, .max_identities = 16, .max_slices = 8, .max_slice_drivers = 16, .byte_cap = 1024 * 1024, .generation = 1, .started_tick_ms = 0, .events_lost = 0 };
    const interrupt_owner = try irq.Session.create(std.testing.allocator, &irq_cfg);
    defer interrupt_owner.destroy();
    var state = try State.init(std.testing.allocator, owner, interrupt_owner, &.{0});
    defer state.deinit();
    const name = @as([512]u8, @splat('p'));
    try state.apply(&.{ .kind = 1, .pid = 1, .flags = 1, .start = 10, .name = &name, .name_len = name.len });
    for (0..256) |index| try state.apply(&.{ .kind = 2, .pid = 1, .tid = @intCast(index + 1) });
    for (0..256) |index| try state.apply(&.{ .kind = 7, .pid = 1, .tid = @intCast(index + 1), .old_pid = if (index == 0) 0 else 1, .old_tid = @intCast(index), .processor = 0, .qpc = @intCast(index * 3), .time_ticks = @intCast(1000000 + index * 3) });
    try state.apply(&.{ .kind = 7, .old_pid = 1, .old_tid = 256, .processor = 0, .qpc = 768, .time_ticks = 1000768 });
    try state.apply(&.{ .kind = 7, .processor = 0, .qpc = 1000, .time_ticks = 1001000 });

    const Region = @import("region_allocator.zig").RegionAllocator;
    const backing = try std.testing.allocator.alloc(u8, 256 * 1024);
    defer std.testing.allocator.free(backing);
    var region = try Region.init(backing);
    defer region.deinit();
    const region_allocator = region.allocator();
    var holes: [5][]u8 = undefined;
    var pins: [5][]u8 = undefined;
    for (&holes, &pins) |*hole, *pin| {
        hole.* = try region_allocator.alloc(u8, 48 * 1024);
        pin.* = try region_allocator.alloc(u8, 64);
    }
    defer for (pins) |pin| region_allocator.free(pin);
    for (holes) |hole| region_allocator.free(hole);
    // Total free space exceeds 240 KiB, but none of the five islands can
    // service the large geometric request caused by intermediate name copies.
    var temporary = std.heap.ArenaAllocator.init(region_allocator);
    defer temporary.deinit();
    var banks: [2]std.heap.ArenaAllocator = .{
        std.heap.ArenaAllocator.init(region_allocator),
        std.heap.ArenaAllocator.init(region_allocator),
    };
    defer for (&banks) |*bank| bank.deinit();
    var previous: ?p.View = null;
    for (0..8) |iteration| {
        const bank = &banks[iteration % banks.len];
        _ = bank.reset(.free_all);
        const view = try state.publishResidency(bank.allocator(), temporary.allocator());
        try std.testing.expect(temporary.reset(.retain_capacity));
        try std.testing.expectEqual(@as(u32, 256), view.count);
        for (view.rows.?[0..view.count]) |row| {
            try std.testing.expectEqual(@as(i64, 3), row.duration);
            try std.testing.expectEqualStrings(&name, row.name.?[0..row.name_len]);
        }
        if (previous) |held| {
            try std.testing.expectEqual(@as(i64, 3), held.rows.?[0].duration);
            try std.testing.expectEqualStrings(&name, held.rows.?[0].name.?[0..held.rows.?[0].name_len]);
        }
        previous = view;
    }
    try state.apply(&.{ .kind = 10 });
    const held = previous.?;
    try std.testing.expectEqualStrings(&name, held.rows.?[0].name.?[0..held.rows.?[0].name_len]);
    try std.testing.expectEqual(@as(u64, 0), region.failures);
}
