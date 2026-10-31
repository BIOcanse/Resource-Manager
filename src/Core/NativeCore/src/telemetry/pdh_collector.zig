const std = @import("std");
pub const ExternalScratch = @import("pdh_scratch.zig");
const PhysicalNetwork = @import("physical_network.zig");
const ResultCode = @import("../common/result_codes.zig").ResultCode;

pub const abi_version: u32 = 0x0002_0000;

const PDH_MORE_DATA: i32 = @bitCast(@as(u32, 0x8000_07d2));
const PDH_FMT_DOUBLE: u32 = 0x0000_0200;
const PDH_CSTATUS_VALID_DATA: u32 = 0;
const PDH_CSTATUS_NEW_DATA: u32 = 1;
const MAX_ARRAY_BUFFER_BYTES = ExternalScratch.maximum_request;

const PdhHandle = ?*anyopaque;

extern "pdh" fn PdhOpenQueryW(
    data_source: ?[*:0]const u16,
    user_data: usize,
    query: *PdhHandle,
) callconv(.winapi) i32;

extern "pdh" fn PdhAddEnglishCounterW(
    query: PdhHandle,
    counter_path: [*:0]const u16,
    user_data: usize,
    counter: *PdhHandle,
) callconv(.winapi) i32;

extern "pdh" fn PdhCollectQueryData(query: PdhHandle) callconv(.winapi) i32;

extern "pdh" fn PdhGetFormattedCounterValue(
    counter: PdhHandle,
    format: u32,
    counter_type: ?*u32,
    value: *PdhFormattedCounterValue,
) callconv(.winapi) i32;

extern "pdh" fn PdhGetFormattedCounterArrayW(
    counter: PdhHandle,
    format: u32,
    buffer_size: *u32,
    item_count: *u32,
    item_buffer: ?[*]PdhFormattedCounterValueItem,
) callconv(.winapi) i32;

extern "pdh" fn PdhCloseQuery(query: PdhHandle) callconv(.winapi) i32;
extern "kernel32" fn GetTickCount64() callconv(.winapi) u64;
extern "kernel32" fn QueryPerformanceCounter(value: *i64) callconv(.winapi) i32;
extern "kernel32" fn QueryPerformanceFrequency(value: *i64) callconv(.winapi) i32;

const FileTime = extern struct { low: u32, high: u32 };
const RawCounter = extern struct { status: u32, timestamp: FileTime, first: i64, second: i64, multi_count: u32 };
const RawCounterItem = extern struct { name: ?[*:0]const u16, value: RawCounter };
extern "pdh" fn PdhGetRawCounterArrayW(counter: PdhHandle, size: *u32, count: *u32, buffer: ?[*]RawCounterItem) callconv(.winapi) i32;
extern "pdh" fn PdhGetCounterTimeBase(counter: PdhHandle, frequency: *i64) callconv(.winapi) i32;
extern "kernel32" fn GetSystemTimeAsFileTime(value: *FileTime) callconv(.winapi) void;
extern "kernel32" fn LocalFileTimeToFileTime(local: *const FileTime, utc: *FileTime) callconv(.winapi) i32;
const EngineSampleKey = struct { luid: u64, pid: u32, physical: u32, engine: u32 };
const EngineSampleTime = struct { utc: i64, counter: i64, frequency: i64, unique: bool = true };
// Sidecar only: legacy FrameHeader/GpuEngineRow and formatted usage ABI remain unchanged.
pub const GpuEngineWindow = extern struct {
    adapter_luid: u64, process_id: u32, physical_index: u32, engine_index: u32, reserved: u32 = 0,
    begin_filetime: i64, end_filetime: i64, begin_counter: i64, end_counter: i64, counter_frequency: i64,
};

const PdhFormattedCounterValue = extern struct {
    c_status: u32,
    padding: u32,
    double_value: f64,
};

const PdhFormattedCounterValueItem = extern struct {
    name: ?[*:0]const u16,
    value: PdhFormattedCounterValue,
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    baseline_reset_interval_ms: u32,
    reserved: u32,
};

pub const FrameFlags = struct {
    pub const has_data: u32 = 1 << 0;
    pub const topology_rebuilt: u32 = 1 << 1;
    pub const baseline_collected: u32 = 1 << 2;
    pub const partial: u32 = 1 << 3;
    pub const disk_complete: u32 = 1 << 4;
    pub const network_complete: u32 = 1 << 5;
    pub const gpu_engine_complete: u32 = 1 << 6;
    pub const gpu_memory_complete: u32 = 1 << 7;
};

pub const FrameHeader = extern struct {
    abi_version: u32,
    struct_size: u32,
    sequence: u64,
    captured_tick_ms: u64,
    flags: u32,
    native_status: i32,
    engine_row_count: u32,
    memory_row_count: u32,
    topology_generation: u32,
    reserved: u32,
};

pub const EngineClass = enum(u32) {
    other = 0,
    ray_tracing = 1,
    compute = 2,
};

pub const GpuEngineRow = extern struct {
    adapter_luid: u64,
    process_id: u32,
    engine_class: u32,
    physical_index: u32,
    engine_index: u32,
    usage_percent: f64,
};

pub const GpuMemoryRow = extern struct {
    adapter_luid: u64,
    process_id: u32,
    reserved: u32,
    dedicated_bytes: f64,
};

pub const IoValidMask = struct {
    pub const disk_active: u32 = 1 << 0;
    pub const disk_read: u32 = 1 << 1;
    pub const disk_write: u32 = 1 << 2;
    pub const disk_queue: u32 = 1 << 3;
    pub const network_receive: u32 = 1 << 4;
    pub const network_send: u32 = 1 << 5;
    pub const network_utilization: u32 = 1 << 6;
    pub const network_bandwidth: u32 = 1 << 7;
};

