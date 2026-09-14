const std = @import("std");
const ResultCode = @import("../common/result_codes.zig").ResultCode;

pub const abi_version: u32 = 0x0002_0000;

pub const Flags = struct {
    pub const priority_class: u32 = 1 << 0;
    pub const affinity_mask: u32 = 1 << 1;
    pub const memory_priority: u32 = 1 << 2;
    pub const power_throttling: u32 = 1 << 3;
    pub const trim_working_set: u32 = 1 << 4;
    pub const all: u32 = priority_class | affinity_mask | memory_priority | power_throttling | trim_working_set;
};

pub const HeaderFlags = struct {
    pub const little_endian: u32 = 1 << 0;
};

pub const Header = extern struct {
    abi_version: u32,
    struct_size: u32,
    item_struct_size: u32,
    result_struct_size: u32,
    item_count: u32,
    flags: u32,
    reserved_0: u32,
    reserved_1: u32,
};

pub const Item = extern struct {
    struct_size: u32,
    process_id: u32,
    flags: u32,
    priority_class: u32,
    memory_priority: u32,
    power_control_mask: u32,
    power_state_mask: u32,
    expected_memory_priority: u32,
    affinity_mask: u64,
    expected_start_file_time: u64,
};

pub const ItemResult = extern struct {
    struct_size: u32,
    process_id: u32,
    requested_flags: u32,
    succeeded_flags: u32,
    open_error: u32,
    identity_error: u32,
    priority_error: u32,
    affinity_error: u32,
    memory_error: u32,
    power_error: u32,
    trim_error: u32,
    observed_memory_priority: u32,
};

const HANDLE = ?*anyopaque;
const FILETIME = extern struct {
    low: u32,
    high: u32,
};
const MemoryPriorityInformation = extern struct {
    memory_priority: u32,
};
const ProcessPowerThrottlingState = extern struct {
    version: u32,
    control_mask: u32,
    state_mask: u32,
};

const PROCESS_SET_QUOTA: u32 = 0x0100;
const PROCESS_SET_INFORMATION: u32 = 0x0200;
const PROCESS_QUERY_LIMITED_INFORMATION: u32 = 0x1000;
const PROCESS_MEMORY_PRIORITY_INFORMATION: u32 = 0;
const PROCESS_POWER_THROTTLING_INFORMATION: u32 = 4;
const PROCESS_POWER_THROTTLING_CURRENT_VERSION: u32 = 1;
const ERROR_INVALID_PARAMETER: u32 = 87;
const ERROR_NOT_FOUND: u32 = 1168;
const MEMORY_PRIORITY_CONFLICT: u32 = 0xE001_0001;

extern "kernel32" fn OpenProcess(
    desired_access: u32,
    inherit_handle: i32,
    process_id: u32,
) callconv(.winapi) HANDLE;
extern "kernel32" fn CloseHandle(handle: HANDLE) callconv(.winapi) i32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
extern "kernel32" fn GetProcessTimes(
    process: HANDLE,
    creation_time: *FILETIME,
    exit_time: *FILETIME,
    kernel_time: *FILETIME,
    user_time: *FILETIME,
) callconv(.winapi) i32;
extern "kernel32" fn SetPriorityClass(process: HANDLE, priority_class: u32) callconv(.winapi) i32;
extern "kernel32" fn SetProcessAffinityMask(process: HANDLE, affinity_mask: usize) callconv(.winapi) i32;
extern "kernel32" fn SetProcessInformation(
    process: HANDLE,
    information_class: u32,
    information: *const anyopaque,
    information_size: u32,
) callconv(.winapi) i32;
extern "kernel32" fn GetProcessInformation(
    process: HANDLE,
    information_class: u32,
    information: *anyopaque,
    information_size: u32,
) callconv(.winapi) i32;
extern "psapi" fn EmptyWorkingSet(process: HANDLE) callconv(.winapi) i32;

