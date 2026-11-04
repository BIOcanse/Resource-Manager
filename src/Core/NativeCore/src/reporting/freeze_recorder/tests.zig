const std = @import("std");
const t = @import("types.zig");
const codec = @import("codec.zig");
const ring_module = @import("ring.zig");
const Review = @import("review.zig").Review;
const wf = @import("windows_file.zig");

const MemoryFile = struct {
    bytes: [codec.record_bytes * 3]u8 = @splat(0),
    size: usize = 0,
    short_write: bool = false,
    closed: bool = false,
    fn owner(c: ?*anyopaque) *MemoryFile {
        return @ptrCast(@alignCast(c.?));
    }
    fn length(c: ?*anyopaque) !u64 {
        return owner(c).size;
    }
    fn setLength(c: ?*anyopaque, size: u64) !void {
        if (size > owner(c).bytes.len) return error.NoSpace;
        owner(c).size = @intCast(size);
    }
    fn read(c: ?*anyopaque, offset: u64, bytes: []u8) !usize {
        const self = owner(c);
        const at: usize = @intCast(offset);
        const n = @min(bytes.len, self.size - at);
        @memcpy(bytes[0..n], self.bytes[at..][0..n]);
        return n;
    }
    fn write(c: ?*anyopaque, offset: u64, bytes: []const u8) !usize {
        const self = owner(c);
        const at: usize = @intCast(offset);
        const n = if (self.short_write) bytes.len / 2 else bytes.len;
        @memcpy(self.bytes[at..][0..n], bytes[0..n]);
        return n;
    }
    fn close(c: ?*anyopaque) void {
        owner(c).closed = true;
    }
    fn ops(self: *MemoryFile) ring_module.FileOps {
        return .{ .context = self, .length = length, .set_length = setLength, .read_at = read, .write_at = write, .close = close };
    }
};

const Capture = struct {
    sequences: [3]u64 = @splat(0),
    count: usize = 0,
    fn visit(context: ?*anyopaque, record: *const t.Record) !void {
        const self: *Capture = @ptrCast(@alignCast(context.?));
        self.sequences[self.count] = record.sequence;
        self.count += 1;
    }
};

test "fixed ring wraps and streams oldest to newest without growing" {
    var file: MemoryFile = .{};
    var ring = try ring_module.Ring.init(file.ops(), .{ .slots = 3 }, true);
    defer ring.close();
    var sample: t.Sample = .{};
    var i: u32 = 1;
    while (i <= 5) : (i += 1) {
        sample.timestamp_ms = @as(i64, i) * 5_000;
        try ring.append(&sample);
    }
    try std.testing.expectEqual(@as(usize, codec.record_bytes * 3), file.size);
    var capture: Capture = .{};
    const streamed = try ring.stream(0, 30_000, &capture, Capture.visit);
    try std.testing.expectEqual(@as(u32, 3), streamed.records);
    try std.testing.expectEqual([3]u64{ 3, 4, 5 }, capture.sequences);
    try std.testing.expectEqual(@as(u64, 5), (try ring.latest()).?.sequence);
}

test "reopen skips torn latest record and preserves valid preceding records" {
    var file: MemoryFile = .{};
    var ring = try ring_module.Ring.init(file.ops(), .{ .slots = 3 }, true);
    try ring.append(&.{ .timestamp_ms = 5_000 });
    try ring.append(&.{ .timestamp_ms = 10_000 });
    try ring.append(&.{ .timestamp_ms = 15_000 });
    file.bytes[codec.record_bytes * 2 + 200] ^= 1;
    var recovered = try ring_module.Ring.init(file.ops(), .{ .slots = 3 }, false);
    try std.testing.expectEqual(@as(u64, 2), (try recovered.latest()).?.sequence);
    try std.testing.expect(recovered.invalid_slots != 0);
    try std.testing.expectError(error.ReadOnly, recovered.append(&.{ .timestamp_ms = 20_000 }));
    recovered.close();
    try std.testing.expect(file.closed);
}

test "short write never publishes a successful latest sequence" {
    var file: MemoryFile = .{};
    var ring = try ring_module.Ring.init(file.ops(), .{ .slots = 3 }, true);
    try ring.append(&.{ .timestamp_ms = 5_000 });
    file.short_write = true;
    try std.testing.expectError(error.ShortWrite, ring.append(&.{ .timestamp_ms = 10_000 }));
    try std.testing.expectEqual(@as(u64, 1), ring.latest_sequence);
}