pub const SystemIo = extern struct {
    struct_size: u32,
    valid_mask: u32,
    disk_active_percent: f64,
    disk_read_bytes_per_second: f64,
    disk_write_bytes_per_second: f64,
    disk_queue_length: f64,
    network_receive_bytes_per_second: f64,
    network_send_bytes_per_second: f64,
    network_utilization_percent: f64,
    network_bandwidth_bits_per_second: f64,
};

pub const Diagnostics = extern struct {
    abi_version: u32,
    struct_size: u32,
    topology_generation: u32,
    counter_count: u32,
    valid_counter_count: u32,
    engine_row_count: u32,
    memory_row_count: u32,
    reserved: u32,
    collect_duration_ns: u64,
    read_duration_ns: u64,
    frame_duration_ns: u64,
};

const GpuCounterMetadata = struct {
    adapter_luid: u64,
    process_id: u32,
    engine_class: EngineClass,
    physical_index: u32,
    engine_index: u32,
};

const CounterSet = struct {
    disk_idle: PdhHandle = null,
    disk_read: PdhHandle = null,
    disk_write: PdhHandle = null,
    disk_queue: PdhHandle = null,
    gpu_engine: PdhHandle = null,
    gpu_memory: PdhHandle = null,

    fn count(self: *const CounterSet) u32 {
        var result: u32 = 0;
        inline for (@typeInfo(CounterSet).@"struct".field_names) |field_name| {
            if (@field(self, field_name) != null) result += 1;
        }
        return result;
    }
};

const ArrayBuffer = struct {
    words: std.ArrayList(u64) = .empty,

    fn deinit(self: *ArrayBuffer, allocator: std.mem.Allocator) void {
        self.words.deinit(allocator);
    }

    fn byteCapacity(self: *const ArrayBuffer, scratch: ?*ExternalScratch.Scratch) u32 {
        if (scratch) |external| return external.region.length;
        return @intCast(self.words.items.len * @sizeOf(u64));
    }

    fn bytePointer(self: *ArrayBuffer, scratch: ?*ExternalScratch.Scratch) ?[*]u8 {
        if (scratch) |external| return external.region.address;
        return if (self.words.items.len == 0) null else @ptrCast(self.words.items.ptr);
    }

    fn ensureByteCapacity(self: *ArrayBuffer, allocator: std.mem.Allocator, required_bytes: u32, scratch: ?*ExternalScratch.Scratch) !void {
        if (scratch) |external| return external.ensure(required_bytes);
        if (required_bytes > MAX_ARRAY_BUFFER_BYTES) return error.ArrayBufferTooLarge;
        const required_words = (@as(usize, required_bytes) + @sizeOf(u64) - 1) / @sizeOf(u64);
        if (required_words > self.words.items.len) try self.words.resize(allocator, required_words);
    }
};

const ArrayBuffers = struct {
    gpu_engine: ArrayBuffer = .{},
    gpu_memory: ArrayBuffer = .{},

    fn deinit(self: *ArrayBuffers, allocator: std.mem.Allocator) void {
        self.gpu_engine.deinit(allocator);
        self.gpu_memory.deinit(allocator);
    }
};

const gpu_engine_path = std.unicode.utf8ToUtf16LeStringLiteral("\\GPU Engine(*)\\Utilization Percentage");
const gpu_memory_path = std.unicode.utf8ToUtf16LeStringLiteral("\\GPU Process Memory(*)\\Dedicated Usage");
const disk_idle_path = std.unicode.utf8ToUtf16LeStringLiteral("\\PhysicalDisk(_Total)\\% Idle Time");
const disk_read_path = std.unicode.utf8ToUtf16LeStringLiteral("\\PhysicalDisk(_Total)\\Disk Read Bytes/sec");
const disk_write_path = std.unicode.utf8ToUtf16LeStringLiteral("\\PhysicalDisk(_Total)\\Disk Write Bytes/sec");
const disk_queue_path = std.unicode.utf8ToUtf16LeStringLiteral("\\PhysicalDisk(_Total)\\Current Disk Queue Length");

