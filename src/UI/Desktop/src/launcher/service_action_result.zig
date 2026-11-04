const std = @import("std");

/// One synchronous native invocation owns all I/O and child observation. Hosts may
/// schedule the invocation, but never run another read/wait/deadline alongside it.
pub const Handle = std.os.windows.HANDLE;
pub const Options = extern struct {
    parent_timeout_ms: u32,
    connect_timeout_ms: u32,
    max_frame_bytes: u32,
    truncate_utf16_units: u32,
    input_buffer_bytes: u32,
    output_buffer_bytes: u32,
};
pub const Status = enum(u32) { ok, bridge_failure, transport, unauthorized, invalid_length, invalid_utf8, eof, canceled, timeout, remote_error, child_exit, invalid_channel, out_of_memory, invalid_options, io };
pub const Result = extern struct {
    status: Status = .ok,
    os_error: u32 = 0,
    exit_code: u32 = 0,
    text: ?[*]u16 = null,
    text_len: usize = 0,

    pub fn deinit(self: *Result, allocator: std.mem.Allocator) void {
        if (self.text) |p| allocator.free(p[0..self.text_len]);
        self.* = .{};
    }
};
pub const OwnedHandle = extern struct { raw: usize = 0, cookie: usize = 0 };
pub const Bridge = extern struct {
    context: ?*anyopaque,
    /// Shared WindowsSessionPipeFactory only: native supplies name and buffers.
    server: *const fn (?*anyopaque, [*]const u16, usize, u32, u32, *OwnedHandle) callconv(.c) u32,
    /// OS launch only: native supplies channel and restart, receives owned child.
    launch: *const fn (?*anyopaque, [*]const u16, usize, u32, *OwnedHandle, *u32) callconv(.c) u32,
    /// Explicit OS connect call. Deadline selection remains native.
    client: *const fn (?*anyopaque, [*]const u16, usize, u32, *OwnedHandle) callconv(.c) u32,
    release: *const fn (?*anyopaque, OwnedHandle) callconv(.c) void,
};

pub const os = struct {
    pub const Overlapped = extern struct { internal: usize = 0, internal_high: usize = 0, offset: u64 = 0, event: ?Handle = null };
    pub extern "kernel32" fn CreateEventW(?*anyopaque, c_int, c_int, ?[*:0]const u16) callconv(.winapi) ?Handle;
    pub extern "kernel32" fn ResetEvent(Handle) callconv(.winapi) c_int;
    pub extern "kernel32" fn CloseHandle(Handle) callconv(.winapi) c_int;
    pub extern "kernel32" fn GetLastError() callconv(.winapi) u32;
    pub extern "kernel32" fn ConnectNamedPipe(Handle, *Overlapped) callconv(.winapi) c_int;
    pub extern "kernel32" fn GetNamedPipeClientProcessId(Handle, *u32) callconv(.winapi) c_int;
    pub extern "kernel32" fn ReadFile(Handle, [*]u8, u32, *u32, ?*Overlapped) callconv(.winapi) c_int;
    pub extern "kernel32" fn WriteFile(Handle, [*]const u8, u32, *u32, ?*Overlapped) callconv(.winapi) c_int;
    pub extern "kernel32" fn FlushFileBuffers(Handle) callconv(.winapi) c_int;
    pub extern "kernel32" fn GetOverlappedResult(Handle, *Overlapped, *u32, c_int) callconv(.winapi) c_int;
    pub extern "kernel32" fn CancelIoEx(Handle, *Overlapped) callconv(.winapi) c_int;
    pub extern "kernel32" fn WaitForMultipleObjects(u32, [*]const Handle, c_int, u32) callconv(.winapi) u32;
    pub extern "kernel32" fn WaitForSingleObject(Handle, u32) callconv(.winapi) u32;
    pub extern "kernel32" fn GetTickCount64() callconv(.winapi) u64;
    pub extern "kernel32" fn GetExitCodeProcess(Handle, *u32) callconv(.winapi) c_int;
    pub extern "kernel32" fn WideCharToMultiByte(u32, u32, [*]const u16, c_int, ?[*]u8, c_int, ?*const u8, ?*c_int) callconv(.winapi) c_int;
    pub extern "kernel32" fn MultiByteToWideChar(u32, u32, [*]const u8, c_int, ?[*]u16, c_int) callconv(.winapi) c_int;
    pub extern "ole32" fn CoCreateGuid(*std.os.windows.GUID) callconv(.winapi) i32;
    pub const infinite: u32 = 0xffffffff;
};

