//! One request borrows the original owner's read result. No second current.
pub const json = @import("../http/crash_reports.zig");
pub const Meta = extern struct { sequence: i64, date: json.DateParts };
pub const Token = enum(u32) { object = 0, array = 1, end = 2, property = 3, string = 4, number = 5, true_value = 6, false_value = 7, null_value = 8 };
pub const Receive = *const fn (?*anyopaque, u32, ?[*]const u16, usize) callconv(.c) i32;
pub const Text = *const fn (?*anyopaque, ?[*]const u16, usize) callconv(.c) i32;
pub const Source = extern struct {
    context: ?*anyopaque,
    read: *const fn (?*anyopaque, *usize) callconv(.c) i32,
    meta: *const fn (?*anyopaque, usize, *Meta) callconv(.c) i32,
    kind: *const fn (?*anyopaque, usize, ?*anyopaque, Text) callconv(.c) i32,
    facts: *const fn (?*anyopaque, usize, ?*anyopaque, Receive) callconv(.c) i32,
    end: *const fn (?*anyopaque) callconv(.c) void,
};
