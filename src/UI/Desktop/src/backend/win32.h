/* Win32 declarations for the Desktop resident, translated by the build's
   translate-C step (build.zig, module "desktop_win32_c"). */
#define WIN32_LEAN_AND_MEAN 1
#define UNICODE 1
#define _UNICODE 1
#define _WIN32_WINNT 0x0A00
/* These imports use only Win32 APIs. Avoid translating unused MinGW CRT
   fortify inline wrappers, which Zig's C translation rejects. */
#define _FORTIFY_SOURCE 0
#include <winsock2.h>
#include <windows.h>
#include <winhttp.h>
#include <iphlpapi.h>
#include <sddl.h>
#include <shellapi.h>
