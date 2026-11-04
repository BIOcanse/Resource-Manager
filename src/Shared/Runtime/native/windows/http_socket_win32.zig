//! Narrow Windows SDK ABI. Constants below are OS identifiers, not host policy.
pub const Handle = *anyopaque;
pub const Socket = usize;
pub const invalid_socket = @import("std").math.maxInt(Socket);
pub const invalid_handle: Handle = @ptrFromInt(invalid_socket);
pub const pending = 997;
pub const aborted = 995;
pub const timeout = 258;
pub const infinite = 0xffffffff;
pub const af_inet = 2;
pub const af_inet6 = 23;
pub const sol_socket = 0xffff;
pub const so_update_accept_context = 0x700b;
pub const so_exclusiveaddruse = ~@as(c_int, 4);
pub const so_rcvbuf = 0x1002;
pub const so_sndbuf = 0x1001;
pub const Overlapped = extern struct {
    internal: usize = 0,
    internal_high: usize = 0,
    offset: u32 = 0,
    offset_high: u32 = 0,
    event: ?Handle = null,
};
pub const Buffer = extern struct { len: u32, ptr: [*]u8 };
pub const Address4 = extern struct {
    family: u16 = af_inet,
    port: u16,
    address: [4]u8 = .{ 127, 0, 0, 1 },
    zero: [8]u8 = @splat(0),
};
pub const Address6 = extern struct {
    family: u16 = af_inet6,
    port: u16,
    flow: u32 = 0,
    address: [16]u8 = .{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 },
    scope: u32 = 0,
};
pub const WsaData = extern struct {
    version: u16,
    high_version: u16,
    max_sockets: u16,
    max_udp: u16,
    vendor: ?[*]u8,
    description: [257]u8,
    system: [129]u8,
};
pub const Guid = extern struct { a: u32, b: u16, c: u16, d: [8]u8 };
pub const accept_ex_guid: Guid = .{ .a = 0xb5367df1, .b = 0xcbac, .c = 0x11cf, .d = .{ 0x95, 0xca, 0, 0x80, 0x5f, 0x48, 0xa1, 0x92 } };
pub const AcceptEx = *const fn (Socket, Socket, [*]u8, u32, u32, u32, *u32, *Overlapped) callconv(.winapi) c_int;
pub extern "ws2_32" fn WSAStartup(u16, *WsaData) callconv(.winapi) c_int;
pub extern "ws2_32" fn WSACleanup() callconv(.winapi) c_int;
pub extern "ws2_32" fn WSAGetLastError() callconv(.winapi) c_int;
pub extern "ws2_32" fn WSASocketW(c_int, c_int, c_int, ?*anyopaque, u32, u32) callconv(.winapi) Socket;
pub extern "ws2_32" fn WSAIoctl(Socket, u32, *const anyopaque, u32, *anyopaque, u32, *u32, ?*Overlapped, ?*anyopaque) callconv(.winapi) c_int;
pub extern "ws2_32" fn bind(Socket, *const anyopaque, c_int) callconv(.winapi) c_int;
pub extern "ws2_32" fn listen(Socket, c_int) callconv(.winapi) c_int;
pub extern "ws2_32" fn setsockopt(Socket, c_int, c_int, *const anyopaque, c_int) callconv(.winapi) c_int;
pub extern "ws2_32" fn getsockname(Socket, *anyopaque, *c_int) callconv(.winapi) c_int;
pub extern "ws2_32" fn getpeername(Socket, *anyopaque, *c_int) callconv(.winapi) c_int;
pub extern "ws2_32" fn closesocket(Socket) callconv(.winapi) c_int;
pub extern "ws2_32" fn WSARecv(Socket, *Buffer, u32, ?*u32, *u32, *Overlapped, ?*anyopaque) callconv(.winapi) c_int;
pub extern "ws2_32" fn WSASend(Socket, *Buffer, u32, ?*u32, u32, *Overlapped, ?*anyopaque) callconv(.winapi) c_int;
pub extern "ws2_32" fn connect(Socket, *const anyopaque, c_int) callconv(.winapi) c_int;
pub extern "ws2_32" fn send(Socket, [*]const u8, c_int, c_int) callconv(.winapi) c_int;
pub extern "ws2_32" fn recv(Socket, [*]u8, c_int, c_int) callconv(.winapi) c_int;
pub extern "ws2_32" fn shutdown(Socket, c_int) callconv(.winapi) c_int;
pub extern "kernel32" fn CreateIoCompletionPort(Handle, ?Handle, usize, u32) callconv(.winapi) ?Handle;
pub extern "kernel32" fn GetQueuedCompletionStatus(Handle, *u32, *usize, *?*Overlapped, u32) callconv(.winapi) c_int;
pub extern "kernel32" fn PostQueuedCompletionStatus(Handle, u32, usize, ?*Overlapped) callconv(.winapi) c_int;
pub extern "kernel32" fn CancelIoEx(Handle, ?*Overlapped) callconv(.winapi) c_int;
pub extern "kernel32" fn GetLastError() callconv(.winapi) u32;
pub extern "kernel32" fn CloseHandle(Handle) callconv(.winapi) c_int;
pub extern "kernel32" fn CreateThread(?*anyopaque, usize, *const fn (?*anyopaque) callconv(.winapi) u32, ?*anyopaque, u32, ?*u32) callconv(.winapi) ?Handle;
pub extern "kernel32" fn WaitForSingleObject(Handle, u32) callconv(.winapi) u32;
pub extern "kernel32" fn GetCurrentProcessId() callconv(.winapi) u32;
pub extern "kernel32" fn GetProcessHandleCount(Handle, *u32) callconv(.winapi) c_int;
pub extern "kernel32" fn GetTickCount64() callconv(.winapi) u64;
pub extern "kernel32" fn CreateEventW(?*anyopaque, c_int, c_int, ?[*:0]const u16) callconv(.winapi) ?Handle;
pub extern "kernel32" fn SetEvent(Handle) callconv(.winapi) c_int;
pub extern "kernel32" fn AcquireSRWLockExclusive(*usize) callconv(.winapi) void;
pub extern "kernel32" fn ReleaseSRWLockExclusive(*usize) callconv(.winapi) void;
pub extern "iphlpapi" fn GetExtendedTcpTable(?[*]u8, *u32, c_int, u32, u32, u32) callconv(.winapi) u32;
