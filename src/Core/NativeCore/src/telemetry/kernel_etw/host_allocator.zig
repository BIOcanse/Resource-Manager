const policy = @import("../../configuration/NativePolicy.g.zig");
const std = @import("std");
const Region = @import("region_allocator.zig").RegionAllocator;

pub const Callbacks = extern struct {
    size: u32 = @sizeOf(Callbacks),
    version: u32 = 1,
    page_size: usize,
    context: ?*anyopaque,
    allocate: ?*const fn (?*anyopaque, u32, u64, u32) callconv(.c) ?*anyopaque,
    remap: ?*const fn (?*anyopaque, u32, ?*anyopaque, u64, u64, u32) callconv(.c) ?*anyopaque,
    release: ?*const fn (?*anyopaque, u32, ?*anyopaque, u64, u32) callconv(.c) u32,

    pub fn validate(self: Callbacks) !void {
        if (self.size != @sizeOf(Callbacks) or self.version != 1 or self.page_size < 1024 or
            !std.math.isPowerOfTwo(self.page_size) or self.allocate == null or self.remap == null or self.release == null)
            return error.InvalidAllocator;
    }
};

pub const Fault = enum(u32) { none = 0, allocation = 1, remap = 2, release = 3, byte_cap = 4, invalid_resize = 5 };

/// Page accounting belongs to an output. Its parent is the whole kernel owner.
/// Only reservations/committed counters are atomic: ordinary allocator state is
/// owned by the consumer flow (the cold control scope is serialized separately).
pub const Accounting = struct {
    cap: usize,
    parent: ?*Accounting = null,
    reserved: std.atomic.Value(usize) = .init(0),
    committed: std.atomic.Value(usize) = .init(0),
    peak: std.atomic.Value(usize) = .init(0),
    requested: std.atomic.Value(usize) = .init(0),
    requested_peak: std.atomic.Value(usize) = .init(0),
    failures: std.atomic.Value(u64) = .init(0),

    fn reserveOne(counter: *std.atomic.Value(usize), cap: usize, amount: usize) bool {
        var old = counter.load(.monotonic);
        while (true) {
            if (old > cap or amount > cap - old) return false;
            if (counter.cmpxchgWeak(old, old + amount, .acq_rel, .monotonic)) |found| old = found else return true;
        }
    }
    fn maximum(counter: *std.atomic.Value(usize), value: usize) void {
        var old = counter.load(.monotonic);
        while (old < value) {
            if (counter.cmpxchgWeak(old, value, .monotonic, .monotonic)) |found| old = found else return;
        }
    }
    pub fn admit(self: *Accounting, amount: usize) bool {
        if (!reserveOne(&self.reserved, self.cap, amount)) {
            _ = self.failures.fetchAdd(1, .monotonic);
            return false;
        }
        if (self.parent) |parent| if (!reserveOne(&parent.reserved, parent.cap, amount)) {
            _ = self.reserved.fetchSub(amount, .monotonic);
            _ = self.failures.fetchAdd(1, .monotonic);
            _ = parent.failures.fetchAdd(1, .monotonic);
            return false;
        };
        return true;
    }
    pub fn abandon(self: *Accounting, amount: usize) void {
        _ = self.reserved.fetchSub(amount, .monotonic);
        if (self.parent) |parent| _ = parent.reserved.fetchSub(amount, .monotonic);
    }
    pub fn committedGrowth(self: *Accounting, amount: usize) void {
        maximum(&self.peak, self.committed.fetchAdd(amount, .monotonic) + amount);
        if (self.parent) |parent| maximum(&parent.peak, parent.committed.fetchAdd(amount, .monotonic) + amount);
    }
    pub fn returned(self: *Accounting, amount: usize) void {
        self.abandon(amount);
        _ = self.committed.fetchSub(amount, .monotonic);
        if (self.parent) |parent| _ = parent.committed.fetchSub(amount, .monotonic);
    }
    pub fn requestChange(self: *Accounting, old: usize, new: usize) void {
        if (new >= old) {
            maximum(&self.requested_peak, self.requested.fetchAdd(new - old, .monotonic) + new - old);
            if (self.parent) |parent| maximum(&parent.requested_peak, parent.requested.fetchAdd(new - old, .monotonic) + new - old);
        } else {
            _ = self.requested.fetchSub(old - new, .monotonic);
            if (self.parent) |parent| _ = parent.requested.fetchSub(old - new, .monotonic);
        }
    }
};

