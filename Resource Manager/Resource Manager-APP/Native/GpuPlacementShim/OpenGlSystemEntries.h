#pragma once
#include <windows.h>
#include <GL/gl.h>
#include <cstring>

namespace ResourceManagerOpenGl
{
struct SystemEntries
{
    decltype(&wglGetCurrentContext) current{};
    decltype(&wglCreateContext) create{};
    decltype(&wglDeleteContext) destroy{};
    decltype(&wglMakeCurrent) makeCurrent{};
    decltype(&wglGetProcAddress) lookup{};
    decltype(&glGetError) error{};
};

inline BOOL ResolveSystemEntries(HMODULE module, SystemEntries& output)
{
    if (!module) { SetLastError(ERROR_MOD_NOT_FOUND); return FALSE; }
    const DWORD incoming = GetLastError();
    SystemEntries candidate;
    const auto resolve = [&](const char* name, auto& entry) {
        const auto address = GetProcAddress(module, name);
        static_assert(sizeof(address) == sizeof(entry));
        std::memcpy(&entry, &address, sizeof(entry));
        return address != nullptr;
    };
    if (!resolve("wglGetCurrentContext", candidate.current) || !resolve("wglCreateContext", candidate.create)
        || !resolve("wglDeleteContext", candidate.destroy) || !resolve("wglMakeCurrent", candidate.makeCurrent)
        || !resolve("wglGetProcAddress", candidate.lookup) || !resolve("glGetError", candidate.error)) return FALSE;
    output = candidate;
    SetLastError(incoming);
    return TRUE;
}
}