pub const Collector = struct {
    allocator: std.mem.Allocator,
    external_scratch: bool = false,
    query: PdhHandle = null,
    counters: CounterSet = .{},
    array_buffers: ArrayBuffers = .{},
    engine_rows: std.ArrayList(GpuEngineRow) = .empty,
    memory_rows: std.ArrayList(GpuMemoryRow) = .empty,
    engine_windows: std.ArrayList(GpuEngineWindow) = .empty,
    engine_sample_times: std.AutoHashMapUnmanaged(EngineSampleKey, EngineSampleTime) = .empty,
    raw_engine_buffer: ArrayBuffer = .{},
    system_io: SystemIo = emptySystemIo(),
    network: PhysicalNetwork.Sampler = .{},
    diagnostics: Diagnostics = emptyDiagnostics(),
    topology_generation: u32 = 0,
    sequence: u64 = 0,
    captured_tick_ms: u64 = 0,
    last_collect_tick_ms: u64 = 0,
    pending_topology_refresh: bool = true,
    last_native_status: i32 = 0,

    pub fn create(config: *const Config) !*Collector {
        // Retained in the ABI/configuration shape for older profiles. A gap
        // between valid captures is the rate window, not a reason to discard it.
        _ = config.baseline_reset_interval_ms;
        const allocator = std.heap.page_allocator;
        const collector = try allocator.create(Collector);
        collector.* = .{ .allocator = allocator };
        return collector;
    }

    pub fn destroy(self: *Collector) void {
        const allocator = self.allocator;
        self.closeQuery();
        self.array_buffers.deinit(allocator);
        self.engine_rows.deinit(allocator);
        self.memory_rows.deinit(allocator);
        self.engine_windows.deinit(allocator);
        self.engine_sample_times.deinit(allocator);
        self.raw_engine_buffer.deinit(allocator);
        allocator.destroy(self);
    }

    pub fn requestTopologyRefresh(self: *Collector) void {
        self.pending_topology_refresh = true;
    }

    pub fn sample(self: *Collector, header: *FrameHeader) ResultCode {
        if (self.external_scratch) return .invalid_argument;
        return self.sampleCore(header, null);
    }

    pub fn sampleExternal(self: *Collector, header: *FrameHeader, provider: ExternalScratch.Provider) ResultCode {
        if (!self.external_scratch or provider.supply == null) return .invalid_argument;
        var scratch: ExternalScratch.Scratch = .{ .provider = provider };
        return self.sampleCore(header, &scratch);
    }

    // RuntimeRead: Sampled
    fn sampleCore(self: *Collector, header: *FrameHeader, scratch: ?*ExternalScratch.Scratch) ResultCode {
        if (self.pending_topology_refresh or self.query == null) {
            const rebuilt = self.rebuildQuery(scratch) catch {
                self.writeHeader(header, 0);
                return .out_of_memory;
            };
            if (rebuilt) {
                self.writeHeader(header, FrameFlags.topology_rebuilt | FrameFlags.baseline_collected);
                return .no_data;
            }
        }

        if (self.query == null) {
            self.writeHeader(header, 0);
            return .unavailable;
        }

        const now_tick_ms = GetTickCount64();
        const frame_started = performanceCounter();
        const collect_started = performanceCounter();
        var collect_utc_begin: FileTime = undefined;
        var collect_utc_end: FileTime = undefined;
        GetSystemTimeAsFileTime(&collect_utc_begin);
        const collect_status = PdhCollectQueryData(self.query);
        GetSystemTimeAsFileTime(&collect_utc_end);
        const collect_finished = performanceCounter();
        self.last_native_status = collect_status;
        if (collect_status != 0) {
            self.engine_sample_times.clearRetainingCapacity();
            self.engine_windows.clearRetainingCapacity();
            self.writeDiagnostics(frame_started, collect_started, collect_finished, collect_finished, collect_finished, 0);
            self.writeHeader(header, 0);
            return .pdh_error;
        }

        self.last_collect_tick_ms = now_tick_ms;
        self.resetFrameData();

        self.readEngineWindows(collect_utc_begin, collect_utc_end, scratch) catch {
            self.engine_windows.clearRetainingCapacity();
            self.engine_sample_times.clearRetainingCapacity();
        };
        const read_started = performanceCounter();
        var valid_value_count: usize = 0;
        var partial = false;

        self.readFixedCounters(&valid_value_count, &partial);
        const gpu_engine_complete = self.readGpuEngineRows(&valid_value_count, &partial, scratch) catch {
            const read_finished = performanceCounter();
            self.writeDiagnostics(frame_started, collect_started, collect_finished, read_started, read_finished, valid_value_count);
            self.writeHeader(header, FrameFlags.partial);
            return .out_of_memory;
        };
        const gpu_memory_complete = self.readGpuMemoryRows(&valid_value_count, &partial, scratch) catch {
            const read_finished = performanceCounter();
            self.writeDiagnostics(frame_started, collect_started, collect_finished, read_started, read_finished, valid_value_count);
            self.writeHeader(header, FrameFlags.partial);
            return .out_of_memory;
        };
        const network_complete = self.readNetworkCounters(now_tick_ms, &valid_value_count, &partial);
        // 磁盘这个域**只要有一项读到了就算有数**。
        //
        // 先前要求四项全齐（活动时间、读、写、队列），缺一项整个域不上报，
        // 于是界面上磁盘四项一起变暗 —— 而实测这几个计数器在系统层面都读得出来，
        // 只是不保证每一帧都同时到齐。
        //
        // 哪一项有值由它自己的位说了算（读取端按位取），域的标志只回答
        // "这条通道这一帧有没有东西"。一个计数器缺席不该把整组打掉。
        const disk_mask = IoValidMask.disk_active |
            IoValidMask.disk_read |
            IoValidMask.disk_write |
            IoValidMask.disk_queue;
        const disk_complete = (self.system_io.valid_mask & disk_mask) != 0;
        var domain_flags: u32 = 0;
        if (disk_complete) domain_flags |= FrameFlags.disk_complete;
        if (network_complete) domain_flags |= FrameFlags.network_complete;
        if (gpu_engine_complete) domain_flags |= FrameFlags.gpu_engine_complete;
        if (gpu_memory_complete) domain_flags |= FrameFlags.gpu_memory_complete;

        const read_finished = performanceCounter();
        if (valid_value_count == 0 and domain_flags == 0) {
            self.writeDiagnostics(frame_started, collect_started, collect_finished, read_started, read_finished, valid_value_count);
            self.writeHeader(header, if (partial) FrameFlags.partial else 0);
            return .no_data;
        }

        self.sequence +%= 1;
        self.captured_tick_ms = GetTickCount64();
        self.writeDiagnostics(frame_started, collect_started, collect_finished, read_started, read_finished, valid_value_count);
        self.writeHeader(header, FrameFlags.has_data | domain_flags | if (partial) FrameFlags.partial else 0);
        return .ok;
    }

    pub fn copyFrame(
        self: *Collector,
        expected_sequence: u64,
        engine_rows: ?[*]GpuEngineRow,
        engine_capacity: u32,
        memory_rows: ?[*]GpuMemoryRow,
        memory_capacity: u32,
        system_io: *SystemIo,
    ) ResultCode {
        if (self.sequence == 0 or expected_sequence != self.sequence) return .stale_frame;
        if (engine_capacity < self.engine_rows.items.len or memory_capacity < self.memory_rows.items.len) {
            return .buffer_too_small;
        }
        if (self.engine_rows.items.len > 0 and engine_rows == null) return .invalid_argument;
        if (self.memory_rows.items.len > 0 and memory_rows == null) return .invalid_argument;

        if (engine_rows) |destination| {
            @memcpy(destination[0..self.engine_rows.items.len], self.engine_rows.items);
        }
        if (memory_rows) |destination| {
            @memcpy(destination[0..self.memory_rows.items.len], self.memory_rows.items);
        }
        system_io.* = self.system_io;
        return .ok;
    }

    pub fn getDiagnostics(self: *const Collector, diagnostics: *Diagnostics) void {
        diagnostics.* = self.diagnostics;
    }

    // RuntimeRead: Sampled
    fn rebuildQuery(self: *Collector, scratch: ?*ExternalScratch.Scratch) !bool {
        self.pending_topology_refresh = false;
        var new_query: PdhHandle = null;
        const open_status = PdhOpenQueryW(null, 0, &new_query);
        self.last_native_status = open_status;
        if (open_status != 0 or new_query == null) {
            if (new_query != null) _ = PdhCloseQuery(new_query);
            return false;
        }

        var committed = false;
        defer if (!committed) {
            _ = PdhCloseQuery(new_query);
        };

        var new_counters: CounterSet = .{};
        addCounter(new_query, disk_idle_path, &new_counters.disk_idle);
        addCounter(new_query, disk_read_path, &new_counters.disk_read);
        addCounter(new_query, disk_write_path, &new_counters.disk_write);
        addCounter(new_query, disk_queue_path, &new_counters.disk_queue);
        addCounter(new_query, gpu_engine_path, &new_counters.gpu_engine);
        addCounter(new_query, gpu_memory_path, &new_counters.gpu_memory);
        if (new_counters.count() == 0) return false;

        var baseline_begin: FileTime = undefined;
        var baseline_end: FileTime = undefined;
        GetSystemTimeAsFileTime(&baseline_begin);
        const baseline_status = PdhCollectQueryData(new_query);
        GetSystemTimeAsFileTime(&baseline_end);
        self.last_native_status = baseline_status;
        if (baseline_status != 0) return false;

        try prepareArrayBuffer(self.allocator, new_counters.gpu_engine, &self.array_buffers.gpu_engine, scratch);
        try prepareArrayBuffer(self.allocator, new_counters.gpu_memory, &self.array_buffers.gpu_memory, scratch);

        if (self.query != null) _ = PdhCloseQuery(self.query);
        self.query = new_query;
        self.counters = new_counters;
        self.engine_sample_times.clearRetainingCapacity();
        self.engine_windows.clearRetainingCapacity();
        self.readEngineWindows(baseline_begin, baseline_end, scratch) catch { self.engine_sample_times.clearRetainingCapacity(); };
        self.topology_generation +%= 1;
        self.last_collect_tick_ms = GetTickCount64();
        self.network.reset();
        _ = self.network.read(self.last_collect_tick_ms);
        committed = true;
        return true;
    }

    fn closeQuery(self: *Collector) void {
        if (self.query != null) _ = PdhCloseQuery(self.query);
        self.query = null;
        self.counters = .{};
        self.last_collect_tick_ms = 0;
        self.engine_sample_times.clearRetainingCapacity();
        self.engine_windows.clearRetainingCapacity();
        self.network.reset();
    }

    fn resetFrameData(self: *Collector) void {
        self.engine_rows.clearRetainingCapacity();
        self.memory_rows.clearRetainingCapacity();
        self.system_io = emptySystemIo();
    }

    fn readFixedCounters(self: *Collector, valid_value_count: *usize, partial: *bool) void {
        self.readFixedCounter(
            self.counters.disk_idle,
            IoValidMask.disk_active,
            &self.system_io.disk_active_percent,
            true,
            valid_value_count,
            partial,
        );
        self.readFixedCounter(
            self.counters.disk_read,
            IoValidMask.disk_read,
            &self.system_io.disk_read_bytes_per_second,
            false,
            valid_value_count,
            partial,
        );
        self.readFixedCounter(
            self.counters.disk_write,
            IoValidMask.disk_write,
            &self.system_io.disk_write_bytes_per_second,
            false,
            valid_value_count,
            partial,
        );
        self.readFixedCounter(
            self.counters.disk_queue,
            IoValidMask.disk_queue,
            &self.system_io.disk_queue_length,
            false,
            valid_value_count,
            partial,
        );
    }

    fn readFixedCounter(
        self: *Collector,
        counter: PdhHandle,
        mask: u32,
        destination: *f64,
        idle_percent: bool,
        valid_value_count: *usize,
        partial: *bool,
    ) void {
        if (counter == null) {
            partial.* = true;
            return;
        }
        const value = readCounterValue(counter, &self.last_native_status) orelse {
            partial.* = true;
            return;
        };
        // Disk Time includes concurrent requests and can exceed 100%; the
        // activity field is the complement of the physical disk idle timer.
        destination.* = if (idle_percent) diskActivityFromIdle(value) else sanitizeNonNegative(value);
        self.system_io.valid_mask |= mask;
        valid_value_count.* += 1;
    }

    pub fn copyEngineWindows(self: *const Collector, expected: u64, rows: ?[*]GpuEngineWindow, capacity: u32, row_size: u32, count: *u32) ResultCode {
        count.* = @intCast(self.engine_windows.items.len);
        if (row_size != @sizeOf(GpuEngineWindow)) return .abi_mismatch;
        if (expected != self.sequence) return .stale_frame;
        if (capacity < self.engine_windows.items.len) return .buffer_too_small;
        if (self.engine_windows.items.len > 0 and rows == null) return .invalid_argument;
        if (rows) |destination| @memcpy(destination[0..self.engine_windows.items.len], self.engine_windows.items);
        return .ok;
    }

    fn readEngineWindows(self: *Collector, utc_begin: FileTime, utc_end: FileTime, scratch: ?*ExternalScratch.Scratch) !void {
        self.engine_windows.clearRetainingCapacity();
        var next: std.AutoHashMapUnmanaged(EngineSampleKey, EngineSampleTime) = .empty;
        errdefer next.deinit(self.allocator);
        var frequency: i64 = 0;
        if (PdhGetCounterTimeBase(self.counters.gpu_engine, &frequency) != 0 or frequency <= 0) {
            self.engine_sample_times.clearRetainingCapacity(); return;
        }
        var size: u32 = 0;
        var count: u32 = 0;
        if (PdhGetRawCounterArrayW(self.counters.gpu_engine, &size, &count, null) != PDH_MORE_DATA or size == 0) {
            self.engine_sample_times.clearRetainingCapacity(); return;
        }
        try self.raw_engine_buffer.ensureByteCapacity(self.allocator, size, scratch);
        size = self.raw_engine_buffer.byteCapacity(scratch);
        const items: [*]RawCounterItem = @ptrCast(@alignCast(self.raw_engine_buffer.bytePointer(scratch).?));
        if (PdhGetRawCounterArrayW(self.counters.gpu_engine, &size, &count, items) != 0 or count > 262144
            or @as(u64, count) * @sizeOf(RawCounterItem) > size) { self.engine_sample_times.clearRetainingCapacity(); return; }
        for (items[0..count]) |item| {
            if (item.value.status != PDH_CSTATUS_VALID_DATA and item.value.status != PDH_CSTATUS_NEW_DATA) continue;
            const name = item.name orelse continue;
            const metadata = parseGpuCounterMetadata(std.mem.span(name)) orelse continue;
            const utc = confirmRawUtc(item.value.timestamp, utc_begin, utc_end) orelse continue;
            if (item.value.second <= 0) continue;
            const key: EngineSampleKey = .{ .luid = metadata.adapter_luid, .pid = metadata.process_id, .physical = metadata.physical_index, .engine = metadata.engine_index };
            const entry = try next.getOrPut(self.allocator, key);
            if (entry.found_existing) { entry.value_ptr.unique = false; continue; }
            entry.value_ptr.* = .{ .utc = utc, .counter = item.value.second, .frequency = frequency };
        }
        var iterator = next.iterator();
        while (iterator.next()) |entry| {
            const current = entry.value_ptr.*;
            const previous = self.engine_sample_times.get(entry.key_ptr.*) orelse continue;
            if (!current.unique or !previous.unique or previous.frequency != current.frequency
                or previous.utc >= current.utc or previous.counter >= current.counter) continue;
            try self.engine_windows.append(self.allocator, .{ .adapter_luid = entry.key_ptr.luid, .process_id = entry.key_ptr.pid,
                .physical_index = entry.key_ptr.physical, .engine_index = entry.key_ptr.engine,
                .begin_filetime = previous.utc, .end_filetime = current.utc,
                .begin_counter = previous.counter, .end_counter = current.counter, .counter_frequency = frequency });
        }
        self.engine_sample_times.deinit(self.allocator);
        self.engine_sample_times = next;
    }
    fn readGpuEngineRows(self: *Collector, valid_value_count: *usize, partial: *bool, scratch: ?*ExternalScratch.Scratch) !bool {
        const items = try readCounterArray(
            self.allocator,
            self.counters.gpu_engine,
            &self.array_buffers.gpu_engine,
            &self.last_native_status,
            scratch,
        ) orelse {
            partial.* = true;
            return false;
        };

        for (items) |item| {
            const value = formattedValue(item.value) orelse continue;
            const name = item.name orelse continue;
            const metadata = parseGpuCounterMetadata(std.mem.span(name)) orelse continue;
            if (metadata.engine_index == std.math.maxInt(u32)) {
                partial.* = true;
                continue;
            }
            valid_value_count.* += 1;
            const usage = sanitizeNonNegative(value);
            try self.engine_rows.append(self.allocator, .{
                .adapter_luid = metadata.adapter_luid,
                .process_id = metadata.process_id,
                .engine_class = @intFromEnum(metadata.engine_class),
                .physical_index = metadata.physical_index,
                .engine_index = metadata.engine_index,
                .usage_percent = usage,
            });
        }
        return true;
    }

    fn readGpuMemoryRows(self: *Collector, valid_value_count: *usize, partial: *bool, scratch: ?*ExternalScratch.Scratch) !bool {
        const items = try readCounterArray(
            self.allocator,
            self.counters.gpu_memory,
            &self.array_buffers.gpu_memory,
            &self.last_native_status,
            scratch,
        ) orelse {
            partial.* = true;
            return false;
        };

        for (items) |item| {
            const value = formattedValue(item.value) orelse continue;
            const name = item.name orelse continue;
            const metadata = parseGpuCounterMetadata(std.mem.span(name)) orelse continue;
            valid_value_count.* += 1;
            const dedicated_bytes = sanitizeNonNegative(value);
            try self.memory_rows.append(self.allocator, .{
                .adapter_luid = metadata.adapter_luid,
                .process_id = metadata.process_id,
                .reserved = 0,
                .dedicated_bytes = dedicated_bytes,
            });
        }
        return true;
    }

    fn readNetworkCounters(self: *Collector, now_ms: u64, valid_value_count: *usize, partial: *bool) bool {
        const rates = self.network.read(now_ms) orelse {
            partial.* = true;
            return false;
        };
        self.system_io.network_receive_bytes_per_second = rates.receive;
        self.system_io.network_send_bytes_per_second = rates.send;
        self.system_io.network_bandwidth_bits_per_second = rates.bandwidth;
        self.system_io.network_utilization_percent = if (rates.bandwidth > 0)
            sanitizePercent((rates.receive + rates.send) * 8.0 * 100.0 / rates.bandwidth)
        else
            0;
        self.system_io.valid_mask |= IoValidMask.network_receive | IoValidMask.network_send |
            IoValidMask.network_bandwidth | IoValidMask.network_utilization;
        valid_value_count.* += 4;
        return true;
    }

    fn writeHeader(self: *const Collector, header: *FrameHeader, flags: u32) void {
        header.* = .{
            .abi_version = abi_version,
            .struct_size = @sizeOf(FrameHeader),
            .sequence = self.sequence,
            .captured_tick_ms = self.captured_tick_ms,
            .flags = flags,
            .native_status = self.last_native_status,
            .engine_row_count = @intCast(self.engine_rows.items.len),
            .memory_row_count = @intCast(self.memory_rows.items.len),
            .topology_generation = self.topology_generation,
            .reserved = 0,
        };
    }

    fn writeDiagnostics(
        self: *Collector,
        frame_started: i64,
        collect_started: i64,
        collect_finished: i64,
        read_started: i64,
        read_finished: i64,
        valid_value_count: usize,
    ) void {
        self.diagnostics = .{
            .abi_version = abi_version,
            .struct_size = @sizeOf(Diagnostics),
            .topology_generation = self.topology_generation,
            .counter_count = self.counters.count(),
            .valid_counter_count = @intCast(valid_value_count),
            .engine_row_count = @intCast(self.engine_rows.items.len),
            .memory_row_count = @intCast(self.memory_rows.items.len),
            .reserved = 0,
            .collect_duration_ns = performanceNanoseconds(collect_started, collect_finished),
            .read_duration_ns = performanceNanoseconds(read_started, read_finished),
            .frame_duration_ns = performanceNanoseconds(frame_started, read_finished),
        };
    }
};