const Page = struct {
    next: ?*Page,
    bytes: usize,
    region: Region,
};

/// Small objects share page-multiple Ledger chunks. Page headers and free lists
/// live inside those chunks, so there is no hidden allocator-metadata heap.
/// Large array blocks have independent Ledger identities and may be remapped by
/// their owning collection. No pool page is ever remapped with live objects.
pub const Context = struct {
    callbacks: Callbacks,
    tag: u32,
    accounting: *Accounting,
    terminal: *std.atomic.Value(u32),
    pages: ?*Page = null,
    small_requested: usize = 0,
    requested: usize = 0,

    pub fn init(callbacks: Callbacks, tag: u32, accounting: *Accounting, terminal: *std.atomic.Value(u32)) !Context {
        try callbacks.validate();
        if (tag & 0xff > 8 or tag >> 8 > 9) return error.InvalidAllocator;
        return .{ .callbacks = callbacks, .tag = tag, .accounting = accounting, .terminal = terminal };
    }
    pub fn allocator(self: *Context) std.mem.Allocator {
        return .{ .ptr = self, .vtable = &.{ .alloc = alloc, .resize = resize, .remap = remap, .free = free } };
    }
    pub fn deinit(self: *Context) void {
        std.debug.assert(self.pages == null and self.requested == 0 and self.small_requested == 0);
    }
    fn fail(self: *Context, value: Fault) void {
        if (self.terminal.cmpxchgStrong(0, @intFromEnum(value), .release, .monotonic) == null and
            (value == .allocation or value == .remap or value == .invalid_resize))
        {
            _ = self.accounting.failures.fetchAdd(1, .monotonic);
            if (self.accounting.parent) |parent| _ = parent.failures.fetchAdd(1, .monotonic);
        }
    }
    fn rounded(self: *const Context, bytes: usize) ?usize {
        const added = std.math.add(usize, bytes, self.callbacks.page_size - 1) catch return null;
        return added & ~(self.callbacks.page_size - 1);
    }
    fn changed(self: *Context, old: usize, new: usize) void {
        self.requested = self.requested - old + new;
        self.accounting.requestChange(old, new);
    }
    fn findPage(self: *Context, ptr: [*]u8) ?*Page {
        const address = @intFromPtr(ptr);
        var node = self.pages;
        // The number of chunks is bounded by this owner's occupied pages.
        var remaining = self.accounting.cap / self.callbacks.page_size + 1;
        while (node) |page| {
            if (remaining == 0) return null;
            remaining -= 1;
            const base = @intFromPtr(page);
            if (address >= base and address - base < page.bytes) return page;
            node = page.next;
        }
        return null;
    }
    fn newPage(self: *Context, len: usize, alignment: std.mem.Alignment) ?*Page {
        const overhead = std.math.add(usize, @sizeOf(Page), alignment.toByteUnits() + 128) catch return null;
        const minimum = std.math.add(usize, overhead, len) catch return null;
        // Capacity grows with occupied small data, not the compiled cap. The
        // first request commits one page and empty chunks return immediately.
        // Growth is bounded by a sixteenth of this owner's declared cap: an
        // unoccupied speculative chunk must not be what crosses that cap, and
        // one surviving small object pins at most that much.
        const step = @max(self.callbacks.page_size, self.accounting.cap / policy.etw_allocator_cap_divisor);
        const target = @max(minimum, @min(self.small_requested / policy.etw_allocator_requested_divisor, step));
        const bytes = self.rounded(@max(self.callbacks.page_size, target)) orelse return null;
        if (!self.accounting.admit(bytes)) { self.fail(.byte_cap); return null; }
        const raw = self.callbacks.allocate.?(self.callbacks.context, self.tag, bytes, @intFromEnum(std.mem.Alignment.fromByteUnits(@alignOf(Page)))) orelse {
            self.accounting.abandon(bytes);
            self.fail(.allocation);
            return null;
        };
        self.accounting.committedGrowth(bytes);
        const page: *Page = @ptrCast(@alignCast(raw));
        const memory: [*]u8 = @ptrCast(raw);
        page.* = .{ .next = self.pages, .bytes = bytes, .region = Region.init(memory[@sizeOf(Page)..bytes]) catch unreachable };
        self.pages = page;
        return page;
    }
    fn alloc(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret: usize) ?[*]u8 {
        const self: *Context = @ptrCast(@alignCast(ctx));
        if (self.terminal.load(.acquire) != 0) return null;
        // A sparse pair's hash table is commonly 1--4 KiB. Giving each such
        // table an independent Ledger identity exhausts the structural slot
        // table long before this owner's byte cap. Co-locate page-sized objects
        // with the same owner/scope; the chunks never move with live objects.
        if (len <= self.callbacks.page_size and alignment.toByteUnits() <= self.callbacks.page_size / policy.etw_allocator_alignment_divisor) {
            var node = self.pages;
            var remaining = self.accounting.cap / self.callbacks.page_size + 1;
            while (node) |page| {
                if (remaining == 0) break;
                remaining -= 1;
                if (page.region.allocator().rawAlloc(len, alignment, ret)) |pointer| {
                    self.small_requested += len;
                    self.changed(0, len);
                    return pointer;
                }
                node = page.next;
            }
            const page = self.newPage(len, alignment) orelse return null;
            const pointer = page.region.allocator().rawAlloc(len, alignment, ret) orelse unreachable;
            self.small_requested += len;
            self.changed(0, len);
            return pointer;
        }
        const physical = self.rounded(len) orelse { self.fail(.allocation); return null; };
        if (!self.accounting.admit(physical)) { self.fail(.byte_cap); return null; }
        const pointer = self.callbacks.allocate.?(self.callbacks.context, self.tag, len, @intFromEnum(alignment)) orelse {
            self.accounting.abandon(physical);
            self.fail(.allocation);
            return null;
        };
        self.accounting.committedGrowth(physical);
        self.changed(0, len);
        return @ptrCast(pointer);
    }
    fn resize(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, len: usize, ret: usize) bool {
        const self: *Context = @ptrCast(@alignCast(ctx));
        if (self.terminal.load(.acquire) != 0) return false;
        if (self.findPage(memory.ptr)) |page| {
            if (!page.region.allocator().rawResize(memory, alignment, len, ret)) return false;
            self.small_requested = self.small_requested - memory.len + len;
            self.changed(memory.len, len);
            return true;
        }
        const old_pages = self.rounded(memory.len) orelse return false;
        if (old_pages != (self.rounded(len) orelse return false)) return false;
        const pointer = self.callbacks.remap.?(self.callbacks.context, self.tag, memory.ptr, memory.len, len, @intFromEnum(alignment)) orelse {
            self.fail(.remap);
            return false;
        };
        if (@intFromPtr(pointer) != @intFromPtr(memory.ptr)) {
            self.fail(.invalid_resize);
            return false;
        }
        self.changed(memory.len, len);
        return true;
    }
    fn remap(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, len: usize, ret: usize) ?[*]u8 {
        const self: *Context = @ptrCast(@alignCast(ctx));
        if (self.terminal.load(.acquire) != 0) return null;
        if (self.findPage(memory.ptr)) |page| {
            if (page.region.allocator().rawRemap(memory, alignment, len, ret)) |pointer| {
                self.small_requested = self.small_requested - memory.len + len;
                self.changed(memory.len, len);
                return pointer;
            }
            // Normal small-object replacement does not grow a shared page.
            const pointer = alloc(ctx, len, alignment, ret) orelse return null;
            @memcpy(pointer[0..@min(memory.len, len)], memory[0..@min(memory.len, len)]);
            free(ctx, memory, alignment, ret);
            return pointer;
        }
        const old_pages = self.rounded(memory.len) orelse return null;
        const new_pages = self.rounded(len) orelse return null;
        // Ledger cannot shrink a live allocation. Let the caller explicitly
        // replace it; do not misreport a moving operation as in-place resize.
        if (new_pages < old_pages) return null;
        const growth = new_pages - old_pages;
        // SDK growth allocates the complete replacement before releasing the
        // old pages. Admit that overlap, not merely the eventual difference.
        if (growth != 0 and !self.accounting.admit(new_pages)) { self.fail(.byte_cap); return null; }
        const pointer = self.callbacks.remap.?(self.callbacks.context, self.tag, memory.ptr, memory.len, len, @intFromEnum(alignment)) orelse {
            if (growth != 0) self.accounting.abandon(new_pages);
            // This latch also denies std.mem.Allocator.realloc's fallback
            // allocation, so a blocked Ledger update is never bypassed.
            self.fail(.remap);
            return null;
        };
        if (growth != 0) {
            // Peak is derived from the SDK's allocate/copy/release sequence;
            // committed returns to the actual final page count immediately.
            self.accounting.committedGrowth(new_pages);
            self.accounting.returned(old_pages);
        }
        self.changed(memory.len, len);
        return @ptrCast(pointer);
    }
    fn free(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret: usize) void {
        const self: *Context = @ptrCast(@alignCast(ctx));
        if (self.findPage(memory.ptr)) |page| {
            page.region.allocator().rawFree(memory, alignment, ret);
            self.small_requested -= memory.len;
            self.changed(memory.len, 0);
            if (page.region.live_allocations != 0) return;
            var link = &self.pages;
            var remaining = self.accounting.cap / self.callbacks.page_size + 1;
            while (link.*) |item| {
                std.debug.assert(remaining != 0);
                remaining -= 1;
                if (item == page) { link.* = item.next; break; }
                link = &item.next;
            }
            const bytes = page.bytes;
            page.region.deinit();
            const code = self.callbacks.release.?(self.callbacks.context, self.tag, page, bytes, @intFromEnum(std.mem.Alignment.fromByteUnits(@alignOf(Page))));
            if (code == 0) self.accounting.returned(bytes) else self.fail(.release);
            return;
        }
        const physical = self.rounded(memory.len) orelse unreachable;
        const code = self.callbacks.release.?(self.callbacks.context, self.tag, memory.ptr, memory.len, @intFromEnum(alignment));
        self.changed(memory.len, 0);
        if (code == 0) self.accounting.returned(physical) else self.fail(.release);
    }
};

