//! Bounded MCP stdio transport. The run owner must keep Writer, its allocator,
//! Sink context and queued Delivery objects alive until finish succeeds. A
//! shutdown timeout leaves a live writer owned by that run until process exit.
const std = @import("std");
const windows = std.os.windows;

extern "kernel32" fn AcquireSRWLockExclusive(lock: *windows.SRWLOCK) callconv(.winapi) void;
extern "kernel32" fn ReleaseSRWLockExclusive(lock: *windows.SRWLOCK) callconv(.winapi) void;
extern "kernel32" fn CreateEventW(attributes: ?*anyopaque, manual_reset: i32, initial_state: i32, name: ?[*:0]const u16) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn SetEvent(event: *anyopaque) callconv(.winapi) i32;
extern "kernel32" fn WaitForSingleObject(handle: *anyopaque, milliseconds: u32) callconv(.winapi) u32;
extern "kernel32" fn CloseHandle(handle: *anyopaque) callconv(.winapi) i32;
extern "kernel32" fn CreateThread(attributes: ?*anyopaque, stack_size: usize, entry: *const fn (?*anyopaque) callconv(.winapi) u32, argument: ?*anyopaque, flags: u32, id: ?*u32) callconv(.winapi) ?*anyopaque;

const wait_object_0: u32 = 0;
const wait_timeout: u32 = 258;
const infinite: u32 = 0xffffffff;
const stack_size_param_is_a_reservation: u32 = 0x00010000;

pub const Mutex = struct {
    native: windows.SRWLOCK = windows.SRWLOCK_INIT,

    pub fn lock(self: *Mutex) void {
        AcquireSRWLockExclusive(&self.native);
    }

    pub fn unlock(self: *Mutex) void {
        ReleaseSRWLockExclusive(&self.native);
    }
};

pub const Frame = union(enum) { line: []const u8, oversized, truncated };

/// The line slice is borrowed only for the duration of callback. Buffer size is
/// the configured maximum JSON line length; an optional terminal CR is excluded.
pub const Framer = struct {
    buffer: []u8,
    used: usize = 0,
    discarding: bool = false,
    extra_cr: bool = false,

    pub fn init(buffer: []u8) Framer {
        return .{ .buffer = buffer };
    }

    pub fn push(self: *Framer, chunk: []const u8, context: anytype, comptime callback: anytype) !void {
        var i: usize = 0;
        while (i < chunk.len) : (i += 1) {
            const byte = chunk[i];
            if (self.discarding) {
                if (byte == '\n') self.discarding = false;
                continue;
            }
            if (byte == '\n') {
                const length = if (!self.extra_cr and self.used > 0 and self.buffer[self.used - 1] == '\r') self.used - 1 else self.used;
                self.used = 0;
                self.extra_cr = false;
                try callback(context, Frame{ .line = self.buffer[0..length] });
                continue;
            }
            if (!self.extra_cr and self.used < self.buffer.len) {
                self.buffer[self.used] = byte;
                self.used += 1;
                continue;
            }
            if (!self.extra_cr and byte == '\r') {
                self.extra_cr = true;
                continue;
            }
            self.used = 0;
            self.extra_cr = false;
            self.discarding = true;
            try callback(context, @as(Frame, .oversized));
        }
    }

    /// A partial line is never passed to the protocol executor at EOF. An
    /// oversized line was already reported exactly once when it exceeded cap.
    pub fn eof(self: *Framer, context: anytype, comptime callback: anytype) !void {
        const partial = !self.discarding and (self.used != 0 or self.extra_cr);
        self.used = 0;
        self.extra_cr = false;
        self.discarding = false;
        if (partial) try callback(context, @as(Frame, .truncated));
    }
};

pub const DeliveryState = enum(u8) { pending, cancelled, writing, done };

pub const Delivery = struct {
    status: std.atomic.Value(DeliveryState) = .init(.pending),

    /// Only the slot owner calls reset, after the worker joined and the previous
    /// queued delivery reached done (or no response was queued).
    pub fn reset(self: *Delivery) void {
        self.status.store(.pending, .release);
    }

    pub fn state(self: *const Delivery) DeliveryState {
        return self.status.load(.acquire);
    }

    pub fn isDone(self: *const Delivery) bool {
        return self.state() == .done;
    }

    pub fn cancel(self: *Delivery) bool {
        return self.status.cmpxchgStrong(.pending, .cancelled, .acq_rel, .acquire) == null;
    }

    fn claim(self: *Delivery) bool {
        // This is the response commit point, before the first sink write.
        return self.status.cmpxchgStrong(.pending, .writing, .acq_rel, .acquire) == null;
    }

    fn complete(self: *Delivery) void {
        self.status.store(.done, .release);
    }
};

