#pragma once
#include "OpenGlRuntimeState.h"
#include "OpenGlModuleLifetime.h"

namespace ResourceManagerOpenGl::Runtime
{
namespace WindowThreadDetail
{
struct Observers
{
    HHOOK before{}, after{};
    size_t destroyingDepth{};
    DWORD failure{};
    void RecordFailure(DWORD error) { if (!failure) failure = error ? error : ERROR_GEN_FAILURE; }
};
inline thread_local Observers observers;

inline LRESULT CALLBACK Before(int code, WPARAM w, LPARAM l)
{
    const DWORD error = GetLastError();
    if (code >= 0 && observers.before && observers.after) {
        const auto& message = *reinterpret_cast<const CWPSTRUCT*>(l);
        if (message.message == WM_NCDESTROY) {
            ++observers.destroyingDepth;
            if (!drawables.BeforeNcDestroy(message.hwnd)) observers.RecordFailure(GetLastError());
        }
    }
    SetLastError(error);
    return CallNextHookEx(nullptr, code, w, l);
}
inline LRESULT CALLBACK After(int code, WPARAM w, LPARAM l)
{
    const DWORD error = GetLastError();
    if (code >= 0 && observers.destroyingDepth) {
        const auto& message = *reinterpret_cast<const CWPRETSTRUCT*>(l);
        if (message.message == WM_NCDESTROY) {
            if (!drawables.AfterNcDestroy(message.hwnd)) observers.RecordFailure(GetLastError());
            --observers.destroyingDepth;
        }
    }
    SetLastError(error);
    return CallNextHookEx(nullptr, code, w, l);
}
}

inline BOOL InstallWindowObserversForCurrentThread()
{
    using namespace WindowThreadDetail;
    const DWORD error = GetLastError();
    if (observers.failure) return Fail(observers.failure);
    if (observers.before && observers.after) return TRUE;
    if (!PinEntryModule(Callback(&Before)) || !PinEntryModule(Callback(&After))) return FALSE;
    observers.before = SetWindowsHookExW(WH_CALLWNDPROC, Before, nullptr, GetCurrentThreadId());
    if (!observers.before) { observers.RecordFailure(GetLastError()); return Fail(observers.failure); }
    observers.after = SetWindowsHookExW(WH_CALLWNDPROCRET, After, nullptr, GetCurrentThreadId());
    if (!observers.after) { observers.RecordFailure(GetLastError()); return Fail(observers.failure); }
    SetLastError(error);
    return TRUE;
}

inline DrawableReference PrepareObservedDrawable(HWND window)
{
    const DWORD error = GetLastError();
    DWORD process{};
    const auto thread = GetWindowThreadProcessId(window, &process);
    if (!thread) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return nullptr; }
    if (thread != GetCurrentThreadId() || process != GetCurrentProcessId()) {
        SetLastError(ERROR_INVALID_THREAD_ID); return nullptr;
    }
    SetLastError(error);
    if (!InstallWindowObserversForCurrentThread()) return nullptr;
    return drawables.PrepareOnWindowThread(window);
}

inline BOOL RemoveWindowObserversForCurrentThread()
{
    using namespace WindowThreadDetail;
    const DWORD error = GetLastError();
    if (observers.destroyingDepth || drawables.CountForThread(GetCurrentThreadId())) return Fail(ERROR_BUSY);
    for (auto* hook : {&observers.before, &observers.after}) {
        if (!*hook) continue;
        if (UnhookWindowsHookEx(*hook)) *hook = nullptr;
        else observers.RecordFailure(GetLastError());
    }
    if (observers.failure) return Fail(observers.failure);
    SetLastError(error);
    return TRUE;
}
}
