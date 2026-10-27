const std = @import("std");
pub const win32 = @import("win32.zig");
const w = win32;
pub const HANDLE = w.HANDLE;
pub const protocol = @import("protocol.zig");
pub const BackendSession = protocol.BackendSession;
pub const ShowCommand = protocol.ShowCommand;
pub const Scope = protocol.Scope;
pub const Event = protocol.Event;
pub const Callback = *const fn (?*anyopaque, Event) void;
pub const Config = struct {
    executable: []const u8,
    /// Owner-selected cleanup poll interval; only used while joining canceled I/O.
    cancel_poll_ms: u32,
    /// Already quoted additional command-line arguments; executable and pipe handles are added here.
    arguments: []const u8 = "",
    session: BackendSession = .{},
    language: []const u8 = "en",
    show: ShowCommand = .show,
};

const Message = struct { bytes: [protocol.max_line]u8 = undefined, len: usize = 0 };
const queue_capacity = 16;

/// One instance belongs to one host block. All send methods are thread safe and
/// copy their input into a fixed queue. run owns and joins every native resource.
pub const Client = struct {
    mutex: w.Mutex = .{},
    queue: [queue_capacity]Message = undefined,
    head: usize = 0,
    count: usize = 0,
    active: bool = false,
    ending: bool = false,
    wake: ?HANDLE = null,
    failure: ?HANDLE = null,
    read: ?HANDLE = null,
    write: ?HANDLE = null,
    context: ?*anyopaque = null,
    callback: Callback = undefined,

    pub fn sendSession(self: *Client, session: BackendSession) !void {
        var escaped: [protocol.max_line]u8 = undefined;
        const reason = if (session.reason) |s| try protocol.quote(&escaped, s) else "null";
        var message: [protocol.max_line]u8 = undefined;
        try self.enqueue(try std.fmt.bufPrint(&message, "{{\"type\":\"backend-session\",\"ready\":{s},\"generation\":{d},\"reason\":{s}}}\n", .{ if (session.ready) "true" else "false", session.generation, reason }));
    }
    pub fn sendLanguage(self: *Client, language: []const u8) !void {
        var escaped: [protocol.max_line]u8 = undefined;
        const value = try protocol.quote(&escaped, language);
        var message: [protocol.max_line]u8 = undefined;
        try self.enqueue(try std.fmt.bufPrint(&message, "{{\"type\":\"language\",\"language\":{s}}}\n", .{value}));
    }
    pub fn showWindow(self: *Client, command: ShowCommand) !void {
        var message: [128]u8 = undefined;
        try self.enqueue(try std.fmt.bufPrint(&message, "{{\"type\":\"show-window\",\"command\":\"{s}\"}}\n", .{@tagName(command)}));
    }
    pub fn sendVisibility(self: *Client, visible: bool) !void {
        try self.enqueue(if (visible) "{\"type\":\"visibility\",\"visible\":true}\n" else "{\"type\":\"visibility\",\"visible\":false}\n");
    }
    fn enqueue(self: *Client, value: []const u8) !void {
        if (value.len > protocol.max_line) return error.MessageTooLong;
        self.mutex.lock();
        defer self.mutex.unlock();
        if (!self.active or self.ending or self.wake == null) return error.NotRunning;
        if (self.count == queue_capacity) return error.QueueFull;
        const slot = &self.queue[(self.head + self.count) % queue_capacity];
        @memcpy(slot.bytes[0..value.len], value);
        slot.len = value.len;
        self.count += 1;
        _ = w.SetEvent(self.wake.?);
    }

    pub fn run(self: *Client, stop: HANDLE, context: ?*anyopaque, callback: Callback, config: Config) !void {
        // The caller supplies a fresh stop token for each run; a pre-stopped run never launches .NET.
        if (w.WaitForSingleObject(stop, 0) == 0) return;
        self.mutex.lock();
        if (self.active) {
            self.mutex.unlock();
            return error.AlreadyRunning;
        }
        self.active = true;
        self.mutex.unlock();
        var failure_code: u32 = 1;
        errdefer callback(context, .{ .crashed = failure_code });
        defer {
            self.mutex.lock();
            self.active = false;
            self.ending = true;
            self.wake = null;
            self.mutex.unlock();
        }
        if (config.cancel_poll_ms == 0 or config.cancel_poll_ms == w.infinite) return error.InvalidCancelPollInterval;
        const wake = w.CreateEventW(null, 1, 0, null) orelse return error.CreateEventFailed;
        defer w.close(wake);
        const failure = w.CreateEventW(null, 1, 0, null) orelse return error.CreateEventFailed;
        defer w.close(failure);
        const child = try spawn(config);
        defer w.close(child.process);
        defer w.close(child.read);
        var write_open = true;
        defer if (write_open) w.close(child.write);
        self.mutex.lock();
        self.head = 0;
        self.count = 0;
        self.ending = false;
        self.wake = wake;
        self.failure = failure;
        self.read = child.read;
        self.write = child.write;
        self.context = context;
        self.callback = callback;
        self.active = true;
        self.mutex.unlock();
        defer {
            // Clear the publication before any associated event handle is closed.
            self.mutex.lock();
            self.active = false;
            self.ending = true;
            self.wake = null;
            self.mutex.unlock();
        }
        // On failure before workers start, EOF still drives the child's root to stop.
        var joined = false;
        errdefer if (!joined) {
            w.close(child.write);
            write_open = false;
            _ = w.WaitForSingleObject(child.process, w.infinite);
        };
        try self.sendSession(config.session);
        try self.sendLanguage(config.language);
        try self.showWindow(config.show);
        // Publish complete startup facts before permitting the child's root to run.
        try self.enqueue("{\"type\":\"parent-wants-me-running\",\"running\":true}\n");
        const writer = try std.Thread.spawn(.{}, writerMain, .{self});
        const reader = std.Thread.spawn(.{}, readerMain, .{self}) catch |err| {
            self.finishWriter();
            while (w.WaitForSingleObject(writer.getHandle(), config.cancel_poll_ms) == 258) _ = w.CancelSynchronousIo(writer.getHandle());
            writer.join();
            return err;
        };
        callback(context, .{ .running = child.pid });
        const handles = [_]HANDLE{ stop, child.process, failure };
        const outcome = w.WaitForMultipleObjects(handles.len, &handles, 0, w.infinite);
        const stopped = outcome == 0;
        // stop is prioritized over pending facts and remains the writer's final message.
        self.finishWriter();
        // The child contract drains its tree on stop/EOF. No process is detached.
        // A stopped child that is no longer reading cannot trap us in WriteFile.
        // CancelSynchronousIo only sees currently pending I/O. Repeat until join
        // because the writer may enter WriteFile immediately after cancellation.
        // This owner-configured poll only runs during cleanup; EOF also means stop.
        while (w.WaitForSingleObject(writer.getHandle(), config.cancel_poll_ms) == 258) {
            _ = w.CancelSynchronousIo(writer.getHandle());
        }
        writer.join();
        w.close(child.write);
        write_open = false;
        _ = w.WaitForSingleObject(child.process, w.infinite);
        _ = w.CancelSynchronousIo(reader.getHandle());
        reader.join();
        joined = true;
        var exit_code: u32 = 1;
        _ = w.GetExitCodeProcess(child.process, &exit_code);
        if (!stopped or exit_code != 0) {
            // Host must synchronously publish WindowWanted=false before returning from this callback.
            failure_code = exit_code;
            return error.ChildExited;
        }
    }

    fn finishWriter(self: *Client) void {
        self.mutex.lock();
        self.ending = true;
        self.count = 0;
        _ = w.SetEvent(self.wake.?);
        self.mutex.unlock();
    }
    fn writerMain(self: *Client) void {
        var message: Message = undefined;
        while (true) {
            _ = w.WaitForSingleObject(self.wake.?, w.infinite);
            self.mutex.lock();
            const ending = self.ending;
            if (ending) {
                self.mutex.unlock();
                writeAll(self.write.?, "{\"type\":\"stop\"}\n") catch {};
                return;
            }
            if (self.count == 0) {
                _ = w.ResetEvent(self.wake.?);
                self.mutex.unlock();
                continue;
            }
            message = self.queue[self.head];
            self.head = (self.head + 1) % queue_capacity;
            self.count -= 1;
            self.mutex.unlock();
            writeAll(self.write.?, message.bytes[0..message.len]) catch {
                _ = w.SetEvent(self.failure.?);
                return;
            };
        }
    }
    fn readerMain(self: *Client) void {
        var line: [protocol.max_line]u8 = undefined;
        var n: usize = 0;
        var incoming: [4096]u8 = undefined;
        var arena: [96 * 1024]u8 = undefined;
        while (true) {
            var got: u32 = 0;
            if (w.ReadFile(self.read.?, &incoming, incoming.len, &got, null) == 0 or got == 0) {
                _ = w.SetEvent(self.failure.?);
                return;
            }
            for (incoming[0..got]) |c| {
                if (c == '\n') {
                    var allocator = std.heap.FixedBufferAllocator.init(&arena);
                    const event = protocol.parse(std.mem.trimEnd(u8, line[0..n], "\r"), allocator.allocator()) catch {
                        _ = w.SetEvent(self.failure.?);
                        return;
                    };
                    if (event) |value| self.callback(self.context, value);
                    n = 0;
                } else {
                    if (n >= line.len - 1) {
                        _ = w.SetEvent(self.failure.?);
                        return;
                    }
                    line[n] = c;
                    n += 1;
                }
            }
        }
    }
};

