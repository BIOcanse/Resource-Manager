const std = @import("std");
const native = @import("consumer.zig");
const decoder = @import("decoder.zig");
const io = @import("io_decode.zig");
const History = @import("thread_process_history.zig").History;
const FileIoState = @import("fileio.zig").FileIoState;
const PathMap = @import("path_mapping.zig").PathMap;
const namespace = @import("namespace.zig");
const Budget = @import("budget.zig").Budget;

// Streaming, independent raw-record input oracle. This is test infrastructure,
// not a production event batch or another production consumer. Only ten scalar
// counts/hashes are retained regardless of ETL size.
const Digest = struct {
    count: u64 = 0,
    hash: u64 = 14695981039346656037,
    fn bytes(self: *Digest, value: []const u8) void {
        for (value) |byte| self.hash = (self.hash ^ byte) *% 1099511628211;
    }
    fn integer(self: *Digest, comptime T: type, value: T) void {
        var buffer: [@sizeOf(T)]u8 = undefined;
        std.mem.writeInt(T, &buffer, value, .little);
        self.bytes(&buffer);
    }
};
const Probe = struct {
    history: History,
    files: FileIoState,
    path_budget: Budget,
    paths: PathMap = undefined,
    consumer: *native.Consumer,
    digests: [10]Digest = @splat(.{}),
    file_counts: [6]u64 = @splat(0), // Create, FileCreate, FileDelete, Write, Rename, Delete.
    namespace_counts: [2]u64 = @splat(0),
    failure: ?anyerror = null,
    limited: bool = false,

    fn notice(context: ?*anyopaque, value: native.Notice) bool {
        const self: *Probe = @ptrCast(@alignCast(context.?));
        self.accept(value) catch |err| {
            self.failure = err;
            return false;
        };
        return self.withinLimit();
    }
    fn withinLimit(self: *Probe) bool {
        if (self.consumer.counters.records < 100_000_000) return true;
        self.limited = true;
        return false;
    }
    fn accept(self: *Probe, value: native.Notice) !void {
        switch (value) {
            .rejected => |record| {
                std.debug.print("Shared core decode failure: provider={x} opcode={d} version={d} pointer={d} bytes={d} qpc={d} reason={s}\n", .{ record.raw.provider.data1, record.raw.opcode, record.raw.version, record.raw.pointer_size, record.raw.payload.len, record.raw.qpc, @errorName(record.reason) });
                return error.InvalidRawRecord;
            },
            .invalid_record => return error.InvalidRawRecord,
            // This probe compares parser fields, not complete aggregate
            // coverage. TraceEvent's parser history survives loss; retain the
            // same history here and report the loss counters without claiming
            // any complete residency/I/O window from this diagnostic pass.
            .loss => {},
            .event => |event| switch (event.data) {
                .thread => |thread| {
                    if (thread.lifecycle == .start or thread.lifecycle == .rundown_start)
                        try self.history.start(thread.thread_id, thread.process_id)
                    else
                        try self.history.end(thread.thread_id, thread.process_id, event.qpc);
                },
                .lost => {},
                else => {},
            },
        }
    }
    fn raw(context: ?*anyopaque, event: decoder.RawEvent) bool {
        const self: *Probe = @ptrCast(@alignCast(context.?));
        self.shared(event) catch |err| {
            std.debug.print("Shared state failure: provider={x} opcode={d} version={d} pointer={d} bytes={d} qpc={d} reason={s}\n", .{ event.provider.data1, event.opcode, event.version, event.pointer_size, event.payload.len, event.qpc, @errorName(err) });
            self.failure = err;
            return false;
        };
        return self.withinLimit();
    }
    fn shared(self: *Probe, event: decoder.RawEvent) !void {
        if (try namespace.decode(event)) |value| {
            self.namespace_counts[if (value == .system_paths) @as(usize, 0) else 1] += 1;
            try namespace.apply(value, &self.paths);
        }
        if (event.provider.eql(@import("fileio.zig").file_io_task_guid)) {
            var round = std.heap.ArenaAllocator.init(self.files.budget.allocator());
            defer round.deinit();
            if (try self.files.observe(event, &self.paths, &self.history, round.allocator())) |value| {
                const index: ?usize = switch (value) {
                    .create => 0,
                    .name => |name| if (name.opcode == 32) 1 else 2,
                    .read_write => |rw| if (rw.kind == .write) @as(usize, 3) else null,
                    .info => |info| switch (info.kind) {
                        .rename => 4,
                        .delete => 5,
                        .set_info => null,
                    },
                };
                if (index) |i| self.file_counts[i] += 1;
            }
        }
        const parsed = io.decode(event, null) catch |err| {
            std.debug.print("Shared raw decode failure: provider={x} opcode={d} version={d} pointer={d} bytes={d} qpc={d} reason={s}\n", .{ event.provider.data1, event.opcode, event.version, event.pointer_size, event.payload.len, event.qpc, @errorName(err) });
            return err;
        };
        if (parsed) |input| switch (input) {
            .disk => |disk| {
                const target = &self.digests[if (disk.is_write) @as(usize, 1) else 0];
                target.count += 1;
                target.integer(u64, @bitCast(disk.qpc));
                target.integer(u32, disk.process_id orelse if (disk.thread_id) |tid| self.history.lookup(tid, disk.qpc) orelse std.math.maxInt(u32) else std.math.maxInt(u32));
                target.integer(u32, disk.bytes);
                target.integer(u32, disk.disk_number orelse std.math.maxInt(u32));
            },
            .network => |network| {
                const family: usize = if (network.remote_address == .ipv6) 2 else 0;
                const protocol: usize = if (network.protocol == .udp) 4 else 0;
                const direction: usize = if (network.direction == .receive) 1 else 0;
                const target = &self.digests[2 + protocol + family + direction];
                target.count += 1;
                target.integer(u64, @bitCast(network.qpc));
                target.integer(u32, network.process_id orelse std.math.maxInt(u32));
                target.integer(u32, network.bytes);
                switch (network.remote_address) {
                    .ipv4 => |address| {
                        target.integer(u32, 4);
                        target.bytes(&address);
                    },
                    .ipv6 => |address| {
                        target.integer(u32, 16);
                        target.bytes(&address);
                    },
                }
            },
        };
    }
};

