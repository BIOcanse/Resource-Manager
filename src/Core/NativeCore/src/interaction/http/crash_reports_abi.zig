const implementation = @import("crash_reports.zig");
const view_adapter = @import("crash_report_view.zig");
const contract = @import("../contracts/crash_reports.zig");

pub export fn rm_crash_reports_encode_view_stream(result: ?*const contract.ViewResult, context: ?*anyopaque, output: ?*const implementation.OutputCallbacks, response: ?*implementation.Response) callconv(.c) i32 {
    return view_adapter.encodeStream(result, context, output, response);
}

pub const Writer = implementation.Writer;
pub const Callbacks = implementation.Callbacks;
pub const OutputCallbacks = implementation.OutputCallbacks;
pub const Response = implementation.Response;
pub const DateParts = implementation.DateParts;

pub export fn rm_crash_reports_describe(operation: u32, found: u32, response: ?*Response) callconv(.c) i32 {
    return implementation.describe(operation, found, response);
}

pub export fn rm_crash_reports_encode_stream(operation: u32, found: u32, context: ?*anyopaque, callbacks: ?*const Callbacks, output_callbacks: ?*const OutputCallbacks, response: ?*Response) callconv(.c) i32 {
    return implementation.encodeStream(operation, found, context, callbacks, output_callbacks, response);
}

pub export fn rm_crash_reports_encode(operation: u32, found: u32, context: ?*anyopaque, callbacks: ?*const Callbacks, buffer: ?[*]u8, capacity: usize, response: ?*Response) callconv(.c) i32 {
    return implementation.encode(operation, found, context, callbacks, buffer, capacity, response);
}

pub export fn rm_crash_reports_string(writer: ?*Writer, text: ?[*]const u16, length: usize) callconv(.c) i32 {
    return implementation.writeString(writer, text, length);
}

pub export fn rm_crash_reports_datetime(writer: ?*Writer, date: ?*const DateParts) callconv(.c) i32 {
    return implementation.writeDateTime(writer, date);
}
