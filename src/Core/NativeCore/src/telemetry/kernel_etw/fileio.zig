const std = @import("std");
const Budget = @import("budget.zig").Budget;
const decoder = @import("decoder.zig");
const path_mapping = @import("path_mapping.zig");
const thread_process = @import("thread_process_history.zig");

/// EVENT_RECORD.ProviderId for classic kernel FileIO records. The logical
/// TraceEvent provider GUID (9e814aad-...) is not used for raw records.
pub const file_io_task_guid: decoder.Guid = .{
    .data1 = 0x90cbdc39,
    .data2 = 0x4a3e,
    .data3 = 0x11d1,
    .data4 = .{ 0x84, 0xf4, 0x00, 0x00, 0xf8, 0x04, 0x64, 0xe3 },
};

pub const SourceMode = enum { recorded, realtime };

pub const Limits = struct {
    max_entries: usize = 32768,
    max_versions: usize = 131072,
    max_name_units: usize = 32768,
    max_path_units: usize = 32768,
    byte_cap: usize = 16 * 1024 * 1024,
};

pub const DecodeError = error{
    InvalidPointerSize,
    InvalidTimestamp,
    InvalidPayload,
    TruncatedPayload,
    UnsupportedVersion,
};

const Version = struct { qpc: i64, name: []u16 };
const AddressHistory = struct { versions: std.ArrayListUnmanaged(Version) = .empty };
const NameContext = struct {
    pub fn hash(_: NameContext, value: []const u16) u64 { return std.hash.Wyhash.hash(0, std.mem.sliceAsBytes(value)); }
    pub fn eql(_: NameContext, left: []const u16, right: []const u16) bool { return std.mem.eql(u16, left, right); }
};

pub const NameEvent = struct {
    opcode: u8,
    file_key: u64,
    file_name: []const u16,
};

pub const CreateEvent = struct {
    qpc: i64,
    process_id: u32,
    thread_id: u32,
    irp: u64,
    file_object: u64,
    create_options: u32,
    create_disposition: u8,
    file_attributes: u32,
    share_access: u32,
    file_name: []const u16,
};

pub const ReadWriteKind = enum { read, write };
pub const ReadWriteEvent = struct {
    qpc: i64,
    process_id: u32,
    thread_id: u32,
    kind: ReadWriteKind,
    offset: i64,
    irp: u64,
    file_object: u64,
    file_key: u64,
    io_size: u32,
    io_flags: u32,
    file_name: ?[]const u16,
};

pub const InfoKind = enum { set_info, delete, rename };
pub const InfoEvent = struct {
    qpc: i64,
    process_id: u32,
    thread_id: u32,
    kind: InfoKind,
    irp: u64,
    file_object: u64,
    file_key: u64,
    extra_info: u64,
    info_class: u32,
    file_name: ?[]const u16,
};

pub const Event = union(enum) {
    name: NameEvent,
    create: CreateEvent,
    read_write: ReadWriteEvent,
    info: InfoEvent,
};

