const std = @import("std");

// Platform facts only. The caller owns the borrowed workspace for this synchronous batch.
// Limits are supplied by the original attribution owner, never expanded by this adapter.
pub const Limits = extern struct { maximum_service_bytes: u32, maximum_services: u32, maximum_text: u32 };
pub const Text = extern struct { pointer: ?[*]const u16 = null, length: usize = 0 };
pub const Query = extern struct { pid: i32, reserved: u32 = 0, birth: i64 };
pub const Metadata = extern struct {
    image_status: u32 = 1,
    services_status: u32 = 1,
    name: Text = .{},
    path: Text = .{},
    product: Text = .{},
    services: ?[*]const Text = null,
    service_count: usize = 0,
};
pub const Facts = extern struct {
    expected_birth: i64,
    actual_birth: i64 = 0,
    identity_status: u32 = 2,
    has_actual_birth: u32 = 0,
    product_available: u32 = 0,
    services_available: u32 = 0,
    path: Text = .{},
    product: Text = .{},
    services: ?[*]const Text = null,
    service_count: usize = 0,
};
pub const QueryCallback = *const fn (?*anyopaque, usize, *Query) callconv(.c) i32;
pub const SinkCallback = *const fn (?*anyopaque, usize, *const Metadata) callconv(.c) i32;

fn slice(text: Text) []const u16 {
    return if (text.pointer) |p| p[0..text.length] else &.{};
}
fn white(c: u16) bool {
    return switch (c) {
        0x0009...0x000d, 0x0020, 0x0085, 0x00a0, 0x1680, 0x2000...0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000 => true,
        else => false,
    };
}
fn blank(text: Text) bool {
    for (slice(text)) |c| {
        if (!white(c)) return false;
    }
    return true;
}
fn fileName(path: Text) Text {
    const value = slice(path);
    var start: usize = 0;
    for (value, 0..) |c, i| {
        if (c == '\\' or c == '/') start = i + 1;
    }
    var end = value.len;
    var i = value.len;
    while (i > start) {
        i -= 1;
        if (value[i] == '.') {
            end = i;
            break;
        }
    }
    return .{ .pointer = value.ptr + start, .length = end - start };
}
// Also used by the injected Windows-facts tests; no C# attribution decision remains.
pub fn compose(facts: *const Facts, maximum_text: u32, output: *Metadata) void {
    output.* = .{};
    const ended = facts.identity_status == 1 or (facts.has_actual_birth != 0 and facts.actual_birth != facts.expected_birth);
    if (ended) {
        output.image_status = 2;
        output.services_status = 2;
        return;
    }
    if (facts.has_actual_birth == 0 or facts.actual_birth != facts.expected_birth) return;
    if (facts.path.length > 0 and facts.path.length <= maximum_text and facts.product_available != 0 and facts.product.length <= maximum_text) {
        output.image_status = 0;
        output.path = facts.path;
        output.name = fileName(facts.path);
        if (!blank(facts.product)) output.product = facts.product;
    }
    if (facts.services_available != 0) {
        output.services_status = 0;
        output.services = facts.services;
        output.service_count = facts.service_count;
    }
}

const ServiceStatus = extern struct { service_type: u32, state: u32, controls: u32, exit: u32, specific_exit: u32, checkpoint: u32, wait_hint: u32, pid: u32, flags: u32 };
const ServiceEntry = extern struct { name: ?[*:0]const u16, display: ?[*:0]const u16, status: ServiceStatus };
const Translation = extern struct { language: u16, code_page: u16 };
const Handle = *anyopaque;
extern "kernel32" fn OpenProcess(u32, i32, u32) callconv(.winapi) ?Handle;
extern "kernel32" fn CloseHandle(Handle) callconv(.winapi) i32;
extern "kernel32" fn QueryFullProcessImageNameW(Handle, u32, [*]u16, *u32) callconv(.winapi) i32;
extern "kernel32" fn GetProcessTimes(Handle, *u64, *u64, *u64, *u64) callconv(.winapi) i32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
extern "kernel32" fn GetFileAttributesW([*:0]const u16) callconv(.winapi) u32;
extern "kernel32" fn LoadLibraryExW([*:0]const u16, ?Handle, u32) callconv(.winapi) ?Handle;
extern "kernel32" fn GetProcAddress(Handle, [*:0]const u8) callconv(.winapi) ?*const anyopaque;
extern "kernel32" fn FreeLibrary(Handle) callconv(.winapi) i32;
extern "advapi32" fn OpenSCManagerW(?[*:0]const u16, ?[*:0]const u16, u32) callconv(.winapi) ?Handle;
extern "advapi32" fn CloseServiceHandle(Handle) callconv(.winapi) i32;
extern "advapi32" fn EnumServicesStatusExW(Handle, u32, u32, u32, ?[*]u8, u32, *u32, *u32, *u32, ?[*:0]const u16) callconv(.winapi) i32;

