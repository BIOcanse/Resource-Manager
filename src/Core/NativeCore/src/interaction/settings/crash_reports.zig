// S-crash: stateless interpretation over call-scoped original UTF-16 regions.
// Channel detection is deliberately not approximated with ASCII folding. The
// caller must supply the original channel until its ordinal Unicode boundary
// is settled. Keep-count bounds are required inputs, with no native defaults.
const std = @import("std");

pub const Text = extern struct {
    pointer: ?[*]const u16,
    length: usize,

    fn slice(self: Text) ?[]const u16 {
        if (self.pointer) |pointer| return pointer[0..self.length];
        return if (self.length == 0) &.{} else null;
    }
};

pub const ModeInput = extern struct {
    mode: Text,
    beta_default: Text,
    stable_default: Text,
    beta_channel: u32,
};

// These are field selectors, not copies or normalized settings. The caller
// reads the selected field from the same immutable settings/options objects.
pub const ModeField = enum(u32) { mode, beta_default, stable_default };
pub const ModeOutput = extern struct {
    effective_field: u32,
    default_field: u32,
    channel_default: u32,
    dumps_on: u32,
};

pub const Status = enum(i32) { ok, invalid_input, invalid_bounds };

pub fn resolveMode(input_pointer: ?*const ModeInput, output_pointer: ?*ModeOutput) i32 {
    const input = input_pointer orelse return @backingInt(Status.invalid_input);
    const output = output_pointer orelse return @backingInt(Status.invalid_input);
    if (input.beta_channel > 1) return @backingInt(Status.invalid_input);
    const mode = input.mode.slice() orelse return @backingInt(Status.invalid_input);
    const beta = input.beta_default.slice() orelse return @backingInt(Status.invalid_input);
    const stable = input.stable_default.slice() orelse return @backingInt(Status.invalid_input);
    const default_field: ModeField = if (input.beta_channel == 1) .beta_default else .stable_default;
    const channel_default = std.mem.eql(u16, mode, std.unicode.utf8ToUtf16LeStringLiteral("default"));
    const effective_field: ModeField = if (channel_default) default_field else .mode;
    const effective = if (channel_default) (if (input.beta_channel == 1) beta else stable) else mode;
    output.* = .{
        .effective_field = @backingInt(effective_field),
        .default_field = @backingInt(default_field),
        .channel_default = @intFromBool(channel_default),
        .dumps_on = @intFromBool(std.mem.eql(u16, effective, std.unicode.utf8ToUtf16LeStringLiteral("on"))),
    };
    return @backingInt(Status.ok);
}

pub fn keepCount(value: i32, minimum: i32, maximum: i32, output_pointer: ?*i32) i32 {
    const output = output_pointer orelse return @backingInt(Status.invalid_input);
    if (minimum > maximum) return @backingInt(Status.invalid_bounds);
    output.* = @min(@max(value, minimum), maximum);
    return @backingInt(Status.ok);
}
