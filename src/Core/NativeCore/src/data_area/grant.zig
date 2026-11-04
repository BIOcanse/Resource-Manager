//! One synchronous grant transaction. All inputs are borrowed from the original
//! connection and data_area.Owner, under its publication/selection gate. None
//! may be decoded from a client's proposed PID, role, session or section list.
//!
//! This leaf owns no region, permission table, reader registry or current value.
//! L supplies whole kernel objects: every exposed byte (including directories,
//! padding, strings and dependencies) must belong to the listed sources. A
//! filtered position list cannot make a mixed physical object safe to grant.
const std = @import("std");
pub const access = @import("rm_access");
pub const plan = @import("rm_data_area_plan");
const process = @import("grant_process.zig");
const windows = std.os.windows;

pub const file_map_read: u32 = 0x0004;
pub const file_map_write: u32 = 0x0002;
pub const filetime_utc_ticks_offset: i64 = 504911232000000000;

pub const Mode = enum(u32) { read_only = 0, own_writer = 1, _ };
pub const Delivery = enum(u32) { not_sent = 0, delivered = 1, unknown = 2, _ };
pub const Status = enum(u32) {
    ok = 0,
    invalid_argument = 1,
    buffer_too_small = 2,
    invalid_caller = 3,
    caller_changed = 4,
    process_failed = 5,
    token_failed = 6,
    denied = 7,
    process_rights_pending = 8,
    cancelled = 9,
    duplicate_failed = 10,
    reply_failed = 11,
    delivery_unknown = 12,
};

/// The source owner's registration, not an H-owned classification table. Raw
/// enum values are validated before they enter the sole P-access policy.
pub const Source = extern struct {
    requirement: u32,
    kind: u32,
    windows_session_id: i32 = -1,
    reserved: u32 = 0,
};

/// Trusted, complete description of one borrowed physical object. `writer`
/// is the original selected W's identity, or all zero for an object that can
/// never be given write access. T/N/A must always use the latter form.
pub const Object = extern struct {
    reference: plan.RegionReference,
    section: ?windows.HANDLE,
    byte_length: u64,
    sources: ?[*]const Source,
    source_count: u32,
    mode: Mode,
    writer: plan.SelectionIdentity,
};

pub const GrantedObject = extern struct {
    reference: plan.RegionReference = .{ .id = .service_normal, .epoch = 0, .layout_generation = 0 },
    remote_handle: usize = 0,
    byte_length: u64 = 0,
    desired_access: u32 = 0,
    reserved: u32 = 0,
};

pub const Outcome = extern struct {
    status: Status = .invalid_argument,
    native_error: u32 = 0,
    exposed_count: u32 = 0,
    delivery: Delivery = .not_sent,
};

/// Existing control-connection callbacks. They are synchronous, must not throw
/// through the ABI, and cannot retain these borrowed pointers. `reply` must
/// commit the original registration before returning delivered. On any failure
/// after duplication, `abort_exposed` runs BEFORE the owner's gate is released.
/// It handles possibly exposed objects through the original source lifetime;
/// it must never close a remote handle number that the caller may have reused.
pub const Control = extern struct {
    context: ?*anyopaque,
    is_cancelled: ?*const fn (?*anyopaque) callconv(.c) u32,
    reply: ?*const fn (?*anyopaque, [*]const GrantedObject, u32) callconv(.c) Delivery,
    abort_exposed: ?*const fn (?*anyopaque, [*]const Object, [*]const GrantedObject, u32) callconv(.c) void,
};

pub const Request = struct {
    /// Alive attester-owned process object, borrowed only for this call.
    caller_process: windows.HANDLE,
    policy: access.Input,
    /// Existing connection identity; zero is valid. Not an edit-session ID.
    connection_generation: u64,
};

