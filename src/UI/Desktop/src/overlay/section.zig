//! The injected overlay section, the same protocol as the window process's
//! InjectedOverlaySection and PerformanceOverlaySharedMemory (native mirror:
//! src/Core/Native/PerformanceOverlay/OverlayProtocol.h). The producer creates
//! the section with a protected DACL; the in-game module only opens it.
const std = @import("std");
const points = @import("desktop_points_values").Bound;
const c = @import("../backend/c.zig").win;

pub const magic: u32 = 0x564F4D52;
pub const version: u32 = 2;
pub const header_bytes = 88;
// OverlayProtocol.h permits up to 64 MiB. The producer's chosen capacity is
// a declared point bounded by that protocol, and readers use pixel_capacity.
pub const maximum_pixel_bytes = points.overlay_pixel_capacity_bytes;
pub const handshake: i32 = 0;
pub const enabled: i32 = 1;
pub const disabled: i32 = -1;
/// The product's name prefix. Tests pass their own prefix explicitly.
pub const product_prefix = "Local\\ResourceManager.PerformanceOverlay";

pub const Header = extern struct {
    magic: u32,
    version: u32,
    header_bytes: u32,
    pixel_capacity: u32,
    generation: i64,
    consumed_generation: i64,
    frame_counter: i64,
    draw_success_counter: i64,
    target_window: u64,
    width: u32,
    height: u32,
    stride: u32,
    enabled: i32,
    swap_chain_width: i32,
    swap_chain_height: i32,
    publisher_process_id: u32,
};
comptime {
    std.debug.assert(@sizeOf(Header) == header_bytes);
    std.debug.assert(@offsetOf(Header, "generation") == 16);
    std.debug.assert(@offsetOf(Header, "target_window") == 48);
    std.debug.assert(@offsetOf(Header, "enabled") == 68);
    std.debug.assert(@offsetOf(Header, "swap_chain_width") == 72);
    std.debug.assert(@offsetOf(Header, "publisher_process_id") == 80);
}

pub const Size = struct { width: i32, height: i32 };

pub const Section = struct {
    mapping: c.HANDLE,
    view: [*]u8,
    mapped_bytes: usize,

    fn header(self: *const Section) *Header {
        return @ptrCast(@alignCast(self.view));
    }

    /// `prefix` is `product_prefix` in the product and a test-only name in tests.
    pub fn create(prefix: []const u8, process_id: u32, start_key: u64, target_window: usize) !Section {
        if (process_id == 0 or start_key == 0 or start_key > std.math.maxInt(i64)) return error.InvalidOverlayTarget;
        var name_bytes: [256]u8 = undefined;
        const name = try std.fmt.bufPrint(&name_bytes, "{s}.{X:0>8}.{X:0>16}", .{ prefix, process_id, start_key });
        var name_wide: [256:0]u16 = undefined;
        const name_length = try std.unicode.utf8ToUtf16Le(name_wide[0 .. name_wide.len - 1], name);
        name_wide[name_length] = 0;
        var sddl_bytes: [256]u8 = undefined;
        var sid_bytes: [192]u8 = undefined;
        const sddl = try std.fmt.bufPrint(&sddl_bytes, "D:P(A;;GA;;;SY)(A;;GA;;;{s})", .{try userSid(&sid_bytes)});
        var sddl_wide: [256:0]u16 = undefined;
        const sddl_length = try std.unicode.utf8ToUtf16Le(sddl_wide[0 .. sddl_wide.len - 1], sddl);
        sddl_wide[sddl_length] = 0;
        var descriptor: c.PSECURITY_DESCRIPTOR = null;
        if (c.ConvertStringSecurityDescriptorToSecurityDescriptorW(&sddl_wide, c.SDDL_REVISION_1, &descriptor, null) == 0) return error.OverlaySectionSecurity;
        defer _ = c.LocalFree(descriptor);
        var security: c.SECURITY_ATTRIBUTES = .{ .nLength = @sizeOf(c.SECURITY_ATTRIBUTES), .lpSecurityDescriptor = descriptor, .bInheritHandle = c.FALSE };
        const bytes: u64 = header_bytes + maximum_pixel_bytes;
        const mapping = c.CreateFileMappingW(c.INVALID_HANDLE_VALUE, &security, c.PAGE_READWRITE, @intCast(bytes >> 32), @truncate(bytes), &name_wide) orelse return error.OverlaySectionCreate;
        const existing = c.GetLastError() == c.ERROR_ALREADY_EXISTS;
        errdefer _ = c.CloseHandle(mapping);
        const raw = c.MapViewOfFile(mapping, c.FILE_MAP_WRITE | c.FILE_MAP_READ, 0, 0, 0) orelse return error.OverlaySectionMap;
        const view: [*]u8 = @ptrCast(raw);
        errdefer _ = c.UnmapViewOfFile(raw);
        var information: c.MEMORY_BASIC_INFORMATION = undefined;
        if (c.VirtualQuery(view, &information, @sizeOf(c.MEMORY_BASIC_INFORMATION)) == 0) return error.OverlaySectionQuery;
        if (information.RegionSize < bytes) return error.OverlaySectionTooSmall;
        var self: Section = .{ .mapping = mapping, .view = view, .mapped_bytes = information.RegionSize };
        const value = self.header();
        if (existing) {
            if (value.magic != magic or value.version != version or value.header_bytes != header_bytes or value.pixel_capacity != maximum_pixel_bytes) return error.IncompatibleOverlaySection;
            @atomicStore(i32, &value.enabled, handshake, .seq_cst);
            value.publisher_process_id = c.GetCurrentProcessId();
            self.setTargetWindow(target_window);
            return self;
        }
        value.* = std.mem.zeroes(Header);
        value.magic = magic;
        value.version = version;
        value.header_bytes = header_bytes;
        value.pixel_capacity = maximum_pixel_bytes;
        value.enabled = handshake;
        value.target_window = target_window;
        value.publisher_process_id = c.GetCurrentProcessId();
        return self;
    }

    pub fn setTargetWindow(self: *Section, window: usize) void {
        const value = self.header();
        if (value.target_window == window) return;
        value.target_window = window;
        @atomicStore(i32, &value.swap_chain_width, 0, .seq_cst);
        @atomicStore(i32, &value.swap_chain_height, 0, .seq_cst);
    }

    /// The in-game module reports its swap chain size here.
    pub fn swapChainSize(self: *const Section) Size {
        // RuntimeRead: Sampled
        const value = self.header();
        return .{ .width = @atomicLoad(i32, &value.swap_chain_width, .seq_cst), .height = @atomicLoad(i32, &value.swap_chain_height, .seq_cst) };
    }

    /// Odd generation while the bitmap changes, the next even value once complete.
    pub fn publish(self: *Section, pixels: []const u8, width: i32, height: i32, stride: i32) !void {
        if (width <= 0 or height <= 0 or width > points.overlay_maximum_dimension or height > points.overlay_maximum_dimension or stride < width * 4) return error.InvalidOverlayImage;
        const length: usize = @as(usize, @intCast(stride)) * @as(usize, @intCast(height));
        if (pixels.len != length or length > maximum_pixel_bytes or length > self.mapped_bytes - header_bytes) return error.InvalidOverlayImage;
        const value = self.header();
        const next_odd = (@atomicLoad(i64, &value.generation, .seq_cst) & ~@as(i64, 1)) + 1;
        @atomicStore(i64, &value.generation, next_odd, .seq_cst);
        value.width = @intCast(width);
        value.height = @intCast(height);
        value.stride = @intCast(stride);
        @memcpy(self.view[header_bytes..][0..length], pixels);
        @atomicStore(i64, &value.generation, next_odd + 1, .seq_cst);
        @atomicStore(i32, &value.enabled, enabled, .seq_cst);
    }

    pub fn disable(self: *Section) void {
        @atomicStore(i32, &self.header().enabled, disabled, .seq_cst);
    }

    pub fn deinit(self: *Section) void {
        self.disable();
        _ = c.UnmapViewOfFile(self.view);
        _ = c.CloseHandle(self.mapping);
    }
};

