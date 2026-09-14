#pragma once
#include "OpenGlCallbackSource.h"
#include "OpenGlIcdPreparation.h"
#include "OpenGlRuntimeHooks.h"

namespace ResourceManagerOpenGl::Runtime
{
// Owned by the original provider installation; callbacks may outlive Configure.
inline ResolvedIcdCallbacks sourceIcdCallbacks;
inline IcdCallbackArray targetIcdCallbacks{};
inline thread_local void* icdPrivateValue{};

inline void WINAPI SetIcdPrivateValue(void* value) { icdPrivateValue = value; }
inline void* WINAPI GetIcdPrivateValue() { return icdPrivateValue; }
inline void WINAPI IcdAdapterLuid(HDC dc, LUID* value)
{
    const auto drawable = drawables.Read(dc);
    if (drawable && drawable.target && value) {
        *value = drawable.target->adapterLuid;
        return;
    }
    sourceIcdCallbacks.Callbacks().pfnGetAdapterLuid(dc, value);
}

// Like the other runtime installers, the caller holds the original Configure lock.
// Once registration is attempted, retain storage even if the driver rejects it.
inline BOOL InstallIcdCallbacks(ResolvedIcdCallbacks&& source, HMODULE module,
    const IcdExports& driver, ULONG version)
{
    if (sourceIcdCallbacks.Callbacks().pfnGetAdapterLuid) return Fail(ERROR_ALREADY_INITIALIZED);
    const auto& original = source.Callbacks();
    if (!original.pfnGetDhglrc || !original.pfnGetAdapterLuid) return Fail(ERROR_INVALID_PARAMETER);
    IcdCallbackArray candidate{};
    IcdCallbackArray originalArray{};
    std::memcpy(originalArray.data(), &original, sizeof(original));
    const IcdCallbackReplacements replacements{
        &SetIcdPrivateValue, &GetIcdPrivateValue, &DriverHandle, &IcdAdapterLuid};
    if (!PrepareIcdCallbacks(static_cast<int>(originalArray.size()), originalArray.data(), replacements, candidate))
        return FALSE;
    sourceIcdCallbacks = std::move(source);
    sourceDriverHandle = sourceIcdCallbacks.Callbacks().pfnGetDhglrc;
    targetIcdCallbacks = candidate;
    return RegisterIcdCallbacks(module, driver, version, targetIcdCallbacks);
}
}
