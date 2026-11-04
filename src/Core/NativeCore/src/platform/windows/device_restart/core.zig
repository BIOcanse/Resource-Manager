const std = @import("std");

// MAX_DEVICE_ID_LEN from cfgmgr32.h includes the terminating NUL.
pub const instance_id_capacity = 200;
pub const Code = enum(u32) {
    success = 0,
    confirmation_required = 1,
    invalid_instance_id = 2,
    administrator_required = 3,
    device_not_found = 4,
    device_unavailable = 5,
    disable_failed = 6,
    enable_failed = 7,
    reboot_required = 8,
    verification_failed = 9,
    driver_not_found = 10,
    ambiguous_device = 11,
    invalid_module = 12,
    enumeration_failed = 13,
    buffer_too_small = 14,
    unsupported_platform = 15,
};

pub const Result = extern struct {
    code: Code = .success,
    // Unmodified Win32 error, never localized text. Zero means no Windows error.
    detail: u32 = 0,
    restart_required: u32 = 0,
};

// The adapter owns the opened exact-instance handle only between open and close.
// Zero is success; callbacks report Win32 errors separately from stable outcomes.
pub const Adapter = struct {
    administrator: *const fn (?*anyopaque) u32,
    open: *const fn (?*anyopaque, [:0]const u16) Result,
    close: *const fn (?*anyopaque) void,
    disable: *const fn (?*anyopaque) u32,
    enable: *const fn (?*anyopaque) u32,
    verify: *const fn (?*anyopaque, *u32) u32,
};

pub fn restart(context: ?*anyopaque, adapter: Adapter, instance_id: []const u16, confirmed: bool) Result {
    if (!confirmed) return .{ .code = .confirmation_required };
    if (!validInstanceId(instance_id)) return .{ .code = .invalid_instance_id };
    const administrator_error = adapter.administrator(context);
    if (administrator_error != 0) return .{ .code = .administrator_required, .detail = administrator_error };
    var terminated: [instance_id_capacity:0]u16 = @splat(0);
    @memcpy(terminated[0..instance_id.len], instance_id);
    const opened = adapter.open(context, terminated[0..instance_id.len :0]);
    if (opened.code != .success) return opened;
    defer adapter.close(context);
    const disable_error = adapter.disable(context);
    if (disable_error != 0) return .{ .code = .disable_failed, .detail = disable_error };
    // Once disable succeeded, enable is attempted exactly once even if a reboot
    // may eventually be necessary. Never leave the device disabled by a query error.
    const enable_error = adapter.enable(context);
    if (enable_error != 0) return .{ .code = .enable_failed, .detail = enable_error, .restart_required = 1 };
    var needs_restart: u32 = 0;
    const verify_error = adapter.verify(context, &needs_restart);
    if (verify_error != 0) return .{ .code = .verification_failed, .detail = verify_error, .restart_required = 1 };
    return .{ .code = if (needs_restart != 0) .reboot_required else .success, .restart_required = @intFromBool(needs_restart != 0) };
}

pub fn validInstanceId(instance_id: []const u16) bool {
    if (instance_id.len == 0 or instance_id.len >= instance_id_capacity) return false;
    var index: usize = 0;
    while (index < instance_id.len) : (index += 1) {
        if (instance_id[index] == 0 or instance_id[index] == '*' or instance_id[index] == '?') return false;
    }
    return true;
}

pub fn equalInsensitive(left: []const u16, right: []const u16) bool {
    if (left.len != right.len) return false;
    var index: usize = 0;
    while (index < left.len) : (index += 1) {
        if (fold(left[index]) != fold(right[index])) return false;
    }
    return true;
}

fn fold(character: u16) u16 {
    return if (character >= 'A' and character <= 'Z') character + ('a' - 'A') else character;
}

pub fn moduleName(path: []const u16) []const u16 {
    var start: usize = 0;
    var end = path.len;
    if (end > 1 and path[0] == '"' and path[end - 1] == '"') {
        start = 1;
        end -= 1;
    }
    var index = start;
    while (index < end) : (index += 1) {
        if (path[index] == '\\' or path[index] == '/') start = index + 1;
    }
    return path[start..end];
}

pub fn validModule(module: []const u16) bool {
    if (module.len <= ".sys".len) return false;
    var index: usize = 0;
    while (index < module.len) : (index += 1) {
        if (module[index] == 0 or module[index] == '\\' or module[index] == '/' or module[index] == '*' or module[index] == '?' or module[index] == '"') return false;
    }
    const suffix = [_]u16{ '.', 's', 'y', 's' };
    return equalInsensitive(module[module.len - suffix.len ..], &suffix);
}

pub fn driverModule(path: []const u16) ?[]const u16 {
    const name = moduleName(path);
    return if (validModule(name)) name else null;
}

// Duplicate observations of the same device do not make an exact identity
// ambiguous; distinct device instances always do. There is no first-match fallback.
pub const UniqueMatch = struct {
    id: [instance_id_capacity]u16 = @splat(0),
    length: usize = 0,
    ambiguous: bool = false,

    pub fn offer(self: *UniqueMatch, instance_id: []const u16) void {
        if (!validInstanceId(instance_id)) {
            self.ambiguous = true;
            return;
        }
        if (self.length == 0) {
            @memcpy(self.id[0..instance_id.len], instance_id);
            self.length = instance_id.len;
        } else if (!equalInsensitive(self.id[0..self.length], instance_id)) self.ambiguous = true;
    }

    pub fn finish(self: *const UniqueMatch, output: []u16) Result {
        if (self.ambiguous) return .{ .code = .ambiguous_device };
        if (self.length == 0) return .{ .code = .driver_not_found };
        if (output.len <= self.length) return .{ .code = .buffer_too_small, .detail = 122 };
        @memcpy(output[0..self.length], self.id[0..self.length]);
        output[self.length] = 0;
        return .{};
    }
};