pub fn validOptions(options: Options) bool {
    return options.max_frame_bytes <= std.math.maxInt(i32) and options.connect_timeout_ms <= std.math.maxInt(i32) and
        options.input_buffer_bytes > 0 and options.output_buffer_bytes > 0 and options.input_buffer_bytes <= std.math.maxInt(i32) and options.output_buffer_bytes <= std.math.maxInt(i32);
}
fn failure(status: Status) Result {
    return .{ .status = status };
}
fn osFailure() Result {
    return .{ .status = .transport, .os_error = os.GetLastError() };
}
fn asHandle(raw: usize) Handle {
    return @ptrFromInt(raw);
}
fn remaining(start: u64, timeout_ms: u32) u32 {
    if (timeout_ms == os.infinite) return os.infinite;
    const elapsed = os.GetTickCount64() -| start;
    return if (elapsed >= timeout_ms) 0 else timeout_ms - @as(u32, @intCast(elapsed));
}

pub fn validChannel(name: []const u16) bool {
    const prefix = "ResourceManager.ServiceStart.";
    if (name.len < prefix.len) return false;
    var i: usize = 0;
    while (i < prefix.len) : (i += 1) if (name[i] != prefix[i]) return false;
    var suffix = name[prefix.len..];
    // Guid.TryParseExact trims Unicode whitespace; keep the actual pipe name.
    while (suffix.len > 0 and whitespace(suffix[0])) suffix = suffix[1..];
    while (suffix.len > 0 and whitespace(suffix[suffix.len - 1])) suffix = suffix[0 .. suffix.len - 1];
    if (suffix.len != 32) return false;
    for (suffix) |unit| if (!(unit >= '0' and unit <= '9') and !(unit >= 'a' and unit <= 'f') and !(unit >= 'A' and unit <= 'F')) return false;
    return true;
}
fn whitespace(unit: u16) bool {
    return (unit >= 9 and unit <= 13) or unit == 32 or unit == 0x85 or unit == 0xa0 or unit == 0x1680 or
        (unit >= 0x2000 and unit <= 0x200a) or unit == 0x2028 or unit == 0x2029 or unit == 0x202f or unit == 0x205f or unit == 0x3000;
}

/// Encodes only the final wire region. Count-only conversion preserves the old
/// byte threshold without first allocating a mirror of the whole original text.
pub fn encode(allocator: std.mem.Allocator, options: Options, input: []const u16) ![]u8 {
    if (!validOptions(options) or input.len > std.math.maxInt(c_int)) return error.InvalidOptions;
    var units = input;
    const full_count = if (units.len == 0) 0 else os.WideCharToMultiByte(65001, 0, units.ptr, @intCast(units.len), null, 0, null, null);
    if (full_count == 0 and units.len != 0) return error.Encoding;
    if (full_count > options.max_frame_bytes) units = units[0..@min(units.len, options.truncate_utf16_units)];
    const count = if (units.len == 0) 0 else os.WideCharToMultiByte(65001, 0, units.ptr, @intCast(units.len), null, 0, null, null);
    if (count == 0 and units.len != 0) return error.Encoding;
    const frame = try allocator.alloc(u8, 4 + @as(usize, @intCast(count)));
    errdefer allocator.free(frame);
    std.mem.writeInt(i32, frame[0..4], count, .little);
    if (count > 0 and os.WideCharToMultiByte(65001, 0, units.ptr, @intCast(units.len), frame[4..].ptr, count, null, null) != count) return error.Encoding;
    return frame;
}