/// Event-stream-owned FileKey/FileObject name history. It retains the string
/// returned by TraceEvent's FileName getter, which is already path-mapped when
/// the name event is observed. Lookups therefore return the saved string as-is.
pub const FileIoState = struct {
    budget: Budget,
    limits: Limits,
    mode: SourceMode,
    histories: std.AutoHashMapUnmanaged(u64, AddressHistory) = .empty,
    names: std.HashMapUnmanaged([]const u16, usize, NameContext, 80) = .empty,
    version_count: usize = 0,

    pub fn init(backing: std.mem.Allocator, limits: Limits, mode: SourceMode) !FileIoState {
        if (limits.max_entries == 0 or limits.max_versions == 0 or limits.max_name_units == 0 or limits.max_path_units == 0 or limits.byte_cap == 0) return error.InvalidConfiguration;
        return .{ .budget = .{ .backing = backing, .cap = limits.byte_cap }, .limits = limits, .mode = mode };
    }

    pub fn deinit(self: *FileIoState) void {
        self.clear();
    }

    pub fn clear(self: *FileIoState) void {
        const allocator = self.budget.allocator();
        var iterator = self.histories.valueIterator();
        while (iterator.next()) |history| {
            for (history.versions.items) |version| self.releaseName(version.name);
            history.versions.deinit(allocator);
        }
        self.histories.clearAndFree(allocator);
        std.debug.assert(self.names.count() == 0);
        self.names.clearAndFree(allocator);
        self.version_count = 0;
    }

    pub fn entryCount(self: *const FileIoState) usize {
        return self.histories.count();
    }

    fn retainName(self: *FileIoState, units: []const u16) ![]u16 {
        if (units.len > self.limits.max_name_units) return error.CapacityExceeded;
        if (self.names.getEntry(units)) |entry| {
            entry.value_ptr.* = try std.math.add(usize, entry.value_ptr.*, 1);
            return @constCast(entry.key_ptr.*);
        }
        const owned = try self.budget.allocator().dupe(u16, units);
        errdefer self.budget.allocator().free(owned);
        try self.names.put(self.budget.allocator(), owned, 1);
        return owned;
    }

    fn releaseName(self: *FileIoState, units: []const u16) void {
        const references = self.names.getPtr(units).?;
        std.debug.assert(references.* > 0);
        references.* -= 1;
        if (references.* == 0) {
            const removed = self.names.fetchRemove(units).?;
            self.budget.allocator().free(removed.key);
        }
    }

    fn copyWideBytes(self: *FileIoState, bytes: []const u8, round: std.mem.Allocator) ![]const u16 {
        if (bytes.len % 2 != 0) return error.InvalidPayload;
        const units = bytes.len / 2;
        if (units > self.limits.max_name_units) return error.CapacityExceeded;
        const text = try round.alloc(u16, units);
        for (text, 0..) |*unit, index| unit.* = std.mem.readInt(u16, bytes[index * 2 ..][0..2], .little);
        return text;
    }

    /// Mirrors HistoryDictionary.Add. Names at an equal QPC replace one
    /// another; backdated additions are inserted in timestamp order. A new
    /// rundown key starts at QPC zero. The single consumer calls this directly.
    fn addName(self: *FileIoState, key: u64, qpc: i64, name: []const u16, is_rundown: bool) !void {
        if (qpc < 0) return error.InvalidTimestamp;
        if (name.len > self.limits.max_name_units) return error.CapacityExceeded;
        const allocator = self.budget.allocator();
        if (self.histories.getPtr(key)) |history| {
            var lo: usize = 0;
            var hi = history.versions.items.len;
            while (lo < hi) {
                const mid = lo + (hi - lo) / 2;
                if (history.versions.items[mid].qpc > qpc) lo = mid + 1 else hi = mid;
            }
            if (lo < history.versions.items.len and history.versions.items[lo].qpc == qpc) {
                const old = history.versions.items[lo].name;
                if (std.mem.eql(u16, old, name)) return;
                const replaced = try self.retainName(name);
                history.versions.items[lo].name = replaced;
                self.releaseName(old);
                return;
            }
            if (self.version_count >= self.limits.max_versions) return error.CapacityExceeded;
            const owned = try self.retainName(name);
            errdefer self.releaseName(owned);
            try history.versions.insert(allocator, lo, .{ .qpc = qpc, .name = owned });
            self.version_count += 1;
            return;
        }

        if (self.histories.count() >= self.limits.max_entries or self.version_count >= self.limits.max_versions) return error.CapacityExceeded;
        const owned = try self.retainName(name);
        errdefer self.releaseName(owned);
        var history: AddressHistory = .{};
        errdefer history.versions.deinit(allocator);
        // A single version is the common per-open-file case. Do not reserve
        // a growth batch for every distinct FileObject in a recorded trace.
        try history.versions.ensureTotalCapacityPrecise(allocator, 1);
        try history.versions.append(allocator, .{ .qpc = if (is_rundown) 0 else qpc, .name = owned });
        try self.histories.put(allocator, key, history);
        self.version_count += 1;
    }

    fn removeName(self: *FileIoState, key: u64) void {
        const allocator = self.budget.allocator();
        const history = self.histories.getPtr(key) orelse return;
        self.version_count -= history.versions.items.len;
        for (history.versions.items) |version| self.releaseName(version.name);
        history.versions.deinit(allocator);
        _ = self.histories.remove(key);
    }

    /// Returns the latest value whose insertion QPC is no later than `qpc`.
    /// FileKey is preferred over FileObject, matching TraceEvent's lookup.
    pub fn lookupName(self: *const FileIoState, key: u64, qpc: i64) ?[]const u16 {
        if (qpc < 0) return null;
        const history = self.histories.get(key) orelse return null;
        var lo: usize = 0;
        var hi = history.versions.items.len;
        while (lo < hi) {
            const mid = lo + (hi - lo) / 2;
            if (history.versions.items[mid].qpc > qpc) lo = mid + 1 else hi = mid;
        }
        if (lo >= history.versions.items.len) return null;
        return history.versions.items[lo].name;
    }

    pub fn resolveName(self: *const FileIoState, file_key: u64, file_object: u64, qpc: i64) ?[]const u16 {
        return self.lookupName(file_key, qpc) orelse self.lookupName(file_object, qpc);
    }

    fn mappedName(self: *const FileIoState, file_key: u64, file_object: u64, qpc: i64) ?[]const u16 {
        return self.resolveName(file_key, file_object, qpc);
    }

    /// Decodes and applies one classic FileIO record. Internal name and cleanup
    /// records update history and return null. Public parser records return a
    /// typed value whose text is valid for the caller's round-arena lifetime.
    pub fn observe(
        self: *FileIoState,
        raw: decoder.RawEvent,
        paths: *path_mapping.PathMap,
        threads: *const thread_process.History,
        round: std.mem.Allocator,
    ) !?Event {
        if (!raw.provider.eql(file_io_task_guid)) return null;
        if (!isSupportedOpcode(raw.opcode)) return null;
        if (raw.pointer_size != 4 and raw.pointer_size != 8) return error.InvalidPointerSize;
        if (raw.qpc < 0) return error.InvalidTimestamp;

        return switch (raw.opcode) {
            0, 32, 35, 36 => try self.observeName(raw, paths, round),
            64 => try self.observeCreate(raw, paths, threads, round),
            65 => blk: {
                const simple = try decodeSimple(raw);
                if (self.mode == .realtime) self.removeName(simple.file_object);
                break :blk null;
            },
            66 => blk: {
                _ = try decodeSimple(raw);
                break :blk null;
            },
            67, 68 => try self.observeReadWrite(raw, threads),
            69, 70, 71 => try self.observeInfo(raw, threads),
            else => null,
        };
    }

    fn observeName(self: *FileIoState, raw: decoder.RawEvent, paths: *path_mapping.PathMap, round: std.mem.Allocator) !?Event {
        const text = try decodeNameRecord(raw);
        const name = try self.copyWideBytes(text.bytes, round);
        const mapped = try paths.map(round, name, self.limits.max_path_units);
        const key = try pointer(raw, 0);
        const rundown = raw.opcode == 35 or raw.opcode == 36;
        try self.addName(key, raw.qpc, mapped, rundown);
        if (self.mode == .realtime and rundown) self.removeName(key);
        if (raw.opcode != 32 and raw.opcode != 35) return null;
        return .{ .name = .{
            .opcode = raw.opcode,
            .file_key = key,
            .file_name = mapped,
        } };
    }

    fn observeCreate(self: *FileIoState, raw: decoder.RawEvent, paths: *path_mapping.PathMap, threads: *const thread_process.History, round: std.mem.Allocator) !?Event {
        const fields = try decodeCreate(raw);
        const name = try self.copyWideBytes(fields.name.bytes, round);
        const mapped = try paths.map(round, name, self.limits.max_path_units);
        const tid = resolveThreadId(raw, fields.thread_id_offset) catch |err| return err;
        const pid = resolveProcessId(raw, tid, threads);
        try self.addName(fields.file_object, raw.qpc, mapped, false);
        return .{ .create = .{
            .qpc = raw.qpc,
            .process_id = pid,
            .thread_id = tid,
            .irp = fields.irp,
            .file_object = fields.file_object,
            .create_options = fields.create_options,
            .create_disposition = fields.create_disposition,
            .file_attributes = fields.file_attributes,
            .share_access = fields.share_access,
            .file_name = mapped,
        } };
    }

    fn observeReadWrite(self: *FileIoState, raw: decoder.RawEvent, threads: *const thread_process.History) !?Event {
        const fields = try decodeReadWrite(raw);
        const tid = try resolveThreadId(raw, fields.thread_id_offset);
        return .{ .read_write = .{
            .qpc = raw.qpc,
            .process_id = resolveProcessId(raw, tid, threads),
            .thread_id = tid,
            .kind = if (raw.opcode == 68) .write else .read,
            .offset = fields.offset,
            .irp = fields.irp,
            .file_object = fields.file_object,
            .file_key = fields.file_key,
            .io_size = fields.io_size,
            .io_flags = fields.io_flags,
            .file_name = self.mappedName(fields.file_key, fields.file_object, raw.qpc),
        } };
    }

    fn observeInfo(self: *FileIoState, raw: decoder.RawEvent, threads: *const thread_process.History) !?Event {
        const fields = try decodeInfo(raw);
        const tid = try resolveThreadId(raw, fields.thread_id_offset);
        return .{ .info = .{
            .qpc = raw.qpc,
            .process_id = resolveProcessId(raw, tid, threads),
            .thread_id = tid,
            .kind = switch (raw.opcode) {
                69 => .set_info,
                70 => .delete,
                71 => .rename,
                else => unreachable,
            },
            .irp = fields.irp,
            .file_object = fields.file_object,
            .file_key = fields.file_key,
            .extra_info = fields.extra_info,
            .info_class = fields.info_class,
            .file_name = self.mappedName(fields.file_key, fields.file_object, raw.qpc),
        } };
    }
};

