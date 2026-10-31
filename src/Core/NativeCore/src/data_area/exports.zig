//! C ABI of the region owner. The C# side is a thin entry over these calls
//! (src/ReadWriteCore/Native/DataArea/DataAreaAbi.cs); no HTTP entry yet.
const std = @import("std");
const ss = @import("rm_shared_section");
const plan = @import("plan.zig");
const owner_module = @import("owner.zig");
const Owner = owner_module.Owner;

pub const abi_version: u32 = 0x0001_0000;

/// 0 is success. Stable for the C ABI.
pub const Status = enum(u32) {
    ok = 0,
    invalid_argument = 1,
    abi_mismatch = 2,
    out_of_memory = 3,
    /// See the ApplyFailure: declaration, local position and package reason.
    declaration_invalid = 4,
    region_too_large = 5,
    /// Creating or mapping a section failed; ApplyFailure.win32_error.
    section_failed = 6,
    /// A borrow did not end within the drain timeout; nothing changed.
    borrowed = 7,
    stopped = 8,
    /// The binding or view is from an earlier layout; register again.
    stale_binding = 9,
    /// The region is being replaced or stopped; try the round again later.
    replacing = 10,
    /// No declaration targets the region.
    absent = 11,
    mixed_regions = 12,
    buffer_too_small = 13,
    not_borrowed = 14,
    not_stopped = 15,
};

fn status(err: owner_module.Error) u32 {
    const value: Status = switch (err) {
        error.InvalidArgument => .invalid_argument,
        error.OutOfMemory => .out_of_memory,
        error.DeclarationInvalid => .declaration_invalid,
        error.RegionTooLarge => .region_too_large,
        error.SectionFailed => .section_failed,
        error.Borrowed => .borrowed,
        error.Stopped => .stopped,
        error.StaleBinding => .stale_binding,
        error.Replacing => .replacing,
        error.Absent => .absent,
        error.MixedRegions => .mixed_regions,
        error.BufferTooSmall => .buffer_too_small,
        error.NotBorrowed => .not_borrowed,
        error.NotStopped => .not_stopped,
    };
    return @intFromEnum(value);
}

fn ok() u32 {
    return @intFromEnum(Status.ok);
}
fn code(value: Status) u32 {
    return @intFromEnum(value);
}

fn owner(handle: ?*anyopaque) ?*Owner {
    const pointer = handle orelse return null;
    return @ptrCast(@alignCast(pointer));
}

pub export fn rm_data_area_abi_version() callconv(.c) u32 {
    return abi_version;
}

/// `settings` may be null for the declared defaults; `settings_size` is its size.
pub export fn rm_data_area_create(settings: ?*const owner_module.Settings, settings_size: u32, output: ?*?*anyopaque) callconv(.c) u32 {
    const out = output orelse return code(.invalid_argument);
    out.* = null;
    if (settings != null and settings_size != @sizeOf(owner_module.Settings)) return code(.abi_mismatch);
    const chosen: owner_module.Settings = if (settings) |given| given.* else .{};
    const self = std.heap.page_allocator.create(Owner) catch return code(.out_of_memory);
    self.* = Owner.init(std.heap.page_allocator, chosen);
    out.* = self;
    return ok();
}

pub export fn rm_data_area_apply(handle: ?*anyopaque, declarations: ?[*]const plan.Declaration, count: u32, now_unix_ms: i64, failure: ?*plan.ApplyFailure) callconv(.c) u32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    var local_failure = plan.ApplyFailure{};
    const detail = failure orelse &local_failure;
    detail.* = .{};
    const list: []const plan.Declaration = if (count == 0) &.{} else (declarations orelse return code(.invalid_argument))[0..count];
    self.apply(list, now_unix_ms, detail) catch |err| return status(err);
    return ok();
}

pub export fn rm_data_area_binding(handle: ?*anyopaque, declaration: u32, output: ?*owner_module.Binding) callconv(.c) u32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    out.* = self.binding(declaration) catch |err| return status(err);
    return ok();
}

pub export fn rm_data_area_read_view(handle: ?*anyopaque, declarations: ?[*]const u32, count: u32, sources: ?[*]u32, capacity: u32, view: ?*owner_module.ReadView) callconv(.c) u32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = view orelse return code(.invalid_argument);
    out.* = .{};
    const list = (declarations orelse return code(.invalid_argument))[0..count];
    const buffer: []u32 = if (capacity == 0) &.{} else (sources orelse return code(.invalid_argument))[0..capacity];
    self.readView(list, buffer, out) catch |err| return status(err);
    return ok();
}