test "host allocation ABI has the fixed x64 shape" {
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(Callbacks));
    try std.testing.expectEqual(@as(usize, 16), @offsetOf(Callbacks, "context"));
    try std.testing.expectEqual(@as(usize, 24), @offsetOf(Callbacks, "allocate"));
    try std.testing.expectEqual(@as(usize, 40), @offsetOf(Callbacks, "release"));
}

pub const TestHost = struct {
    const Block = struct { ptr: ?[*]u8 = null, physical: usize = 0, wanted: usize = 0, alignment: std.mem.Alignment = .@"1", tag: u32 = 0 };
    blocks: [164]Block = @splat(.{}),
    alloc_calls: usize = 0,
    remap_calls: usize = 0,
    block_growth: bool = false,
    fail_allocate_after: ?usize = null,

    pub fn callbacks(self: *TestHost) Callbacks {
        return .{ .page_size = 4096, .context = self, .allocate = allocate, .remap = move, .release = release };
    }
    fn find(self: *TestHost, pointer: ?*anyopaque) ?*Block {
        for (&self.blocks) |*block| if (block.ptr) |ptr| {
            if (@intFromPtr(ptr) == @intFromPtr(pointer orelse return null)) return block;
        };
        return null;
    }
    fn allocate(context: ?*anyopaque, tag: u32, wanted: u64, alignment_log2: u32) callconv(.c) ?*anyopaque {
        const self: *TestHost = @ptrCast(@alignCast(context.?));
        self.alloc_calls += 1;
        if (self.fail_allocate_after) |maximum| if (self.alloc_calls > maximum) return null;
        if (alignment_log2 > 20) return null;
        const alignment: std.mem.Alignment = @enumFromInt(alignment_log2);
        const bytes = std.mem.alignForward(usize, @intCast(wanted), 4096);
        for (&self.blocks) |*block| if (block.ptr == null) {
            const ptr = std.testing.allocator.rawAlloc(bytes, alignment, @returnAddress()) orelse return null;
            @memset(ptr[0..bytes], 0);
            block.* = .{ .ptr = ptr, .physical = bytes, .wanted = @intCast(wanted), .alignment = alignment, .tag = tag };
            return ptr;
        };
        return null;
    }
    fn move(context: ?*anyopaque, tag: u32, old: ?*anyopaque, old_bytes: u64, new_bytes: u64, alignment_log2: u32) callconv(.c) ?*anyopaque {
        const self: *TestHost = @ptrCast(@alignCast(context.?));
        self.remap_calls += 1;
        const block = self.find(old) orelse return null;
        if (block.tag != tag or block.wanted != old_bytes or @intFromEnum(block.alignment) != alignment_log2) return null;
        const physical = std.mem.alignForward(usize, @intCast(new_bytes), 4096);
        if (physical == block.physical) {
            block.wanted = @intCast(new_bytes);
            return old;
        }
        if (self.block_growth or physical < block.physical) return null;
        const ptr = std.testing.allocator.rawAlloc(physical, block.alignment, @returnAddress()) orelse return null;
        @memset(ptr[0..physical], 0);
        @memcpy(ptr[0..block.physical], block.ptr.?[0..block.physical]);
        std.testing.allocator.rawFree(block.ptr.?[0..block.physical], block.alignment, @returnAddress());
        block.ptr = ptr;
        block.physical = physical;
        block.wanted = @intCast(new_bytes);
        return ptr;
    }
    fn release(context: ?*anyopaque, tag: u32, pointer: ?*anyopaque, bytes: u64, alignment_log2: u32) callconv(.c) u32 {
        const self: *TestHost = @ptrCast(@alignCast(context.?));
        const block = self.find(pointer) orelse return 1;
        if (block.tag != tag or block.wanted != bytes or @intFromEnum(block.alignment) != alignment_log2) return 2;
        std.testing.allocator.rawFree(block.ptr.?[0..block.physical], block.alignment, @returnAddress());
        block.* = .{};
        return 0;
    }
    pub fn empty(self: *const TestHost) bool {
        for (self.blocks) |block| if (block.ptr != null) return false;
        return true;
    }
};