fn addCounter(query: PdhHandle, path: [*:0]const u16, counter: *PdhHandle) void {
    if (PdhAddEnglishCounterW(query, path, 0, counter) != 0) counter.* = null;
}

fn prepareArrayBuffer(allocator: std.mem.Allocator, counter: PdhHandle, buffer: *ArrayBuffer, scratch: ?*ExternalScratch.Scratch) !void {
    if (counter == null) return;
    var required_bytes: u32 = 0;
    var item_count: u32 = 0;
    const status = PdhGetFormattedCounterArrayW(
        counter,
        PDH_FMT_DOUBLE,
        &required_bytes,
        &item_count,
        null,
    );
    if (status == PDH_MORE_DATA and required_bytes > 0) {
        try buffer.ensureByteCapacity(allocator, required_bytes, scratch);
    }
}

fn readCounterArray(
    allocator: std.mem.Allocator,
    counter: PdhHandle,
    buffer: *ArrayBuffer,
    last_native_status: *i32,
    scratch: ?*ExternalScratch.Scratch,
) !?[]const PdhFormattedCounterValueItem {
    return readCounterArrayUsing(PdhGetFormattedCounterArrayW, allocator, counter, buffer, last_native_status, scratch);
}

fn readCounterArrayUsing(
    read: *const fn (PdhHandle, u32, *u32, *u32, ?[*]PdhFormattedCounterValueItem) callconv(.winapi) i32,
    allocator: std.mem.Allocator,
    counter: PdhHandle,
    buffer: *ArrayBuffer,
    last_native_status: *i32,
    scratch: ?*ExternalScratch.Scratch,
) !?[]const PdhFormattedCounterValueItem {
    if (counter == null) return null;

    var attempt: u8 = 0;
    while (attempt < 3) : (attempt += 1) {
        var buffer_size = buffer.byteCapacity(scratch);
        var item_count: u32 = 0;
        const status = read(
            counter,
            PDH_FMT_DOUBLE,
            &buffer_size,
            &item_count,
            if (buffer.bytePointer(scratch)) |pointer| @ptrCast(@alignCast(pointer)) else null,
        );
        last_native_status.* = status;
        if (status == PDH_MORE_DATA) {
            if (buffer_size == 0) return null;
            try buffer.ensureByteCapacity(allocator, buffer_size, scratch);
            continue;
        }
        if (status != 0) return null;
        if (item_count == 0) return &.{};

        const required_item_bytes = @as(u64, item_count) * @sizeOf(PdhFormattedCounterValueItem);
        if (required_item_bytes > buffer.byteCapacity(scratch)) return null;
        const items: [*]const PdhFormattedCounterValueItem = @ptrCast(@alignCast(buffer.bytePointer(scratch).?));
        return items[0..item_count];
    }

    return null;
}

