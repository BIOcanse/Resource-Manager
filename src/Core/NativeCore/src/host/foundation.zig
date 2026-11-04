//! Passive bootstrap before settings, Control grants, or product work.
//! Paths are borrowed for this scope; no logger/settings initialization cycle.
pub const clock = @import("rm_windows_clock");
pub const identities = @import("runtime_identity.zig");
pub const diagnostics = @import("diagnostic_sink.zig");

pub const Foundation = struct {
    identity: identities.Identity,
    frequency: clock.Frequency,
    diagnostic_sink: ?diagnostics.Sink,

    pub fn init(path: [:0]const u16, archive: [:0]const u16, rotate_bytes: u64) !Foundation {
        const sink = try diagnostics.Sink.init(path, archive, rotate_bytes);
        const frequency = try clock.frequency();
        const identity = try identities.capture();
        return .{ .identity = identity, .frequency = frequency, .diagnostic_sink = sink };
    }

    pub fn initFrom(path: [:0]const u16, archive: [:0]const u16, rotate_bytes: u64, frequency: clock.Frequency, source: identities.Source) !Foundation {
        const sink = try diagnostics.Sink.init(path, archive, rotate_bytes);
        if (frequency.ticks_per_second == 0) return error.ClockUnavailable;
        const identity = try identities.captureFrom(source);
        return .{ .identity = identity, .frequency = frequency, .diagnostic_sink = sink };
    }

    pub fn deinit(self: *Foundation) void {
        // All acquisitions are values or synchronous single-use handles.
        // The composing host drains its domain runs BEFORE ending this scope.
        self.identity = .{};
        self.frequency = .{ .ticks_per_second = 0 };
        self.diagnostic_sink = null;
    }
};
