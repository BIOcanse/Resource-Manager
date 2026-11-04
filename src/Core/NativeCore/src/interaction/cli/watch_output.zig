//! Per-delivery framing only. CLI1 supplies its one text/JSON formatter, already
//! configured with the explicitly selected format and compact stream rendering.
const std = @import("std");

/// `formatter.writeValue(writer, delivery)` borrows the original typed delivery
/// synchronously and emits one value without a trailing newline. JSON strings
/// must be escaped by that common formatter, never split into multiple records.
/// Flush before returning so a caller can release the delivery's sending loan.
/// No output queue, retained current value, payload copy or logging lives here.
pub fn emit(writer: *std.Io.Writer, formatter: anytype, delivery: anytype) !void {
    try formatter.writeValue(writer, delivery);
    try writer.writeByte('\n');
    try writer.flush();
}
