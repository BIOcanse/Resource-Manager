const std = @import("std");
const ResultCode = @import("../common/result_codes.zig").ResultCode;

pub const abi_version: u32 = 0x0001_0000;

const PDH_MORE_DATA: i32 = @bitCast(@as(u32, 0x8000_07d2));
const PDH_FMT_DOUBLE: u32 = 0x0000_0200;
const PDH_CSTATUS_VALID_DATA: u32 = 0;
const PDH_CSTATUS_NEW_DATA: u32 = 1;
const MAX_ARRAY_BUFFER_BYTES: u32 = 64 * 1024 * 1024;
const DEFAULT_BASELINE_RESET_INTERVAL_MS: u32 = 5_000;
const MIN_BASELINE_RESET_INTERVAL_MS: u32 = 1_000;

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
};

const CounterSet = struct {
    disk_active: PdhHandle = null,
    disk_read: PdhHandle = null,
    disk_write: PdhHandle = null,
    disk_queue: PdhHandle = null,
    gpu_engine: PdhHandle = null,
    gpu_memory: PdhHandle = null,
    network_receive: PdhHandle = null,
    network_send: PdhHandle = null,
    network_bandwidth: PdhHandle = null,

    fn count(self: *const CounterSet) u32 {
        var result: u32 = 0;
        inline for (std.meta.fields(CounterSet)) |field| {
            if (@field(self, field.name) != null) result += 1;
        }
        return result;
    }
};

const ArrayBuffer = struct {
    words: std.ArrayList(u64) = .empty,

    fn deinit(self: *ArrayBuffer, allocator: std.mem.Allocator) void {
        self.words.deinit(allocator);
    }

    fn byteCapacity(self: *const ArrayBuffer) u32 {
        return @intCast(self.words.items.len * @sizeOf(u64));
    }

    fn itemPointer(self: *ArrayBuffer) ?[*]PdhFormattedCounterValueItem {
        return if (self.words.items.len == 0) null else @ptrCast(self.words.items.ptr);
    }

    fn ensureByteCapacity(self: *ArrayBuffer, allocator: std.mem.Allocator, required_bytes: u32) !void {
        if (required_bytes > MAX_ARRAY_BUFFER_BYTES) return error.ArrayBufferTooLarge;
        const required_words = (@as(usize, required_bytes) + @sizeOf(u64) - 1) / @sizeOf(u64);
        if (required_words > self.words.items.len) try self.words.resize(allocator, required_words);
    }
};

const ArrayBuffers = struct {
    gpu_engine: ArrayBuffer = .{},
    gpu_memory: ArrayBuffer = .{},
    network_receive: ArrayBuffer = .{},
    network_send: ArrayBuffer = .{},
    network_bandwidth: ArrayBuffer = .{},

    fn deinit(self: *ArrayBuffers, allocator: std.mem.Allocator) void {
        self.gpu_engine.deinit(allocator);
        self.gpu_memory.deinit(allocator);
        self.network_receive.deinit(allocator);
        self.network_send.deinit(allocator);
        self.network_bandwidth.deinit(allocator);
    }
};

const gpu_engine_path = std.unicode.utf8ToUtf16LeStringLiteral("\\GPU Engine(*)\\Utilization Percentage");
const gpu_memory_path = std.unicode.utf8ToUtf16LeStringLiteral("\\GPU Process Memory(*)\\Dedicated Usage");
const disk_active_path = std.unicode.utf8ToUtf16LeStringLiteral("\\PhysicalDisk(_Total)\\% Disk Time");
const disk_read_path = std.unicode.utf8ToUtf16LeStringLiteral("\\PhysicalDisk(_Total)\\Disk Read Bytes/sec");
const disk_write_path = std.unicode.utf8ToUtf16LeStringLiteral("\\PhysicalDisk(_Total)\\Disk Write Bytes/sec");
const disk_queue_path = std.unicode.utf8ToUtf16LeStringLiteral("\\PhysicalDisk(_Total)\\Current Disk Queue Length");
const network_receive_path = std.unicode.utf8ToUtf16LeStringLiteral("\\Network Interface(*)\\Bytes Received/sec");
const network_send_path = std.unicode.utf8ToUtf16LeStringLiteral("\\Network Interface(*)\\Bytes Sent/sec");
const network_bandwidth_path = std.unicode.utf8ToUtf16LeStringLiteral("\\Network Interface(*)\\Current Bandwidth");

