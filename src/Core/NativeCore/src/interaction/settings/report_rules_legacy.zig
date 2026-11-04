//! Existing foreign-language ABI adapter. Its synchronous drain and the bounded
//! path share the same original schema traversal. No source state is copied.
const json = @import("../wire/output.zig");
const schema = @import("report_rules.zig");
pub const Callbacks = extern struct {
    count: *const fn (?*anyopaque, usize, u32, *i64) callconv(.c) i32,
    value: *const fn (?*anyopaque, usize, usize, u32, *json.Writer) callconv(.c) i32,
};
const Source = struct {
    context: ?*anyopaque,
    callbacks: *const Callbacks,
    pub fn count(self: Source, index: usize, field: u32) !i64 {
        var result: i64 = 0;
        if (self.callbacks.count(self.context, index, field, &result) != 0) return error.Source;
        return result;
    }
};
pub fn encode(context: ?*anyopaque, callbacks: ?*const Callbacks, output: ?*const json.OutputCallbacks) i32 {
    var writer = json.Writer{ .buffer = null, .capacity = 0, .context = context, .output_callbacks = output orelse return json.Code.invalid };
    const source = Source{ .context = context, .callbacks = callbacks orelse return json.Code.invalid };
    var traversal: schema.Traversal = .{};
    while (true) {
        const token = traversal.next(source) catch |err| return if (err == error.Source) json.Code.callback else json.Code.invalid;
        switch (token) {
            .end => break,
            .raw => |bytes| writer.raw(bytes),
            .value => |key| if (source.callbacks.value(context, key.index, key.child, key.field, &writer) != 0) return json.Code.callback,
        }
        if (writer.failure != 0) return writer.failure;
    }
    if (output.?.commit(context, writer.used) != 0) return json.Code.callback;
    return 0;
}
