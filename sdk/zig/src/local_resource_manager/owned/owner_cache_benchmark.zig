//! Reusable round1/round2 evidence fixture; never imported by production code.
//! Run one profile per process. Copy THIS file and its root unchanged into the
//! frozen source tree. --cache legacy leaves Config defaults intact; enabled
//! explicitly enables the candidate and is rejected by a frozen old Config.
//! Each sample includes rendezvous, byte reads, use/end, writer attrs, overlap Tick,
//! quiescent Tick (including cache retirement), and the controlled window pause.
//! Setup fills the real C2 index to capacity. Only two 64-byte payloads have
//! physical pages; cold entries have real zero-byte lifecycle registrations.
const std = @import("std");
const builtin = @import("builtin");
const uid_module = @import("../uid.zig");
const index_module = @import("index.zig");
const owner_module = @import("owner.zig");
const notifications = @import("notifications.zig");
const types = @import("types.zig");

extern "kernel32" fn Sleep(milliseconds: u32) callconv(.winapi) void;
const max_readers = 8;
const fixed_number: u64 = 0x1000_0000_0000_0001;
const dynamic_number: u64 = 0x8000_0000_0000_0001;
const Cache = enum { legacy, enabled };
const Path = enum { fixed_direct, fixed_uid, dynamic_uid };
const Frequency = enum { low, hot };
const Options = struct {
    uid_bits: u16 = 128,
    capacity: usize = 1024,
    readers: usize = 1,
    cache: Cache = .legacy,
    path: Path = .dynamic_uid,
    frequency: Frequency = .hot,
    samples: usize = 5,
    windows: usize = 16,
    hot_repeats: usize = 4096,
    pause_ms: u32 = 1,
    wait_timeout_ms: u32 = 30_000,
};
const Counts = struct {
    uses: u64 = 0,
    ends: u64 = 0,
    bytes_read: u64 = 0,
    checksum: u64 = 0,
    notresident: u64 = 0,
    loading: u64 = 0,
    busy: u64 = 0,
    errors: u64 = 0,
    calls_started_during_writer: u64 = 0,
    repeat_calls_started_during_writer: u64 = 0,
    hot_loop_ns: u64 = 0,
    first_error: ?[]const u8 = null,
    fn fail(self: *@This(), err: anyerror) void {
        self.errors += 1;
        if (self.first_error == null) self.first_error = @errorName(err);
    }
};
const Sample = struct {
    wall_ns: u64 = 0,
    writer_attr_ns: u64 = 0,
    writer_overlap_tick_ns: u64 = 0,
    writer_retirement_tick_ns: u64 = 0,
    writer_updates: u64 = 0,
    ticks: u64 = 0,
    tick_scanned: u64 = 0,
    tick_released: u64 = 0,
    windows_with_all_readers_at_start_barrier: u64 = 0,
    windows_with_all_first_leases_active: u64 = 0,
    writer: Counts = .{},
    readers: [max_readers]Counts = @splat(.{}),
    completed: bool = false,
};