test "record codec retains null metrics process names and private commit ranking" {
    var sample: t.Sample = .{ .timestamp_ms = 123, .physical_available_bytes = 0 };
    var i: u32 = 1;
    while (i <= 5) : (i += 1) sample.considerProcess(.{ .pid = i, .private_commit_bytes = i * 1_000 });
    sample.top_processes[0].image_name_len = 4;
    @memcpy(sample.top_processes[0].image_name[0..4], "test");
    sample.disk_count = 1;
    sample.disks[0] = .{ .physical_disk_id = 7, .queue_depth = 4, .completed_operations = 0 };
    var bytes: [codec.record_bytes]u8 = undefined;
    try codec.encode(&bytes, &.{ .sequence = 8, .sample = sample });
    const got = try codec.decode(&bytes);
    try std.testing.expectEqual(@as(?u64, null), got.sample.commit_bytes);
    try std.testing.expectEqual(@as(?u64, 0), got.sample.physical_available_bytes);
    try std.testing.expectEqual(@as(?f32, null), got.sample.disks[0].latency_ms);
    try std.testing.expectEqual(@as(u32, 5), got.sample.top_processes[0].pid);
    try std.testing.expectEqualStrings("test", got.sample.top_processes[0].imageName());
    try std.testing.expectEqual(@as(u32, 3), got.sample.top_processes[2].pid);
}

test "gap provenance retains publication window missing metrics and every disk slot" {
    var sample: t.Sample = .{
        .timestamp_ms = 123,
        .interrupt_timestamp_ms = -5,
        .interrupt_window_ms = 5_000,
        .interrupt_unavailable_reason = .@"stale-publication",
        .kernel_leak_unavailable_reason = .@"system-item-not-supported",
        .disk_count = @intCast(t.disk_capacity),
    };
    var i: usize = 0;
    while (i < t.disk_capacity) : (i += 1) {
        sample.disks[i] = .{
            .physical_disk_id = @intCast(i),
            .completed_operations_unavailable_reason = @as(t.GapReason, @fromBackingInt(@intCast(1 + i % 8))),
        };
    }
    var bytes: [codec.record_bytes]u8 = undefined;
    try codec.encode(&bytes, &.{ .sequence = 8, .sample = sample });
    const got = (try codec.decode(&bytes)).sample;
    try std.testing.expectEqual(@as(?f32, null), got.interrupt_cpu_percent);
    try std.testing.expectEqual(@as(?u64, null), got.longest_interrupt_us);
    try std.testing.expectEqual(@as(?i64, -5), got.interrupt_timestamp_ms);
    try std.testing.expectEqual(@as(?u32, 5_000), got.interrupt_window_ms);
    try std.testing.expectEqual(sample.interrupt_unavailable_reason, got.interrupt_unavailable_reason);
    try std.testing.expectEqual(@as(?bool, null), got.kernel_leak_active);
    try std.testing.expectEqual(sample.kernel_leak_unavailable_reason, got.kernel_leak_unavailable_reason);
    i = 0;
    while (i < t.disk_capacity) : (i += 1) {
        try std.testing.expectEqual(@as(?u64, null), got.disks[i].completed_operations);
        try std.testing.expectEqual(sample.disks[i].completed_operations_unavailable_reason, got.disks[i].completed_operations_unavailable_reason);
    }
}

test "publication metadata present zero and absent stay distinct" {
    var bytes: [codec.record_bytes]u8 = undefined;
    try codec.encode(&bytes, &.{ .sequence = 1, .sample = .{
        .interrupt_cpu_percent = 0,
        .longest_interrupt_us = 0,
        .interrupt_timestamp_ms = 0,
        .interrupt_window_ms = 0,
        .kernel_leak_active = false,
    } });
    const zero = (try codec.decode(&bytes)).sample;
    try std.testing.expectEqual(@as(?i64, 0), zero.interrupt_timestamp_ms);
    try std.testing.expectEqual(@as(?u32, 0), zero.interrupt_window_ms);
    try std.testing.expectEqual(@as(?f32, 0), zero.interrupt_cpu_percent);
    try std.testing.expectEqual(@as(?u64, 0), zero.longest_interrupt_us);
    try std.testing.expectEqual(@as(?bool, false), zero.kernel_leak_active);
    try std.testing.expectEqual(@as(?t.GapReason, null), zero.interrupt_unavailable_reason);
    try codec.encode(&bytes, &.{ .sequence = 2, .sample = .{} });
    const absent = (try codec.decode(&bytes)).sample;
    try std.testing.expectEqual(@as(?i64, null), absent.interrupt_timestamp_ms);
    try std.testing.expectEqual(@as(?u32, null), absent.interrupt_window_ms);
}