test "host small pools return window pages independently of run identities" {
    var host: TestHost = .{};
    var whole: Accounting = .{ .cap = 1024 * 1024 };
    var cpu: Accounting = .{ .cap = 512 * 1024, .parent = &whole };
    var fault: std.atomic.Value(u32) = .init(0);
    var metadata = try Context.init(host.callbacks(), 1, &cpu, &fault);
    defer metadata.deinit();
    var window = try Context.init(host.callbacks(), 1 | (1 << 8), &cpu, &fault);
    defer window.deinit();
    const m = metadata.allocator();
    const w = window.allocator();
    const identity = try m.alloc(u8, 80);
    @memset(identity, 17);
    var pairs: [192][]u8 = undefined;
    var count: usize = 0;
    defer for (pairs[0..count]) |pair| w.free(pair);
    for (&pairs) |*pair| {
        pair.* = try w.alloc(u8, 128);
        @memset(pair.*, 23);
        count += 1;
    }
    try std.testing.expect(host.alloc_calls < pairs.len / 2);
    try std.testing.expect(cpu.committed.load(.monotonic) > 4096);
    for (pairs) |pair| w.free(pair);
    count = 0;
    try std.testing.expectEqual(@as(usize, 0), window.requested);
    try std.testing.expect(window.pages == null);
    try std.testing.expectEqual(@as(usize, 4096), cpu.committed.load(.monotonic));
    try std.testing.expectEqualSlices(u8, &@as([80]u8, @splat(17)), identity);
    m.free(identity);
    try std.testing.expect(host.empty());
    try std.testing.expectEqual(@as(usize, 0), whole.committed.load(.monotonic));
    try std.testing.expectEqual(@as(u32, 0), fault.load(.monotonic));
}

