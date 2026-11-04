const std = @import("std");
const errors = @import("errors.zig");

const Capture = struct {
    bytes: [1024]u8 = undefined,
    length: usize = 0,
    fn write(context: ?*anyopaque, bytes: [*]const u8, length: usize) callconv(.c) i32 {
        const self: *Capture = @ptrCast(@alignCast(context.?));
        if (length > self.bytes.len) return -1;
        @memcpy(self.bytes[0..length], bytes[0..length]);
        self.length = length;
        return 0;
    }
};

test "freeze report errors retain stable reason and original detail for every response status" {
    const cases = .{
        .{ error.InvalidConfiguration, 400, "freeze-settings-invalid" },
        .{ error.InvalidProvider, 400, "freeze-event-provider-invalid" },
        .{ error.SyntaxError, 400, "freeze-request-invalid-json" },
        .{ error.NotFound, 404, "freeze-review-not-found" },
        .{ error.Busy, 409, "freeze-reports-busy" },
        .{ error.RingMigrationRequired, 409, "freeze-ring-migration-required" },
        .{ error.WorkspaceUnavailable, 503, "freeze-reports-workspace-unavailable" },
        .{ error.DocumentCapacityExceeded, 503, "freeze-reports-document-capacity-exceeded" },
        .{ error.OpenFailed, 500, "freeze-reports-failed" },
        .{ error.OutputFailed, 500, "freeze-reports-failed" },
    };
    inline for (cases) |case| {
        const failure = errors.describe(case[0]);
        try std.testing.expectEqual(@as(i32, case[1]), failure.status);
        try std.testing.expectEqualStrings(case[2], failure.reason);
        var capture: Capture = .{};
        try std.testing.expectEqual(@as(i32, 0), errors.emit(failure, Capture.write, &capture));
        const parsed = try std.json.parseFromSlice(struct { @"error": []const u8, reasonCode: []const u8, detail: []const u8 }, std.testing.allocator, capture.bytes[0..capture.length], .{});
        defer parsed.deinit();
        try std.testing.expectEqualStrings(case[2], parsed.value.@"error");
        try std.testing.expectEqualStrings(case[2], parsed.value.reasonCode);
        try std.testing.expectEqualStrings(@errorName(case[0]), parsed.value.detail);
    }
}

test "freeze report integer fallback is stateless and normalizes unknown status" {
    inline for (.{ 400, 404, 409, 503, 500 }) |code| {
        const failure = errors.fromStatus(code);
        try std.testing.expectEqual(@as(i32, code), failure.status);
        try std.testing.expect(failure.reason.len != 0 and failure.detail.len != 0);
    }
    try std.testing.expectEqual(@as(i32, 500), errors.fromStatus(0).status);
    try std.testing.expectEqual(@as(i32, 500), errors.fromStatus(599).status);
}