fn readCounterValue(counter: PdhHandle, last_native_status: *i32) ?f64 {
    var value: PdhFormattedCounterValue = undefined;
    const status = PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, null, &value);
    last_native_status.* = status;
    if (status != 0) return null;
    return formattedValue(value);
}

fn fileTimeInteger(time: FileTime) i64 {
    return @bitCast((@as(u64, time.high) << 32) | time.low);
}
test "formatted parser grows external storage on MORE_DATA and consumes names from its real address" {
    const Fixture = struct {
        bytes: [1024]u8 align(8) = undefined,
        calls: usize = 0,
        supplies: usize = 0,
        fn supply(context: ?*anyopaque, required: u32, output: *ExternalScratch.Region) callconv(.c) i32 {
            const self: *@This() = @ptrCast(@alignCast(context.?));
            self.supplies += 1;
            output.* = .{ .address = &self.bytes, .length = required };
            return 0;
        }
        fn read(counter: PdhHandle, _: u32, size: *u32, count: *u32, output: ?[*]PdhFormattedCounterValueItem) callconv(.winapi) i32 {
            const self: *@This() = @ptrCast(@alignCast(counter.?));
            self.calls += 1;
            const required: u32 = if (self.calls == 1) 128 else 1024;
            if (size.* < required) { size.* = required; return PDH_MORE_DATA; }
            const bytes: [*]u8 = @ptrCast(output.?);
            const name: [*:0]u16 = @ptrCast(@alignCast(bytes + 32));
            const source = std.unicode.utf8ToUtf16LeStringLiteral("pid_42_luid_0x00000001_0x00000002_phys_0_eng_3_engtype_3D");
            @memcpy(name[0..source.len + 1], source[0..source.len + 1]);
            output.?[0] = .{ .name = name, .value = .{ .c_status = 0, .padding = 0, .double_value = 37.5 } };
            count.* = 1;
            return 0;
        }
    };
    var fixture: Fixture = .{};
    var scratch: ExternalScratch.Scratch = .{ .provider = .{ .context = &fixture, .supply = Fixture.supply } };
    var buffer: ArrayBuffer = .{};
    defer buffer.deinit(std.testing.allocator);
    var status: i32 = 0;
    const rows = (try readCounterArrayUsing(Fixture.read, std.testing.allocator, &fixture, &buffer, &status, &scratch)).?;
    try std.testing.expectEqual(@as(usize, 3), fixture.calls);
    try std.testing.expectEqual(@as(usize, 2), fixture.supplies);
    try std.testing.expectEqual(@as(usize, 0), buffer.words.capacity);
    try std.testing.expectEqual(@intFromPtr(&fixture.bytes), @intFromPtr(rows.ptr));
    try std.testing.expectEqual(@as(f64, 37.5), formattedValue(rows[0].value).?);
    const metadata = parseGpuCounterMetadata(std.mem.span(rows[0].name.?)).?;
    try std.testing.expectEqual(@as(u32, 42), metadata.process_id);
    try std.testing.expectEqual(@as(u32, 3), metadata.engine_index);
    // A second parser uses the same call-local region without allocation.
    _ = try readCounterArrayUsing(Fixture.read, std.testing.allocator, &fixture, &buffer, &status, &scratch);
    try std.testing.expectEqual(@as(usize, 2), fixture.supplies);
}