pub fn write(allocator: std.mem.Allocator, options: Options, bridge: Bridge, name: []const u16, text: []const u16) Result {
    if (!validOptions(options)) return failure(.invalid_options);
    if (!validChannel(name)) return failure(.invalid_channel);
    var client: OwnedHandle = .{};
    // The old sender connects before encoding; a missing server still wins over
    // any later encoding failure. No native timer or queue duplicates the OS call.
    if (bridge.client(bridge.context, name.ptr, name.len, options.connect_timeout_ms, &client) != 0) return failure(.bridge_failure);
    defer bridge.release(bridge.context, client);
    const frame = encode(allocator, options, text) catch |err| return failure(if (err == error.OutOfMemory) .out_of_memory else .invalid_options);
    defer allocator.free(frame);
    var offset: usize = 0;
    while (offset < frame.len) {
        var written: u32 = 0;
        if (os.WriteFile(asHandle(client.raw), frame[offset..].ptr, @intCast(frame.len - offset), &written, null) == 0) return .{ .status = .io, .os_error = os.GetLastError() };
        if (written == 0) return failure(.eof);
        offset += written;
    }
    // NamedPipeClientStream.Flush is a no-op for this synchronous write-through
    // stream. FlushFileBuffers would add an acknowledgement wait absent in C#.
    return .{};
}

const Receiver = struct {
    allocator: std.mem.Allocator,
    options: Options,
    pipe: Handle,
    pid: u32,
    event: Handle,
    overlapped: os.Overlapped = .{},
    phase: enum { connecting, header, body, done } = .connecting,
    pending: bool = false,
    header: [4]u8 = undefined,
    body: ?[]u8 = null,
    offset: usize = 0,
    result: Result = .{},

    fn init(allocator: std.mem.Allocator, options: Options, pipe: Handle, pid: u32) !Receiver {
        return .{ .allocator = allocator, .options = options, .pipe = pipe, .pid = pid, .event = os.CreateEventW(null, 1, 0, null) orelse return error.Event };
    }
    fn deinit(self: *Receiver) void {
        if (self.pending) {
            _ = os.CancelIoEx(self.pipe, &self.overlapped);
            var ignored: u32 = 0;
            _ = os.GetOverlappedResult(self.pipe, &self.overlapped, &ignored, 1);
            self.pending = false;
        }
        _ = os.CloseHandle(self.event);
        if (self.body) |bytes| self.allocator.free(bytes);
        self.result.deinit(self.allocator);
    }
    fn finish(self: *Receiver, result: Result) void {
        self.result = result;
        self.phase = .done;
    }
    fn ioFailure(self: *Receiver) void {
        const code = os.GetLastError();
        self.finish(.{ .status = switch (code) {
            109, 232, 233 => .eof,
            995 => .canceled,
            else => .io,
        }, .os_error = code });
    }
    fn reset(self: *Receiver) void {
        _ = os.ResetEvent(self.event);
        // Low-bit event suppresses an IOCP completion packet if the shared
        // managed SafePipeHandle has already been associated with its IOCP.
        self.overlapped = .{ .event = @ptrFromInt(@intFromPtr(self.event) | 1) };
    }
    fn connected(self: *Receiver) void {
        var actual: u32 = 0;
        if (os.GetNamedPipeClientProcessId(self.pipe, &actual) == 0 or actual != self.pid) {
            self.finish(failure(.unauthorized));
            return;
        }
        self.phase = .header;
        self.offset = 0;
    }
    fn start(self: *Receiver) void {
        self.reset();
        if (os.ConnectNamedPipe(self.pipe, &self.overlapped) != 0) self.connected() else switch (os.GetLastError()) {
            997 => self.pending = true,
            535 => self.connected(),
            else => self.ioFailure(),
        }
        self.pump();
    }
    fn consume(self: *Receiver, count: u32) void {
        if (self.phase == .connecting) {
            self.connected();
            return;
        }
        if (count == 0) {
            self.finish(failure(.eof));
            return;
        }
        self.offset += count;
        if (self.phase == .header and self.offset == self.header.len) {
            const size = std.mem.readInt(i32, &self.header, .little);
            if (size < 0 or size > self.options.max_frame_bytes) {
                self.finish(failure(.invalid_length));
                return;
            }
            if (size == 0) {
                self.finish(.{});
                return;
            }
            self.body = self.allocator.alloc(u8, @intCast(size)) catch {
                self.finish(failure(.out_of_memory));
                return;
            };
            self.offset = 0;
            self.phase = .body;
        } else if (self.phase == .body and self.offset == self.body.?.len) {
            const bytes = self.body.?;
            if (!std.unicode.utf8ValidateSlice(bytes)) {
                self.finish(failure(.invalid_utf8));
                return;
            }
            const count16 = os.MultiByteToWideChar(65001, 8, bytes.ptr, @intCast(bytes.len), null, 0);
            if (count16 <= 0) {
                self.finish(failure(.invalid_utf8));
                return;
            }
            const text = self.allocator.alloc(u16, @intCast(count16)) catch {
                self.finish(failure(.out_of_memory));
                return;
            };
            if (os.MultiByteToWideChar(65001, 8, bytes.ptr, @intCast(bytes.len), text.ptr, count16) != count16) {
                self.allocator.free(text);
                self.finish(failure(.invalid_utf8));
                return;
            }
            self.finish(.{ .text = text.ptr, .text_len = text.len });
        }
    }
    fn pump(self: *Receiver) void {
        while (!self.pending and self.phase != .done) {
            const target = if (self.phase == .header) self.header[self.offset..] else self.body.?[self.offset..];
            self.reset();
            var count: u32 = 0;
            if (os.ReadFile(self.pipe, target.ptr, @intCast(target.len), &count, &self.overlapped) != 0) self.consume(count) else if (os.GetLastError() == 997) {
                self.pending = true;
            } else {
                self.ioFailure();
            }
        }
    }
    fn ready(self: *Receiver) void {
        var count: u32 = 0;
        self.pending = false;
        if (os.GetOverlappedResult(self.pipe, &self.overlapped, &count, 0) == 0) self.ioFailure() else self.consume(count);
        self.pump();
    }
    fn take(self: *Receiver) Result {
        const value = self.result;
        self.result = .{};
        return value;
    }
};

