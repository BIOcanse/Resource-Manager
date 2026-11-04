const std = @import("std");
const m = @import("model.zig");
pub const Rule = struct {
    channel: m.Channel,
    provider: []const u8,
    ids: []const u32,
    category: m.Category,
    target_kind: m.TargetKind,
};
// F3 contract defaults, verified with read-only publisher/log evidence; see fixtures/README.md.
// Missing local history or classic manifest IDs does not disable a contract rule.
// The caller owns/persists its editable rule table. Empty IDs means all publisher IDs.
pub const defaults = [_]Rule{
    .{ .channel = .system, .provider = "disk", .ids = &.{ 7, 11, 51, 153 }, .category = .storage_retry_reset, .target_kind = .physical_disk },
    .{ .channel = .system, .provider = "storport", .ids = &.{129}, .category = .storage_retry_reset, .target_kind = .storage_port },
    .{ .channel = .system, .provider = "stornvme", .ids = &.{129}, .category = .storage_retry_reset, .target_kind = .storage_port },
    .{ .channel = .system, .provider = "UASPStor", .ids = &.{129}, .category = .storage_retry_reset, .target_kind = .storage_port },
    .{ .channel = .system, .provider = "Ntfs", .ids = &.{ 50, 140 }, .category = .storage_retry_reset, .target_kind = .volume },
    // Windows' manifest publisher for Ntfs 140 differs from the classic source name.
    .{ .channel = .system, .provider = "Microsoft-Windows-Ntfs", .ids = &.{140}, .category = .storage_retry_reset, .target_kind = .volume },
    .{ .channel = .system, .provider = "Display", .ids = &.{4101}, .category = .gpu_reset, .target_kind = .gpu_device },
    .{ .channel = .system, .provider = "nvlddmkm", .ids = &.{ 13, 153 }, .category = .gpu_reset, .target_kind = .gpu_device },
    .{ .channel = .system, .provider = "Microsoft-Windows-Resource-Exhaustion-Detector", .ids = &.{2004}, .category = .memory_exhausted, .target_kind = .machine },
    .{ .channel = .application, .provider = "Application Error", .ids = &.{1000}, .category = .desktop_crash, .target_kind = .desktop_process },
    .{ .channel = .application, .provider = "Application Hang", .ids = &.{1002}, .category = .desktop_crash, .target_kind = .desktop_process },
    .{ .channel = .system, .provider = "Microsoft-Windows-WHEA-Logger", .ids = &.{}, .category = .hardware_error, .target_kind = .hardware },
    .{ .channel = .system, .provider = "Microsoft-Windows-Kernel-Power", .ids = &.{41}, .category = .unexpected_shutdown, .target_kind = .machine },
    .{ .channel = .system, .provider = "EventLog", .ids = &.{6008}, .category = .unexpected_shutdown, .target_kind = .machine },
    .{ .channel = .system, .provider = "Microsoft-Windows-WER-SystemErrorReporting", .ids = &.{1001}, .category = .unexpected_shutdown, .target_kind = .machine },
};
pub fn match(table: []const Rule, channel: m.Channel, provider: []const u8, id: u32) ?Rule {
    for (table) |rule| {
        if (rule.channel != channel or !std.ascii.eqlIgnoreCase(rule.provider, provider)) continue;
        if (rule.ids.len == 0 or std.mem.findScalar(u32, rule.ids, id) != null) return rule;
    }
    return null;
}
/// Construct XPath only from validated publisher names and numeric IDs, never
/// interpolate an unchecked XPath fragment from settings.
pub fn xpath(allocator: std.mem.Allocator, table: []const Rule, channel: m.Channel) ![:0]u16 {
    var out: std.ArrayList(u8) = .empty;
    defer out.deinit(allocator);
    try out.appendSlice(allocator, "*[System[");
    var count: usize = 0;
    for (table) |rule| {
        if (rule.channel != channel) continue;
        if (rule.provider.len == 0) return error.InvalidProvider;
        for (rule.provider) |c| if (!(std.ascii.isAlphanumeric(c) or c == '-' or c == '_' or c == '.' or c == ' ')) return error.InvalidProvider;
        if (count != 0) try out.appendSlice(allocator, " or ");
        try out.appendSlice(allocator, "(Provider[@Name='");
        try out.appendSlice(allocator, rule.provider);
        try out.appendSlice(allocator, "']");
        if (rule.ids.len != 0) {
            try out.appendSlice(allocator, " and (");
            for (rule.ids, 0..) |id, i| {
                if (i != 0) try out.appendSlice(allocator, " or ");
                const text = try std.fmt.allocPrint(allocator, "EventID={d}", .{id});
                defer allocator.free(text);
                try out.appendSlice(allocator, text);
            }
            try out.append(allocator, ')');
        }
        try out.append(allocator, ')');
        count += 1;
    }
    if (count == 0) try out.appendSlice(allocator, "EventID=4294967295");
    try out.appendSlice(allocator, "]]");
    return std.unicode.utf8ToUtf16LeAllocZ(allocator, out.items);
}
