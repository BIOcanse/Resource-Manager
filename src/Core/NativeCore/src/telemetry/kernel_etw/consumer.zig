const std = @import("std");
pub const decoder = @import("decoder.zig");

// Windows SDK evntrace.h / evntcons.h. Explicit layouts avoid bringing an
// alternate C runtime or a C# event callback into this native owner.
pub const EventDescriptor = extern struct { id: u16, version: u8, channel: u8, level: u8, opcode: u8, task: u16, keyword: u64 };
pub const EventHeader = extern struct {
    size: u16,
    header_type: u16,
    flags: u16,
    property: u16,
    thread_id: u32,
    process_id: u32,
    timestamp: i64,
    provider: decoder.Guid,
    descriptor: EventDescriptor,
    processor_time: u64,
    activity: decoder.Guid,
};
pub const EventRecord = extern struct {
    header: EventHeader,
    // ProcessorIndex if EVENT_HEADER_FLAG_PROCESSOR_INDEX; otherwise the low
    // byte is ProcessorNumber and the high byte is Alignment.
    processor_index: u16,
    logger_id: u16,
    extended_count: u16,
    payload_length: u16,
    extended_data: ?*anyopaque,
    payload: ?*anyopaque,
    context: ?*anyopaque,
};
const LegacyHeader = extern struct {
    size: u16,
    field_flags: u16,
    version: u32,
    thread_id: u32,
    process_id: u32,
    timestamp: i64,
    provider: decoder.Guid,
    processor_time: u64,
};
const LegacyEvent = extern struct {
    header: LegacyHeader,
    instance_id: u32,
    parent_instance_id: u32,
    parent_guid: decoder.Guid,
    payload: ?*anyopaque,
    payload_length: u32,
    buffer_context: u32,
};
const TimeZone = extern struct { bias: i32, standard_name: [32]u16, standard_date: [8]u16, standard_bias: i32, daylight_name: [32]u16, daylight_date: [8]u16, daylight_bias: i32 };
pub const LogHeader = extern struct {
    buffer_size: u32,
    version: u32,
    provider_version: u32,
    processor_count: u32,
    end_time: i64,
    timer_resolution: u32,
    maximum_file_size: u32,
    logfile_mode: u32,
    buffers_written: u32,
    start_buffers: u32,
    pointer_size: u32,
    events_lost: u32,
    cpu_speed: u32,
    logger_name: ?[*:0]u16,
    logfile_name: ?[*:0]u16,
    timezone: TimeZone,
    boot_time: i64,
    performance_frequency: i64,
    start_time: i64,
    clock_type: u32,
    buffers_lost: u32,
};
pub const LogFile = extern struct {
    logfile_name: ?[*:0]u16,
    logger_name: ?[*:0]u16,
    current_time: i64,
    buffers_read: u32,
    process_mode: u32,
    current_event: LegacyEvent,
    header: LogHeader,
    buffer_callback: ?*const fn (*LogFile) callconv(.winapi) u32,
    buffer_size: u32,
    filled: u32,
    events_lost: u32,
    event_callback: ?*const fn (*EventRecord) callconv(.winapi) void,
    is_kernel_trace: u32,
    context: ?*anyopaque,
};
extern "advapi32" fn OpenTraceW(logfile: *LogFile) callconv(.winapi) u64;
extern "advapi32" fn ProcessTrace(handles: [*]const u64, count: u32, start: ?*const anyopaque, end: ?*const anyopaque) callconv(.winapi) u32;
extern "advapi32" fn CloseTrace(handle: u64) callconv(.winapi) u32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
extern "kernel32" fn QueryPerformanceCounter(value: *i64) callconv(.winapi) i32;
const FileTime = extern struct { low: u32, high: u32 };
extern "kernel32" fn GetSystemTimePreciseAsFileTime(value: *FileTime) callconv(.winapi) void;

/// UTC is sampled between these QPC readings. Midpoint mapping has sampling
/// uncertainty of at most half the bracket, plus the Windows UTC accuracy.
/// Raw event QPC is unchanged; this is not an exact wall-clock correlation.
pub const ClockSample = struct {
    qpc_before: i64,
    qpc_after: i64,
    filetime_100ns: i64,

    // RuntimeRead: Sampled
    pub fn read() error{ClockSamplingFailed}!ClockSample {
        var before: i64 = 0;
        var after: i64 = 0;
        var utc: FileTime = undefined;
        if (QueryPerformanceCounter(&before) == 0) return error.ClockSamplingFailed;
        GetSystemTimePreciseAsFileTime(&utc);
        if (QueryPerformanceCounter(&after) == 0 or before < 0 or after < before) return error.ClockSamplingFailed;
        const time = @as(u64, utc.high) << 32 | utc.low;
        return .{ .qpc_before = before, .qpc_after = after, .filetime_100ns = std.math.cast(i64, time) orelse return error.ClockSamplingFailed };
    }
    pub fn midpoint(self: ClockSample) i64 {
        return self.qpc_before + @divTrunc(self.qpc_after - self.qpc_before, 2);
    }
};