/// Stable native storage lets the binding register the real pipe operation
/// before handing its wait to a host worker, just as the original async method.
/// The host keeps only this opaque owner and borrowed handle leases.
pub const ReadSession = struct { receiver: Receiver, start: u64, timeout_ms: u32, cancel: ?Handle };
pub fn beginRead(allocator: std.mem.Allocator, options: Options, pipe: Handle, pid: u32, cancel: ?Handle, timeout_ms: u32, result: *Result) ?*ReadSession {
    result.* = .{};
    if (!validOptions(options)) {
        result.* = failure(.invalid_options);
        return null;
    }
    if (cancel) |signal| if (os.WaitForSingleObject(signal, 0) == 0) {
        result.* = failure(.canceled);
        return null;
    };
    const session = allocator.create(ReadSession) catch {
        result.* = failure(.out_of_memory);
        return null;
    };
    const receiver = Receiver.init(allocator, options, pipe, pid) catch {
        allocator.destroy(session);
        result.* = osFailure();
        return null;
    };
    session.* = .{ .receiver = receiver, .start = os.GetTickCount64(), .timeout_ms = timeout_ms, .cancel = cancel };
    session.receiver.start();
    return session;
}
pub fn abortRead(session: *ReadSession) void {
    const allocator = session.receiver.allocator;
    session.receiver.deinit();
    allocator.destroy(session);
}
pub fn finishRead(session: *ReadSession) Result {
    defer abortRead(session);
    const receiver = &session.receiver;
    while (receiver.phase != .done) {
        const handles: [2]Handle = .{ receiver.event, session.cancel orelse receiver.event };
        const outcome = os.WaitForMultipleObjects(if (session.cancel != null) 2 else 1, &handles, 0, remaining(session.start, session.timeout_ms));
        switch (outcome) {
            0 => receiver.ready(),
            1 => return failure(.canceled),
            258 => return failure(.timeout),
            else => return osFailure(),
        }
    }
    return receiver.take();
}
/// Synchronous convenience over the same owner, not a second implementation.
pub fn read(allocator: std.mem.Allocator, options: Options, pipe: Handle, pid: u32, cancel: ?Handle, timeout_ms: u32) Result {
    var result: Result = .{};
    const session = beginRead(allocator, options, pipe, pid, cancel, timeout_ms, &result) orelse return result;
    return finishRead(session);
}

