const std = @import("std");
const builtin = @import("builtin");
const core = @import("core.zig");

// Behavioral reference, independently implemented in Zig (no source copied):
// microsoft/Windows-driver-samples, f4c1ff842b6a0001152f44cb61788b216a1c7627,
// setup/devcon/cmds.cpp ControlCallback, MS-PL. Use class installers so vendor
// class/co-installers participate. Disable and matching enable are both scoped
// to the current hardware profile; global disabled state is never changed.
// https://github.com/microsoft/Windows-driver-samples/blob/f4c1ff842b6a0001152f44cb61788b216a1c7627/setup/devcon/cmds.cpp

const Handle = *anyopaque;
const DeviceInfo = extern struct {
    size: u32 = @sizeOf(DeviceInfo),
    class_guid: [16]u8 = @splat(0),
    instance: u32 = 0,
    reserved: usize = 0,
};
const ClassHeader = extern struct { size: u32 = @sizeOf(ClassHeader), function: u32 = 0x12 };
const PropertyChange = extern struct {
    header: ClassHeader = .{},
    change: u32,
    scope: u32 = 2, // DICS_FLAG_CONFIGSPECIFIC; HwProfile zero means current.
    hardware_profile: u32 = 0,
};
const InstallParameters = extern struct {
    size: u32 = @sizeOf(InstallParameters),
    flags: u32 = 0,
    flags_ex: u32 = 0,
    parent: ?Handle = null,
    message_handler: ?*anyopaque = null,
    handler_context: ?*anyopaque = null,
    queue: ?Handle = null,
    class_reserved: usize = 0,
    reserved: u32 = 0,
    driver_path: [260]u16 = @splat(0), // MAX_PATH, fixed by SP_DEVINSTALL_PARAMS_W ABI.
};
const AdministratorSid = extern struct {
    revision: u8 = 1,
    subauthority_count: u8 = 2,
    authority: [6]u8 = .{ 0, 0, 0, 0, 0, 5 },
    subauthorities: [2]u32 = .{ 32, 544 }, // SECURITY_BUILTIN_DOMAIN_RID, DOMAIN_ALIAS_RID_ADMINS.
};

fn invalidHandle(handle: Handle) bool {
    return @intFromPtr(handle) == std.math.maxInt(usize);
}

pub const Access = struct {
    devices: ?Handle = null,
    device: DeviceInfo = .{},

    pub const adapter: core.Adapter = .{
        .administrator = administrator,
        .open = open,
        .close = close,
        .disable = disable,
        .enable = enable,
        .verify = verify,
    };

    fn from(context: ?*anyopaque) *Access {
        return @ptrCast(@alignCast(context.?));
    }

    fn administrator(_: ?*anyopaque) u32 {
        var sid: AdministratorSid = .{};
        var member: i32 = 0;
        if (CheckTokenMembership(null, &sid, &member) == 0) return GetLastError();
        return if (member != 0) 0 else 5;
    }

    // RuntimeRead: EventDriven("Confirmed device restart requested")
    fn open(context: ?*anyopaque, instance_id: [:0]const u16) core.Result {
        const self = from(context);
        const devices = SetupDiCreateDeviceInfoList(null, null);
        if (invalidHandle(devices)) return .{ .code = .device_unavailable, .detail = GetLastError() };
        self.devices = devices;
        if (SetupDiOpenDeviceInfoW(devices, instance_id.ptr, null, 0, &self.device) == 0) {
            const detail = GetLastError();
            close(context);
            return .{ .code = if (detail == 1168 or detail == 433 or detail == 259) .device_not_found else .device_unavailable, .detail = detail };
        }
        var status: u32 = 0;
        var problem: u32 = 0;
        const cr = CM_Get_DevNode_Status(&status, &problem, self.device.instance, 0);
        if (cr != 0 or (status & 8) == 0 or problem != 0) { // DN_STARTED.
            close(context);
            // A successful status query reporting a stopped/problem node is a
            // stable unavailable outcome, not a fabricated Win32 failure.
            return .{ .code = .device_unavailable, .detail = if (cr != 0) CM_MapCrToWin32Err(cr, 31) else 0 };
        }
        return .{};
    }

    fn close(context: ?*anyopaque) void {
        const self = from(context);
        if (self.devices) |devices| _ = SetupDiDestroyDeviceInfoList(devices);
        self.devices = null;
    }

    fn change(context: ?*anyopaque, value: u32) u32 {
        const self = from(context);
        var parameters: PropertyChange = .{ .change = value };
        if (SetupDiSetClassInstallParamsW(self.devices.?, &self.device, &parameters.header, @sizeOf(PropertyChange)) == 0) return GetLastError();
        if (SetupDiCallClassInstaller(parameters.header.function, self.devices.?, &self.device) == 0) return GetLastError();
        return 0;
    }

    fn disable(context: ?*anyopaque) u32 {
        return change(context, 2); // DICS_DISABLE.
    }

    fn enable(context: ?*anyopaque) u32 {
        return change(context, 1); // DICS_ENABLE; one matching enable, no silent retries.
    }

    // RuntimeRead: EventDriven("Confirmed device restart completed its enable step")
    fn verify(context: ?*anyopaque, restart_required: *u32) u32 {
        const self = from(context);
        var parameters: InstallParameters = .{};
        if (SetupDiGetDeviceInstallParamsW(self.devices.?, &self.device, &parameters) == 0) return GetLastError();
        restart_required.* = @intFromBool(parameters.flags & (0x80 | 0x100) != 0); // DI_NEEDRESTART | DI_NEEDREBOOT.
        var status: u32 = 0;
        var problem: u32 = 0;
        const cr = CM_Get_DevNode_Status(&status, &problem, self.device.instance, 0);
        if (cr != 0) return CM_MapCrToWin32Err(cr, 31);
        if ((status & 8) == 0 or problem != 0) restart_required.* = 1;
        return 0;
    }
};

