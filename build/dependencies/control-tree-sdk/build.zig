const std = @import("std");

/// Restore a pinned, length/SHA-256 verified package into Zig's build cache.
pub fn source(b: *std.Build, repository: std.Build.LazyPath) std.Build.LazyPath {
    // Used only by migration equivalence to run the identical consumers against
    // the pre-migration source captured in a registered worker workspace.
    if (b.option([]const u8, "control-source-root", "Controller source for migration equivalence")) |root|
        return .{ .cwd_relative = root };
    const powershell = b.option([]const u8, "control-powershell", "PowerShell executable for SDK restore") orelse "pwsh";
    const restore = b.addSystemCommand(&.{ powershell, "-NoProfile", "-NonInteractive", "-File" });
    restore.addFileArg(repository.path(b, "build/scripts/build/Restore-ControlTreeSdk.ps1"));
    restore.addFileInput(repository.path(b, "build/dependencies/control-tree-sdk/dependency-manifest.json"));
    restore.addFileInput(repository.path(b, "build/dependencies/control-tree-sdk/manifest.0.1.0-dev.1.json"));
    restore.addFileInput(repository.path(b, "build/dependencies/control-tree-sdk/ControlTree.Sources.0.1.0-dev.1.zip"));
    restore.addArg("-Output");
    return restore.addOutputDirectoryArg("control-tree-sdk").path(b, "controller");
}

pub fn build(_: *std.Build) void {}