fn isSupportedOpcode(opcode: u8) bool {
    return switch (opcode) {
        0, 32, 35, 36, 64, 65, 66, 67, 68, 69, 70, 71 => true,
        else => false,
    };
}

const Text = struct { bytes: []const u8, next: usize };
fn wideString(payload: []const u8, offset: usize) DecodeError!Text {
    if (offset > payload.len or offset % 2 != 0) return error.InvalidPayload;
    var end = offset;
    while (end <= payload.len and payload.len - end >= 2) : (end += 2) {
        if (payload[end] == 0 and payload[end + 1] == 0) return .{ .bytes = payload[offset..end], .next = end + 2 };
    }
    return error.TruncatedPayload;
}

fn hostOffset(raw: decoder.RawEvent, offset: usize, pointer_count: usize) usize {
    return offset + (@as(usize, raw.pointer_size) - 4) * pointer_count;
}

fn uint(comptime T: type, payload: []const u8, offset: usize) DecodeError!T {
    if (offset > payload.len or payload.len - offset < @sizeOf(T)) return error.TruncatedPayload;
    return std.mem.readInt(T, payload[offset..][0..@sizeOf(T)], .little);
}

pub fn pointer(raw: decoder.RawEvent, offset: usize) DecodeError!u64 {
    return if (raw.pointer_size == 4) try uint(u32, raw.payload, offset) else try uint(u64, raw.payload, offset);
}

