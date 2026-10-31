const std = @import("std");
const c = @import("c.zig").win;
const paths = @import("path.zig");
const wide = std.unicode.utf8ToUtf16LeStringLiteral;
pub const maximum_file_bytes = 72;
const maximum_components = 513;
const security_capacity = 128 * 1024;

pub const Key = struct {
    bytes: [maximum_file_bytes]u8 = @splat(0),
    len: usize = 0,
    pub fn value(self: *const Key) []const u8 {
        return self.bytes[0..self.len];
    }
    pub fn clear(self: *Key) void {
        std.crypto.secureZero(u8, &self.bytes);
        self.len = 0;
    }
};

pub fn decode(bytes: []const u8) !Key {
    if (bytes.len > maximum_file_bytes) return error.KeyTooLong;
    var n = bytes.len;
    while (n > 0 and (bytes[n - 1] == '\r' or bytes[n - 1] == '\n')) n -= 1;
    var key: Key = .{};
    if (n == 0) return key;
    if (n != 70 or !std.mem.startsWith(u8, bytes[0..n], "rm_sk_")) return error.InvalidKey;
    for (bytes[6..n]) |ch| if (!std.ascii.isHex(ch)) return error.InvalidKey;
    @memcpy(key.bytes[0..n], bytes[0..n]);
    key.len = n;
    return key;
}

const Stamp = struct {
    exists: bool = false,
    volume: u32 = 0,
    index_high: u32 = 0,
    index_low: u32 = 0,
    write: i64 = 0,
    change: i64 = 0,
    size_high: u32 = 0,
    size_low: u32 = 0,
    attributes: u32 = 0,
};
const Security = struct {
    bytes: [security_capacity]u8 align(8) = undefined,
    len: usize = 0,
    fn capture(self: *Security, handle: c.HANDLE) !void {
        self.len = 0;
        var needed: u32 = 0;
        if (c.GetKernelObjectSecurity(handle, c.OWNER_SECURITY_INFORMATION | c.DACL_SECURITY_INFORMATION, &self.bytes, self.bytes.len, &needed) == 0) return error.SecurityReadFailed;
        if (needed < 20 or needed > self.bytes.len) return error.InvalidSecurity;
        self.len = needed;
    }
};
const Snapshot = struct {
    stamps: [maximum_components]Stamp = undefined,
    security: [2]Security = .{ .{}, .{} },
    count: usize = 0,
    fn same(a: *const Snapshot, b: *const Snapshot) bool {
        if (a.count != b.count) return false;
        for (a.stamps[0..a.count], b.stamps[0..b.count]) |x, y| if (!std.meta.eql(x, y)) return false;
        for (&a.security, &b.security) |*x, *y| if (!std.mem.eql(u8, x.bytes[0..x.len], y.bytes[0..y.len])) return false;
        return true;
    }
};

