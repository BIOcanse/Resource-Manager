const std = @import("std");
const t = @import("types.zig");
const codec = @import("codec.zig");
pub const record_bytes = codec.record_bytes;

// Synchronous, externally serialized ownership. Callbacks do not retain buffers.
// The owner retains the context until Ring.close. No allocator is used here.
pub const FileOps = struct {
    context: ?*anyopaque,
    length: *const fn (?*anyopaque) anyerror!u64,
    set_length: *const fn (?*anyopaque, u64) anyerror!void,
    read_at: *const fn (?*anyopaque, u64, []u8) anyerror!usize,
    write_at: *const fn (?*anyopaque, u64, []const u8) anyerror!usize,
    close: *const fn (?*anyopaque) void,
};
pub const Visitor = *const fn (?*anyopaque, *const t.Record) anyerror!void;
pub const StreamResult = struct { records: u32 = 0, missing_records: u32 = 0 };

pub const Ring = struct {
    file: FileOps,
    config: t.Config,
    writable: bool,
    closed: bool = false,
    latest_sequence: u64 = 0,
    latest_timestamp_ms: ?i64 = null,
    invalid_slots: u32 = 0,
    // The only retained record buffer. Samples passed to append belong to the host.
    buffer: [record_bytes]u8 = undefined,

    pub fn init(file: FileOps, config: t.Config, writable: bool) !Ring {
        try config.validate();
        var self: Ring = .{ .file = file, .config = config, .writable = writable };
        const expected = @as(u64, config.slots) * record_bytes;
        const size = try file.length(file.context);
        if (size == 0 and writable) {
            try file.set_length(file.context, expected);
            @memset(&self.buffer, 0);
            var slot: u32 = 0;
            // Materialize allocation now rather than doing growth during distress.
            while (slot < config.slots) : (slot += 1) {
                try self.writeExact(@as(u64, slot) * record_bytes);
            }
        } else if (size != expected) {
            // Resizing an existing ring is a host migration, never silent data loss.
            return error.RingSizeMismatch;
        }
        var slot: u32 = 0;
        while (slot < config.slots) : (slot += 1) {
            const record = try self.readSlot(slot) orelse continue;
            if ((record.sequence - 1) % config.slots != slot) {
                self.invalid_slots += 1;
                continue;
            }
            if (record.sequence > self.latest_sequence) {
                self.latest_sequence = record.sequence;
                self.latest_timestamp_ms = record.sample.timestamp_ms;
            }
        }
        return self;
    }

    pub fn close(self: *Ring) void {
        if (self.closed) return;
        self.file.close(self.file.context);
        self.closed = true;
    }

    pub fn append(self: *Ring, sample: *const t.Sample) !void {
        if (self.closed) return error.RingClosed;
        if (!self.writable) return error.ReadOnly;
        if (self.latest_sequence == std.math.maxInt(u64)) return error.SequenceExhausted;
        const sequence = self.latest_sequence + 1;
        try codec.encode(&self.buffer, &.{ .sequence = sequence, .sample = sample.* });
        try self.writeExact(((sequence - 1) % self.config.slots) * record_bytes);
        // Publish state only after the complete write-through operation succeeds.
        self.latest_sequence = sequence;
        self.latest_timestamp_ms = sample.timestamp_ms;
    }

    pub fn latest(self: *Ring) !?t.Record {
        if (self.closed) return error.RingClosed;
        if (self.latest_sequence == 0) return null;
        return self.readSlot(@intCast((self.latest_sequence - 1) % self.config.slots));
    }

    // Sequence order is physical insertion order, independent of clock corrections.
    // The visitor must copy any record it needs after returning. It may stream raw
    // export rows or update a Review without collecting the entire ring in memory.
    pub fn stream(self: *Ring, from_ms: i64, to_ms: i64, context: ?*anyopaque, visitor: Visitor) !StreamResult {
        if (self.closed) return error.RingClosed;
        if (from_ms > to_ms) return error.InvalidWindow;
        var result: StreamResult = .{};
        if (self.latest_sequence == 0) return result;
        const first = self.latest_sequence - @min(self.latest_sequence, self.config.slots) + 1;
        var sequence = first;
        while (true) {
            const record = try self.readSlot(@intCast((sequence - 1) % self.config.slots));
            if (record) |r| {
                if (r.sequence != sequence) {
                    result.missing_records += 1;
                } else if (r.sample.timestamp_ms >= from_ms and r.sample.timestamp_ms <= to_ms) {
                    try visitor(context, &r);
                    result.records += 1;
                }
            } else {
                result.missing_records += 1;
            }
            if (sequence == self.latest_sequence) break;
            sequence += 1;
        }
        return result;
    }

    fn readSlot(self: *Ring, slot: u32) !?t.Record {
        const count = try self.file.read_at(self.file.context, @as(u64, slot) * record_bytes, &self.buffer);
        if (count != record_bytes) return error.ShortRead;
        var nonzero = false;
        var i: usize = 0;
        while (i < self.buffer.len) : (i += 1) nonzero = nonzero or self.buffer[i] != 0;
        if (!nonzero) return null;
        return codec.decode(&self.buffer) catch {
            self.invalid_slots +|= 1;
            return null;
        };
    }

    fn writeExact(self: *Ring, offset: u64) !void {
        if (try self.file.write_at(self.file.context, offset, &self.buffer) != record_bytes)
            return error.ShortWrite;
    }
};
