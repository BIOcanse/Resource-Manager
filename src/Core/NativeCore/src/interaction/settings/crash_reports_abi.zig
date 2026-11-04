pub const implementation = @import("crash_reports.zig");
const parameters = implementation;

pub export fn rm_crash_report_parameters_resolve_mode(input: ?*const parameters.ModeInput, output: ?*parameters.ModeOutput) callconv(.c) i32 {
    return parameters.resolveMode(input, output);
}

pub export fn rm_crash_report_parameters_keep_count(value: i32, minimum: i32, maximum: i32, output: ?*i32) callconv(.c) i32 {
    return parameters.keepCount(value, minimum, maximum, output);
}
