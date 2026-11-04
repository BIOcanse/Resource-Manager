//! Callback fixture for invoking action functions without installing input hooks.
const lookup = @import("text/lookup.zig");
const Definition = @import("hotkeys/definition.zig").Definition;

pub const Fixture = struct {
    balloons: usize = 0,
    last: lookup.Balloon = .{},
    pub fn host(self: *Fixture, comptime T: type) T {
        return .{ .context = self, .running = running, .binding = binding, .balloon = balloon, .text = text, .backend_ready = ready };
    }
    fn running(_: *anyopaque) void {}
    fn binding(_: *anyopaque) Definition {
        return .{};
    }
    fn text(_: *anyopaque) *const lookup.Text {
        return lookup.select("en-US");
    }
    fn ready(_: *anyopaque) bool {
        return true;
    }
    fn balloon(raw: *anyopaque, value: *const lookup.Balloon) void {
        const self: *Fixture = @ptrCast(@alignCast(raw));
        self.last = value.*;
        self.balloons += 1;
    }
};
