//! H-io/H-parse/H-send seam. No sockets, parser, endpoint or allocation policy.
//! A single dispatcher owns submissions and completions. One receive and one send
//! may coexist per connection. Successful submission borrows the entire buffer
//! until its ONE terminal completion, including cancellation. Close is a request;
//! only `closed` permits slot reuse. Never retain a source/codec borrow for send:
//! submit final wire bytes owned by the sending run scope instead.
pub const ConnectionId = struct { instance: u64, slot: u32, serial: u64 };
pub const Family = enum { ipv4, ipv6 };
pub const Endpoint = struct {
    family: Family,
    address: [16]u8,
    scope: u32,
    port: u16,
};
pub const Facts = struct { local: Endpoint, remote: Endpoint };
pub const Kind = enum { accepted, received, sent, ready, closing, closed, stopped };
/// Opaque bounded callback registration. Never a source pointer or request buffer.
pub const WakeToken = struct { instance: u64, slot: u32, serial: u64 };
pub const Outcome = enum { ok, eof, cancelled, failed };
pub const Completion = struct {
    kind: Kind,
    connection: ConnectionId,
    outcome: Outcome = .ok,
    /// Receive: this operation's bytes. Send: total bytes, partial IO included.
    transferred: usize = 0,
    os_error: u32 = 0,
    /// Only ready uses this value; check against the request's current ticket.
    request_ticket: u64 = 0,
    /// A slice of the caller's receive buffer, never an internal/copy buffer.
    data: []const u8 = &.{},
};

/// H-parse consumes received.data, pauses without another receive when full,
/// and retains unconsumed input in its own bounded run-scope workspace.
/// H-send submits ONE final slice, produces no more while it is in flight,
/// and releases its local workspace lease on sent (success/failure/cancel).
/// Immediate submission errors retain nothing and owe no completion.
/// A receive EOF is a half close: pending output can still finish. Errors close
/// the connection, but each pending operation still gets a terminal completion.
pub const Transport = struct {
    context: *anyopaque,
    receive_fn: *const fn (*anyopaque, ConnectionId, []u8) anyerror!void,
    send_fn: *const fn (*anyopaque, ConnectionId, []const u8) anyerror!void,
    close_fn: *const fn (*anyopaque, ConnectionId) anyerror!void,

    pub fn receive(self: Transport, id: ConnectionId, buffer: []u8) !void {
        return self.receive_fn(self.context, id, buffer);
    }
    pub fn send(self: Transport, id: ConnectionId, bytes: []const u8) !void {
        return self.send_fn(self.context, id, bytes);
    }
    pub fn close(self: Transport, id: ConnectionId) !void {
        return self.close_fn(self.context, id);
    }
};

// closing tells the dispatcher to cancel/join its original sources. Retire each
// WakeToken after join; only closed proves physical IO plus source/ready drain.
// Keep connection/request state and output borrows alive until that point.
// Transport never owns or closes a domain's workspace or subscription.