pub fn writeAll(handle: HANDLE, bytes: []const u8) !void {
    var offset: usize = 0;
    while (offset < bytes.len) {
        var written: u32 = 0;
        if (w.WriteFile(handle, bytes[offset..].ptr, @intCast(bytes.len - offset), &written, null) == 0 or written == 0) return error.PipeWriteFailed;
        offset += written;
    }
}

const Child = struct { process: HANDLE, read: HANDLE, write: HANDLE, pid: u32 };
/// Raw endpoints are only available in test binaries for explicit demand/EOF cases.
pub const test_support = if (@import("builtin").is_test) struct {
    pub const CapturedChild = struct { child: Child, stderr: HANDLE };
    pub fn launch(config: Config) !Child {
        return spawn(config);
    }
    pub fn launchCaptured(config: Config) !CapturedChild {
        const security: w.Security = .{};
        var read: HANDLE = undefined;
        var write: HANDLE = undefined;
        if (w.CreatePipe(&read, &write, &security, 65536) == 0) return error.CreatePipeFailed;
        errdefer w.close(read);
        defer w.close(write);
        if (w.SetHandleInformation(read, 1, 0) == 0) return error.HandleInformationFailed;
        return .{ .child = try spawnWithErrorOutput(config, write), .stderr = read };
    }
} else struct {};
fn spawn(config: Config) !Child {
    return spawnWithErrorOutput(config, null);
}
fn spawnWithErrorOutput(config: Config, error_output: ?HANDLE) !Child {
    var child_read: HANDLE = undefined;
    var parent_write: HANDLE = undefined;
    var parent_read: HANDLE = undefined;
    var child_write: HANDLE = undefined;
    const security: w.Security = .{};
    if (w.CreatePipe(&child_read, &parent_write, &security, 65536) == 0) return error.CreatePipeFailed;
    defer w.close(child_read);
    errdefer w.close(parent_write);
    if (w.CreatePipe(&parent_read, &child_write, &security, 65536) == 0) return error.CreatePipeFailed;
    defer w.close(child_write);
    errdefer w.close(parent_read);
    if (w.SetHandleInformation(parent_write, 1, 0) == 0 or w.SetHandleInformation(parent_read, 1, 0) == 0) return error.HandleInformationFailed;
    var attribute_bytes: usize = 0;
    _ = w.InitializeProcThreadAttributeList(null, 1, 0, &attribute_bytes);
    var attributes: [4096]u8 align(16) = undefined;
    if (attribute_bytes > attributes.len or w.InitializeProcThreadAttributeList(&attributes, 1, 0, &attribute_bytes) == 0) return error.AttributeListFailed;
    defer w.DeleteProcThreadAttributeList(&attributes);
    var inherited = [_]HANDLE{ child_read, child_write, error_output orelse child_write };
    const inherited_count: usize = if (error_output != null) 3 else 2;
    if (w.UpdateProcThreadAttribute(&attributes, 0, 0x00020002, &inherited, @sizeOf(HANDLE) * inherited_count, null, null) == 0) return error.AttributeListFailed;
    if (std.mem.indexOfScalar(u8, config.executable, '"') != null or std.mem.indexOfScalar(u8, config.executable, 0) != null) return error.InvalidExecutable;
    var command_bytes: [32 * 1024]u8 = undefined;
    const command = try std.fmt.bufPrint(&command_bytes, "\"{s}\" {s} --resident-pipe-read {d} --resident-pipe-write {d} --resident-cancel-poll-ms {d}", .{ config.executable, config.arguments, @intFromPtr(child_read), @intFromPtr(child_write), config.cancel_poll_ms });
    var command_utf16: [32 * 1024]u16 = undefined;
    const command_len = try std.unicode.utf8ToUtf16Le(command_utf16[0 .. command_utf16.len - 1], command);
    command_utf16[command_len] = 0;
    var startup: w.StartupEx = .{ .startup = std.mem.zeroes(w.Startup), .attributes = &attributes };
    startup.startup.cb = @sizeOf(w.StartupEx);
    if (error_output) |output| {
        startup.startup.dwFlags = 0x00000100; // STARTF_USESTDHANDLES, test-only stderr capture.
        startup.startup.hStdInput = child_read;
        startup.startup.hStdOutput = output;
        startup.startup.hStdError = output;
    }
    var process: w.Process = undefined;
    if (w.CreateProcessW(null, command_utf16[0..command_len :0].ptr, null, null, 1, 0x00080000 | 0x08000000, null, null, &startup, &process) == 0) return error.CreateProcessFailed;
    w.close(process.thread);
    return .{ .process = process.process, .read = parent_read, .write = parent_write, .pid = process.pid };
}