pub const Collector = struct {
    allocator: std.mem.Allocator,
    baseline_reset_interval_ms: u32,
    query: PdhHandle = null,
    counters: CounterSet = .{},
    array_buffers: ArrayBuffers = .{},
    engine_rows: std.ArrayList(GpuEngineRow) = .empty,
    memory_rows: std.ArrayList(GpuMemoryRow) = .empty,
    system_io: SystemIo = emptySystemIo(),
    diagnostics: Diagnostics = emptyDiagnostics(),
    topology_generation: u32 = 0,
    sequence: u64 = 0,
    captured_tick_ms: u64 = 0,
    last_collect_tick_ms: u64 = 0,
    pending_topology_refresh: bool = true,
    last_native_status: i32 = 0,

    pub fn create(config: *const Config) !*Collector {
        const allocator = std.heap.page_allocator;
        const collector = try allocator.create(Collector);
        collector.* = .{
            .allocator = allocator,
            .baseline_reset_interval_ms = normalizeBaselineResetInterval(config.baseline_reset_interval_ms),
        };
        return collector;
    }

    pub fn destroy(self: *Collector) void {
        const allocator = self.allocator;
        self.closeQuery();
        self.array_buffers.deinit(allocator);
        self.engine_rows.deinit(allocator);
        self.memory_rows.deinit(allocator);
        allocator.destroy(self);
    }

    pub fn requestTopologyRefresh(self: *Collector) void {
        self.pending_topology_refresh = true;
    }

    pub fn sample(self: *Collector, header: *FrameHeader) ResultCode {
        if (self.pending_topology_refresh or self.query == null) {
            const rebuilt = self.rebuildQuery() catch {
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
        const requires_new_baseline = self.last_collect_tick_ms != 0 and now_tick_ms -| self.last_collect_tick_ms > self.baseline_reset_interval_ms;
        const frame_started = performanceCounter();
        const collect_started = performanceCounter();
        const collect_status = PdhCollectQueryData(self.query);
        const collect_finished = performanceCounter();
        self.last_native_status = collect_status;
        if (collect_status != 0) {
            self.writeDiagnostics(frame_started, collect_started, collect_finished, collect_finished, collect_finished, 0);
            self.writeHeader(header, 0);
            return .pdh_error;
        }

        self.last_collect_tick_ms = now_tick_ms;
        if (requires_new_baseline) {
            self.resetFrameData();
            self.writeDiagnostics(frame_started, collect_started, collect_finished, collect_finished, collect_finished, 0);
            self.writeHeader(header, FrameFlags.baseline_collected);
            return .no_data;
        }

        self.resetFrameData();

        const read_started = performanceCounter();
        var valid_value_count: usize = 0;
        var partial = false;

        self.readFixedCounters(&valid_value_count, &partial);
        const gpu_engine_complete = self.readGpuEngineRows(&valid_value_count, &partial) catch {
            const read_finished = performanceCounter();
            self.writeDiagnostics(frame_started, collect_started, collect_finished, read_started, read_finished, valid_value_count);
            self.writeHeader(header, FrameFlags.partial);
            return .out_of_memory;
        };
        const gpu_memory_complete = self.readGpuMemoryRows(&valid_value_count, &partial) catch {
            const read_finished = performanceCounter();
            self.writeDiagnostics(frame_started, collect_started, collect_finished, read_started, read_finished, valid_value_count);
            self.writeHeader(header, FrameFlags.partial);
            return .out_of_memory;
        };
        const network_complete = self.readNetworkCounters(&valid_value_count, &partial) catch {
            const read_finished = performanceCounter();
            self.writeDiagnostics(frame_started, collect_started, collect_finished, read_started, read_finished, valid_value_count);
            self.writeHeader(header, FrameFlags.partial);
            return .out_of_memory;
        };
        const disk_mask = IoValidMask.disk_active |
            IoValidMask.disk_read |
            IoValidMask.disk_write |
            IoValidMask.disk_queue;
        const disk_complete = (self.system_io.valid_mask & disk_mask) == disk_mask;
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

    fn rebuildQuery(self: *Collector) !bool {
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
        addCounter(new_query, disk_active_path, &new_counters.disk_active);
        addCounter(new_query, disk_read_path, &new_counters.disk_read);
        addCounter(new_query, disk_write_path, &new_counters.disk_write);
        addCounter(new_query, disk_queue_path, &new_counters.disk_queue);
        addCounter(new_query, gpu_engine_path, &new_counters.gpu_engine);
        addCounter(new_query, gpu_memory_path, &new_counters.gpu_memory);
        addCounter(new_query, network_receive_path, &new_counters.network_receive);
        addCounter(new_query, network_send_path, &new_counters.network_send);
        addCounter(new_query, network_bandwidth_path, &new_counters.network_bandwidth);
        if (new_counters.count() == 0) return false;

        const baseline_status = PdhCollectQueryData(new_query);
        self.last_native_status = baseline_status;
        if (baseline_status != 0) return false;

        try prepareArrayBuffer(self.allocator, new_counters.gpu_engine, &self.array_buffers.gpu_engine);
        try prepareArrayBuffer(self.allocator, new_counters.gpu_memory, &self.array_buffers.gpu_memory);
        try prepareArrayBuffer(self.allocator, new_counters.network_receive, &self.array_buffers.network_receive);
        try prepareArrayBuffer(self.allocator, new_counters.network_send, &self.array_buffers.network_send);
        try prepareArrayBuffer(self.allocator, new_counters.network_bandwidth, &self.array_buffers.network_bandwidth);

        if (self.query != null) _ = PdhCloseQuery(self.query);
        self.query = new_query;
        self.counters = new_counters;
        self.topology_generation +%= 1;
        self.last_collect_tick_ms = GetTickCount64();
        committed = true;
        return true;
    }

    fn closeQuery(self: *Collector) void {
        if (self.query != null) _ = PdhCloseQuery(self.query);
        self.query = null;
        self.counters = .{};
        self.last_collect_tick_ms = 0;
    }

    fn resetFrameData(self: *Collector) void {
        self.engine_rows.clearRetainingCapacity();
        self.memory_rows.clearRetainingCapacity();
        self.system_io = emptySystemIo();
    }

    fn readFixedCounters(self: *Collector, valid_value_count: *usize, partial: *bool) void {
        self.readFixedCounter(
            self.counters.disk_active,
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
        clamp_percent: bool,
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
        destination.* = if (clamp_percent) sanitizePercent(value) else sanitizeNonNegative(value);
        self.system_io.valid_mask |= mask;
        valid_value_count.* += 1;
    }

    fn readGpuEngineRows(self: *Collector, valid_value_count: *usize, partial: *bool) !bool {
        const items = try readCounterArray(
            self.allocator,
            self.counters.gpu_engine,
            &self.array_buffers.gpu_engine,
            &self.last_native_status,
        ) orelse {
            partial.* = true;
            return false;
        };

        for (items) |item| {
            const value = formattedValue(item.value) orelse continue;
            const name = item.name orelse continue;
            const metadata = parseGpuCounterMetadata(std.mem.span(name)) orelse continue;
            valid_value_count.* += 1;
            const usage = sanitizeNonNegative(value);
            try self.engine_rows.append(self.allocator, .{
                .adapter_luid = metadata.adapter_luid,
                .process_id = metadata.process_id,
                .engine_class = @intFromEnum(metadata.engine_class),
                .usage_percent = usage,
            });
        }
        return true;
    }

    fn readGpuMemoryRows(self: *Collector, valid_value_count: *usize, partial: *bool) !bool {
        const items = try readCounterArray(
            self.allocator,
            self.counters.gpu_memory,
            &self.array_buffers.gpu_memory,
            &self.last_native_status,
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

    fn readNetworkCounters(self: *Collector, valid_value_count: *usize, partial: *bool) !bool {
        const receive_complete = try self.sumNetworkCounter(
            self.counters.network_receive,
            &self.array_buffers.network_receive,
            IoValidMask.network_receive,
            &self.system_io.network_receive_bytes_per_second,
            valid_value_count,
            partial,
        );
        const send_complete = try self.sumNetworkCounter(
            self.counters.network_send,
            &self.array_buffers.network_send,
            IoValidMask.network_send,
            &self.system_io.network_send_bytes_per_second,
            valid_value_count,
            partial,
        );
        const bandwidth_complete = try self.sumNetworkCounter(
            self.counters.network_bandwidth,
            &self.array_buffers.network_bandwidth,
            IoValidMask.network_bandwidth,
            &self.system_io.network_bandwidth_bits_per_second,
            valid_value_count,
            partial,
        );

        const complete = receive_complete and send_complete and bandwidth_complete;
        if (complete) {
            const bandwidth = self.system_io.network_bandwidth_bits_per_second;
            const bytes_per_second = self.system_io.network_receive_bytes_per_second +
                self.system_io.network_send_bytes_per_second;
            self.system_io.network_utilization_percent = if (bandwidth > 0)
                sanitizePercent(bytes_per_second * 8.0 * 100.0 / bandwidth)
            else
                0;
            self.system_io.valid_mask |= IoValidMask.network_utilization;
        }
        return complete;
    }

    fn sumNetworkCounter(
        self: *Collector,
        counter: PdhHandle,
        buffer: *ArrayBuffer,
        mask: u32,
        destination: *f64,
        valid_value_count: *usize,
        partial: *bool,
    ) !bool {
        const items = try readCounterArray(
            self.allocator,
            counter,
            buffer,
            &self.last_native_status,
        ) orelse {
            partial.* = true;
            return false;
        };

        var valid_items: usize = 0;
        var complete = true;
        for (items) |item| {
            const value = formattedValue(item.value) orelse {
                complete = false;
                continue;
            };
            destination.* += sanitizeNonNegative(value);
            valid_items += 1;
        }
        if (complete) {
            self.system_io.valid_mask |= mask;
        } else {
            partial.* = true;
        }
        valid_value_count.* += valid_items;
        return complete;
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

fn prepareArrayBuffer(allocator: std.mem.Allocator, counter: PdhHandle, buffer: *ArrayBuffer) !void {
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
        try buffer.ensureByteCapacity(allocator, required_bytes);
    }
}

fn readCounterArray(
    allocator: std.mem.Allocator,
    counter: PdhHandle,
    buffer: *ArrayBuffer,
    last_native_status: *i32,
) !?[]const PdhFormattedCounterValueItem {
    if (counter == null) return null;

    var attempt: u8 = 0;
    while (attempt < 3) : (attempt += 1) {
        var buffer_size = buffer.byteCapacity();
        var item_count: u32 = 0;
        const status = PdhGetFormattedCounterArrayW(
            counter,
            PDH_FMT_DOUBLE,
            &buffer_size,
            &item_count,
            buffer.itemPointer(),
        );
        last_native_status.* = status;
        if (status == PDH_MORE_DATA) {
            if (buffer_size == 0) return null;
            try buffer.ensureByteCapacity(allocator, buffer_size);
            continue;
        }
        if (status != 0) return null;
        if (item_count == 0) return &.{};

        const required_item_bytes = @as(u64, item_count) * @sizeOf(PdhFormattedCounterValueItem);
        if (required_item_bytes > buffer.byteCapacity()) return null;
        const items: [*]const PdhFormattedCounterValueItem = @ptrCast(buffer.words.items.ptr);
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

fn performanceCounter() i64 {
    var value: i64 = 0;
    return if (QueryPerformanceCounter(&value) != 0) value else 0;
}

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

fn normalizeBaselineResetInterval(value: u32) u32 {
    if (value == 0) return DEFAULT_BASELINE_RESET_INTERVAL_MS;
    return @max(value, MIN_BASELINE_RESET_INTERVAL_MS);
}

test "GPU counter metadata is projected to numeric ABI fields" {
    const path = std.unicode.utf8ToUtf16LeStringLiteral(
        "pid_4242_luid_0x00000001_0xabcdef12_phys_0_eng_1_engtype_Compute",
    );
    const metadata = parseGpuCounterMetadata(path).?;

    try std.testing.expectEqual(@as(u64, 0x0000_0001_abcd_ef12), metadata.adapter_luid);
    try std.testing.expectEqual(@as(u32, 4242), metadata.process_id);
    try std.testing.expectEqual(EngineClass.compute, metadata.engine_class);
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

test "wildcard query owns at most nine persistent counters" {
    var counters: CounterSet = .{};
    try std.testing.expectEqual(@as(u32, 0), counters.count());
    counters.disk_active = @ptrFromInt(1);
    counters.gpu_engine = @ptrFromInt(2);
    try std.testing.expectEqual(@as(u32, 2), counters.count());
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(PdhFormattedCounterValueItem));
}

test "baseline reset interval has a bounded default and minimum" {
    try std.testing.expectEqual(@as(u32, 5_000), normalizeBaselineResetInterval(0));
    try std.testing.expectEqual(@as(u32, 1_000), normalizeBaselineResetInterval(250));
    try std.testing.expectEqual(@as(u32, 7_500), normalizeBaselineResetInterval(7_500));
}
