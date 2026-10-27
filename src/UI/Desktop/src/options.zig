const std = @import("std");
const w = @import("win32.zig");
const c = w.c;
const instance = @import("shell/instance.zig");
pub const Options = struct {
    package_root: []const u8 = "",
    window_executable: []const u8 = "",
    child_arguments: []const u8 = "",
    backend_path: []const u8 = "",
    backend_service: []const u8 = "ResourceManager.Service",
    backend_port: u16 = 9321,
    pipe_name: []const u8 = instance.default_pipe,
    command: instance.Command = .show,
    diagnostics: []const u8 = "",
    preferences_path: ?[]const u8 = null,
    // Storage is structural and bounded by the Windows command-line/path contract.
    storage: [256 * 1024]u8 = undefined,
    used: usize = 0,
    pub fn parse(self: *Options) !void {
        var count: c_int = 0;
        const args = c.CommandLineToArgvW(c.GetCommandLineW(), &count);
        if (args == null) return error.CommandLine;
        defer _ = c.LocalFree(@ptrCast(args));
        var index: usize = 1;
        while (index < @as(usize, @intCast(count))) : (index += 1) {
            const argument = try self.copyWide(std.mem.span(@as([*:0]const u16, @ptrCast(args[index]))));
            if (std.mem.eql(u8, argument, "--background-startup")) {
                self.command = .ensure_running;
                continue;
            }
            if (std.mem.eql(u8, argument, "--exit-existing")) {
                self.command = .exit;
                continue;
            }
            if (std.mem.eql(u8, argument, "--show-existing")) {
                self.command = .show;
                continue;
            }
            if (std.mem.eql(u8, argument, "--show-existing-passive")) {
                self.command = .show_passive;
                continue;
            }
            if (std.mem.eql(u8, argument, "--show-existing-passive-secondary")) {
                self.command = .show_passive_secondary;
                continue;
            }
            index += 1;
            if (index >= @as(usize, @intCast(count))) return error.MissingArgument;
            const value = try self.copyWide(std.mem.span(@as([*:0]const u16, @ptrCast(args[index]))));
            if (std.mem.eql(u8, argument, "--package-root")) self.package_root = value else if (std.mem.eql(u8, argument, "--window-executable")) self.window_executable = value else if (std.mem.eql(u8, argument, "--child-arguments")) self.child_arguments = value else if (std.mem.eql(u8, argument, "--backend-path")) self.backend_path = value else if (std.mem.eql(u8, argument, "--backend-service")) self.backend_service = value else if (std.mem.eql(u8, argument, "--backend-port")) self.backend_port = try std.fmt.parseInt(u16, value, 10) else if (std.mem.eql(u8, argument, "--instance-pipe")) self.pipe_name = value else if (std.mem.eql(u8, argument, "--diagnostics")) self.diagnostics = value else if (std.mem.eql(u8, argument, "--preferences-path")) self.preferences_path = value else return error.UnknownArgument;
        }
        var image: [32768]u16 = undefined;
        const length = c.GetModuleFileNameW(null, &image, image.len);
        if (length == 0 or length >= image.len) return error.ExecutablePath;
        const executable = try self.copyWide(image[0..length]);
        const directory = std.fs.path.dirname(executable) orelse return error.ExecutablePath;
        // Packaging step 6 supplies the product paths. Development uses explicit
        // arguments; the sole default layout is siblings beside this executable.
        if (self.package_root.len == 0) self.package_root = directory;
        if (self.window_executable.len == 0) self.window_executable = try self.format("{s}\\ResourceManager.NativeUi.exe", .{directory});
        if (self.backend_path.len == 0) self.backend_path = try self.format("{s}\\ResourceManager.exe", .{directory});
        if (self.backend_port == 0) return error.InvalidPort;
    }
    pub fn format(self: *Options, comptime pattern: []const u8, args: anytype) ![]const u8 {
        const result = try std.fmt.bufPrint(self.storage[self.used..], pattern, args);
        self.used += result.len;
        return result;
    }
    fn copyWide(self: *Options, text: []const u16) ![]const u8 {
        const written = try std.unicode.utf16LeToUtf8(self.storage[self.used..], text);
        const result = self.storage[self.used..][0..written];
        self.used += written;
        return result;
    }
};
