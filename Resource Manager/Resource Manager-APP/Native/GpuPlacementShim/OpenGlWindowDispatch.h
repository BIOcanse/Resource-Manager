#pragma once
#include <windows.h>
#include <GL/gl.h>
#include <GL/wglext.h>
#include <memory>

namespace ResourceManagerOpenGl
{
struct WindowFunctions
{
    PFNWGLCREATECONTEXTATTRIBSARBPROC createAttributes{};
    PFNWGLGETEXTENSIONSSTRINGARBPROC arb{};
    PFNWGLGETEXTENSIONSSTRINGEXTPROC ext{};
    PFNWGLSWAPINTERVALEXTPROC setInterval{};
    PFNWGLGETSWAPINTERVALEXTPROC getInterval{};
    PFNWGLMAKECONTEXTCURRENTARBPROC makeRead{};
    PFNWGLGETCURRENTREADDCARBPROC readDc{};
    PFNWGLCHOOSEPIXELFORMATARBPROC choose{};
    PFNWGLGETPIXELFORMATATTRIBIVARBPROC ints{};
    PFNWGLGETPIXELFORMATATTRIBFVARBPROC floats{};
};

class WindowDispatch final
{
    WindowDispatch(HMODULE owner, const WindowFunctions& entries) : module(owner), functions(entries) {}
public:
    // Loaded-module references belong to the installing runtime, not this value.
    const HMODULE module;
    const WindowFunctions functions;

    static std::shared_ptr<const WindowDispatch> Create(HMODULE module, const WindowFunctions& entries)
    {
        const DWORD error = GetLastError();
        if (!module) { SetLastError(ERROR_INVALID_PARAMETER); return nullptr; }
        const INT_PTR addresses[]{
            reinterpret_cast<INT_PTR>(entries.createAttributes), reinterpret_cast<INT_PTR>(entries.arb),
            reinterpret_cast<INT_PTR>(entries.ext), reinterpret_cast<INT_PTR>(entries.setInterval),
            reinterpret_cast<INT_PTR>(entries.getInterval), reinterpret_cast<INT_PTR>(entries.makeRead),
            reinterpret_cast<INT_PTR>(entries.readDc), reinterpret_cast<INT_PTR>(entries.choose),
            reinterpret_cast<INT_PTR>(entries.ints), reinterpret_cast<INT_PTR>(entries.floats)
        };
        for (const auto address : addresses) {
            if (address == 1 || address == 2 || address == 3 || address == -1) {
                SetLastError(ERROR_INVALID_PARAMETER);
                return nullptr;
            }
        }
        try {
            auto result = std::shared_ptr<const WindowDispatch>(new WindowDispatch(module, entries));
            SetLastError(error);
            return result;
        } catch (const std::bad_alloc&) {
            SetLastError(ERROR_NOT_ENOUGH_MEMORY);
            return nullptr;
        }
    }
};
using WindowDispatchReference = std::shared_ptr<const WindowDispatch>;
}