/// The current user's SID string.
pub fn userSid(output: []u8) ![]const u8 {
    var token: c.HANDLE = null;
    if (c.OpenProcessToken(c.GetCurrentProcess(), c.TOKEN_QUERY, &token) == 0) return error.UserIdentity;
    defer _ = c.CloseHandle(token);
    var token_buffer: [512]u8 align(@alignOf(c.TOKEN_USER)) = undefined;
    var needed: c.DWORD = 0;
    if (c.GetTokenInformation(token, c.TokenUser, &token_buffer, token_buffer.len, &needed) == 0) return error.UserIdentity;
    const user: *const c.TOKEN_USER = @ptrCast(@alignCast(&token_buffer));
    var sid: [*c]u16 = null;
    if (c.ConvertSidToStringSidW(user.User.Sid, &sid) == 0) return error.UserIdentity;
    defer _ = c.LocalFree(@ptrCast(sid));
    const length = try std.unicode.utf16LeToUtf8(output, std.mem.span(@as([*:0]const u16, @ptrCast(sid))));
    return output[0..length];
}

test "a created section carries the protocol header and publishes even generations" {
    var prefix: [96]u8 = undefined;
    const name = try std.fmt.bufPrint(&prefix, "Local\\ResourceManager.Desktop.OverlayTest.{d}", .{c.GetCurrentProcessId()});
    var section = try Section.create(name, 1234, 0x01DC_0000_1111_2222, 0x5678);
    defer section.deinit();
    const value = section.header();
    try std.testing.expectEqual(magic, value.magic);
    try std.testing.expectEqual(@as(u64, 0x5678), value.target_window);
    try std.testing.expectEqual(handshake, value.enabled);
    value.swap_chain_width = 640;
    value.swap_chain_height = 480;
    section.setTargetWindow(0x5678);
    try std.testing.expectEqual(@as(i32, 640), section.swapChainSize().width);
    section.setTargetWindow(0x9abc);
    try std.testing.expectEqual(@as(i32, 0), section.swapChainSize().width);
    const pixels = [_]u8{ 1, 2, 3, 4, 5, 6, 7, 8 };
    try section.publish(&pixels, 2, 1, 8);
    try std.testing.expectEqual(@as(i64, 2), value.generation);
    try std.testing.expectEqual(enabled, value.enabled);
    try std.testing.expectEqualSlices(u8, &pixels, section.view[header_bytes..][0..8]);
    try section.publish(&pixels, 2, 1, 8);
    try std.testing.expectEqual(@as(i64, 4), value.generation);
    try std.testing.expectError(error.InvalidOverlayImage, section.publish(&pixels, 3, 1, 8));
    section.disable();
    try std.testing.expectEqual(disabled, value.enabled);
    // Reopening keeps the header and returns to the handshake state.
    var again = try Section.create(name, 1234, 0x01DC_0000_1111_2222, 0x9abc);
    defer again.deinit();
    try std.testing.expectEqual(handshake, again.header().enabled);
    try std.testing.expectEqual(@as(i64, 4), again.header().generation);
    try std.testing.expectError(error.InvalidOverlayTarget, Section.create(name, 1, 0x8000_0000_0000_0000, 0));
}
