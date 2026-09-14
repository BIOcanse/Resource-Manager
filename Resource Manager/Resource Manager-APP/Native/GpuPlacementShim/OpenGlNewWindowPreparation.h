#pragma once
#include "OpenGlWindowThread.h"
#include "OpenGlPixelFormatPreparation.h"

namespace ResourceManagerOpenGl::Runtime
{
// Only the application's explicit SetPixelFormat publishes a previously unknown window.
inline BOOL PrepareNewWindowPixelFormat(HDC dc, const DrawableSnapshot& formats)
{
    if (drawables.Read(dc)) return TRUE;
    if (!formats.target || !formats.directory || !formats.windowDispatch || !systemPixelFormat)
        return Fail(ERROR_NOT_READY);
    const HWND window = WindowFromDC(dc);
    DWORD process{};
    const DWORD thread = GetWindowThreadProcessId(window, &process);
    if (!thread) return Fail(ERROR_INVALID_WINDOW_HANDLE);
    if (thread != GetCurrentThreadId() || process != GetCurrentProcessId())
        return Fail(ERROR_INVALID_THREAD_ID);
    const auto& prepared = *formats.target;
    const int sourceFormat = systemPixelFormat(dc);
    auto target = PrepareTarget(prepared.module, prepared.driver, prepared.adapterLuid);
    if (!target) return FALSE;
    const auto record = PrepareObservedDrawable(window);
    return record && drawables.PublishTarget(record, std::move(target), sourceFormat)
        && drawables.PublishDirectory(record, formats.directory)
        && drawables.PublishWindowDispatch(record, formats.windowDispatch);
}
}