fn exact(payload: []const u8, length: usize) DecodeError!void {
    if (payload.len < length) return error.TruncatedPayload;
    if (payload.len != length) return error.InvalidPayload;
}

fn minimum(payload: []const u8, length: usize) DecodeError!void {
    if (payload.len < length) return error.TruncatedPayload;
}

fn decodeNameRecord(raw: decoder.RawEvent) DecodeError!Text {
    const result = try wideString(raw.payload, hostOffset(raw, 4, 1));
    if (raw.version <= 2) try exact(raw.payload, result.next) else try minimum(raw.payload, result.next);
    return result;
}

const CreateFields = struct {
    irp: u64,
    file_object: u64,
    create_options: u32,
    create_disposition: u8,
    file_attributes: u32,
    share_access: u32,
    thread_id_offset: usize,
    name: Text,
};

fn decodeCreate(raw: decoder.RawEvent) DecodeError!CreateFields {
    const old_layout = raw.version <= 2;
    const name = try wideString(raw.payload, hostOffset(raw, 24, if (old_layout) 3 else 2));
    if (raw.version == 2 or raw.version == 3) try exact(raw.payload, name.next) else try minimum(raw.payload, name.next);
    return .{
        .irp = try pointer(raw, 0),
        .file_object = try pointer(raw, hostOffset(raw, if (old_layout) 8 else 4, if (old_layout) 2 else 1)),
        .create_options = (try uint(u32, raw.payload, hostOffset(raw, 12, if (old_layout) 3 else 2))) & 0x00ff_ffff,
        .create_disposition = try uint(u8, raw.payload, hostOffset(raw, 15, if (old_layout) 3 else 2)),
        .file_attributes = try uint(u32, raw.payload, hostOffset(raw, 16, if (old_layout) 3 else 2)),
        .share_access = try uint(u32, raw.payload, hostOffset(raw, 20, if (old_layout) 3 else 2)),
        .thread_id_offset = hostOffset(raw, if (old_layout) 4 else 8, if (old_layout) 1 else 2),
        .name = name,
    };
}

const SimpleFields = struct { file_object: u64, file_key: u64, thread_id_offset: usize };
pub fn decodeSimple(raw: decoder.RawEvent) DecodeError!SimpleFields {
    const old_layout = raw.version <= 2;
    const end = if (raw.version == 2)
        hostOffset(raw, 16, 4)
    else if (raw.version == 3)
        hostOffset(raw, 16, 3)
    else
        hostOffset(raw, 16, if (old_layout) 4 else 3);
    if (raw.version == 2 or raw.version == 3) try exact(raw.payload, end) else try minimum(raw.payload, end);
    return .{
        .file_object = try pointer(raw, hostOffset(raw, if (old_layout) 8 else 4, if (old_layout) 2 else 1)),
        .file_key = try pointer(raw, hostOffset(raw, if (old_layout) 12 else 8, if (old_layout) 3 else 2)),
        .thread_id_offset = hostOffset(raw, if (old_layout) 4 else 12, if (old_layout) 1 else 3),
    };
}

const ReadWriteFields = struct {
    offset: i64,
    irp: u64,
    file_object: u64,
    file_key: u64,
    io_size: u32,
    io_flags: u32,
    thread_id_offset: usize,
};

