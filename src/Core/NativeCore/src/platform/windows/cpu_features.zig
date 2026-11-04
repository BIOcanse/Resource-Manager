const std = @import("std");
const builtin = @import("builtin");

// ABI/protocol constants, not product defaults. The request always names its probes.
pub const abi_version: u32 = 1;
pub const probe_features: u32 = 1;
pub const probe_xstate: u32 = 2;
pub const Architecture = enum(u32) { x86 = 1, x64 = 2, other = 3 };
pub const Status = enum(u32) {
    not_requested = 0,
    present = 1,
    missing_dll = 2,
    missing_entry = 3,
    unsupported_architecture = 4,
    failed = 5,
};
pub const Request = extern struct {
    struct_size: u32 = @sizeOf(Request),
    version: u32 = abi_version,
    probes: u32,
    architecture: u32,
};
pub const FeatureName = extern struct { text: [*]const u16, length: usize };
pub const Result = extern struct {
    struct_size: u32 = @sizeOf(Result),
    features_status: Status = .not_requested,
    xstate_status: Status = .not_requested,
    feature_bits: u32 = 0,
    enabled_xstate_mask: u64 = 0,
    features_error: u32 = 0,
    xstate_error: u32 = 0,
    names: [*]const FeatureName = &names,
    name_count: u32 = names.len,
    reserved: u32 = 0,
};
// Synchronous only. Callbacks write values/errors only when returning the corresponding status.
pub const Callbacks = extern struct {
    struct_size: u32 = @sizeOf(Callbacks),
    version: u32 = abi_version,
    feature: ?*const fn (?*anyopaque, u32, *u32, *u32) callconv(.c) u32,
    xstate: ?*const fn (?*anyopaque, *u64, *u32) callconv(.c) u32,
};

// Exact original Windows flags and ordering. No CPUID or runtime-intrinsic substitute.
// https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-isprocessorfeaturepresent
pub const flags = [_]u32{ 6, 10, 13, 36, 37, 38, 39, 40, 41 };
const labels = [_][]const u16{
    std.unicode.utf8ToUtf16LeStringLiteral("SSE"),
    std.unicode.utf8ToUtf16LeStringLiteral("SSE2"),
    std.unicode.utf8ToUtf16LeStringLiteral("SSE3"),
    std.unicode.utf8ToUtf16LeStringLiteral("SSSE3"),
    std.unicode.utf8ToUtf16LeStringLiteral("SSE4.1"),
    std.unicode.utf8ToUtf16LeStringLiteral("SSE4.2"),
    std.unicode.utf8ToUtf16LeStringLiteral("AVX"),
    std.unicode.utf8ToUtf16LeStringLiteral("AVX2"),
    std.unicode.utf8ToUtf16LeStringLiteral("AVX512F"),
};
const names: [labels.len]FeatureName = blk: {
    var table: [labels.len]FeatureName = undefined;
    var index: usize = 0;
    while (index < labels.len) : (index += 1)
        table[index] = .{ .text = labels[index].ptr, .length = labels[index].len };
    break :blk table;
};

fn supportedArchitecture(architecture: u32) bool {
    return architecture == @backingInt(Architecture.x86) or architecture == @backingInt(Architecture.x64);
}

fn validRequest(request: *const Request) bool {
    return request.struct_size == @sizeOf(Request) and request.version == abi_version and
        request.probes != 0 and request.probes & ~(probe_features | probe_xstate) == 0 and
        (supportedArchitecture(request.architecture) or request.architecture == @backingInt(Architecture.other));
}

fn readStatus(value: u32) Status {
    return switch (value) {
        @backingInt(Status.present) => .present,
        @backingInt(Status.missing_dll) => .missing_dll,
        @backingInt(Status.missing_entry) => .missing_entry,
        else => .failed,
    };
}

