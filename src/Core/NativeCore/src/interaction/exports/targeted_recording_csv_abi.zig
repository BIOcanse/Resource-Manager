const csv = @import("targeted_recording_csv.zig");

pub export fn rm_targeted_recording_csv_encode(context: ?*anyopaque, input: ?*const csv.Callbacks, output: ?*const csv.Output, length: ?*u64) callconv(.c) i32 {
    return csv.encode(context, input, output, length);
}
pub export fn rm_targeted_recording_csv_measure(context: ?*anyopaque, input: ?*const csv.Callbacks, length: ?*u64) callconv(.c) i32 {
    return csv.encode(context, input, null, length);
}
pub export fn rm_targeted_recording_csv_text(writer: *csv.Writer, pointer: ?[*]const u16, length: usize) callconv(.c) i32 {
    return csv.text(writer, pointer, length);
}
pub export fn rm_targeted_recording_csv_integer(writer: *csv.Writer, value: i64, sign: ?[*]const u16, length: usize) callconv(.c) i32 {
    return csv.integer(writer, value, sign, length);
}
pub export fn rm_targeted_recording_csv_unsigned(writer: *csv.Writer, value: u64) callconv(.c) i32 {
    return csv.unsigned(writer, value);
}
pub export fn rm_targeted_recording_csv_boolean(writer: *csv.Writer, value: u32) callconv(.c) i32 {
    if (value > 1) return csv.Code.invalid;
    writer.raw(if (value == 1) "true" else "false");
    return writer.failure;
}
pub export fn rm_targeted_recording_csv_number(writer: *csv.Writer, value: f64) callconv(.c) i32 {
    return csv.number(writer, value);
}
pub export fn rm_targeted_recording_csv_datetime(writer: *csv.Writer, value: *const csv.DateParts) callconv(.c) i32 {
    return csv.dateTime(writer, value);
}
pub export fn rm_targeted_recording_csv_source(writer: *csv.Writer, value: i32, sign: ?[*]const u16, length: usize) callconv(.c) i32 {
    return csv.source(writer, value, sign, length);
}
pub export fn rm_targeted_recording_csv_compare(left: ?[*]const u16, left_length: usize, right: ?[*]const u16, right_length: usize) callconv(.c) i32 {
    return csv.compareUtf16(left, left_length, right, right_length);
}
pub export fn rm_targeted_recording_csv_metric(writer: *csv.Writer, key: ?[*]const u16, length: usize, present: u32, value: f64) callconv(.c) i32 {
    return csv.metric(writer, key, length, present, value);
}
