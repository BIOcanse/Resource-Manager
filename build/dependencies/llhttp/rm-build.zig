const std = @import("std");

// Imported by NativeCore's build owner. No download, code generation or DLL.
pub fn addTo(b: *std.Build, module: *std.Build.Module, root: std.Build.LazyPath) void {
    module.addIncludePath(root.path(b, "include"));
    module.addCSourceFiles(.{
        .root = root,
        .files = &.{ "src/api.c", "src/http.c", "src/llhttp.c" },
        .flags = &.{"-std=c99"},
    });
    module.link_libc = true;
}
