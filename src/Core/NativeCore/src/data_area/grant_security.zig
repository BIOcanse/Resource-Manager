//! Explicit creation security for an unnamed data-area section. This leaf does
//! not wire Section.create: the caller must pass attributes() at creation time.
const std = @import("std");
const windows = std.os.windows;

pub const Error = error{ SecurityFailed, UnsupportedServiceIdentity };

// Win32 SDK layout/rights constants, not configurable operating policy.
const security_max_sid_size = 68;
const token_user_class: u32 = 1;
const win_local_system_sid: u32 = 22;
const acl_revision: u32 = 2;
const security_descriptor_revision: u32 = 1;
const se_dacl_protected: u16 = 0x1000;
const file_map_all_access: u32 = 0x000f001f;
const error_invalid_data: u32 = 13;
const error_insufficient_buffer: u32 = 122;
const error_invalid_sid: u32 = 1337;
const error_not_supported: u32 = 50;
const lptr: u32 = 0x0040; // LMEM_FIXED | LMEM_ZEROINIT.

const SidAndAttributes = extern struct { sid: ?*anyopaque, attributes: u32 };
const TokenUser = extern struct { user: SidAndAttributes };
const Acl = extern struct {
    revision: u8,
    sbz1: u8,
    size: u16,
    ace_count: u16,
    sbz2: u16,
};
const SecurityDescriptor = extern struct {
    revision: u8,
    sbz1: u8,
    control: u16,
    owner: ?*anyopaque,
    group: ?*anyopaque,
    sacl: ?*Acl,
    dacl: ?*Acl,
};
// ACCESS_ALLOWED_ACE has an 8-byte header/mask before its variable-length SID.
const acl_capacity = @sizeOf(Acl) + 8 + security_max_sid_size;
const Storage = extern struct {
    descriptor: SecurityDescriptor,
    acl: [acl_capacity]u8 align(@alignOf(Acl)),
    owner_sid: [security_max_sid_size]u8 align(@alignOf(u32)),
};

extern "advapi32" fn GetTokenInformation(token: windows.HANDLE, information_class: u32, information: ?*anyopaque, information_length: u32, return_length: *u32) callconv(.winapi) i32;
extern "advapi32" fn IsValidSid(sid: *const anyopaque) callconv(.winapi) i32;
extern "advapi32" fn GetLengthSid(sid: *const anyopaque) callconv(.winapi) u32;
extern "advapi32" fn CopySid(length: u32, destination: *anyopaque, source: *const anyopaque) callconv(.winapi) i32;
extern "advapi32" fn CreateWellKnownSid(kind: u32, domain: ?*const anyopaque, sid: *anyopaque, length: *u32) callconv(.winapi) i32;
extern "advapi32" fn EqualSid(first: *const anyopaque, second: *const anyopaque) callconv(.winapi) i32;
extern "advapi32" fn InitializeAcl(acl: *Acl, length: u32, revision: u32) callconv(.winapi) i32;
extern "advapi32" fn AddAccessAllowedAce(acl: *Acl, revision: u32, access: u32, sid: *const anyopaque) callconv(.winapi) i32;
extern "advapi32" fn InitializeSecurityDescriptor(descriptor: *SecurityDescriptor, revision: u32) callconv(.winapi) i32;
extern "advapi32" fn SetSecurityDescriptorOwner(descriptor: *SecurityDescriptor, owner: *anyopaque, defaulted: i32) callconv(.winapi) i32;
extern "advapi32" fn SetSecurityDescriptorDacl(descriptor: *SecurityDescriptor, present: i32, acl: *Acl, defaulted: i32) callconv(.winapi) i32;
extern "advapi32" fn SetSecurityDescriptorControl(descriptor: *SecurityDescriptor, bits: u16, values: u16) callconv(.winapi) i32;
extern "kernel32" fn LocalAlloc(flags: u32, bytes: usize) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn LocalFree(memory: *anyopaque) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;

