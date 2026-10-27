const std = @import("std");
const Alignment = std.mem.Alignment;
const none = std.math.maxInt(usize);
const Header = struct {
    span: usize,
    previous_span: usize,
    requested: usize = 0,
    payload_offset: usize = 0,
    next_free: usize = none,
    previous_free: usize = none,
    used: bool = false,
};
const block_alignment = @alignOf(Header);
const anchor_size = @sizeOf(usize);
const minimum_block = std.mem.alignForward(usize, @sizeOf(Header) + anchor_size + 1, block_alignment);

/// Bounded free-list allocator over a caller-owned (for example ledger-leased)
/// span. No system allocator, fallback, external metadata, or recursive walk.
/// The caller keeps this object and its span stable and serializes access.
pub const RegionAllocator = struct {
    memory: []u8,
    /// Entire supplied region, including alignment loss: the actual reserved
    /// backing footprint. This does not decrease when individual blocks free.
    region_bytes: usize,
    capacity: usize,
    /// Occupied physical spans include block metadata, padding and slack.
    current: usize = 0,
    peak: usize = 0,
    requested_current: usize = 0,
    requested_peak: usize = 0,
    live_allocations: usize = 0,
    failures: u64 = 0,
    free_head: usize = 0,

    pub fn init(span: []u8) !RegionAllocator {
        const address = @intFromPtr(span.ptr);
        const aligned = alignChecked(address, block_alignment) orelse return error.InvalidRegion;
        const prefix = aligned - address;
        if (prefix > span.len) return error.InvalidRegion;
        const capacity = (span.len - prefix) & ~(@as(usize, block_alignment) - 1);
        if (capacity < minimum_block) return error.InvalidRegion;
        var self: RegionAllocator = .{ .memory = span[prefix..][0..capacity], .region_bytes = span.len, .capacity = capacity };
        self.header(0).* = .{ .span = capacity, .previous_span = 0 };
        return self;
    }

    /// The caller releases the external region only after this succeeds.
    pub fn deinit(self: *RegionAllocator) void {
        std.debug.assert(self.live_allocations == 0 and self.current == 0 and self.requested_current == 0);
        self.* = undefined;
    }

    pub fn allocator(self: *RegionAllocator) std.mem.Allocator {
        return .{ .ptr = self, .vtable = &.{ .alloc = alloc, .resize = resize, .remap = remap, .free = free } };
    }

    fn header(self: *RegionAllocator, offset: usize) *Header {
        std.debug.assert(offset <= self.capacity - @sizeOf(Header) and offset % block_alignment == 0);
        return @ptrCast(@alignCast(self.memory.ptr + offset));
    }

    fn removeFree(self: *RegionAllocator, offset: usize) void {
        const h = self.header(offset);
        std.debug.assert(!h.used);
        if (h.previous_free == none) self.free_head = h.next_free else self.header(h.previous_free).next_free = h.next_free;
        if (h.next_free != none) self.header(h.next_free).previous_free = h.previous_free;
        h.next_free = none;
        h.previous_free = none;
    }

    fn addFree(self: *RegionAllocator, offset: usize) void {
        const h = self.header(offset);
        std.debug.assert(!h.used);
        h.next_free = self.free_head;
        h.previous_free = none;
        if (self.free_head != none) self.header(self.free_head).previous_free = offset;
        self.free_head = offset;
    }

    fn updateFollowing(self: *RegionAllocator, offset: usize) void {
        const h = self.header(offset);
        const next = offset + h.span;
        if (next < self.capacity) self.header(next).previous_span = h.span;
    }

    fn mergeNext(self: *RegionAllocator, offset: usize) void {
        const h = self.header(offset);
        const next = offset + h.span;
        if (next < self.capacity and !self.header(next).used) {
            const next_span = self.header(next).span;
            self.removeFree(next);
            h.span += next_span;
        }
        self.updateFollowing(offset);
    }

    /// Split an occupied, unlinked span. The free tail coalesces immediately.
    fn splitTail(self: *RegionAllocator, offset: usize, wanted: usize) void {
        const h = self.header(offset);
        if (h.span - wanted < minimum_block) {
            self.updateFollowing(offset);
            return;
        }
        const remainder = h.span - wanted;
        h.span = wanted;
        const tail = offset + wanted;
        self.header(tail).* = .{ .span = remainder, .previous_span = wanted };
        self.mergeNext(tail);
        self.addFree(tail);
    }

    fn occupied(self: *RegionAllocator, memory: []u8, alignment: Alignment) usize {
        const address = @intFromPtr(memory.ptr);
        const base = @intFromPtr(self.memory.ptr);
        std.debug.assert(address >= base + @sizeOf(Header) + anchor_size and address < base + self.capacity);
        std.debug.assert(alignment.check(address));
        const anchor: *align(1) const usize = @ptrFromInt(address - anchor_size);
        const offset = anchor.*;
        const h = self.header(offset);
        std.debug.assert(h.used and h.requested == memory.len and base + offset + h.payload_offset == address);
        return offset;
    }

    fn account(self: *RegionAllocator, old_span: usize, new_span: usize, old_requested: usize, new_requested: usize) void {
        self.current = self.current - old_span + new_span;
        self.requested_current = self.requested_current - old_requested + new_requested;
        self.peak = @max(self.peak, self.current);
        self.requested_peak = @max(self.requested_peak, self.requested_current);
    }

    fn alloc(ctx: *anyopaque, len: usize, alignment: Alignment, ret: usize) ?[*]u8 {
        _ = ret;
        const self: *RegionAllocator = @ptrCast(@alignCast(ctx));
        var offset = self.free_head;
        var remaining = self.capacity / minimum_block + 1;
        while (offset != none and remaining != 0) : (remaining -= 1) {
            const h = self.header(offset);
            const base = @intFromPtr(self.memory.ptr) + offset;
            const payload = alignChecked(base + @sizeOf(Header) + anchor_size, alignment.toByteUnits()) orelse break;
            const payload_offset = payload - base;
            const payload_end = std.math.add(usize, payload_offset, len) catch break;
            const wanted = alignChecked(payload_end, block_alignment) orelse break;
            if (wanted <= h.span) {
                self.removeFree(offset);
                h.used = true;
                h.requested = len;
                h.payload_offset = payload_offset;
                self.splitTail(offset, wanted);
                const anchor: *align(1) usize = @ptrFromInt(payload - anchor_size);
                anchor.* = offset;
                self.account(0, h.span, 0, len);
                self.live_allocations += 1;
                return @ptrFromInt(payload);
            }
            offset = h.next_free;
        }
        self.failures +|= 1;
        return null;
    }

    fn resize(ctx: *anyopaque, memory: []u8, alignment: Alignment, len: usize, ret: usize) bool {
        _ = ret;
        const self: *RegionAllocator = @ptrCast(@alignCast(ctx));
        const offset = self.occupied(memory, alignment);
        const h = self.header(offset);
        const wanted = alignChecked(std.math.add(usize, h.payload_offset, len) catch return false, block_alignment) orelse return false;
        const old_span = h.span;
        if (wanted > h.span) {
            const next = offset + h.span;
            if (next >= self.capacity or self.header(next).used or self.header(next).span < wanted - h.span) return false;
            const next_span = self.header(next).span;
            self.removeFree(next);
            h.span += next_span;
        }
        self.splitTail(offset, wanted);
        h.requested = len;
        self.account(old_span, h.span, memory.len, len);
        return true;
    }

    fn remap(ctx: *anyopaque, memory: []u8, alignment: Alignment, len: usize, ret: usize) ?[*]u8 {
        return if (resize(ctx, memory, alignment, len, ret)) memory.ptr else null;
    }

    fn free(ctx: *anyopaque, memory: []u8, alignment: Alignment, ret: usize) void {
        _ = ret;
        const self: *RegionAllocator = @ptrCast(@alignCast(ctx));
        var offset = self.occupied(memory, alignment);
        const h = self.header(offset);
        self.account(h.span, 0, memory.len, 0);
        self.live_allocations -= 1;
        h.used = false;
        h.requested = 0;
        h.payload_offset = 0;
        self.mergeNext(offset);
        if (h.previous_span != 0) {
            const previous = offset - h.previous_span;
            if (!self.header(previous).used) {
                self.removeFree(previous);
                self.header(previous).span += h.span;
                offset = previous;
                self.updateFollowing(offset);
            }
        }
        self.addFree(offset);
    }
};