fn decodeReadWrite(raw: decoder.RawEvent) DecodeError!ReadWriteFields {
    const old_layout = raw.version <= 2;
    const end = if (raw.version == 2) hostOffset(raw, 32, 4) else hostOffset(raw, 32, 3);
    if (raw.version == 2) try exact(raw.payload, end) else try minimum(raw.payload, end);
    return .{
        .offset = try uint(i64, raw.payload, 0),
        .irp = try pointer(raw, 8),
        .file_object = try pointer(raw, hostOffset(raw, if (old_layout) 16 else 12, if (old_layout) 2 else 1)),
        .file_key = try pointer(raw, hostOffset(raw, if (old_layout) 20 else 16, if (old_layout) 3 else 2)),
        .io_size = try uint(u32, raw.payload, hostOffset(raw, 24, if (old_layout) 4 else 3)),
        .io_flags = try uint(u32, raw.payload, hostOffset(raw, 28, if (old_layout) 4 else 3)),
        .thread_id_offset = hostOffset(raw, if (old_layout) 12 else 20, if (old_layout) 1 else 3),
    };
}

const InfoFields = struct {
    irp: u64,
    file_object: u64,
    file_key: u64,
    extra_info: u64,
    info_class: u32,
    thread_id_offset: usize,
};

fn decodeInfo(raw: decoder.RawEvent) DecodeError!InfoFields {
    const old_layout = raw.version <= 2;
    const end = if (raw.version == 2)
        hostOffset(raw, 24, 5)
    else if (raw.version == 3)
        hostOffset(raw, 24, 4)
    else
        hostOffset(raw, 24, if (old_layout) 5 else 4);
    if (raw.version == 2 or raw.version == 3) try exact(raw.payload, end) else try minimum(raw.payload, end);
    return .{
        .irp = try pointer(raw, 0),
        .file_object = try pointer(raw, hostOffset(raw, if (old_layout) 8 else 4, if (old_layout) 2 else 1)),
        .file_key = try pointer(raw, hostOffset(raw, if (old_layout) 12 else 8, if (old_layout) 3 else 2)),
        .extra_info = try pointer(raw, hostOffset(raw, if (old_layout) 16 else 12, if (old_layout) 4 else 3)),
        .info_class = try uint(u32, raw.payload, hostOffset(raw, 20, if (old_layout) 5 else 4)),
        .thread_id_offset = hostOffset(raw, if (old_layout) 4 else 16, if (old_layout) 1 else 4),
    };
}

pub fn resolveThreadId(raw: decoder.RawEvent, payload_offset: usize) DecodeError!u32 {
    return if (raw.header_thread_id == std.math.maxInt(u32)) try uint(u32, raw.payload, payload_offset) else raw.header_thread_id;
}

pub fn resolveProcessId(raw: decoder.RawEvent, thread_id: u32, threads: *const thread_process.History) u32 {
    if (raw.header_process_id != std.math.maxInt(u32)) return raw.header_process_id;
    return threads.lookup(thread_id, raw.qpc) orelse std.math.maxInt(u32);
}

fn testRaw(opcode: u8, qpc: i64, payload: []const u8) decoder.RawEvent {
    return .{
        .provider = file_io_task_guid,
        .opcode = opcode,
        .version = 2,
        .pointer_size = 8,
        .qpc = qpc,
        .processor = 0,
        .header_process_id = 1,
        .header_thread_id = 2,
        .payload = payload,
    };
}

fn testNamePayload(a: std.mem.Allocator, key: u64, name: []const u8) ![]u8 {
    const payload = try a.alloc(u8, 8 + name.len * 2 + 2);
    @memset(payload, 0);
    std.mem.writeInt(u64, payload[0..8], key, .little);
    for (name, 0..) |unit, index| payload[8 + index * 2] = unit;
    return payload;
}

test "recorded FileIO history keeps backdated names and rundown EOF identities" {
    var state = try FileIoState.init(std.testing.allocator, .{ .max_entries = 8, .max_versions = 16, .max_name_units = 64, .max_path_units = 128, .byte_cap = 65536 }, .recorded);
    defer state.deinit();
    try state.addName(7, 300, &.{ 'n', 'e', 'w' }, false);
    try state.addName(7, 100, &.{ 'o', 'l', 'd' }, false);
    try std.testing.expectEqualSlices(u16, &.{ 'o', 'l', 'd' }, state.lookupName(7, 250).?);
    try std.testing.expectEqualSlices(u16, &.{ 'n', 'e', 'w' }, state.lookupName(7, 300).?);

    // A FileIORundown added to an empty HistoryDictionary key starts at QPC 0,
    // even when the rundown record appears at recorded EOF with a later QPC.
    try state.addName(9, 900, &.{ 'e', 'o', 'f' }, true);
    try std.testing.expectEqualSlices(u16, &.{ 'e', 'o', 'f' }, state.lookupName(9, 0).?);
    try std.testing.expectEqualSlices(u16, &.{ 'e', 'o', 'f' }, state.lookupName(9, 899).?);

    // File-key reuse adds a new interval; old recorded QPCs still resolve.
    try state.addName(7, 500, &.{ 'r', 'e', 'u', 's', 'e' }, false);
    try std.testing.expectEqualSlices(u16, &.{ 'n', 'e', 'w' }, state.lookupName(7, 350).?);
    try std.testing.expectEqualSlices(u16, &.{ 'r', 'e', 'u', 's', 'e' }, state.lookupName(7, 500).?);
}