const VersionApi = struct {
    module: Handle,
    size: *const fn (u32, [*:0]const u16, *u32) callconv(.winapi) u32,
    read: *const fn (u32, [*:0]const u16, u32, u32, [*]u8) callconv(.winapi) i32,
    query: *const fn ([*]const u8, [*:0]const u16, *?*anyopaque, *u32) callconv(.winapi) i32,
    fn open() ?VersionApi {
        const module = LoadLibraryExW(std.unicode.utf8ToUtf16LeStringLiteral("version.dll"), null, 0x00000800) orelse return null;
        const size = GetProcAddress(module, "GetFileVersionInfoSizeExW") orelse {
            _ = FreeLibrary(module);
            return null;
        };
        const read = GetProcAddress(module, "GetFileVersionInfoExW") orelse {
            _ = FreeLibrary(module);
            return null;
        };
        const query = GetProcAddress(module, "VerQueryValueW") orelse {
            _ = FreeLibrary(module);
            return null;
        };
        return .{ .module = module, .size = @ptrCast(size), .read = @ptrCast(read), .query = @ptrCast(query) };
    }
};
fn baseBytes(limits: Limits) usize {
    return std.mem.alignForward(usize, limits.maximum_service_bytes, 8) + @as(usize, limits.maximum_services) * @sizeOf(Text) + (@as(usize, limits.maximum_text) + 1) * 2;
}
pub fn workspaceBytes(limits: Limits) usize {
    return baseBytes(limits);
}
fn pathBuffer(memory: []align(8) u8, limits: Limits) []u16 {
    const offset = std.mem.alignForward(usize, limits.maximum_service_bytes, 8) + @as(usize, limits.maximum_services) * @sizeOf(Text);
    return @as([*]u16, @ptrCast(@alignCast(memory.ptr + offset)))[0 .. @as(usize, limits.maximum_text) + 1];
}
fn readPath(query: Query, buffer: []u16) Text {
    if (query.pid <= 0) return .{};
    const process = OpenProcess(0x1000, 0, @intCast(query.pid)) orelse return .{};
    defer _ = CloseHandle(process);
    var birth: u64 = 0;
    var exit: u64 = 0;
    var kernel: u64 = 0;
    var user: u64 = 0;
    if (GetProcessTimes(process, &birth, &exit, &kernel, &user) == 0 or birth != @as(u64, @bitCast(query.birth)) or exit != 0) return .{};
    var count: u32 = @intCast(buffer.len - 1);
    if (QueryFullProcessImageNameW(process, 0, buffer.ptr, &count) == 0 or count == 0 or count >= buffer.len) return .{};
    buffer[count] = 0;
    return .{ .pointer = buffer.ptr, .length = count };
}
fn readIdentity(pid: i32, facts: *Facts) void {
    const process = OpenProcess(0x1000, 0, @intCast(pid)) orelse {
        facts.identity_status = if (GetLastError() == 87) 1 else 2;
        return;
    };
    defer _ = CloseHandle(process);
    var birth: u64 = 0;
    var exit: u64 = 0;
    var kernel: u64 = 0;
    var user: u64 = 0;
    if (GetProcessTimes(process, &birth, &exit, &kernel, &user) == 0) return;
    if (exit != 0) {
        facts.identity_status = 1;
        return;
    }
    if (birth > 0 and birth <= std.math.maxInt(i64)) {
        facts.identity_status = 0;
        facts.has_actual_birth = 1;
        facts.actual_birth = @intCast(birth);
    }
}
// Size query borrows the base workspace, so even the bounded Windows path buffer is off-stack.
// Exact version-resource demand is measured, not capped by a second default.
pub fn required(context: ?*anyopaque, get_query: QueryCallback, count: usize, limits: Limits, memory: []align(8) u8) usize {
    if (memory.len < baseBytes(limits) or count > limits.maximum_services) return 0;
    const api = VersionApi.open() orelse return baseBytes(limits);
    defer _ = FreeLibrary(api.module);
    var maximum: usize = 0;
    const path = pathBuffer(memory, limits);
    for (0..count) |i| {
        var q: Query = undefined;
        if (get_query(context, i, &q) != 0) return 0;
        const value = readPath(q, path);
        if (value.length == 0) continue;
        var ignored: u32 = 0;
        maximum = @max(maximum, api.size(1, @ptrCast(path.ptr), &ignored));
    }
    return baseBytes(limits) + maximum;
}
// Behavioral reference: dotnet/runtime d9a38d9d8b5660ff45d67cf52644a9be65372009,
// System.Diagnostics.FileVersionInfo/FileVersionInfo.Windows.cs and FileVersionInfo.cs (MIT).
// This independently written projection preserves its language fallback and FileVersion stop rule.
fn versionString(api: VersionApi, memory: []const u8, pair: Translation, comptime field: []const u8) Text {
    var key: [64]u16 = undefined;
    const prefix = std.unicode.utf8ToUtf16LeStringLiteral("\\StringFileInfo\\");
    @memcpy(key[0..prefix.len], prefix);
    const hex = "0123456789ABCDEF";
    var at: usize = prefix.len;
    for ([_]u16{ pair.language, pair.code_page }) |number| {
        var shift: u4 = 12;
        for (0..4) |digit| {
            key[at] = hex[(number >> shift) & 15];
            at += 1;
            if (digit != 3) shift -= 4;
        }
    }
    const suffix = std.unicode.utf8ToUtf16LeStringLiteral(field);
    @memcpy(key[at .. at + suffix.len], suffix);
    at += suffix.len;
    key[at] = 0;
    var value: ?*anyopaque = null;
    var characters: u32 = 0;
    if (api.query(memory.ptr, @ptrCast(&key), &value, &characters) == 0 or value == null or characters == 0) return .{};
    const address = @intFromPtr(value.?);
    const start = @intFromPtr(memory.ptr);
    if (address < start or address >= start + memory.len or @as(usize, characters) * 2 > start + memory.len - address or address % 2 != 0) return .{};
    const pointer: [*]const u16 = @ptrCast(@alignCast(value.?));
    // FileVersionInfo reads null-terminated strings; stop at the first terminator.
    for (pointer[0..characters], 0..) |c, index| {
        if (c == 0) return .{ .pointer = pointer, .length = index };
    }
    return .{};
}
fn resourceProduct(api: VersionApi, path: [*:0]const u16, memory: []u8, facts: *Facts) void {
    const attributes = GetFileAttributesW(path);
    if (attributes == 0xffffffff or attributes & 0x10 != 0) return;
    var ignored: u32 = 0;
    const bytes = api.size(1, path, &ignored);
    // FileVersionInfo does not throw for an absent/unreadable version resource once
    // the file existence check succeeds; the product is then genuinely absent.
    facts.product_available = 1;
    if (bytes == 0) return;
    if (bytes > memory.len) {
        facts.product_available = 0;
        return;
    }
    if (api.read(3, path, 0, bytes, memory.ptr) == 0) return;
    var translations: ?*anyopaque = null;
    var length: u32 = 0;
    var declared: Translation = .{ .language = 0x0409, .code_page = 0x04e4 };
    if (api.query(memory.ptr, std.unicode.utf8ToUtf16LeStringLiteral("\\VarFileInfo\\Translation"), &translations, &length) != 0 and translations != null and length >= @sizeOf(Translation)) {
        const address = @intFromPtr(translations.?);
        const start = @intFromPtr(memory.ptr);
        if (address >= start and address + @sizeOf(Translation) <= start + memory.len) declared = @as(*align(1) const Translation, @ptrCast(translations.?)).*;
    }
    const pairs = [_]Translation{ declared, .{ .language = 0x0409, .code_page = 0x04b0 }, .{ .language = 0x0409, .code_page = 0x04e4 }, .{ .language = 0x0409, .code_page = 0 } };
    for (pairs, 0..) |pair, index| {
        if (index != 0 and pair.language == declared.language and pair.code_page == declared.code_page) continue;
        facts.product = versionString(api, memory[0..bytes], pair, "\\ProductName");
        if (versionString(api, memory[0..bytes], pair, "\\FileVersion").length != 0) break;
    }
}
fn boundedName(pointer: ?[*:0]const u16, memory: []const u8) ?Text {
    const p = pointer orelse return null;
    const address = @intFromPtr(p);
    const start = @intFromPtr(memory.ptr);
    if (address < start or address >= start + memory.len or address % 2 != 0) return null;
    const available = (start + memory.len - address) / 2;
    for (p[0..available], 0..) |c, i| {
        if (c == 0) return .{ .pointer = p, .length = i };
    }
    return null;
}
pub fn capture(context: ?*anyopaque, get_query: QueryCallback, sink: SinkCallback, count: usize, limits: Limits, memory: []align(8) u8) i32 {
    if (count > limits.maximum_services or memory.len < baseBytes(limits)) return 1;
    const service_memory = memory[0..limits.maximum_service_bytes];
    const text_offset = std.mem.alignForward(usize, limits.maximum_service_bytes, 8);
    const service_text: []Text = @as([*]Text, @ptrCast(@alignCast(memory.ptr + text_offset)))[0..limits.maximum_services];
    var service_count: u32 = 0;
    var services_available = false;
    if (OpenSCManagerW(null, null, 0x0004)) |manager| {
        defer _ = CloseServiceHandle(manager);
        var required_bytes: u32 = 0;
        var resume_handle: u32 = 0;
        const sized = EnumServicesStatusExW(manager, 0, 0x30, 3, null, 0, &required_bytes, &service_count, &resume_handle, null);
        if (sized != 0 and service_count == 0 and required_bytes == 0) {
            services_available = true;
        } else if (required_bytes > 0 and required_bytes <= service_memory.len and GetLastError() == 234) {
            resume_handle = 0;
            if (EnumServicesStatusExW(manager, 0, 0x30, 3, service_memory.ptr, required_bytes, &required_bytes, &service_count, &resume_handle, null) != 0 and service_count <= limits.maximum_services and resume_handle == 0 and @as(usize, service_count) * @sizeOf(ServiceEntry) <= service_memory.len) services_available = true;
        }
    }
    const api = VersionApi.open();
    defer {
        if (api) |a| _ = FreeLibrary(a.module);
    }
    const path = pathBuffer(memory, limits);
    const version_memory = memory[baseBytes(limits)..];
    for (0..count) |index| {
        var q: Query = undefined;
        if (get_query(context, index, &q) != 0) return 2;
        var facts: Facts = .{ .expected_birth = q.birth };
        facts.path = readPath(q, path);
        if (facts.path.length > 0) {
            if (api) |a| resourceProduct(a, @ptrCast(path.ptr), version_memory, &facts);
        }
        var names: usize = 0;
        if (services_available) {
            const entries: [*]const ServiceEntry = @ptrCast(@alignCast(service_memory.ptr));
            for (0..service_count) |i| {
                if (entries[i].status.pid != @as(u32, @intCast(q.pid))) continue;
                if (boundedName(entries[i].name, service_memory)) |name| {
                    if (!blank(name)) {
                        service_text[names] = name;
                        names += 1;
                    }
                }
            }
            facts.services_available = 1;
            facts.services = service_text.ptr;
            facts.service_count = names;
        }
        readIdentity(q.pid, &facts);
        var result: Metadata = .{};
        compose(&facts, limits.maximum_text, &result);
        if (sink(context, index, &result) != 0) return 2;
    }
    return 0;
}