fn key(comptime Uid: type, number: u64) Uid {
    var uid = Uid{ .bytes = @splat(0) };
    std.mem.writeInt(u64, uid.bytes[uid.bytes.len - 8 ..][0..8], number, .big);
    return uid;
}
fn attrs() types.Attributes {
    return .{ .recoverability = .recoverable, .recovery_cost_coefficient = 1, .access_loss_impact = .unobservable_now };
}
fn benchmarkPolicy() types.Policy {
    // Keep native payloads resident: capacity trigger and idle are explicitly
    // disabled in BOTH sources. Tick still executes its normal owner work.
    return .{ .score_epochs = 8, .idle_epochs = 0, .protection = .concentrated, .concentrated_trigger_percent = 0, .concentrated_target_percent = 100, .smooth_trigger_percent = 20, .smooth_emergency_percent = 10, .smooth_interval_epochs = 1, .smooth_release_count = 1, .tick_maximum_actions = 1, .release_check = .{ .enabled = false, .tolerance_parts_per_million = 0, .tolerance_bytes = 0 } };
}
fn configuration(comptime bits: u16, comptime capacity: usize, comptime enabled: bool) types.Config {
    const legacy = types.Config{ .uid_width = bits, .activity_width = 8, .index_buckets = @intCast(capacity * 2) };
    // Evidence-only compatibility for the frozen BEFORE source, never a
    // production compatibility layer. All other legacy defaults are untouched.
    if (comptime @hasField(types.Config, "use_window_cache")) {
        if (enabled) {
            var config = legacy;
            config.use_window_cache = true;
            config.use_window_cache_slots = 64;
            return config;
        }
    }
    return legacy;
}
fn LedgerType(comptime bits: u16, comptime capacity: usize, comptime enabled: bool) type {
    const Uid = uid_module.Identifier(bits);
    const Index = index_module.Candidate(Uid, .c2_chain);
    const tables = [_]types.Table{.{ .capacity = @intCast(capacity), .partition_capacity = 0, .reserved_start = 0, .reserved_count = 0, .attributes = attrs(), .policy = benchmarkPolicy() }};
    const fixed = [_]types.For(Uid).Fixed{.{ .table = 0, .parent = null, .ordinal = 0, .uid = key(Uid, fixed_number), .allocation_bytes = 64, .attributes = null }};
    return owner_module.Owner(configuration(bits, capacity, enabled), &tables, &fixed, Index);
}
fn elapsed(start: i96, finish: i96) u64 {
    return if (finish <= start) 0 else @intCast(finish - start);
}

