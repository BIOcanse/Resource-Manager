// One demand-scoped Windows shared-memory query. It is called by the existing
// subscribed source, never scheduled here. OS and Ledger capabilities are
// injected; Windows uses the same API sequence as the replaced managed reader.
const std = @import("std");
const gpu = @import("gpu_owner.zig");
pub const selection = @import("adapter_selection.zig");
const more_data: i32 = @bitCast(@as(u32, 0x800007d2));
const format_large_no_scale: u32 = 0x1400;
const process_path = std.unicode.utf8ToUtf16LeStringLiteral("\\GPU Process Memory(*)\\Shared Usage");
const adapter_path = std.unicode.utf8ToUtf16LeStringLiteral("\\GPU Adapter Memory(*)\\Shared Usage");
pub const Work = *const fn (?*anyopaque, [*]u8, usize) callconv(.c) u32;
pub const Host = extern struct {
    size: u32 = @sizeOf(Host), version: u32 = 1,
    context: ?*anyopaque,
    open: ?*const fn (?*anyopaque, *usize) callconv(.c) i32,
    add: ?*const fn (?*anyopaque, usize, [*:0]const u16, *usize) callconv(.c) i32,
    collect: ?*const fn (?*anyopaque, usize) callconv(.c) i32,
    array: ?*const fn (?*anyopaque, usize, u32, *u32, *u32, ?[*]u8) callconv(.c) i32,
    close: ?*const fn (?*anyopaque, usize) callconv(.c) i32,
    workspace_context: ?*anyopaque,
    with_region: ?*const fn (?*anyopaque, usize, Work, ?*anyopaque) callconv(.c) u32,
    consume_context: ?*anyopaque,
    consume: ?*const fn (?*anyopaque, *const gpu.SharedResult, usize) callconv(.c) void,
};
pub const Request = extern struct {
    limits: gpu.DecoderLimits,
    maximum_workspace: usize,
    requested: ?[*]const u64,
    requested_count: usize,
    adapters: ?[*]const selection.Adapter = null,
    adapter_count: usize = 0,
    process: u32,
    reserved: u32 = 0,
};
const Operation = struct {
    host: *const Host, request: *const Request, query: usize = 0, counter: usize = 0,
    allocated: u32 = 0, count: u32 = 0, closed: bool = false, emitted: bool = false, ignore_close_failure: bool = false,
    result: gpu.SharedResult = .{},
    initial_failures: u32 = 0,
    fn fail(self: *Operation, status: i32, failures: u32) void {
        self.result = .{ .result = 8, .native_status = @bitCast(status), .failures = failures };
    }
    fn close(self: *Operation) void {
        if (self.closed or self.query == 0) return;
        self.closed = true;
        const status = self.host.close.?(self.host.context, self.query);
        if (status != 0 and !self.ignore_close_failure) self.fail(status, 0);
    }
    fn emit(self: *Operation, bytes: usize) void {
        self.close();
        self.host.consume.?(self.host.consume_context, &self.result, bytes);
        self.emitted = true;
    }
};
fn selected(request: *const Request) []const u64 {
    return if (request.requested) |p| p[0..request.requested_count] else &.{};
}
fn adapters(request: *const Request) []const selection.Adapter {
    return if (request.adapters) |p| p[0..request.adapter_count] else &.{};
}
fn selectedCount(request: *const Request) u32 {
    if (request.process == 0) return selection.selectedCount(adapters(request));
    const ids = selected(request);
    var count: u32 = 0;
    for (ids, 0..) |id, index| {
        if (id != 0 and std.mem.indexOfScalar(u64, ids[0..index], id) == null) count += 1;
    }
    return count;
}
fn preparationFailures(operation: *Operation) u32 {
    return if (operation.request.process == 1) 0 else
        std.math.add(u32, operation.initial_failures, selectedCount(operation.request)) catch std.math.maxInt(u32);
}
fn work(context: ?*anyopaque, bytes: [*]u8, length: usize) callconv(.c) u32 {
    const operation: *Operation = @ptrCast(@alignCast(context orelse return 0));
    var region = std.heap.FixedBufferAllocator.init(bytes[0..length]);
    const allocator = region.allocator();
    const adapter_ids = allocator.alloc(u64, if (operation.request.process == 0) operation.request.adapter_count else 0) catch return 0;
    var ids: []const u64 = selected(operation.request);
    if (operation.request.process == 0) {
        var count: usize = 0;
        for (adapters(operation.request)) |adapter| if (selection.selected(adapters(operation.request), adapter)) {
            adapter_ids[count] = adapter.luid; count += 1;
        };
        ids = adapter_ids[0..count];
    }
    const raw = allocator.alloc(u8, operation.allocated) catch return 0;
    const values = allocator.alloc(gpu.Formatted, operation.count) catch return 0;
    const rows = allocator.alloc(gpu.SharedRow, operation.count) catch return 0;
    var returned = operation.allocated;
    var count = operation.count;
    const status = operation.host.array.?(operation.host.context, operation.counter,
        format_large_no_scale, &returned, &count, raw.ptr);
    if (status != 0) {
        operation.fail(status, preparationFailures(operation));
    } else if (!gpu.decode(raw.ptr, operation.allocated, returned, count, operation.request.limits, values)) {
        operation.fail(more_data, preparationFailures(operation));
    } else {
        const result = gpu.shared(values[0..count], ids, operation.request.process == 1, rows, &operation.result);
        if (result != 0) return 0;
        operation.result.failures = std.math.add(u32, operation.result.failures, operation.initial_failures) catch return 0;
        if (operation.request.process == 0) {
            var written: usize = 0;
            for (rows[0..operation.result.count]) |row| if (selection.indexOf(adapters(operation.request), row.luid)) |index| {
                rows[written] = row; rows[written].pid = index; written += 1;
            };
            operation.result.count = written;
        }
        operation.result.result = if (operation.result.count != 0) 0 else if (operation.result.failures != 0) 8 else 4;
    }
    // Close must happen before consumption while result rows are still borrowed
    // from the local Ledger lease. A close error overrides all otherwise-good data.
    operation.emit(region.end_index);
    return 1;
}
pub fn read(host: *const Host, request: *const Request) u32 {
    if (host.size != @sizeOf(Host) or host.version != 1 or request.process > 1 or
        host.open == null or host.add == null or host.collect == null or host.array == null or host.close == null or
        host.with_region == null or host.consume == null or request.maximum_workspace == 0 or
        (request.requested == null and request.requested_count != 0) or request.requested_count > std.math.maxInt(u32) or
        (request.adapters == null and request.adapter_count != 0) or request.adapter_count > std.math.maxInt(u32) or request.reserved != 0) return 1;
    var operation = Operation{ .host = host, .request = request };
    operation.initial_failures = if (request.process == 0) selection.ambiguousCount(adapters(request)) else 0;
    defer if (!operation.emitted) operation.emit(0);
    if (selectedCount(request) == 0) {
        operation.result.failures = operation.initial_failures;
        operation.result.flags = @intFromBool(operation.initial_failures == 0);
        return 0;
    }
    const open = host.open.?(host.context, &operation.query);
    if (open != 0 or operation.query == 0) { operation.ignore_close_failure = true; operation.fail(open, 0); return 0; }
    const add = host.add.?(host.context, operation.query, if (request.process == 1) process_path else adapter_path, &operation.counter);
    if (add != 0 or operation.counter == 0) { operation.fail(add, 0); return 0; }
    const collect = host.collect.?(host.context, operation.query);
    if (collect != 0) { operation.fail(collect, preparationFailures(&operation)); return 0; }
    const probe = host.array.?(host.context, operation.counter, format_large_no_scale, &operation.allocated, &operation.count, null);
    if (probe == 0) {
        operation.result.failures = operation.initial_failures;
        if (operation.initial_failures != 0) operation.result.result = 8;
        return 0;
    }
    if (probe != more_data) { operation.fail(probe, preparationFailures(&operation)); return 0; }
    if (operation.allocated == 0 or operation.allocated > request.limits.bytes or operation.count > request.limits.items or
        @as(u64, operation.count) * 24 > operation.allocated) {
        operation.fail(more_data, preparationFailures(&operation)); return 0;
    }
    const structured = std.math.mul(usize, operation.count, @sizeOf(gpu.Formatted) + @sizeOf(gpu.SharedRow)) catch {
        operation.fail(more_data, preparationFailures(&operation)); return 0;
    };
    const adapter_bytes = std.math.mul(usize, if (request.process == 0) request.adapter_count else 0, @sizeOf(u64)) catch return 1;
    const required = std.math.add(usize, std.math.add(usize, std.math.add(usize, operation.allocated, structured) catch return 1,
        adapter_bytes) catch return 1, @alignOf(u64) + @alignOf(gpu.Formatted) + @alignOf(gpu.SharedRow)) catch return 1;
    if (required > request.maximum_workspace) { operation.fail(more_data, preparationFailures(&operation)); return 0; }
    if (host.with_region.?(host.workspace_context, required, work, &operation) == 0 or !operation.emitted) {
        operation.fail(more_data, preparationFailures(&operation)); return 5;
    }
    return 0;
}