pub const invalid_handle: u64 = @as(u64, std.math.maxInt(usize));
pub const Metadata = struct {
    qpc_frequency: i64 = 0,
    start_filetime_100ns: i64 = 0,
    end_filetime_100ns: i64 = 0,
    boot_filetime_100ns: i64 = 0,
    // Set only by the EventTrace/Header record, paired with start_filetime.
    // The first ordinary record is never treated as the session start.
    header_qpc: ?i64 = null,
    // Real-time OpenTrace need not deliver an EventTrace/Header. Sample UTC at
    // open explicitly; leave the original trace start/end metadata unchanged.
    realtime_clock: ?ClockSample = null,
    clock_type: u32 = 0,
    pointer_size: u8 = 0,
    processor_count: u32 = 0,
    os_buffer_size: u32 = 0,
    logfile_mode: u32 = 0,

    pub fn qpcToFiletime(self: Metadata, qpc: i64) error{ NoClockAnchor, InvalidClock, Overflow }!i64 {
        if (self.clock_type != 1 or self.qpc_frequency <= 0) return error.InvalidClock;
        const anchor = if (self.realtime_clock) |sample| sample.midpoint() else self.header_qpc orelse return error.NoClockAnchor;
        const filetime = if (self.realtime_clock) |sample| sample.filetime_100ns else self.start_filetime_100ns;
        const delta: i128 = @as(i128, qpc) - anchor;
        const value = @as(i128, filetime) + @divTrunc(delta * 10_000_000, self.qpc_frequency);
        return std.math.cast(i64, value) orelse error.Overflow;
    }
};
pub const LossSource = enum { logfile_header, buffer_callback };
pub const Loss = struct { source: LossSource, events_lost: u32, buffers_lost: u32 };
pub const Rejected = struct { raw: decoder.RawEvent, reason: decoder.DecodeError };
pub const InvalidRecord = struct { original_header_size: u16, flags: u16, payload_length: u16, missing_payload: bool };
pub const Notice = union(enum) { event: decoder.Event, rejected: Rejected, loss: Loss, invalid_record: InvalidRecord };
/// Pure Zig function pointer; the sink is never marshalled to a managed delegate.
/// Return false to request cancellation at the next buffer boundary.
pub const Sink = struct {
    context: ?*anyopaque,
    on_notice: *const fn (?*anyopaque, Notice) bool,
    // Optional native routing of disk/file/network and other shared-session
    // records. A null callback does not claim those owners were migrated.
    on_unhandled: ?*const fn (?*anyopaque, decoder.RawEvent) bool = null,
};
pub const Counters = struct { records: u64 = 0, decoded: u64 = 0, rejected: u64 = 0, unhandled: u64 = 0, lost_markers: u64 = 0, events_lost: u32 = 0, buffers_lost: u32 = 0 };
pub const ConsumerError = error{ AlreadyOpen, NotOpen, Processing, OpenFailed, UnsupportedClock, InvalidTraceMetadata, ClockSamplingFailed, CleanupFailed };