test "host large datasets move with bytes intact and same page resize stays put" {
    var host: TestHost = .{};
    var accounting: Accounting = .{ .cap = 128 * 1024 };
    var fault: std.atomic.Value(u32) = .init(0);
    var context = try Context.init(host.callbacks(), 3, &accounting, &fault);
    defer context.deinit();
    const a = context.allocator();
    var memory = try a.alloc(u8, 5000);
    defer a.free(memory);
    @memset(memory, 71);
    const initial = memory.ptr;
    try std.testing.expect(a.resize(memory, 5100));
    memory = memory.ptr[0..5100];
    try std.testing.expectEqual(initial, memory.ptr);
    memory = try a.realloc(memory, 21000);
    try std.testing.expect(initial != memory.ptr);
    for (memory[0..5000]) |value| try std.testing.expectEqual(@as(u8, 71), value);
    for (memory[8192..]) |value| try std.testing.expectEqual(@as(u8, 0), value);
    try std.testing.expectEqual(@as(usize, 24576), accounting.committed.load(.monotonic));
    try std.testing.expectEqual(@as(usize, 32768), accounting.peak.load(.monotonic));
    try std.testing.expectEqual(@as(u32, 0), fault.load(.monotonic));
}

test "sparse pair cell tables share owner pages below the unchanged Ledger slot limit" {
    var host: TestHost = .{}; // The product's compute table has 164 slots.
    var accounting: Accounting = .{ .cap = 1024 * 1024 };
    var fault: std.atomic.Value(u32) = .init(0);
    var context = try Context.init(host.callbacks(), 1 | (1 << 8), &accounting, &fault);
    defer context.deinit();
    const a = context.allocator();
    const Cell = struct { duration: i64, switches: i32 };
    var pairs: [200]std.AutoHashMapUnmanaged(i64, Cell) = @splat(.empty);
    defer for (&pairs) |*cells| cells.deinit(a);
    for (&pairs, 0..) |*cells, pair| {
        for (0..40) |bucket| try cells.put(a, @intCast(bucket), .{ .duration = @intCast(pair + bucket), .switches = @intCast(bucket) });
    }
    for (&pairs, 0..) |*cells, pair| {
        for (0..40) |bucket| try std.testing.expectEqualDeep(Cell{ .duration = @intCast(pair + bucket), .switches = @intCast(bucket) }, cells.get(@intCast(bucket)).?);
    }
    try std.testing.expectEqual(@as(u32, 0), fault.load(.acquire));
    try std.testing.expectEqual(@as(u64, 0), accounting.failures.load(.acquire));
    try std.testing.expect(accounting.committed.load(.acquire) < pairs.len * 4096);
    for (&pairs) |*cells| { cells.deinit(a); cells.* = .empty; }
    try std.testing.expect(host.empty());
    try std.testing.expectEqual(@as(usize, 0), accounting.committed.load(.acquire));
}