// Windows leaf adapter. All entries are read-only PDH calls already used in the
// product; English wildcard paths and 64-bit item layout match the old reader.
extern "pdh" fn PdhOpenQueryW(?[*:0]const u16, usize, *usize) callconv(.winapi) i32;
extern "pdh" fn PdhAddEnglishCounterW(usize, [*:0]const u16, usize, *usize) callconv(.winapi) i32;
extern "pdh" fn PdhCollectQueryData(usize) callconv(.winapi) i32;
extern "pdh" fn PdhGetFormattedCounterArrayW(usize, u32, *u32, *u32, ?[*]u8) callconv(.winapi) i32;
extern "pdh" fn PdhCloseQuery(usize) callconv(.winapi) i32;
fn windowsOpen(_: ?*anyopaque, query: *usize) callconv(.c) i32 { return PdhOpenQueryW(null, 0, query); }
fn windowsAdd(_: ?*anyopaque, query: usize, path: [*:0]const u16, counter: *usize) callconv(.c) i32 { return PdhAddEnglishCounterW(query, path, 0, counter); }
fn windowsCollect(_: ?*anyopaque, query: usize) callconv(.c) i32 { return PdhCollectQueryData(query); }
fn windowsArray(_: ?*anyopaque, counter: usize, format: u32, bytes: *u32, count: *u32, buffer: ?[*]u8) callconv(.c) i32 {
    return PdhGetFormattedCounterArrayW(counter, format, bytes, count, buffer);
}
fn windowsClose(_: ?*anyopaque, query: usize) callconv(.c) i32 { return PdhCloseQuery(query); }
pub fn windows(host: *Host) void {
    host.open = windowsOpen; host.add = windowsAdd; host.collect = windowsCollect; host.array = windowsArray; host.close = windowsClose;
}