/// Scratch has a fixed bound and may be allocated by the host's bounded allocator.
/// No timestamp cache: each request fully validates and compares two snapshots.
pub const Reader = struct {
    before: Snapshot = .{},
    after: Snapshot = .{},
    path: paths.Buffer = undefined,
    pub fn read(self: *Reader, package_root: []const u16, stop: c.HANDLE) !?Key {
        const root = try paths.full(package_root, &self.path);
        var n = root.len;
        while (n > 0 and self.path[n - 1] == '\\') n -= 1;
        const suffix = wide("\\UserData\\Security\\default-key");
        if (n + suffix.len >= self.path.len) return error.InvalidPath;
        @memcpy(self.path[n..][0..suffix.len], suffix);
        n += suffix.len;
        self.path[n] = 0;
        try self.capture(&self.before, n, stop);
        // The directory is validated even when the file does not exist.
        if (self.before.stamps[1].exists) try verifySecurity(&self.before.security[1], true);
        if (!self.before.stamps[0].exists) return null;
        if (!self.before.stamps[1].exists) return error.DirectoryMissing;
        try verifySecurity(&self.before.security[0], false);
        const file = c.CreateFileW(&self.path, c.GENERIC_READ, c.FILE_SHARE_READ | c.FILE_SHARE_WRITE | c.FILE_SHARE_DELETE, null, c.OPEN_EXISTING, c.FILE_FLAG_OPEN_REPARSE_POINT | c.FILE_FLAG_OVERLAPPED, null);
        if (file == c.INVALID_HANDLE_VALUE) return error.KeyReadFailed;
        defer _ = c.CloseHandle(file);
        var raw: [maximum_file_bytes + 1]u8 = undefined;
        defer std.crypto.secureZero(u8, &raw);
        var count: u32 = 0;
        const completed = c.CreateEventW(null, 1, 0, null) orelse return error.EventUnavailable;
        defer _ = c.CloseHandle(completed);
        var overlapped: c.OVERLAPPED = std.mem.zeroes(c.OVERLAPPED);
        overlapped.hEvent = completed;
        if (c.ReadFile(file, &raw, raw.len, &count, &overlapped) == 0) {
            if (c.GetLastError() != c.ERROR_IO_PENDING) return error.KeyReadFailed;
            const handles = [_]c.HANDLE{ stop, completed };
            const result = c.WaitForMultipleObjects(handles.len, &handles, 0, c.INFINITE);
            if (result != c.WAIT_OBJECT_0 + 1) {
                _ = c.CancelIoEx(file, &overlapped);
                _ = c.GetOverlappedResult(file, &overlapped, &count, 1);
                return if (result == c.WAIT_OBJECT_0) error.Stopped else error.KeyReadFailed;
            }
            if (c.GetOverlappedResult(file, &overlapped, &count, 0) == 0) return error.KeyReadFailed;
        }
        var key = try decode(raw[0..count]);
        errdefer key.clear();
        try self.capture(&self.after, n, stop);
        if (!self.before.same(&self.after)) return error.StorageChangedDuringValidation;
        return key;
    }

    fn capture(self: *Reader, snapshot: *Snapshot, length: usize, stop: c.HANDLE) !void {
        snapshot.count = 0;
        for (&snapshot.security) |*security| security.len = 0;
        const root = try paths.rootLength(self.path[0..length]);
        var n = length;
        while (true) {
            if (c.WaitForSingleObject(stop, 0) != c.WAIT_TIMEOUT) return error.Stopped;
            if (snapshot.count == maximum_components) return error.TooManyPathComponents;
            const saved = self.path[n];
            self.path[n] = 0;
            const index = snapshot.count;
            snapshot.stamps[index] = readStamp(@ptrCast(&self.path), if (index < 2) &snapshot.security[index] else null, index < 2) catch |err| {
                self.path[n] = saved;
                return err;
            };
            self.path[n] = saved;
            snapshot.count += 1;
            if (n <= root) break;
            while (n > root and self.path[n - 1] != '\\') n -= 1;
            if (n > root) n -= 1;
        }
        if (snapshot.count < 2) return error.InvalidPath;
    }
};

fn readStamp(path: [*:0]const u16, security: ?*Security, changes: bool) !Stamp {
    const access = @as(c.DWORD, c.FILE_READ_ATTRIBUTES) | (if (security != null) @as(c.DWORD, c.READ_CONTROL) else 0);
    const handle = c.CreateFileW(path, access, c.FILE_SHARE_READ | c.FILE_SHARE_WRITE | c.FILE_SHARE_DELETE, null, c.OPEN_EXISTING, c.FILE_FLAG_BACKUP_SEMANTICS | c.FILE_FLAG_OPEN_REPARSE_POINT, null);
    if (handle == c.INVALID_HANDLE_VALUE) {
        const err = c.GetLastError();
        if (err == c.ERROR_FILE_NOT_FOUND or err == c.ERROR_PATH_NOT_FOUND) return .{};
        return error.MetadataReadFailed;
    }
    defer _ = c.CloseHandle(handle);
    var identity: c.BY_HANDLE_FILE_INFORMATION = undefined;
    var basic: c.FILE_BASIC_INFO = undefined;
    if (c.GetFileInformationByHandle(handle, &identity) == 0 or c.GetFileInformationByHandleEx(handle, c.FileBasicInfo, &basic, @sizeOf(@TypeOf(basic))) == 0) return error.MetadataReadFailed;
    if (basic.FileAttributes & c.FILE_ATTRIBUTE_REPARSE_POINT != 0) return error.ReparsePoint;
    if (security) |s| try s.capture(handle);
    if (changes and identity.nFileSizeHigh != 0) return error.KeyTooLong;
    if (security != null and !changes) unreachable;
    return .{ .exists = true, .volume = identity.dwVolumeSerialNumber, .index_high = identity.nFileIndexHigh, .index_low = identity.nFileIndexLow, .write = if (changes) basic.LastWriteTime.QuadPart else 0, .change = if (changes) basic.ChangeTime.QuadPart else 0, .size_high = identity.nFileSizeHigh, .size_low = identity.nFileSizeLow, .attributes = basic.FileAttributes };
}

fn sidIs(sid: c.PSID, kind: c.WELL_KNOWN_SID_TYPE) bool {
    return c.IsWellKnownSid(sid, kind) != 0;
}

