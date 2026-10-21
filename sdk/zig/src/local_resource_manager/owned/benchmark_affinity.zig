//! Explicit process placement for the standalone comparison driver only.
//! This module is not used by the ledger owner or its public ABI.
const builtin = @import("builtin");

const ProcessorNumber = extern struct {
    group: u16,
    number: u8,
    reserved: u8,
};

extern "kernel32" fn GetCurrentProcess() callconv(.winapi) *anyopaque;
extern "kernel32" fn GetProcessAffinityMask(process: *anyopaque, process_mask: *usize, system_mask: *usize) callconv(.winapi) i32;
extern "kernel32" fn SetProcessAffinityMask(process: *anyopaque, mask: usize) callconv(.winapi) i32;
extern "kernel32" fn GetActiveProcessorGroupCount() callconv(.winapi) u16;
extern "kernel32" fn GetCurrentProcessorNumberEx(number: *ProcessorNumber) callconv(.winapi) void;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;

pub const Placement = struct {
    requested_mask: usize,
    before_process_mask: usize,
    after_process_mask: usize,
    system_mask: usize,
    active_group_count: u16,
    executing_group: u16,
    executing_processor: u8,
};

/// Run before constructing the fixture. Only this short-lived benchmark
/// process changes; errors terminate the invocation before any timer starts.
pub fn apply(mask: usize) !Placement {
    if (builtin.os.tag != .windows) return error.UnsupportedAffinityPlatform;
    if (mask == 0 or mask & (mask - 1) != 0) return error.AffinityRequiresOneProcessor;
    const groups = GetActiveProcessorGroupCount();
    if (groups != 1) return error.AffinityRequiresOneProcessorGroup;
    const process = GetCurrentProcess();
    var before: usize = 0;
    var system: usize = 0;
    if (GetProcessAffinityMask(process, &before, &system) == 0) {
        const api_error = GetLastError();
        @import("std").debug.print("GetProcessAffinityMask failed: {d}\n", .{api_error});
        return error.AffinityQueryFailed;
    }
    if (mask & before != mask or mask & system != mask) return error.AffinityProcessorUnavailable;
    if (SetProcessAffinityMask(process, mask) == 0) {
        const api_error = GetLastError();
        @import("std").debug.print("SetProcessAffinityMask failed: {d}\n", .{api_error});
        return error.AffinitySetFailed;
    }
    var after: usize = 0;
    var after_system: usize = 0;
    if (GetProcessAffinityMask(process, &after, &after_system) == 0) {
        const api_error = GetLastError();
        @import("std").debug.print("GetProcessAffinityMask readback failed: {d}\n", .{api_error});
        return error.AffinityReadbackFailed;
    }
    if (after != mask or after_system != system) return error.AffinityReadbackMismatch;
    var number: ProcessorNumber = undefined;
    GetCurrentProcessorNumberEx(&number);
    if (number.group != 0 or number.number != @ctz(mask)) return error.AffinityExecutionMismatch;
    return .{
        .requested_mask = mask,
        .before_process_mask = before,
        .after_process_mask = after,
        .system_mask = system,
        .active_group_count = groups,
        .executing_group = number.group,
        .executing_processor = number.number,
    };
}