// Re-sign deliberately malformed fixtures so rejection proves format validation,
// rather than merely detecting that the fixture's CRC changed.
fn refreshRecordChecksum(bytes: *[codec.record_bytes]u8) void {
    var crc = std.hash.Crc32.init();
    crc.update(bytes[0..32]);
    crc.update(bytes[36..]);
    std.mem.writeInt(u32, bytes[32..36], crc.final(), .little);
}

test "legacy v1 zero tail remains readable without invented gap reasons" {
    var bytes: [codec.record_bytes]u8 = undefined;
    var sample: t.Sample = .{
        .timestamp_ms = 123,
        .interrupt_timestamp_ms = 120,
        .interrupt_window_ms = 5_000,
        .interrupt_unavailable_reason = .@"source-unavailable",
        .kernel_leak_unavailable_reason = .@"not-published",
        .disk_count = 1,
        .physical_available_bytes = 0,
    };
    sample.disks[0] = .{
        .physical_disk_id = 7,
        .completed_operations_unavailable_reason = .@"counter-warmup",
    };
    try codec.encode(&bytes, &.{ .sequence = 8, .sample = sample });
    @memset(bytes[codec.legacy_payload_bytes..], 0);
    refreshRecordChecksum(&bytes);
    const got = (try codec.decode(&bytes)).sample;
    try std.testing.expectEqual(@as(i64, 123), got.timestamp_ms);
    try std.testing.expectEqual(@as(?u64, 0), got.physical_available_bytes);
    try std.testing.expectEqual(@as(?f32, null), got.interrupt_cpu_percent);
    try std.testing.expectEqual(@as(?i64, null), got.interrupt_timestamp_ms);
    try std.testing.expectEqual(@as(?u32, null), got.interrupt_window_ms);
    try std.testing.expectEqual(@as(?t.GapReason, null), got.interrupt_unavailable_reason);
    try std.testing.expectEqual(@as(?t.GapReason, null), got.kernel_leak_unavailable_reason);
    try std.testing.expectEqual(@as(u32, 7), got.disks[0].physical_disk_id);
    try std.testing.expectEqual(@as(?u64, null), got.disks[0].completed_operations);
    try std.testing.expectEqual(@as(?t.GapReason, null), got.disks[0].completed_operations_unavailable_reason);
}

test "CRC valid unknown gap extension marker reasons flags and padding are rejected" {
    var original: [codec.record_bytes]u8 = undefined;
    try codec.encode(&original, &.{ .sequence = 1, .sample = .{} });
    const at = codec.legacy_payload_bytes;
    const corruptions = [_]struct { offset: usize, value: u8 }{
        .{ .offset = at, .value = 0xff },
        .{ .offset = at + 4, .value = 4 },
        .{ .offset = at + 5, .value = 9 },
        .{ .offset = at + 6, .value = 0xff },
        .{ .offset = at + 7, .value = 1 },
        .{ .offset = at + 20, .value = 9 },
        .{ .offset = at + 20, .value = 0x90 },
    };
    var i: usize = 0;
    while (i < corruptions.len) : (i += 1) {
        var bytes = original;
        bytes[corruptions[i].offset] = corruptions[i].value;
        refreshRecordChecksum(&bytes);
        try std.testing.expectError(error.InvalidRecord, codec.decode(&bytes));
    }
    var markerless = original;
    @memset(markerless[at..][0..4], 0);
    markerless[at + 5] = 1;
    refreshRecordChecksum(&markerless);
    try std.testing.expectError(error.InvalidRecord, codec.decode(&markerless));
    if (at + codec.extension_bytes < codec.record_bytes) {
        var padding = original;
        padding[at + codec.extension_bytes] = 1;
        refreshRecordChecksum(&padding);
        try std.testing.expectError(error.InvalidRecord, codec.decode(&padding));
    }
}