fn alignChecked(value: usize, alignment: usize) ?usize {
    return (std.math.add(usize, value, alignment - 1) catch return null) & ~(alignment - 1);
}

test "region allocator charges physical blocks and reuses tiny allocations without system allocation" {
    const backing = try std.testing.allocator.alloc(u8, 8192);
    defer std.testing.allocator.free(backing);
    var region = try RegionAllocator.init(backing);
    defer region.deinit();
    const a = region.allocator();
    var allocations: [32][]u8 = undefined;
    for (&allocations) |*item| item.* = try a.alloc(u8, 1);
    try std.testing.expectEqual(@as(usize, 32), region.requested_current);
    try std.testing.expect(region.current > region.requested_current and region.current < 8192);
    try std.testing.expectEqual(region.current, region.peak);
    for (allocations) |item| a.free(item);
    try std.testing.expectEqual(@as(usize, 0), region.current);
    try std.testing.expectEqual(region.capacity, region.header(0).span);
    const large = try a.alloc(u8, 7000);
    a.free(large);
}

test "region allocator handles alignment unaligned input coalescing and capacity failure" {
    const backing = try std.testing.allocator.alloc(u8, 8193);
    defer std.testing.allocator.free(backing);
    var region = try RegionAllocator.init(backing[1..]);
    defer region.deinit();
    const a = region.allocator();
    const first = try a.alignedAlloc(u8, comptime std.mem.Alignment.fromByteUnits(256), 200);
    const second = try a.alloc(u8, 300);
    const third = try a.alloc(u8, 400);
    try std.testing.expect(@intFromPtr(first.ptr) % 256 == 0);
    a.free(second);
    a.free(first);
    a.free(third);
    try std.testing.expectEqual(region.capacity, region.header(0).span);
    try std.testing.expectError(error.OutOfMemory, a.alloc(u8, region.capacity));
    try std.testing.expectEqual(@as(u64, 1), region.failures);
    try std.testing.expectEqual(@as(usize, 0), region.current);
}

