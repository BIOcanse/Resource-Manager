#pragma once
#include "OpenGlRuntimeState.h"
#include "OpenGlModuleLifetime.h"

namespace ResourceManagerOpenGl
{
using IcdCallbackArray = std::array<PROC, sizeof(WGLCALLBACKS) / sizeof(PROC)>;
struct IcdCallbackReplacements
{
    PFN_SETCURRENTVALUE setCurrent{};
    PFN_GETCURRENTVALUE getCurrent{};
    PFN_GETDHGLRC driverHandle{};
    PFN_GETADAPTERLUID adapterLuid{};
};

inline BOOL PrepareIcdCallbacks(int count, const PROC* source,
    const IcdCallbackReplacements& replacements, IcdCallbackArray& output)
{
    const DWORD error = GetLastError();
    if (!source || count != static_cast<int>(output.size())) return Runtime::Fail(ERROR_INVALID_PARAMETER);
    const PROC changed[]{Runtime::Callback(replacements.setCurrent), Runtime::Callback(replacements.getCurrent),
        Runtime::Callback(replacements.driverHandle), Runtime::Callback(replacements.adapterLuid)};
    for (auto entry : changed) if (!Runtime::Callable(entry)) return Runtime::Fail(ERROR_INVALID_PARAMETER);
    IcdCallbackArray candidate;
    std::memcpy(candidate.data(), source, sizeof(candidate));
    candidate[0] = changed[0]; candidate[1] = changed[1];
    candidate[2] = changed[2]; candidate[5] = changed[3];
    // Slot 3 is reserved by the native ABI, not a callable entry.
    for (size_t i = 0; i < candidate.size(); ++i)
        if (i != 3 && candidate[i] && !Runtime::Callable(candidate[i])) return Runtime::Fail(ERROR_INVALID_PARAMETER);
    output = candidate;
    SetLastError(error);
    return TRUE;
}

// The installing provider owns stable callback storage for the rest of the process.
// Version rejection or a partial installation never triggers retry or module unload.
inline BOOL RegisterIcdCallbacks(HMODULE module, const IcdExports& driver, ULONG version,
    IcdCallbackArray& callbacks)
{
    if (!module) return Runtime::Fail(ERROR_INVALID_PARAMETER);
    const PROC entries[]{
        Runtime::Callback(driver.validateVersion), Runtime::Callback(driver.setCallbacks),
        Runtime::Callback(driver.describePixelFormat), Runtime::Callback(driver.setPixelFormat),
        Runtime::Callback(driver.createLayerContext), Runtime::Callback(driver.setContext),
        Runtime::Callback(driver.releaseContext), Runtime::Callback(driver.deleteContext),
        Runtime::Callback(driver.getProcAddress), Runtime::Callback(driver.shareLists),
        Runtime::Callback(driver.copyContext), Runtime::Callback(driver.swapBuffers)
    };
    for (auto entry : entries) if (!Runtime::Callable(entry)) return Runtime::Fail(ERROR_PROC_NOT_FOUND);
    for (size_t i = 0; i < callbacks.size(); ++i) {
        if (i == 3) continue;
        if ((i == 0 || i == 1 || i == 2 || i == 5 || callbacks[i]) && !Runtime::Callable(callbacks[i]))
            return Runtime::Fail(ERROR_INVALID_PARAMETER);
    }
    if (driver.presentBuffers && !Runtime::Callable(Runtime::Callback(driver.presentBuffers)))
        return Runtime::Fail(ERROR_PROC_NOT_FOUND);
    if (!PinEntryModule(reinterpret_cast<PROC>(module))) return FALSE;
    for (auto entry : entries) if (!PinEntryModule(entry)) return FALSE;
    if (driver.presentBuffers && !PinEntryModule(Runtime::Callback(driver.presentBuffers))) return FALSE;
    for (size_t i = 0; i < callbacks.size(); ++i)
        if (i != 3 && callbacks[i] && !PinEntryModule(callbacks[i])) return FALSE;
    if (!driver.validateVersion(version)) return FALSE;
    driver.setCallbacks(static_cast<int>(callbacks.size()), callbacks.data());
    return TRUE;
}
}