pub fn captureWithCallbacks(request_ptr: ?*const Request, context: ?*anyopaque, callbacks_ptr: ?*const Callbacks, output_ptr: ?*Result, output_size: u32) i32 {
    const request = request_ptr orelse return -1;
    const output = output_ptr orelse return -1;
    const callbacks = callbacks_ptr orelse return -1;
    if (output_size != @sizeOf(Result) or !validRequest(request) or
        callbacks.struct_size != @sizeOf(Callbacks) or callbacks.version != abi_version) return -1;
    if (request.probes & probe_features != 0 and callbacks.feature == null) return -1;
    if (request.probes & probe_xstate != 0 and supportedArchitecture(request.architecture) and callbacks.xstate == null) return -1;

    // All state is the caller's small result and this call's stack. No allocations or retained context.
    output.* = .{};
    if (request.probes & probe_features != 0) {
        var index: usize = 0;
        var bits: u32 = 0;
        output.features_status = .present;
        while (index < flags.len) : (index += 1) {
            var present: u32 = 0;
            const status = readStatus(callbacks.feature.?(context, flags[index], &present, &output.features_error));
            if (status != .present) {
                output.features_status = status;
                break; // A missing API discards the incomplete prefix, as the original Read().
            }
            if (present != 0) bits |= @as(u32, 1) << @intCast(index);
        }
        if (output.features_status == .present) output.feature_bits = bits;
        // ReadFields never calls the second probe after an unexpected first-probe exception.
        if (output.features_status == .failed) return 0;
    }
    if (request.probes & probe_xstate != 0) {
        if (!supportedArchitecture(request.architecture)) {
            output.xstate_status = .unsupported_architecture;
        } else {
            var mask: u64 = 0;
            output.xstate_status = readStatus(callbacks.xstate.?(context, &mask, &output.xstate_error));
            if (output.xstate_status == .present) output.enabled_xstate_mask = mask;
        }
    }
    return 0;
}

// Runtime read: fixed for this OS/process architecture. Read-once ownership stays upstream.
pub fn capture(request: ?*const Request, output: ?*Result, output_size: u32) i32 {
    var access: WindowsAccess = .{};
    const callbacks: Callbacks = .{ .feature = WindowsAccess.feature, .xstate = WindowsAccess.xstate };
    return captureWithCallbacks(request, &access, &callbacks, output, output_size);
}

const WindowsAccess = struct {
    feature_fn: ?*const fn (u32) callconv(.winapi) i32 = null,

    fn resolve(comptime T: type, name: [*:0]const u8, error_code: *u32) ?T {
        if (builtin.os.tag != .windows) {
            error_code.* = error_mod_not_found;
            return null;
        }
        // Borrowed kernel32 module: Windows keeps it loaded throughout the synchronous call.
        // Microsoft's XState compatibility recipe uses these two APIs, not LoadLibrary + a cache.
        // https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getenabledxstatefeatures
        const module = GetModuleHandleW(std.unicode.utf8ToUtf16LeStringLiteral("kernel32.dll")) orelse {
            error_code.* = GetLastError();
            return null;
        };
        const address = GetProcAddress(module, name) orelse {
            error_code.* = GetLastError();
            return null;
        };
        return @ptrCast(address);
    }

    fn resolutionStatus(error_code: u32) u32 {
        return @backingInt(switch (error_code) {
            error_mod_not_found => Status.missing_dll,
            error_proc_not_found => Status.missing_entry,
            else => Status.failed,
        });
    }

    fn feature(context: ?*anyopaque, flag: u32, value: *u32, error_code: *u32) callconv(.c) u32 {
        const self: *WindowsAccess = @ptrCast(@alignCast(context.?));
        if (self.feature_fn == null) {
            self.feature_fn = resolve(@TypeOf(self.feature_fn.?), "IsProcessorFeaturePresent", error_code);
            if (self.feature_fn == null) return resolutionStatus(error_code.*);
        }
        value.* = if (self.feature_fn.?(flag) != 0) 1 else 0;
        return @backingInt(Status.present);
    }

    fn xstate(_: ?*anyopaque, value: *u64, error_code: *u32) callconv(.c) u32 {
        const read = resolve(*const fn () callconv(.winapi) u64, "GetEnabledXStateFeatures", error_code) orelse
            return resolutionStatus(error_code.*);
        value.* = read(); // Every bit, including zero and bit 63, is an OS fact.
        return @backingInt(Status.present);
    }
};

const error_mod_not_found = 126;
const error_proc_not_found = 127;
extern "kernel32" fn GetModuleHandleW([*:0]const u16) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn GetProcAddress(*anyopaque, [*:0]const u8) callconv(.winapi) ?*const anyopaque;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
