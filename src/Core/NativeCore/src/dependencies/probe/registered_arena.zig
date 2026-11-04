//! Allocation failure reports the exact next extent, so the host can grow the same registered lease.
const std = @import("std");
const A = std.mem.Allocator;
const F = std.heap.FixedBufferAllocator;
pub const Arena = struct {
    fixed: F,
    required: usize = 0,
    pub fn init(buffer: []u8) Arena {
        return .{ .fixed = F.init(buffer) };
    }
    pub fn allocator(self: *Arena) A {
        return .{ .ptr = self, .vtable = &.{ .alloc = alloc, .resize = resize, .remap = remap, .free = free } };
    }
    fn alloc(context: *anyopaque, size: usize, alignment: std.mem.Alignment, ra: usize) ?[*]u8 {
        const self: *Arena = @ptrCast(@alignCast(context));
        const result = F.alloc(&self.fixed, size, alignment, ra);
        if (result == null) {
            const offset = std.mem.alignPointerOffset(self.fixed.buffer.ptr + self.fixed.end_index, alignment.toByteUnits()) orelse return null;
            const aligned = std.math.add(usize, self.fixed.end_index, offset) catch std.math.maxInt(usize);
            const end = std.math.add(usize, aligned, size) catch std.math.maxInt(usize);
            self.required = @max(self.required, end);
        }
        return result;
    }
    fn resize(context: *anyopaque, memory: []u8, alignment: std.mem.Alignment, size: usize, ra: usize) bool {
        const self: *Arena = @ptrCast(@alignCast(context));
        const result = F.resize(&self.fixed, memory, alignment, size, ra);
        if (!result and self.fixed.isLastAllocation(memory)) {
            const offset = @intFromPtr(memory.ptr) - @intFromPtr(self.fixed.buffer.ptr);
            self.required = @max(self.required, std.math.add(usize, offset, size) catch std.math.maxInt(usize));
        }
        return result;
    }
    fn remap(context: *anyopaque, memory: []u8, alignment: std.mem.Alignment, size: usize, ra: usize) ?[*]u8 {
        return if (resize(context, memory, alignment, size, ra)) memory.ptr else null;
    }
    fn free(context: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ra: usize) void {
        const self: *Arena = @ptrCast(@alignCast(context));
        F.free(&self.fixed, memory, alignment, ra);
    }
};