test "default gap provenance uses existing v1 padding and record size" {
    try std.testing.expectEqual(@as(usize, 680), codec.legacy_payload_bytes);
    try std.testing.expectEqual(@as(usize, 28), codec.extension_bytes);
    try std.testing.expectEqual(@as(usize, 768), codec.record_bytes);
    var bytes: [codec.record_bytes]u8 = undefined;
    try codec.encode(&bytes, &.{ .sequence = 1, .sample = .{} });
    try std.testing.expectEqual(@as(u32, 1), std.mem.readInt(u32, bytes[8..12], .little));
    try std.testing.expectEqualStrings("GAP1", bytes[680..684]);
}

var captured_flags: u32 = 0;
var captured_access: u32 = 0;
var captured_disposition: u32 = 0;
fn captureOpen(_: [*:0]const u16, access: u32, _: u32, _: ?*anyopaque, disposition: u32, flags: u32, _: ?std.os.windows.HANDLE) callconv(.winapi) std.os.windows.HANDLE {
    captured_access = access;
    captured_flags = flags;
    captured_disposition = disposition;
    return @ptrFromInt(7);
}

test "Windows ring file creation actually requests write through and readonly does not create" {
    _ = try wf.WindowsFile.openWith(std.unicode.utf8ToUtf16LeStringLiteral("ring.bin"), true, captureOpen);
    try std.testing.expect(captured_flags & wf.write_through != 0);
    try std.testing.expect(captured_access & 0x40000000 != 0);
    try std.testing.expectEqual(@as(u32, 4), captured_disposition);
    _ = try wf.WindowsFile.openWith(std.unicode.utf8ToUtf16LeStringLiteral("ring.bin"), false, captureOpen);
    try std.testing.expect(captured_access & 0x40000000 == 0);
    try std.testing.expectEqual(@as(u32, 3), captured_disposition);
}

fn quietSample(timestamp_ms: i64) t.Sample {
    var sample: t.Sample = .{ .timestamp_ms = timestamp_ms, .commit_bytes = 40, .commit_limit_bytes = 100, .physical_total_bytes = 100, .physical_available_bytes = 50, .paged_pool_bytes = 2, .nonpaged_pool_bytes = 2, .interrupt_cpu_percent = 0, .longest_interrupt_us = 0, .kernel_leak_active = false, .disks_complete = true, .processes_complete = true, .disk_count = 1 };
    sample.disks[0] = .{ .physical_disk_id = 4, .queue_depth = 0, .completed_operations = 0 };
    return sample;
}

test "October 08 resource exhaustion 2004 produces commit exhausted even with quiet ring" {
    var review = try Review.init(.{ .commit_immediate_percent = 99 }, 1_000_000, 1_010_000);
    review.addSample(&.{ .sequence = 1, .sample = quietSample(1_000_000) });
    review.addEvent(&.{ .kind = .resource_exhaustion, .timestamp_ms = 999_000, .identity = 2004 });
    review.addEvent(&.{ .kind = .unexpected_shutdown, .timestamp_ms = 1_005_000, .identity = 41 });
    review.setEventsComplete();
    const result = review.finish();
    try std.testing.expect(result.tags.commit_exhausted);
    try std.testing.expect(!result.tags.recording_stopped_normal);
    try std.testing.expectEqual(@as(?u64, 5_000), result.gap_ms);
}

test "October 07 critical process died EF retains bugcheck and stable dedup identity" {
    var review = try Review.init(.{}, 1_000_000, 1_020_000);
    const bug: t.EventEvidence = .{ .kind = .bugcheck, .timestamp_ms = 1_015_000, .shutdown_timestamp_ms = 1_005_000, .identity = 1001, .bugcheck_code = 0xEF };
    review.addEvent(&bug);
    const first = review.finish();
    review.addEvent(&bug);
    review.addEvent(&.{ .kind = .unexpected_shutdown, .timestamp_ms = 1_018_000, .shutdown_timestamp_ms = 1_005_000, .identity = 6008 });
    const repeated = review.finish();
    try std.testing.expect(first.unexpected_shutdown);
    try std.testing.expect(first.tags.bugcheck);
    try std.testing.expectEqual(@as(?u32, 0xEF), first.bugcheck_code);
    try std.testing.expectEqual(first.evidence_identity, repeated.evidence_identity);
}

test "quiet complete records stopped before unexpected shutdown yield normal recording stop" {
    var review = try Review.init(.{ .commit_immediate_percent = 99 }, 1_000_000, 1_010_000);
    review.addSample(&.{ .sequence = 1, .sample = quietSample(995_000) });
    review.addSample(&.{ .sequence = 2, .sample = quietSample(1_000_000) });
    review.addEvent(&.{ .kind = .unexpected_shutdown, .timestamp_ms = 1_005_000, .identity = 41 });
    review.setEventsComplete();
    try std.testing.expect(review.finish().tags.recording_stopped_normal);
}

