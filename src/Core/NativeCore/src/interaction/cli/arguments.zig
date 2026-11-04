//! CLI syntax only. Capability schemas own parameter names, types and defaults.
const std = @import("std");
const Allocator = std.mem.Allocator;

pub const Format = enum { text, json };
pub const default_format: Format = .text;
pub const Command = enum { help, capabilities, call, query, watch };
pub const Parameter = struct { name: []const u8, value: []const u8 };

pub const Options = struct {
    command: Command,
    operation: ?[]const u8 = null,
    format: Format = default_format,
    input_file: ?[]const u8 = null,
    parameters: []const Parameter,
    /// Original tokens after watch's operation ID, including any --format.
    /// CLI2 owns their interpretation; CLI1 does not validate watch options.
    watch_args: []const []const u8 = &.{},

    /// Only the parameter array is allocated. Text and watch_args borrow argv,
    /// which the caller must keep alive until it finishes using these options.
    pub fn deinit(self: *Options, allocator: Allocator) void {
        allocator.free(self.parameters);
        self.* = undefined;
    }
};

const Flag = struct {
    name: []const u8,
    inline_value: ?[]const u8,
};

fn splitFlag(token: []const u8) !Flag {
    const body = token[2..];
    const separator = std.mem.indexOfScalar(u8, body, '=');
    const name = if (separator) |index| body[0..index] else body;
    if (name.len == 0 or name[0] == '-') return error.InvalidArgument;
    return .{
        .name = name,
        .inline_value = if (separator) |index| body[index + 1 ..] else null,
    };
}

fn flagValue(flag: Flag, argv: []const []const u8, index: *usize) ![]const u8 {
    if (flag.inline_value) |value| return value;
    if (index.* + 1 >= argv.len) return error.MissingValue;
    const value = argv[index.* + 1];
    // Negative numbers remain values. A following long option is never an
    // implicit true or a value; use --name=--literal for such string values.
    if (std.mem.startsWith(u8, value, "--")) return error.MissingValue;
    index.* += 1;
    return value;
}

fn parseCommand(token: []const u8) !Command {
    if (std.mem.eql(u8, token, "help") or std.mem.eql(u8, token, "--help")) return .help;
    if (std.mem.eql(u8, token, "capabilities")) return .capabilities;
    if (std.mem.eql(u8, token, "call")) return .call;
    if (std.mem.eql(u8, token, "query")) return .query;
    if (std.mem.eql(u8, token, "watch")) return .watch;
    return error.UnknownCommand;
}

/// argv excludes the executable name. --format is global for CLI1 commands.
/// For watch, only tokens before its operation are CLI1 syntax; the remaining
/// tokens are passed verbatim to CLI2, including its own formatting options.
pub fn parse(allocator: Allocator, argv: []const []const u8) !Options {
    var parameters: std.ArrayList(Parameter) = .empty;
    errdefer parameters.deinit(allocator);
    var command: ?Command = null;
    var result: Options = .{ .command = .help, .parameters = &.{} };
    var format_seen = false;
    var input_seen = false;
    var index: usize = 0;
    while (index < argv.len) : (index += 1) {
        const token = argv[index];
        if (command == null and std.mem.eql(u8, token, "--help")) {
            command = .help;
            continue;
        }
        if (std.mem.startsWith(u8, token, "--")) {
            const flag = try splitFlag(token);
            if (std.mem.eql(u8, flag.name, "format")) {
                if (format_seen) return error.DuplicateOption;
                format_seen = true;
                const value = try flagValue(flag, argv, &index);
                if (std.mem.eql(u8, value, "text")) {
                    result.format = .text;
                } else if (std.mem.eql(u8, value, "json")) {
                    result.format = .json;
                } else return error.InvalidFormat;
                continue;
            }
            const selected = command orelse return error.UnexpectedOption;
            if ((selected != .call and selected != .query) or result.operation == null)
                return error.UnexpectedOption;
            if (std.mem.eql(u8, flag.name, "input")) {
                if (input_seen) return error.DuplicateOption;
                input_seen = true;
                if (parameters.items.len != 0) return error.ConflictingInput;
                const value = try flagValue(flag, argv, &index);
                if (value.len == 0) return error.MissingValue;
                result.input_file = value;
            } else {
                if (input_seen) return error.ConflictingInput;
                const value = try flagValue(flag, argv, &index);
                try parameters.append(allocator, .{ .name = flag.name, .value = value });
            }
            continue;
        }
        if (token.len == 0 or token[0] == '-') return error.InvalidArgument;
        if (command == null) {
            command = try parseCommand(token);
            continue;
        }
        if (command.? == .capabilities or result.operation != null)
            return error.UnexpectedArgument;
        result.operation = token;
        if (command.? == .watch) {
            result.watch_args = argv[index + 1 ..];
            break;
        }
    }
    result.command = command orelse .help;
    switch (result.command) {
        .call, .query, .watch => if (result.operation == null) return error.MissingOperation,
        .help, .capabilities => {},
    }
    result.parameters = try parameters.toOwnedSlice(allocator);
    return result;
}
