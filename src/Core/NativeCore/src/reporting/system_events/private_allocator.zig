//! Bounded owner contexts use ordinary private heap for small retained control
//! state. Large per-operation scratch comes exclusively from the scoped Ledger.
const host = @import("../../telemetry/kernel_etw/host_allocator.zig");
extern "kernel32" fn GetProcessHeap() callconv(.winapi) ?*anyopaque;
extern "kernel32" fn HeapAlloc(?*anyopaque, u32, usize) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn HeapReAlloc(?*anyopaque, u32, ?*anyopaque, usize) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn HeapFree(?*anyopaque, u32, ?*anyopaque) callconv(.winapi) i32;
fn allocate(_: ?*anyopaque, _: u32, size: u64, alignment: u32) callconv(.c) ?*anyopaque {
    if (alignment > 4) return null;
    return HeapAlloc(GetProcessHeap(), 0, @intCast(size));
}
fn remap(_: ?*anyopaque, _: u32, old: ?*anyopaque, _: u64, size: u64, alignment: u32) callconv(.c) ?*anyopaque {
    if (alignment > 4) return null;
    return HeapReAlloc(GetProcessHeap(), 0, old, @intCast(size));
}
fn release(_: ?*anyopaque, _: u32, pointer: ?*anyopaque, _: u64, _: u32) callconv(.c) u32 {
    return if (HeapFree(GetProcessHeap(), 0, pointer) != 0) 0 else 1;
}
pub const callbacks: host.Callbacks = .{ .page_size = 4096, .context = null, .allocate = allocate, .remap = remap, .release = release };