pub const CancelCallback = *const fn (?*anyopaque) void;

/// A binding exists only while the common capability call is active. Callbacks
/// must be nonblocking and must not reenter this Cancellation. Calling under
/// the mutex means unbind returns only after any in-flight callback has ended.
pub const Cancellation = struct {
    requested: std.atomic.Value(bool) = .init(false),
    mutex: Mutex = .{},
    context: ?*anyopaque = null,
    callback: ?CancelCallback = null,

    /// The slot owner resets only after its previous worker has joined.
    pub fn reset(self: *Cancellation) void {
        self.mutex.lock();
        defer self.mutex.unlock();
        std.debug.assert(self.callback == null);
        self.requested.store(false, .release);
    }

    pub fn isRequested(self: *const Cancellation) bool {
        return self.requested.load(.acquire);
    }

    pub fn bind(self: *Cancellation, context: ?*anyopaque, callback: CancelCallback) void {
        self.mutex.lock();
        defer self.mutex.unlock();
        std.debug.assert(self.callback == null);
        self.context = context;
        self.callback = callback;
        if (self.isRequested()) callback(context);
    }

    pub fn unbind(self: *Cancellation) void {
        self.mutex.lock();
        defer self.mutex.unlock();
        self.callback = null;
        self.context = null;
    }

    pub fn cancel(self: *Cancellation, delivery: ?*Delivery) void {
        // Suppress an uncommitted response before waiting for the capability
        // binding lock. Repeated cancellation still suppresses a new delivery.
        if (delivery) |value| _ = value.cancel();
        self.mutex.lock();
        defer self.mutex.unlock();
        if (!self.requested.swap(true, .acq_rel)) {
            if (self.callback) |callback| callback(self.context);
        }
    }
};

pub const Sink = struct {
    context: ?*anyopaque,
    write_all: *const fn (?*anyopaque, []const u8) anyerror!void,
    /// The first write/event-wait failure is reported after state publication
    /// and outside all writer locks. Must be nonblocking; must not destroy or
    /// reenter Writer. A process owner can terminate a reader blocked on stdin.
    on_failure: ?*const fn (?*anyopaque, anyerror) void = null,
};

