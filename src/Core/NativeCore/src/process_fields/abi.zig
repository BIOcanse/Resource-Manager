pub const inventory = @import("inventory.zig");
pub const image_path = @import("image_path.zig");

pub export fn rm_process_facts_inventory_visit(data: ?[*]const u8, length: usize, limits: ?*const inventory.Limits, context: ?*anyopaque, visitor: ?inventory.Visitor, count: ?*usize) callconv(.c) i32 {
    if (count) |value| value.* = 0;
    if (data == null or limits == null or visitor == null or count == null) return @backingInt(inventory.Status.invalid_argument);
    const layout = inventory.Layout.forPointerBytes(@sizeOf(usize)) orelse return @backingInt(inventory.Status.invalid_argument);
    return @backingInt(inventory.visit(data.?[0..length], limits.?.*, layout, context, visitor.?, count.?));
}

pub export fn rm_process_facts_image_path_read(handle: ?*anyopaque, buffer: ?[*]u16, capacity: usize, result: ?*image_path.Result) callconv(.c) i32 {
    if (result == null) return @backingInt(inventory.Status.invalid_argument);
    result.?.* = .{ .status = .invalid_argument, .win32_error = 0, .characters = 0 };
    if (buffer == null or capacity == 0 or capacity > @import("std").math.maxInt(u32))
        return @backingInt(inventory.Status.invalid_argument);
    result.?.* = image_path.read(handle, buffer.?[0..capacity], image_path.windows_api);
    return 0;
}
