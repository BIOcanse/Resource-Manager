//! Native-owned report failure vocabulary. Error output has its own synchronous
//! callback so an incomplete success document can always be discarded by a host.
const std = @import("std");

pub const Write = *const fn (?*anyopaque, [*]const u8, usize) callconv(.c) i32;
pub const Failure = struct {
    status: i32,
    reason: []const u8,
    detail: []const u8,
};

pub fn describe(err: anyerror) Failure {
    const selected: struct { i32, []const u8 } = switch (err) {
        error.NotFound => .{ 404, "freeze-review-not-found" },
        error.Busy => .{ 409, "freeze-reports-busy" },
        error.AlreadyStarted => .{ 409, "freeze-reports-already-started" },
        error.RingMigrationRequired => .{ 409, "freeze-ring-migration-required" },
        error.InvalidConfig, error.InvalidConfiguration => .{ 400, "freeze-settings-invalid" },
        error.InvalidPath => .{ 400, "freeze-recorder-path-invalid" },
        error.InvalidOperation => .{ 400, "freeze-operation-invalid" },
        error.InvalidProvider => .{ 400, "freeze-event-provider-invalid" },
        error.SyntaxError,
        error.UnexpectedToken,
        error.UnknownField,
        error.MissingField,
        error.InvalidNumber,
        error.InvalidCharacter,
        error.Overflow,
        error.LengthMismatch,
        error.InvalidEnumTag,
        error.DuplicateField,
        error.UnexpectedEndOfInput,
        => .{ 400, "freeze-request-invalid-json" },
        error.OutOfMemory => .{ 503, "freeze-reports-memory-unavailable" },
        error.DocumentCapacityExceeded => .{ 503, "freeze-reports-document-capacity-exceeded" },
        error.WorkspaceUnavailable => .{ 503, "freeze-reports-workspace-unavailable" },
        error.SubscribeAccessDenied => .{ 500, "freeze-event-subscribe-access-denied" },
        error.SubscribeInvalidParameter => .{ 500, "freeze-event-subscribe-invalid-parameter" },
        error.SubscribeBookmarkNotFound => .{ 500, "freeze-event-subscribe-bookmark-not-found" },
        error.SubscribeInvalidQuery => .{ 500, "freeze-event-subscribe-invalid-query" },
        error.SubscribeChannelNotFound => .{ 500, "freeze-event-subscribe-channel-not-found" },
        error.StaleSubscription => .{ 500, "freeze-event-subscription-stale" },
        else => .{ 500, "freeze-reports-failed" },
    };
    return .{ .status = selected[0], .reason = selected[1], .detail = @errorName(err) };
}

/// For integer-only lifecycle calls and a failed/missing error transfer. This
/// projection is stateless; no owner or thread retains a mutable last error.
pub fn fromStatus(code: i32) Failure {
    return switch (code) {
        400 => .{ .status = 400, .reason = "freeze-request-invalid", .detail = "InvalidRequest" },
        404 => describe(error.NotFound),
        409 => .{ .status = 409, .reason = "freeze-reports-conflict", .detail = "Conflict" },
        503 => .{ .status = 503, .reason = "freeze-reports-unavailable", .detail = "Unavailable" },
        else => .{ .status = 500, .reason = "freeze-reports-failed", .detail = "NativeFailure" },
    };
}

pub fn emit(failure: Failure, write: Write, context: ?*anyopaque) i32 {
    // This stack-only transport buffer is independent of the failed report
    // workspace. All vocabulary is static; detail is the native error name.
    var buffer: [1024]u8 = undefined;
    var writer = std.Io.Writer.fixed(&buffer);
    std.json.Stringify.value(.{
        .@"error" = failure.reason,
        .reasonCode = failure.reason,
        .detail = failure.detail,
    }, .{}, &writer) catch return -1;
    return write(context, buffer[0..writer.end].ptr, writer.end);
}

test {
    _ = @import("errors_tests.zig");
}