/// No asynchronous borrow or long-lived process pin escapes this function.
/// GET uses a stable single output instead; this API is only registration for
/// a continuing mapping. All validation precedes the first outbound duplicate.
pub fn execute(request: Request, objects: []const Object, output: []GrantedObject, control: Control) Outcome {
    var result = Outcome{};
    if (objects.len == 0 or objects.len > std.math.maxInt(u32) or
        control.is_cancelled == null or control.reply == null or control.abort_exposed == null)
        return result;
    if (output.len < objects.len) {
        result.status = .buffer_too_small;
        return result;
    }
    @memset(output[0..objects.len], .{});
    const caller = request.policy.caller orelse {
        result.status = .invalid_caller;
        return result;
    };
    const pin = process.Pin.open(request.caller_process, caller, &result.native_error) catch |err| {
        result.status = processStatus(err);
        return result;
    };
    defer pin.close();

    var i: usize = 0;
    while (i < objects.len) : (i += 1) {
        const object = objects[i];
        if (object.section == null or object.byte_length == 0 or object.byte_length > std.math.maxInt(usize) or
            object.reference.reserved != 0 or object.reference.epoch == 0 or object.reference.layout_generation == 0 or
            object.source_count == 0 or object.sources == null)
            return result;
        var previous: usize = 0;
        while (previous < i) : (previous += 1) {
            // An object is granted once. Different epochs for one slot cannot
            // simultaneously belong to this Owner's current collection either.
            if (object.reference.id == objects[previous].reference.id or object.section == objects[previous].section)
                return result;
        }
        const sources = object.sources.?[0..object.source_count];
        var s: usize = 0;
        while (s < sources.len) : (s += 1) {
            const source = sources[s];
            if (source.reserved != 0 or source.windows_session_id < -1) return result;
            const requirement = std.enums.fromInt(access.Requirement, source.requirement) orelse return result;
            const kind = std.enums.fromInt(access.ReadKind, source.kind) orelse return result;
            var input = request.policy;
            input.requirement = requirement;
            const decision = access.evaluateRead(input, .{
                .kind = kind,
                .windows_session_id = if (source.windows_session_id < 0) null else source.windows_session_id,
            });
            if (decision.disposition != .allowed) {
                result.status = if (decision.disposition == .check_process_permission) .process_rights_pending else .denied;
                return result;
            }
            // The process-detail lifetime/publication transaction is not yet
            // supplied by P-proc-read + R-proc-facts + P-proc-wire. Even admin
            // admission is insufficient. Never simulate that missing contract.
            if (kind == .process_detail) {
                result.status = .process_rights_pending;
                return result;
            }
        }
        switch (object.mode) {
            .read_only => {},
            .own_writer => {
                const id = @backingInt(object.reference.id);
                if (id < plan.region_count or
                    plan.selectionRegion((id - plan.region_count) / 2, .writer) != object.reference.id)
                {
                    result.status = .denied;
                    return result;
                }
                const creation = std.math.sub(i64, caller.process_created_utc_ticks, filetime_utc_ticks_offset) catch return result;
                if (!object.writer.valid() or creation <= 0 or caller.process_id <= 0 or caller.windows_session_id < 0 or
                    object.writer.process_id != @as(u32, @intCast(caller.process_id)) or
                    object.writer.process_start_time != @as(u64, @intCast(creation)) or
                    object.writer.session_id != @as(u32, @intCast(caller.windows_session_id)) or
                    object.writer.connection_generation != request.connection_generation)
                {
                    result.status = .denied;
                    return result;
                }
                // W is always login-session data. Its entire object must be
                // registered as that same desktop source, even for an admin.
                s = 0;
                while (s < sources.len) : (s += 1) {
                    if (sources[s].kind != @backingInt(access.ReadKind.desktop_fact) or
                        sources[s].windows_session_id != caller.windows_session_id)
                    {
                        result.status = .denied;
                        return result;
                    }
                }
            },
            _ => return result,
        }
    }

    // Scope-local pin remains the SAME Windows process for proof and all
    // outbound duplicates. No OpenProcess(PID) race or inherited RW handle.
    i = 0;
    while (i < objects.len) : (i += 1) {
        if (control.is_cancelled.?(control.context) != 0) {
            result.status = .cancelled;
            return abort(objects, output, control, result);
        }
        pin.validate(caller, &result.native_error) catch |err| {
            result.status = processStatus(err);
            return abort(objects, output, control, result);
        };
        const object = objects[i];
        const rights = file_map_read | @as(u32, if (object.mode == .own_writer) file_map_write else 0);
        const remote = pin.duplicateSection(object.section.?, rights, &result.native_error) catch {
            result.status = .duplicate_failed;
            return abort(objects, output, control, result);
        };
        output[i] = .{ .reference = object.reference, .remote_handle = @intFromPtr(remote), .byte_length = object.byte_length, .desired_access = rights };
        result.exposed_count += 1;
    }
    if (control.is_cancelled.?(control.context) != 0) {
        result.status = .cancelled;
        return abort(objects, output, control, result);
    }
    pin.validate(caller, &result.native_error) catch |err| {
        result.status = processStatus(err);
        return abort(objects, output, control, result);
    };
    result.delivery = control.reply.?(control.context, output.ptr, @intCast(objects.len));
    if (result.delivery != .delivered) {
        result.status = if (result.delivery == .not_sent) .reply_failed else .delivery_unknown;
        return abort(objects, output, control, result);
    }
    result.status = .ok;
    return result;
}

fn abort(objects: []const Object, output: []const GrantedObject, control: Control, result: Outcome) Outcome {
    if (result.exposed_count != 0)
        control.abort_exposed.?(control.context, objects.ptr, output.ptr, result.exposed_count);
    return result;
}

fn processStatus(err: process.Error) Status {
    return switch (err) {
        error.InvalidCaller => .invalid_caller,
        error.CallerChanged => .caller_changed,
        error.ProcessFailed => .process_failed,
        error.TokenFailed => .token_failed,
    };
}