test "external collector rejects ordinary sample and never frees caller storage" {
    const config: Config = .{ .abi_version = abi_version, .struct_size = @sizeOf(Config), .baseline_reset_interval_ms = 0, .reserved = 0 };
    const collector = try Collector.create(&config);
    collector.external_scratch = true;
    var header: FrameHeader = undefined;
    try std.testing.expectEqual(ResultCode.invalid_argument, collector.sample(&header));
    try std.testing.expectEqual(ResultCode.invalid_argument, collector.sampleExternal(&header, .{ .context = null, .supply = null }));
    try std.testing.expectEqual(@as(usize, 0), collector.raw_engine_buffer.words.capacity);
    try std.testing.expectEqual(@as(usize, 0), collector.array_buffers.gpu_engine.words.capacity);
    try std.testing.expectEqual(@as(usize, 0), collector.array_buffers.gpu_memory.words.capacity);
    collector.destroy();
}

test "optional GPU engine window sidecar preserves legacy row ABI" {
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(GpuEngineWindow));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(GpuEngineRow));
}
test "counter UTC confirmation never invents a reversed collection interval" {
    const raw: FileTime = .{ .low = 150, .high = 0 };
    try std.testing.expectEqual(@as(?i64, 150), confirmRawUtc(raw, .{ .low = 100, .high = 0 }, .{ .low = 200, .high = 0 }));
    try std.testing.expectEqual(@as(?i64, null), confirmRawUtc(raw, .{ .low = 200, .high = 0 }, .{ .low = 100, .high = 0 }));
}
fn confirmRawUtc(raw: FileTime, begin: FileTime, end: FileTime) ?i64 {
    const first = fileTimeInteger(begin);
    const last = fileTimeInteger(end);
    if (first <= 0 or last < first) return null;
    const direct = fileTimeInteger(raw);
    if (direct >= first and direct <= last) return direct;
    var utc: FileTime = undefined;
    if (LocalFileTimeToFileTime(&raw, &utc) == 0) return null;
    const converted = fileTimeInteger(utc);
    return if (converted >= first and converted <= last) converted else null;
}
fn formattedValue(value: PdhFormattedCounterValue) ?f64 {
    if ((value.c_status != PDH_CSTATUS_VALID_DATA and value.c_status != PDH_CSTATUS_NEW_DATA) or
        !std.math.isFinite(value.double_value)) return null;
    return value.double_value;
}

