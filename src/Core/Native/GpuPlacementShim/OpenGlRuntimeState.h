#pragma once
#include "OpenGlContextDispatch.h"
#include "OpenGlDrawableStore.h"
#include "GpuPlacementApiObservation.h"
#include <array>
#include <atomic>
#include <string>
#include <tuple>

namespace ResourceManagerOpenGl::Runtime
{
using GetBytesEntry = void(APIENTRY*)(GLenum, GLubyte*);
using CreateAttributes = HGLRC(WINAPI*)(HDC, HGLRC, const int*);
using ResourceManagerOpenGl::ContextRecord;
using ResourceManagerOpenGl::ContextReference;
using ResourceManagerOpenGl::ModernEntries;

// One provider runtime; these are the original stores, not per-API policy owners.
inline DrawableStore drawables;
inline ContextStore contexts;
inline std::mutex bindingLock;
inline std::atomic<ResourceManagerGpuObservation::CallStore*> apiObservations{};
inline thread_local bool internalContextWork{};
inline void ObserveApiCall() noexcept
{
    if (!internalContextWork) {
        if (const auto store = apiObservations.load()) store->Record(ResourceManagerGpuObservation::CallApi::OpenGL);
    }
}
inline thread_local ContextReference current;
inline thread_local ContextReference pendingRelease;
inline thread_local PGLCLTPROCTABLE currentTable{};
inline thread_local HDC boundDc{};
inline HMODULE sourceModuleReference{};
inline PFN_GETDHGLRC sourceDriverHandle{};
inline decltype(&wglCreateContext) systemCreate{};
inline decltype(&wglCreateLayerContext) systemCreateLayer{};
inline CreateAttributes systemCreateAttributes{};
inline decltype(&wglMakeCurrent) systemMakeCurrent{};
inline decltype(&wglGetCurrentContext) systemCurrent{};
inline decltype(&wglGetCurrentDC) systemCurrentDC{};
inline decltype(&wglDeleteContext) systemDelete{};
inline decltype(&wglGetProcAddress) systemLookup{};
inline decltype(&wglShareLists) systemShare{};
inline decltype(&GetPixelFormat) systemPixelFormat{};
inline GetBytesEntry originalVendorBytes{};
inline PROC originalVendorBytesAddress{};

template<typename T> PROC Callback(T function)
{
    PROC result{};
    static_assert(sizeof(result) == sizeof(function));
    std::memcpy(&result, &function, sizeof(result));
    return result;
}
bool Callable(PROC entry)
{
    return entry && entry != reinterpret_cast<PROC>(1) && entry != reinterpret_cast<PROC>(2) &&
        entry != reinterpret_cast<PROC>(3) && entry != reinterpret_cast<PROC>(-1);
}
BOOL Fail(DWORD error) { SetLastError(error); return FALSE; }
HGLRC Handle(const ContextReference& record) { return PublicHandle(record); }
ContextReference Find(HGLRC handle) { return contexts.Find(handle); }
bool ModernHookAddress(PROC address);
bool ResolveModern(ModernEntries& entries, decltype(&DrvGetProcAddress) resolve);
PROC ModernLookup(LPCSTR name, PROC nativeEntry);
bool WindowLookup(LPCSTR name, PROC nativeEntry, PROC& result);
}
