//! RM-owned loopback Winsock/IOCP connection lifetime; no HTTP semantics.
//! Slots/OVERLAPPEDs must not move between init and deinit. Large slot tables and
//! IO buffers come from the owner's stable-UID Ledger workspace, leased locally
//! around the dispatch run. This module allocates no memory and stores no lease.
//! One dispatcher calls poll/submissions. requestStop alone is cross-thread.
//! Windows contracts: learn.microsoft.com/windows/win32/winsock/overlapped-i-o-and-event-objects-2
//! and /windows/win32/api/mswsock/nf-mswsock-acceptex. Even synchronous success
//! queues IOCP completion; no FILE_SKIP_COMPLETION_PORT_ON_SUCCESS is used.
const std = @import("std");
pub const io = @import("../../interaction/http/io.zig");
pub const win = @import("http_socket_win32.zig");

pub const Config = struct {
    family: io.Family,
    port: u16,
    backlog: u31,
    max_io_bytes: u31,
    socket_receive_bytes: u31,
    socket_send_bytes: u31,
    /// run() creates exactly one dispatcher, with this explicit reserved stack.
    /// poll() instead uses its caller's stack and never creates a thread.
    thread_stack_bytes: usize,
};
const State = enum { vacant, accepting, connected, closing };
const OperationKind = enum { accept, receive, send };
const Operation = struct {
    overlapped: win.Overlapped = .{},
    kind: OperationKind,
    slot: u32 = 0,
    pending: bool = false,
    buffer: win.Buffer = undefined,
    input: []u8 = &.{},
    output: []const u8 = &.{},
    done: usize = 0,
    flags: u32 = 0,
};
pub const Slot = struct {
    socket: win.Socket = win.invalid_socket,
    serial: u64 = 0,
    state: State = .vacant,
    read_eof: bool = false,
    facts: io.Facts = undefined,
    accept_op: Operation = .{ .kind = .accept },
    receive_op: Operation = .{ .kind = .receive },
    send_op: Operation = .{ .kind = .send },
    // AcceptEx needs sockaddr size + 16 bytes for EACH address; zero payload.
    addresses: [2 * (@sizeOf(win.Address6) + 16)]u8 = undefined,
    fn pending(self: *const Slot) bool {
        return self.accept_op.pending or self.receive_op.pending or self.send_op.pending;
    }
};

