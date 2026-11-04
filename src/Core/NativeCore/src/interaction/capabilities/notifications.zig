//! Notification read selection. Permission admission belongs only to P-access.
const std = @import("std");
pub const contract = @import("../contracts/notifications.zig");
pub fn read(source: contract.Source) !usize {
    var count: usize = 0;
    if (source.read(source.context, &count) != 0) return error.SourceFailure;
    return count;
}
const Selection = struct {
    visible: bool = false,
    fn receive(context: ?*anyopaque, text: ?[*]const u16, length: usize) callconv(.c) i32 {
        const self: *Selection = @ptrCast(@alignCast(context.?));
        const value = (text orelse return 1)[0..length];
        // Approved source partition: ordinary read. Removed access-request and
        // unregistered kinds cannot disclose facts through this projection.
        inline for (.{ "memory-protection.action", "update-available", "crash-report" }) |name| {
            if (value.len == name.len) {
                var equal = true;
                for (value, name) |a, b| if (a != b) {
                    equal = false;
                    break;
                };
                if (equal) self.visible = true;
            }
        }
        return 0;
    }
};
pub fn visible(source: contract.Source, index: usize) !bool {
    var selection: Selection = .{};
    if (source.kind(source.context, index, &selection, Selection.receive) != 0) return error.SourceFailure;
    return selection.visible;
}
