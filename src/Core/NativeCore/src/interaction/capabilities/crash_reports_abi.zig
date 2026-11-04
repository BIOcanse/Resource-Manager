const binding = @import("crash_reports.zig");
const contract = binding.contract;

pub export fn rm_crash_capability_visit(result: ?*const contract.ViewResult, context: ?*anyopaque, sink: ?*const contract.view.Sink) callconv(.c) i32 {
    return binding.visitResult(result, context, sink);
}

pub export fn rm_crash_capability_invoke(operation: u32, target: ?[*]const u16, target_length: usize, context: ?*anyopaque, owner: ?*const contract.Owner, result: ?*contract.Result) callconv(.c) i32 {
    return binding.invoke(operation, target, target_length, context, owner, result);
}