pub fn restart(instance_id: []const u16, confirmed: bool) core.Result {
    if (builtin.os.tag != .windows) return .{ .code = .unsupported_platform };
    var access: Access = .{};
    return core.restart(&access, Access.adapter, instance_id, confirmed);
}

// Runtime read: live, in the explicit action request. No topology cache is used
// to widen a driver's identity. A shared driver serving distinct devices is
// ambiguous and therefore cannot choose an arbitrary device for the caller.
// RuntimeRead: EventDriven("Report device action requested")
pub fn resolveDriver(module_path: []const u16, output: []u16) core.Result {
    if (output.len != 0) output[0] = 0;
    // ETW report evidence contains the image path; match the exact driver basename.
    const module_name = core.driverModule(module_path) orelse return .{ .code = .invalid_module };
    if (builtin.os.tag != .windows) return .{ .code = .unsupported_platform };
    const devices = SetupDiGetClassDevsW(null, null, null, 2 | 4); // PRESENT | ALLCLASSES.
    if (invalidHandle(devices)) return .{ .code = .enumeration_failed, .detail = GetLastError() };
    defer _ = SetupDiDestroyDeviceInfoList(devices);
    var match: core.UniqueMatch = .{};
    var index: u32 = 0;
    // MemberIndex is a DWORD; reaching its maximum is an explicit failure.
    while (index < std.math.maxInt(u32)) : (index += 1) {
        var device: DeviceInfo = .{};
        if (SetupDiEnumDeviceInfo(devices, index, &device) == 0) {
            const detail = GetLastError();
            if (detail == 259) return match.finish(output); // ERROR_NO_MORE_ITEMS.
            return .{ .code = .enumeration_failed, .detail = detail };
        }
        var service: [257]u16 = @splat(0); // SCM maximum service-name length is 256.
        var kind: u32 = 0;
        var required: u32 = 0;
        if (SetupDiGetDeviceRegistryPropertyW(devices, &device, 4, &kind, @ptrCast(&service), @sizeOf(@TypeOf(service)), &required) == 0) { // SPDRP_SERVICE.
            const detail = GetLastError();
            if (detail == 13 or detail == 2) continue; // This node has no installed service.
            return .{ .code = .enumeration_failed, .detail = detail };
        }
        if (kind != 1 or required < @sizeOf(u16) or required > @sizeOf(@TypeOf(service)) or required % @sizeOf(u16) != 0) return .{ .code = .enumeration_failed, .detail = 13 };
        const service_length = terminatedLength(&service) orelse return .{ .code = .enumeration_failed, .detail = 13 };
        if (service_length == 0) continue;
        const prefix = std.unicode.utf8ToUtf16LeStringLiteral("SYSTEM\\CurrentControlSet\\Services\\");
        var key_path: [prefix.len + service.len:0]u16 = @splat(0);
        @memcpy(key_path[0..prefix.len], prefix);
        @memcpy(key_path[prefix.len..][0..service_length], service[0..service_length]);
        var key: ?Handle = null;
        const opened = RegOpenKeyExW(hkey_local_machine, &key_path, 0, 0x20019, &key); // KEY_READ.
        if (opened == 2) continue;
        if (opened != 0) return .{ .code = .enumeration_failed, .detail = @bitCast(opened) };
        var image: [260]u16 = @splat(0); // Bounded legacy driver path; overflow fails closed.
        var image_size: u32 = @sizeOf(@TypeOf(image));
        const queried = RegQueryValueExW(key.?, std.unicode.utf8ToUtf16LeStringLiteral("ImagePath"), null, &kind, @ptrCast(&image), &image_size);
        _ = RegCloseKey(key.?);
        if (queried == 2) continue;
        if (queried != 0) return .{ .code = .enumeration_failed, .detail = @bitCast(queried) };
        if ((kind != 1 and kind != 2) or image_size < @sizeOf(u16) or image_size > @sizeOf(@TypeOf(image)) or image_size % @sizeOf(u16) != 0) return .{ .code = .enumeration_failed, .detail = 13 };
        const image_length = terminatedLength(&image) orelse return .{ .code = .enumeration_failed, .detail = 13 };
        if (!core.equalInsensitive(module_name, core.moduleName(image[0..image_length]))) continue;
        var instance: [core.instance_id_capacity]u16 = @splat(0);
        if (SetupDiGetDeviceInstanceIdW(devices, &device, &instance, instance.len, &required) == 0) return .{ .code = .enumeration_failed, .detail = GetLastError() };
        const length = terminatedLength(&instance) orelse return .{ .code = .enumeration_failed, .detail = 13 };
        match.offer(instance[0..length]);
        if (match.ambiguous) return .{ .code = .ambiguous_device };
    }
    return .{ .code = .enumeration_failed, .detail = 534 }; // ERROR_ARITHMETIC_OVERFLOW.
}