fn Driver(comptime bits: u16, comptime capacity: usize, comptime enabled: bool) type {
    const Ledger = LedgerType(bits, capacity, enabled);
    const Uid = Ledger.uid_type;
    const Index = index_module.Candidate(Uid, .c2_chain);
    return struct {
        const Self = @This();
        ledger: *Ledger,
        io: std.Io,
        options: Options,
        result: *Sample,
        ready: std.atomic.Value(u32) = .init(0),
        start: std.atomic.Value(bool) = .init(false),
        cancel: std.atomic.Value(bool) = .init(false),
        stages: [max_readers]std.atomic.Value(u32) = @splat(.init(0)),
        first_leases: [max_readers]std.atomic.Value(u32) = @splat(.init(0)),
        go: std.atomic.Value(u32) = .init(0),
        release_first: std.atomic.Value(u32) = .init(0),
        writer_done: std.atomic.Value(u32) = .init(0),
        retired: std.atomic.Value(u32) = .init(0),
        writer_active: std.atomic.Value(bool) = .init(false),

        fn now(self: *Self) i96 {
            return std.Io.Clock.awake.now(self.io).nanoseconds;
        }
        fn waitAtLeast(self: *Self, value: *std.atomic.Value(u32), wanted: u32) bool {
            const began = self.now();
            while (value.load(.acquire) < wanted) {
                if (self.cancel.load(.acquire)) return false;
                if (elapsed(began, self.now()) > @as(u64, self.options.wait_timeout_ms) * 1_000_000) {
                    self.cancel.store(true, .release);
                    return false;
                }
                // Bounded low-duty wait; no background CPU soak or thread pool.
                Sleep(1);
            }
            return !self.cancel.load(.acquire);
        }
        fn awaitStart(self: *Self) bool {
            _ = self.ready.fetchAdd(1, .release);
            const began = self.now();
            while (!self.start.load(.acquire)) {
                if (self.cancel.load(.acquire)) return false;
                if (elapsed(began, self.now()) > @as(u64, self.options.wait_timeout_ms) * 1_000_000) {
                    self.cancel.store(true, .release);
                    return false;
                }
                Sleep(1);
            }
            return !self.cancel.load(.acquire);
        }
        fn target(self: *Self) Uid {
            return key(Uid, if (self.options.path == .dynamic_uid) dynamic_number else fixed_number);
        }
        fn borrow(self: *Self, counts: *Counts) !Ledger.api.Borrow {
            if (self.writer_active.load(.acquire)) counts.calls_started_during_writer += 1;
            const value = if (self.options.path == .fixed_direct)
                try self.ledger.useFixed(0)
            else
                try self.ledger.use(self.target());
            return switch (value) {
                .in_memory => |result| blk: {
                    counts.uses += 1;
                    errdefer self.end(result.lease, counts) catch {};
                    if (result.region.len != 64) return error.UnexpectedPayloadLength;
                    // Actual native payload load; immutable after setup. The
                    // checksum depends on the read value, not pointer arithmetic.
                    const payload: *const volatile u8 = &result.region[0];
                    const byte = payload.*;
                    counts.bytes_read += 1;
                    counts.checksum +%= byte;
                    if (byte != 0xa7) return error.PayloadCanaryMismatch;
                    break :blk result;
                },
                .not_in_memory => blk: {
                    counts.notresident += 1;
                    break :blk error.UnexpectedNotResident;
                },
                .loading => blk: {
                    counts.loading += 1;
                    break :blk error.UnexpectedLoading;
                },
            };
        }
        fn end(self: *Self, lease: Ledger.api.Lease, counts: *Counts) !void {
            if (try self.ledger.endUse(lease, .complete) != .completed) return error.UnexpectedForcedReturn;
            counts.ends += 1;
        }
        fn reader(self: *Self, id: usize) void {
            const counts = &self.result.readers[id];
            if (!self.awaitStart()) {
                counts.fail(error.StartCancelled);
                return;
            }
            const repeats = if (self.options.frequency == .low) 1 else self.options.hot_repeats;
            var window_index: u32 = 0;
            while (window_index < self.options.windows) : (window_index += 1) {
                if (!self.waitAtLeast(&self.retired, window_index)) {
                    counts.fail(error.WindowCancelled);
                    return;
                }
                self.stages[id].store(window_index * 2 + 1, .release);
                if (!self.waitAtLeast(&self.go, window_index + 1)) {
                    counts.fail(error.WriterCancelled);
                    return;
                }
                // One short local first call proves lifetime overlap with the
                // writer attr mutation. Its lease ends BEFORE repeated calls;
                // it never spans or protects the independent hot repeat loop.
                const first = self.borrow(counts) catch |err| {
                    counts.fail(err);
                    self.cancel.store(true, .release);
                    return;
                };
                self.first_leases[id].store(window_index + 1, .release);
                const first_released = self.waitAtLeast(&self.release_first, window_index + 1);
                self.end(first.lease, counts) catch |err| {
                    counts.fail(err);
                    self.cancel.store(true, .release);
                };
                if (!first_released) {
                    counts.fail(error.WriterCancelled);
                    return;
                }
                // Independent local use/end scopes; there is no outer lease.
                const repeated_began = self.now();
                var repeat: usize = 1;
                while (repeat < repeats and !self.cancel.load(.acquire)) : (repeat += 1) {
                    if (self.writer_active.load(.acquire)) counts.repeat_calls_started_during_writer += 1;
                    const current = self.borrow(counts) catch |err| {
                        counts.fail(err);
                        self.cancel.store(true, .release);
                        break;
                    };
                    self.end(current.lease, counts) catch |err| {
                        counts.fail(err);
                        self.cancel.store(true, .release);
                        break;
                    };
                }
                counts.hot_loop_ns += elapsed(repeated_began, self.now());
                const finished = self.waitAtLeast(&self.writer_done, window_index + 1);
                self.stages[id].store(window_index * 2 + 2, .release);
                if (!finished) {
                    counts.fail(error.WriterCancelled);
                    return;
                }
            }
        }
        fn tick(self: *Self, retirement: bool) void {
            const work = emptyWorkspace();
            var workspaces = [_]Ledger.Workspace{work};
            const began = self.now();
            const tick_result = self.ledger.tick(&workspaces) catch |err| {
                self.result.writer.fail(err);
                self.cancel.store(true, .release);
                return;
            };
            const ns = elapsed(began, self.now());
            if (retirement) self.result.writer_retirement_tick_ns += ns else self.result.writer_overlap_tick_ns += ns;
            self.result.ticks += 1;
            self.result.tick_scanned += tick_result.scanned;
            self.result.tick_released += tick_result.released;
            if (tick_result.released != 0) {
                self.result.writer.fail(error.UnexpectedPayloadRelease);
                self.cancel.store(true, .release);
            }
        }
        fn writer(self: *Self) void {
            if (!self.awaitStart()) {
                self.result.writer.fail(error.StartCancelled);
                return;
            }
            var window_index: u32 = 0;
            while (window_index < self.options.windows) : (window_index += 1) {
                var id: usize = 0;
                while (id < self.options.readers) : (id += 1) {
                    if (!self.waitAtLeast(&self.stages[id], window_index * 2 + 1)) {
                        self.result.writer.fail(error.ReaderCancelled);
                        return;
                    }
                }
                self.result.windows_with_all_readers_at_start_barrier += 1;
                self.writer_active.store(true, .release);
                self.go.store(window_index + 1, .release);
                id = 0;
                while (id < self.options.readers) : (id += 1) {
                    if (!self.waitAtLeast(&self.first_leases[id], window_index + 1)) {
                        self.result.writer.fail(error.ReaderCancelled);
                        return;
                    }
                }
                self.result.windows_with_all_first_leases_active += 1;
                const began = self.now();
                const changed = self.ledger.updateUid(self.target(), .{ .attributes = .{
                    .recovery_cost_coefficient = 1 + window_index % 2,
                } }) catch |err| {
                    self.result.writer.fail(err);
                    self.cancel.store(true, .release);
                    return;
                };
                switch (changed) {
                    .updated => self.result.writer_updates += 1,
                    .in_use => self.result.writer.busy += 1,
                    else => {
                        self.result.writer.fail(error.UnexpectedAttributeUpdate);
                        self.cancel.store(true, .release);
                        return;
                    },
                }
                self.result.writer_attr_ns += elapsed(began, self.now());
                self.release_first.store(window_index + 1, .release);
                // Fixed matched writer work extends into independent short calls,
                // rather than time only one mostly-empty Tick after rendezvous.
                var change_number: u32 = 1;
                while (change_number < 16) : (change_number += 1) {
                    Sleep(0); // bounded scheduling yield; charged to wall time
                    const changed_at = self.now();
                    const extra = self.ledger.updateUid(self.target(), .{ .attributes = .{
                        .recovery_cost_coefficient = 1 + (window_index + change_number) % 2,
                    } }) catch |err| {
                        self.result.writer.fail(err);
                        self.cancel.store(true, .release);
                        return;
                    };
                    switch (extra) {
                        .updated => self.result.writer_updates += 1,
                        .in_use => self.result.writer.busy += 1,
                        else => {
                            self.result.writer.fail(error.UnexpectedAttributeUpdate);
                            self.cancel.store(true, .release);
                            return;
                        },
                    }
                    self.result.writer_attr_ns += elapsed(changed_at, self.now());
                }
                self.tick(false);
                self.writer_active.store(false, .release);
                self.writer_done.store(window_index + 1, .release);
                id = 0;
                while (id < self.options.readers) : (id += 1) {
                    if (!self.waitAtLeast(&self.stages[id], window_index * 2 + 2)) {
                        self.result.writer.fail(error.ReaderCancelled);
                        return;
                    }
                }
                // No logical users remain. This Tick charges retirement to the
                // same measured sample, rather than hiding it in teardown.
                self.tick(true);
                if (self.options.pause_ms != 0) Sleep(self.options.pause_ms);
                self.retired.store(window_index + 1, .release);
                if (self.cancel.load(.acquire)) return;
            }
        }
        fn emptyWorkspace() Ledger.Workspace {
            return .{ .candidates = &.{}, .rows = &.{}, .shells = &.{}, .homes = &.{}, .notes = &.{} };
        }
        fn sample(self: *Self) !void {
            var threads: [max_readers + 1]std.Thread = undefined;
            var spawned: usize = 0;
            errdefer {
                self.cancel.store(true, .release);
                self.start.store(true, .release);
                while (spawned != 0) {
                    spawned -= 1;
                    threads[spawned].join();
                }
            }
            threads[spawned] = try std.Thread.spawn(.{ .stack_size = 512 * 1024 }, writer, .{self});
            spawned += 1;
            var id: usize = 0;
            while (id < self.options.readers) : (id += 1) {
                threads[spawned] = try std.Thread.spawn(.{ .stack_size = 512 * 1024 }, reader, .{ self, id });
                spawned += 1;
            }
            if (!self.waitAtLeast(&self.ready, @intCast(spawned))) return error.StartBarrierTimeout;
            const began = self.now();
            self.start.store(true, .release);
            while (spawned != 0) {
                spawned -= 1;
                threads[spawned].join();
            }
            self.result.wall_ns = elapsed(began, self.now());
            self.result.completed = !self.cancel.load(.acquire) and self.result.writer.errors == 0;
            const expected: u64 = @as(u64, @intCast(self.options.windows)) *
                @as(u64, @intCast(if (self.options.frequency == .low) 1 else self.options.hot_repeats));
            for (self.result.readers[0..self.options.readers]) |*counts| {
                if (counts.uses != expected or counts.ends != expected or counts.bytes_read != expected or
                    counts.checksum != expected * 0xa7) counts.fail(error.ReaderCountMismatch);
                if (counts.errors != 0) self.result.completed = false;
            }
            if (self.result.writer_updates != self.options.windows * 16 or self.result.ticks != self.options.windows * 2 or
                self.result.windows_with_all_readers_at_start_barrier != self.options.windows or
                self.result.windows_with_all_first_leases_active != self.options.windows)
            {
                self.result.writer.fail(error.WriterCountMismatch);
                self.result.completed = false;
            }
        }

        pub fn run(allocator: std.mem.Allocator, io: std.Io, options: Options) !void {
            const rows = try allocator.alloc(Ledger.Row, capacity);
            defer allocator.free(rows);
            const tables = try allocator.alloc(Ledger.TableState, 1);
            defer allocator.free(tables);
            const bindings = try allocator.alloc(Ledger.FixedBinding, 1);
            defer allocator.free(bindings);
            const nodes = try allocator.alloc(Index.Node, capacity);
            defer allocator.free(nodes);
            const homes = try allocator.alloc(Index.Home, capacity * 2);
            defer allocator.free(homes);
            var subscriber_rows = [_][]notifications.Notifier(Uid).Record{&.{}};
            var ledger: Ledger = undefined;
            try Ledger.init(&ledger, rows, &.{}, tables, bindings, nodes, homes, &subscriber_rows);
            // Caller quiescence is proven by joining every fixture thread.
            // Teardown is outside timing and no owner API follows it.
            defer for (rows) |*row| row.resource.forceRelease() catch @panic("fixture cleanup failed");
            var work = emptyWorkspace();
            const payload_uids = [_]Uid{ key(Uid, fixed_number), key(Uid, dynamic_number) };
            for (payload_uids) |uid| {
                const pushed = try ledger.push(.{ .table = 0 }, uid, 64, &work);
                const fresh = switch (pushed) {
                    .newly_allocated => |value| value,
                    else => return error.UnexpectedSeedState,
                };
                @memset(fresh.region, 0xa7);
                if (try ledger.endUse(fresh.lease, .complete) != .completed) return error.SeedEndFailed;
            }
            const Resource = @TypeOf(rows[0].resource);
            var slot: usize = 2;
            while (slot < capacity) : (slot += 1) {
                const uid = key(Uid, dynamic_number + @as(u64, @intCast(slot)));
                const row = &rows[slot];
                row.uid = uid;
                row.resource = try Resource.dynamic(0);
                const allocation_number = try ledger.numbers.take();
                const fresh = (try row.resource.push(1, allocation_number)).fresh;
                if (try row.resource.endUse(fresh.lease, .complete, 1) != .completed) return error.ColdSeedEndFailed;
                try ledger.index.insert(@intCast(slot), uid, .{ .kind = .resource, .table = 0, .slot = @intCast(slot) });
            }
            const samples = try allocator.alloc(Sample, options.samples);
            defer allocator.free(samples);
            for (samples, 0..) |*result, sample_index| {
                result.* = .{};
                var driver = Self{ .ledger = &ledger, .io = io, .options = options, .result = result };
                driver.sample() catch |err| {
                    result.writer.fail(err);
                    try emit(allocator, io, options, samples[0 .. sample_index + 1]);
                    return err;
                };
                // Preserve the failed sample and stop; do not feed a damaged
                // fixture into another sample or silently discard evidence.
                if (!result.completed) {
                    try emit(allocator, io, options, samples[0 .. sample_index + 1]);
                    return error.SampleFailed;
                }
            }
            try emit(allocator, io, options, samples);
        }
        fn emit(allocator: std.mem.Allocator, io: std.Io, options: Options, samples: []const Sample) !void {
            const metadata_bytes = @sizeOf(Ledger) + capacity * @sizeOf(Ledger.Row) + @sizeOf(Ledger.TableState) +
                @sizeOf(Ledger.FixedBinding) + capacity * @sizeOf(Index.Node) + capacity * 2 * @sizeOf(Index.Home);
            const record = .{
                .schema = "resource-manager/owner-window-cache-contention/v1",
                .uid_width_bits = bits,
                .capacity = capacity,
                .indexed_entries = capacity,
                .cache = @tagName(options.cache),
                .cache_config_available = @hasField(types.Config, "use_window_cache"),
                .cache_slots = if (enabled) @as(u32, 64) else @as(u32, 0),
                .path = @tagName(options.path),
                .frequency = @tagName(options.frequency),
                .reader_count = options.readers,
                .window_count = options.windows,
                .calls_per_reader_window = if (options.frequency == .low) 1 else options.hot_repeats,
                .window_pause_ms = options.pause_ms,
                .wait_timeout_ms = options.wait_timeout_ms,
                .build_mode = @tagName(builtin.mode),
                .analysis_enabled = false,
                .row_bytes = @sizeOf(Ledger.Row),
                .owner_bytes = @sizeOf(Ledger),
                .legacy_row_bytes = @sizeOf(LedgerType(bits, capacity, false).Row),
                .legacy_owner_bytes = @sizeOf(LedgerType(bits, capacity, false)),
                .index_node_bytes = @sizeOf(Index.Node),
                .index_home_bytes = @sizeOf(Index.Home),
                .metadata_array_and_owner_bytes = metadata_bytes,
                .sample_array_bytes = samples.len * @sizeOf(Sample),
                .resident_fixture_bytes_excluding_stacks_allocator_and_json = metadata_bytes + samples.len * @sizeOf(Sample) + 2 * std.heap.pageSize(),
                .stdout_buffer_bytes = 4096,
                .maximum_concurrent_threads = options.readers + 2,
                .fixture_worker_stack_bytes = 512 * 1024,
                .estimated_thread_stack_reservation_bytes = (options.readers + 1) * 512 * 1024,
                .fixture = "full C2 index; fixed and dynamic immutable 64-byte payloads; all cold rows real zero-byte registrations; setup and teardown excluded",
                .policy = benchmarkPolicy(),
                .timed_boundary = "start release to every thread joined; includes reader byte loads/use/end, 16 attr updates with bounded scheduling yields, overlap Tick, quiescent retirement Tick, rendezvous and controlled pause",
                .overlap = "per-window first native borrow/read from every reader remains locally leased until writer attr mutation completes; first leases end before independent hot repeat calls; calls_started_during_writer includes the bounded first-lease rendezvous; repeat_calls_started_during_writer separately records independent repeats begun after attr completion before overlap Tick completion",
                .samples = samples,
            };
            const json = try std.json.Stringify.valueAlloc(allocator, record, .{});
            defer allocator.free(json);
            var buffer: [4096]u8 = undefined;
            var output = std.Io.File.stdout().writer(io, &buffer);
            try output.interface.writeAll(json);
            try output.interface.writeAll("\n");
            try output.interface.flush();
        }
    };
}

