#pragma once
#include <windows.h>
#include <GL/gl.h>
#include <cstddef>
#include <cstring>
#include <type_traits>

#if WINVER < 0x0A00
#error The WGL provider requires an explicit Windows 10 or newer compile target.
#endif

using FLONG = ULONG;
extern "C" {
#include "third_party/mesa-wgl/gldrv.h"
}

namespace ResourceManagerOpenGl
{
static_assert(sizeof(DHGLRC) == 4);
static_assert(sizeof(GLDISPATCHTABLE) == OPENGL_VERSION_110_ENTRIES * sizeof(PROC));
static_assert(offsetof(GLCLTPROCTABLE, glDispatchTable) == sizeof(void*));
static_assert(sizeof(GLCLTPROCTABLE) == (OPENGL_VERSION_110_ENTRIES + 1) * sizeof(void*));
static_assert(sizeof(WGLCALLBACKS) == 9 * sizeof(PROC));
static_assert(!std::is_same_v<PFN_PRESENTBUFFERS, decltype(&DrvPresentBuffers)>);

#define RM_GL_CORE(Name, Index) \
    static_assert(std::is_same_v<decltype(&::Name), decltype(GLDISPATCHTABLE::Name)>); \
    static_assert(offsetof(GLDISPATCHTABLE, Name) == Index * sizeof(PROC));
#include "OpenGlCoreEntries.inc"
#undef RM_GL_CORE

struct IcdExports
{
    decltype(&DrvValidateVersion) validateVersion{};
    decltype(&DrvSetCallbackProcs) setCallbacks{};
    decltype(&DrvDescribePixelFormat) describePixelFormat{};
    decltype(&DrvSetPixelFormat) setPixelFormat{};
    decltype(&DrvCreateLayerContext) createLayerContext{};
    decltype(&DrvSetContext) setContext{};
    decltype(&DrvReleaseContext) releaseContext{};
    decltype(&DrvDeleteContext) deleteContext{};
    decltype(&DrvGetProcAddress) getProcAddress{};
    decltype(&DrvShareLists) shareLists{};
    decltype(&DrvCopyContext) copyContext{};
    decltype(&DrvSwapBuffers) swapBuffers{};
    // Optional: a driver may present without using this callback path.
    decltype(&DrvPresentBuffers) presentBuffers{};
};

template<typename Function>
inline Function ReadIcdExport(HMODULE module, const char* name) noexcept
{
    const auto address = GetProcAddress(module, name);
    Function result{};
    static_assert(sizeof(result) == sizeof(address));
    std::memcpy(&result, &address, sizeof(result));
    return result;
}

// The caller owns the loaded module. Reading its exports never loads a driver or installs hooks.
inline bool ResolveIcdExports(HMODULE module, IcdExports& output) noexcept
{
    output = {};
    if (!module) { SetLastError(ERROR_INVALID_HANDLE); return false; }
    IcdExports candidate;
#define RM_ICD_EXPORT(Member, Name) \
    candidate.Member = ReadIcdExport<decltype(candidate.Member)>(module, #Name); \
    if (!candidate.Member) { SetLastError(ERROR_PROC_NOT_FOUND); return false; }
    RM_ICD_EXPORT(validateVersion, DrvValidateVersion)
    RM_ICD_EXPORT(setCallbacks, DrvSetCallbackProcs)
    RM_ICD_EXPORT(describePixelFormat, DrvDescribePixelFormat)
    RM_ICD_EXPORT(setPixelFormat, DrvSetPixelFormat)
    RM_ICD_EXPORT(createLayerContext, DrvCreateLayerContext)
    RM_ICD_EXPORT(setContext, DrvSetContext)
    RM_ICD_EXPORT(releaseContext, DrvReleaseContext)
    RM_ICD_EXPORT(deleteContext, DrvDeleteContext)
    RM_ICD_EXPORT(getProcAddress, DrvGetProcAddress)
    RM_ICD_EXPORT(shareLists, DrvShareLists)
    RM_ICD_EXPORT(copyContext, DrvCopyContext)
    RM_ICD_EXPORT(swapBuffers, DrvSwapBuffers)
#undef RM_ICD_EXPORT
    candidate.presentBuffers = ReadIcdExport<decltype(candidate.presentBuffers)>(module, "DrvPresentBuffers");
    output = candidate;
    return true;
}
}