fn verifySecurity(security: *Security, directory: bool) !void {
    const sd: c.PSECURITY_DESCRIPTOR = &security.bytes;
    if (security.len < 20 or c.IsValidSecurityDescriptor(sd) == 0) return error.InvalidSecurity;
    var owner: c.PSID = null;
    var defaulted: c.BOOL = 0;
    if (c.GetSecurityDescriptorOwner(sd, &owner, &defaulted) == 0 or (!sidIs(owner, c.WinLocalSystemSid) and !sidIs(owner, c.WinBuiltinAdministratorsSid))) return error.UntrustedOwner;
    var control: c.SECURITY_DESCRIPTOR_CONTROL = 0;
    var revision: u32 = 0;
    if (c.GetSecurityDescriptorControl(sd, &control, &revision) == 0 or control & c.SE_DACL_PROTECTED == 0) return error.UnprotectedAcl;
    var present: c.BOOL = 0;
    var acl: c.PACL = null;
    if (c.GetSecurityDescriptorDacl(sd, &present, &acl, &defaulted) == 0 or present == 0 or acl == null or c.IsValidAcl(acl) == 0) return error.InvalidAcl;
    var system_full = false;
    var admin_full = false;
    var users_read = false;
    var index: u32 = 0;
    while (index < acl.*.AceCount) : (index += 1) {
        var raw: ?*anyopaque = null;
        if (c.GetAce(acl, index, &raw) == 0) return error.InvalidAcl;
        const ace: *align(1) const c.ACCESS_ALLOWED_ACE = @ptrCast(raw.?);
        const flags = ace.Header.AceFlags;
        if (ace.Header.AceType != c.ACCESS_ALLOWED_ACE_TYPE or flags & ~@as(u8, c.OBJECT_INHERIT_ACE | c.CONTAINER_INHERIT_ACE) != 0) return error.UnsupportedAcl;
        const sid: c.PSID = @ptrCast(@constCast(&ace.SidStart));
        if (sidIs(sid, c.WinLocalSystemSid) or sidIs(sid, c.WinBuiltinAdministratorsSid)) {
            if (ace.Mask != c.FILE_ALL_ACCESS or (directory and flags != c.OBJECT_INHERIT_ACE | c.CONTAINER_INHERIT_ACE)) return error.UnsupportedAcl;
            if (sidIs(sid, c.WinLocalSystemSid)) system_full = true else admin_full = true;
        } else if (sidIs(sid, c.WinBuiltinUsersSid)) {
            const rights: u32 = if (directory) c.FILE_TRAVERSE | c.FILE_READ_ATTRIBUTES | c.READ_CONTROL | c.SYNCHRONIZE else c.FILE_GENERIC_READ;
            if (ace.Mask != rights or flags != 0) return error.UnsupportedAcl;
            users_read = true;
        } else return error.UnexpectedPrincipal;
    }
    if (!system_full or !admin_full or (!directory and !users_read)) return error.IncompleteAcl;
}

test "key wire format and bounded length" {
    const valid = "rm_sk_" ++ "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    var key = try decode(valid ++ "\r\n");
    defer key.clear();
    try std.testing.expectEqualStrings(valid, key.value());
    try std.testing.expectError(error.KeyTooLong, decode(valid ++ "\r\n\n"));
    try std.testing.expectError(error.InvalidKey, decode("rm_sk_" ++ "gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg"));
    try std.testing.expectError(error.InvalidKey, decode(valid ++ " "));
    try std.testing.expectEqual(@as(usize, 0), (try decode("\r\n")).len);
}

test "trusted ACL policy matches directory and key file rules" {
    const cases = [_]struct { sddl: [:0]const u16, directory: bool, accepted: bool }{
        .{ .sddl = wide("O:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;BU)"), .directory = false, .accepted = true },
        .{ .sddl = wide("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x1200a0;;;BU)"), .directory = true, .accepted = true },
        .{ .sddl = wide("O:SYD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;BU)"), .directory = false, .accepted = false },
        .{ .sddl = wide("O:BUD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;BU)"), .directory = false, .accepted = false },
        .{ .sddl = wide("O:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;BU)"), .directory = false, .accepted = false },
        .{ .sddl = wide("O:SYD:P(A;;FA;;;SY)(A;;FR;;;BU)"), .directory = false, .accepted = false },
        .{ .sddl = wide("O:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(D;;FR;;;BU)"), .directory = false, .accepted = false },
    };
    var security: Security = .{};
    for (cases) |case| {
        var descriptor: c.PSECURITY_DESCRIPTOR = null;
        var n: u32 = 0;
        try std.testing.expect(c.ConvertStringSecurityDescriptorToSecurityDescriptorW(case.sddl.ptr, c.SDDL_REVISION_1, &descriptor, &n) != 0);
        defer _ = c.LocalFree(descriptor);
        try std.testing.expect(n <= security.bytes.len);
        @memcpy(security.bytes[0..n], @as([*]const u8, @ptrCast(descriptor))[0..n]);
        security.len = n;
        if (verifySecurity(&security, case.directory)) |_| {
            try std.testing.expect(case.accepted);
        } else |_| {
            try std.testing.expect(!case.accepted);
        }
    }
}