pub const Host = struct {
    slots: []Slot,
    config: Config,
    port: u16 = 0,
    listener: win.Socket = win.invalid_socket,
    completion_port: ?win.Handle = null,
    accept_ex: win.AcceptEx = undefined,
    stopping: bool = false,
    outstanding: usize = 0,
    started: bool = false,

    /// Caller supplies exact capacity. All policy bounds are prepared upstream;
    /// this platform module has no alternate defaults or unbounded work queue.
    pub fn init(self: *Host, slots: []Slot, config: Config) !void {
        if (slots.len == 0 or slots.len > std.math.maxInt(u32) or config.backlog == 0 or
            config.max_io_bytes == 0 or config.socket_receive_bytes == 0 or
            config.socket_send_bytes == 0 or config.thread_stack_bytes == 0)
            return error.InvalidConfig;
        self.* = .{ .slots = slots, .config = config };
        for (slots) |*slot| slot.* = .{};
        var data: win.WsaData = undefined;
        if (win.WSAStartup(0x0202, &data) != 0) return error.WinsockUnavailable;
        self.started = true;
        // Failure cleanup consumes every queued completion before releasing slots.
        errdefer self.deinit();
        self.completion_port = win.CreateIoCompletionPort(win.invalid_handle, null, 0, 1) orelse return error.CompletionPortUnavailable;
        self.listener = try self.newSocket();
        const exclusive: c_int = 1;
        if (win.setsockopt(self.listener, win.sol_socket, win.so_exclusiveaddruse, &exclusive, @sizeOf(c_int)) != 0) return error.BindFailed;
        if (config.family == .ipv4) {
            var address: win.Address4 = .{ .port = std.mem.nativeToBig(u16, config.port) };
            if (win.bind(self.listener, &address, @sizeOf(win.Address4)) != 0) return error.BindFailed;
        } else {
            var address: win.Address6 = .{ .port = std.mem.nativeToBig(u16, config.port) };
            if (win.bind(self.listener, &address, @sizeOf(win.Address6)) != 0) return error.BindFailed;
        }
        if (win.listen(self.listener, @intCast(config.backlog)) != 0) return error.ListenFailed;
        self.port = (try endpoint(self.listener, false)).port;
        if (win.CreateIoCompletionPort(@ptrFromInt(self.listener), self.completion_port, 0, 1) == null) return error.CompletionPortUnavailable;
        var count: u32 = 0;
        if (win.WSAIoctl(self.listener, 0xc8000006, &win.accept_ex_guid, @sizeOf(win.Guid), @ptrCast(&self.accept_ex), @sizeOf(win.AcceptEx), &count, null, null) != 0)
            return error.AcceptExUnavailable;
        for (slots, 0..) |*slot, index| {
            slot.accept_op.slot = @intCast(index);
            slot.receive_op.slot = @intCast(index);
            slot.send_op.slot = @intCast(index);
            try self.armAccept(slot);
        }
    }

    fn newSocket(self: *Host) !win.Socket {
        // WSA_FLAG_OVERLAPPED | WSA_FLAG_NO_HANDLE_INHERIT.
        const socket = win.WSASocketW(if (self.config.family == .ipv4) win.af_inet else win.af_inet6, 1, 6, null, 0, 0x81);
        if (socket == win.invalid_socket) return error.SocketUnavailable;
        errdefer _ = win.closesocket(socket);
        const receive_bytes: c_int = @intCast(self.config.socket_receive_bytes);
        const send_bytes: c_int = @intCast(self.config.socket_send_bytes);
        if (win.setsockopt(socket, win.sol_socket, win.so_rcvbuf, &receive_bytes, @sizeOf(c_int)) != 0 or
            win.setsockopt(socket, win.sol_socket, win.so_sndbuf, &send_bytes, @sizeOf(c_int)) != 0) return error.SocketConfigFailed;
        return socket;
    }

    fn armAccept(self: *Host, slot: *Slot) !void {
        std.debug.assert(!slot.pending() and slot.socket == win.invalid_socket);
        if (slot.serial == std.math.maxInt(u64)) return error.ConnectionIdExhausted;
        slot.serial += 1;
        slot.read_eof = false;
        slot.socket = try self.newSocket();
        errdefer {
            _ = win.closesocket(slot.socket);
            slot.socket = win.invalid_socket;
            slot.state = .vacant;
        }
        slot.accept_op.overlapped = .{};
        var received: u32 = 0;
        const ok = self.accept_ex(self.listener, slot.socket, &slot.addresses, 0, @sizeOf(win.Address6) + 16, @sizeOf(win.Address6) + 16, &received, &slot.accept_op.overlapped);
        if (ok == 0 and win.WSAGetLastError() != win.pending) return error.AcceptFailed;
        slot.state = .accepting;
        slot.accept_op.pending = true;
        self.outstanding += 1;
    }

    fn lookup(self: *Host, id: io.ConnectionId) !*Slot {
        if (id.slot >= self.slots.len) return error.StaleConnection;
        const slot = &self.slots[id.slot];
        if (slot.serial != id.serial or slot.state != .connected or self.stopping) return error.StaleConnection;
        return slot;
    }
    pub fn facts(self: *Host, id: io.ConnectionId) !io.Facts {
        const slot = try self.lookup(id);
        // Query the ACTUAL socket again, never a saved PID or a preflight socket.
        return .{ .local = try endpoint(slot.socket, false), .remote = try endpoint(slot.socket, true) };
    }

    pub fn receive(self: *Host, id: io.ConnectionId, buffer: []u8) !void {
        const slot = try self.lookup(id);
        if (slot.receive_op.pending) return error.ReceiveBusy;
        if (slot.read_eof) return error.ReceiveEnded;
        if (buffer.len == 0) return error.EmptyBuffer;
        const op = &slot.receive_op;
        op.overlapped = .{};
        op.input = buffer;
        op.flags = 0;
        op.buffer = .{ .len = @intCast(@min(buffer.len, self.config.max_io_bytes)), .ptr = buffer.ptr };
        if (win.WSARecv(slot.socket, &op.buffer, 1, null, &op.flags, &op.overlapped, null) != 0 and win.WSAGetLastError() != win.pending) {
            op.input = &.{};
            return error.ReceiveFailed;
        }
        op.pending = true;
        self.outstanding += 1;
    }

    pub fn send(self: *Host, id: io.ConnectionId, bytes: []const u8) !void {
        const slot = try self.lookup(id);
        if (slot.send_op.pending) return error.SendBusy;
        if (bytes.len == 0) return error.EmptyBuffer;
        const op = &slot.send_op;
        op.output = bytes;
        op.done = 0;
        self.submitSend(slot) catch |err| {
            op.output = &.{};
            return err;
        };
    }
    fn submitSend(self: *Host, slot: *Slot) !void {
        const op = &slot.send_op;
        op.overlapped = .{};
        op.buffer = .{ .len = @intCast(@min(op.output.len - op.done, self.config.max_io_bytes)), .ptr = @constCast(op.output[op.done..].ptr) };
        if (win.WSASend(slot.socket, &op.buffer, 1, null, 0, &op.overlapped, null) != 0 and win.WSAGetLastError() != win.pending) return error.SendFailed;
        op.pending = true;
        self.outstanding += 1;
    }

    /// Before any credential/output bytes, a caller can use the same two-ended
    /// proof owner as the desktop client. A rejected/replaced peer gets zero IO.
    /// No proof result is cached; the verifier owns process/token revalidation.
    pub fn sendVerified(self: *Host, id: io.ConnectionId, bytes: []const u8, context: ?*anyopaque, verify: *const fn (?*anyopaque, io.Facts) bool) !void {
        if (!verify(context, try self.facts(id))) return error.UntrustedPeer;
        try self.send(id, bytes);
    }

    fn closeSlot(_: *Host, slot: *Slot) void {
        slot.state = .closing;
        if (slot.socket != win.invalid_socket) {
            // Cancellation is NOT completion. Keep every operation/buffer alive.
            _ = win.CancelIoEx(@ptrFromInt(slot.socket), null);
            _ = win.closesocket(slot.socket);
            slot.socket = win.invalid_socket;
        }
    }
    pub fn close(self: *Host, id: io.ConnectionId) !void {
        self.closeSlot(try self.lookup(id));
    }

    pub fn transport(self: *Host) io.Transport {
        return .{ .context = self, .receive_fn = receiveBridge, .send_fn = sendBridge, .close_fn = closeBridge };
    }
    fn receiveBridge(context: *anyopaque, id: io.ConnectionId, buffer: []u8) !void {
        return (@as(*Host, @ptrCast(@alignCast(context)))).receive(id, buffer);
    }
    fn sendBridge(context: *anyopaque, id: io.ConnectionId, bytes: []const u8) !void {
        return (@as(*Host, @ptrCast(@alignCast(context)))).send(id, bytes);
    }
    fn closeBridge(context: *anyopaque, id: io.ConnectionId) !void {
        return (@as(*Host, @ptrCast(@alignCast(context)))).close(id);
    }

    /// Thread-safe while the host is alive. A null OVERLAPPED is the stop packet;
    /// no other code may destroy the host until run()/drain completes.
    pub fn requestStop(self: *Host) !void {
        if (win.PostQueuedCompletionStatus(self.completion_port orelse return error.HostClosed, 0, 0, null) == 0) return error.StopPostFailed;
    }
    pub fn beginStop(self: *Host) void {
        if (self.stopping) return;
        self.stopping = true;
        if (self.listener != win.invalid_socket) {
            _ = win.CancelIoEx(@ptrFromInt(self.listener), null);
            _ = win.closesocket(self.listener);
            self.listener = win.invalid_socket;
        }
        for (self.slots) |*slot| if (slot.state != .vacant) self.closeSlot(slot);
    }

    /// null means timeout. Every posted IO produces a single terminal event,
    /// except AcceptEx failure which is a closed event for the unexposed slot.
    /// Internal partial sends do NOT release bytes or produce multiple events.
    pub fn poll(self: *Host, wait_ms: u32) !?io.Completion {
        while (true) {
            for (self.slots, 0..) |*slot, index| {
                if (slot.state == .closing and !slot.pending()) {
                    const result: io.Completion = .{ .kind = .closed, .connection = .{ .slot = @intCast(index), .serial = slot.serial }, .outcome = if (self.stopping) .cancelled else .ok };
                    slot.state = .vacant;
                    // Re-arm on NEXT poll: closed must be consumed before reuse.
                    return result;
                }
                if (slot.state == .vacant and !self.stopping) try self.armAccept(slot);
            }
            if (self.stopping and self.outstanding == 0) return .{ .kind = .stopped, .connection = .{ .slot = 0, .serial = 0 } };
            var transferred: u32 = 0;
            var key: usize = 0;
            var overlapped: ?*win.Overlapped = null;
            const success = win.GetQueuedCompletionStatus(self.completion_port orelse return error.HostClosed, &transferred, &key, &overlapped, wait_ms);
            const failure = if (success == 0) win.GetLastError() else 0;
            const ov = overlapped orelse {
                if (success != 0) {
                    self.beginStop();
                    continue;
                }
                if (failure == win.timeout) return null;
                return error.CompletionWaitFailed;
            };
            const op: *Operation = @fieldParentPtr("overlapped", ov);
            std.debug.assert(op.pending and self.outstanding != 0);
            op.pending = false;
            self.outstanding -= 1;
            const slot = &self.slots[op.slot];
            const id: io.ConnectionId = .{ .slot = op.slot, .serial = slot.serial };
            const cancelled = slot.state == .closing;
            const outcome: io.Outcome = if (cancelled or failure == win.aborted) .cancelled else if (failure != 0) .failed else .ok;
            switch (op.kind) {
                .accept => {
                    if (outcome != .ok) {
                        self.closeSlot(slot);
                        continue;
                    }
                    if (win.setsockopt(slot.socket, win.sol_socket, win.so_update_accept_context, &self.listener, @sizeOf(win.Socket)) != 0 or
                        win.CreateIoCompletionPort(@ptrFromInt(slot.socket), self.completion_port, 0, 1) == null)
                    {
                        self.closeSlot(slot);
                        continue;
                    }
                    slot.facts = .{ .local = endpoint(slot.socket, false) catch {
                        self.closeSlot(slot);
                        continue;
                    }, .remote = endpoint(slot.socket, true) catch {
                        self.closeSlot(slot);
                        continue;
                    } };
                    if (!loopback(slot.facts.local) or !loopback(slot.facts.remote)) {
                        self.closeSlot(slot);
                        continue;
                    }
                    slot.state = .connected;
                    return .{ .kind = .accepted, .connection = id };
                },
                .receive => {
                    std.debug.assert(transferred <= op.buffer.len);
                    const data = op.input[0..transferred];
                    op.input = &.{};
                    if (outcome == .failed) self.closeSlot(slot);
                    if (outcome == .ok and transferred == 0) slot.read_eof = true;
                    return .{ .kind = .received, .connection = id, .outcome = if (outcome == .ok and transferred == 0) .eof else outcome, .transferred = transferred, .data = data, .os_error = failure };
                },
                .send => {
                    std.debug.assert(transferred <= op.buffer.len);
                    op.done += transferred;
                    var result: io.Completion = .{ .kind = .sent, .connection = id, .outcome = outcome, .transferred = op.done, .os_error = failure };
                    if (outcome == .ok and transferred > 0 and op.done < op.output.len) {
                        if (self.submitSend(slot)) |_| {
                            continue;
                        } else |_| {
                            result.outcome = .failed;
                            result.os_error = @intCast(win.WSAGetLastError());
                        }
                    } else if (outcome == .ok and transferred == 0) result.outcome = .failed;
                    op.output = &.{};
                    if (result.outcome == .failed) self.closeSlot(slot);
                    return result;
                },
            }
        }
    }

    /// Caller must dispatch cancelled events if it needs to release IO leases;
    /// deinit is only a final safety drain, not a replacement for that dispatch.
    pub fn deinit(self: *Host) void {
        if (!self.started) return;
        self.beginStop();
        if (self.completion_port != null) {
            while (self.outstanding != 0) _ = self.poll(win.infinite) catch unreachable;
            _ = win.CloseHandle(self.completion_port.?);
            self.completion_port = null;
        }
        _ = win.WSACleanup();
        self.started = false;
    }

    /// Scoped one-thread driver. Callback runs on that thread, must not block;
    /// it posts further IO or requests stop. Join proves no thread/stack remains.
    pub fn run(self: *Host, context: ?*anyopaque, dispatch: *const fn (?*anyopaque, *Host, io.Completion) void) !void {
        var driver: Driver = .{ .host = self, .context = context, .dispatch = dispatch };
        const thread = win.CreateThread(null, self.config.thread_stack_bytes, Driver.entry, &driver, 0x10000, null) orelse return error.ThreadUnavailable;
        defer _ = win.CloseHandle(thread);
        if (win.WaitForSingleObject(thread, win.infinite) != 0) unreachable;
        if (driver.failed) return error.CompletionWaitFailed;
    }
};