test "missing samples metrics event catchup or corrupted slots never become normal" {
    var review = try Review.init(.{}, 1_000_000, 1_010_000);
    review.addEvent(&.{ .kind = .unexpected_shutdown, .timestamp_ms = 1_005_000, .identity = 41 });
    try std.testing.expect(!review.finish().tags.recording_stopped_normal);
    review.addSample(&.{ .sequence = 1, .sample = .{ .timestamp_ms = 1_000_000 } });
    review.setEventsComplete();
    try std.testing.expect(!review.finish().inputs_complete);
    try std.testing.expect(!review.finish().tags.recording_stopped_normal);
}

test "storage stall requires three consecutive observations of same physical disk with valid zero completions" {
    var review = try Review.init(.{}, 1_000_000, 1_010_000);
    var sample = quietSample(990_000);
    sample.disks[0].queue_depth = 1;
    review.addSample(&.{ .sequence = 1, .sample = sample });
    sample.timestamp_ms += 5_000;
    review.addSample(&.{ .sequence = 2, .sample = sample });
    try std.testing.expect(!review.finish().tags.storage_unresponsive);
    sample.timestamp_ms += 5_000;
    review.addSample(&.{ .sequence = 3, .sample = sample });
    try std.testing.expect(review.finish().tags.storage_unresponsive);
    var missing = try Review.init(.{}, 1_000_000, 1_010_000);
    sample.disks[0].completed_operations = null;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) missing.addSample(&.{ .sequence = sequence, .sample = sample });
    try std.testing.expect(!missing.finish().tags.storage_unresponsive);
    try std.testing.expect(!missing.finish().inputs_complete);
}

test "thresholds are inclusive independent tags and configurable" {
    var review = try Review.init(.{ .commit_immediate_percent = 99 }, 1_000_000, 1_010_000);
    var sample = quietSample(1_000_000);
    sample.commit_bytes = 99;
    sample.physical_available_bytes = 2;
    sample.nonpaged_pool_bytes = 10;
    review.addSample(&.{ .sequence = 1, .sample = sample });
    review.addEvent(&.{ .kind = .gpu_reset, .timestamp_ms = 999_000, .identity = 4101 });
    review.addEvent(&.{ .kind = .hardware_error, .timestamp_ms = 999_000, .identity = 17 });
    const tags = review.finish().tags;
    try std.testing.expect(tags.commit_exhausted and tags.physical_exhausted and tags.pool_high);
    try std.testing.expect(tags.gpu_reset and tags.hardware_error);
    var raised = try Review.init(.{ .commit_immediate_percent = null, .physical_available_percent = 1, .nonpaged_pool_percent = 11 }, 1_000_000, 1_010_000);
    raised.addSample(&.{ .sequence = 1, .sample = sample });
    try std.testing.expect(!raised.finish().tags.hasCause());
}

test "old shutdown before latest ring record never produces a new incident" {
    var review = try Review.init(.{}, 1_000_000, 1_010_000);
    review.addSample(&.{ .sequence = 1, .sample = quietSample(1_000_000) });
    review.addEvent(&.{ .kind = .unexpected_shutdown, .timestamp_ms = 1_005_000, .shutdown_timestamp_ms = 990_000, .identity = 6008 });
    review.setEventsComplete();
    try std.testing.expect(!review.finish().unexpected_shutdown);
}

test "active system kernel leak marks pool high without requiring high pool ratio" {
    var review = try Review.init(.{}, 1_000_000, 1_010_000);
    var sample = quietSample(1_000_000);
    sample.kernel_leak_active = true;
    review.addSample(&.{ .sequence = 1, .sample = sample });
    try std.testing.expect(review.finish().tags.pool_high);
}

test "storage sequence gap breaks the consecutive stall window" {
    var review = try Review.init(.{}, 1_000_000, 1_010_000);
    var sample = quietSample(1_000_000);
    sample.disks[0].queue_depth = 1;
    review.addSample(&.{ .sequence = 1, .sample = sample });
    review.addSample(&.{ .sequence = 2, .sample = sample });
    review.addSample(&.{ .sequence = 4, .sample = sample });
    try std.testing.expect(!review.finish().tags.storage_unresponsive);
    try std.testing.expect(!review.finish().inputs_complete);
}