test "optional whole finite ETL shared disk and network input digests" {
    const key = std.unicode.utf8ToUtf16LeStringLiteral("RESOURCE_MANAGER_ETW_SHARED_ETL_PATH");
    const path = std.testing.environ.getWindows(key) orelse return error.SkipZigTest;
    var consumer: native.Consumer = undefined;
    var probe: Probe = .{
        .history = try History.init(std.testing.allocator, 1_000_000, 128 * 1024 * 1024, false),
        .files = try FileIoState.init(std.testing.allocator, .{ .max_entries = 262144, .max_versions = 2_000_000, .max_name_units = 4096, .max_path_units = 4096, .byte_cap = 256 * 1024 * 1024 }, .recorded),
        .path_budget = .{ .backing = std.testing.allocator, .cap = 8 * 1024 * 1024 },
        .consumer = &consumer,
    };
    defer probe.history.deinit();
    defer probe.files.deinit();
    probe.paths = try PathMap.initDeferredWithLimits(probe.path_budget.allocator(), 4096, 512);
    defer probe.paths.deinit();
    consumer = native.Consumer.init(.{ .context = &probe, .on_notice = Probe.notice, .on_unhandled = Probe.raw });
    try consumer.openEtl(path);
    defer _ = consumer.close() catch {};
    const status = try consumer.process();
    if (probe.failure) |err| {
        std.debug.print("Shared probe failed after records={d} rejected={d} lost={d}: {s}\n", .{ consumer.counters.records, consumer.counters.rejected, consumer.counters.events_lost, @errorName(err) });
        return err;
    }
    try std.testing.expect(!probe.limited);
    try std.testing.expectEqual(@as(u32, 0), status);
    try std.testing.expectEqual(@as(u64, 0), consumer.counters.rejected);
    std.debug.print("Shared native input records={d} rejected={d} lost={d} buffersLost={d} stop=eof historyCurrent={d} historyPeak={d}\n", .{ consumer.counters.records, consumer.counters.rejected, consumer.counters.events_lost, consumer.counters.buffers_lost, probe.history.budget.current, probe.history.budget.peak });
    const names = [_][]const u8{ "diskRead", "diskWrite", "tcp4Send", "tcp4Recv", "tcp6Send", "tcp6Recv", "udp4Send", "udp4Recv", "udp6Send", "udp6Recv" };
    for (names, probe.digests) |name, digest| std.debug.print("Shared native variant={s} count={d} hash={x:0>16}\n", .{ name, digest.count, digest.hash });
    std.debug.print("Shared native FileIO counts={any} namespaceCounts={any} fileCurrent={d} filePeak={d} pathCurrent={d} pathPeak={d}\n", .{ probe.file_counts, probe.namespace_counts, probe.files.budget.current, probe.files.budget.peak, probe.path_budget.current, probe.path_budget.peak });
}