pub fn execute(
    header: *const Header,
    items_pointer: ?[*]const Item,
    item_capacity: u32,
    results_pointer: ?[*]ItemResult,
    result_capacity: u32,
) ResultCode {
    if (header.abi_version != abi_version or
        header.struct_size != @sizeOf(Header) or
        header.item_struct_size != @sizeOf(Item) or
        header.result_struct_size != @sizeOf(ItemResult) or
        header.flags != HeaderFlags.little_endian)
    {
        return .abi_mismatch;
    }

    if (header.item_count > item_capacity or header.item_count > result_capacity) {
        return .buffer_too_small;
    }
    if (header.item_count == 0) return .ok;

    const items = items_pointer orelse return .invalid_argument;
    const results = results_pointer orelse return .invalid_argument;
    for (0..header.item_count) |index| {
        executeItem(&items[index], &results[index]);
    }
    return .ok;
}

fn executeItem(item: *const Item, result: *ItemResult) void {
    result.* = .{
        .struct_size = @sizeOf(ItemResult),
        .process_id = item.process_id,
        .requested_flags = item.flags & Flags.all,
        .succeeded_flags = 0,
        .open_error = 0,
        .identity_error = 0,
        .priority_error = 0,
        .affinity_error = 0,
        .memory_error = 0,
        .power_error = 0,
        .trim_error = 0,
        .observed_memory_priority = 0,
    };

    if (item.struct_size != @sizeOf(Item) or
        item.process_id == 0 or
        result.requested_flags == 0 or
        item.flags != result.requested_flags or
        ((item.flags & Flags.memory_priority) != 0 and
            (item.expected_start_file_time == 0 or
                !isSupportedMemoryPriority(item.memory_priority) or
                !isSupportedMemoryPriority(item.expected_memory_priority))))
    {
        result.open_error = ERROR_INVALID_PARAMETER;
        return;
    }

    var desired_access = PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION;
    if ((item.flags & Flags.trim_working_set) != 0) desired_access |= PROCESS_SET_QUOTA;
    const process = OpenProcess(desired_access, 0, item.process_id);
    if (process == null) {
        result.open_error = GetLastError();
        return;
    }
    defer _ = CloseHandle(process);

    if (item.expected_start_file_time != 0 and !matchesExpectedStartTime(process, item.expected_start_file_time, &result.identity_error)) {
        return;
    }

    if ((item.flags & Flags.memory_priority) != 0) {
        var observed = MemoryPriorityInformation{ .memory_priority = 0 };
        if (GetProcessInformation(
            process,
            PROCESS_MEMORY_PRIORITY_INFORMATION,
            &observed,
            @sizeOf(MemoryPriorityInformation),
        ) == 0) {
            result.memory_error = GetLastError();
        } else {
            result.observed_memory_priority = observed.memory_priority;
            result.memory_error = memoryPriorityPreconditionError(
                item.memory_priority,
                item.expected_memory_priority,
                observed.memory_priority,
            );
            if (result.memory_error == 0) {
                var state = MemoryPriorityInformation{ .memory_priority = item.memory_priority };
                if (SetProcessInformation(
                    process,
                    PROCESS_MEMORY_PRIORITY_INFORMATION,
                    &state,
                    @sizeOf(MemoryPriorityInformation),
                ) != 0) {
                    result.succeeded_flags |= Flags.memory_priority;
                } else {
                    result.memory_error = GetLastError();
                }
            }
        }
        if ((result.succeeded_flags & Flags.memory_priority) == 0) return;
    }

    if ((item.flags & Flags.priority_class) != 0) {
        if (!isSupportedPriorityClass(item.priority_class)) {
            result.priority_error = ERROR_INVALID_PARAMETER;
        } else if (SetPriorityClass(process, item.priority_class) != 0) {
            result.succeeded_flags |= Flags.priority_class;
        } else {
            result.priority_error = GetLastError();
        }
    }

    if ((item.flags & Flags.affinity_mask) != 0) {
        if (item.affinity_mask == 0 or item.affinity_mask > std.math.maxInt(usize)) {
            result.affinity_error = ERROR_INVALID_PARAMETER;
        } else if (SetProcessAffinityMask(process, @intCast(item.affinity_mask)) != 0) {
            result.succeeded_flags |= Flags.affinity_mask;
        } else {
            result.affinity_error = GetLastError();
        }
    }

    if ((item.flags & Flags.power_throttling) != 0) {
        var state = ProcessPowerThrottlingState{
            .version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            .control_mask = item.power_control_mask,
            .state_mask = item.power_state_mask,
        };
        if (SetProcessInformation(process, PROCESS_POWER_THROTTLING_INFORMATION, &state, @sizeOf(ProcessPowerThrottlingState)) != 0) {
            result.succeeded_flags |= Flags.power_throttling;
        } else {
            result.power_error = GetLastError();
        }
    }

    if ((item.flags & Flags.trim_working_set) != 0) {
        if (EmptyWorkingSet(process) != 0) {
            result.succeeded_flags |= Flags.trim_working_set;
        } else {
            result.trim_error = GetLastError();
        }
    }
}

