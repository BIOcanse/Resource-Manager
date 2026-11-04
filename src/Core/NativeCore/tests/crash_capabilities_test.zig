const std = @import("std");
const capability = @import("crash_capabilities");
const c = capability.contract;
const expect = std.testing.expect;
const equal = std.testing.expectEqual;

const Owner = struct {
    calls: [4]usize = @splat(0),
    missing: bool = false,
    fail: bool = false,
    target: ?[*]const u16 = null,
    length: usize = 0,

    fn from(context: ?*anyopaque) *Owner {
        return @ptrCast(@alignCast(context.?));
    }
    fn call(self: *Owner, operation: c.Operation, target: ?[*]const u16, length: usize, value: *?*anyopaque) i32 {
        self.calls[@backingInt(operation)] += 1;
        self.target = target;
        self.length = length;
        // Deliberately set a value on failure: it must never escape the call.
        value.* = if (self.missing) null else self;
        return if (self.fail) 71 else 0;
    }
    fn read(context: ?*anyopaque, value: *?*anyopaque) callconv(.c) i32 {
        return from(context).call(.read, null, 0, value);
    }
    fn package(context: ?*anyopaque, target: ?[*]const u16, length: usize, value: *?*anyopaque) callconv(.c) i32 {
        return from(context).call(.package, target, length, value);
    }
    fn acknowledge(context: ?*anyopaque, target: ?[*]const u16, length: usize, value: *?*anyopaque) callconv(.c) i32 {
        return from(context).call(.acknowledge, target, length, value);
    }
    fn delete(context: ?*anyopaque, target: ?[*]const u16, length: usize, deleted: *u32) callconv(.c) i32 {
        const self = from(context);
        var ignored: ?*anyopaque = null;
        const code = self.call(.delete, target, length, &ignored);
        deleted.* = if (self.missing) 0 else 1;
        return code;
    }
    const callbacks: c.Owner = .{ .read = read, .package = package, .acknowledge = acknowledge, .delete = delete };
};

test "all four bindings preserve target references, one call and original null/false semantics" {
    const target = [_]u16{ 'r', 0, 0xd800, 0x4e2d, 0xdcff };
    var operation: u32 = 0;
    while (operation <= @backingInt(c.Operation.delete)) : (operation += 1) {
        var missing: usize = 0;
        while (missing < 2) : (missing += 1) {
            var owner: Owner = .{ .missing = missing != 0 };
            var result: c.Result = undefined;
            try equal(c.Code.success, capability.invoke(operation, &target, target.len, &owner, &Owner.callbacks, &result));
            try equal(operation, @backingInt(result.operation));
            try equal(if (operation == 0 or missing == 0) c.Outcome.success else c.Outcome.not_found, result.outcome);
            var i: usize = 0;
            while (i < owner.calls.len) : (i += 1) try equal(@as(usize, if (i == operation) 1 else 0), owner.calls[i]);
            if (operation != 0) {
                try expect(owner.target.? == &target);
                try equal(target.len, owner.length);
            }
            try equal(if (operation == @backingInt(c.Operation.delete) or missing != 0) null else @as(?*anyopaque, &owner), result.value);
        }
    }
}

test "every owner exception is a callback failure with no borrowed result or repeat" {
    var operation: u32 = 0;
    while (operation <= @backingInt(c.Operation.delete)) : (operation += 1) {
        var owner: Owner = .{ .fail = true };
        var result: c.Result = .{ .operation = .read, .outcome = .success, .value = &owner };
        try equal(c.Code.callback, capability.invoke(operation, null, 0, &owner, &Owner.callbacks, &result));
        try equal(@as(?*anyopaque, null), result.value);
        try equal(@as(usize, 1), owner.calls[operation]);
    }
}

test "invalid ABI calls refuse before side effects and empty targets stay owner inputs" {
    var owner: Owner = .{};
    var result: c.Result = undefined;
    try equal(c.Code.invalid, capability.invoke(99, null, 0, &owner, &Owner.callbacks, &result));
    try equal(c.Code.invalid, capability.invoke(1, null, 1, &owner, &Owner.callbacks, &result));
    try equal(c.Code.invalid, capability.invoke(1, null, 0, &owner, null, &result));
    try equal(c.Code.invalid, capability.invoke(1, null, 0, &owner, &Owner.callbacks, null));
    try equal([_]usize{ 0, 0, 0, 0 }, owner.calls);
    try equal(c.Code.success, capability.invoke(1, null, 0, &owner, &Owner.callbacks, &result));
    try equal(@as(usize, 1), owner.calls[1]);
    try equal(@as(usize, 0), owner.length);
}

test "public IDs are protocol neutral and distinct" {
    try std.testing.expectEqualStrings("crash-reports.read", c.id(.read));
    try std.testing.expectEqualStrings("crash-reports.package", c.id(.package));
    try std.testing.expectEqualStrings("crash-reports.acknowledge", c.id(.acknowledge));
    try std.testing.expectEqualStrings("crash-reports.delete", c.id(.delete));
}
