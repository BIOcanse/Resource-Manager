pub const installation = @import("installation.zig");

// Leaf registration: the shared Desktop export root imports this file once.
pub export fn rm_launcher_installation_read(host: ?*const installation.Host, result: ?*installation.Result) callconv(.c) u32 {
    const source = host orelse return 1;
    const target = result orelse return 1;
    if (source.read_value == null or source.operation == null or source.expected_service.data == null) return 1;
    target.* = installation.read(source);
    return 0;
}