fn parse(args: []const [:0]const u8) !Options {
    var options = Options{};
    var i: usize = 1;
    while (i < args.len) {
        const name = args[i];
        i += 1;
        if (i == args.len) return error.MissingOptionValue;
        const value = args[i];
        i += 1;
        if (std.mem.eql(u8, name, "--uid-bits")) options.uid_bits = try std.fmt.parseInt(u16, value, 10) else if (std.mem.eql(u8, name, "--capacity")) options.capacity = try std.fmt.parseInt(usize, value, 10) else if (std.mem.eql(u8, name, "--readers")) options.readers = try std.fmt.parseInt(usize, value, 10) else if (std.mem.eql(u8, name, "--samples")) options.samples = try std.fmt.parseInt(usize, value, 10) else if (std.mem.eql(u8, name, "--windows")) options.windows = try std.fmt.parseInt(usize, value, 10) else if (std.mem.eql(u8, name, "--hot-repeats")) options.hot_repeats = try std.fmt.parseInt(usize, value, 10) else if (std.mem.eql(u8, name, "--pause-ms")) options.pause_ms = try std.fmt.parseInt(u32, value, 10) else if (std.mem.eql(u8, name, "--wait-timeout-ms")) options.wait_timeout_ms = try std.fmt.parseInt(u32, value, 10) else if (std.mem.eql(u8, name, "--cache")) options.cache = std.meta.stringToEnum(Cache, value) orelse return error.InvalidCache else if (std.mem.eql(u8, name, "--path")) options.path = std.meta.stringToEnum(Path, value) orelse return error.InvalidPath else if (std.mem.eql(u8, name, "--frequency")) options.frequency = std.meta.stringToEnum(Frequency, value) orelse return error.InvalidFrequency else return error.UnknownOption;
    }
    if (options.uid_bits != 128 and options.uid_bits != 256) return error.InvalidUidWidth;
    if (options.capacity != 1024 and options.capacity != 1_048_576) return error.InvalidCapacity;
    if (options.readers != 1 and options.readers != 4 and options.readers != 8) return error.InvalidReaderCount;
    if (options.samples < 3 or options.samples > 32 or options.windows < 4 or options.windows > 1024 or
        options.hot_repeats < 2 or options.hot_repeats > 65536 or options.pause_ms > 100 or
        options.wait_timeout_ms < 1000 or options.wait_timeout_ms > 120_000) return error.InvalidBound;
    if (options.cache == .enabled and !@hasField(types.Config, "use_window_cache")) return error.CacheUnsupportedByFrozenSource;
    return options;
}
fn runProfile(comptime bits: u16, comptime capacity: usize, allocator: std.mem.Allocator, io: std.Io, options: Options) !void {
    // The outer comptime branch prevents even semantic instantiation of the
    // enabled Owner specialization against a frozen Config without the fields.
    if (comptime @hasField(types.Config, "use_window_cache")) {
        if (options.cache == .enabled) {
            try Driver(bits, capacity, true).run(allocator, io, options);
            return;
        }
    }
    try Driver(bits, capacity, false).run(allocator, io, options);
}
pub fn main(init: std.process.Init) !void {
    if (builtin.os.tag != .windows) return error.WindowsNativeFixtureRequired;
    if (builtin.mode != .ReleaseFast) return error.TimingRequiresReleaseFast;
    const args = try init.minimal.args.toSlice(init.arena.allocator());
    const options = try parse(args);
    if (options.uid_bits == 128) {
        if (options.capacity == 1024) try runProfile(128, 1024, init.gpa, init.io, options) else try runProfile(128, 1_048_576, init.gpa, init.io, options);
    } else {
        if (options.capacity == 1024) try runProfile(256, 1024, init.gpa, init.io, options) else try runProfile(256, 1_048_576, init.gpa, init.io, options);
    }
}