/// Owns one stable LocalAlloc allocation. Treat initialized values as move-only:
/// use one deinit, and keep attributes alive only until its matching deinit.
pub const Security = struct {
    storage: ?*Storage,

    /// The deployed service runs as LocalSystem. Require that live TokenUser
    /// identity: accepting an interactive user's SID as owner/full-access ACE
    /// would authorize that user's other processes to regain write rights.
    /// Never copies a default DACL. The borrowed token needs TOKEN_QUERY and
    /// is never closed here. Unsupported identity reports ERROR_NOT_SUPPORTED.
    pub fn init(service_token: windows.HANDLE, native_error: *u32) Error!Security {
        native_error.* = 0;
        var token_buffer: [@sizeOf(TokenUser) + security_max_sid_size]u8 align(@alignOf(TokenUser)) = undefined;
        var token_bytes: u32 = 0;
        try checked(GetTokenInformation(service_token, token_user_class, &token_buffer, token_buffer.len, &token_bytes), native_error);
        if (token_bytes < @sizeOf(TokenUser) or token_bytes > token_buffer.len) {
            native_error.* = error_invalid_data;
            return error.SecurityFailed;
        }
        const token: *const TokenUser = @ptrCast(&token_buffer);
        const service_sid = token.user.sid orelse {
            native_error.* = error_invalid_sid;
            return error.SecurityFailed;
        };
        // Validate that the SID and its maximum subauthority storage are in the
        // returned token buffer before handing them to SID APIs.
        const begin = @intFromPtr(&token_buffer);
        const end = begin + token_bytes;
        const sid_begin = @intFromPtr(service_sid);
        if (sid_begin < begin + @sizeOf(TokenUser) or sid_begin > end or end - sid_begin < 8) {
            native_error.* = error_invalid_sid;
            return error.SecurityFailed;
        }
        const sid_header: [*]const u8 = @ptrCast(service_sid);
        const sid_bytes = @as(usize, 8) + @as(usize, sid_header[1]) * @sizeOf(u32);
        if (sid_bytes > security_max_sid_size or sid_bytes > end - sid_begin) {
            native_error.* = error_insufficient_buffer;
            return error.SecurityFailed;
        }
        if (IsValidSid(service_sid) == 0) {
            // IsValidSid does not promise GetLastError on failure.
            native_error.* = error_invalid_sid;
            return error.SecurityFailed;
        }
        if (GetLengthSid(service_sid) != sid_bytes) {
            native_error.* = error_invalid_sid;
            return error.SecurityFailed;
        }

        var system_sid: [security_max_sid_size]u8 align(@alignOf(u32)) = undefined;
        var system_bytes: u32 = system_sid.len;
        try checked(CreateWellKnownSid(win_local_system_sid, null, &system_sid, &system_bytes), native_error);
        if (EqualSid(service_sid, &system_sid) == 0) {
            native_error.* = error_not_supported;
            return error.UnsupportedServiceIdentity;
        }

        const allocation = LocalAlloc(lptr, @sizeOf(Storage)) orelse {
            native_error.* = GetLastError();
            return error.SecurityFailed;
        };
        errdefer _ = LocalFree(allocation);
        const storage: *Storage = @ptrCast(@alignCast(allocation));
        try checked(CopySid(storage.owner_sid.len, &storage.owner_sid, service_sid), native_error);
        const acl: *Acl = @ptrCast(&storage.acl);
        try checked(InitializeAcl(acl, storage.acl.len, acl_revision), native_error);
        try checked(AddAccessAllowedAce(acl, acl_revision, file_map_all_access, &storage.owner_sid), native_error);
        try checked(InitializeSecurityDescriptor(&storage.descriptor, security_descriptor_revision), native_error);
        try checked(SetSecurityDescriptorOwner(&storage.descriptor, &storage.owner_sid, 0), native_error);
        try checked(SetSecurityDescriptorDacl(&storage.descriptor, 1, acl, 0), native_error);
        try checked(SetSecurityDescriptorControl(&storage.descriptor, se_dacl_protected, se_dacl_protected), native_error);
        return .{ .storage = storage };
    }

    /// Pass the address of this returned value to CreateFileMappingW. Calling
    /// after deinit is a programming error; never falls back to a default DACL.
    pub fn attributes(self: *Security) windows.SECURITY_ATTRIBUTES {
        const storage = self.storage orelse @panic("section security already released");
        return .{
            .nLength = @sizeOf(windows.SECURITY_ATTRIBUTES),
            .lpSecurityDescriptor = &storage.descriptor,
            .bInheritHandle = .FALSE,
        };
    }

    /// Releases the entire descriptor/ACL/SID allocation. Repeated calls on the
    /// same owner are harmless; copies of the owner must not be deinitialized.
    pub fn deinit(self: *Security) void {
        if (self.storage) |storage| {
            _ = LocalFree(storage);
            self.storage = null;
        }
    }
};

fn checked(result: i32, native_error: *u32) Error!void {
    if (result == 0) {
        // Capture before the caller's errdefer frees any LocalAlloc storage.
        native_error.* = GetLastError();
        return error.SecurityFailed;
    }
}