pub fn terminatedLength(value: []const u16) ?usize {
    var index: usize = 0;
    while (index < value.len) : (index += 1) if (value[index] == 0) return index;
    return null;
}

const hkey_local_machine: Handle = @ptrFromInt(@as(usize, @bitCast(@as(isize, -2147483646))));
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
extern "advapi32" fn CheckTokenMembership(?Handle, *const AdministratorSid, *i32) callconv(.winapi) i32;
extern "setupapi" fn SetupDiCreateDeviceInfoList(?*const anyopaque, ?Handle) callconv(.winapi) Handle;
extern "setupapi" fn SetupDiOpenDeviceInfoW(Handle, [*:0]const u16, ?Handle, u32, *DeviceInfo) callconv(.winapi) i32;
extern "setupapi" fn SetupDiDestroyDeviceInfoList(Handle) callconv(.winapi) i32;
extern "setupapi" fn SetupDiSetClassInstallParamsW(Handle, *DeviceInfo, *ClassHeader, u32) callconv(.winapi) i32;
extern "setupapi" fn SetupDiCallClassInstaller(u32, Handle, *DeviceInfo) callconv(.winapi) i32;
extern "setupapi" fn SetupDiGetDeviceInstallParamsW(Handle, *DeviceInfo, *InstallParameters) callconv(.winapi) i32;
extern "setupapi" fn SetupDiGetClassDevsW(?*const anyopaque, ?[*:0]const u16, ?Handle, u32) callconv(.winapi) Handle;
extern "setupapi" fn SetupDiEnumDeviceInfo(Handle, u32, *DeviceInfo) callconv(.winapi) i32;
extern "setupapi" fn SetupDiGetDeviceRegistryPropertyW(Handle, *DeviceInfo, u32, *u32, [*]u8, u32, *u32) callconv(.winapi) i32;
extern "setupapi" fn SetupDiGetDeviceInstanceIdW(Handle, *DeviceInfo, [*]u16, u32, *u32) callconv(.winapi) i32;
extern "cfgmgr32" fn CM_Get_DevNode_Status(*u32, *u32, u32, u32) callconv(.winapi) u32;
extern "cfgmgr32" fn CM_MapCrToWin32Err(u32, u32) callconv(.winapi) u32;
extern "advapi32" fn RegOpenKeyExW(Handle, [*:0]const u16, u32, u32, *?Handle) callconv(.winapi) i32;
extern "advapi32" fn RegQueryValueExW(Handle, [*:0]const u16, ?*u32, *u32, [*]u8, *u32) callconv(.winapi) i32;
extern "advapi32" fn RegCloseKey(Handle) callconv(.winapi) i32;
