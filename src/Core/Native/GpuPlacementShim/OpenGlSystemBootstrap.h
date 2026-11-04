#pragma once
#include "OpenGlSystemEntries.h"
#include <windows.h>
#include <GL/gl.h>
#include <functional>
#include <new>
#include <type_traits>
#include <utility>

namespace ResourceManagerOpenGl
{
struct SystemBootstrapResult
{
    DWORD error{}, cleanupError{};
    bool Succeeded() const noexcept { return !error && !cleanupError; }
};

// Explicit Configure work on its own thread. The callback consumes this temporary
// context; persistent entries and any extra objects stay with their existing owners.
template<typename Prepare>
SystemBootstrapResult ExecuteSystemBootstrap(const PIXELFORMATDESCRIPTOR& format,
    SIZE size, Prepare&& prepare)
{
    static_assert(std::is_same_v<std::invoke_result_t<Prepare, HDC, HGLRC, int>, DWORD>);
    const DWORD priorError = GetLastError();
    if (size.cx <= 0 || size.cy <= 0 || format.nSize != sizeof(format) || format.nVersion != 1)
        return {ERROR_INVALID_PARAMETER, 0};
    SystemEntries system;
    if (!ResolveSystemEntries(GetModuleHandleW(L"opengl32.dll"), system)) {
        const DWORD failure = GetLastError(); SetLastError(priorError); return {failure, 0};
    }
    if (system.current()) { SetLastError(priorError); return {ERROR_BUSY, 0}; }
    const auto error = [] { const DWORD value = GetLastError(); return value ? value : static_cast<DWORD>(ERROR_GEN_FAILURE); };
    const auto require = [&](BOOL okay) { if (!okay) throw error(); };
    const auto instance = GetModuleHandleW(nullptr);
    constexpr const wchar_t* className = L"ResourceManager.GpuPlacement.SystemBootstrap";
    bool registered{};
    HWND window{};
    HDC dc{};
    HGLRC context{};
    SystemBootstrapResult result;
    try {
        WNDCLASSW wc{};
        wc.style = CS_OWNDC;
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = instance;
        wc.lpszClassName = className;
        require(RegisterClassW(&wc) != 0); registered = true;
        window = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, className, L"",
            WS_POPUP, 0, 0, size.cx, size.cy, nullptr, nullptr, instance, nullptr);
        require(window != nullptr);
        dc = GetDC(window); require(dc != nullptr);
        const int selected = ChoosePixelFormat(dc, &format); require(selected > 0);
        require(SetPixelFormat(dc, selected, &format));
        context = system.create(dc); require(context != nullptr);
        require(system.makeCurrent(dc, context));
        result.error = std::invoke(std::forward<Prepare>(prepare), dc, context, selected);
    } catch (DWORD failure) { result.error = failure; }
      catch (const std::bad_alloc&) { result.error = ERROR_NOT_ENOUGH_MEMORY; }
      catch (...) { result.error = ERROR_UNHANDLED_EXCEPTION; }

    const auto cleanup = [&](BOOL okay) { if (!okay && !result.cleanupError) result.cleanupError = error(); };
    if (context) {
        const auto currentContext = system.current();
        if (currentContext == context) cleanup(system.makeCurrent(nullptr, nullptr));
        else if (currentContext) result.cleanupError = ERROR_BUSY;
        cleanup(system.destroy(context));
    }
    if (dc) cleanup(ReleaseDC(window, dc) != 0);
    if (window) cleanup(DestroyWindow(window));
    if (registered) cleanup(UnregisterClassW(className, instance));
    SetLastError(priorError);
    return result;
}
}
