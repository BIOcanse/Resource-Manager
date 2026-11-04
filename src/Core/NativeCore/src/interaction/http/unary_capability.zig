//! Finite synchronous HTTP projection of C18. The existing family supplies its
//! final output writer and typed status mapping. This is not a listener/family
//! cutover or a continuation codec for unbounded sources.
const std = @import("std");
const capability = @import("rm_invocation_directory");
const c = capability.contract;
pub const Outcome = struct { admission: c.Status, result: ?c.Status };
pub fn encode(binding: *const capability.Unary, caller: capability.Caller, request: c.Request, control: c.Control, writer: *std.Io.Writer) !Outcome {
    var delivery: Delivery = .{ .writer = writer };
    const admission = try binding.invoke(caller, request, control, .{ .context = &delivery, .send = Delivery.send });
    return .{ .admission = admission, .result = delivery.status };
}
const Delivery = struct {
    writer: *std.Io.Writer,
    status: ?c.Status = null,
    fn send(context: *anyopaque, result: capability.Result) !void {
        const self: *Delivery = @ptrCast(@alignCast(context));
        defer result.payload.release(result.payload.context);
        self.status = result.status;
        try result.payload.write_json(result.payload.context, self.writer);
    }
};
