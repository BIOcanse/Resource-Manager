//! Deterministic failure seam for lifecycle tests. All successful operations use
//! the SDK's actual system-page backend; no alternate allocator is implemented.
const memory = @import("../memory_region.zig");
pub var failures: usize = 0;
pub var attempts: usize = 0;
pub const Pages = struct {
    pub fn allocate(bytes: usize) memory.Error![*]u8 {
        return (try memory.Region.allocate(bytes)).base.?;
    }
    pub fn allocateWriteWatch(bytes: usize, comptime enabled: bool) memory.Error![*]u8 {
        return (try memory.Region.allocateWriteWatch(bytes, enabled)).base.?;
    }
    pub fn release(base: [*]u8, bytes: usize) memory.Error!void {
        attempts += 1;
        if (failures != 0) {
            failures -= 1;
            return error.ReleaseFailed;
        }
        var region = memory.Region{ .base = base, .allocation_bytes = bytes, .page_bytes = bytes };
        try region.release();
    }
};
pub const Region = memory.RegionFor(Pages);