test "equal FileIO names share storage while retaining every timestamp boundary" {
    var state = try FileIoState.init(std.testing.allocator, .{ .max_entries = 8, .max_versions = 16, .byte_cap = 65536 }, .recorded);
    defer state.deinit();
    try state.addName(7, 100, &.{ 's', 'a', 'm', 'e' }, false);
    try state.addName(7, 300, &.{ 's', 'a', 'm', 'e' }, false);
    try state.addName(9, 100, &.{ 's', 'a', 'm', 'e' }, false);
    try std.testing.expectEqual(@as(usize, 3), state.version_count);
    try std.testing.expectEqual(@as(usize, 1), state.names.count());
    try std.testing.expectEqual(state.histories.get(7).?.versions.items[0].name.ptr, state.histories.get(9).?.versions.items[0].name.ptr);
    try state.addName(7, 200, &.{ 'b', 'a', 'c', 'k' }, false);
    try std.testing.expectEqualSlices(u16, &.{ 'b', 'a', 'c', 'k' }, state.lookupName(7, 250).?);
    try std.testing.expectEqualSlices(u16, &.{ 's', 'a', 'm', 'e' }, state.lookupName(7, 300).?);
    try state.addName(7, 300, &.{ 'n', 'e', 'w' }, false);
    state.removeName(7);
    try std.testing.expectEqual(@as(usize, 1), state.names.count());
    try std.testing.expectEqualSlices(u16, &.{ 's', 'a', 'm', 'e' }, state.lookupName(9, 100).?);
    state.removeName(9);
    try std.testing.expectEqual(@as(usize, 0), state.names.count());
}

test "realtime FileDelete and Cleanup retire entire name histories" {
    var state = try FileIoState.init(std.testing.allocator, .{ .max_entries = 8, .max_versions = 16, .max_name_units = 64, .max_path_units = 128, .byte_cap = 65536 }, .realtime);
    defer state.deinit();
    try state.addName(7, 100, &.{ 'o', 'l', 'd' }, false);
    state.removeName(7);
    try std.testing.expect(state.lookupName(7, 100) == null);
    try state.addName(7, 200, &.{ 'n', 'e', 'w' }, false);
    try std.testing.expectEqualSlices(u16, &.{ 'n', 'e', 'w' }, state.lookupName(7, 200).?);
    // A recorded-style backdated lookup after live address reuse has only the
    // currently retained chain, exactly as HistoryDictionary.Remove behaves.
    try std.testing.expect(state.lookupName(7, 150) == null);
}