pub fn run(allocator: std.mem.Allocator, options: Options, bridge: Bridge, restart: bool, cancel: ?Handle) Result {
    if (!validOptions(options)) return failure(.invalid_options);
    if (cancel) |signal| if (os.WaitForSingleObject(signal, 0) == 0) return failure(.canceled);
    var guid: std.os.windows.GUID = undefined;
    if (os.CoCreateGuid(&guid) < 0) return failure(.transport);
    var ascii: ["ResourceManager.ServiceStart.".len + 32]u8 = undefined;
    const name8 = std.fmt.bufPrint(&ascii, "ResourceManager.ServiceStart.{x:0>8}{x:0>4}{x:0>4}{x:0>2}{x:0>2}{x:0>2}{x:0>2}{x:0>2}{x:0>2}{x:0>2}{x:0>2}", .{ guid.Data1, guid.Data2, guid.Data3, guid.Data4[0], guid.Data4[1], guid.Data4[2], guid.Data4[3], guid.Data4[4], guid.Data4[5], guid.Data4[6], guid.Data4[7] }) catch unreachable;
    var name: [ascii.len]u16 = undefined;
    for (name8, 0..) |byte, i| name[i] = byte;
    var server: OwnedHandle = .{};
    if (bridge.server(bridge.context, &name, name.len, options.input_buffer_bytes, options.output_buffer_bytes, &server) != 0) return failure(.bridge_failure);
    defer bridge.release(bridge.context, server);
    const start = os.GetTickCount64(); // Same position as the original CancellationTokenSource.
    var child: OwnedHandle = .{};
    var pid: u32 = 0;
    if (bridge.launch(bridge.context, &name, name.len, @intFromBool(restart), &child, &pid) != 0) return failure(.bridge_failure);
    defer bridge.release(bridge.context, child);
    if (cancel) |signal| if (os.WaitForSingleObject(signal, 0) == 0) return failure(.canceled);
    if (remaining(start, options.parent_timeout_ms) == 0) return failure(.timeout);
    var receiver = Receiver.init(allocator, options, asHandle(server.raw), pid) catch return osFailure();
    defer receiver.deinit(); // Drain before releasing either OS bridge object.
    receiver.start();
    var exited = false;
    var exit_code: u32 = 0;
    while (!exited or receiver.phase != .done) {
        var handles: [3]Handle = undefined;
        var count: u32 = 0;
        var child_index: ?u32 = null;
        var io_index: ?u32 = null;
        var cancel_index: ?u32 = null;
        if (!exited) {
            child_index = count;
            handles[count] = asHandle(child.raw);
            count += 1;
        }
        if (receiver.pending) {
            io_index = count;
            handles[count] = receiver.event;
            count += 1;
        }
        if (cancel) |handle| {
            cancel_index = count;
            handles[count] = handle;
            count += 1;
        }
        const index = os.WaitForMultipleObjects(count, &handles, 0, remaining(start, options.parent_timeout_ms));
        if (index == 258) return failure(.timeout);
        if (index >= count) return osFailure();
        if (cancel_index != null and index == cancel_index.?) return failure(.canceled);
        if (child_index != null and index == child_index.?) {
            if (os.GetExitCodeProcess(asHandle(child.raw), &exit_code) == 0) return osFailure();
            exited = true;
        } else if (io_index != null and index == io_index.?) receiver.ready();
    }
    var result = receiver.take();
    result.exit_code = exit_code;
    if (result.status == .ok) {
        if (result.text_len != 0) result.status = .remote_error else if (exit_code != 0) result.status = .child_exit;
    }
    return result;
}
