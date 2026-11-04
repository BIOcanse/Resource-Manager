//! Read-only crash-domain fields. A Source borrows one original result for a
//! synchronous call. Text is lent only inside its receiver; no pointer survives
//! that callback. No HTTP, settings, report storage or allocation lives here.
pub const Shape = enum(u32) { list = 0, package = 1, acknowledge = 2, delete = 3 };
pub const Scalar = enum(u32) {
    report_count = 0,
    dump_present = 1,
    dump_size = 2,
    fault_present = 3,
    package_present = 4,
    package_size = 5,
    acknowledged = 6,
    result_package_size = 7,
    /// Saved, then deleted by Windows' per-program rotation: status missing with the old dump kept.
    dump_removed = 8,
};
pub const Text = enum(u32) {
    dump_folder = 0,
    id = 1,
    program = 3,
    role = 4,
    version = 5,
    dump_status = 6,
    dump_path = 7,
    fault_module = 8,
    fault_offset = 9,
    fault_exception_code = 10,
    fault_managed_stack = 11,
    fault_description = 12,
    package_path = 13,
    result_package_path = 15,
};
pub const Date = enum(u32) { occurred_at = 0, package_created_at = 1, result_package_created_at = 2 };
pub const DateParts = extern struct { year: i32, month: i32, day: i32, hour: i32, minute: i32, second: i32, fraction: i32, offset_minutes: i32 };
pub const Code = struct {
    pub const success: i32 = 0;
    pub const invalid: i32 = 1;
    pub const callback: i32 = 2;
};
pub const TextReceiver = *const fn (?*anyopaque, ?[*]const u16, usize) callconv(.c) i32;
pub const Accessors = extern struct {
    scalar: *const fn (?*anyopaque, usize, u32, *i64) callconv(.c) i32,
    text: *const fn (?*anyopaque, usize, u32, ?*anyopaque, TextReceiver) callconv(.c) i32,
    date: *const fn (?*anyopaque, usize, u32, *DateParts) callconv(.c) i32,
};
pub const Source = extern struct {
    context: ?*anyopaque,
    accessors: ?*const Accessors,

    pub fn scalar(self: Source, index: usize, field: Scalar, output: *i64) i32 {
        const accessors = self.accessors orelse return Code.invalid;
        return if (accessors.scalar(self.context, index, @backingInt(field), output) == 0) Code.success else Code.callback;
    }
    pub fn text(self: Source, index: usize, field: Text, context: ?*anyopaque, receiver: TextReceiver) i32 {
        const accessors = self.accessors orelse return Code.invalid;
        return if (accessors.text(self.context, index, @backingInt(field), context, receiver) == 0) Code.success else Code.callback;
    }
    pub fn date(self: Source, index: usize, field: Date, output: *DateParts) i32 {
        const accessors = self.accessors orelse return Code.invalid;
        return if (accessors.date(self.context, index, @backingInt(field), output) == 0) Code.success else Code.callback;
    }
};
/// A non-HTTP consumer of the same fields. Scalar presence precedes optional
/// members; the caller already knows Shape. No report array is materialized.
pub const Sink = extern struct {
    scalar: *const fn (?*anyopaque, usize, u32, i64) callconv(.c) i32,
    text: *const fn (?*anyopaque, usize, u32, ?[*]const u16, usize) callconv(.c) i32,
    date: *const fn (?*anyopaque, usize, u32, *const DateParts) callconv(.c) i32,
};
const VisitError = error{ Invalid, Callback };
const Visitor = struct {
    source: Source,
    sink: *const Sink,
    context: ?*anyopaque,

    fn number(self: Visitor, index: usize, field: Scalar) VisitError!i64 {
        var value: i64 = 0;
        const code = self.source.scalar(index, field, &value);
        if (code == Code.invalid) return error.Invalid;
        if (code != Code.success or self.sink.scalar(self.context, index, @backingInt(field), value) != 0) return error.Callback;
        return value;
    }
    fn flag(self: Visitor, index: usize, field: Scalar) VisitError!bool {
        return switch (try self.number(index, field)) {
            0 => false,
            1 => true,
            else => error.Invalid,
        };
    }
    fn text(self: Visitor, index: usize, field: Text) VisitError!void {
        var relay: TextRelay = .{ .visitor = self, .index = index, .field = field };
        const code = self.source.text(index, field, &relay, TextRelay.receive);
        if (code == Code.invalid) return error.Invalid;
        if (code != Code.success or relay.calls != 1) return error.Callback;
    }
    fn date(self: Visitor, index: usize, field: Date) VisitError!void {
        var value: DateParts = undefined;
        const code = self.source.date(index, field, &value);
        if (code == Code.invalid) return error.Invalid;
        if (code != Code.success or self.sink.date(self.context, index, @backingInt(field), &value) != 0) return error.Callback;
    }
    fn package(self: Visitor, index: usize, result: bool) VisitError!void {
        try self.text(index, if (result) .result_package_path else .package_path);
        _ = try self.number(index, if (result) .result_package_size else .package_size);
        try self.date(index, if (result) .result_package_created_at else .package_created_at);
    }
    fn report(self: Visitor, index: usize) VisitError!void {
        try self.text(index, .id);
        try self.date(index, .occurred_at);
        try self.text(index, .program);
        try self.text(index, .role);
        try self.text(index, .version);
        try self.text(index, .dump_status);
        if (try self.flag(index, .dump_present)) {
            try self.text(index, .dump_path);
            _ = try self.number(index, .dump_size);
        }
        _ = try self.flag(index, .dump_removed);
        if (try self.flag(index, .fault_present)) {
            try self.text(index, .fault_module);
            try self.text(index, .fault_offset);
            try self.text(index, .fault_exception_code);
            try self.text(index, .fault_managed_stack);
            try self.text(index, .fault_description);
        }
        if (try self.flag(index, .package_present)) try self.package(index, false);
        _ = try self.flag(index, .acknowledged);
    }
    fn run(self: Visitor, shape: Shape) VisitError!void {
        switch (shape) {
            .list => {
                const count = try self.number(0, .report_count);
                if (count < 0) return error.Invalid;
                try self.text(0, .dump_folder);
                var index: usize = 0;
                while (index < @as(u64, @intCast(count))) : (index += 1) try self.report(index);
            },
            .package => {
                try self.report(0);
                try self.package(0, true);
            },
            .acknowledge => try self.report(0),
            .delete => {},
        }
    }
};
const TextRelay = struct {
    visitor: Visitor,
    index: usize,
    field: Text,
    calls: usize = 0,
    fn receive(context: ?*anyopaque, pointer: ?[*]const u16, length: usize) callconv(.c) i32 {
        const self: *TextRelay = @ptrCast(@alignCast(context.?));
        self.calls += 1;
        if (self.calls != 1 or (pointer == null and length != 0)) return Code.invalid;
        return self.visitor.sink.text(self.visitor.context, self.index, @backingInt(self.field), pointer, length);
    }
};
pub fn visit(shape: u32, source: Source, context: ?*anyopaque, sink: ?*const Sink) i32 {
    if (shape > @backingInt(Shape.delete)) return Code.invalid;
    const visitor: Visitor = .{ .source = source, .context = context, .sink = sink orelse return Code.invalid };
    visitor.run(@fromBackingInt(@intCast(shape))) catch |err| return switch (err) {
        error.Invalid => Code.invalid,
        error.Callback => Code.callback,
    };
    return Code.success;
}
