#pragma once
#include "OpenGlProviderWindows.h"
#include "OpenGlWindowCall.h"
#include <unordered_set>
#include <vector>

namespace ResourceManagerOpenGl::Provider
{
struct ConfigurationOptions
{
    PIXELFORMATDESCRIPTOR bootstrapFormat;
    SIZE bootstrapSize;
    int maximumFormats;
    size_t maximumWindows;
    DWORD messageTimeout;
};

inline std::mutex configurationLock;
inline Runtime::InstalledTarget installed;
inline volatile LONG ready{};

namespace Detail
{
struct Windows
{
    size_t maximum;
    std::vector<HWND> values;
    std::unordered_set<HWND> seen;
    DWORD error{};
};
inline BOOL CALLBACK AddWindow(HWND window, LPARAM parameter)
{
    auto& output = *reinterpret_cast<Windows*>(parameter);
    if (output.error) return FALSE;
    DWORD process{};
    if (!GetWindowThreadProcessId(window, &process) || process != GetCurrentProcessId()) return TRUE;
    try {
        if (!output.seen.insert(window).second) return TRUE;
        if (output.values.size() == output.maximum) { output.error = ERROR_BUFFER_OVERFLOW; return FALSE; }
        output.values.push_back(window);
        return TRUE;
    } catch (const std::bad_alloc&) { output.error = ERROR_NOT_ENOUGH_MEMORY; return FALSE; }
}
inline BOOL CALLBACK AddTree(HWND window, LPARAM parameter)
{
    DWORD process{};
    if (!GetWindowThreadProcessId(window, &process) || process != GetCurrentProcessId()) return TRUE;
    if (!AddWindow(window, parameter)) return FALSE;
    EnumChildWindows(window, AddWindow, parameter);
    return reinterpret_cast<Windows*>(parameter)->error == 0;
}
inline BOOL PublishWindow(HWND window, const Runtime::InstalledTarget& target)
{
    const HDC dc = GetDC(window);
    if (!dc) return FALSE;
    SetLastError(0);
    // Unformatted windows are not OpenGL drawables yet. Do not select a format for them.
    const BOOL published = Runtime::systemPixelFormat(dc) == 0 || Runtime::PublishInstalledTarget(dc, target);
    const DWORD operationError = GetLastError();
    const BOOL released = ReleaseDC(window, dc);
    const DWORD cleanupError = GetLastError();
    SetLastError(published ? (released ? operationError : cleanupError) : operationError);
    return published && released;
}
}

// The original Configure entry holds configurationLock across policy update and this call.
inline BOOL Configure(ResolvedIcdCallbacks&& callbacks, LUID adapter, const ConfigurationOptions& options,
    Runtime::CreationPolicyReader policyReader)
{
    const DWORD incoming = GetLastError();
    if (!options.maximumWindows || !options.messageTimeout || options.messageTimeout == INFINITE)
        return Runtime::Fail(ERROR_INVALID_PARAMETER);
    if (InterlockedCompareExchange(&ready, 0, 0) < 0) return Runtime::Fail(ERROR_INVALID_STATE);
    if (installed.module && (installed.adapter.LowPart != adapter.LowPart || installed.adapter.HighPart != adapter.HighPart))
        return Runtime::Fail(ERROR_NOT_SUPPORTED);
    if (!installed.module) {
        const auto result = Runtime::InstallPreparedTarget(std::move(callbacks), adapter,
            options.bootstrapFormat, options.bootstrapSize, options.maximumFormats, installed, policyReader);
        if (!result.Succeeded()) {
            InterlockedExchange(&ready, -1);
            return Runtime::Fail(result.error ? result.error : result.cleanupError);
        }
    }
    Detail::Windows windows{options.maximumWindows, {}, {}, 0};
    SetLastError(0);
    const BOOL enumerated = EnumWindows(Detail::AddTree, reinterpret_cast<LPARAM>(&windows));
    if (!enumerated || windows.error) {
        InterlockedExchange(&ready, -1);
        return Runtime::Fail(windows.error ? windows.error : Runtime::WindowCallDetail::ErrorOrFailure());
    }
    for (HWND window : windows.values) {
        const auto result = Runtime::ExecuteOnWindowThread(window,
            [target = installed](HWND owner) { return Detail::PublishWindow(owner, target); }, options.messageTimeout);
        if (result.state != Runtime::WindowCallState::Completed || !result.returned || result.cleanupError) {
            InterlockedExchange(&ready, -1);
            const DWORD error = result.operationError ? result.operationError
                : result.cleanupError ? result.cleanupError : result.dispatchError;
            return Runtime::Fail(error ? error : ERROR_GEN_FAILURE);
        }
    }
    InterlockedExchange(&ready, 1);
    SetLastError(incoming);
    return TRUE;
}
}