/// Place in stable owner memory before open and retain through process return.
/// open/process are serialized by the caller. requestStop is concurrent-safe.
/// close may run concurrently with real-time processing; an ETL must first be
/// cancelled with requestStop and joined before close (Windows API contract).
/// Metadata and counters are read after process returns, or on its sink thread.
pub const Consumer = struct {
    logfile: LogFile = std.mem.zeroes(LogFile),
    metadata: Metadata = .{},
    counters: Counters = .{},
    sink: Sink,
    last_windows_error: u32 = 0,
    handle: std.atomic.Value(u64) = .init(invalid_handle),
    processing: std.atomic.Value(bool) = .init(false),
    stop_requested: std.atomic.Value(bool) = .init(false),
    realtime: bool = false,

    pub fn init(sink: Sink) Consumer {
        return .{ .sink = sink };
    }

    pub fn openRealtime(self: *Consumer, session_name: [:0]const u16) ConsumerError!void {
        return self.open(session_name, true);
    }
    pub fn openEtl(self: *Consumer, filename: [:0]const u16) ConsumerError!void {
        return self.open(filename, false);
    }
    // RuntimeRead: Fixed
    fn open(self: *Consumer, name: [:0]const u16, realtime: bool) ConsumerError!void {
        if (self.processing.load(.acquire)) return error.Processing;
        if (self.handle.load(.acquire) != invalid_handle) return error.AlreadyOpen;
        self.logfile = std.mem.zeroes(LogFile);
        self.metadata = .{};
        self.counters = .{};
        self.last_windows_error = 0;
        self.realtime = realtime;
        self.stop_requested.store(false, .release);
        // OpenTrace accepts mutable strings but does not modify them. They must
        // remain valid until close; callers own their capped storage.
        if (realtime) self.logfile.logger_name = @constCast(name.ptr) else self.logfile.logfile_name = @constCast(name.ptr);
        self.logfile.process_mode = 0x1000_1000 | (if (realtime) @as(u32, 0x100) else 0);
        self.logfile.buffer_callback = bufferCallback;
        self.logfile.event_callback = eventCallback;
        self.logfile.context = self;
        const handle = OpenTraceW(&self.logfile);
        if (handle == invalid_handle) {
            self.last_windows_error = GetLastError();
            return error.OpenFailed;
        }
        // From this point the owner has a real OS resource, including when
        // metadata is unsupported. A failed cleanup must remain retryable.
        self.handle.store(handle, .release);
        const h = self.logfile.header;
        if (h.clock_type != 1 or h.performance_frequency <= 0) {
            return self.rejectOpen(error.UnsupportedClock);
        }
        if ((h.pointer_size != 4 and h.pointer_size != 8) or h.processor_count == 0) {
            return self.rejectOpen(error.InvalidTraceMetadata);
        }
        var metadata: Metadata = .{
            .qpc_frequency = h.performance_frequency,
            .start_filetime_100ns = h.start_time,
            .end_filetime_100ns = h.end_time,
            .boot_filetime_100ns = h.boot_time,
            .clock_type = h.clock_type,
            .pointer_size = @intCast(h.pointer_size),
            .processor_count = h.processor_count,
            .os_buffer_size = h.buffer_size,
            .logfile_mode = h.logfile_mode,
        };
        if (realtime) metadata.realtime_clock = ClockSample.read() catch return self.rejectOpen(error.ClockSamplingFailed);
        self.metadata = metadata;
    }
    fn rejectOpen(self: *Consumer, reason: ConsumerError) ConsumerError!void {
        const status = self.close() catch return error.CleanupFailed;
        self.last_windows_error = status;
        if (status != 0 and status != 6 and status != 7007) return error.CleanupFailed;
        return reason;
    }
    /// Run on the owner's dedicated registered native consumer thread. Returns
    /// the original Windows status (0 success, 1223 buffer cancellation, etc.).
    // RuntimeRead: Sampled
    pub fn process(self: *Consumer) ConsumerError!u32 {
        if (self.processing.cmpxchgStrong(false, true, .acq_rel, .acquire) != null) return error.Processing;
        defer self.processing.store(false, .release);
        const handle = self.handle.load(.acquire);
        if (handle == invalid_handle) return error.NotOpen;
        if (self.metadata.clock_type != 1 or self.metadata.qpc_frequency <= 0) return error.UnsupportedClock;
        self.observeLoss(.logfile_header, self.logfile.header.events_lost, self.logfile.header.buffers_lost);
        const result = ProcessTrace(@ptrCast(&handle), 1, null, null);
        self.last_windows_error = result;
        return result;
    }
    pub fn requestStop(self: *Consumer) void {
        self.stop_requested.store(true, .release);
    }

    pub fn close(self: *Consumer) ConsumerError!u32 {
        if (!self.realtime and self.processing.load(.acquire)) return error.Processing;
        const handle = self.handle.swap(invalid_handle, .acq_rel);
        if (handle == invalid_handle) return 0;
        const result = CloseTrace(handle);
        // ERROR_CTX_CLOSE_PENDING is success; ProcessTrace still needs joining.
        // Preserve the handle after other failures so an explicit retry can close it.
        if (result != 0 and result != 6 and result != 7007) self.handle.store(handle, .release);
        return result;
    }
    fn notify(self: *Consumer, notice: Notice) void {
        if (!self.sink.on_notice(self.sink.context, notice)) self.requestStop();
    }
    fn observeLoss(self: *Consumer, source: LossSource, events: u32, buffers: u32) void {
        // These are cumulative snapshots of the same session, not additive
        // event counts. LostEvent marker counts are reported separately.
        if (events > self.counters.events_lost or buffers > self.counters.buffers_lost) {
            self.counters.events_lost = @max(events, self.counters.events_lost);
            self.counters.buffers_lost = @max(buffers, self.counters.buffers_lost);
            self.notify(.{ .loss = .{ .source = source, .events_lost = self.counters.events_lost, .buffers_lost = self.counters.buffers_lost } });
        }
    }
    fn bufferCallback(logfile: *LogFile) callconv(.winapi) u32 {
        const self: *Consumer = @ptrCast(@alignCast(logfile.context orelse return 0));
        self.observeLoss(.buffer_callback, @max(logfile.events_lost, logfile.header.events_lost), logfile.header.buffers_lost);
        return if (self.stop_requested.load(.acquire)) 0 else 1;
    }
    fn eventCallback(record: *EventRecord) callconv(.winapi) void {
        const self: *Consumer = @ptrCast(@alignCast(record.context orelse return));
        self.acceptRecord(record);
    }
    // Public to exercise the real native callback conversion with synthetic SDK
    // records. This does not allocate or retain an ETW payload.
    pub fn acceptRecord(self: *Consumer, record: *const EventRecord) void {
        self.counters.records +|= 1;
        const flags = record.header.flags;
        // Size describes the original ETW record, which may use a compact
        // kernel header. ProcessTrace has already supplied a full EVENT_RECORD;
        // comparing the original size with sizeof(EVENT_HEADER) rejects valid
        // compact CSwitch/interrupt records. Payload bounds use UserDataLength.
        if ((record.payload_length > 0 and record.payload == null) or (flags & 0x60) == 0x60) {
            self.counters.rejected +|= 1;
            self.notify(.{ .invalid_record = .{ .original_header_size = record.header.size, .flags = flags, .payload_length = record.payload_length, .missing_payload = record.payload == null } });
            return;
        }
        const payload: []const u8 = if (record.payload) |data| @as([*]const u8, @ptrCast(data))[0..record.payload_length] else &.{};
        const raw: decoder.RawEvent = .{
            .provider = record.header.provider,
            .opcode = record.header.descriptor.opcode,
            .version = record.header.descriptor.version,
            .pointer_size = if (flags & 0x20 != 0) 4 else if (flags & 0x40 != 0) 8 else self.metadata.pointer_size,
            .qpc = record.header.timestamp,
            .processor = if (flags & 0x200 != 0) record.processor_index else record.processor_index & 0xff,
            .header_process_id = record.header.process_id,
            .header_thread_id = record.header.thread_id,
            .payload = payload,
        };
        // EventTrace/Header has its own GUID and may contain cross-architecture
        // pointer fields. OpenTrace has already normalized LogfileHeader for us.
        const header_guid: decoder.Guid = .{ .data1 = 0x68fdd900, .data2 = 0x4a3e, .data3 = 0x11d1, .data4 = .{ 0x84, 0xf4, 0, 0, 0xf8, 4, 0x64, 0xe3 } };
        if (raw.provider.eql(header_guid) and raw.opcode == 0 and raw.qpc >= 0 and self.metadata.header_qpc == null) self.metadata.header_qpc = raw.qpc;
        const event = decoder.decode(raw) catch |reason| {
            self.counters.rejected +|= 1;
            self.notify(.{ .rejected = .{ .raw = raw, .reason = reason } });
            return;
        };
        if (event) |value| {
            self.counters.decoded +|= 1;
            if (value.data == .lost) self.counters.lost_markers +|= 1;
            self.notify(.{ .event = value });
        } else {
            self.counters.unhandled +|= 1;
            if (self.sink.on_unhandled) |handler| if (!handler(self.sink.context, raw)) {
                self.requestStop();
            };
        }
    }
};

comptime {
    if (@sizeOf(usize) == 8) {
        std.debug.assert(@sizeOf(EventHeader) == 80);
        std.debug.assert(@sizeOf(EventRecord) == 112);
        std.debug.assert(@offsetOf(EventRecord, "payload") == 96);
        std.debug.assert(@sizeOf(LogHeader) == 280);
        std.debug.assert(@offsetOf(LogHeader, "performance_frequency") == 256);
        std.debug.assert(@sizeOf(LogFile) == 448);
        std.debug.assert(@offsetOf(LogFile, "event_callback") == 424);
    }
}
