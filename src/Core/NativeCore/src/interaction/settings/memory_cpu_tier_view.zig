//! Z2 query projection over a temporary synchronous managed-field bridge.
//! The caller may supply a published resolution or an unpublished settings
//! preview. This view neither resolves settings nor publishes/deploys parameters.
//! S-tier supplies the unique explicit initial-value/parameter contract. Remove
//! these callbacks when the source is native, without copying a settings graph.
//! Owns no settings, defaults, arrays or HTTP concepts.
pub const Scalar = enum(u32) {
    tier,
    option_count,
    ignored_count,
    runtime_present,
    writable,
    file_count,
    running_gen0_present,
    running_gen0,
    running_conserve_present,
    running_conserve,
    next_gen0_present,
    next_gen0,
    next_conserve,
    value_present,
    value_integer,
    cell_count,
    cell_present,
    cell_integer,
    minimum,
    maximum,
    unset,
    choice_count,
};
pub const Text = enum(u32) {
    name,
    unit,
    kind,
    category,
    source,
    apply,
    value_choice,
    cell_choice,
    cell_category,
    choice,
    reason,
    last_failure,
    not_installed_reason,
    native_ui_running,
    file_process,
    file_path,
    ignored_code,
    ignored_path,
};
pub const Visitor = *const fn (?*anyopaque, ?[*]const u16, usize) callconv(.c) i32;
// Family-specific synchronous borrows, not a generic reflection ABI or native
// parameter region. Text calls the managed source, which pins its original
// UTF-16 only while invoking this native visitor. No address survives the visit.
pub const Callbacks = extern struct {
    scalar: *const fn (?*anyopaque, usize, usize, Scalar, *i64) callconv(.c) i32,
    text: *const fn (?*anyopaque, usize, usize, Text, ?*anyopaque, Visitor) callconv(.c) i32,
};
pub const Error = error{ Source, Invalid, UnsupportedRuntimeOption };
pub const Source = struct {
    context: ?*anyopaque,
    callbacks: *const Callbacks,

    pub fn scalar(self: Source, index: usize, child: usize, field: Scalar) Error!i64 {
        var result: i64 = undefined;
        if (self.callbacks.scalar(self.context, index, child, field, &result) != 0) return error.Source;
        return result;
    }
    pub fn count(self: Source, index: usize, field: Scalar) Error!usize {
        const result = try self.scalar(index, 0, field);
        if (result < 0) return error.Invalid;
        return @intCast(result);
    }
    pub fn text(self: Source, index: usize, child: usize, field: Text) TextRef {
        return .{ .source = self, .index = index, .child = child, .field = field };
    }
    pub fn optional(self: Source, present: Scalar, value: Scalar) Error!?i64 {
        return if (try self.scalar(0, 0, present) != 0) try self.scalar(0, 0, value) else null;
    }
};
pub const TextRef = struct {
    source: Source,
    index: usize,
    child: usize,
    field: Text,

    pub fn visit(self: TextRef, context: ?*anyopaque, visitor: Visitor) Error!void {
        if (self.source.callbacks.text(self.source.context, self.index, self.child, self.field, context, visitor) != 0) return error.Source;
    }
    pub fn equals(self: TextRef, expected: []const u8) Error!bool {
        var comparison = Comparison{ .expected = expected };
        try self.visit(&comparison, Comparison.accept);
        return comparison.equal;
    }
};
const Comparison = struct {
    expected: []const u8,
    equal: bool = false,
    fn accept(context: ?*anyopaque, text: ?[*]const u16, length: usize) callconv(.c) i32 {
        const self: *Comparison = @ptrCast(@alignCast(context.?));
        if (text == null or length != self.expected.len) return 0;
        var index: usize = 0;
        while (index < length) : (index += 1) {
            if (text.?[index] != self.expected[index]) return 0;
        }
        self.equal = true;
        return 0;
    }
};

pub fn usePublished(has_value: bool) bool {
    return has_value;
}
// These are CLR service running and writer-reported next-start values, distinct
// from the selected option value. They are not Zig allocator settings or an
// observation of NativeUi's current GC state. Without a writer, next == running.
// Keep the GC contract until the corresponding CLR consumers actually retire.
pub const RuntimeValue = struct { running: ?i64, next: ?i64 };
pub const View = struct {
    source: Source,

    pub fn hasRuntime(self: View) Error!bool {
        return try self.source.scalar(0, 0, .runtime_present) != 0;
    }
    pub fn runtimeValue(self: View, index: usize) Error!RuntimeValue {
        const name = self.source.text(index, 0, .name);
        if (try name.equals("gcGen0MaxBudget")) {
            const running = try self.source.optional(.running_gen0_present, .running_gen0);
            return .{ .running = running, .next = if (try self.hasRuntime()) try self.source.optional(.next_gen0_present, .next_gen0) else running };
        }
        if (try name.equals("gcConserveMemory")) {
            // The original no-writer path casts AppContext's Int64 to Int32,
            // using zero only when the runtime has not supplied a value.
            const running: i64 = @as(i32, @truncate((try self.source.optional(.running_conserve_present, .running_conserve)) orelse 0));
            return .{ .running = running, .next = if (try self.hasRuntime()) try self.source.scalar(0, 0, .next_conserve) else running };
        }
        return error.UnsupportedRuntimeOption;
    }
    pub fn restart(self: View, index: usize) Error!bool {
        return self.source.text(index, 0, .apply).equals("restart");
    }
    pub fn integerRange(self: View, index: usize) Error!bool {
        return self.source.text(index, 0, .kind).equals("integer");
    }
};
