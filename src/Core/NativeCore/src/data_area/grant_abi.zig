//! Dedicated internal grant ABI. Public registration is deliberately owned by
//! maintenance. This is NOT a wire request: every fact/handle/description is
//! supplied by the actual attester, policy registration and original Owner.
const std = @import("std");
pub const grant = @import("grant.zig");

pub const version: u32 = 0x0001_0000;
pub const Request = extern struct {
    struct_size: u32,
    abi_version: u32,
    caller_process: ?std.os.windows.HANDLE,
    process_id: i32,
    windows_session_id: i32,
    process_created_utc_ticks: i64,
    windows_sid: ?[*]const u16,
    windows_sid_length: u32,
    is_administrator: u32,
    local_connection: u32,
    origin_allowed: u32,
    normal_actions_allowed: u32,
    reserved: u32,
    connection_generation: u64,
};

pub export fn rm_data_area_grant_execute(
    request: ?*const Request,
    objects: ?[*]const grant.Object,
    object_count: u32,
    output: ?[*]grant.GrantedObject,
    output_capacity: u32,
    control: ?*const grant.Control,
    outcome: ?*grant.Outcome,
) callconv(.c) grant.Status {
    const result = outcome orelse return .invalid_argument;
    result.* = .{};
    const input = request orelse return result.status;
    if (input.struct_size != @sizeOf(Request) or input.abi_version != version or
        input.reserved != 0 or input.is_administrator > 1 or input.local_connection > 1 or
        input.origin_allowed > 1 or input.normal_actions_allowed > 1 or object_count == 0 or
        input.windows_sid_length == 0)
        return result.status;
    const actual_process = input.caller_process orelse return result.status;
    const sid = input.windows_sid orelse return result.status;
    const items = objects orelse return result.status;
    const destination = output orelse return result.status;
    const connection = control orelse return result.status;
    result.* = grant.execute(.{
        .caller_process = actual_process,
        .policy = .{
            // Each original source registration supplies its own requirement.
            // No route/default permission is selected by the grant boundary.
            .requirement = .missing_policy,
            .local_connection = input.local_connection != 0,
            .origin_allowed = input.origin_allowed != 0,
            .normal_actions_allowed = input.normal_actions_allowed != 0,
            .caller = .{
                .process_id = input.process_id,
                .process_created_utc_ticks = input.process_created_utc_ticks,
                .windows_sid = sid[0..input.windows_sid_length],
                .windows_session_id = input.windows_session_id,
                .is_administrator = input.is_administrator != 0,
            },
        },
        .connection_generation = input.connection_generation,
    }, items[0..object_count], destination[0..output_capacity], connection.*);
    return result.status;
}