fn parseGpuCounterMetadata(path: []const u16) ?GpuCounterMetadata {
    const process_id = parseDecimalAfter(path, "pid_") orelse return null;
    const luid_start = findAsciiInsensitive(path, "luid_0x") orelse return null;
    const high_start = luid_start + "luid_0x".len;
    const high = parseFixedHex(path, high_start, 8) orelse return null;
    const low_prefix_start = high_start + 8;
    if (!matchesAsciiInsensitive(path, low_prefix_start, "_0x")) return null;
    const low = parseFixedHex(path, low_prefix_start + 3, 8) orelse return null;

    return .{
        .adapter_luid = (@as(u64, high) << 32) | low,
        .process_id = process_id,
        .engine_class = classifyEngine(path),
        .physical_index = parseDecimalAfter(path, "phys_") orelse 0,
        .engine_index = parseDecimalAfter(path, "eng_") orelse std.math.maxInt(u32),
    };
}

fn classifyEngine(path: []const u16) EngineClass {
    const engine_start = findAsciiInsensitive(path, "engtype_") orelse return .other;
    const value_start = engine_start + "engtype_".len;
    var value_end = value_start;
    while (value_end < path.len and path[value_end] != '_' and path[value_end] != ')' and path[value_end] != '\\') : (value_end += 1) {}
    const value = path[value_start..value_end];

    if (containsAsciiInsensitive(value, "raytracing") or
        containsAsciiInsensitive(value, "ray tracing") or
        containsAsciiInsensitive(value, "rt")) return .ray_tracing;
    if (containsAsciiInsensitive(value, "compute") or containsAsciiInsensitive(value, "cuda")) return .compute;
    return .other;
}

