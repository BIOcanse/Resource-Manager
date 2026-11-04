//! C19 -> accepted C20 wake binding. Run-local, not an owner of domain state.
//! Keep stable until original callbacks join; source posting errors propagate.
const std = @import("std");
const socket = @import("http_socket");
const host = @import("http_host");
const io = socket.io;

pub const Ready = struct {
    socket: *socket.Host,
    connection: io.ConnectionId,
    ticket: u64,
    token: io.WakeToken,
    retired: bool = false,

    pub fn init(transport: *socket.Host, connection: io.ConnectionId) !Ready {
        const ticket = try transport.beginRequest(connection);
        const token = try transport.registerWake(connection, ticket);
        return .{ .socket = transport, .connection = connection, .ticket = ticket, .token = token };
    }
    pub fn wake(self: *Ready) host.Wake {
        return .{ .context = self, .post = post };
    }
    fn post(context: *anyopaque) !void {
        const self: *Ready = @ptrCast(@alignCast(context));
        if (!try self.socket.postReady(self.token)) return error.StaleWake;
    }
    pub fn matches(self: *const Ready, completion: io.Completion) bool {
        return !self.retired and completion.kind == .ready and
            std.meta.eql(completion.connection, self.connection) and completion.request_ticket == self.ticket;
    }
    /// Operation.release is the actual source unregister/join boundary. C20's
    /// closed additionally waits for already queued ready packets and physical IO.
    pub fn retire(self: *Ready, operation: *const host.Operation) !void {
        if (operation.phase != .released) return error.SourceNotJoined;
        if (self.retired) return;
        try self.socket.retireWake(self.token);
        self.retired = true;
    }
};