fn matchesExpectedStartTime(process: HANDLE, expected: u64, error_code: *u32) bool {
    var creation = FILETIME{ .low = 0, .high = 0 };
    var exit_time = FILETIME{ .low = 0, .high = 0 };
    var kernel_time = FILETIME{ .low = 0, .high = 0 };
    var user_time = FILETIME{ .low = 0, .high = 0 };
    if (GetProcessTimes(process, &creation, &exit_time, &kernel_time, &user_time) == 0) {
        error_code.* = GetLastError();
        return false;
    }

    const actual = (@as(u64, creation.high) << 32) | creation.low;
    if (actual != expected) {
        error_code.* = ERROR_NOT_FOUND;
        return false;
    }
    return true;
}

fn isSupportedPriorityClass(value: u32) bool {
    return switch (value) {
        0x0000_0020,
        0x0000_0040,
        0x0000_0080,
        0x0000_0100,
        0x0000_4000,
        0x0000_8000,
        => true,
        else => false,
    };
}

fn isSupportedMemoryPriority(value: u32) bool {
    return value >= 1 and value <= 5;
}

fn memoryPriorityPreconditionError(target: u32, expected: u32, observed: u32) u32 {
    if (!isSupportedMemoryPriority(target) or
        !isSupportedMemoryPriority(expected) or
        !isSupportedMemoryPriority(observed))
    {
        return ERROR_INVALID_PARAMETER;
    }
    return if (expected == observed) 0 else MEMORY_PRIORITY_CONFLICT;
}

test "process policy ABI keeps fixed layouts" {
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(Header));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(Item));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(ItemResult));
}

test "invalid process policy item is reported without opening a process" {
    var header = Header{
        .abi_version = abi_version,
        .struct_size = @sizeOf(Header),
        .item_struct_size = @sizeOf(Item),
        .result_struct_size = @sizeOf(ItemResult),
        .item_count = 1,
        .flags = HeaderFlags.little_endian,
        .reserved_0 = 0,
        .reserved_1 = 0,
    };
    var items = [_]Item{.{
        .struct_size = 0,
        .process_id = 0,
        .flags = Flags.priority_class,
        .priority_class = 0x20,
        .memory_priority = 0,
        .power_control_mask = 0,
        .power_state_mask = 0,
        .expected_memory_priority = 0,
        .affinity_mask = 0,
        .expected_start_file_time = 0,
    }};
    var results: [1]ItemResult = undefined;

    try std.testing.expectEqual(
        ResultCode.ok,
        execute(&header, &items, items.len, &results, results.len),
    );
    try std.testing.expectEqual(ERROR_INVALID_PARAMETER, results[0].open_error);
    try std.testing.expectEqual(@as(u32, 0), results[0].succeeded_flags);
}

test "memory priority precondition rejects conflicting or invalid state" {
    try std.testing.expectEqual(
        @as(u32, 0),
        memoryPriorityPreconditionError(3, 5, 5),
    );
    try std.testing.expectEqual(
        MEMORY_PRIORITY_CONFLICT,
        memoryPriorityPreconditionError(3, 5, 4),
    );
    try std.testing.expectEqual(
        ERROR_INVALID_PARAMETER,
        memoryPriorityPreconditionError(0, 5, 5),
    );
    try std.testing.expectEqual(
        ERROR_INVALID_PARAMETER,
        memoryPriorityPreconditionError(3, 5, 0),
    );
}
