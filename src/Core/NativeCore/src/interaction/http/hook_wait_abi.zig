pub const implementation = @import("hook_wait.zig");
pub export fn rm_hook_wait_select(input: ?*const implementation.Input, response: ?*implementation.Response) callconv(.c) i32 {
    return implementation.select(input, response);
}
pub export fn rm_hook_wait_encode_stream(input: ?*const implementation.Input, context: ?*anyopaque, callbacks: ?*const implementation.Callbacks, output: ?*const implementation.OutputCallbacks, response: ?*implementation.Response) callconv(.c) i32 {
    return implementation.encodeStream(input, context, callbacks, output, response);
}
pub export fn rm_hook_wait_string(writer: ?*implementation.Writer, text: ?[*]const u16, length: usize) callconv(.c) i32 {
    return implementation.writeString(writer, text, length);
}
pub export fn rm_hook_wait_datetime(writer: ?*implementation.Writer, date: ?*const implementation.DateParts) callconv(.c) i32 {
    return implementation.writeDateTime(writer, date);
}
pub export fn rm_hook_wait_null(writer: ?*implementation.Writer) callconv(.c) i32 {
    return implementation.writeNull(writer);
}
pub export fn rm_hook_wait_bool(writer: ?*implementation.Writer, value: u32) callconv(.c) i32 {
    return implementation.writeBool(writer, value);
}
pub export fn rm_hook_wait_i64(writer: ?*implementation.Writer, value: i64) callconv(.c) i32 {
    return implementation.writeI64(writer, value);
}
pub export fn rm_hook_wait_u64(writer: ?*implementation.Writer, value: u64) callconv(.c) i32 {
    return implementation.writeU64(writer, value);
}
pub export fn rm_hook_wait_f64(writer: ?*implementation.Writer, value: f64) callconv(.c) i32 {
    return implementation.writeF64(writer, value);
}
pub export fn rm_hook_wait_action_state(writer: ?*implementation.Writer, value: i32) callconv(.c) i32 {
    return implementation.writeActionState(writer, value);
}
pub export fn rm_hook_wait_decimal(writer: ?*implementation.Writer, low: u32, middle: u32, high: u32, flags: u32) callconv(.c) i32 {
    return implementation.writeDecimal(writer, low, middle, high, flags);
}