const Driver = struct {
    host: *Host,
    context: ?*anyopaque,
    dispatch: *const fn (?*anyopaque, *Host, io.Completion) void,
    failed: bool = false,
    fn entry(context: ?*anyopaque) callconv(.winapi) u32 {
        const self: *Driver = @ptrCast(@alignCast(context.?));
        while (true) {
            const completion = self.host.poll(win.infinite) catch {
                self.failed = true;
                self.host.beginStop();
                // A live IOCP cannot be discarded with pending OVERLAPPEDs.
                continue;
            } orelse continue;
            self.dispatch(self.context, self.host, completion);
            if (completion.kind == .stopped) return 0;
        }
    }
};

pub fn endpoint(socket: win.Socket, remote: bool) !io.Endpoint {
    var storage: [128]u8 align(8) = undefined;
    var length: c_int = storage.len;
    const result = if (remote) win.getpeername(socket, &storage, &length) else win.getsockname(socket, &storage, &length);
    if (result != 0 or length < @sizeOf(u16)) return error.EndpointUnavailable;
    const family = std.mem.readInt(u16, storage[0..2], .little);
    var value: io.Endpoint = .{ .family = undefined, .address = @splat(0), .scope = 0, .port = undefined };
    if (family == win.af_inet and length == @sizeOf(win.Address4)) {
        value.family = .ipv4;
        @memcpy(value.address[0..4], storage[4..8]);
    } else if (family == win.af_inet6 and length == @sizeOf(win.Address6)) {
        value.family = .ipv6;
        @memcpy(&value.address, storage[8..24]);
        value.scope = std.mem.readInt(u32, storage[24..28], .little);
    } else return error.EndpointUnavailable;
    value.port = std.mem.readInt(u16, storage[2..4], .big);
    return value;
}
fn loopback(value: io.Endpoint) bool {
    if (value.family == .ipv4) return value.address[0] == 127;
    const local: win.Address6 = .{ .port = 0 };
    return std.mem.eql(u8, &value.address, &local.address);
}