pub export fn rm_data_area_acquire(handle: ?*anyopaque, region: u32, layout_generation: u64, output: ?*owner_module.Borrow) callconv(.c) u32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    out.* = .{};
    out.* = self.acquire(@enumFromInt(region), layout_generation) catch |err| return status(err);
    return ok();
}

pub export fn rm_data_area_release(handle: ?*anyopaque, borrow: ?*const owner_module.Borrow) callconv(.c) u32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const given = borrow orelse return code(.invalid_argument);
    self.release(given) catch |err| return status(err);
    return ok();
}

pub export fn rm_data_area_stop(handle: ?*anyopaque, now_unix_ms: i64) callconv(.c) u32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    self.stop(now_unix_ms) catch |err| return status(err);
    return ok();
}

/// Refuses (and keeps the handle valid) while a region is still mapped.
pub export fn rm_data_area_destroy(handle: ?*anyopaque) callconv(.c) u32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    self.deinit() catch |err| return status(err);
    std.heap.page_allocator.destroy(self);
    return ok();
}

comptime {
    _ = &rm_data_area_abi_version;
    _ = &rm_data_area_create;
    _ = &rm_data_area_apply;
    _ = &rm_data_area_binding;
    _ = &rm_data_area_read_view;
    _ = &rm_data_area_acquire;
    _ = &rm_data_area_release;
    _ = &rm_data_area_stop;
    _ = &rm_data_area_destroy;
}

test "ABI structures keep their layout" {
    try std.testing.expectEqual(@as(usize, 88), @sizeOf(ss.SourceIdentity));
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(plan.PositionDeclaration));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(plan.Declaration));
    try std.testing.expectEqual(@as(usize, 16), @offsetOf(plan.Declaration, "sources"));
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(plan.ApplyFailure));
    try std.testing.expectEqual(@as(usize, 8), @sizeOf(owner_module.Settings));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(owner_module.Binding));
    try std.testing.expectEqual(@as(usize, 24), @offsetOf(owner_module.Binding, "layout_generation"));
    try std.testing.expectEqual(@as(usize, 16), @sizeOf(owner_module.ReadView));
    try std.testing.expectEqual(@as(usize, 40), @sizeOf(owner_module.Borrow));
    try std.testing.expectEqual(@as(usize, 32), @offsetOf(owner_module.Borrow, "region"));
}

test "the C ABI round-trips a declaration, a borrow and stop" {
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(ok(), rm_data_area_create(null, 0, &handle));
    const sources = [_]ss.SourceIdentity{try ss.SourceIdentity.init("abi.test", 1, 0, 0)};
    const positions = [_]plan.PositionDeclaration{.{ .source = 0, .kind = .scalar, .element_width = 8, .capacity = 1 }};
    const declarations = [_]plan.Declaration{plan.Declaration.init(.administrator, &sources, &positions)};
    var failure = plan.ApplyFailure{};
    try std.testing.expectEqual(ok(), rm_data_area_apply(handle, &declarations, 1, 1, &failure));
    var binding: owner_module.Binding = undefined;
    try std.testing.expectEqual(ok(), rm_data_area_binding(handle, 0, &binding));
    var borrow = owner_module.Borrow{};
    try std.testing.expectEqual(code(.absent), rm_data_area_acquire(handle, 0, binding.layout_generation, &borrow));
    try std.testing.expectEqual(code(.invalid_argument), rm_data_area_acquire(handle, 9, binding.layout_generation, &borrow));
    try std.testing.expectEqual(ok(), rm_data_area_acquire(handle, 1, binding.layout_generation, &borrow));
    try std.testing.expectEqual(@as(u64, 64 * 1024), borrow.byte_length);
    try std.testing.expectEqual(code(.not_stopped), rm_data_area_destroy(handle));
    try std.testing.expectEqual(ok(), rm_data_area_release(handle, &borrow));
    try std.testing.expectEqual(ok(), rm_data_area_stop(handle, 2));
    try std.testing.expectEqual(ok(), rm_data_area_destroy(handle));
}
