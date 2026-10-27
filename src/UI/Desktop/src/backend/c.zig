pub const win = @cImport({
    @cDefine("WIN32_LEAN_AND_MEAN", "1");
    @cDefine("UNICODE", "1");
    @cDefine("_UNICODE", "1");
    @cDefine("_WIN32_WINNT", "0x0A00");
    // These imports use only Win32 APIs. Avoid translating unused MinGW CRT
    // fortify inline wrappers, which Zig 0.16 rejects during C translation.
    @cDefine("_FORTIFY_SOURCE", "0");
    @cInclude("winsock2.h");
    @cInclude("windows.h");
    @cInclude("winhttp.h");
    @cInclude("iphlpapi.h");
    @cInclude("sddl.h");
    @cInclude("shellapi.h");
});