pub const Writer = struct {
    const Entry = struct { length: usize, delivery: ?*Delivery };

    allocator: std.mem.Allocator,
    entries: []Entry,
    payload: []u8,
    stride: usize,
    sink: Sink,
    mutex: Mutex = .{},
    head: usize = 0,
    count: usize = 0,
    closing: bool = false,
    draining: bool = false,
    failure: ?anyerror = null,
    wake: *anyopaque,
    thread: ?*anyopaque = null,

    /// The allocator must be the run's ledger-derived bounded allocator. Every
    /// queue entry owns one fixed payload segment, including its trailing LF.
    pub fn init(allocator: std.mem.Allocator, capacity: usize, max_frame_bytes: usize, sink: Sink) !Writer {
        if (capacity == 0 or max_frame_bytes == 0) return error.InvalidCapacity;
        const stride = std.math.add(usize, max_frame_bytes, 1) catch return error.InvalidCapacity;
        const total = std.math.mul(usize, capacity, stride) catch return error.InvalidCapacity;
        const entries = try allocator.alloc(Entry, capacity);
        errdefer allocator.free(entries);
        const payload = try allocator.alloc(u8, total);
        errdefer allocator.free(payload);
        const wake = CreateEventW(null, 0, 0, null) orelse return error.EventCreationFailed;
        return .{ .allocator = allocator, .entries = entries, .payload = payload, .stride = stride, .sink = sink, .wake = wake };
    }

    /// Writer must have reached its final address before starting. CreateThread
    /// uses that explicit reservation without std.Thread's private heap block.
    pub fn start(self: *Writer, stack_size: usize) !void {
        self.mutex.lock();
        defer self.mutex.unlock();
        if (self.thread != null) return error.AlreadyStarted;
        if (self.closing) return error.WriterClosed;
        if (stack_size == 0) return error.InvalidStackSize;
        self.thread = CreateThread(null, stack_size, threadMain, self, stack_size_param_is_a_reservation, null) orelse return error.ThreadCreationFailed;
    }

    /// Copies and enqueues without waiting for the sink or a free queue slot.
    /// No response may contain literal CR/LF; JSON escaped newlines are fine.
    pub fn enqueue(self: *Writer, frame: []const u8, delivery: ?*Delivery) !void {
        if (frame.len >= self.stride) return error.FrameTooLarge;
        if (std.mem.indexOfAny(u8, frame, "\r\n") != null) return error.EmbeddedNewline;
        self.mutex.lock();
        defer self.mutex.unlock();
        if (self.closing) return error.WriterClosed;
        if (self.failure) |failure| return failure;
        if (self.count == self.entries.len) return error.QueueFull;
        if (delivery) |value| {
            const status = value.state();
            if (status != .pending and status != .cancelled) return error.InvalidDeliveryState;
        }
        const tail = (self.head + self.count) % self.entries.len;
        const offset = tail * self.stride;
        @memcpy(self.payload[offset..][0..frame.len], frame);
        self.payload[offset + frame.len] = '\n';
        self.entries[tail] = .{ .length = frame.len + 1, .delivery = delivery };
        self.count += 1;
        // The handle remains owned until a successful finish and deinit.
        _ = SetEvent(self.wake);
    }

    /// Deterministic pre-start consumer for tests. A running writer is always
    /// the sole sink owner; concurrent/manual consumers are rejected.
    pub fn drainOne(self: *Writer) !bool {
        self.mutex.lock();
        const started = self.thread != null;
        self.mutex.unlock();
        if (started) return error.WriterAlreadyStarted;
        return self.consumeOne();
    }

    fn consumeOne(self: *Writer) !bool {
        self.mutex.lock();
        if (self.draining) {
            self.mutex.unlock();
            return error.ConcurrentDrain;
        }
        if (self.count == 0) {
            self.mutex.unlock();
            return false;
        }
        self.draining = true;
        const entry = self.entries[self.head];
        const offset = self.head * self.stride;
        const bytes = self.payload[offset..][0..entry.length];
        const failed = self.failure != null;
        self.mutex.unlock();

        // Keep this queue slot occupied while writing; producers can never
        // overwrite the borrowed payload even when the pipe is blocked.
        const claimed = if (entry.delivery) |delivery| delivery.claim() else true;
        var write_error: ?anyerror = null;
        if (claimed and !failed) self.sink.write_all(self.sink.context, bytes) catch |err| {
            write_error = err;
        };
        if (entry.delivery) |delivery| delivery.complete();

        self.mutex.lock();
        self.head = (self.head + 1) % self.entries.len;
        self.count -= 1;
        self.draining = false;
        const first_failure = write_error != null and self.failure == null;
        if (first_failure) self.failure = write_error;
        self.mutex.unlock();
        if (first_failure) {
            if (self.sink.on_failure) |callback| callback(self.sink.context, write_error.?);
        }
        if (write_error) |err| return err;
        return true;
    }

    fn threadMain(context: ?*anyopaque) callconv(.winapi) u32 {
        const self: *Writer = @ptrCast(@alignCast(context.?));
        while (true) {
            const consumed = self.consumeOne() catch true;
            if (consumed) continue;
            self.mutex.lock();
            const finished = self.closing and self.count == 0;
            self.mutex.unlock();
            if (finished) return 0;
            if (WaitForSingleObject(self.wake, infinite) != wait_object_0) {
                self.mutex.lock();
                const first_failure = self.failure == null;
                if (first_failure) self.failure = error.EventWaitFailed;
                self.closing = true;
                self.mutex.unlock();
                if (first_failure) {
                    if (self.sink.on_failure) |callback| callback(self.sink.context, error.EventWaitFailed);
                }
            }
        }
    }

    /// Signals EOF and drains accepted entries. timeout_ms is finite: INFINITE
    /// is rejected. A timeout does not close handles, join, or free live state.
    /// The run owner must exit the process before releasing that state.
    pub fn finish(self: *Writer, timeout_ms: u32) !void {
        if (timeout_ms == infinite) return error.InvalidTimeout;
        self.mutex.lock();
        self.closing = true;
        const thread = self.thread;
        _ = SetEvent(self.wake);
        self.mutex.unlock();
        const handle = thread orelse return error.WriterNotStarted;
        switch (WaitForSingleObject(handle, timeout_ms)) {
            wait_object_0 => {},
            wait_timeout => return error.ShutdownTimedOut,
            else => return error.ThreadWaitFailed,
        }
        _ = CloseHandle(handle);
        self.mutex.lock();
        self.thread = null;
        const failure = self.failure;
        self.mutex.unlock();
        if (failure) |err| return err;
    }

    /// Safe only after finish joined, or when start was never called. Refuse to
    /// free a live writer even if a caller's error defer runs after a timeout.
    pub fn deinit(self: *Writer) void {
        if (self.thread != null) return;
        var i: usize = 0;
        while (i < self.count) : (i += 1) {
            const entry = self.entries[(self.head + i) % self.entries.len];
            if (entry.delivery) |delivery| delivery.complete();
        }
        _ = CloseHandle(self.wake);
        self.allocator.free(self.payload);
        self.allocator.free(self.entries);
        self.* = undefined;
    }
};