fn parseDecimalAfter(path: []const u16, token: []const u8) ?u32 {
    const token_start = findAsciiInsensitive(path, token) orelse return null;
    var index = token_start + token.len;
    var value: u64 = 0;
    var digits: usize = 0;
    while (index < path.len and path[index] >= '0' and path[index] <= '9') : (index += 1) {
        value = value * 10 + path[index] - '0';
        if (value > std.math.maxInt(u32)) return null;
        digits += 1;
    }
    return if (digits > 0) @intCast(value) else null;
}

fn parseFixedHex(path: []const u16, start: usize, count: usize) ?u32 {
    if (start + count > path.len) return null;
    var value: u32 = 0;
    for (path[start .. start + count]) |character| {
        const digit: u32 = switch (character) {
            '0'...'9' => character - '0',
            'a'...'f' => character - 'a' + 10,
            'A'...'F' => character - 'A' + 10,
            else => return null,
        };
        value = (value << 4) | digit;
    }
    return value;
}

fn findAsciiInsensitive(path: []const u16, token: []const u8) ?usize {
    if (token.len == 0 or path.len < token.len) return null;
    var index: usize = 0;
    while (index + token.len <= path.len) : (index += 1) {
        if (matchesAsciiInsensitive(path, index, token)) return index;
    }
    return null;
}

fn containsAsciiInsensitive(path: []const u16, token: []const u8) bool {
    return findAsciiInsensitive(path, token) != null;
}

fn matchesAsciiInsensitive(path: []const u16, start: usize, token: []const u8) bool {
    if (start + token.len > path.len) return false;
    for (token, 0..) |expected, offset| {
        if (asciiLowerWide(path[start + offset]) != std.ascii.toLower(expected)) return false;
    }
    return true;
}

fn asciiLowerWide(character: u16) u16 {
    return if (character >= 'A' and character <= 'Z') character + ('a' - 'A') else character;
}

fn emptySystemIo() SystemIo {
    return .{
        .struct_size = @sizeOf(SystemIo),
        .valid_mask = 0,
        .disk_active_percent = 0,
        .disk_read_bytes_per_second = 0,
        .disk_write_bytes_per_second = 0,
        .disk_queue_length = 0,
        .network_receive_bytes_per_second = 0,
        .network_send_bytes_per_second = 0,
        .network_utilization_percent = 0,
        .network_bandwidth_bits_per_second = 0,
    };
}

fn emptyDiagnostics() Diagnostics {
    return .{
        .abi_version = abi_version,
        .struct_size = @sizeOf(Diagnostics),
        .topology_generation = 0,
        .counter_count = 0,
        .valid_counter_count = 0,
        .engine_row_count = 0,
        .memory_row_count = 0,
        .reserved = 0,
        .collect_duration_ns = 0,
        .read_duration_ns = 0,
        .frame_duration_ns = 0,
    };
}

// RuntimeRead: Sampled
fn performanceCounter() i64 {
    var value: i64 = 0;
    return if (QueryPerformanceCounter(&value) != 0) value else 0;
}

// RuntimeRead: Fixed
fn performanceNanoseconds(started: i64, finished: i64) u64 {
    if (started <= 0 or finished <= started) return 0;
    var frequency: i64 = 0;
    if (QueryPerformanceFrequency(&frequency) == 0 or frequency <= 0) return 0;
    const ticks: u128 = @intCast(finished - started);
    return @intCast((ticks * std.time.ns_per_s) / @as(u128, @intCast(frequency)));
}

fn sanitizeNonNegative(value: f64) f64 {
    return if (!std.math.isFinite(value) or value < 0) 0 else value;
}

fn sanitizePercent(value: f64) f64 {
    return std.math.clamp(sanitizeNonNegative(value), 0, 100);
}

fn diskActivityFromIdle(idle_percent: f64) f64 {
    return 100 - sanitizePercent(idle_percent);
}

test "disk activity uses idle time rather than concurrent request time" {
    try std.testing.expectEqual(@as(f64, 0), diskActivityFromIdle(100));
    try std.testing.expectEqual(@as(f64, 100), diskActivityFromIdle(0));
    try std.testing.expectEqual(@as(f64, 27.5), diskActivityFromIdle(72.5));
    try std.testing.expectEqual(@as(f64, 0), diskActivityFromIdle(100.01));
}

test "GPU counter metadata is projected to numeric ABI fields" {
    const path = std.unicode.utf8ToUtf16LeStringLiteral(
        "pid_4242_luid_0x00000001_0xabcdef12_phys_0_eng_1_engtype_Compute",
    );
    const metadata = parseGpuCounterMetadata(path).?;

    try std.testing.expectEqual(@as(u64, 0x0000_0001_abcd_ef12), metadata.adapter_luid);
    try std.testing.expectEqual(@as(u32, 4242), metadata.process_id);
    try std.testing.expectEqual(EngineClass.compute, metadata.engine_class);
    try std.testing.expectEqual(@as(u32, 0), metadata.physical_index);
    try std.testing.expectEqual(@as(u32, 1), metadata.engine_index);
}

test "GPU ray tracing engine classification is case insensitive" {
    const path = std.unicode.utf8ToUtf16LeStringLiteral(
        "pid_7_luid_0x00000000_0x00000002_engtype_RayTracing",
    );
    try std.testing.expectEqual(EngineClass.ray_tracing, parseGpuCounterMetadata(path).?.engine_class);
}

test "invalid GPU counter path is rejected" {
    const path = std.unicode.utf8ToUtf16LeStringLiteral("no_numeric_identity");
    try std.testing.expect(parseGpuCounterMetadata(path) == null);
}

test "query owns six disk and GPU counters with separate physical network sampling" {
    var counters: CounterSet = .{};
    try std.testing.expectEqual(@as(u32, 0), counters.count());
    counters.disk_idle = @ptrFromInt(1);
    counters.gpu_engine = @ptrFromInt(2);
    try std.testing.expectEqual(@as(u32, 2), counters.count());
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(PdhFormattedCounterValueItem));
}


