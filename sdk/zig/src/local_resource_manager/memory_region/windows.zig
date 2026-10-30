const std = @import("std");

pub const mem_commit: u32 = 0x1000;
const mem_reserve: u32 = 0x2000;
pub const mem_write_watch: u32 = 0x200000;
const mem_release: u32 = 0x8000;
pub const mem_free: u32 = 0x10000;
const page_readwrite: u32 = 0x04;

extern "kernel32" fn VirtualAlloc(address: ?*anyopaque, size: usize, allocation_type: u32, protect: u32) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn VirtualFree(address: *anyopaque, size: usize, free_type: u32) callconv(.winapi) i32;
extern "kernel32" fn VirtualQuery(address: *const anyopaque, information: *MemoryBasicInformation, length: usize) callconv(.winapi) usize;

pub fn allocate(page_bytes: usize) error{AllocationFailed}![*]u8 {
    return allocateWriteWatch(page_bytes, false);
}

pub fn allocateWriteWatch(page_bytes: usize, comptime enabled: bool) error{AllocationFailed}![*]u8 {
    std.debug.assert(page_bytes > 0);
    return @ptrCast(VirtualAlloc(null, page_bytes, mem_reserve | mem_commit | (if (enabled) mem_write_watch else 0), page_readwrite) orelse return error.AllocationFailed);
}

pub fn release(base: [*]u8) error{ReleaseFailed}!void {
    if (VirtualFree(base, 0, mem_release) == 0) return error.ReleaseFailed;
}

pub const MemoryBasicInformation = if (@sizeOf(usize) == 8) extern struct {
    BaseAddress: ?*anyopaque,
    AllocationBase: ?*anyopaque,
    AllocationProtect: u32,
    // Windows exposes PartitionId on 64-bit MEMORY_BASIC_INFORMATION.
    PartitionId: u16,
    RegionSize: usize,
    State: u32,
    Protect: u32,
    Type: u32,
} else extern struct {
    BaseAddress: ?*anyopaque,
    AllocationBase: ?*anyopaque,
    AllocationProtect: u32,
    RegionSize: usize,
    State: u32,
    Protect: u32,
    Type: u32,
};

/// OS diagnostics used by focused acceptance tests, never by passive use.
pub fn query(base: [*]const u8) error{ReleaseFailed}!MemoryBasicInformation {
    var information: MemoryBasicInformation = undefined;
    if (VirtualQuery(base, &information, @sizeOf(MemoryBasicInformation)) != @sizeOf(MemoryBasicInformation)) return error.ReleaseFailed;
    return information;
}
