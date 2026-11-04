//! Existing byte-mode line protocol. An explicitly supplied test name isolates
//! both the pipe and mutex; tests must never claim the installed product name.
const std = @import("std");
const w = @import("../win32.zig");
const c = w.c;
pub const default_pipe = "ResourceManager.NativeUi.Instance";
pub const Command = enum { show, show_passive, show_passive_secondary, ensure_running, exit };
pub const Callback = *const fn (?*anyopaque, Command) void;

pub fn parse(bytes: []const u8) !Command {
    var value = bytes;
    if (std.mem.startsWith(u8, value, "\xef\xbb\xbf")) value = value[3..];
    value = std.mem.trim(u8, value, "\r\n ");
    if (value.len == 0 or std.ascii.eqlIgnoreCase(value, "show")) return .show;
    if (std.ascii.eqlIgnoreCase(value, "show-passive")) return .show_passive;
    if (std.ascii.eqlIgnoreCase(value, "show-passive-secondary")) return .show_passive_secondary;
    if (std.ascii.eqlIgnoreCase(value, "ensure-running")) return .ensure_running;
    if (std.ascii.eqlIgnoreCase(value, "exit")) return .exit;
    return error.UnknownCommand;
}

pub fn text(command: Command) []const u8 {
    return switch (command) {
        .show => "show",
        .show_passive => "show-passive",
        .show_passive_secondary => "show-passive-secondary",
        .ensure_running => "ensure-running",
        .exit => "exit",
    };
}

pub const Instance = struct {
    mutex: c.HANDLE = null,
    primary: bool = false,
    name: [300:0]u16 = undefined,

    pub fn init(name: []const u8) !Instance {
        var result: Instance = .{};
        var bytes: [300]u8 = undefined;
        const full = try std.fmt.bufPrint(&bytes, "\\\\.\\pipe\\{s}", .{name});
        const count = try std.unicode.utf8ToUtf16Le(&result.name, full);
        result.name[count] = 0;
        var mutex_name: [300:0]u16 = undefined;
        const mutex_text = if (std.mem.eql(u8, name, default_pipe)) "Local\\ResourceManager.NativeUi" else try std.fmt.bufPrint(&bytes, "Local\\{s}", .{name});
        const length = try std.unicode.utf8ToUtf16Le(&mutex_name, mutex_text);
        mutex_name[length] = 0;
        result.mutex = c.CreateMutexW(null, c.TRUE, &mutex_name);
        if (result.mutex == null) return error.Mutex;
        result.primary = c.GetLastError() != c.ERROR_ALREADY_EXISTS;
        return result;
    }
    pub fn deinit(self: *Instance) void {
        if (self.primary) _ = c.ReleaseMutex(self.mutex);
        _ = c.CloseHandle(self.mutex);
    }
    pub fn signal(self: *Instance, command: Command, timeout_ms: u32, poll_ms: u32) !void {
        if (timeout_ms == 0 or poll_ms == 0) return error.InvalidConnectPolicy;
        // Match today's .NET Connect timeout even while the primary is still
        // creating its first pipe or replacing a consumed pipe instance.
        const deadline = c.GetTickCount64() + timeout_ms;
        var pipe: c.HANDLE = c.INVALID_HANDLE_VALUE;
        while (true) {
            pipe = c.CreateFileW(&self.name, c.GENERIC_WRITE, 0, null, c.OPEN_EXISTING, 0, null);
            if (pipe != c.INVALID_HANDLE_VALUE) break;
            const code = c.GetLastError();
            if (code != c.ERROR_FILE_NOT_FOUND and code != c.ERROR_PIPE_BUSY) return error.NoInstance;
            const now = c.GetTickCount64();
            if (now >= deadline) return error.NoInstance;
            const remaining: u32 = @intCast(deadline - now);
            if (code == c.ERROR_PIPE_BUSY) _ = c.WaitNamedPipeW(&self.name, remaining) else c.Sleep(@min(poll_ms, remaining));
        }
        defer _ = c.CloseHandle(pipe);
        var buffer: [80]u8 = undefined;
        const line = try std.fmt.bufPrint(&buffer, "{s}\n", .{text(command)});
        var written: c.DWORD = 0;
        if (c.WriteFile(pipe, line.ptr, @intCast(line.len), &written, null) == 0 or written != line.len) return error.PipeIo;
    }
    pub fn run(self: *Instance, stop: c.HANDLE, context: ?*anyopaque, callback: Callback, running: *const fn (?*anyopaque) void) !void {
        const event = c.CreateEventW(null, c.TRUE, c.FALSE, null);
        if (event == null) return error.Event;
        defer _ = c.CloseHandle(event);
        var announced = false;
        while (!w.stopped(stop)) {
            const pipe = c.CreateNamedPipeW(&self.name, c.PIPE_ACCESS_INBOUND | c.FILE_FLAG_OVERLAPPED | c.FILE_FLAG_FIRST_PIPE_INSTANCE, c.PIPE_TYPE_BYTE | c.PIPE_READMODE_BYTE | c.PIPE_WAIT | c.PIPE_REJECT_REMOTE_CLIENTS, 1, 512, 512, 0, null);
            if (pipe == c.INVALID_HANDLE_VALUE) return error.PipeCreate;
            defer _ = c.CloseHandle(pipe);
            if (!announced) {
                running(context);
                announced = true;
            }
            var operation: c.OVERLAPPED = std.mem.zeroes(c.OVERLAPPED);
            operation.hEvent = event;
            _ = c.ResetEvent(event);
            if (c.ConnectNamedPipe(pipe, &operation) == 0) {
                const code = c.GetLastError();
                if (code == c.ERROR_IO_PENDING) {
                    _ = w.waitIo(stop, pipe, &operation) catch |err| {
                        if (err == error.Stopped) return;
                        continue;
                    };
                } else if (code != c.ERROR_PIPE_CONNECTED) return error.PipeConnect;
            }
            var buffer: [256]u8 = undefined;
            var used: usize = 0;
            while (used < buffer.len and !w.stopped(stop)) {
                _ = c.ResetEvent(event);
                operation = std.mem.zeroes(c.OVERLAPPED);
                operation.hEvent = event;
                var received: c.DWORD = 0;
                var success = c.ReadFile(pipe, buffer[used..].ptr, @intCast(buffer.len - used), &received, &operation);
                if (success == 0 and c.GetLastError() == c.ERROR_IO_PENDING) {
                    received = w.waitIo(stop, pipe, &operation) catch break;
                    success = c.TRUE;
                }
                if (success == 0 or received == 0) break;
                used += received;
                if (std.mem.indexOfScalar(u8, buffer[0..used], '\n')) |end| {
                    const command = parse(buffer[0..end]) catch break;
                    callback(context, command);
                    break;
                }
            }
            _ = c.DisconnectNamedPipe(pipe);
        }
    }
};

test "existing single-instance commands and BOM framing" {
    try std.testing.expectEqual(Command.show_passive_secondary, try parse("\xef\xbb\xbfshow-passive-secondary\r\n"));
    for (std.enums.values(Command)) |command| try std.testing.expectEqual(command, try parse(text(command)));
    try std.testing.expectError(error.UnknownCommand, parse("other"));
}