test "FileIO records apply mode-specific removals and map names when added" {
    var threads = try thread_process.History.init(std.testing.allocator, 8, 65536, false);
    defer threads.deinit();
    var paths = path_mapping.PathMap.init(std.testing.allocator);
    defer paths.deinit();
    var round = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer round.deinit();
    const scratch = round.allocator();

    var recorded = try FileIoState.init(std.testing.allocator, .{ .max_entries = 8, .max_versions = 32, .max_name_units = 128, .max_path_units = 128, .byte_cap = 65536 }, .recorded);
    defer recorded.deinit();
    const delete_payload = try testNamePayload(scratch, 7, "\\Device\\Later\\file.sys");
    const delete = (try recorded.observe(testRaw(35, 900, delete_payload), &paths, &threads, scratch)).?;
    switch (delete) {
        .name => |value| try std.testing.expectEqualSlices(u16, &.{ '\\', 'D', 'e', 'v', 'i', 'c', 'e', '\\', 'L', 'a', 't', 'e', 'r', '\\', 'f', 'i', 'l', 'e', '.', 's', 'y', 's' }, value.file_name),
        else => return error.UnexpectedEventKind,
    }
    // FileDelete is a rundown addition in the pinned parser. For a new key it
    // starts at zero, and recorded mode retains it at QPCs before the event.
    try std.testing.expectEqualSlices(u16, &.{ '\\', 'D', 'e', 'v', 'i', 'c', 'e', '\\', 'L', 'a', 't', 'e', 'r', '\\', 'f', 'i', 'l', 'e', '.', 's', 'y', 's' }, recorded.lookupName(7, 0).?);
    const rundown_payload = try testNamePayload(scratch, 9, "\\Device\\Later\\eof.sys");
    try std.testing.expect((try recorded.observe(testRaw(36, 950, rundown_payload), &paths, &threads, scratch)) == null);
    try std.testing.expectEqualSlices(u16, &.{ '\\', 'D', 'e', 'v', 'i', 'c', 'e', '\\', 'L', 'a', 't', 'e', 'r', '\\', 'e', 'o', 'f', '.', 's', 'y', 's' }, recorded.lookupName(9, 0).?);
    const late_prefix = [_]u8{ '\\', 0, 'D', 0, 'e', 0, 'v', 0, 'i', 0, 'c', 0, 'e', 0, '\\', 0, 'L', 0, 'a', 0, 't', 0, 'e', 0, 'r', 0, '\\', 0 };
    const late_drive = [_]u8{ 'Z', 0, ':', 0, '\\', 0 };
    try paths.addVolumeMappingUtf16Le(&late_prefix, &late_drive);
    // TraceEvent stores FileName (already KernelToUser-converted) in history;
    // a namespace mapping arriving later does not rewrite that saved value.
    try std.testing.expectEqualSlices(u16, &.{ '\\', 'D', 'e', 'v', 'i', 'c', 'e', '\\', 'L', 'a', 't', 'e', 'r', '\\', 'f', 'i', 'l', 'e', '.', 's', 'y', 's' }, recorded.lookupName(7, 0).?);

    var realtime = try FileIoState.init(std.testing.allocator, .{ .max_entries = 8, .max_versions = 32, .max_name_units = 128, .max_path_units = 128, .byte_cap = 65536 }, .realtime);
    defer realtime.deinit();
    const live_delete_payload = try testNamePayload(scratch, 11, "\\Device\\Live\\file.sys");
    _ = try realtime.observe(testRaw(35, 100, live_delete_payload), &paths, &threads, scratch);
    try std.testing.expect(realtime.lookupName(11, 100) == null);

    var cleanup_payload = @as([32]u8, @splat(0));
    std.mem.writeInt(u64, cleanup_payload[16..24], 12, .little);
    try recorded.addName(12, 50, &.{ 'r', 'e', 'c' }, false);
    try realtime.addName(12, 50, &.{ 'l', 'i', 'v' }, false);
    _ = try realtime.observe(testRaw(65, 200, &cleanup_payload), &paths, &threads, scratch);
    _ = try recorded.observe(testRaw(65, 200, &cleanup_payload), &paths, &threads, scratch);
    try std.testing.expect(realtime.lookupName(12, 100) == null);
    try std.testing.expectEqualSlices(u16, &.{ 'r', 'e', 'c' }, recorded.lookupName(12, 100).?);
}

test "FileIO identifies the task GUID and rejects malformed recognized names" {
    const logical_guid: decoder.Guid = .{
        .data1 = 0x9e814aad,
        .data2 = 0x3204,
        .data3 = 0x11d2,
        .data4 = .{ 0x9a, 0x82, 0, 0x60, 8, 0xa8, 0x69, 0x39 },
    };
    const malformed = [_]u8{ 7, 0, 0, 0, 0, 0, 0, 0, 'X', 0 };
    const raw: decoder.RawEvent = .{
        .provider = logical_guid,
        .opcode = 0,
        .version = 2,
        .pointer_size = 8,
        .qpc = 1,
        .processor = 0,
        .header_process_id = 1,
        .header_thread_id = 2,
        .payload = &malformed,
    };
    var state = try FileIoState.init(std.testing.allocator, .{ .byte_cap = 65536 }, .recorded);
    defer state.deinit();
    var paths = path_mapping.PathMap.init(std.testing.allocator);
    defer paths.deinit();
    var threads = try thread_process.History.init(std.testing.allocator, 8, 65536, false);
    defer threads.deinit();
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    try std.testing.expect((try state.observe(raw, &paths, &threads, arena.allocator())) == null);
    const recognized: decoder.RawEvent = .{ .provider = file_io_task_guid, .opcode = 0, .version = 2, .pointer_size = 8, .qpc = 1, .processor = 0, .header_process_id = 1, .header_thread_id = 2, .payload = &malformed };
    try std.testing.expectError(error.TruncatedPayload, state.observe(recognized, &paths, &threads, arena.allocator()));
}

