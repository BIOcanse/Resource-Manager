//! Explicit ordinary-user acceptance after Initialize-BackendKeyFixture.ps1.
const std = @import("std");
const c = @import("c.zig").win;
const Reader = @import("key_reader.zig").Reader;
const wide = std.unicode.utf8ToUtf16LeStringLiteral;
test "prepared trusted and untrusted key storage are validated on every read" {
    @setEvalBranchQuota(10000);
    // Initialize-BackendKeyFixture.ps1 prepares protected storage with an
    // administrator token. This test only reads it under its ordinary token.
    const root = wide("F:\\code\\Resource-Manager-Development\\desktop-resident\\backend-key-fixture");
    const untrusted = wide("F:\\code\\Resource-Manager-Development\\desktop-resident\\backend-key-fixture\\untrusted");
    const valid = "rm_sk_" ++ "abcdef0123456789" ** 4;
    const stop = c.CreateEventW(null, 1, 0, null) orelse return error.EventUnavailable;
    defer _ = c.CloseHandle(stop);
    const memory = c.VirtualAlloc(null, @sizeOf(Reader), c.MEM_RESERVE | c.MEM_COMMIT, c.PAGE_READWRITE) orelse return error.OutOfMemory;
    defer _ = c.VirtualFree(memory, 0, c.MEM_RELEASE);
    const reader: *Reader = @ptrCast(@alignCast(memory));
    reader.* = .{};
    var key = (try reader.read(root, stop)) orelse return error.TrustedFixtureNotPrepared;
    defer key.clear();
    try std.testing.expectEqualStrings(valid, key.value());
    try std.testing.expectError(error.UnsupportedAcl, reader.read(untrusted, stop));
    // Reusing the same reader after rejection must validate trusted storage
    // again, without retaining the other package's value or failure.
    var again = (try reader.read(root, stop)) orelse return error.TrustedFixtureNotPrepared;
    defer again.clear();
    try std.testing.expectEqualStrings(valid, again.value());
    _ = c.SetEvent(stop);
    try std.testing.expectError(error.Stopped, reader.read(root, stop));
}