test "small pool chunks stay bounded so occupied data near the cap is not refused for slack" {
    // Real window close: 9.3 MB of cell tables sat in 14.4 MB of chunks and
    // the next chunk asked for half the occupied data again, crossing 24 MiB.
    var host: TestHost = .{};
    var accounting: Accounting = .{ .cap = 1024 * 1024 };
    var fault: std.atomic.Value(u32) = .init(0);
    var context = try Context.init(host.callbacks(), 1 | (1 << 8), &accounting, &fault);
    defer context.deinit();
    const a = context.allocator();
    var objects: [768][]u8 = undefined;
    var count: usize = 0;
    defer for (objects[0..count]) |object| a.free(object);
    for (&objects) |*object| {
        object.* = try a.alloc(u8, 1024);
        count += 1;
    }
    try std.testing.expectEqual(@as(u32, 0), fault.load(.acquire));
    try std.testing.expectEqual(@as(u64, 0), accounting.failures.load(.acquire));
    for (host.blocks) |block| if (block.ptr != null) try std.testing.expect(block.physical <= accounting.cap / 16);
    try std.testing.expect(accounting.committed.load(.acquire) < accounting.cap);
    for (objects) |object| a.free(object);
    count = 0;
    try std.testing.expect(host.empty());
    try std.testing.expectEqual(@as(usize, 0), accounting.committed.load(.acquire));
}

