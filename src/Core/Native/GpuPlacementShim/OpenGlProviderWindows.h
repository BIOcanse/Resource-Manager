#pragma once
#include "OpenGlProviderInstallation.h"

namespace ResourceManagerOpenGl::Runtime
{
// The Configure window work borrows its DC. Publication never sets a format or context.
inline BOOL PublishInstalledTarget(HDC dc, const InstalledTarget& installed)
{
    const DWORD incoming = GetLastError();
    if (!dc || !installed.module || !installed.directory || !installed.windows
        || installed.windows->module != installed.module || !systemPixelFormat)
        return Fail(ERROR_INVALID_PARAMETER);
    const HWND window = WindowFromDC(dc);
    DWORD process{};
    const DWORD thread = GetWindowThreadProcessId(window, &process);
    if (!thread) return Fail(ERROR_INVALID_WINDOW_HANDLE);
    if (thread != GetCurrentThreadId() || process != GetCurrentProcessId())
        return Fail(ERROR_INVALID_THREAD_ID);
    const int sourceFormat = systemPixelFormat(dc);
    if (sourceFormat <= 0) return Fail(ERROR_INVALID_PIXEL_FORMAT);

    const auto current = drawables.Read(dc);
    auto target = current.target;
    if (target && (target->module != installed.module
        || target->adapterLuid.LowPart != installed.adapter.LowPart
        || target->adapterLuid.HighPart != installed.adapter.HighPart
        || current.sourcePixelFormat != sourceFormat)) return Fail(ERROR_ALREADY_EXISTS);
    if ((current.directory && current.directory != installed.directory)
        || (current.windowDispatch && current.windowDispatch != installed.windows))
        return Fail(ERROR_ALREADY_EXISTS);
    if (!target) {
        target = PrepareTarget(installed.module, installed.driver, installed.adapter);
        if (!target) return FALSE;
    }
    const auto record = PrepareObservedDrawable(window);
    if (!record || !drawables.PublishTarget(record, target, sourceFormat)
        || !drawables.PublishDirectory(record, installed.directory)
        || !drawables.PublishWindowDispatch(record, installed.windows)) return FALSE;
    SetLastError(incoming);
    return TRUE;
}
}