test "FileIOCreate and FileIOWrite repair sentinel identity and resolve FileObject names" {
    var state = try FileIoState.init(std.testing.allocator, .{ .max_entries = 8, .max_versions = 16, .max_name_units = 128, .max_path_units = 128, .byte_cap = 65536 }, .recorded);
    defer state.deinit();
    var paths = path_mapping.PathMap.init(std.testing.allocator);
    defer paths.deinit();
    var threads = try thread_process.History.init(std.testing.allocator, 8, 65536, false);
    defer threads.deinit();
    try threads.start(55, 77);
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();

    var create_payload = @as([52]u8, @splat(0));
    std.mem.writeInt(u64, create_payload[0..8], 0x111, .little);
    std.mem.writeInt(u32, create_payload[8..12], 55, .little);
    std.mem.writeInt(u64, create_payload[16..24], 0x44, .little);
    std.mem.writeInt(u32, create_payload[24..28], 0xff00_0102, .little);
    create_payload[27] = 3;
    std.mem.writeInt(u32, create_payload[28..32], 0x20, .little);
    std.mem.writeInt(u32, create_payload[32..36], 7, .little);
    for ("C:\\file", 0..) |unit, index| create_payload[36 + index * 2] = unit;
    var create_raw = testRaw(64, 100, &create_payload);
    create_raw.header_process_id = std.math.maxInt(u32);
    create_raw.header_thread_id = std.math.maxInt(u32);
    const created = (try state.observe(create_raw, &paths, &threads, arena.allocator())).?;
    switch (created) {
        .create => |value| {
            try std.testing.expectEqual(@as(u32, 77), value.process_id);
            try std.testing.expectEqual(@as(u32, 55), value.thread_id);
            try std.testing.expectEqual(@as(u64, 0x44), value.file_object);
            try std.testing.expectEqual(@as(u32, 0x0001_02), value.create_options);
            try std.testing.expectEqual(@as(u8, 3), value.create_disposition);
            try std.testing.expectEqualSlices(u16, &.{ 'C', ':', '\\', 'f', 'i', 'l', 'e' }, value.file_name);
        },
        else => return error.UnexpectedEventKind,
    }

    var write_payload = @as([48]u8, @splat(0));
    std.mem.writeInt(i64, write_payload[0..8], 0x1234, .little);
    std.mem.writeInt(u64, write_payload[8..16], 0x222, .little);
    std.mem.writeInt(u32, write_payload[16..20], 55, .little);
    std.mem.writeInt(u64, write_payload[24..32], 0x44, .little);
    std.mem.writeInt(u64, write_payload[32..40], 0x99, .little);
    std.mem.writeInt(u32, write_payload[40..44], 0x8000_0001, .little);
    std.mem.writeInt(u32, write_payload[44..48], 0x11, .little);
    var write_raw = testRaw(68, 200, &write_payload);
    write_raw.header_process_id = std.math.maxInt(u32);
    write_raw.header_thread_id = std.math.maxInt(u32);
    const written = (try state.observe(write_raw, &paths, &threads, arena.allocator())).?;
    switch (written) {
        .read_write => |value| {
            try std.testing.expectEqual(ReadWriteKind.write, value.kind);
            try std.testing.expectEqual(@as(u32, 77), value.process_id);
            try std.testing.expectEqual(@as(u32, 55), value.thread_id);
            try std.testing.expectEqual(@as(u32, 0x8000_0001), value.io_size);
            try std.testing.expectEqual(@as(i64, 0x1234), value.offset);
            try std.testing.expectEqualSlices(u16, &.{ 'C', ':', '\\', 'f', 'i', 'l', 'e' }, value.file_name.?);
        },
        else => return error.UnexpectedEventKind,
    }
}

test "FileIO histories enforce entry, version, and owner-byte caps" {
    // Leave room for the hash table's minimum allocation; this owner tests
    // entry/version limits. The independent one-byte owner below tests bytes.
    var state = try FileIoState.init(std.testing.allocator, .{ .max_entries = 1, .max_versions = 1, .max_name_units = 8, .max_path_units = 8, .byte_cap = 4096 }, .recorded);
    defer state.deinit();
    try state.addName(1, 1, &.{'a'}, false);
    try std.testing.expectError(error.CapacityExceeded, state.addName(1, 2, &.{'b'}, false));
    try std.testing.expectError(error.CapacityExceeded, state.addName(2, 1, &.{'c'}, false));
    try std.testing.expectEqualSlices(u16, &.{'a'}, state.lookupName(1, 1).?);
    var tiny = try FileIoState.init(std.testing.allocator, .{ .max_entries = 1, .max_versions = 1, .max_name_units = 64, .max_path_units = 64, .byte_cap = 1 }, .recorded);
    defer tiny.deinit();
    try std.testing.expectError(error.OutOfMemory, tiny.addName(1, 1, &.{'a'}, false));
    try std.testing.expectEqual(@as(usize, 0), tiny.budget.current);
    try std.testing.expect(tiny.budget.failures > 0);
}