test "blocked host remap is terminal and cannot use realloc fallback" {
    var host: TestHost = .{};
    var accounting: Accounting = .{ .cap = 128 * 1024 };
    var fault: std.atomic.Value(u32) = .init(0);
    var context = try Context.init(host.callbacks(), 2, &accounting, &fault);
    defer context.deinit();
    const a = context.allocator();
    const memory = try a.alloc(u8, 5000);
    @memset(memory, 31);
    host.block_growth = true;
    const calls = host.alloc_calls;
    try std.testing.expectError(error.OutOfMemory, a.realloc(memory, 12288));
    try std.testing.expectEqual(calls, host.alloc_calls);
    try std.testing.expectEqual(@as(usize, 8192), accounting.committed.load(.monotonic));
    try std.testing.expectEqual(@intFromEnum(Fault.remap), fault.load(.monotonic));
    for (memory) |value| try std.testing.expectEqual(@as(u8, 31), value);
    a.free(memory);
    try std.testing.expect(host.empty());
    try std.testing.expectEqual(@as(usize, 0), accounting.committed.load(.monotonic));
}

test "host growth admits replacement overlap rather than the final page difference" {
    var host: TestHost = .{};
    var accounting: Accounting = .{ .cap = 12288 };
    var fault: std.atomic.Value(u32) = .init(0);
    var context = try Context.init(host.callbacks(), 1, &accounting, &fault);
    defer context.deinit();
    const a = context.allocator();
    const memory = try a.alloc(u8, 5000);
    try std.testing.expectError(error.OutOfMemory, a.realloc(memory, 10000));
    try std.testing.expectEqual(@as(usize, 0), host.remap_calls);
    try std.testing.expectEqual(@as(usize, 8192), accounting.committed.load(.acquire));
    try std.testing.expectEqual(@intFromEnum(Fault.byte_cap), fault.load(.acquire));
    a.free(memory);
    try std.testing.expect(host.empty());
}

test "same page logical shrink retains the Ledger allocation and updates slice accounting" {
    var host: TestHost = .{};
    var accounting: Accounting = .{ .cap = 65536 };
    var fault: std.atomic.Value(u32) = .init(0);
    var context = try Context.init(host.callbacks(), 3, &accounting, &fault);
    defer context.deinit();
    const a = context.allocator();
    var memory = try a.alloc(u8, 5000);
    @memset(memory, 43);
    const original = memory.ptr;
    memory = try a.realloc(memory, 4500);
    try std.testing.expectEqual(original, memory.ptr);
    try std.testing.expectEqual(@as(usize, 1), host.remap_calls);
    try std.testing.expectEqual(@as(usize, 8192), accounting.committed.load(.acquire));
    try std.testing.expectEqual(@as(u32, 0), fault.load(.acquire));
    for (memory) |byte| try std.testing.expectEqual(@as(u8, 43), byte);
    a.free(memory);
    try std.testing.expect(host.empty());
}