test "region in-place growth shrink and realloc preserve contents and accurate accounting" {
    const backing = try std.testing.allocator.alloc(u8, 8192);
    defer std.testing.allocator.free(backing);
    var region = try RegionAllocator.init(backing);
    defer region.deinit();
    const a = region.allocator();
    var first = try a.alloc(u8, 100);
    @memset(first, 42);
    const second = try a.alloc(u8, 100);
    try std.testing.expect(!a.resize(first, 500));
    a.free(second);
    try std.testing.expect(a.resize(first, 500));
    first = first.ptr[0..500];
    try std.testing.expectEqual(@as(usize, 500), region.requested_current);
    for (first[0..100]) |byte| try std.testing.expectEqual(@as(u8, 42), byte);
    try std.testing.expect(a.resize(first, 50));
    first = first.ptr[0..50];
    const blocker = try a.alloc(u8, 100);
    first = try a.realloc(first, 300);
    for (first[0..50]) |byte| try std.testing.expectEqual(@as(u8, 42), byte);
    a.free(blocker);
    a.free(first);
    try std.testing.expectEqual(@as(usize, 0), region.requested_current);
    try std.testing.expectEqual(region.capacity, region.header(0).span);
}

test "region allocator rejects undersized region" {
    var backing: [16]u8 = undefined;
    try std.testing.expectError(error.InvalidRegion, RegionAllocator.init(&backing));
}
