//! Invoke transport wrapper around C18. No defaults, business registry or policy.
const pipe = @import("rm_invocation");
const capability = @import("rm_invocation_directory");
pub const Adapter = struct {
    unary: *const capability.Unary,
    pub fn bind(self: *Adapter) pipe.Binding {
        return .{ .descriptor = self.unary.descriptor, .context = self, .admit = admit, .invoke = invoke };
    }
    fn admit(context: *anyopaque, caller: pipe.Caller, _: pipe.contract.Descriptor) !pipe.contract.Status {
        const self: *Adapter = @ptrCast(@alignCast(context));
        return self.unary.admit(.{ .proof = caller.context });
    }
    fn invoke(context: *anyopaque, caller: pipe.Caller, request: pipe.contract.Request, scope: *pipe.Scope) !void {
        const self: *Adapter = @ptrCast(@alignCast(context));
        try self.unary.invokeAdmitted(.{ .proof = caller.context }, request, scope.control, .{ .context = scope, .send = send });
    }
    fn send(context: *anyopaque, result: capability.Result) !void {
        const scope: *pipe.Scope = @ptrCast(@alignCast(context));
        try scope.send(.{ .status = result.status, .payload = .{
            .context = result.payload.context,
            .write_json = result.payload.write_json,
            .release = result.payload.release,
        } });
    }
};
