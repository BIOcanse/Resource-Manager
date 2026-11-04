const std = @import("std");

// All text borrows the caller's original UTF-16 string for this synchronous call.
// identity belongs to that caller; neither it nor data is retained by this module.
pub const Text = extern struct {
    data: ?[*]const u16 = null,
    len: usize = 0,
    identity: usize = 0,

    pub fn slice(self: Text) []const u16 {
        return if (self.data) |data| data[0..self.len] else &.{};
    }
};

pub const Value = enum(u32) { service_name, install_root, backend, native_ui, launcher, desktop };
pub const Operation = enum(u32) { full_path, resident_for, relative_path, is_path_rooted, equal_ignore_case, file_exists };
pub const Status = enum(u32) { success, invalid_registration, missing_value, program_missing, bridge_failure };

// Registry open/close remains in the caller. A missing/non-string value is Text{}.
// Each callback returns zero on success, nonzero after capturing a typed exception.
pub const Host = extern struct {
    context: ?*anyopaque,
    expected_service: Text,
    read_value: ?*const fn (?*anyopaque, u32, *Text) callconv(.c) u32,
    operation: ?*const fn (?*anyopaque, u32, *const Text, *const Text, *Text, *u32) callconv(.c) u32,
};

pub const Result = extern struct {
    status: u32 = @backingInt(Status.success),
    missing_value: u32 = @backingInt(Value.service_name),
    missing_path: Text = .{},
    root: Text = .{},
    backend: Text = .{},
    native_ui: Text = .{},
    launcher: Text = .{},
};

pub fn read(host: *const Host) Result {
    var result: Result = .{};
    readInner(host, &result) catch |err| {
        result.status = @backingInt(switch (err) {
            error.InvalidRegistration => Status.invalid_registration,
            error.MissingValue => Status.missing_value,
            error.ProgramMissing => Status.program_missing,
            error.BridgeFailure => Status.bridge_failure,
        });
    };
    return result;
}

const Failure = error{ InvalidRegistration, MissingValue, ProgramMissing, BridgeFailure };

fn readInner(host: *const Host, result: *Result) Failure!void {
    const service = try value(host, .service_name);
    if (service.data == null or !std.mem.eql(u16, service.slice(), host.expected_service.slice()))
        return error.InvalidRegistration;

    const root = try path(host, .full_path, try required(host, .install_root, result), .{});
    const backend = try required(host, .backend, result);
    const native_ui = try required(host, .native_ui, result);
    const launcher = try required(host, .launcher, result);
    // ProductDesktopLayout is the only layout authority; do not duplicate its names.
    const resident = try path(host, .resident_for, root, .{});
    const desktop = try value(host, .desktop);
    if (desktop.data != null) {
        const registered = try path(host, .full_path, desktop, .{});
        if (!try fact(host, .equal_ignore_case, registered, resident)) return error.InvalidRegistration;
    }

    const paths = [_]Text{ backend, native_ui, resident, launcher };
    var index: usize = 0;
    while (index < paths.len) : (index += 1) {
        const candidate = paths[index];
        const relative = try path(host, .relative_path, root, candidate);
        if (try fact(host, .is_path_rooted, relative, .{}) or escapesRoot(relative.slice()) or
            !try fact(host, .file_exists, candidate, .{}))
        {
            result.missing_path = candidate;
            return error.ProgramMissing;
        }
    }
    result.root = root;
    result.backend = try path(host, .full_path, backend, .{});
    result.native_ui = try path(host, .full_path, native_ui, .{});
    result.launcher = try path(host, .full_path, launcher, .{});
}

fn value(host: *const Host, name: Value) Failure!Text {
    var output: Text = .{};
    if (host.read_value.?(host.context, @backingInt(name), &output) != 0) return error.BridgeFailure;
    return output;
}

fn required(host: *const Host, name: Value, result: *Result) Failure!Text {
    const output = try value(host, name);
    var index: usize = 0;
    while (index < output.len and isWhiteSpace(output.slice()[index])) : (index += 1) {}
    if (output.data == null or index == output.len) {
        result.missing_value = @backingInt(name);
        return error.MissingValue;
    }
    return output;
}

fn path(host: *const Host, op: Operation, first: Text, second: Text) Failure!Text {
    var output: Text = .{};
    var unused: u32 = 0;
    if (host.operation.?(host.context, @backingInt(op), &first, &second, &output, &unused) != 0)
        return error.BridgeFailure;
    return output;
}

fn fact(host: *const Host, op: Operation, first: Text, second: Text) Failure!bool {
    var unused: Text = .{};
    var output: u32 = 0;
    if (host.operation.?(host.context, @backingInt(op), &first, &second, &unused, &output) != 0)
        return error.BridgeFailure;
    return output != 0;
}

fn escapesRoot(relative: []const u16) bool {
    return relative.len >= 2 and relative[0] == '.' and relative[1] == '.' and
        (relative.len == 2 or relative[2] == '\\');
}

// System.Char.IsWhiteSpace's UTF-16 White_Space characters. These are format
// semantics, not product defaults. In particular U+FEFF/U+200B are not whitespace.
fn isWhiteSpace(unit: u16) bool {
    return switch (unit) {
        0x0009...0x000d,
        0x0020,
        0x0085,
        0x00a0,
        0x1680,
        0x2000...0x200a,
        0x2028,
        0x2029,
        0x202f,
        0x205f,
        0x3000,
        => true,
        else => false,
    };
}
